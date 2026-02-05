using System.Runtime.InteropServices;
using Everywhere.Linux.Interop.AtspiBackend.Native;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop.AtspiBackend.Core;

public sealed class AtspiContext : IDisposable
{
    private readonly ILogger _logger;
    private readonly Thread? _eventLoopThread;
    private bool _disposed;

    public bool IsInitialized { get; private set; }

    public AtspiContext(ILogger logger)
    {
        _logger = logger;

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AT_SPI_BUS")))
        {
            Environment.SetEnvironmentVariable("AT_SPI_BUS", "session");
        }

        // Initialize GObject type system
        GObject.Module.Initialize();

        // Initialize AT-SPI
        var result = AtspiNative.atspi_init();
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Failed to initialize AT-SPI. Return code: {result}. " +
                "Ensure accessibility services are running.");
        }

        IsInitialized = true;
        _logger.LogInformation("AT-SPI initialized successfully");

        // Start event loop in background thread
        _eventLoopThread = new Thread(EventLoopWorker)
        {
            Name = "AT-SPI Event Loop",
            IsBackground = true
        };
        _eventLoopThread.Start();
    }
    private void EventLoopWorker()
    {
        try
        {
            _logger.LogDebug("AT-SPI event loop starting");
            AtspiNative.atspi_event_main();
            _logger.LogDebug("AT-SPI event loop exited");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AT-SPI event loop crashed: {Message}", ex.Message);
        }
    }
    public void EnsureInitialized()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AtspiContext));
        }

        if (!IsInitialized)
        {
            throw new InvalidOperationException("AT-SPI context not initialized");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsInitialized = false;

        try
        {
            AtspiNative.atspi_exit();
            _logger.LogInformation("AT-SPI shutdown completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during AT-SPI shutdown");
        }

        // Event loop thread will terminate when atspi_exit() is called
    }
}