using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.Core.Geometry;
using RollGrinder.Data.Model;
using RollGrinder.Services.Records;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 磨削记录 › 轧辊台账（阶段 1，修改稿 5.1）：辊号、类型、辊身长度、公称直径、当前直径、材质、
/// 重量与磨削履历都在这里登记、修改。尺寸属于轧辊本身——作业只是选一支台账里的辊。
/// </summary>
public sealed partial class RecordsViewModel
{
    /// <summary>磨削履历一次列多少条。</summary>
    private const int HistoryLimit = 50;

    private readonly IRollLedgerService ledgerService;

    /// <summary>正在编辑的那支辊原来的样子（改已有的辊时保留登记时间与编号）。</summary>
    private RollRecord? editingRoll;

    /// <summary>程序里换选中行时（重读列表）不要再触发一次加载——那边自己会等着加载完。</summary>
    private bool suppressLedgerSelection;

    [ObservableProperty]
    private RollLedgerRowViewModel? selectedLedgerRow;

    /// <summary>右边的表是在登记一支新辊（辊号可填），还是在改已有的辊（辊号不能改）。</summary>
    [ObservableProperty]
    private bool isNewLedgerRoll;

    [ObservableProperty]
    private string ledgerRollId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LedgerIsWorkRoll), nameof(LedgerIsBackupRoll))]
    private RollKind ledgerKind = RollKind.WorkRoll;

    [ObservableProperty]
    private string ledgerBodyLengthText = string.Empty;

    [ObservableProperty]
    private string ledgerDiameterText = string.Empty;

    [ObservableProperty]
    private string ledgerCurrentDiameterText = string.Empty;

    [ObservableProperty]
    private string ledgerMaterial = string.Empty;

    [ObservableProperty]
    private string ledgerGrindStartText = string.Empty;

    [ObservableProperty]
    private string ledgerCurveLengthText = string.Empty;

    [ObservableProperty]
    private string ledgerCurveToleranceText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LedgerTotalWeightText))]
    private string ledgerNetWeightText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LedgerTotalWeightText))]
    private string ledgerHeadBoxWeightText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LedgerTotalWeightText))]
    private string ledgerTailBoxWeightText = string.Empty;

    public bool LedgerIsWorkRoll => LedgerKind == RollKind.WorkRoll;

    public bool LedgerIsBackupRoll => LedgerKind == RollKind.BackupRoll;

    /// <summary>吊装总重：三项都填了才算，缺一项写 "--"。</summary>
    public string LedgerTotalWeightText =>
        TryOptional(LedgerNetWeightText, out double? net) && net is double n
        && TryOptional(LedgerHeadBoxWeightText, out double? head) && head is double h
        && TryOptional(LedgerTailBoxWeightText, out double? tail) && tail is double t
            ? (n + h + t).ToString("F0", CultureInfo.CurrentCulture)
            : "--";

    /// <summary>上一次"保存"没存进去的原因，逐条。</summary>
    public ObservableCollection<string> LedgerProblems { get; } = new();

    /// <summary>选中那支辊的磨削履历。</summary>
    public ObservableCollection<LedgerHistoryRowViewModel> LedgerHistory { get; } = new();

    partial void OnSelectedLedgerRowChanged(RollLedgerRowViewModel? value)
    {
        if (value is not null && !this.suppressLedgerSelection)
        {
            _ = RunGuardedAsync(token => ShowLedgerRollAsync(value.RollId, token), CancellationToken.None);
        }
    }

    /// <summary>登记一支新辊：表清空，辊号可填，类型默认工作辊。</summary>
    [RelayCommand]
    private void NewLedgerRoll()
    {
        this.suppressLedgerSelection = true;
        SelectedLedgerRow = null;
        this.suppressLedgerSelection = false;
        this.editingRoll = null;
        IsNewLedgerRoll = true;
        LedgerRollId = string.Empty;
        LedgerKind = RollKind.WorkRoll;
        LedgerBodyLengthText = string.Empty;
        LedgerDiameterText = string.Empty;
        LedgerCurrentDiameterText = string.Empty;
        LedgerMaterial = string.Empty;
        LedgerGrindStartText = string.Empty;
        LedgerCurveLengthText = string.Empty;
        LedgerCurveToleranceText = string.Empty;
        LedgerNetWeightText = string.Empty;
        LedgerHeadBoxWeightText = string.Empty;
        LedgerTailBoxWeightText = string.Empty;
        LedgerProblems.Clear();
        LedgerHistory.Clear();
    }

    [RelayCommand]
    private void SetLedgerKind(RollKind kind) => LedgerKind = kind;

    /// <summary>校验并存；存不进去就把原因逐条列在表下面。</summary>
    [RelayCommand]
    private Task SaveLedgerRollAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            LedgerProblems.Clear();
            RollRecord? roll = ReadLedgerForm();
            if (roll is null)
            {
                LedgerProblems.Add(Localizer["Ledger_Problem_NotANumber"]);
                return;
            }

            RollLedgerSaveResult result = await this.ledgerService.SaveAsync(roll, IsNewLedgerRoll, token).ConfigureAwait(true);
            if (!result.Saved)
            {
                foreach (RollLedgerProblem problem in result.Problems)
                {
                    LedgerProblems.Add(DescribeLedgerProblem(problem));
                }

                return;
            }

            StatusResourceKey = "Ledger_Saved";
            await ReloadLedgerAsync(roll.RollId.Trim(), token).ConfigureAwait(true);
        }, cancellationToken);

    /// <summary>重读台账列表，并选中 <paramref name="selectRollId"/>（没有就选第一支；一支都没有就开一张新表）。</summary>
    private async Task ReloadLedgerAsync(string? selectRollId, CancellationToken cancellationToken)
    {
        Ledger.Clear();
        foreach (RollLedgerRow row in await this.recordService.LoadLedgerAsync(500, cancellationToken).ConfigureAwait(true))
        {
            Ledger.Add(new RollLedgerRowViewModel(row, KindLabel(row.Kind)));
        }

        RollLedgerRowViewModel? target = Ledger.FirstOrDefault(row => row.RollId == selectRollId) ?? Ledger.FirstOrDefault();
        if (target is null)
        {
            NewLedgerRoll();
            return;
        }

        this.suppressLedgerSelection = true;
        try
        {
            SelectedLedgerRow = target;
        }
        finally
        {
            this.suppressLedgerSelection = false;
        }

        await ShowLedgerRollAsync(target.RollId, cancellationToken).ConfigureAwait(true);
    }

    private async Task ShowLedgerRollAsync(string rollId, CancellationToken cancellationToken)
    {
        RollRecord? roll = await this.ledgerService.GetAsync(rollId, cancellationToken).ConfigureAwait(true);
        if (roll is null)
        {
            return;
        }

        this.editingRoll = roll;
        IsNewLedgerRoll = false;
        LedgerRollId = roll.RollId;
        LedgerKind = roll.Kind == RollKind.Unspecified ? RollKind.WorkRoll : roll.Kind;
        LedgerBodyLengthText = Format(roll.Geometry.BodyLengthMm, "F0");
        LedgerDiameterText = Format(roll.Geometry.NominalDiameterMm, "F1");
        LedgerCurrentDiameterText = Format(roll.CurrentDiameterMm, "F1");
        LedgerMaterial = roll.Material ?? string.Empty;
        LedgerGrindStartText = Format(roll.Data.GrindStartPositionMm, "F1");
        LedgerCurveLengthText = Format(roll.Data.CurveLengthMm, "F1");
        LedgerCurveToleranceText = Format(roll.Data.CurveToleranceMicrometer, "F1");
        LedgerNetWeightText = Format(roll.Data.NetWeightKg, "F0");
        LedgerHeadBoxWeightText = Format(roll.Data.HeadBoxWeightKg, "F0");
        LedgerTailBoxWeightText = Format(roll.Data.TailBoxWeightKg, "F0");
        LedgerProblems.Clear();

        LedgerHistory.Clear();
        foreach (GrindingRecord record in await this.ledgerService.HistoryAsync(rollId, HistoryLimit, cancellationToken).ConfigureAwait(true))
        {
            LedgerHistory.Add(new LedgerHistoryRowViewModel(
                record.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                record.JobId,
                Localizer["JobState_" + record.State],
                record.FinishedAtUtc is DateTimeOffset finished
                    ? (finished - record.StartedAtUtc).TotalMinutes.ToString("F0", CultureInfo.CurrentCulture) + " min"
                    : "--"));
        }
    }

    /// <summary>表里的内容读成一支辊。长度与公称直径必填；其余没填就是"没登记"。有格子不是数返回 null。</summary>
    private RollRecord? ReadLedgerForm()
    {
        if (!TryRequired(LedgerBodyLengthText, out double length)
            || !TryRequired(LedgerDiameterText, out double diameter)
            || length <= 0.0 || diameter <= 0.0
            || !TryOptional(LedgerCurrentDiameterText, out double? current)
            || !TryOptional(LedgerGrindStartText, out double? grindStart)
            || !TryOptional(LedgerCurveLengthText, out double? curveLength)
            || !TryOptional(LedgerCurveToleranceText, out double? curveTolerance)
            || !TryOptional(LedgerNetWeightText, out double? net)
            || !TryOptional(LedgerHeadBoxWeightText, out double? head)
            || !TryOptional(LedgerTailBoxWeightText, out double? tail))
        {
            return null;
        }

        string rollId = (LedgerRollId ?? string.Empty).Trim();
        RollRecord basis = this.editingRoll is not null && !IsNewLedgerRoll
            ? this.editingRoll
            : new RollRecord(rollId, rollId, RollGeometry.FromDiameter(length, diameter), null, DateTimeOffset.UtcNow);

        return basis with
        {
            Geometry = RollGeometry.FromDiameter(length, diameter),
            Material = string.IsNullOrWhiteSpace(LedgerMaterial) ? null : LedgerMaterial.Trim(),
            Kind = LedgerKind,
            CurrentDiameterMm = current,
            Data = new RollDataSheet(grindStart, curveLength, curveTolerance, net, head, tail),
        };
    }

    private string DescribeLedgerProblem(RollLedgerProblem problem) => problem switch
    {
        RollLedgerProblem.MissingRollId => Localizer["Ledger_Problem_MissingRollId"],
        RollLedgerProblem.RollIdTaken => Localizer["Ledger_Problem_RollIdTaken"],
        RollLedgerProblem.BodyLengthOutOfRange => Localizer["Ledger_Problem_BodyLength"],
        RollLedgerProblem.DiameterOutOfRange => Localizer["Ledger_Problem_Diameter"],
        RollLedgerProblem.CurrentDiameterOutOfRange => Localizer["Ledger_Problem_CurrentDiameter"],
        _ => Localizer["Ledger_Problem_NegativeWeight"],
    };

    private string KindLabel(RollKind kind) => kind switch
    {
        RollKind.WorkRoll => Localizer["RollKind_WorkRoll"],
        RollKind.BackupRoll => Localizer["RollKind_BackupRoll"],
        _ => "--",
    };

    private static string Format(double? value, string format) =>
        value is double number ? number.ToString(format, CultureInfo.CurrentCulture) : string.Empty;

    private static bool TryRequired(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryOptional(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (TryRequired(text, out double parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
