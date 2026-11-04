using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using RollGrinder.Services.Jobs;

namespace RollGrinder.App.ViewModels;

/// <summary>可调范围、本次调整的显示文字（核对页、工艺页、自动页、记录页共用）。</summary>
public static class AdjustText
{
    public static string Number(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    /// <summary>参数名（没有译文退回键）。</summary>
    public static string Parameter(IStringLocalizer localizer, string key)
    {
        string text = localizer["Parameter_" + key];
        return text.StartsWith('!') ? key : text;
    }

    /// <summary>可调方式："±10%" / "只减" / "只加" / "4 – 8" / "🔒"。</summary>
    public static string Mode(IStringLocalizer localizer, AdjustRule rule) => rule.Mode switch
    {
        AdjustMode.Percent => localizer.Format("Adjust_ModePercentFormat", Number(rule.Percent)),
        AdjustMode.DecreaseOnly => localizer["Adjust_ModeDecrease"],
        AdjustMode.IncreaseOnly => localizer["Adjust_ModeIncrease"],
        AdjustMode.Absolute => localizer.Format("Adjust_ModeAbsoluteFormat", Number(rule.Lower ?? 0.0), Number(rule.Upper ?? 0.0)),
        _ => localizer["Adjust_ModeLocked"],
    };

    /// <summary>允许的绝对范围："720 – 880" / "≤ 20" / "≥ 2" / "🔒"。</summary>
    public static string Bounds(IStringLocalizer localizer, AdjustBounds bounds) => bounds switch
    {
        { Adjustable: false } => localizer["Adjust_ModeLocked"],
        { Lower: double low, Upper: double high } => localizer.Format("Adjust_BetweenFormat", Number(low), Number(high)),
        { Upper: double high } => localizer.Format("Adjust_AtMostFormat", Number(high)),
        { Lower: double low } => localizer.Format("Adjust_AtLeastFormat", Number(low)),
        _ => "--",
    };

    /// <summary>"第 2 道 粗磨 拖板速度 800 → 760"。</summary>
    public static string Change(IStringLocalizer localizer, JobAdjustment adjustment, string? stepTypeKey) =>
        localizer.Format(
            "Adjust_ChangeFormat",
            adjustment.StepOrder,
            stepTypeKey is null ? string.Empty : localizer["StepType_" + stepTypeKey],
            Parameter(localizer, adjustment.ParameterKey),
            Number(adjustment.PlanValue),
            Number(adjustment.JobValue));

    /// <summary>被拒的原因。</summary>
    public static string Refusal(IStringLocalizer localizer, AdjustRefusal refusal) => localizer["Adjust_" + refusal];
}

/// <summary>
/// 本次调整（设计稿 v4 5.2.5）的一行：工序、参数、设定值（方案值）、本次值、允许（绝对范围）、上次的值。
/// 本次值超范围时行标红、说原因，"✓ 用于本次"不收。
/// </summary>
public sealed partial class AdjustRowViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;

    public AdjustRowViewModel(AdjustableParameter parameter, double value, double? lastValue, IStringLocalizer localizer)
    {
        Parameter = parameter ?? throw new ArgumentNullException(nameof(parameter));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        StepText = localizer.Format("Adjust_StepFormat", parameter.StepOrder, localizer["StepType_" + parameter.StepTypeKey]);
        ParameterText = AdjustText.Parameter(localizer, parameter.ParameterKey);
        UnitText = parameter.Descriptor.Unit == ParameterUnit.None ? string.Empty : localizer["Unit_" + parameter.Descriptor.Unit];
        PlanText = AdjustText.Number(parameter.PlanValue);
        AllowedText = AdjustText.Bounds(localizer, parameter.Bounds);
        ModeText = AdjustText.Mode(localizer, parameter.Rule);
        LastValue = lastValue;
        LastText = lastValue is double last ? localizer.Format("Adjust_LastFormat", AdjustText.Number(last)) : string.Empty;
        this.valueText = AdjustText.Number(value);
        Refresh();
    }

    public AdjustableParameter Parameter { get; }

    public string StepText { get; }

    public string ParameterText { get; }

    public string UnitText { get; }

    /// <summary>设定值（方案值）。</summary>
    public string PlanText { get; }

    /// <summary>允许的绝对范围。</summary>
    public string AllowedText { get; }

    /// <summary>可调方式（±% / 只减 / 只加）。</summary>
    public string ModeText { get; }

    /// <summary>上次作业的值（"上次 760"）；没调过为空。</summary>
    public string LastText { get; }

    public double? LastValue { get; }

    public bool HasLast => LastValue is not null;

    /// <summary>本次值（输入）。</summary>
    [ObservableProperty]
    private string valueText;

    /// <summary>与设定值不同（行尾标 Δ）。</summary>
    [ObservableProperty]
    private bool isChanged;

    /// <summary>超范围（不收）。</summary>
    [ObservableProperty]
    private bool isRefused;

    [ObservableProperty]
    private string refusalText = string.Empty;

    /// <summary>"Δ −40"。</summary>
    [ObservableProperty]
    private string deltaText = string.Empty;

    /// <summary>范围条：下限、上限都有时可拖。</summary>
    public bool HasSlider => Parameter.Bounds is { Lower: double, Upper: double } bounds && bounds.Upper > bounds.Lower;

    public double SliderMinimum => Parameter.Bounds.Lower ?? 0.0;

    public double SliderMaximum => Parameter.Bounds.Upper ?? 0.0;

    public double SliderValue
    {
        get => Value is double value ? Math.Clamp(value, SliderMinimum, SliderMaximum) : Parameter.PlanValue;
        set
        {
            double stepped = Math.Round(value / Step) * Step;
            ValueText = AdjustText.Number(Math.Clamp(stepped, SliderMinimum, SliderMaximum));
        }
    }

    /// <summary>读得懂的本次值；读不懂为 null。</summary>
    public double? Value =>
        double.TryParse(ValueText, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
        || double.TryParse(ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            ? value
            : null;

    /// <summary>−/+ 一下走多少：道次类 1，其余约为设定值的 1%（取一位有效数字）。</summary>
    public double Step
    {
        get
        {
            if (Parameter.Descriptor.Unit == ParameterUnit.Count)
            {
                return 1.0;
            }

            double basis = Math.Abs(Parameter.PlanValue) * 0.01;
            if (basis <= 0.0)
            {
                return 0.1;
            }

            double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(basis)));
            return Math.Round(basis / magnitude) * magnitude;
        }
    }

    public void Nudge(int direction)
    {
        double current = Value ?? Parameter.PlanValue;
        double next = current + (direction * Step);
        next = Math.Round(next / Step) * Step;
        ValueText = AdjustText.Number(next);
    }

    public void Restore() => ValueText = AdjustText.Number(Parameter.PlanValue);

    partial void OnValueTextChanged(string value)
    {
        Refresh();
        OnPropertyChanged(nameof(SliderValue));
    }

    private void Refresh()
    {
        double? value = Value;
        if (value is not double v)
        {
            IsChanged = true;
            IsRefused = true;
            RefusalText = this.localizer["Check_NotANumber"];
            DeltaText = string.Empty;
            return;
        }

        IsChanged = Math.Abs(v - Parameter.PlanValue) > 1e-9;
        AdjustRefusal refusal = Parameter.Check(v);
        IsRefused = refusal != AdjustRefusal.None;
        RefusalText = IsRefused ? AdjustText.Refusal(this.localizer, refusal) : string.Empty;
        DeltaText = IsChanged
            ? this.localizer.Format("Adjust_DeltaFormat", (v - Parameter.PlanValue).ToString("+0.###;-0.###", CultureInfo.CurrentCulture))
            : string.Empty;
    }
}

/// <summary>不可调（🔒）的参数一行：要改就改方案。</summary>
public sealed record LockedParameterRowViewModel(string StepText, string ParameterText, string ValueText);

/// <summary>辅助：把本次调整按参数取最后一次。</summary>
internal static class AdjustmentLists
{
    public static IReadOnlyList<JobAdjustment> Latest(IEnumerable<JobAdjustment> adjustments) =>
        adjustments
            .GroupBy(item => (item.StepOrder, item.ParameterKey))
            .Select(group => group.Last())
            .ToArray();
}
