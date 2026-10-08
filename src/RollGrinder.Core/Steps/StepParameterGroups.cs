using System;
using System.Collections.Generic;

namespace RollGrinder.Core.Steps;

/// <summary>自动页参数对照表里的一组。顺序就是显示顺序。</summary>
public enum StepParameterGroup
{
    /// <summary>速度：拖板速度、头架转速、砂轮线速度……</summary>
    Speed = 0,

    /// <summary>进给：周期进给、连续进给。</summary>
    Infeed = 1,

    /// <summary>道次与去除：磨削道次、磨削量、光磨道次。</summary>
    Removal = 2,

    /// <summary>其他：折返时间、变速、在线测量，以及辅助工序自己的参数。</summary>
    Other = 3,
}

/// <summary>
/// 自动页参数区（方案 F）的分组与分栏。
///
/// 加工中操作工看参数是按"多快 → 吃多深 → 还要几道"这个顺序判断的，
/// 所以不按 schema 的原始顺序平铺，而是分成四组、固定顺序，组内仍按矩阵的行序。
/// 变速的三个参数（模式 / 幅度 / 周期）合成一行看——单独拆开占三行，又看不出一件事。
/// </summary>
public static class StepParameterGroups
{
    /// <summary>合成"变速"一行的三个参数，按显示顺序。</summary>
    public static IReadOnlyList<string> SpeedVariationKeys { get; } = new[]
    {
        StepParameterKeys.SpeedVariationTarget,
        StepParameterKeys.SpeedVariationPercent,
        StepParameterKeys.SpeedVariationPeriodRevolutions,
    };

    /// <summary>参数属于哪一组；不认识的都归"其他"。</summary>
    public static StepParameterGroup GroupOf(string parameterKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameterKey);

        return parameterKey switch
        {
            StepParameterKeys.FeedMmPerMin
                or StepParameterKeys.WorkpieceSpeedRpm
                or StepParameterKeys.WheelSpeedRpm
                or StepParameterKeys.WheelSurfaceSpeedMPerSec
                or StepParameterKeys.DressFeedMmPerMin => StepParameterGroup.Speed,
            StepParameterKeys.InfeedPerPassDiameterMicrometer
                or StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin
                or StepParameterKeys.DressInfeedRadiusMicrometer => StepParameterGroup.Infeed,
            StepParameterKeys.PassCount
                or StepParameterKeys.StockDiameterMicrometer
                or StepParameterKeys.SparkOutPassCount
                or StepParameterKeys.DressPassCount => StepParameterGroup.Removal,
            _ => StepParameterGroup.Other,
        };
    }

    /// <summary>
    /// 把按顺序排好的若干组分到左右两栏：前 k 组放左栏，其余放右栏，组不拆开。
    /// 组名竖排在组左边一窄列、不占行，挑两栏里较高那一栏最矮的 k；一样高时左栏多放。
    /// </summary>
    /// <param name="groupRowCounts">每组的参数行数，按显示顺序。</param>
    /// <returns>放进左栏的组数。</returns>
    public static int SplitIndex(IReadOnlyList<int> groupRowCounts)
    {
        ArgumentNullException.ThrowIfNull(groupRowCounts);

        int total = 0;
        foreach (int rows in groupRowCounts)
        {
            total += rows;
        }

        int best = groupRowCounts.Count;
        int bestHeight = total;
        int left = 0;
        for (int k = 0; k <= groupRowCounts.Count; k++)
        {
            int height = Math.Max(left, total - left);
            if (height < bestHeight || (height == bestHeight && k > best))
            {
                best = k;
                bestHeight = height;
            }

            if (k < groupRowCounts.Count)
            {
                left += groupRowCounts[k];
            }
        }

        return best;
    }
}
