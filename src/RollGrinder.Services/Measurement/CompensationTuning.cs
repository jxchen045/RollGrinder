using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Audit;

namespace RollGrinder.Services.Measurement;

/// <summary>
/// 上位机算补偿时用的三个设定（修改稿 3②、5.5 补偿子视图）。
/// </summary>
/// <param name="Gain">增益 0–1：一次吸收多少比例的偏差。</param>
/// <param name="SmoothingPoints">平滑窗口点数（奇数）。</param>
/// <param name="MaxCorrectionMicrometer">单次修正的限幅（直径量 µm）；machine.json 没给且没改过时为空。</param>
public sealed record CompensationTuning(double Gain, int SmoothingPoints, double? MaxCorrectionMicrometer)
{
    /// <summary>平滑窗口的上限：再宽就把辊形本身抹平了。</summary>
    public const int MaxSmoothingPoints = 51;

    /// <summary>检查一组设定；不成立返回出错项的键（与 <see cref="CompensationTuningKeys"/> 一致），成立返回 null。</summary>
    public string? FindInvalid(double? machineLimitMicrometer)
    {
        if (!(Gain > 0.0 && Gain <= 1.0))
        {
            return CompensationTuningKeys.Gain;
        }

        if (SmoothingPoints < 1 || SmoothingPoints % 2 == 0 || SmoothingPoints > MaxSmoothingPoints)
        {
            return CompensationTuningKeys.SmoothingPoints;
        }

        if (MaxCorrectionMicrometer is double max
            && (!(max > 0.0) || (machineLimitMicrometer is double limit && max > limit + 1e-9)))
        {
            return CompensationTuningKeys.MaxCorrectionMicrometer;
        }

        return null;
    }
}

/// <summary>补偿设定在设定表与改动记录里的键。</summary>
public static class CompensationTuningKeys
{
    public const string Gain = "compensation.gain";

    public const string SmoothingPoints = "compensation.smoothingPoints";

    public const string MaxCorrectionMicrometer = "compensation.maxCorrectionMicrometer";

    public static IReadOnlyList<string> All { get; } = new[] { Gain, SmoothingPoints, MaxCorrectionMicrometer };
}

/// <summary>
/// 补偿设定：配置文件（hmi.json 的增益与平滑、machine.json 的限幅）给默认值，
/// 制造商可以在自动页的补偿子视图里改；改过的存进库、写改动记录，"恢复配置值"删掉覆盖。
/// 限幅只能往小改——machine.json 里的那个数是机床的上限，界面上不能把它放大。
/// 改动只影响之后算出来的补偿，不碰正在执行的程序。
/// </summary>
public interface ICompensationTuningService
{
    event EventHandler? Changed;

    /// <summary>现在生效的设定。</summary>
    CompensationTuning Current { get; }

    /// <summary>配置文件里的设定（没改过时就是它）。</summary>
    CompensationTuning Configured { get; }

    /// <summary>machine.json 给的限幅上限（直径量 µm）；没给为空。</summary>
    double? MachineLimitMicrometer { get; }

    /// <summary>有没有改过（有覆盖值）。</summary>
    bool IsOverridden { get; }

    Task LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(CompensationTuning tuning, string changedBy, CancellationToken cancellationToken);

    /// <summary>恢复配置文件的值。</summary>
    Task ResetAsync(string changedBy, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICompensationTuningService"/>
public sealed class CompensationTuningService : ICompensationTuningService
{
    private readonly IHmiSettingRepository repository;
    private readonly IChangeLog changeLog;
    private readonly TimeProvider timeProvider;

    public CompensationTuningService(
        IHmiSettingRepository repository,
        IChangeLog changeLog,
        MachineDescription machine,
        HmiSettings settings,
        TimeProvider timeProvider)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.changeLog = changeLog ?? throw new ArgumentNullException(nameof(changeLog));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(settings);

        MachineLimitMicrometer = machine.Thresholds.TryGetValue(CompensationService.MaxCompensationRadiusMmKey, out double radiusMm)
            ? UnitConversion.RadiusMmToDiameterMicrometer(radiusMm)
            : null;
        Configured = new CompensationTuning(settings.CompensationGain, settings.CompensationSmoothingPoints, MachineLimitMicrometer);
        Current = Configured;
    }

    public event EventHandler? Changed;

    public CompensationTuning Current { get; private set; }

    public CompensationTuning Configured { get; }

    public double? MachineLimitMicrometer { get; }

    public bool IsOverridden { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> stored = await this.repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        CompensationTuning tuning = Configured with
        {
            Gain = Number(stored, CompensationTuningKeys.Gain) ?? Configured.Gain,
            SmoothingPoints = (int?)Number(stored, CompensationTuningKeys.SmoothingPoints) ?? Configured.SmoothingPoints,
            MaxCorrectionMicrometer = Number(stored, CompensationTuningKeys.MaxCorrectionMicrometer) ?? Configured.MaxCorrectionMicrometer,
        };

        // 库里的覆盖值不成立（例如 machine.json 后来把限幅改小了）：不用它，回到配置值，免得算出越界的补偿。
        bool usable = tuning.FindInvalid(MachineLimitMicrometer) is null;
        Current = usable ? tuning : Configured;
        IsOverridden = usable && CompensationTuningKeys.All.Any(stored.ContainsKey);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(CompensationTuning tuning, string changedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        if (tuning.FindInvalid(MachineLimitMicrometer) is string invalid)
        {
            throw new DomainException($"Compensation setting '{invalid}' is outside its allowed range.");
        }

        CompensationTuning before = Current;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CompensationTuningKeys.Gain] = Text(tuning.Gain),
            [CompensationTuningKeys.SmoothingPoints] = Text(tuning.SmoothingPoints),
        };
        if (tuning.MaxCorrectionMicrometer is double max)
        {
            values[CompensationTuningKeys.MaxCorrectionMicrometer] = Text(max);
        }

        await this.repository.SaveAsync(values, changedBy, this.timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await this.changeLog.RecordAsync(ChangeLogAreas.Compensation, Diff(before, tuning), changedBy, cancellationToken)
            .ConfigureAwait(false);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(string changedBy, CancellationToken cancellationToken)
    {
        CompensationTuning before = Current;
        await this.repository.RemoveAsync(CompensationTuningKeys.All, cancellationToken).ConfigureAwait(false);
        await this.changeLog.RecordAsync(ChangeLogAreas.Compensation, Diff(before, Configured), changedBy, cancellationToken)
            .ConfigureAwait(false);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<ChangedItem> Diff(CompensationTuning before, CompensationTuning after)
    {
        yield return new ChangedItem(CompensationTuningKeys.Gain, Text(before.Gain), Text(after.Gain));
        yield return new ChangedItem(CompensationTuningKeys.SmoothingPoints, Text(before.SmoothingPoints), Text(after.SmoothingPoints));
        yield return new ChangedItem(
            CompensationTuningKeys.MaxCorrectionMicrometer,
            before.MaxCorrectionMicrometer is double oldMax ? Text(oldMax) : null,
            after.MaxCorrectionMicrometer is double newMax ? Text(newMax) : null);
    }

    private static double? Number(IReadOnlyDictionary<string, string> stored, string key) =>
        stored.TryGetValue(key, out string? text)
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
