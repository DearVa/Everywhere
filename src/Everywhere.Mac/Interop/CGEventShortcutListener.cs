using System.Reactive.Disposables;
using Avalonia.Input;
using Everywhere.Interop;
using Serilog;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Provides a global keyboard and mouse shortcut listener for macOS using CoreGraphics Event Taps.
/// Requires Accessibility permissions.
/// </summary>
// ReSharper disable once InconsistentNaming
public sealed class CGEventShortcutListener : IShortcutListener, IDisposable
{
    private readonly Dictionary<KeyboardShortcut, List<Action>> _keyboardHandlers = new();
    private readonly Dictionary<MouseShortcut, List<Action>> _mouseHandlers = new();
    private readonly Lock _syncLock = new();

    private KeyboardShortcutScopeImpl? _currentCaptureScope;
    private bool _needToSwallowModifierKey;

    public CGEventShortcutListener()
    {
        CGEventListener.Default.EventReceived += HandleEvent;
    }

    private void HandleEvent(CGEventType type, CGEvent cgEvent, ref nint cgEventRef)
    {
        switch (type)
        {
            case CGEventType.KeyDown:
                HandleKeyDown(cgEvent, ref cgEventRef);
                break;
            case CGEventType.KeyUp:
                HandleKeyUp(ref cgEventRef);
                break;
            case CGEventType.FlagsChanged:
                HandleFlagsChanged(cgEvent, ref cgEventRef);
                break;
            case CGEventType.LeftMouseDown:
            case CGEventType.LeftMouseUp:
            case CGEventType.RightMouseDown:
            case CGEventType.RightMouseUp:
            case CGEventType.OtherMouseDown:
            case CGEventType.OtherMouseUp:
                // HandleMouse(type, nsEvent);
                break;
        }
    }

    private void HandleKeyDown(CGEvent cgEvent, ref nint cgEventRef)
    {
        var key = ((ushort)cgEvent.GetLongValueField(CGEventField.KeyboardEventKeycode)).ToAvaloniaKey();
        var modifiers = cgEvent.Flags.ToAvaloniaKeyModifiers();
        var shortcut = new KeyboardShortcut(key, modifiers);

        List<Action>? handlers = null;
        using (var _ = _syncLock.EnterScope())
        {
            if (_currentCaptureScope != null)
            {
                // If we are in capture mode, update the current shortcut being pressed.
                _currentCaptureScope.PressingShortcut = _currentCaptureScope.PressingShortcut with { Key = shortcut.Key };
                cgEventRef = 0; // Swallow the event
                return;
            }

            if (_keyboardHandlers.TryGetValue(shortcut, out var registeredHandlers))
            {
                handlers = [.. registeredHandlers];
            }
        }

        if (handlers != null)
        {
            foreach (var handler in handlers)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    // Swallow exceptions from handlers to avoid crashing the event loop.
                    Log.ForContext<CGEventShortcutListener>().Error(
                        ex,
                        "Exception occurred while handling keyboard shortcut {Shortcut}.",
                        shortcut);
                }
            }

            cgEventRef = 0; // Swallow the event

            // If a shortcut was handled, we may need to swallow the following modifier key event.
            if (modifiers != KeyModifiers.None) _needToSwallowModifierKey = true;
        }
    }

    private void HandleKeyUp(ref nint cgEventRef)
    {
        // If we are in capture mode, notify that the shortcut has been finished.
        using var _ = _syncLock.EnterScope();
        if (_currentCaptureScope is null) return;

        _currentCaptureScope.NotifyShortcutFinished();
        cgEventRef = 0; // Swallow the event
    }

    private void HandleFlagsChanged(CGEvent cgEvent, ref nint cgEventRef)
    {
        var modifiers = cgEvent.Flags.ToAvaloniaKeyModifiers();
        if (_needToSwallowModifierKey)
        {
            // Swallow the following modifier key event until no modifiers are pressed.
            cgEventRef = 0;
            if (modifiers == KeyModifiers.None) _needToSwallowModifierKey = false;
        }

        using var _ = _syncLock.EnterScope();
        if (_currentCaptureScope is null) return;

        _currentCaptureScope.PressingShortcut = _currentCaptureScope.PressingShortcut with { Modifiers = modifiers };
        cgEventRef = 0; // Swallow the event
    }

    public IDisposable Register(KeyboardShortcut shortcut, Action handler)
    {
        if (shortcut.Key == Key.None || shortcut.Modifiers == KeyModifiers.None)
            throw new ArgumentException("Invalid keyboard shortcut.", nameof(shortcut));
        ArgumentNullException.ThrowIfNull(handler);

        using var _ = _syncLock.EnterScope();
        if (!_keyboardHandlers.TryGetValue(shortcut, out var handlers))
        {
            handlers = [];
            _keyboardHandlers[shortcut] = handlers;
        }

        handlers.Add(handler);
        return Disposable.Create(() =>
        {
            using var _ = _syncLock.EnterScope();
            if (_keyboardHandlers.TryGetValue(shortcut, out var existingHandlers))
            {
                existingHandlers.Remove(handler);
                if (existingHandlers.Count == 0)
                {
                    _keyboardHandlers.Remove(shortcut);
                }
            }
        });
    }

    public IDisposable Register(MouseShortcut shortcut, Action handler)
    {
        // TODO: Implement mouse shortcut registration.
        // This will involve listening to mouse events in HandleEvent,
        // managing timers for delays, and invoking handlers.
        throw new NotImplementedException();
    }

    public IKeyboardShortcutScope StartCaptureKeyboardShortcut()
    {
        using var _ = _syncLock.EnterScope();
        if (_currentCaptureScope != null) return _currentCaptureScope;

        // Start a new capture scope
        var scope = new KeyboardShortcutScopeImpl(this);
        _currentCaptureScope = scope;
        return scope;
    }

    /// <summary>
    /// Implementation of IKeyboardShortcutScope for capturing keyboard shortcuts.
    /// This class is intended to be used internally by CGEventShortcutListener.
    /// </summary>
    private sealed class KeyboardShortcutScopeImpl(CGEventShortcutListener owner) : IKeyboardShortcutScope
    {
        public KeyboardShortcut PressingShortcut
        {
            get;
            set
            {
                if (field == value) return;
                field = value;
                PressingShortcutChanged?.Invoke(this, value);
            }
        }

        public bool IsDisposed { get; private set; }

        public event IKeyboardShortcutScope.PressingShortcutChangedHandler? PressingShortcutChanged;

        public event IKeyboardShortcutScope.ShortcutFinishedHandler? ShortcutFinished;

        public void NotifyShortcutFinished() => ThreadPool.QueueUserWorkItem(_ => ShortcutFinished?.Invoke(this, PressingShortcut));

        public void Dispose()
        {
            if (IsDisposed) return;

            using var _ = owner._syncLock.EnterScope();
            if (owner._currentCaptureScope == this) owner._currentCaptureScope = null;
            IsDisposed = true;
        }
    }

    public void Dispose()
    {
        _currentCaptureScope?.Dispose();
        CGEventListener.Default.EventReceived -= HandleEvent;
    }
}