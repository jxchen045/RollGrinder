using System;
using System.Globalization;
using RollGrinder.Services.Records;

namespace RollGrinder.App.ViewModels;

/// <summary>轧辊台账列表里的一行。</summary>
public sealed class RollLedgerRowViewModel
{
    public RollLedgerRowViewModel(RollLedgerRow row, string kindText)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        KindText = kindText ?? string.Empty;
    }

    public RollLedgerRow Row { get; }

    /// <summary>辊号：作业里选辊、记录里查辊都认它。</summary>
    public string RollId => Row.RollId;

    public string KindText { get; }

    public string DiameterText => Row.NominalDiameterMm.ToString("F1", CultureInfo.CurrentCulture);

    /// <summary>当前直径；没登记时写 "--"，不写公称直径冒充。</summary>
    public string CurrentDiameterText => Row.CurrentDiameterMm is double current
        ? current.ToString("F1", CultureInfo.CurrentCulture)
        : "--";

    public string BodyLengthText => Row.BodyLengthMm.ToString("F0", CultureInfo.CurrentCulture);

    public string MaterialText => Row.Material ?? string.Empty;

    public string GrindCountText => Row.GrindCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>一次都没磨过时写 "--"，不写一个假的日期。</summary>
    public string LastGroundText => Row.LastGroundAtUtc is DateTimeOffset at
        ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
        : "--";
}

/// <summary>磨削履历里的一行。</summary>
/// <param name="StartedText">开始时间（本地）。</param>
/// <param name="JobId">作业号。</param>
/// <param name="StateText">状态。</param>
/// <param name="DurationText">用时；没收尾时为 "--"。</param>
public sealed record LedgerHistoryRowViewModel(string StartedText, string JobId, string StateText, string DurationText);
