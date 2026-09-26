namespace RollGrinder.App.ViewModels;

/// <summary>
/// 几页之间递的"便条"：作业是在作业页上一步步拼出来的，但有两样东西得去别的页取——
/// 从工艺程序页按"用于作业"带过来的程序，和去轧辊台账新登记的那支辊。
/// 递过去的一方写，作业页（或台账）进来时读了就清掉。不存盘：上位机重启就是一张空作业。
/// </summary>
public sealed class JobDraft
{
    /// <summary>工艺程序页按"用于作业"时，要带进作业的程序。</summary>
    public string? PendingProgramId { get; set; }

    /// <summary>作业页按"新登记轧辊"：台账页进来时直接开一张新表。</summary>
    public bool RegisterNewRollRequested { get; set; }

    /// <summary>应作业页要求刚在台账登记的那支辊；回到作业页时直接选上它。</summary>
    public string? RegisteredRollId { get; set; }
}
