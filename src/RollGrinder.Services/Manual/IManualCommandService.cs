using System.Threading;
using System.Threading.Tasks;

namespace RollGrinder.Services.Manual;

/// <summary>一次手动动作的结果。界面按 <see cref="ReasonResourceKey"/> 取文案，服务层不产出界面文字。</summary>
/// <param name="Outcome">结果。</param>
/// <param name="ReasonResourceKey">失败原因的资源键；成功时为 null。</param>
public sealed record ManualCommandResult(ManualCommandOutcome Outcome, string? ReasonResourceKey = null)
{
    public bool Succeeded => Outcome == ManualCommandOutcome.Sent;

    public static ManualCommandResult Sent { get; } = new(ManualCommandOutcome.Sent);
}

/// <summary>手动动作的结果分类。</summary>
public enum ManualCommandOutcome
{
    /// <summary>命令已经发下去了。</summary>
    Sent = 0,

    /// <summary>tagmap 里没有登记这个动作的命令位。</summary>
    NotMapped = 1,

    /// <summary>自动循环还挂着程序，这个动作现在不该按。</summary>
    ChannelBusy = 2,

    /// <summary>没连上机床。</summary>
    Disconnected = 3,

    /// <summary>写机床失败。</summary>
    WriteFailed = 4,
}

/// <summary>
/// 手动页按钮矩阵的执行入口。界面不碰网关，一律经这里（架构约束 ②⑦）。
/// 这里发出去的都是**离散命令**，发完就结束，不构成实时控制回路。
/// </summary>
public interface IManualCommandService
{
    /// <summary>这个动作在本台机床的 tagmap 里有没有登记。没有就把按钮压暗，而不是按下去没反应。</summary>
    bool IsMapped(ManualCommandDescriptor command);

    /// <summary>这个动作现在能不能按（连接、通道状态）。</summary>
    ManualCommandResult CanExecute(ManualCommandDescriptor command);

    /// <summary>
    /// 执行一个动作。
    /// 脉冲型：写 true，等一个脉宽，再写 false。
    /// 保持型：按 <paramref name="desiredState"/> 写；传 null 时按当前回读值取反。
    /// </summary>
    Task<ManualCommandResult> ExecuteAsync(
        ManualCommandDescriptor command,
        bool? desiredState,
        CancellationToken cancellationToken);

    /// <summary>保持型动作的当前状态；拿不到返回 null（界面显示"--"而不是假装关着）。</summary>
    bool? ReadState(ManualCommandDescriptor command);
}
