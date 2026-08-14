using Avalonia.Threading;

namespace Everywhere.Common;

/// <summary>
/// Wraps an Avalonia DispatcherTimer to implement the ITimer interface.
/// </summary>
public sealed class DispatcherTimerImpl : ITimer
{
    public event Action? Callback;

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    private readonly DispatcherTimer _timer;

    public DispatcherTimerImpl()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Default);
        _timer.Tick += HandleTimerTick;
    }

    private void HandleTimerTick(object? sender, EventArgs e)
    {
        // Stop before invoking the callback so a callback may safely schedule the next
        // one-shot tick. DebounceExecutor relies on this when it re-arms after a stale
        // callback that was already queued before the timer was reset.
        _timer.Stop();
        Callback?.Invoke();
    }

    public void Start()
    {
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void Dispose() { }
}