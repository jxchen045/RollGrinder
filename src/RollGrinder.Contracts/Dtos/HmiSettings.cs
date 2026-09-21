namespace RollGrinder.Contracts.Dtos;

/// <summary>
/// 上位机自身的设置（hmi.json）：与机床无关，只影响界面与记录行为。
/// </summary>
/// <param name="SchemaVersion">配置结构版本。</param>
/// <param name="Culture">界面语言，例如 zh-CN。</param>
/// <param name="PollIntervalMs">向机床取数的周期（ms）。</param>
/// <param name="UiRefreshHz">界面刷新频率（Hz，5–10）。</param>
/// <param name="ProfileSampleCount">辊形曲线的采样点数。</param>
/// <param name="ChartHistorySeconds">趋势图保留的时长（s）。</param>
/// <param name="RecordRetentionDays">磨削记录保留天数。</param>
/// <param name="AlarmHistoryLimit">报警条目上限。</param>
/// <param name="CompensationGain">补偿增益（0–1）：一次吸收多少比例的偏差。</param>
/// <param name="CompensationSmoothingPoints">补偿平滑窗口点数（奇数）。</param>
/// <param name="ProfileToleranceDiameterMicrometer">辊形验收公差（直径量 µm）。</param>
/// <param name="DefaultRole">用户管理里新建账号时预选的权限。启动权限由登录决定，不看这一项。</param>
/// <param name="ManualPulseMs">手动动作脉冲命令的脉宽（ms）。</param>
public sealed record HmiSettings(
    int SchemaVersion,
    string Culture,
    int PollIntervalMs,
    int UiRefreshHz,
    int ProfileSampleCount,
    int ChartHistorySeconds,
    int RecordRetentionDays,
    int AlarmHistoryLimit,
    double CompensationGain,
    int CompensationSmoothingPoints,
    double ProfileToleranceDiameterMicrometer,
    UserRole DefaultRole,
    int ManualPulseMs = 300);
