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
public sealed record HmiSettings(
    int SchemaVersion,
    string Culture,
    int PollIntervalMs,
    int UiRefreshHz,
    int ProfileSampleCount,
    int ChartHistorySeconds,
    int RecordRetentionDays,
    int AlarmHistoryLimit);
