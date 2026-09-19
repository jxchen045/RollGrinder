using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Units;

namespace RollGrinder.Sim;

/// <summary>
/// 一台"会动"的假机床：按下发的参数模拟走刀、去除量与测量值。
/// 只用于无机床环境下的调试与自动测试，时间由调用方推进，结果可复现。
/// 这里的系数是仿真保真度参数，不是机床配置——机床差异一律来自 machine.json。
/// </summary>
public sealed class SimulatedMachine
{
    /// <summary>程序启动时假定的待磨余量（半径量 mm）。</summary>
    public const double InitialStockRadiusMm = 0.30;

    /// <summary>磨削时的去除速度（半径量 mm/min）。</summary>
    public const double RemovalRateMmPerMinute = 0.06;

    /// <summary>测量值的模拟波动幅度（半径量 mm）。</summary>
    public const double MeasurementNoiseRadiusMm = 0.0005;

    private readonly MachineDescription machine;
    private readonly Dictionary<string, TagValue> writtenValues = new(StringComparer.Ordinal);
    private readonly string? carriageAxisName;
    private readonly string? infeedAxisName;
    private readonly string? workpieceSpindleName;
    private readonly string? wheelSpindleName;

    private double targetRadiusMm;
    private double currentRadiusMm;
    private double bodyLengthMm;
    private double feedMmPerMin;
    private double carriagePositionMm;
    private int carriageDirection = 1;
    private double elapsedSeconds;

    public SimulatedMachine(MachineDescription machine)
    {
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));

        this.carriageAxisName = AxisName(MachineAxisRoles.Carriage);
        this.infeedAxisName = AxisName(MachineAxisRoles.InfeedRadius);
        this.workpieceSpindleName = AxisName(MachineAxisRoles.WorkpieceSpindle);
        this.wheelSpindleName = AxisName(MachineAxisRoles.WheelSpindle);

        this.targetRadiusMm = UnitConversion.DiameterMmToRadiusMm(machine.Workpiece.MinDiameterMm);
        this.currentRadiusMm = this.targetRadiusMm;
        this.bodyLengthMm = machine.Workpiece.MinBodyLengthMm;
        this.feedMmPerMin = 1000.0;
    }

    /// <summary>当前通道状态。</summary>
    public NcChannelState ChannelState { get; private set; } = NcChannelState.Reset;

    /// <summary>当前辊件半径（mm）。</summary>
    public double CurrentRadiusMm => this.currentRadiusMm;

    /// <summary>还剩多少余量（半径量 mm）。</summary>
    public double RemainingStockRadiusMm => Math.Max(0.0, this.currentRadiusMm - this.targetRadiusMm);

    /// <summary>当前程序名。</summary>
    public string ProgramName { get; private set; } = string.Empty;

    /// <summary>接受一次写入。参数有效标志置真即开始模拟磨削。</summary>
    public void Write(string logicalName, TagValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentNullException.ThrowIfNull(value);

        this.writtenValues[logicalName] = value;

        switch (logicalName)
        {
            case MachineTagKeys.JobRollRadiusMm:
                this.targetRadiusMm = ToDouble(value.Raw) ?? this.targetRadiusMm;
                break;

            case MachineTagKeys.JobBodyLengthMm:
                this.bodyLengthMm = ToDouble(value.Raw) ?? this.bodyLengthMm;
                break;

            case MachineTagKeys.JobFeedMmPerMin:
                this.feedMmPerMin = ToDouble(value.Raw) ?? this.feedMmPerMin;
                break;

            case MachineTagKeys.JobParametersValid when value.Raw is bool valid:
                if (valid)
                {
                    StartProgram();
                }
                else
                {
                    ChannelState = NcChannelState.Reset;
                }

                break;

            default:
                break;
        }
    }

    /// <summary>推进仿真时间。</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        this.elapsedSeconds += delta.TotalSeconds;
        if (ChannelState != NcChannelState.Running)
        {
            return;
        }

        double minutes = delta.TotalMinutes;

        // 纵向拖板在辊身两端之间往复。
        double travelMm = this.feedMmPerMin * minutes;
        this.carriagePositionMm += travelMm * this.carriageDirection;
        if (this.carriagePositionMm >= this.bodyLengthMm)
        {
            this.carriagePositionMm = this.bodyLengthMm;
            this.carriageDirection = -1;
        }
        else if (this.carriagePositionMm <= 0.0)
        {
            this.carriagePositionMm = 0.0;
            this.carriageDirection = 1;
        }

        // 去除量按恒定速率逼近目标半径。
        double removalMm = RemovalRateMmPerMinute * minutes;
        this.currentRadiusMm = Math.Max(this.targetRadiusMm, this.currentRadiusMm - removalMm);

        if (this.currentRadiusMm <= this.targetRadiusMm + double.Epsilon)
        {
            ChannelState = NcChannelState.Reset;
            ProgramName = string.Empty;
        }
    }

    /// <summary>读取一个逻辑变量；仿真不认识的变量返回 null，由调用方决定怎么处理。</summary>
    public object? Read(string logicalName)
    {
        if (logicalName == MachineTagKeys.ChannelState)
        {
            return (int)ChannelState;
        }

        if (logicalName == MachineTagKeys.ProgramName)
        {
            return ProgramName;
        }

        if (logicalName == MachineTagKeys.MeasuredDiameterMm)
        {
            return UnitConversion.RadiusMmToDiameterMm(this.currentRadiusMm + Noise());
        }

        if (this.carriageAxisName is not null && logicalName == MachineTagKeys.AxisActualPositionMm(this.carriageAxisName))
        {
            return this.carriagePositionMm;
        }

        if (this.infeedAxisName is not null && logicalName == MachineTagKeys.AxisActualPositionMm(this.infeedAxisName))
        {
            return this.currentRadiusMm;
        }

        if (this.workpieceSpindleName is not null && logicalName == MachineTagKeys.AxisActualSpeedRpm(this.workpieceSpindleName))
        {
            return ChannelState == NcChannelState.Running ? 20.0 : 0.0;
        }

        if (this.wheelSpindleName is not null && logicalName == MachineTagKeys.AxisActualSpeedRpm(this.wheelSpindleName))
        {
            return ChannelState == NcChannelState.Running ? 900.0 : 0.0;
        }

        return this.writtenValues.TryGetValue(logicalName, out TagValue? written) ? written.Raw : null;
    }

    private void StartProgram()
    {
        this.currentRadiusMm = this.targetRadiusMm + InitialStockRadiusMm;
        this.carriagePositionMm = 0.0;
        this.carriageDirection = 1;
        ChannelState = NcChannelState.Running;
        ProgramName = string.Create(
            CultureInfo.InvariantCulture,
            $"SIM_{this.machine.MachineId}.MPF");
    }

    private double Noise()
    {
        // 确定性的"噪声"：同样的时间序列给同样的结果，测试才可复现。
        return MeasurementNoiseRadiusMm * Math.Sin(this.elapsedSeconds * 1.7);
    }

    private string? AxisName(string role) => this.machine.Axes
        .FirstOrDefault(axis => axis.IsPresent && string.Equals(axis.Role, role, StringComparison.Ordinal))?.Name;

    private static double? ToDouble(object? raw) => raw switch
    {
        double number => number,
        int integer => integer,
        IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
        _ => null,
    };
}
