using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.Contracts;
using RollGrinder.Core;
using RollGrinder.Data;
using RollGrinder.Services.Alarms;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 界面层的统一异常出口：领域异常、网关异常与存储异常都转成报警条目，
/// 不在界面上静默吞掉，也不让它冲垮 UI 线程。
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    protected ViewModelBase(IAlarmSink alarms)
    {
        Alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.uiContext = SynchronizationContext.Current;
    }

    // 构造时所在线程（界面线程）的同步上下文；单元测试里没有，就地执行。
    private readonly SynchronizationContext? uiContext;

    protected IAlarmSink Alarms { get; }

    [ObservableProperty]
    private bool isBusy;

    /// <summary>
    /// 服务层的事件可能在后台线程上触发（后台服务、ConfigureAwait(false) 之后）；
    /// 界面集合只能在界面线程上改，所以事件处理一律经这里回到界面线程。
    /// </summary>
    protected void OnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (this.uiContext is null || SynchronizationContext.Current == this.uiContext)
        {
            action();
            return;
        }

        this.uiContext.Post(_ => action(), null);
    }

    /// <summary>执行一段可能失败的界面操作；失败转报警并返回 false。</summary>
    protected async Task<bool> RunGuardedAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            await action(cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is DomainException or GatewayException or DataStoreException
                                      or Microsoft.Data.Sqlite.SqliteException)
        {
            Alarms.RaiseException(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
