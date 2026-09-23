using Microsoft.Win32;

namespace RollGrinder.App.Interaction;

/// <summary>选文件。返回 null 表示操作员取消了。</summary>
public interface IFileDialogs
{
    /// <summary>选一个要读的文件。</summary>
    /// <param name="defaultExtension">默认扩展名，如 ".csv"。</param>
    /// <param name="filter">过滤器，如 "CSV|*.csv"。</param>
    string? PickOpenPath(string defaultExtension, string filter);

    /// <summary>选一个要写的文件。</summary>
    /// <param name="suggestedFileName">建议文件名（含扩展名）。</param>
    /// <param name="defaultExtension">默认扩展名。</param>
    /// <param name="filter">过滤器。</param>
    string? PickSavePath(string suggestedFileName, string defaultExtension, string filter);
}

/// <summary>Windows 标准文件对话框。</summary>
public sealed class WindowsFileDialogs : IFileDialogs
{
    public string? PickOpenPath(string defaultExtension, string filter)
    {
        var dialog = new OpenFileDialog { DefaultExt = defaultExtension, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSavePath(string suggestedFileName, string defaultExtension, string filter)
    {
        var dialog = new SaveFileDialog { FileName = suggestedFileName, DefaultExt = defaultExtension, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
