using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 轧辊数据表里的一格。
///
/// 不走 <see cref="ParameterRowViewModel"/>：那一套是给参数模式驱动的工艺参数用的，
/// 有单位、有上下限、有选项。轧辊数据是一支辊的登记项，**空着就是没登记**，
/// 没有默认值也没有校验——逼着填只会让人乱填一个数。
/// </summary>
public sealed partial class RollDataRowViewModel : ObservableObject
{
    public RollDataRowViewModel(string key, string label, string text)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Label = label ?? throw new ArgumentNullException(nameof(label));
        this.text = text ?? string.Empty;
    }

    public string Key { get; }

    public string Label { get; }

    [ObservableProperty]
    private string text;
}
