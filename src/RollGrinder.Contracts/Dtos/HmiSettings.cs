namespace RollGrinder.Contracts.Dtos;

/// <summary>
/// 上位机自身的设置（hmi.json）：与机床无关，只影响界面与记录行为。
///
/// 这里**不放现场标定值**（砂轮直径、探头距离、各项验收公差……）——
/// 那些换一次砂轮就变，要在设置页上按权限改、要记谁改的，所以存数据库，
/// 见 <c>RollGrinder.Core.Calibration.MachineCalibration</c>。
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
/// <param name="DefaultRole">用户管理里新建账号时预选的权限。启动权限由登录决定，不看这一项。</param>
/// <param name="ManualPulseMs">手动动作脉冲命令的脉宽（ms）。</param>
/// <param name="Layout">版面档位：auto（按屏幕选）、standard（1920×1080）、compact（1366×768）。</param>
/// <param name="FullScreen">全屏、盖住任务栏（现场 kiosk）；调试时可关。</param>
/// <param name="PlanChangePermission">
/// 作业里"变更这支辊的工艺"谁能做（关系设计 M2）：operator（默认，操作者即可）或 administrator（只有管理员及以上）。
/// </param>
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
    UserRole DefaultRole,
    int ManualPulseMs = 300,
    string Layout = "auto",
    bool FullScreen = true,
    string PlanChangePermission = "operator")
{
    /// <summary>变更计划只许管理员及以上。</summary>
    public bool PlanChangeNeedsAdministrator =>
        string.Equals(PlanChangePermission, "administrator", System.StringComparison.OrdinalIgnoreCase);
}
