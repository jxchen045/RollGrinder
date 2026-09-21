namespace RollGrinder.App.Navigation;

/// <summary>七个主界面。底部功能条按当前页给出不同的 8 个键。</summary>
public enum PageKey
{
    /// <summary>自动磨削（主界面）。</summary>
    AutoGrinding = 0,

    /// <summary>辊形编辑。</summary>
    Profile = 1,

    /// <summary>工序编程。</summary>
    Steps = 2,

    /// <summary>磨削记录。</summary>
    Records = 3,

    /// <summary>手动与辅助操作。</summary>
    Manual = 4,

    /// <summary>诊断。</summary>
    Diagnostics = 5,

    /// <summary>设置：现场标定值。</summary>
    Settings = 6,
}
