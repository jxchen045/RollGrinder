namespace RollGrinder.App.Navigation;

/// <summary>七个主界面（区域），外加只能被派去的任务页（作业）。底部功能条按当前页给出不同的 8 个键。</summary>
public enum PageKey
{
    /// <summary>自动磨削（主界面）。</summary>
    AutoGrinding = 0,

    /// <summary>辊形编辑。</summary>
    Profile = 1,

    /// <summary>工艺程序（原"工序编程"）。</summary>
    Steps = 2,

    /// <summary>磨削记录。</summary>
    Records = 3,

    /// <summary>手动与辅助操作。</summary>
    Manual = 4,

    /// <summary>诊断。</summary>
    Diagnostics = 5,

    /// <summary>设置：现场标定值。</summary>
    Settings = 6,

    /// <summary>
    /// 作业（阶段 1）：选轧辊、辊形、工艺程序，核对后下发。不在页面菜单里——
    /// 它是从自动加工页或工艺程序页"派"过去的任务页，导航槽写着回哪儿。
    /// </summary>
    Job = 7,
}
