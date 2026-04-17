using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Handles X11 input events, manages hotkeys and hooks.
/// </summary>
public sealed class X11InputHandler
{
    private readonly ILogger _logger;
    private readonly X11Context _context;
    private readonly X11CoreServices _services;
    
    private readonly ConcurrentDictionary<int, RegInfo> _regs = new();
    private int _nextId = 1;

    private Action<KeyboardShortcut, EventType>? _keyboardHook;
    private Action<PixelPoint, EventType>? _mouseHook;

    private class RegInfo
    {
        public KeyCode Keycode { get; init; }
        public uint Mods { get; init; }
        public Action Handler { get; init; } = () => { };
    }

    public X11InputHandler(ILogger logger, X11Context context, X11CoreServices services)
    {
        _logger = logger;
        _context = context;
        _services = services;
        _context.XEventReceived += OnXEventReceived;
    }

    public int GrabKey(KeyboardShortcut hotkey, Action handler)
    {
        if (_context.Display == IntPtr.Zero) return 0;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        _context.Invoke(() =>
        {
            try
            {
                uint mods = ConvertModifiers(hotkey.Modifiers);
                var keycode = _services.XKeycode(hotkey.Key);
                if (keycode == 0) { tcs.SetResult(0); return; }

                var variants = new[] { 0u, (uint)KeyButtonMask.LockMask, (uint)KeyButtonMask.Mod2Mask, (uint)KeyButtonMask.LockMask | (uint)KeyButtonMask.Mod2Mask };
                foreach (var v in variants)
                {
                    _context.Invoke(() =>
                        Xlib.XGrabKey(_context.Display, keycode, (KeyButtonMask)(mods | v), _context.RootWindow, false, GrabMode.Async, GrabMode.Async)
                    );
                }
                _context.XFlush();

                var id = Interlocked.Increment(ref _nextId);
                _regs[id] = new RegInfo { Keycode = keycode, Mods = mods, Handler = handler };
                tcs.SetResult(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GrabKey op failed");
                tcs.SetResult(0);
            }
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    public void UngrabKey(int id)
    {
        _context.Invoke(() =>
        {
            if (!_regs.TryRemove(id, out var info)) return;
            var variants = new[] { 0u, (uint)KeyButtonMask.LockMask, (uint)KeyButtonMask.Mod2Mask, (uint)KeyButtonMask.LockMask | (uint)KeyButtonMask.Mod2Mask };
            foreach (var v in variants)
                Xlib.XUngrabKey(_context.Display, info.Keycode, (KeyButtonMask)(info.Mods | v), _context.RootWindow);
            _context.XFlush();
        });
    }

    public void GrabKeyHook(Action<KeyboardShortcut, EventType> hook)
    {
        _keyboardHook = hook;
        _context.Invoke(() =>
        {
            X11Native.XGrabKeyboard(_context.Display, _context.RootWindow, 0, GrabMode.Async, GrabMode.Async, X11Native.CurrentTime);
            _context.XFlush();
        });
    }

    public void UngrabKeyHook()
    {
        _keyboardHook = null;
        _context.Invoke(() =>
        {
            X11Native.XUngrabKeyboard(_context.Display, _context.RootWindow);
            Xlib.XSetInputFocus(_context.Display, _context.RootWindow, RevertFocus.RevertToPointerRoot, X11Native.CurrentTime);
            _context.XFlush();
        });
    }

    public void GrabMouseHook(Action<PixelPoint, EventType> hook)
    {
        _mouseHook = hook;
        _context.Invoke(() =>
        {
            Xlib.XGrabPointer(_context.Display, _context.RootWindow, false, 
                EventMask.ButtonPressMask | EventMask.ButtonReleaseMask | EventMask.ButtonMotionMask, 
                GrabMode.Async, GrabMode.Async, X11Window.None, 0, X11Native.CurrentTime);
            _context.XFlush();
        });
    }

    public void UngrabMouseHook()
    {
        _mouseHook = null;
        _context.Invoke(() =>
        {
            Xlib.XUngrabPointer(_context.Display, X11Native.CurrentTime);
            _context.XFlush();
        });
    }
    public void SendKeyboardShortcut(KeyboardShortcut shortcut)
    {
        KeyCode Modifier2Keycode(KeyModifiers mod)
        {
            var key = mod switch
            {
                KeyModifiers.Meta => Key.LWin,
                KeyModifiers.Control => Key.LeftCtrl,
                KeyModifiers.Shift => Key.LeftShift,
                KeyModifiers.Alt => Key.LeftAlt,
                _ => Key.None
            };
            return key == Key.None ? 0 : _services.XKeycode(key);
        }

        List<KeyCode> keycodes = [];
        List<KeyModifiers> mods = [KeyModifiers.Meta, KeyModifiers.Control, KeyModifiers.Shift, KeyModifiers.Alt];
        keycodes.AddRange(
            from m in mods
            where shortcut.Modifiers.HasFlag(m)
            select Modifier2Keycode(m)
            into code
            where code != 0
            select code);
        keycodes.Add(_services.XKeycode(shortcut.Key));
        _context.Invoke(() =>
        {
            foreach (var code in keycodes)
            {
                XTest.XTestFakeKeyEvent(_context.Display, code, true, 0);
            }
            foreach (var code in Enumerable.Reverse(keycodes))
            {
                XTest.XTestFakeKeyEvent(_context.Display, code, false, 0);
            }
            _context.XFlush();
        });
    }

    private void OnXEventReceived(IntPtr eventPtr)
    {
        var type = GetEventType(eventPtr);
        if (type == EventType.Unknown) return;

        switch (type)
        {
            case EventType.KeyDown:
            case EventType.KeyUp:
                HandleKeyboardEvent(eventPtr, type);
                break;
            case EventType.MouseDown:
            case EventType.MouseUp:
            case EventType.MouseDrag:
                HandleMouseEvent(eventPtr, type);
                break;
        }
    }

    private void HandleKeyboardEvent(IntPtr eventPtr, EventType type)
    {
        var evKey = Marshal.PtrToStructure<XKeyEvent>(eventPtr);
        var norm = evKey.state & (uint)(~(KeyButtonMask.LockMask | KeyButtonMask.Mod2Mask));
        var key = _services.KeycodeToAvaloniaKey((KeyCode)evKey.keycode);
        var modifiers = KeyStateToModifier(norm);

        if (_keyboardHook != null)
        {
            ThreadPool.QueueUserWorkItem(_ => _keyboardHook.Invoke(new KeyboardShortcut(key, modifiers), type));
        }

        if (type == EventType.KeyDown)
        {
            var keycode = (KeyCode)evKey.keycode;
            foreach (var kv in _regs)
            {
                if (kv.Value.Keycode == keycode && kv.Value.Mods == norm)
                {
                    ThreadPool.QueueUserWorkItem(_ => kv.Value.Handler());
                }
            }
        }
    }

    private void HandleMouseEvent(IntPtr eventPtr, EventType type)
    {
        var btnEv = Marshal.PtrToStructure<XButtonEvent>(eventPtr);
        if (_mouseHook != null)
        {
            ThreadPool.QueueUserWorkItem(_ => _mouseHook.Invoke(new PixelPoint(btnEv.x_root, btnEv.y_root), type));
        }
    }

    private uint ConvertModifiers(KeyModifiers modifiers)
    {
        uint mods = 0;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= (uint)KeyButtonMask.ShiftMask;
        if (modifiers.HasFlag(KeyModifiers.Control)) mods |= (uint)KeyButtonMask.ControlMask;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= (uint)KeyButtonMask.Mod1Mask;
        if (modifiers.HasFlag(KeyModifiers.Meta)) mods |= (uint)KeyButtonMask.Mod4Mask;
        return mods;
    }

    private KeyModifiers KeyStateToModifier(uint state)
    {
        var mod = KeyModifiers.None;
        if ((state & ((uint)KeyButtonMask.ShiftMask)) != 0) mod |= KeyModifiers.Shift;
        if ((state & ((uint)KeyButtonMask.ControlMask)) != 0) mod |= KeyModifiers.Control;
        if ((state & ((uint)KeyButtonMask.Mod1Mask)) != 0) mod |= KeyModifiers.Alt;
        if ((state & ((uint)KeyButtonMask.Mod4Mask)) != 0) mod |= KeyModifiers.Meta;
        return mod;
    }

    private EventType GetEventType(IntPtr rawEvent)
    {
        var ev = Marshal.PtrToStructure<XAnyEvent>(rawEvent);
        var type = (Event)ev.type;
        switch (type)
        {
            case Event.KeyPress: return EventType.KeyDown;
            case Event.KeyRelease: return EventType.KeyUp;
            case Event.ButtonPress: return EventType.MouseDown; // simplified mapping, add details if needed
            case Event.ButtonRelease: return EventType.MouseUp;
            case Event.MotionNotify: return EventType.MouseDrag;
            default: return EventType.Unknown;
        }
    }
}
