namespace RollGrinder.App.ViewModels;

/// <summary>
/// 几页之间递的"便条"：作业页派去轧辊区新登记的那支辊，以及从作业核对或库"打开"到辊形区、工艺区去改的那一条。
/// 递过去的一方写，接收的一页进来时读了就清掉。不存盘：上位机重启就是一张空作业。
/// </summary>
public sealed class JobDraft
{
    /// <summary>作业页按"新登记轧辊"：台账页进来时直接开一张新表。</summary>
    public bool RegisterNewRollRequested { get; set; }

    /// <summary>作业页里敲了一个台账里没有的辊号：带到登记表里预填。</summary>
    public string? RegisterRollIdHint { get; set; }

    /// <summary>应作业页要求刚在台账登记的那支辊；回到作业页时直接选上它。</summary>
    public string? RegisteredRollId { get; set; }

    /// <summary>要在辊形区打开来改的辊形（作业向导、库的"打开"）。</summary>
    public string? ProfileToOpen { get; set; }

    /// <summary>要在工艺区打开来改的程序（作业向导、库的"打开"）。</summary>
    public string? ProgramToOpen { get; set; }

    /// <summary>磨削记录进来时要选中的那份作业（库 › 作业"打开"、自动页"磨削记录（本支辊）"）。</summary>
    public string? RecordsJobId { get; set; }

    /// <summary><see cref="ProfileToOpen"/> / <see cref="ProgramToOpen"/> 取这个值表示"开一张新的"（库的"新建"）。</summary>
    public const string NewEntry = "*new*";
}
