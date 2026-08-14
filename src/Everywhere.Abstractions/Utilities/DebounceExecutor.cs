using System.Diagnostics;
using ITimer = Everywhere.Common.ITimer;

namespace Everywhere.Utilities;

/// <summary>
/// A high-performance, low-allocation debounced executor.
/// It debounces calls to a parameterless method and, when the delay has passed,
/// it invokes a value provider (Func{T}) and passes the result to an action (Action{T}).
/// </summary>
/// <typeparam name="TSender">The type of the value to be processed.</typeparam>
/// <typeparam name="TTimer"></typeparam>
public class DebounceExecutor<TSender, TTimer> : IDisposable where TTimer : class, ITimer, new()
{
    /// <summary>
    /// Gets or sets the debounce delay time.
    /// </summary>
    public TimeSpan Delay { get; set; }

    /// <summary>
    /// Gets or sets the maximum amount of time an execution may remain pending after its
    /// first trigger. A <see langword="null"/> value preserves ordinary trailing-debounce
    /// behavior without a maximum wait.
    /// </summary>
    /// <remarks>
    /// This is useful for continuously changing sources such as streamed chat output: each
    /// change may reset <see cref="Delay"/>, but the pending batch is still executed once this
    /// limit is reached. The value is measured from the first trigger in the current batch.
    /// </remarks>
    public TimeSpan? MaximumDelay { get; set; }

    private readonly TTimer _timer;
    private readonly Func<TSender> _valueProvider;
    private readonly Action<TSender> _action;
    private readonly Lock _stateLock = new();

    private volatile bool _isDisposed;
    private bool _hasPendingExecution;
    private long _firstTriggerTimestamp;
    private TimeSpan _dueElapsed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DebounceExecutor{TSender, TTimer}"/> class.
    /// </summary>
    /// <param name="valueProvider">The function to call to get the value when the action is to be executed.</param>
    /// <param name="action">The action to execute with the value from the provider.</param>
    /// <param name="delay">The debounce delay time.</param>
    public DebounceExecutor(Func<TSender> valueProvider, Action<TSender> action, TimeSpan delay)
    {
        _valueProvider = valueProvider;
        _action = action;
        Delay = delay;
        _timer = new TTimer();
        _timer.Callback += TimerCallback;
    }

    /// <summary>
    /// Triggers the execution of the action after the debounce delay.
    /// If called again before the delay has passed, the timer is reset. When
    /// <see cref="MaximumDelay"/> is set, the timer cannot be postponed beyond that
    /// maximum interval for the current batch.
    /// I've renamed Execute to Trigger, as it's a more fitting name for a parameterless method that starts a process.
    /// </summary>
    public void Trigger()
    {
        lock (_stateLock)
        {
            if (_isDisposed) return;

            // TODO: maybe we need to move Stopwatch calls into ITimer, but DispatcherTimer is not a good fit for that, as it doesn't have a way to get the current timestamp.
            var now = Stopwatch.GetTimestamp();
            if (!_hasPendingExecution)
            {
                _hasPendingExecution = true;
                _firstTriggerTimestamp = now;
            }

            var elapsed = Stopwatch.GetElapsedTime(_firstTriggerTimestamp, now);
            var interval = Delay;
            if (MaximumDelay is { } maximumDelay)
            {
                interval = Min(interval, maximumDelay - elapsed);
            }

            interval = Max(interval, TimeSpan.Zero);
            _dueElapsed = elapsed + interval;
            _timer.Interval = interval;
            _timer.Start();
        }
    }

    public void Cancel()
    {
        lock (_stateLock)
        {
            if (_isDisposed) return;

            _hasPendingExecution = false;
            _timer.Stop();
        }
    }

    private void TimerCallback()
    {
        lock (_stateLock)
        {
            if (_isDisposed || !_hasPendingExecution) return;

            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_firstTriggerTimestamp, now);
            if (elapsed < _dueElapsed)
            {
                // A timer callback may already be queued when Trigger resets the timer.
                // Re-arm it for the current deadline instead of executing an early batch.
                _timer.Interval = _dueElapsed - elapsed;
                _timer.Start();
                return;
            }

            _hasPendingExecution = false;
        }

        try
        {
            // Get the value and execute the action.
            var value = _valueProvider();
            _action(value);
        }
        catch
        {
            // Depending on requirements, you might want to log exceptions here.
            // By default, we suppress exceptions from the provider or action to prevent the timer from crashing.
        }
    }

    /// <summary>
    /// Disposes the executor, stopping any pending operations.
    /// </summary>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_isDisposed) return;

            _isDisposed = true;
            _hasPendingExecution = false;
            _timer.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}