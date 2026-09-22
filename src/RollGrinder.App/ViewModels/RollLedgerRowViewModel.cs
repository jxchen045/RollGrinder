using System;
using System.Globalization;
using RollGrinder.Services.Records;

namespace RollGrinder.App.ViewModels;

/// <summary>轧辊台账里的一行。</summary>
public sealed class RollLedgerRowViewModel
{
    public RollLedgerRowViewModel(RollLedgerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
    }

    public RollLedgerRow Row { get; }

    public string Code => Row.Code;

    public string DiameterText => Row.NominalDiameterMm.ToString("F1", CultureInfo.CurrentCulture);

    public string BodyLengthText => Row.BodyLengthMm.ToString("F0", CultureInfo.CurrentCulture);

    public string MaterialText => Row.Material ?? string.Empty;

    public string GrindCountText => Row.GrindCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>一次都没磨过时写 "--"，不写一个假的日期。</summary>
    public string LastGroundText => Row.LastGroundAtUtc is DateTimeOffset at
        ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
        : "--";
}
