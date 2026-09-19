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
    }

    protected IAlarmSink Alarms { get; }

    [ObservableProperty]
    private bool isBusy;

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
