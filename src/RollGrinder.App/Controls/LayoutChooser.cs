namespace RollGrinder.App.Controls;

/// <summary>版面档位（分辨率适配方案第 3 节）。</summary>
public enum LayoutKind
{
    /// <summary>1920×1080，19″ IFP1900。</summary>
    Standard = 0,

    /// <summary>1366×768，15″ IPC477E。</summary>
    Compact = 1,
}

/// <summary>选档规则（不依赖 WPF，单测直接链接）。</summary>
public static class LayoutChooser
{
    /// <summary>选档：配置写死就照配置；auto 时主屏（DIP）宽 ≥ 1800 且高 ≥ 1000 为标准档，否则紧凑档。</summary>
    public static LayoutKind Choose(string? setting, double screenWidth, double screenHeight) => setting switch
    {
        "standard" => LayoutKind.Standard,
        "compact" => LayoutKind.Compact,
        _ => screenWidth >= 1800 && screenHeight >= 1000 ? LayoutKind.Standard : LayoutKind.Compact,
    };
}
