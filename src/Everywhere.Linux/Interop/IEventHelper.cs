using Avalonia;
using Avalonia.Input;
using Everywhere.Interop;

namespace Everywhere.Linux.Interop;

public enum EventType
{
    Unknown,
    KeyDown,
    KeyUp,
    MouseDown,
    MouseUp,
    MouseRight,
    MouseMiddle,
    MouseDrag,
    MouseWheelDown,
    MouseWheelUp,
    MouseWheelLeft,
    MouseWheelRight,
    FocusChange,
}

public interface IEventHelper
{
    bool GetKeyState(KeyModifiers keyModifiers);

    /// <summary>
    /// Grab a global key. Returns an id (>0) on success, or 0 on failure.
    /// The _backend must handle common modifier permutations (Lock/NumLock).
    /// </summary>
    int GrabKey(KeyboardShortcut hotkey, Action handler);

    /// <summary>Ungrab a previously grabbed key by id.</summary>
    void UngrabKey(int id);

    void GrabKeyHook(Action<KeyboardShortcut, EventType> hook);

    void UngrabKeyHook();

    int GrabMouse(MouseShortcut hotkey, Action handler);

    void UngrabMouse(int id);

    void GrabMouseHook(Action<PixelPoint, EventType> hook);

    void UngrabMouseHook();
}