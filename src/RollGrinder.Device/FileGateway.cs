using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Device;

/// <summary>
/// 文件网关：回放一段录制下来的机床数据，用于离线复盘与无机床演示。
///
/// 录制文件是 JSON Lines（.jsonl），一行一帧，按 offsetMs 升序：
///   {"offsetMs":0,"values":{"machine.channelState":2,"axis.Z.actualPositionMm":0.0}}
///   {"offsetMs":500,"values":{"axis.Z.actualPositionMm":16.7}}
/// 每帧只需写变化的量，未出现的量沿用上一帧。放到最后一帧后保持不动。
///
/// 回放是只读的事实记录，所以写入不回灌到回放流里：写入值另存一份 overlay
/// （读的时候优先取 overlay），同时追加到 writes-*.jsonl 便于核对下发内容。
/// </summary>
internal sealed class FileGateway : IMachineGateway
{
    /// <summary>默认的录制文件目录（相对数据目录）。</summary>
    public const string ReplayDirectoryName = "replay";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ITagMap tagMap;
    private readonly string dataDirectory;
    private readonly string? replayFilePath;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, object?> overlay = new(StringComparer.Ordinal);
    private readonly object gate = new();

    private IReadOnlyList<ReplayFrame> frames = Array.Empty<ReplayFrame>();
    private long startedTimestamp;
    private string? writeLogPath;

    public FileGateway(ITagMap tagMap, string dataDirectory, string? replayFilePath, TimeProvider timeProvider)
    {
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        this.replayFilePath = replayFilePath;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public GatewayConnectionState ConnectionState { get; private set; } = GatewayConnectionState.Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        string path = ResolveReplayFile();
        IReadOnlyList<ReplayFrame> loaded = await LoadAsync(path, cancellationToken).ConfigureAwait(false);

        lock (this.gate)
        {
            this.frames = loaded;
            this.overlay.Clear();
            this.startedTimestamp = this.timeProvider.GetTimestamp();
            this.writeLogPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                string.Create(CultureInfo.InvariantCulture, $"writes-{this.timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.jsonl"));
            ConnectionState = GatewayConnectionState.Connected;
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        ConnectionState = GatewayConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task<MachineStateSnapshot> ReadStateAsync(IReadOnlyList<string> logicalNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logicalNames);
        cancellationToken.ThrowIfCancellationRequested();
        RequireConnected();

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        var values = new List<TagValue>(logicalNames.Count);

        lock (this.gate)
        {
            IReadOnlyDictionary<string, object?> state = StateAtLocked();
            foreach (string logicalName in logicalNames)
            {
                // tagmap 里没有的变量说明本台机床没有这一项，跳过而不是让整次取数失败。
                if (!this.tagMap.TryResolve(logicalName, out TagDescriptor? descriptor) || descriptor is null)
                {
                    continue;
                }

                bool known = state.TryGetValue(logicalName, out object? raw);
                values.Add(new TagValue(logicalName, descriptor.DataType, raw, now, known && raw is not null));
            }
        }

        return Task.FromResult(new MachineStateSnapshot(now, ConnectionState, values));
    }

    public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireConnected();

        TagDescriptor descriptor = this.tagMap.Resolve(logicalName);
        lock (this.gate)
        {
            IReadOnlyDictionary<string, object?> state = StateAtLocked();
            bool known = state.TryGetValue(logicalName, out object? raw);
            return Task.FromResult(new TagValue(
                logicalName,
                descriptor.DataType,
                raw,
                this.timeProvider.GetUtcNow(),
                known && raw is not null));
        }
    }

    public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken) =>
        WriteTagsAsync(new[] { new TagWrite(logicalName, value) }, cancellationToken);

    public async Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        RequireConnected();

        var log = new StringBuilder();
        lock (this.gate)
        {
            foreach (TagWrite write in writes)
            {
                TagDescriptor descriptor = this.tagMap.Resolve(write.LogicalName);
                if (descriptor.Access == TagAccess.Read)
                {
                    throw new GatewayException($"Tag '{write.LogicalName}' is read-only.");
                }

                this.overlay[write.LogicalName] = write.Value.Raw;
                log.Append('{')
                    .Append("\"utc\":\"").Append(this.timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture))
                    .Append("\",\"key\":").Append(JsonSerializer.Serialize(write.LogicalName, SerializerOptions))
                    .Append(",\"value\":").Append(JsonSerializer.Serialize(write.Value.Raw, SerializerOptions))
                    .AppendLine("}");
            }
        }

        if (this.writeLogPath is not null && log.Length > 0)
        {
            try
            {
                await File.AppendAllTextAsync(this.writeLogPath, log.ToString(), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new GatewayException($"Could not append to '{this.writeLogPath}': {ex.Message}", ex);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        ConnectionState = GatewayConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    private void RequireConnected()
    {
        if (ConnectionState != GatewayConnectionState.Connected)
        {
            throw new GatewayException("The file gateway is not connected; call ConnectAsync first.");
        }
    }

    private string ResolveReplayFile()
    {
        if (!string.IsNullOrWhiteSpace(this.replayFilePath))
        {
            return File.Exists(this.replayFilePath)
                ? this.replayFilePath
                : throw new GatewayException($"Replay file '{this.replayFilePath}' was not found.");
        }

        string directory = Path.Combine(this.dataDirectory, ReplayDirectoryName);
        if (!Directory.Exists(directory))
        {
            throw new GatewayException($"Replay directory '{directory}' does not exist.");
        }

        string? newest = Directory.EnumerateFiles(directory, "*.jsonl")
            .Where(file => !Path.GetFileName(file).StartsWith("writes-", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return newest ?? throw new GatewayException($"Replay directory '{directory}' holds no .jsonl recording.");
    }

    private static async Task<IReadOnlyList<ReplayFrame>> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var frames = new List<ReplayFrame>();
        int lineNumber = 0;

        try
        {
            foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ReplayFrameJson? frame = JsonSerializer.Deserialize<ReplayFrameJson>(line, SerializerOptions);
                if (frame is null)
                {
                    continue;
                }

                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, JsonElement> pair in frame.Values ?? new Dictionary<string, JsonElement>())
                {
                    values[pair.Key] = ToClrValue(pair.Value);
                }

                frames.Add(new ReplayFrame(frame.OffsetMs, values));
            }
        }
        catch (JsonException ex)
        {
            throw new GatewayException($"Replay file '{path}' is malformed at line {lineNumber}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new GatewayException($"Replay file '{path}' could not be read: {ex.Message}", ex);
        }

        if (frames.Count == 0)
        {
            throw new GatewayException($"Replay file '{path}' holds no frames.");
        }

        frames.Sort((left, right) => left.OffsetMs.CompareTo(right.OffsetMs));
        return frames;
    }

    private static object? ToClrValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt32(out int integer) ? integer : element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null => null,
        _ => element.ToString(),
    };

    private IReadOnlyDictionary<string, object?> StateAtLocked()
    {
        TimeSpan elapsed = this.timeProvider.GetElapsedTime(this.startedTimestamp, this.timeProvider.GetTimestamp());
        double elapsedMs = elapsed.TotalMilliseconds;

        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (ReplayFrame frame in this.frames)
        {
            if (frame.OffsetMs > elapsedMs)
            {
                break;
            }

            foreach (KeyValuePair<string, object?> pair in frame.Values)
            {
                state[pair.Key] = pair.Value;
            }
        }

        foreach (KeyValuePair<string, object?> pair in this.overlay)
        {
            state[pair.Key] = pair.Value;
        }

        return state;
    }

    private sealed record ReplayFrame(double OffsetMs, IReadOnlyDictionary<string, object?> Values);

    private sealed class ReplayFrameJson
    {
        public double OffsetMs { get; set; }

        public Dictionary<string, JsonElement>? Values { get; set; }
    }
}
