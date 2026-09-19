using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Device;

/// <summary>
/// 经 OPC UA 访问 SINUMERIK ONE。
/// 只做读写，不参与实时控制回路：会话断了也不影响 NC 把当前这支辊磨完。
/// 端点、安全策略与超时来自 machine.json，节点地址来自 tagmap.json，代码里不写死任何一项。
/// </summary>
internal sealed class OpcUaGateway : IMachineGateway
{
    private const string ApplicationName = "RollGrinder HMI";

    private readonly ITagMap tagMap;
    private readonly ControllerDescription controller;
    private readonly string pkiDirectory;
    private readonly ITelemetryContext telemetry;
    private readonly SemaphoreSlim sessionGate = new(1, 1);

    private ApplicationConfiguration? applicationConfiguration;
    private ISession? session;

    public OpcUaGateway(
        ITagMap tagMap,
        ControllerDescription controller,
        string pkiDirectory,
        ILoggerFactory loggerFactory)
    {
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.pkiDirectory = pkiDirectory ?? throw new ArgumentNullException(nameof(pkiDirectory));
        this.telemetry = new LoggerFactoryTelemetryContext(
            loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)));
    }

    public GatewayConnectionState ConnectionState { get; private set; } = GatewayConnectionState.Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await this.sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.session is { Connected: true })
            {
                return;
            }

            // 会话还在但已经断了：先丢掉旧的，否则重连会把它连同订阅一起漏掉。
            if (this.session is not null)
            {
                this.session.KeepAlive -= OnKeepAlive;
                this.session.Dispose();
                this.session = null;
            }

            ConnectionState = GatewayConnectionState.Connecting;

            string endpointUrl = this.controller.EndpointUrl
                ?? throw new GatewayException("machine.json does not give controller.endpointUrl for the OPC UA gateway.");

            ApplicationConfiguration configuration = await EnsureConfigurationAsync(cancellationToken).ConfigureAwait(false);

            EndpointDescription? endpointDescription = await CoreClientUtils.SelectEndpointAsync(
                configuration,
                endpointUrl,
                this.controller.UseSecurity,
                this.controller.OperationTimeoutMs,
                this.telemetry,
                cancellationToken).ConfigureAwait(false);

            if (endpointDescription is null)
            {
                throw new GatewayException(
                    $"'{endpointUrl}' offers no endpoint matching the configured security setting "
                    + $"(useSecurity={this.controller.UseSecurity}).");
            }

            var endpoint = new ConfiguredEndpoint(
                null,
                endpointDescription,
                EndpointConfiguration.Create(configuration));

            this.session = await new DefaultSessionFactory(this.telemetry).CreateAsync(
                configuration,
                endpoint,
                updateBeforeConnect: false,
                ApplicationName,
                (uint)this.controller.SessionTimeoutMs,
                new UserIdentity(new AnonymousIdentityToken()),
                preferredLocales: null,
                cancellationToken).ConfigureAwait(false);

            this.session.KeepAlive += OnKeepAlive;
            ConnectionState = GatewayConnectionState.Connected;
        }
        catch (Exception ex) when (ex is ServiceResultException or IOException or TimeoutException)
        {
            ConnectionState = GatewayConnectionState.Faulted;
            throw new GatewayException(
                $"Could not connect to '{this.controller.EndpointUrl}': {ex.Message}", ex);
        }
        finally
        {
            this.sessionGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await this.sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.session is null)
            {
                return;
            }

            this.session.KeepAlive -= OnKeepAlive;
            try
            {
                await this.session.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResultException)
            {
                // 关会话失败没有补救价值：进程要退了，NC 那边照磨。
            }

            this.session.Dispose();
            this.session = null;
            ConnectionState = GatewayConnectionState.Disconnected;
        }
        finally
        {
            this.sessionGate.Release();
        }
    }

    public async Task<MachineStateSnapshot> ReadStateAsync(
        IReadOnlyList<string> logicalNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logicalNames);
        ISession activeSession = RequireSession();

        var descriptors = new List<TagDescriptor>(logicalNames.Count);
        var readValueIds = new ReadValueIdCollection();
        foreach (string logicalName in logicalNames)
        {
            // tagmap 里没有的变量说明本台机床没有这一项，跳过而不是让整次取数失败。
            if (!this.tagMap.TryResolve(logicalName, out TagDescriptor? descriptor) || descriptor is null)
            {
                continue;
            }

            descriptors.Add(descriptor);
            readValueIds.Add(new ReadValueId
            {
                NodeId = OpcUaValueMapper.ParseNodeId(descriptor),
                AttributeId = Attributes.Value,
            });
        }

        if (descriptors.Count == 0)
        {
            return MachineStateSnapshot.Empty(DateTimeOffset.UtcNow);
        }

        try
        {
            ReadResponse response = await activeSession.ReadAsync(
                requestHeader: null,
                maxAge: 0.0,
                timestampsToReturn: TimestampsToReturn.Source,
                nodesToRead: readValueIds,
                cancellationToken).ConfigureAwait(false);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var values = new List<TagValue>(descriptors.Count);
            for (int i = 0; i < descriptors.Count; i++)
            {
                DataValue? dataValue = i < response.Results.Count ? response.Results[i] : null;
                values.Add(OpcUaValueMapper.ToTagValue(descriptors[i], dataValue, now));
            }

            return new MachineStateSnapshot(now, GatewayConnectionState.Connected, values);
        }
        catch (Exception ex) when (ex is ServiceResultException or IOException or TimeoutException)
        {
            ConnectionState = GatewayConnectionState.Faulted;
            throw new GatewayException($"Reading the machine state failed: {ex.Message}", ex);
        }
    }

    public async Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken)
    {
        ISession activeSession = RequireSession();
        TagDescriptor descriptor = this.tagMap.Resolve(logicalName);

        var readValueIds = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = OpcUaValueMapper.ParseNodeId(descriptor), AttributeId = Attributes.Value },
        };

        try
        {
            ReadResponse response = await activeSession.ReadAsync(
                requestHeader: null,
                maxAge: 0.0,
                timestampsToReturn: TimestampsToReturn.Source,
                nodesToRead: readValueIds,
                cancellationToken).ConfigureAwait(false);

            return OpcUaValueMapper.ToTagValue(
                descriptor,
                response.Results.Count > 0 ? response.Results[0] : null,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is ServiceResultException or IOException or TimeoutException)
        {
            ConnectionState = GatewayConnectionState.Faulted;
            throw new GatewayException($"Reading tag '{logicalName}' failed: {ex.Message}", ex);
        }
    }

    public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken) =>
        WriteTagsAsync(new[] { new TagWrite(logicalName, value) }, cancellationToken);

    public async Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            return;
        }

        ISession activeSession = RequireSession();

        // 保序：一条一条写。下发时"参数有效"标志在最后一条，
        // 批量乱序会让 NC 在参数没写全时就认这组数据。
        for (int i = 0; i < writes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TagWrite write = writes[i];
            TagDescriptor descriptor = this.tagMap.Resolve(write.LogicalName);
            if (descriptor.Access == TagAccess.Read)
            {
                throw new GatewayException($"Tag '{write.LogicalName}' is read-only.");
            }

            var writeValues = new WriteValueCollection
            {
                new WriteValue
                {
                    NodeId = OpcUaValueMapper.ParseNodeId(descriptor),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(OpcUaValueMapper.ToOpcValue(descriptor, write.Value))),
                },
            };

            try
            {
                WriteResponse response = await activeSession
                    .WriteAsync(requestHeader: null, writeValues, cancellationToken).ConfigureAwait(false);

                if (response.Results.Count > 0 && StatusCode.IsNotGood(response.Results[0]))
                {
                    throw new GatewayException(
                        $"Writing tag '{write.LogicalName}' was rejected by the controller: {response.Results[0]}. "
                        + $"{i} of {writes.Count} writes had been applied.");
                }
            }
            catch (Exception ex) when (ex is ServiceResultException or IOException or TimeoutException)
            {
                ConnectionState = GatewayConnectionState.Faulted;
                throw new GatewayException(
                    $"Writing tag '{write.LogicalName}' failed after {i} of {writes.Count} writes: {ex.Message}", ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        this.sessionGate.Dispose();
    }

    private ISession RequireSession() =>
        this.session is { Connected: true }
            ? this.session
            : throw new GatewayException("The OPC UA gateway is not connected; call ConnectAsync first.");

    private void OnKeepAlive(ISession keepAliveSession, KeepAliveEventArgs e)
    {
        // 心跳异常只改状态；重连交给上层的监视循环，它会连同报警一起处理。
        ConnectionState = ServiceResult.IsBad(e.Status)
            ? GatewayConnectionState.Faulted
            : GatewayConnectionState.Connected;
    }

    private async Task<ApplicationConfiguration> EnsureConfigurationAsync(CancellationToken cancellationToken)
    {
        if (this.applicationConfiguration is not null)
        {
            return this.applicationConfiguration;
        }

        Directory.CreateDirectory(this.pkiDirectory);

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = ApplicationName,
            ApplicationUri = Utils.Format("urn:{0}:RollGrinder:HMI", System.Net.Dns.GetHostName()),
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(this.pkiDirectory, "own"),
                    SubjectName = "CN=" + ApplicationName,
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(this.pkiDirectory, "trusted"),
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(this.pkiDirectory, "issuers"),
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(this.pkiDirectory, "rejected"),
                },
                AutoAcceptUntrustedCertificates = this.controller.AutoAcceptUntrustedCertificates,
                AddAppCertToTrustedStore = true,
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = this.controller.OperationTimeoutMs },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = this.controller.SessionTimeoutMs,
            },
        };

        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken).ConfigureAwait(false);

        configuration.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted
                && this.controller.AutoAcceptUntrustedCertificates)
            {
                e.Accept = true;
            }
        };

        // 没有客户端证书就自签一张放进 data/pki/own，否则带安全策略的端点连不上。
        var application = new ApplicationInstance(configuration, this.telemetry);
        bool hasCertificate = await application
            .CheckApplicationInstanceCertificatesAsync(silent: true, lifeTimeInMonths: null, cancellationToken)
            .ConfigureAwait(false);
        if (!hasCertificate)
        {
            throw new GatewayException(
                $"No usable OPC UA client certificate in '{this.pkiDirectory}'.");
        }

        this.applicationConfiguration = configuration;
        return configuration;
    }
}
