using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Documents;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using RollGrinder.App.Interaction;

namespace RollGrinder.App.SelfTest;

/// <summary>
/// 自检时替换掉文件对话框与打印机：
/// - 另存为：直接给结果目录下的一个路径；
/// - 打开：给用例事先放进 <see cref="OpenAnswers"/> 的路径，没放就当操作员取消；
/// - 打印：按 A4 排版写成 XPS 文件——排版、分页那段代码照样全走一遍，只是不出纸。
/// </summary>
internal sealed class AutoAnswerInteraction : IFileDialogs, IDocumentOutput
{
    /// <summary>A4，单位 1/96 英寸。</summary>
    private const double A4WidthDip = 793.7;
    private const double A4HeightDip = 1122.5;

    private readonly string filesDirectory;
    private readonly string printsDirectory;
    private readonly List<string> produced = new();
    private int printCount;

    public AutoAnswerInteraction(string filesDirectory, string printsDirectory)
    {
        this.filesDirectory = filesDirectory;
        this.printsDirectory = printsDirectory;
        Directory.CreateDirectory(filesDirectory);
        Directory.CreateDirectory(printsDirectory);
    }

    /// <summary>下一次"打开文件"要回答的路径。</summary>
    public Queue<string> OpenAnswers { get; } = new();

    /// <summary>自检过程中产生的所有文件（导出、备份、打印）。</summary>
    public IReadOnlyList<string> Produced => this.produced;

    /// <summary>最近一次产生的文件。</summary>
    public string? LastProduced => this.produced.LastOrDefault();

    public string? PickOpenPath(string defaultExtension, string filter) =>
        OpenAnswers.Count > 0 ? OpenAnswers.Dequeue() : null;

    public string? PickSavePath(string suggestedFileName, string defaultExtension, string filter)
    {
        string path = Unique(Path.Combine(this.filesDirectory, suggestedFileName));
        this.produced.Add(path);
        return path;
    }

    public bool Print(FlowDocument document, string description, bool askOperator)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.printCount++;
        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"{this.printCount:D2}-{(askOperator ? "manual" : "auto")}-{Sanitize(description)}.xps");
        string path = Unique(Path.Combine(this.printsDirectory, name));

        document.PageWidth = A4WidthDip;
        document.PageHeight = A4HeightDip;

        using (var xps = new XpsDocument(path, FileAccess.ReadWrite))
        {
            XpsDocumentWriter writer = XpsDocument.CreateXpsDocumentWriter(xps);
            IDocumentPaginatorSource source = document;
            writer.Write(source.DocumentPaginator);
        }

        this.produced.Add(path);
        return true;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{stem}-{i}{extension}"));
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Sanitize(string text)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(text.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }
}
