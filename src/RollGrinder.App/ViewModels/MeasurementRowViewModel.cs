using System;
using System.Globalization;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Units;

namespace RollGrinder.App.ViewModels;

/// <summary>测点列表里的一行；界面按辊身坐标与直径量显示。</summary>
public sealed class MeasurementRowViewModel
{
    public MeasurementRowViewModel(MeasurementPoint point)
    {
        Point = point ?? throw new ArgumentNullException(nameof(point));
    }

    public MeasurementPoint Point { get; }

    public string BodyPositionText => Point.BodyPositionMm.ToString("F1", CultureInfo.CurrentCulture);

    public string DiameterText =>
        UnitConversion.RadiusMmToDiameterMm(Point.MeasuredRadiusMm).ToString("F4", CultureInfo.CurrentCulture);
}
