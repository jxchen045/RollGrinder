using System;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Services.NcInterface;

/// <summary>
/// 诊断 › 连接与接口的"NC 接口"一块（界面设计稿 v4 5.9.2）：
/// 上位机与 NC 两边的序号，一眼看出握手是否跟上。全部来自监视快照，不额外读机床。
/// </summary>
/// <param name="DownloadSeq">上位机最后一次的下发序号；未映射为空。</param>
/// <param name="AcceptedSeq">NC 锁存的下发序号；未映射为空。</param>
/// <param name="ParametersValid">参数有效标志。</param>
/// <param name="StagedEdits">改参数是否走暂存区握手（tagmap 有映射）。</param>
/// <param name="EditSeq">上位机的改参数序号。</param>
/// <param name="EditAppliedSeq">NC 已采用的改参数序号。</param>
/// <param name="Pass">道次缓冲进度。</param>
/// <param name="PassDataStale">看门狗：加工中、但道次数据长时间没更新（只提示）。</param>
public sealed record NcInterfaceStatus(
    int? DownloadSeq,
    int? AcceptedSeq,
    bool? ParametersValid,
    bool StagedEdits,
    int? EditSeq,
    int? EditAppliedSeq,
    PassBufferStatus Pass,
    bool PassDataStale)
{
    /// <summary>NC 正在用的就是上位机最后下发的那一份。</summary>
    public bool DownloadConfirmed => DownloadSeq is int sent && AcceptedSeq == sent;

    /// <summary>上一次改参数已写入暂存区、NC 还没采用（⧗ 等待生效）。</summary>
    public bool EditPending => EditSeq is int sent && EditAppliedSeq is int applied && sent != applied;

    /// <summary>从一份监视快照拼出接口状态。</summary>
    public static NcInterfaceStatus From(
        MachineStateSnapshot snapshot, IPassDataCollector passData, bool stagedEdits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(passData);

        int? Int(string key) => snapshot.GetNumberOrNull(key) is double value ? (int)value : null;

        return new NcInterfaceStatus(
            Int(MachineTagKeys.JobDownloadSeq),
            Int(MachineTagKeys.JobAcceptedSeq),
            snapshot.GetBooleanOrNull(MachineTagKeys.JobParametersValid),
            stagedEdits,
            Int(MachineTagKeys.JobEditSeq),
            Int(MachineTagKeys.JobEditAppliedSeq),
            passData.Status,
            passData.IsStale(now));
    }
}
