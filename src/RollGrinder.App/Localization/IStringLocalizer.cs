namespace RollGrinder.App.Localization;

/// <summary>
/// 界面字符串访问入口。代码与 XAML 中不得出现硬编码界面文案。
/// </summary>
public interface IStringLocalizer
{
    /// <summary>按键取字符串；键不存在时返回 "!键!" 以便在界面上立刻看见遗漏。</summary>
    string this[string key] { get; }

    /// <summary>按键取字符串并做参数格式化。</summary>
    string Format(string key, params object?[] arguments);
}
