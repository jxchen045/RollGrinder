namespace RollGrinder.Core;

/// <summary>轧辊类型。台账登记一支辊的类型；程序可写"适用类型"，作业核对时两者要对得上。</summary>
public enum RollKind
{
    /// <summary>没登记（程序上表示"不限"）。</summary>
    Unspecified = 0,

    /// <summary>工作辊。</summary>
    WorkRoll = 1,

    /// <summary>支承辊。</summary>
    BackupRoll = 2,
}
