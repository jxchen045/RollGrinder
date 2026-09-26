using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;

namespace RollGrinder.App.ViewModels;

/// <summary>命名框里按"确定"或"覆盖"之后的结果。</summary>
/// <param name="IsDone">存成了，关框。</param>
/// <param name="Message">没存成的原因，显示在框里。</param>
/// <param name="CanOverwrite">原因是名字被库里另一条占了：给出"覆盖"按钮。</param>
public sealed record NamePromptOutcome(bool IsDone, string? Message, bool CanOverwrite)
{
    public static NamePromptOutcome Done { get; } = new(true, null, false);

    public static NamePromptOutcome Refused(string message) => new(false, message, false);

    public static NamePromptOutcome Conflict(string message) => new(false, message, true);
}

/// <summary>
/// 起名字的框：盖在页面上，一个输入框加"确定 / 覆盖 / 取消"。
///
/// 第一轮甲方测试：另存为不问名字，直接复制出一条同名的，库里于是有两条"工作辊-凸度300"，
/// 分不清哪条是哪条。现在另存为一定先起名字；名字在库里已经有了，框里说清楚，
/// 由操作员选"覆盖"那一条、改个名字，或者取消。
///
/// 谁用谁给一个存的动作：参数是名字和"是否覆盖同名的那一条"。
/// </summary>
public sealed partial class NamePromptViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;
    private Func<string, bool, CancellationToken, Task<NamePromptOutcome>>? store;

    public NamePromptViewModel(IStringLocalizer localizer)
    {
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    [ObservableProperty]
    private bool isOpen;

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string name = string.Empty;

    /// <summary>上一次没存成的原因；空串表示没有。</summary>
    [ObservableProperty]
    private string errorText = string.Empty;

    /// <summary>名字被库里另一条占了：显示"覆盖"。改了名字就收回去。</summary>
    [ObservableProperty]
    private bool canOverwrite;

    partial void OnNameChanged(string value)
    {
        ErrorText = string.Empty;
        CanOverwrite = false;
    }

    /// <summary>打开命名框。</summary>
    /// <param name="title">框的标题（已本地化）。</param>
    /// <param name="initialName">预填的名字，打开时整段选中，直接打字就替换掉。</param>
    /// <param name="onStore">存的动作：名字、是否覆盖同名的那一条。</param>
    /// <param name="conflictMessage">打开时就已经知道重名（按"保存"撞了名）：直接给出原因和"覆盖"。</param>
    public void Open(
        string title,
        string initialName,
        Func<string, bool, CancellationToken, Task<NamePromptOutcome>> onStore,
        string? conflictMessage = null)
    {
        this.store = onStore ?? throw new ArgumentNullException(nameof(onStore));
        Title = title ?? string.Empty;
        Name = initialName ?? string.Empty;
        ErrorText = conflictMessage ?? string.Empty;
        CanOverwrite = conflictMessage is not null;
        IsOpen = true;
    }

    [RelayCommand]
    private Task ConfirmAsync(CancellationToken cancellationToken) => StoreAsync(overwrite: false, cancellationToken);

    [RelayCommand]
    private Task OverwriteAsync(CancellationToken cancellationToken) =>
        CanOverwrite ? StoreAsync(overwrite: true, cancellationToken) : Task.CompletedTask;

    [RelayCommand]
    private void Cancel()
    {
        IsOpen = false;
        this.store = null;
        ErrorText = string.Empty;
        CanOverwrite = false;
    }

    private async Task StoreAsync(bool overwrite, CancellationToken cancellationToken)
    {
        if (!IsOpen || this.store is null)
        {
            return;
        }

        string trimmed = (Name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            ErrorText = this.localizer["NamePrompt_Empty"];
            return;
        }

        NamePromptOutcome outcome = await this.store(trimmed, overwrite, cancellationToken).ConfigureAwait(true);
        if (outcome.IsDone)
        {
            IsOpen = false;
            this.store = null;
            return;
        }

        ErrorText = outcome.Message ?? string.Empty;
        CanOverwrite = outcome.CanOverwrite;
    }
}
