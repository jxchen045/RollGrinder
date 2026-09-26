using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// "另存为"的命名框：盖在页面上，一个输入框加"确定 / 取消"。
///
/// 第一轮甲方测试：另存为不问名字，直接复制出一条同名的，库里于是有两条"工作辊-凸度300"，
/// 分不清哪条是哪条。现在另存为一定先起名字，名字在库里已经有了就留在框里说清楚，不存。
///
/// 谁用谁给一个"确定"时要做的事：成功返回 null 关框；不成功返回要显示的原因，框不关。
/// </summary>
public sealed partial class NamePromptViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;
    private Func<string, CancellationToken, Task<string?>>? confirm;

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

    /// <summary>上一次"确定"没成的原因；空串表示没有。</summary>
    [ObservableProperty]
    private string errorText = string.Empty;

    partial void OnNameChanged(string value) => ErrorText = string.Empty;

    /// <summary>打开命名框。</summary>
    /// <param name="title">框的标题（已本地化）。</param>
    /// <param name="initialName">预填的名字，打开时整段选中，直接打字就替换掉。</param>
    /// <param name="onConfirm">按"确定"时做的事：成功返回 null，不成功返回原因。</param>
    public void Open(string title, string initialName, Func<string, CancellationToken, Task<string?>> onConfirm)
    {
        this.confirm = onConfirm ?? throw new ArgumentNullException(nameof(onConfirm));
        Title = title ?? string.Empty;
        Name = initialName ?? string.Empty;
        ErrorText = string.Empty;
        IsOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmAsync(CancellationToken cancellationToken)
    {
        if (!IsOpen || this.confirm is null)
        {
            return;
        }

        string trimmed = (Name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            ErrorText = this.localizer["NamePrompt_Empty"];
            return;
        }

        string? error = await this.confirm(trimmed, cancellationToken).ConfigureAwait(true);
        if (error is null)
        {
            IsOpen = false;
            this.confirm = null;
        }
        else
        {
            ErrorText = error;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        IsOpen = false;
        this.confirm = null;
        ErrorText = string.Empty;
    }
}
