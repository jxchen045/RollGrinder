using System.Windows.Controls;

namespace RollGrinder.App.Views;

/// <summary>
/// 工艺程序页。选工序不再用下拉：右侧竖向软键"插入工序 ▸"按类别开子菜单（修改稿原则 1）。
/// </summary>
public partial class StepsView : UserControl
{
    public StepsView()
    {
        InitializeComponent();
    }
}
