using System;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Centring;
using RollGrinder.Services.Calibration;

namespace RollGrinder.Services.Measurement;

/// <summary>
/// 对中：在辊的两端各记一组读数，比出装夹偏差。
///
/// **只读机床，不动机床。** 测量臂开到哪一端、什么时候读数，由操作工在
/// 手动页上自己操作；这里只在按下"记录本端"时把当时的几个数抓下来。
/// 怎么调中心架也由人来做——上位机给的是一个方向和一个数，不是一条指令。
/// </summary>
public interface ICentringService
{
    /// <summary>抓一组当前读数，记在指定那一端。</summary>
    Task<CentringReading> CaptureAsync(RollEnd end, CancellationToken cancellationToken);

    /// <summary>拖板轴的名字。界面按它写行头——轴名来自 machine.json，不在代码里写死。</summary>
    string CarriageAxisName { get; }

    /// <summary>进给轴的名字。</summary>
    string InfeedAxisName { get; }

    /// <summary>某一端已记下的那一组；没记过返回 null。</summary>
    CentringReading? Reading(RollEnd end);

    /// <summary>两端都记过时给出比较结果；缺一端返回 null。</summary>
    CentringComparison? Compare();

    /// <summary>清掉两端的记录（换辊、重新找正）。</summary>
    void Clear();

    /// <summary>两端的记录有变化。</summary>
    event EventHandler? Changed;
}

/// <inheritdoc cref="ICentringService"/>
public sealed class CentringService : ICentringService
{
    private readonly IMachineGateway gateway;
    private readonly MachineDescription machine;
    private readonly ICalibrationService calibration;
    private readonly TimeProvider timeProvider;

    private CentringReading? head;
    private CentringReading? tail;

    public CentringService(
        IMachineGateway gateway,
        MachineDescription machine,
        ICalibrationService calibration,
        TimeProvider timeProvider)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler? Changed;

    /// <inheritdoc/>
    public string CarriageAxisName => AxisName(MachineAxisRoles.Carriage);

    /// <inheritdoc/>
    public string InfeedAxisName => AxisName(MachineAxisRoles.InfeedRadius);

    public async Task<CentringReading> CaptureAsync(RollEnd end, CancellationToken cancellationToken)
    {
        // 一次读齐：几个数必须是同一时刻的，分开读中间辊转过去了就对不上。
        MachineStateSnapshot snapshot = await this.gateway.ReadStateAsync(
            new[]
            {
                MachineTagKeys.MeasureProbeAMm,
                MachineTagKeys.MeasureProbeBMm,
                MachineTagKeys.MeasuredDiameterMm,
                MachineTagKeys.AxisActualPositionMm(CarriageAxisName),
                MachineTagKeys.AxisActualPositionMm(InfeedAxisName),
            },
            cancellationToken).ConfigureAwait(false);

        var reading = new CentringReading(
            end,
            Require(snapshot, MachineTagKeys.MeasureProbeAMm),
            Require(snapshot, MachineTagKeys.MeasureProbeBMm),
            Require(snapshot, MachineTagKeys.MeasuredDiameterMm),
            Require(snapshot, MachineTagKeys.AxisActualPositionMm(CarriageAxisName)),
            Require(snapshot, MachineTagKeys.AxisActualPositionMm(InfeedAxisName)),
            this.timeProvider.GetUtcNow());

        if (end == RollEnd.Head)
        {
            this.head = reading;
        }
        else
        {
            this.tail = reading;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return reading;
    }

    public CentringReading? Reading(RollEnd end) => end == RollEnd.Head ? this.head : this.tail;

    public CentringComparison? Compare() => this.head is null || this.tail is null
        ? null
        : CentringComparison.Compare(
            this.head, this.tail, this.calibration.Current.CentringToleranceMicrometer);

    public void Clear()
    {
        this.head = null;
        this.tail = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string AxisName(string role)
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

    /// <summary>
    /// 少一个数就不记。对中是拿两组数相减，缺一项算出来的差是错的，
    /// 而错的对中会让人去调一台本来就正的机床。
    /// </summary>
    private static double Require(MachineStateSnapshot snapshot, string logicalName) =>
        snapshot.GetNumberOrNull(logicalName)
        ?? throw new GatewayException($"Tag '{logicalName}' did not return a usable value.");
}
