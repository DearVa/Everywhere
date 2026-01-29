using CoreFoundation;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Listens to global CGEvents such as keyboard and mouse events.
/// </summary>
public class CGEventListener
{
    /// <summary>
    /// Delegate for handling CGEvents. Set cgEventRef to zero to swallow the event.
    /// </summary>
    public delegate void CGEventHandler(CGEventType type, CGEvent cgEvent, ref nint cgEventRef);

    /// <summary>
    /// The default event listener with standard permissions. It can intercept and modify events.
    /// </summary>
    public static CGEventListener Default { get; } = new(CGEventTapOptions.Default);

    /// <summary>
    /// The event listener with listen-only permissions. It cannot intercept or modify events. But handlers can take longer time to process events.
    /// </summary>
    public static CGEventListener ListenOnly { get; } = new(CGEventTapOptions.ListenOnly);

    public event CGEventHandler? EventReceived;

    private readonly CGEventTapOptions _options;
    private readonly AutoResetEvent _readySignal = new(false);

    private CFMachPort? _eventTap;
    private CFRunLoopSource? _runLoopSource;

    private CGEventListener(CGEventTapOptions options)
    {
        // It's crucial to check for permissions before attempting to create the tap.
        PermissionHelper.EnsureAccessibilityTrusted();

        _options = options;
        var workerThread = new Thread(RunLoopThread)
        {
            Name = "CGEventListenerThread",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        workerThread.Start();
        _readySignal.WaitOne();
    }

    private void RunLoopThread()
    {
        using var eventTap = CreateTap();
        _eventTap = eventTap;

        _runLoopSource = eventTap.CreateRunLoopSource();
        CFRunLoop.Current.AddSource(_runLoopSource, CFRunLoop.ModeDefault);

        _readySignal.Set();
        CFRunLoop.Current.Run();
    }

    private CFMachPort CreateTap()
    {
        const CGEventMask mask =
            CGEventMask.KeyDown | CGEventMask.KeyUp | CGEventMask.FlagsChanged |
            CGEventMask.LeftMouseDown | CGEventMask.LeftMouseUp |
            CGEventMask.RightMouseDown | CGEventMask.RightMouseUp |
            CGEventMask.OtherMouseDown | CGEventMask.OtherMouseUp;

        var tap = CGEvent.CreateTap(
            CGEventTapLocation.HID,
            CGEventTapPlacement.HeadInsert,
            _options,
            mask,
            HandleEvent,
            IntPtr.Zero);

        return tap ?? throw new InvalidOperationException("CGEvent tap creation failed.");
    }

    private nint HandleEvent(nint proxy, CGEventType type, nint cgEventRef, nint userData)
    {
        // Early exit if the event tap is not initialized
        if (_eventTap is null) return cgEventRef;

        if (type is CGEventType.TapDisabledByTimeout or CGEventType.TapDisabledByUserInput)
        {
            CGEvent.TapEnable(_eventTap);
            return cgEventRef;
        }

        using var cgEvent = CGInterop.CGEventFromHandle(cgEventRef);
        EventReceived?.Invoke(type, cgEvent, ref cgEventRef);
        return cgEventRef;
    }
}