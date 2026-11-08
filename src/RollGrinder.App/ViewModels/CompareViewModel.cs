using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Services.Library;

namespace RollGrinder.App.ViewModels;

/// <summary>对比的一行：哪一项、A、B；两边不同的加粗、浅琥珀底。</summary>
public sealed record DiffRowViewModel(string ItemText, string AText, string BText, bool Differs);

/// <summary>
/// 对比（设计稿 v4 5.5.1 方案列表、5.6.1 模板库的"对比"）：左边逐项列出两边的辊形 / 工艺 / 可调范围，
/// 右边两条辊形叠图（以辊身中心为 0）。方案区、模板区共用。
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly HmiSettings settings;
    private IReadOnlyList<DiffRow> rows = Array.Empty<DiffRow>();

    public CompareViewModel(IStringLocalizer localizer, RollProfileTypeRegistry profileTypes, GrindingStepTypeRegistry stepTypes, HmiSettings settings)
    {
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public ObservableCollection<DiffRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private string titleA = string.Empty;

    [ObservableProperty]
    private string titleB = string.Empty;

    [ObservableProperty]
    private string summary = string.Empty;

    /// <summary>只看不同（默认）；关掉看全部。</summary>
    [ObservableProperty]
    private bool onlyDifferences = true;

    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> CurveA { get; private set; } = Array.Empty<(double, double)>();

    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> CurveB { get; private set; } = Array.Empty<(double, double)>();

    public bool HasCurve => CurveA.Count > 1 || CurveB.Count > 1;

    public event EventHandler? CurvesChanged;

    partial void OnOnlyDifferencesChanged(bool value) => Rebuild();

    public void Load(CompareSide a, CompareSide b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        TitleA = a.Title;
        TitleB = b.Title;
        this.rows = PlanDiff.Compare(a, b, this.stepTypes);
        CurveA = Compose(a.Profile);
        CurveB = Compose(b.Profile);
        OnPropertyChanged(nameof(HasCurve));
        Summary = this.localizer.Format("Compare_SummaryFormat", this.rows.Count(row => row.Differs), this.rows.Count);
        Rebuild();
        CurvesChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<(double, double)> Compose(RollProfileDefinition? profile)
    {
        if (profile is null)
        {
            return Array.Empty<(double, double)>();
        }

        try
        {
            return profile.Profile.Compose(RollGeometry.FromDiameter(profile.BodyLengthMm, RollGeometry.PreviewDiameterMm), this.profileTypes, this.settings.ProfileSampleCount)
                .Points.Select(point => (point.BodyPositionMm, RollGrinder.Core.Units.UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
                .ToArray();
        }
        catch (RollGrinder.Core.DomainException)
        {
            return Array.Empty<(double, double)>();
        }
    }

    private void Rebuild()
    {
        Rows.Clear();
        foreach (DiffRow row in this.rows.Where(row => !OnlyDifferences || row.Differs))
        {
            Rows.Add(new DiffRowViewModel(Item(row), Value(row, row.A), Value(row, row.B), row.Differs));
        }
    }

    private string Item(DiffRow row)
    {
        string parameter = row.ParameterKey is null ? string.Empty : AdjustText.Parameter(this.localizer, row.ParameterKey);
        string step = row.TypeKey is null || row.Field is not (DiffField.StepParameter or DiffField.StepType) ? string.Empty : this.localizer["StepType_" + row.TypeKey];
        return row.Field switch
        {
            DiffField.Name => this.localizer[row.TypeKey == "program" ? "Diff_ProgramName" : "Diff_ProfileName"],
            DiffField.SegmentType => this.localizer.Format("Diff_SegmentTypeFormat", row.Index ?? 0),
            DiffField.SegmentLength => this.localizer.Format("Diff_SegmentLengthFormat", row.Index ?? 0),
            DiffField.SegmentParameter => this.localizer.Format("Diff_SegmentParameterFormat", row.Index ?? 0, parameter),
            DiffField.StepType => this.localizer.Format("Diff_StepTypeFormat", row.Index ?? 0),
            DiffField.StepParameter => this.localizer.Format("Diff_StepParameterFormat", row.Index ?? 0, step, parameter),
            DiffField.Range => this.localizer.Format("Diff_RangeFormat", row.Index ?? 0, parameter),
            DiffField.NominalLength => this.localizer["Diff_NominalLength"],
            DiffField.Tolerance => this.localizer["Diff_Tolerance"],
            DiffField.EndZone => this.localizer["Diff_EndZone"],
            DiffField.SegmentCount => this.localizer["Diff_SegmentCount"],
            DiffField.ApplicableKind => this.localizer["Diff_ApplicableKind"],
            DiffField.StandardStock => this.localizer["Diff_StandardStock"],
            DiffField.StepCount => this.localizer["Diff_StepCount"],
            _ => row.Field.ToString(),
        };
    }

    private string Value(DiffRow row, string raw)
    {
        if (raw.Length == 0)
        {
            return "—";
        }

        return row.Field switch
        {
            DiffField.SegmentType => this.localizer["ProfileType_" + raw],
            DiffField.StepType => this.localizer["StepType_" + raw],
            DiffField.ApplicableKind => this.localizer["RollKind_" + raw],
            DiffField.Range when AdjustRule.Parse(raw) is { } rule => AdjustText.Mode(this.localizer, rule),
            _ => raw,
        };
    }
}
