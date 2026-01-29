namespace Everywhere.Interop;

public interface IWatchdogManager
{
    /// <summary>
    /// Registers a subprocess to be monitored by the Watchdog.
    /// </summary>
    /// <param name="processId">The id of process to monitor.</param>
    Task RegisterProcessAsync(int processId);

    /// <summary>
    /// Unregisters a subprocess from the Watchdog.
    /// </summary>
    /// <param name="processId">The id of process to stop monitoring.</param>
    /// <param name="killIfRunning"></param>
    Task UnregisterProcessAsync(int processId, bool killIfRunning = true);
}