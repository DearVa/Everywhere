using System.Runtime.InteropServices;
using Everywhere.Linux.Interop.AtspiBackend.Native;
using GObj = GObject.Object;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop.AtspiBackend.Core;

public sealed class AtspiEventHub : IDisposable
{
    private readonly ILogger _logger;
    private readonly AtspiNative.AtspiEventListenerCallback _nativeCallback;
    private readonly Lock _focusLock = new();
    private IntPtr _eventListener;
    private GObj? _focusedElement;
    private bool _disposed;
    public event EventHandler<FocusChangedEventArgs>? FocusChanged;

    public event EventHandler<ElementDefunctEventArgs>? ElementDefunct;

    public AtspiEventHub(ILogger logger)
    {
        _logger = logger;
        
        // Keep callback alive to prevent GC collection
        _nativeCallback = OnNativeEvent;
    }
    public void RegisterEvents()
    {
        if (_eventListener != IntPtr.Zero)
        {
            _logger.LogWarning("Events already registered");
            return;
        }

        // Create event listener
        _eventListener = AtspiNative.atspi_event_listener_new(
            _nativeCallback,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_eventListener == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create AT-SPI event listener");
        }

        // Register for focus change events
        RegisterEventType("object:state-changed:focused");
        
        // Register for defunct state (element destroyed)
        RegisterEventType("object:state-changed:defunct");

        _logger.LogInformation("AT-SPI events registered successfully");
    }
    private void RegisterEventType(string eventType)
    {
        var eventTypePtr = Marshal.StringToCoTaskMemUTF8(eventType);
        try
        {
            var result = AtspiNative.atspi_event_listener_register(
                _eventListener,
                eventTypePtr,
                IntPtr.Zero);

            if (result == 0)
            {
                _logger.LogWarning("Failed to register event type: {EventType}", eventType);
            }
            else
            {
                _logger.LogDebug("Registered event type: {EventType}", eventType);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(eventTypePtr);
        }
    }
    private void OnNativeEvent(IntPtr eventPtr, IntPtr userData)
    {
        if (_disposed || eventPtr == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var ev = Marshal.PtrToStructure<AtspiNative.AtspiEvent>(eventPtr);
            var eventType = Marshal.PtrToStringAnsi(ev.type) ?? string.Empty;

            if (eventType.Contains("focused"))
            {
                HandleFocusEvent(ev);
            }
            else if (eventType.Contains("defunct"))
            {
                HandleDefunctEvent(ev);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling AT-SPI event: {Message}", ex.Message);
        }
    }
    private void HandleFocusEvent(AtspiNative.AtspiEvent ev)
    {
        if (ev.source == IntPtr.Zero)
        {
            return;
        }

        lock (_focusLock)
        {
            GObj? newFocus = null;

            if (ev.detail1 == 1) // Focus in
            {
                try
                {
                    newFocus = GObjectWrapper.WrapOwned(ev.source);
                    _focusedElement = newFocus;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to wrap focused element");
                }
            }
            else // Focus out
            {
                _focusedElement = null;
            }

            // Fire C# event
            try
            {
                FocusChanged?.Invoke(this, new FocusChangedEventArgs(newFocus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in FocusChanged event handler");
            }
        }
    }
    private void HandleDefunctEvent(AtspiNative.AtspiEvent ev)
    {
        if (ev.source == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var defunctElement = GObjectWrapper.WrapOwned(ev.source);
            
            _logger.LogDebug("Element became defunct: {Hash}", defunctElement?.GetHashCode());

            // Fire C# event so cache can remove this element
            ElementDefunct?.Invoke(this, new ElementDefunctEventArgs(defunctElement));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle defunct element");
        }
    }
    public GObj? GetFocusedElement()
    {
        lock (_focusLock)
        {
            return _focusedElement;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_eventListener != IntPtr.Zero)
        {
            try
            {
                // Unregister events
                UnregisterEventType("object:state-changed:focused");
                UnregisterEventType("object:state-changed:defunct");

                _eventListener = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error unregistering AT-SPI events");
            }
        }
    }
    private void UnregisterEventType(string eventType)
    {
        var eventTypePtr = Marshal.StringToCoTaskMemUTF8(eventType);
        try
        {
            AtspiNative.atspi_event_listener_deregister(
                _eventListener,
                eventTypePtr,
                IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(eventTypePtr);
        }
    }
}
public sealed class FocusChangedEventArgs : EventArgs
{
    public GObj? FocusedElement { get; }

    public FocusChangedEventArgs(GObj? focusedElement)
    {
        FocusedElement = focusedElement;
    }
}
public sealed class ElementDefunctEventArgs : EventArgs
{
    public GObj? DefunctElement { get; }

    public ElementDefunctEventArgs(GObj? defunctElement)
    {
        DefunctElement = defunctElement;
    }
}
internal static class GObjectWrapper
{
    public static GObj? WrapOwned(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        return (GObj)GObject.Internal.InstanceWrapper.WrapHandle<GObj>(handle, true);
    }

    public static GObj WrapSingleton(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(handle));
        }

        return (GObj)GObject.Internal.InstanceWrapper.WrapHandle<GObj>(handle, false);
    }
}