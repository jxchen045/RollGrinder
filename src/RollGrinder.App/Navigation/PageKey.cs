namespace RollGrinder.App.Navigation;

/// <summary>
/// 一个画面（一个页面视图模型）。画面归属于区域（<see cref="AreaKey"/>），见 <see cref="AreaCatalog"/>。
/// 数值沿用旧版，自检记录与日志里的页号不会因为改版而错位。
/// </summary>
public enum PageKey
{
    /// <summary>自动磨削：机床区 AUTO 方式的基本画面。</summary>
    AutoGrinding = 0,

    /// <summary>辊形编辑（辊形区）。</summary>
    Profile = 1,

    /// <summary>工艺程序（工艺区）。</summary>
    Steps = 2,

    /// <summary>磨削记录（记录区）。</summary>
    Records = 3,

    /// <summary>手动动作页：测量臂、尾架、头架拨盘、托瓦、测量对中（机床区 JOG 横键）。</summary>
    Manual = 4,

    /// <summary>诊断：报警、变量监视、改动记录、运行日志、连接与接口、备份与恢复。</summary>
    Diagnostics = 5,

    /// <summary>参数：砂轮、标定、标定审计（原"设置"）。</summary>
    Parameters = 6,

    /// <summary>作业（机床区横键"作业"，第 1 格）：待磨清单 → 一页核对 → 下发。</summary>
    Job = 7,

    /// <summary>手动磨削：机床区 JOG 方式的基本画面（位置、测量、砂轮、头架、拖板）。</summary>
    ManualGrinding = 8,

    /// <summary>轧辊：台账（计划、寿命、履历）、新登记、多选改计划、导入 / 导出。原"库"区的位置。</summary>
    Rolls = 9,

    /// <summary>调试：机床配置、标签映射、系统（只从区域菜单进，制造商）。</summary>
    Commissioning = 10,

    /// <summary>辊形库（辊形区横键"辊形库"）：版本、在用清单、停用。</summary>
    ProfileLibrary = 11,

    /// <summary>程序库（工艺区横键"程序库"）：同上。</summary>
    ProgramLibrary = 12,
}

/// <summary>
/// 操作区域（最终稿 D1）：8 个，对应区域菜单的 8 个横键（F10 或右上区域方块打开）。
/// 区域之间是平的；一个区域里可以有几个画面（机床区：手动磨削、自动磨削、手动动作页、作业）。
/// </summary>
public enum AreaKey
{
    /// <summary>机床：随 NC 方式显示手动磨削（JOG）或自动磨削（AUTO）。</summary>
    Machine = 0,

    /// <summary>辊形。</summary>
    Profile = 1,

    /// <summary>工艺程序。</summary>
    Steps = 2,

    /// <summary>轧辊（以轧辊为中心：台账、计划、导入导出）。原"库"区的位置。</summary>
    Rolls = 3,

    /// <summary>参数。</summary>
    Parameters = 4,

    /// <summary>记录。</summary>
    Records = 5,

    /// <summary>诊断。</summary>
    Diagnostics = 6,

    /// <summary>调试（只在区域菜单里，要制造商权限）。</summary>
    Commissioning = 7,
}

/// <summary>NC 操作方式（machine.operatingMode：0 JOG / 1 MDA / 2 AUTO，与 SINUMERIK 的 opMode 取值一致）。</summary>
public enum MachineMode
{
    /// <summary>读不到。</summary>
    Unknown = -1,

    /// <summary>手动。</summary>
    Jog = 0,

    /// <summary>MDA（本上位机不提供 MDA 画面，最终稿 M2；按手动处理）。</summary>
    Mda = 1,

    /// <summary>自动。</summary>
    Auto = 2,
}
