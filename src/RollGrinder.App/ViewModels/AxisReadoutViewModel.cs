using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 位置窗里的一行（最终稿 5.1、F1）：轴名、用途、机床坐标，数字 L1 54 px。
/// 手持盒选中的那根轴整行青底、写"◀"——操作者一眼知道手轮现在摇的是谁。
/// </summary>
public sealed partial class AxisReadoutViewModel : ObservableObject
{
    private readonly string format;

    private AxisReadoutViewModel(AxisDescription axis, int? pendantCode, IStringLocalizer localizer)
    {
        AxisName = axis.Name;
        Role = axis.Role;
        RoleText = localizer["AxisRole_" + axis.Role];
        PendantCode = pendantCode;
        this.format = "F3";
    }

    /// <summary>NC 里的轴名（X、X1、Z、U）——面板上印的就是这个，不翻译。</summary>
    public string AxisName { get; }

    public string Role { get; }

    /// <summary>用途（磨架、测量架、拖板、辊形轴）。</summary>
    public string RoleText { get; }

    /// <summary>手持盒选轴开关上这根轴的档位（pendant.axisSelect：1 X、2 Y/U、3 Z、4 X1）。</summary>
    public int? PendantCode { get; }

    [ObservableProperty]
    private string valueText = "--";

    /// <summary>手持盒现在选的是这根轴。</summary>
    [ObservableProperty]
    private bool isPendantSelected;

    /// <summary>刷新读数与手持盒选轴。</summary>
    public void Update(MachineStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        double? value = snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(AxisName));
        ValueText = value is null ? "--" : value.Value.ToString(this.format, CultureInfo.CurrentCulture);
        IsPendantSelected = PendantCode is { } code
            && snapshot.GetNumberOrNull(MachineTagKeys.PendantAxisSelect) is { } selected
            && (int)selected == code;
    }

    /// <summary>按用途列出机床上装了的直线轴（顺序照 <paramref name="roles"/>）。</summary>
    public static IReadOnlyList<AxisReadoutViewModel> For(
        MachineDescription machine, IStringLocalizer localizer, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(localizer);
        return roles
            .SelectMany(role => machine.Axes.Where(axis => axis.IsPresent
                && string.Equals(axis.Role, role, StringComparison.Ordinal)))
            .Select(axis => new AxisReadoutViewModel(axis, PendantCodeOf(axis.Role), localizer))
            .ToArray();
    }

    /// <summary>手持盒选轴开关的档位（最终稿 7.2）。</summary>
    public static int? PendantCodeOf(string role) => role switch
    {
        MachineAxisRoles.InfeedRadius => 1,
        MachineAxisRoles.RollProfile => 2,
        MachineAxisRoles.Carriage => 3,
        MachineAxisRoles.MeasuringCarriage => 4,
        _ => null,
    };
}
