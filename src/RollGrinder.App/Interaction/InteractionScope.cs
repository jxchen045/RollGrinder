using System;

namespace RollGrinder.App.Interaction;

/// <summary>
/// 视图代码里需要"问操作员"或"往外输出"的地方——选文件、打印——统一从这里取实现。
///
/// 视图由 DataTemplate 创建，走不了构造函数注入，所以和 <c>LocalizationScope</c> 一样是个静态入口。
/// 平时就是 Windows 的标准对话框与打印机；界面自检（--selftest）时换成自动应答：
/// 给临时路径、把打印写成 XPS 文件——既不会卡在模态对话框上，也不会真的出纸。
/// </summary>
public static class InteractionScope
{
    private static IFileDialogs fileDialogs = new WindowsFileDialogs();
    private static IDocumentOutput documentOutput = new PrinterDocumentOutput();

    /// <summary>选文件。</summary>
    public static IFileDialogs FileDialogs => fileDialogs;

    /// <summary>打印。</summary>
    public static IDocumentOutput DocumentOutput => documentOutput;

    /// <summary>换掉实现（只有组合根与界面自检会调）。</summary>
    public static void SetCurrent(IFileDialogs dialogs, IDocumentOutput output)
    {
        fileDialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        documentOutput = output ?? throw new ArgumentNullException(nameof(output));
    }
}
