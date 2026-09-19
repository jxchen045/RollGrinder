using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Measurement;

/// <summary>测量数据的采集与归档。</summary>
public interface IMeasurementService
{
    /// <summary>
    /// 从机床取一个测点：拖板位置作辊身坐标，测径仪读数换算成半径量。
    /// </summary>
    Task<MeasurementPoint> CapturePointAsync(CancellationToken cancellationToken);

    /// <summary>归档一次测量，返回测量标识。</summary>
    Task<string> SaveAsync(
        string jobId,
        IReadOnlyList<MeasurementPoint> points,
        string source,
        CancellationToken cancellationToken);

    /// <summary>取某支作业最近一次测量。</summary>
    Task<MeasurementRecord?> GetLatestAsync(string jobId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMeasurementService"/>
public sealed class MeasurementService : IMeasurementService
{
    private readonly IMachineGateway gateway;
    private readonly IMeasurementRepository repository;
    private readonly MachineDescription machine;
    private readonly TimeProvider timeProvider;

    public MeasurementService(
        IMachineGateway gateway,
        IMeasurementRepository repository,
        MachineDescription machine,
        TimeProvider timeProvider)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<MeasurementPoint> CapturePointAsync(CancellationToken cancellationToken)
    {
        string carriageAxisName = FindAxisName(MachineAxisRoles.Carriage);

        TagValue position = await this.gateway
            .ReadTagAsync(MachineTagKeys.AxisActualPositionMm(carriageAxisName), cancellationToken)
            .ConfigureAwait(false);
        TagValue diameter = await this.gateway
            .ReadTagAsync(MachineTagKeys.MeasuredDiameterMm, cancellationToken)
            .ConfigureAwait(false);

        double bodyPositionMm = RequireNumber(position);
        double measuredDiameterMm = RequireNumber(diameter);

        return new MeasurementPoint(
            bodyPositionMm,
            UnitConversion.DiameterMmToRadiusMm(measuredDiameterMm));
    }

    public async Task<string> SaveAsync(
        string jobId,
        IReadOnlyList<MeasurementPoint> points,
        string source,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        string measurementId = Guid.NewGuid().ToString("N");
        await this.repository.AddAsync(
            new MeasurementRecord(
                measurementId,
                jobId,
                this.timeProvider.GetUtcNow(),
                source,
                new MeasuredProfile(points)),
            cancellationToken).ConfigureAwait(false);

        return measurementId;
    }

    public Task<MeasurementRecord?> GetLatestAsync(string jobId, CancellationToken cancellationToken) =>
        this.repository.GetLatestByJobAsync(jobId, cancellationToken);

    private string FindAxisName(string role)
    {
        foreach (AxisDescription axis in this.machine.Axes)
        {
            if (axis.IsPresent && string.Equals(axis.Role, role, StringComparison.Ordinal))
            {
                return axis.Name;
            }
        }

        throw new GatewayException($"machine.json does not describe a present axis with role '{role}'.");
    }

    private static double RequireNumber(TagValue value)
    {
        if (!value.IsGood || value.Raw is null)
        {
            throw new GatewayException($"Tag '{value.Key}' did not return a usable value.");
        }

        return Convert.ToDouble(value.Raw, System.Globalization.CultureInfo.InvariantCulture);
    }
}
