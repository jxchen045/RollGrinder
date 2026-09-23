using System.Windows.Controls;
using System.Windows.Documents;

namespace RollGrinder.App.Interaction;

/// <summary>把一份排好版的文档输出出去（打印）。</summary>
public interface IDocumentOutput
{
    /// <summary>输出文档。</summary>
    /// <param name="document">文档。会按输出目标的可打印区域重新分页。</param>
    /// <param name="description">打印队列里显示的名字。</param>
    /// <param name="askOperator">true：先让操作员选打印机（可取消）；false：直接用系统默认打印机。</param>
    /// <returns>真的输出了返回 true；操作员取消返回 false。打印机故障照常抛异常，由调用方转报警。</returns>
    bool Print(FlowDocument document, string description, bool askOperator);
}

/// <summary>打到打印机上。</summary>
public sealed class PrinterDocumentOutput : IDocumentOutput
{
    public bool Print(FlowDocument document, string description, bool askOperator)
    {
        // 不 ShowDialog 的 PrintDialog 用的就是系统默认打印机。
        var dialog = new PrintDialog();
        if (askOperator && dialog.ShowDialog() != true)
        {
            return false;
        }

        // 按所选打印机的可打印区域重新分页：换一台纸张不同的打印机也不会切掉边。
        document.PageHeight = dialog.PrintableAreaHeight;
        document.PageWidth = dialog.PrintableAreaWidth;

        IDocumentPaginatorSource paginator = document;
        dialog.PrintDocument(paginator.DocumentPaginator, description);
        return true;
    }
}
