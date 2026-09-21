using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core;
using RollGrinder.Core.Calibration;
using RollGrinder.Core.Parameters;
using RollGrinder.Data;

namespace RollGrinder.Services.Calibration;

/// <summary>
/// 现场标定值的持有者。
///
/// 界面与服务读 <see cref="Current"/>——一份不可变快照，取值不用 await，
/// 改动由设置页经 <see cref="SaveAsync"/> 走一遍，改完广播一次。
/// </summary>
public interface ICalibrationService
{
    /// <summary>当前标定值。没载入过时是全套默认值。</summary>
    MachineCalibration Current { get; }

    /// <summary>标定值改了。</summary>
    event EventHandler? Changed;

    /// <summary>从库里载入。启动时调一次。</summary>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>保存改动，记下改动人。</summary>
    Task SaveAsync(ParameterSet values, string changedBy, CancellationToken cancellationToken);

    /// <summary>每一项最后一次是谁改的、什么时候改的。</summary>
    Task<IReadOnlyList<CalibrationAudit>> LoadAuditAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICalibrationService"/>
public sealed class CalibrationService : ICalibrationService
{
    private readonly ICalibrationRepository repository;

    public CalibrationService(ICalibrationRepository repository)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public MachineCalibration Current { get; private set; } = MachineCalibration.Defaults;

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        ParameterSet stored = await this.repository.LoadAsync(cancellationToken).ConfigureAwait(false);

        // 库里一条都没有也照样成立：MachineCalibration 会把缺的键补成默认值。
        Current = new MachineCalibration(stored);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(ParameterSet values, string changedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);

        ParameterValidationResult result = MachineCalibration.Schema.Validate(values);
        if (!result.IsValid)
        {
            throw new DomainException(
                $"Calibration value '{result.Violations[0].ParameterKey}' is outside its allowed range.");
        }

        await this.repository.SaveAsync(values, changedBy, cancellationToken).ConfigureAwait(false);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CalibrationAudit>> LoadAuditAsync(CancellationToken cancellationToken) =>
        this.repository.LoadAuditAsync(cancellationToken);
}
