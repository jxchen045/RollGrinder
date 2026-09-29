using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 手动磨削页上一格会马上影响机床的值：给定、倍率、往复速度、行程（最终稿 4.5、5.1）。
///
/// 规则：点选再点弹键盘；输入后<b>先标红</b>，对话行问"xx → yy，按 ✓ 确认写入"，确认才写；
/// 取消或 5 秒不答，恢复成已写入的值。倍率格旁有 −5 / +5 大键，同样走标红 → 确认。
/// 不写机床的格（例如定位目标）不走确认，改了就算。
/// </summary>
public sealed partial class SetpointViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;
    private readonly Action<SetpointViewModel, double> onChanged;
    private bool suppress;

    /// <param name="labelResourceKey">标签。</param>
    /// <param name="unitResourceKey">单位（资源键）。</param>
    /// <param name="minimum">下限。</param>
    /// <param name="maximum">上限。</param>
    /// <param name="decimals">小数位数。</param>
    /// <param name="step">−/+ 键一次改多少；0 表示没有 −/+ 键。</param>
    /// <param name="localizer">取字。</param>
    /// <param name="onChanged">人改了值（已在范围内）时调：由页面决定问不问、怎么写。</param>
    public SetpointViewModel(
        string labelResourceKey,
        string unitResourceKey,
        double minimum,
        double maximum,
        int decimals,
        double step,
        IStringLocalizer localizer,
        Action<SetpointViewModel, double> onChanged)
    {
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        this.onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        Label = localizer[labelResourceKey];
        Unit = localizer[unitResourceKey];
        Minimum = minimum;
        Maximum = maximum;
        Decimals = decimals;
        Step = step;
        HasStep = step > 0;
        StepDownCommand = new RelayCommand(() => Nudge(-step));
        StepUpCommand = new RelayCommand(() => Nudge(step));
    }

    public string Label { get; }

    public string Unit { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    public int Decimals { get; }

    public double Step { get; }

    /// <summary>有 −/+ 键（倍率格）。</summary>
    public bool HasStep { get; }

    public RelayCommand StepDownCommand { get; }

    public RelayCommand StepUpCommand { get; }

    /// <summary>选中时对话行的说明：含义、单位、范围。</summary>
    public string HintText => this.localizer.Format("Setpoint_HintFormat", Label, Unit, Format(Minimum), Format(Maximum));

    /// <summary>格里的字。</summary>
    [ObservableProperty]
    private string text = string.Empty;

    /// <summary>改了还没确认写入：标红。</summary>
    [ObservableProperty]
    private bool isPending;

    /// <summary>已经写进机床（或读回来）的值。</summary>
    [ObservableProperty]
    private double? committed;

    /// <summary>当前格里的数（解析不了为 null）。</summary>
    public double? Value => double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
        || double.TryParse(Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v)
            ? v
            : null;

    /// <summary>把值当成已写入（写成功、或者从机床读回来）。标红撤掉。</summary>
    public void Accept(double value)
    {
        Committed = value;
        this.suppress = true;
        Text = Format(value);
        this.suppress = false;
        IsPending = false;
    }

    /// <summary>从机床读回来的值：人正在改（标红）时不覆盖。</summary>
    public void Observe(double? value)
    {
        if (IsPending || value is null)
        {
            return;
        }

        if (Committed is null || Math.Abs(Committed.Value - value.Value) > Math.Pow(10, -Decimals) / 2)
        {
            Accept(value.Value);
        }
    }

    /// <summary>放弃改动：恢复成已写入的值。</summary>
    public void Revert()
    {
        this.suppress = true;
        Text = Committed is { } value ? Format(value) : string.Empty;
        this.suppress = false;
        IsPending = false;
    }

    /// <summary>显示用的格式（与界面语言无关，数不随中英文变样）。</summary>
    public string Format(double value) => value.ToString("F" + Decimals, CultureInfo.InvariantCulture);

    partial void OnTextChanged(string value)
    {
        if (this.suppress)
        {
            return;
        }

        if (Value is not { } parsed || parsed < Minimum || parsed > Maximum)
        {
            // 键盘已经挡过范围；这里是键盘以外的输入（实体键盘）写进来的越界值：不收。
            Revert();
            return;
        }

        if (Committed is { } current && Math.Abs(current - parsed) < Math.Pow(10, -Decimals) / 2)
        {
            IsPending = false;
            return;
        }

        IsPending = true;
        this.onChanged(this, parsed);
    }

    private void Nudge(double delta)
    {
        double from = Value ?? Committed ?? Minimum;
        Text = Format(Math.Clamp(from + delta, Minimum, Maximum));
    }
}
