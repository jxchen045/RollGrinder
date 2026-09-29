using System;
using System.Collections.Generic;

namespace RollGrinder.App.Controls;

/// <summary>
/// 曲线上的超差段（最终稿 D4：误差曲线超差段加粗）。纯逻辑，单测覆盖。
/// </summary>
public static class CurveMath
{
    /// <summary>
    /// 找出曲线越出 ±<paramref name="tolerance"/> 的连续段。每段带上左右相邻的一个点，
    /// 画出来的粗线从穿出公差带的那一小段开始，不会在点与点之间断开。
    /// </summary>
    /// <param name="positions">横坐标（按递增排好）。</param>
    /// <param name="values">纵坐标。</param>
    /// <param name="tolerance">公差带半宽（≥ 0）。</param>
    /// <returns>每段的起止下标（含）。</returns>
    public static IReadOnlyList<(int First, int Last)> ExcessRuns(
        IReadOnlyList<double> positions, IReadOnlyList<double> values, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(values);
        if (positions.Count != values.Count)
        {
            throw new ArgumentException("Positions and values must have the same length.", nameof(values));
        }

        if (tolerance < 0 || double.IsNaN(tolerance))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }

        var runs = new List<(int, int)>();
        int start = -1;
        for (int i = 0; i < values.Count; i++)
        {
            bool outside = Math.Abs(values[i]) > tolerance;
            if (outside && start < 0)
            {
                start = i;
            }
            else if (!outside && start >= 0)
            {
                runs.Add((Math.Max(0, start - 1), i));
                start = -1;
            }
        }

        if (start >= 0)
        {
            runs.Add((Math.Max(0, start - 1), values.Count - 1));
        }

        return runs;
    }
}
