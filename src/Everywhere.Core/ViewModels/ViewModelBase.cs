using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Utilities;
using Everywhere.Views;
using ShadUI;

namespace Everywhere.ViewModels;

public abstract class ReactiveViewModelBase : ObservableValidator
{
    [field: AllowNull, MaybeNull]
    protected DialogManager DialogManager
    {
        get => field ??= ServiceLocator.Resolve<DialogManager>();
        private set;
    }

    [field: AllowNull, MaybeNull]
    protected ToastManager ToastManager
    {
        get => field ??= ServiceLocator.Resolve<ToastManager>();
        private set;
    }

    protected AnonymousExceptionHandler DialogExceptionHandler => new((exception, message, source, lineNumber) =>
        DialogManager.CreateDialog(exception.GetFriendlyMessage().ToString() ?? "Unknown error", $"[{source}:{lineNumber}] {message ?? "Error"}"));

    protected AnonymousExceptionHandler ToastExceptionHandler => new((exception, message, source, lineNumber) =>
        ToastManager.CreateToast($"[{source}:{lineNumber}] {message ?? "Error"}")
            .WithContent(exception.GetFriendlyMessage().ToTextBlock())
            .DismissOnClick()
            .ShowError());

    protected IClipboard Clipboard { get; private set; } = ServiceLocator.Resolve<IClipboard>();

    protected IStorageProvider StorageProvider { get; private set; } = ServiceLocator.Resolve<IStorageProvider>();

    /// <summary>
    /// Invoked when the view's <see cref="Control.Loaded"/> event is raised.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected internal virtual Task ViewLoaded(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Invoked when the view's <see cref="Control.Unloaded"/> event is raised.
    /// </summary>
    /// <returns></returns>
    protected internal virtual Task ViewUnloaded() => Task.CompletedTask;

    protected virtual IExceptionHandler? LifetimeExceptionHandler => null;

    private void HandleLifetimeException(string stage, Exception e)
    {
        var handler = LifetimeExceptionHandler ?? DialogManager.ToExceptionHandler();
        handler.HandleException(e, $"Lifetime Exception: [{stage}]");
    }

    public void Bind(Control target)
    {
        target.DataContext = this;

        var cancellationTokenSource = new ReusableCancellationTokenSource();
        target.Loaded += async (_, _) =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(target);
                if (topLevel is not null)
                {
                    if (topLevel.Clipboard is { } clipboard) Clipboard = clipboard;
                    StorageProvider = topLevel.StorageProvider;

                    if (topLevel is IReactiveHost reactiveHost)
                    {
                        DialogManager = reactiveHost.DialogHost.Manager;
                        ToastManager = reactiveHost.ToastHost.Manager;
                    }
                }

                await ViewLoaded(cancellationTokenSource.Token);
            }
            catch (Exception e)
            {
                HandleLifetimeException(nameof(ViewLoaded), e);
            }
        };

        target.Unloaded += async (_, _) =>
        {
            try
            {
                cancellationTokenSource.Cancel();
                await ViewUnloaded();
            }
            catch (Exception e)
            {
                HandleLifetimeException(nameof(ViewUnloaded), e);
            }
        };
    }
}

[Flags]
public enum ExecutionFlags
{
    None = 0,
    EnqueueIfBusy = 1,
}

public abstract partial class BusyViewModelBase : ReactiveViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; private set; }

    public bool IsNotBusy => !IsBusy;

    private Task? _currentTask;
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    protected async Task ExecuteBusyTaskAsync(
        Func<CancellationToken, Task> task,
        IExceptionHandler? exceptionHandler = null,
        ExecutionFlags flags = ExecutionFlags.None,
        CancellationToken cancellationToken = default)
    {
        await _executionLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!flags.HasFlag(ExecutionFlags.EnqueueIfBusy) && IsBusy) return;

            Task taskToWait;
            if (_currentTask is { IsCompleted: false })
            {
                taskToWait = _currentTask;
                _currentTask = _currentTask.ContinueWith(
                    async _ =>
                    {
                        try { await task(cancellationToken); }
                        catch when (cancellationToken.IsCancellationRequested) { }
                    },
                    TaskContinuationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                taskToWait = _currentTask = task(cancellationToken);
            }

            try
            {
                IsBusy = true;
                await taskToWait;
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (exceptionHandler != null)
            {
                exceptionHandler.HandleException(e);
            }
            finally
            {
                IsBusy = _currentTask is
                {
                    Status: TaskStatus.WaitingToRun or TaskStatus.Running or TaskStatus.WaitingForChildrenToComplete
                };
            }
        }
        finally
        {
            _executionLock.Release();
        }
    }

    protected Task ExecuteBusyTaskAsync(
        Action<CancellationToken> action,
        IExceptionHandler? exceptionHandler = null,
        ExecutionFlags flags = ExecutionFlags.None,
        CancellationToken cancellationToken = default) => ExecuteBusyTaskAsync(
        token =>
        {
            action(token);
            return Task.CompletedTask;
        },
        exceptionHandler,
        flags,
        cancellationToken);

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnIsBusyChanged(bool value) => OnIsBusyChanged();

    /// <summary>
    /// Invoked when the value of <see cref="IsBusy"/> changes.
    /// </summary>
    protected virtual void OnIsBusyChanged() { }
}