using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Everywhere.Extensions;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using Tmds.Linux;
using Window = Avalonia.Controls.Window;
using X11;
using X11Window = X11.Window;
using System.Diagnostics;

namespace Everywhere.Linux.Interop;

/// <summary>
/// X11 Backend Impl
/// </summary>
/// <remarks>
/// nuget X11.Net is used possibly
/// X11 lib doc: https://www.x.org/releases/current/doc/libX11/libX11/libX11.html
/// Extended Window Manager Hints (EWMH): https://specifications.freedesktop.org/wm/latest/
/// </remarks>
public sealed partial class X11WindowBackend : IWindowBackend, IEventHelper
{
    private readonly ILogger<X11WindowBackend> _logger;
    private readonly X11Window _rootWindow;
    private readonly ConcurrentDictionary<int, RegInfo> _regs = new();
    private readonly BlockingCollection<Action> _ops = new(new ConcurrentQueue<Action>());
    private readonly Thread? _xThread;
    private readonly int _wakePipeR = -1;
    private readonly int _wakePipeW = -1;
    private readonly ConcurrentDictionary<X11Window, IVisualElement> _windowCache = new();
    private readonly AtomCache _atomCache;

    // Current process id - used to identify and skip our own overlay windows during hit testing
    private readonly int _processId = Process.GetCurrentProcess().Id;

    private volatile bool _running;

    private IntPtr _display;
    private X11Window _scanSkipWindowHandle = X11Window.None;
    private int _nextId = 1;
    private Action<KeyboardShortcut, EventType>? _keyboardHook;
    private Action<PixelPoint, EventType>? _mouseHook;

    public X11WindowBackend(ILogger<X11WindowBackend> logger)
    {
        _logger = logger;
        _display = Xlib.XOpenDisplay(Environment.GetEnvironmentVariable("DISPLAY"));
        _atomCache = new AtomCache(_display);
        if (_display == IntPtr.Zero) return;
        _rootWindow = Xlib.XDefaultRootWindow(_display);
        // select key events on root window so we receive KeyPress/KeyRelease
        Xlib.XSelectInput(
            _display,
            _rootWindow,
            EventMask.KeyPressMask | EventMask.KeyReleaseMask
            | EventMask.ButtonPressMask | EventMask.ButtonReleaseMask | EventMask.ButtonMotionMask
            | EventMask.FocusChangeMask);
        // create wake pipe
        try
        {
            var fds = new int[2];
            unsafe
            {
                fixed (int* pfds = fds)
                {
                    if (LibC.pipe(pfds) == 0)
                    {
                        _wakePipeR = fds[0];
                        _wakePipeW = fds[1];
                        try
                        {
                            // set non-blocking
                            var flags = LibC.fcntl(_wakePipeR, LibC.F_GETFL, 0);
                            LibC.fcntl(_wakePipeR, LibC.F_SETFL, flags | LibC.O_NONBLOCK);
                            LibC.fcntl(_wakePipeW, LibC.F_SETFL, flags | LibC.O_NONBLOCK);
                            // set close-on-exec
                            LibC.fcntl(_wakePipeR, LibC.F_SETFD, LibC.FD_CLOEXEC);
                            LibC.fcntl(_wakePipeW, LibC.F_SETFD, LibC.FD_CLOEXEC);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("XThread fcntl Error: {ex}", ex.Message);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("XThread pipe file Error: {ex}", ex.Message);
        }

        _running = true;
        _xThread = new Thread(XThreadMain) { IsBackground = true, Name = "X11DisplayThread" };
        _xThread.Start();
    }

    ~X11WindowBackend()
    {
        _running = false;
        try
        {
            _ops.CompleteAdding();
            // wake the thread so poll/select returns
            if (_wakePipeW != -1)
            {
                var b = new byte[] { 1 };
                unsafe
                {
                    fixed (byte* pb = b)
                    {
                        LibC.write(_wakePipeW, pb, b.Length);
                    }
                }
            }
            if (_xThread?.IsAlive == true) _xThread.Join(500);
            if (_display != IntPtr.Zero)
            {
                Xlib.XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }
            if (_wakePipeR != -1) LibC.close(_wakePipeR);
            if (_wakePipeW != -1) LibC.close(_wakePipeW);
        }
        catch (Exception ex)
        {
            _logger.LogError("X11 Backend Close Error: {ex}", ex.Message);
        }
    }

    private KeyCode XKeycode(Key key)
    {
        var ks = Xlib.XStringToKeysym(key.ToString());
        KeyCode keycode = 0;
        if (ks != 0) keycode = Xlib.XKeysymToKeycode(_display, ks);
        if (keycode != 0) return keycode;
        ks = Xlib.XStringToKeysym(key.ToString().ToUpperInvariant());
        if (ks != 0) keycode = Xlib.XKeysymToKeycode(_display, ks);
        return keycode;
    }

    public bool GetKeyState(KeyModifiers keyModifier)
    {
        Dictionary<KeyModifiers, List<Key>> testKey = new()
        {
            [KeyModifiers.Alt] = [Key.LeftAlt, Key.RightAlt],
            [KeyModifiers.Control] = [Key.LeftCtrl, Key.RightCtrl],
            [KeyModifiers.Shift] = [Key.LeftShift, Key.RightShift],
            [KeyModifiers.Meta] = [Key.LWin, Key.RWin],
        };
        
        if (!testKey.TryGetValue(keyModifier, out var keys))
        {
            // Unknown or unsupported modifier; consider it not pressed.
            return false;
        }
        
        var keymap = new byte[32];
        XQueryKeymap(_display, keymap);
        
        // The modifier is considered pressed if either the left or right key is pressed.
        return Check(keymap, keys[0]) || Check(keymap, keys[1]);

        bool Check(byte[] map, Key key)
        {
            var keycode = XKeycode(key);
            if (keycode == 0) return false;

            var byteIndex = (byte)keycode / 8;
            var bitIndex = (byte)keycode % 8;
            var pressed = (map[byteIndex] >> bitIndex) & 1;
            return pressed == 1;
        }
    }

    public int GrabKey(KeyboardShortcut hotkey, Action handler)
    {
        if (_display == IntPtr.Zero) return 0;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        XThreadAction(() =>
        {
            try
            {
                uint mods = 0;
                if (hotkey.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= (uint)KeyButtonMask.ShiftMask;
                if (hotkey.Modifiers.HasFlag(KeyModifiers.Control)) mods |= (uint)KeyButtonMask.ControlMask;
                if (hotkey.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= (uint)KeyButtonMask.Mod1Mask;
                if (hotkey.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= (uint)KeyButtonMask.Mod4Mask;
                var keycode = XKeycode(hotkey.Key);
                if (keycode == 0)
                {
                    tcs.SetResult(0);
                    return;
                }

                var variants = new[]
                {
                    (KeyButtonMask)0u, KeyButtonMask.LockMask, KeyButtonMask.Mod2Mask,
                    KeyButtonMask.LockMask | KeyButtonMask.Mod2Mask
                };
                foreach (var v in variants)
                {
                    Xlib.XGrabKey(
                        _display,
                        keycode,
                        (KeyButtonMask)(mods | (uint)v),
                        _rootWindow,
                        false,
                        GrabMode.Async,
                        GrabMode.Async);
                }
                Xlib.XFlush(_display);
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
        if (_display == IntPtr.Zero) return;
        XThreadAction(() =>
        {
            if (!_regs.TryRemove(id, out var info)) return;
            var variants = new[]
            {
                (KeyButtonMask)0u, KeyButtonMask.LockMask, KeyButtonMask.Mod2Mask,
                KeyButtonMask.LockMask | KeyButtonMask.Mod2Mask
            };
            foreach (var v in variants) Xlib.XUngrabKey(_display, info.Keycode, (KeyButtonMask)(info.Mods | (uint)v), _rootWindow);
            Xlib.XFlush(_display);
        });
    }

    public void GrabKeyHook(Action<KeyboardShortcut, EventType> hook)
    {
        _keyboardHook = hook;
        XThreadAction(() =>
        {
            XGrabKeyboard(
                _display,
                _rootWindow,
                0,
                GrabMode.Async,
                GrabMode.Async,
                CurrentTime);
            Xlib.XFlush(_display);
        });
    }

    public void UngrabKeyHook()
    {
        _keyboardHook = null;
        XThreadAction(() =>
        {
            XUngrabKeyboard(_display, _rootWindow);
            // In X11 Ungrab do not remove focus, set focus to root
            Xlib.XSetInputFocus(_display, _rootWindow, RevertFocus.RevertToPointerRoot, CurrentTime);
            Xlib.XFlush(_display);
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
            return key == Key.None ? 0 : XKeycode(key);
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
        keycodes.Add(XKeycode(shortcut.Key));
        XThreadAction(() =>
        {
            foreach (var k in keycodes)
            {
                if (k == 0)
                {
                    _logger.LogWarning("invalid key?");
                    continue;
                }
                XTest.XTestFakeKeyEvent(_display, k, true, 0);
                Thread.Sleep(10);
            }
            keycodes.Reverse();
            foreach (var k in keycodes)
            {
                if (k == 0)
                {
                    _logger.LogWarning("invalid key?");
                    continue;
                }
                XTest.XTestFakeKeyEvent(_display, k, false, 0);
                Thread.Sleep(10);
            }
            Xlib.XFlush(_display);
        });
    }

    public int GrabMouse(MouseShortcut hotkey, Action handler)
    {
        throw new NotImplementedException();
    }

    public void UngrabMouse(int id)
    {
        throw new NotImplementedException();
    }

    public void GrabMouseHook(Action<PixelPoint, EventType> hook)
    {
        _mouseHook = hook;
        XThreadAction(() =>
        {
            Xlib.XGrabPointer(
                _display,
                _rootWindow,
                false,
                EventMask.ButtonPressMask | EventMask.ButtonReleaseMask | EventMask.ButtonMotionMask,
                GrabMode.Async,
                GrabMode.Async,
                X11Window.None,
                0,
                CurrentTime);
            Xlib.XFlush(_display);
        });
    }

    public void UngrabMouseHook()
    {
        _mouseHook = null;
        XThreadAction(() =>
        {
            Xlib.XUngrabPointer(_display, CurrentTime);
            Xlib.XFlush(_display);
        });
    }

    private static string KeysymToString(KeySym ks)
    {
        try
        {
            var p = XKeysymToString(ks);
            if (p == IntPtr.Zero) return string.Empty;
            return Marshal.PtrToStringAnsi(p) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private Key KeycodeToAvaloniaKey(KeyCode keycode)
    {
        try
        {
            var ks = Xlib.XKeycodeToKeysym(_display, keycode, 0);
            var name = KeysymToString(ks);
            if (!string.IsNullOrEmpty(name) && name.Length == 1 && char.IsLetter(name[0]))
                return (Key)Enum.Parse(typeof(Key), name.ToUpperInvariant());
            return Key.None;
        }
        catch
        {
            return Key.None;
        }
    }

    private void XGetProperty(
        X11Window window,
        string propertyName,
        long length,
        Atom reqType,
        Action<Atom, int, ulong, ulong, IntPtr> propertyCallback)
    { // actualType, actualFormat, nitems, bytesAfter, data
        var atom = _atomCache.GetAtom(propertyName, onlyIfExists: true);
        if (atom == Atom.None) return;

        XGetWindowProperty(
            _display,
            window,
            atom,
            0,
            length,
            0,
            reqType,
            out var actualType,
            out var actualFormat,
            out var nItems,
            out var bytesAfter,
            out var prop);
        try
        {
            propertyCallback(actualType, actualFormat, nItems, bytesAfter, prop);
        }
        finally
        {
            Xlib.XFree(prop);
        }
    }

    private int XGetWindowPid(X11Window window)
    {
        if (window == X11Window.None) return 0;

        int pid = 0;

        // local helper to try read _NET_WM_PID from a window
        bool TryReadPidFromWindow(X11Window w)
        {
            pid = 0;
            XGetProperty(
                w,
                "_NET_WM_PID",
                1,
                Atom.Cardinal,
                (_, _, nItems, _, prop) =>
                {
                    if (nItems == 0 || prop == IntPtr.Zero) return;
                    pid = Marshal.ReadInt32(prop);
                });
            return pid != 0;
        }

        // 1) Try the window itself
        if (TryReadPidFromWindow(window)) return pid;

        // 2) Walk up the parent chain to look for a PID on an ancestor
        try
        {
            var root = X11Window.None;
            var parent = X11Window.None;
            Xlib.XQueryTree(_display, window, ref root, ref parent, out var children);
            var current = parent;
            while (current != X11Window.None && current != _rootWindow)
            {
                if (TryReadPidFromWindow(current)) return pid;
                Xlib.XQueryTree(_display, current, ref root, ref parent, out children);
                current = parent;
            }
        }
        catch { /* ignore errors while walking parents */ }

        // 3) If not found, search children recursively (depth-limited)
        try
        {
            const int MaxDepth = 8; // avoid pathological recursion
            var stack = new Stack<(X11Window window, int depth)>();
            stack.Push((window, 0));
            while (stack.Count > 0)
            {
                var (w, depth) = stack.Pop();
                var root = X11Window.None;
                var parent = X11Window.None;
                Xlib.XQueryTree(_display, w, ref root, ref parent, out var children);
                foreach (var child in children)
                {
                    if (child == X11Window.None) continue;
                    if (TryReadPidFromWindow(child)) return pid;
                    if (depth + 1 < MaxDepth)
                    {
                        stack.Push((child, depth + 1));
                    }
                }
            }
        }
        catch { /* ignore errors while walking children */ }

        return 0;
    }

    private void XForEachTopWindow(Action<X11Window> handle)
    {

        XGetProperty(
            _rootWindow,
            "_NET_CLIENT_LIST",
            -1,
            Atom.Window,
            (type, format, count, _, data) =>
            {
                if (type == Atom.Window && format == 32)
                {
                    for (var i = 0; i < ((int)count); i++)
                    {
                        var ptr = new IntPtr(data.ToInt64() + i * sizeof(X11Window));
                        var client = (X11Window)Marshal.ReadInt64(ptr);
                        if (client != X11Window.None)
                        {
                            handle(client);
                        }

                    }
                }
            });
    }

    private IVisualElement GetWindowElement(X11Window window, Func<IVisualElement> maker)
    {
        // return cached if any
        if (_windowCache.TryGetValue(window, out var cachedElement))
        {
            // _logger.LogDebug("Using cached window element for {window}", window.ToString("X"));
            return cachedElement;
        }

        // create
        var element = maker();
        // _logger.LogDebug("Creating window element for {window}", window.ToString("X"));
        _windowCache[window] = element;
        return element;
    }

    private IVisualElement GetWindowElement(X11Window window)
    {
        return GetWindowElement(window, () => new X11WindowVisualElement(this, window));
    }

    public IVisualElement? GetFocusedWindowElement()
    {
        try
        {
            if (_display == IntPtr.Zero) return null;
            X11Window focusWindow = X11Window.None;
            RevertFocus revertTo = RevertFocus.RevertToNone;
            Xlib.XGetInputFocus(_display, ref focusWindow, ref revertTo);
            if (focusWindow == X11Window.None) return null;
            return GetWindowElement(focusWindow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetFocusedWindowElement failed");
            return null;
        }
    }

    private X11Window GetWindowAtPoint(X11Window window, int x, int y)
    {
        if (window == _scanSkipWindowHandle)
        {
            return ScanSkipWindow;
        }
        Xlib.XGetWindowAttributes(_display, window, out var attr);
        if (XGetWindowPid(window) == _processId)
        {
            return X11Window.None;
        }
        if ((MapState)attr.map_state != MapState.IsViewable || attr.override_redirect) // skip hidden
        {
            return X11Window.None;
        }
        var rx = x - attr.x;
        var ry = y - attr.y;
        var root = X11Window.None;
        var parent = X11Window.None;
        Xlib.XQueryTree(_display, window, ref root, ref parent, out var children);
        foreach (var child in children.Reversed()) // backward iter to find the topmost
        {
            if (child != X11Window.None)
            {
                var sub = GetWindowAtPoint(child, rx, ry);
                if (sub == ScanSkipWindow)
                {
                    if (window != _rootWindow)
                    {
                        return ScanSkipWindow;
                    }
                    continue;
                }
                if (sub != X11Window.None)
                {
                    return sub;
                }
            }
        }
        if (x < attr.x || y < attr.y ||
            x >= attr.x + attr.width || y >= attr.y + attr.height)
        {
            return X11Window.None;
        }
        // _logger.LogDebug(
        //     "<< get {window}",
        //     window.ToString("X"));
        return window;
    }

    public IVisualElement GetWindowElementAt(PixelPoint point)
    {
        var child = GetWindowAtPoint(_rootWindow, point.X, point.Y);
        // if child = 0 fallback to root
        if (child == X11Window.None)
        {
            _logger.LogDebug("XQueryPointer returned child=0, using root window");
            child = _rootWindow;
        }

        _logger.LogDebug(
            "GetWindowElementAt at ({x},{y}) -> window {window}",
            point.X,
            point.Y,
            child.ToString("X"));

        return GetWindowElement(child);
    }

    private PixelRect XGetWindowBound(X11Window window)
    {
        try
        {
            if (window == X11Window.None)
            {
                _logger.LogWarning("BoundingRectangle called with zero window handle");
                return default;
            }
            Xlib.XGetWindowAttributes(_display, window, out var windowAttr);
            var x = windowAttr.x;
            var y = windowAttr.y;
            var w = windowAttr.width;
            var h = windowAttr.height;
            var result = XTranslateCoordinates(
                _display,
                window,
                _rootWindow,
                0,
                0,
                out var absX,
                out var absY,
                out _);

            if (result != 0) return new PixelRect(absX, absY, (int)w, (int)h);
            _logger.LogWarning("XTranslateCoordinates failed for window {window}", window.ToString("X"));
            return new PixelRect(x, y, (int)w, (int)h);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BoundingRectangle failed for window {window}", window.ToString("X"));
            return default;
        }
    }

    public IVisualElement? GetWindowElementByInfo(int pid, PixelRect rect)
    {
        var target = X11Window.None;
        XForEachTopWindow((window) =>
        {
            if (XGetWindowPid(window) == pid && XGetWindowBound(window) == rect)
            {
                target = window;
            }
        });
        if (target != X11Window.None)
        {
            return GetWindowElement(target);
        }
        return null;
    }

    public IVisualElement GetScreenElement()
    {
        var screenIdx = Xlib.XDefaultScreen(_display);
        var screenWindow = Xlib.XRootWindow(_display, screenIdx);
        return GetWindowElement(screenWindow, () => new X11ScreenVisualElement(this, screenIdx));
    }

    public void SetFocusable(Window window, bool focusable)
    {
        try
        {
            var ph = window.TryGetPlatformHandle();
            if (ph is null) return;
            var wnd = (X11Window)ph.Handle;

            if (_display == IntPtr.Zero) return;

            var atomHints = _atomCache.GetAtom("WM_HINTS", onlyIfExists: false);
            if (atomHints != Atom.None)
            {
                // WM_HINTS：flags(32 bit) + input(32 bit) + Others...
                var hints = new ulong[2];
                hints[0] = 1u << 0; // InputHint flag
                hints[1] = (focusable) ? 1u : 0u;
                unsafe
                {
                    fixed (ulong* pHints = hints)
                    {
                        Xlib.XChangeProperty(
                            _display,
                            wnd,
                            atomHints,
                            atomHints,
                            32,
                            (int)PropertyMode.Replace,
                            (IntPtr)pHints,
                            2);
                    }
                }
            }

            Xlib.XFlush(_display);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "X11 SetWindowNoFocus failed");
        }
    }

    public void SetHitTestVisible(Window window, bool visible)
    {
        try
        {
            var ph = window.TryGetPlatformHandle();
            if (ph is null) return;
            X11Window wnd = (X11Window)ph.Handle;

            if (_display == IntPtr.Zero) return;

            if (XFixesQueryExtension(_display, out _, out _) != 0)
            {
                if (visible)
                {
                    IntPtr fullRegion = XFixesCreateRegion(
                        _display,
                        new[]
                        {
                            new XRectangle { x = 0, y = 0, width = (ushort)window.Width, height = (ushort)window.Height }
                        },
                        1);
                    XFixesSetWindowShapeRegion(_display, wnd, ShapeInput, 0, 0, fullRegion);
                    XFixesDestroyRegion(_display, fullRegion);
                }
                else
                {
                    IntPtr emptyRegion = XFixesCreateRegion(
                        _display,
                        [],
                        0);
                    XFixesSetWindowShapeRegion(_display, wnd, ShapeInput, 0, 0, emptyRegion);
                    XFixesDestroyRegion(_display, emptyRegion);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("X11 SetHitTestVisible {visible} Failed: {Message}", visible, ex.Message);
        }
    }

    public void SetOverrideRedirect(Window window, bool redirect)
    {
        try
        {
            var ph = window.TryGetPlatformHandle();
            if (ph is null) return;
            var wnd = (X11Window)ph.Handle;

            if (_display == IntPtr.Zero) return;

            var swa = new XSetWindowAttributes
            {
                override_redirect = redirect ? 1 : 0
            };

            const ulong CWOverrideRedirect = 1UL << 9; // CWOverrideRedirect from X11/X.h
            XChangeWindowAttributes(_display, wnd, CWOverrideRedirect, ref swa);
            Xlib.XFlush(_display);
        }
        catch (Exception ex)
        {
            _logger.LogError("X11 SetOverrideRedirect Failed: {Message}", ex.Message);
        }
    }

    public bool GetEffectiveVisible(Window window)
    {
        var ph = window.TryGetPlatformHandle();
        if (ph is null) return false;
        var wnd = (X11Window)ph.Handle;
        Xlib.XGetWindowAttributes(_display, wnd, out var attr);
        if ((MapState)attr.map_state != MapState.IsViewable)
        {
            return false;
        }
        var xaAtom = _atomCache.GetAtom("ATOM", onlyIfExists: false);
        if (xaAtom == Atom.None) return false;

        var hiddenAtom = _atomCache.GetAtom("_NET_WM_STATE_HIDDEN", onlyIfExists: false);
        if (hiddenAtom == Atom.None) return false;

        bool isHidden = false;

        XGetProperty(
            window: wnd,
            propertyName: "_NET_WM_STATE",
            length: -1,
            reqType: xaAtom,
            propertyCallback: (actualType, actualFormat, nItems, _, data) =>
            {
                if (actualType != xaAtom || actualFormat != 32 || nItems == 0 || data == IntPtr.Zero)
                    return;

                // check any _NET_WM_STATE_HIDDEN
                for (ulong i = 0; i < nItems; i++)
                {
                    uint atomValue = (uint)Marshal.ReadInt32(data, (int)(i * 4));
                    if (atomValue == (uint)hiddenAtom)
                    {
                        isHidden = true;
                        break;
                    }
                }
            });
        return !isHidden;
    }

    public void SetCloaked(Window window, bool cloaked)
    {
        // In X11, we manually handle window element search and skip picker window,
        // so just use AvaloniaUI hide/show
        if (cloaked)
        {
            window.Hide();
        }
        else
        {
            window.Show();
            window.Activate();
        }

        Xlib.XFlush(_display);
    }

    public bool AnyModelDialogOpened(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return false;
        var ownerWindow = (X11Window)handle.Handle;
        var dialogFound = false;
        XForEachTopWindow((win) =>
        {
            if (win == ownerWindow)
            {
                dialogFound = true;
            }
        });
        return dialogFound;
    }

    public PixelPoint GetPointer()
    {
        var window = X11Window.None;
        var child = X11Window.None;
        int rx = 0, ry = 0, wx = 0, wy = 0;
        uint mask = 0;
        Xlib.XQueryPointer(
            _display,
            _rootWindow,
            ref window,
            ref child,
            ref rx,
            ref ry,
            ref wx,
            ref wy,
            ref mask);
        return new PixelPoint(rx, ry);
    }

    public void SetPickerWindow(Window? window)
    {
        var handle = (X11Window?)window?.TryGetPlatformHandle()?.Handle;
        _scanSkipWindowHandle = handle ?? X11Window.None;
    }

    public Bitmap Capture(IVisualElement? window, PixelRect rect)
    {
        try
        {
            var wnd = _rootWindow;
            PixelRect captureRect = rect;
            if (window != null)
            {
                wnd = (X11Window)window.NativeWindowHandle;
            }
            if (wnd == X11Window.None)
            {
                throw new ArgumentException("Invalid visual element: window handle is zero", nameof(window));
            }
            if (captureRect.Width <= 0 || captureRect.Height <= 0)
            {
                throw new ArgumentException($"Invalid visual element bounding rectangle: {captureRect}", nameof(window));
            }
            return StandardXGetImageCapture(wnd, captureRect);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Capture(element) failed for element {elementType} {elementId}",
                window?.Type.ToString() ?? "null",
                window?.Id ?? "null");
            throw;
        }
    }

    private Bitmap StandardXGetImageCapture(X11Window drawable, PixelRect rect)
    {
        uint winW = 0, winH = 0;
        if (!IsValidDrawable(drawable, ref winW, ref winH))
        {
            _logger.LogError("Invalid drawable: {drawable}", drawable.ToString("X"));
            throw new InvalidOperationException("Invalid drawable");
        }
        if (rect.Y < 0 || rect.Y + rect.Height > winH || rect.X < 0 || rect.X + rect.Width > winW)
        {
            rect = rect.Intersect(new PixelRect(0, 0, (int)winW, (int)winH));
        }

        _logger.LogDebug(
            "Calling XGetImage with drawable={drawable}, x={x}, y={y}, w={w}, h={h}",
            drawable.ToString("X"),
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height);

        var ximage = Xlib.XGetImage(
            _display,
            drawable,
            rect.X,
            rect.Y,
            (uint)rect.Width,
            (uint)rect.Height,
            (ulong)Planes.AllPlanes,
            PixmapFormat.ZPixmap);
        try
        {
            if (ximage.data == IntPtr.Zero)
            {
                _logger.LogError(
                    "XGetImage returned null for drawable {drawable} at ({x},{y}) size {width}x{height}",
                    drawable.ToString("X"),
                    rect.X,
                    rect.Y,
                    rect.Width,
                    rect.Height);
                throw new InvalidOperationException($"XGetImage failed for drawable {drawable}");
            }
            // check pixel format
            _logger.LogDebug(
                "XImage format: depth={depth}, bits_per_pixel={bpp}, bytes_per_line={bpl}",
                ximage.depth,
                ximage.bits_per_pixel,
                ximage.bytes_per_line);

            var stride = ximage.bytes_per_line;
            int bufferSize = stride * ximage.height;
            byte[] pixelData = new byte[bufferSize];
            Marshal.Copy(ximage.data, pixelData, 0, bufferSize);

            ConvertPixelFormat(pixelData, ximage, rect.Width, rect.Height, stride);

            unsafe
            {
                fixed (byte* p = pixelData)
                {
                    return new Bitmap(
                        Avalonia.Platform.PixelFormat.Bgra8888,
                        Avalonia.Platform.AlphaFormat.Unpremul,
                        new nint(p),
                        new PixelSize(rect.Width, rect.Height),
                        new Vector(96, 96),
                        stride);
                }
            }
        }
        finally
        {
            Xutil.XDestroyImage(ref ximage);
        }
    }

    private void ConvertPixelFormat(byte[] pixelData, XImage ximage, int width, int height, int stride)
    {
        bool converted = false;
        byte[] bgraData = new byte[height * stride];

        int bpp = ximage.bits_per_pixel;
        int depth = ximage.depth;
        if (bpp == 24 || bpp == 32)
        {
            // Use masks to extract channels
            for (int y = 0; y < height; y++)
            {
                int srcRowStart = y * (width * (bpp / 8));
                int dstRowStart = y * stride;

                for (int x = 0; x < width; x++)
                {
                    int srcPixel = srcRowStart + x * (bpp / 8);
                    int dstPixel = dstRowStart + x * 4;

                    if (srcPixel + (bpp / 8 - 1) < pixelData.Length)
                    {
                        uint pixelValue = 0;
                        for (int i = 0; i < (bpp / 8); i++)
                            pixelValue |= (uint)pixelData[srcPixel + i] << (8 * i); // assuming little-endian

                        byte r = (byte)((pixelValue & ximage.red_mask) >> GetShiftFromMask(ximage.red_mask));
                        byte g = (byte)((pixelValue & ximage.green_mask) >> GetShiftFromMask(ximage.green_mask));
                        byte b = (byte)((pixelValue & ximage.blue_mask) >> GetShiftFromMask(ximage.blue_mask));

                        // Normalize to 8-bit if masks are smaller
                        r = NormalizeChannel(r, ximage.red_mask);
                        g = NormalizeChannel(g, ximage.green_mask);
                        b = NormalizeChannel(b, ximage.blue_mask);

                        bgraData[dstPixel] = b;
                        bgraData[dstPixel + 1] = g;
                        bgraData[dstPixel + 2] = r;
                        bgraData[dstPixel + 3] = depth == 32 ? (byte)(pixelValue >> 24) : (byte)255;
                    }
                }
            }
            converted = true;
        }
        else if (bpp == 16 && depth <= 16)
        {
            // RGB565 or other 16-bit formats — use masks same way
            for (int y = 0; y < height; y++)
            {
                int srcRowStart = y * (width * 2);
                int dstRowStart = y * stride;

                for (int x = 0; x < width; x++)
                {
                    int srcPixel = srcRowStart + x * 2;
                    int dstPixel = dstRowStart + x * 4;

                    if (srcPixel + 1 < pixelData.Length)
                    {
                        ushort pixelValue = (ushort)(pixelData[srcPixel] | (pixelData[srcPixel + 1] << 8));

                        byte r = (byte)((pixelValue & ximage.red_mask) >> GetShiftFromMask(ximage.red_mask));
                        byte g = (byte)((pixelValue & ximage.green_mask) >> GetShiftFromMask(ximage.green_mask));
                        byte b = (byte)((pixelValue & ximage.blue_mask) >> GetShiftFromMask(ximage.blue_mask));

                        r = NormalizeChannel(r, ximage.red_mask);
                        g = NormalizeChannel(g, ximage.green_mask);
                        b = NormalizeChannel(b, ximage.blue_mask);

                        bgraData[dstPixel] = b;
                        bgraData[dstPixel + 1] = g;
                        bgraData[dstPixel + 2] = r;
                        bgraData[dstPixel + 3] = 255;
                    }
                }
            }
            converted = true;
        }

        if (converted)
        {
            Array.Copy(bgraData, pixelData, Math.Min(bgraData.Length, pixelData.Length));
        }
        else
        {
            _logger.LogDebug("No pixel format conversion needed for {bpp} bits per pixel", bpp);
        }
    }

    // helper: find the bit shift for the first set a bit in the mask
    private static int GetShiftFromMask(ulong mask)
    {
        int shift = 0;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            shift++;
        }
        return shift;
    }

    // helper: expand channel value to full 8-bit range
    private static byte NormalizeChannel(byte value, ulong mask)
    {
        int bits = CountBits(mask);
        if (bits == 0) return 0;
        // scale to 8-bit
        return (byte)(value * 255 / ((1 << bits) - 1));
    }

    private static int CountBits(ulong mask)
    {
        int c = 0;
        while (mask != 0)
        {
            c += (int)(mask & 1);
            mask >>= 1;
        }
        return c;
    }

    private bool IsValidDrawable(X11Window drawable, ref uint w, ref uint h)
    {
        try
        {
            // check by getgeometry
            var r = X11Window.None;
            int x = 0, y = 0;
            uint border = 0, depth = 0;
            var result = Xlib.XGetGeometry(
                _display,
                drawable,
                ref r,
                ref x,
                ref y,
                ref w,
                ref h,
                ref border,
                ref depth);
            return result != 0;
        }
        catch
        {
            return false;
        }
    }

    private class X11WindowVisualElement(
        X11WindowBackend backend,
        X11Window windowHandle
    ) : IVisualElement
    {
        public string Id => windowHandle.ToString("X");
        public IVisualElement? Parent
        {
            get
            {
                var root = X11Window.None;
                var parent = X11Window.None;
                Xlib.XQueryTree(backend._display, windowHandle, ref root, ref parent, out _);
                // while (parentHandle != IntPtr.Zero && parentHandle != backend._rootWindow)
                // {
                //     XQueryTree(backend._display, parentHandle, out _, out var current, out _, out var count);
                //     if (count > 1) break;
                //     parentHandle = current;
                // } TODO: compress ok ? current disabled
                if (parent != X11Window.None
                    && parent != backend._rootWindow)
                {
                    return backend.GetWindowElement(parent);
                }
                return null;
            }
        }
        public IEnumerable<IVisualElement> Children
        {
            get
            {
                var root = X11Window.None;
                var parent = X11Window.None;
                Xlib.XQueryTree(backend._display, windowHandle, ref root, ref parent, out var children);
                foreach (var child in children)
                {
                    if (child != X11Window.None)
                    {
                        yield return backend.GetWindowElement(child);
                    }
                }
            }
        }

        private class X11SiblingAccessor(
            X11WindowVisualElement elem,
            X11Window parent,
            X11WindowBackend backend
        ) : VisualElementSiblingAccessor
        {
            private int _childCount;
            private int _selfIndex;
            private readonly List<X11Window> _childArr = [];

            protected override void EnsureResources()
            {
                base.EnsureResources();
                var root = X11Window.None;
                var pparent = X11Window.None;
                Xlib.XQueryTree(backend._display, parent, ref root, ref pparent, out var children);
                _childArr.Clear();
                int index = 0;
                foreach (var child in children)
                {
                    if (child != X11Window.None)
                    {
                        if (child == (X11Window)elem.NativeWindowHandle)
                        {
                            _selfIndex = index;
                        }
                        _childArr.Add(child);
                        index++;
                    }
                }
                _childCount = index;
            }


            protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
            {
                for (var i = _selfIndex + 1; i < _childCount; i++)
                {
                    var child = _childArr[i];
                    yield return backend.GetWindowElement(child);
                }
            }

            protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
            {
                for (var i = _selfIndex - 1; i >= 0; i--)
                {
                    var child = _childArr[i];
                    yield return backend.GetWindowElement(child);
                }
            }
        }

        public VisualElementSiblingAccessor SiblingAccessor
        {
            get
            {
                var root = X11Window.None;
                var parent = X11Window.None;
                Xlib.XQueryTree(backend._display, windowHandle, ref root, ref parent, out _);
                return new X11SiblingAccessor(this, parent, backend);
            }
        }

        public VisualElementType Type => VisualElementType.TopLevel;
        public VisualElementStates States => VisualElementStates.None;
        public string? Name
        {
            get
            {
                try
                {
                    var name = "";
                    return Xlib.XFetchName(backend._display, windowHandle, ref name) != Status.Failure ? name : null;
                }
                catch
                {
                    return null;
                }
            }
        }
        public PixelRect BoundingRectangle => backend.XGetWindowBound((X11Window)NativeWindowHandle);
        public int ProcessId
        {
            get
            {
                var pid = 0;
                var win = this;
                while (win != null)
                {
                    pid = backend.XGetWindowPid((X11Window)win.NativeWindowHandle);
                    if (pid != 0)
                    {
                        break;
                    }
                    win = (X11WindowVisualElement?)win.Parent;
                }
                return pid;
            }
        }
        public IntPtr NativeWindowHandle => (IntPtr)windowHandle;
        public string? GetText(int maxLength = -1) => Name;

        public string? GetSelectionText()
        {
            return null;
        }

        public void Invoke()
        {
            // no op
        }

        public void SetText(string text)
        {
            // no op
        }

        public void SendShortcut(KeyboardShortcut shortcut)
        {
            Xlib.XSetInputFocus(backend._display, windowHandle, RevertFocus.RevertToParent, CurrentTime);
            backend.SendKeyboardShortcut(shortcut);
        }

        public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(backend.Capture(this, BoundingRectangle.WithX(0).WithY(0))).WaitAsync(cancellationToken);
    }

    private class X11ScreenVisualElement(
        X11WindowBackend backend,
        int index
    ) : IVisualElement
    {
        public string Id => $"Screen {index}";
        public IVisualElement? Parent => null;
        public IEnumerable<IVisualElement> Children
        {
            get
            {
                var rootWindow = Xlib.XRootWindow(backend._display, index);
                var root = X11Window.None;
                var parent = X11Window.None;
                Xlib.XQueryTree(backend._display, rootWindow, ref root, ref parent, out var children);
                foreach (var child in children)
                {
                    if (child != X11Window.None)
                    {
                        yield return backend.GetWindowElement(child);
                    }
                }
            }
        }

        private class X11ScreenSiblingAccessor(X11WindowBackend backend, int index) : VisualElementSiblingAccessor
        {

            protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
            {
                var count = XScreenCount(backend._display);
                for (var i = index + 1; i < count; i++)
                {
                    yield return new X11ScreenVisualElement(backend, i);
                }
            }

            protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
            {
                for (var i = index - 1; i >= 0; i--)
                {
                    yield return new X11ScreenVisualElement(backend, i);
                }
            }
        }

        public VisualElementSiblingAccessor SiblingAccessor => new X11ScreenSiblingAccessor(backend, index);

        public VisualElementType Type => VisualElementType.Screen;
        public VisualElementStates States => VisualElementStates.None;
        public string Name => Id;
        public PixelRect BoundingRectangle
        {
            get
            {
                var display = backend._display;
                int height = XDisplayHeight(display, index);
                int width = XDisplayWidth(display, index);
                return new PixelRect(0, 0, width, height);
            }
        }
        public int ProcessId => 0;
        public IntPtr NativeWindowHandle => (IntPtr)Xlib.XRootWindow(backend._display, index);
        public string GetText(int maxLength = -1) => Name;
        public string? GetSelectionText() => null;

        public void Invoke() { }

        public void SetText(string text) { } // no op

        public void SendShortcut(KeyboardShortcut shortcut) { } // no op

        public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(backend.Capture(this, BoundingRectangle.WithX(0).WithY(0))).WaitAsync(cancellationToken);
    }

    private void XThreadProcessEvent(IntPtr eventPtr)
    {
        var type = GetEventType(eventPtr);

        switch (type)
        {
            case EventType.KeyDown:
            case EventType.KeyUp:
            {
                var evKey = Marshal.PtrToStructure<XKeyEvent>(eventPtr);
                var state = evKey.state;
                var norm = state & (uint)(~(KeyButtonMask.LockMask | KeyButtonMask.Mod2Mask));
                var key = KeycodeToAvaloniaKey((KeyCode)evKey.keycode);
                var modifiers = KeyStateToModifier(norm);
                if (_keyboardHook != null)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        _keyboardHook?.Invoke(new KeyboardShortcut(key, modifiers), type);
                    });
                }
                if (type == EventType.KeyDown)
                {
                    var keycode = (KeyCode)evKey.keycode;
                    // iterate over a snapshot to avoid concurrent-enumeration / ToString recursion issues
                    foreach (var kv in _regs)
                    {
                        var info = kv.Value;
                        if (info.Keycode == keycode && info.Mods == norm)
                        {
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                info.Handler();
                            });
                        }
                    }
                }
            }
                break;
            case EventType.MouseDown:
            case EventType.MouseUp:
            case EventType.MouseDrag:
            {
                var buttonEvent = Marshal.PtrToStructure<XButtonEvent>(eventPtr);
                if (_mouseHook != null)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        _mouseHook?.Invoke(
                            new PixelPoint(buttonEvent.x_root, buttonEvent.y_root),
                            type
                        );
                    });
                }
            }
                break;
        }
    }

    private void XThreadMain()
    {
        // Error Callback, X11 will call this when Internal Error throwed
        Xlib.XSetErrorHandler(OnXError);
        Lock evLock = new Lock();
        var evPtr = Marshal.AllocHGlobal(256);
        var buf = new byte[4];
        try
        {
            var xfd = Xlib.XConnectionNumber(_display);
            var fds = new pollfd[2];
            fds[0].fd = xfd;
            fds[0].events = LibC.POLLIN;
            fds[1].fd = _wakePipeR;
            fds[1].events = LibC.POLLIN;


            while (_running || !_ops.IsCompleted)
            {
                // process pending ops first
                while (_ops.TryTake(out var op))
                {
                    try { op(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "X op failed");
                    }
                }

                // wait for either X fd or wake pipe
                unsafe
                {
                    fixed (pollfd* pfds = fds)
                    {
                        var rc = LibC.poll(pfds, (uint)fds.Length, -1);
                        if (rc <= 0) continue;
                        // wake pipe signaled -> drain and process ops
                        if ((fds[1].revents & LibC.POLLIN) != 0)
                        {
                            try
                            {
                                while (true)
                                {
                                    // _logger.LogDebug("About to read from wakePipe fd={fd}", _wakePipeR);
                                    int r;
                                    fixed (byte* pbuf = buf)
                                    {
                                        r = (int)LibC.read(_wakePipeR, pbuf, buf.Length);
                                    }
                                    // _logger.LogDebug("Read from wakePipe fd={fd} returned={r}", _wakePipeR, r);
                                    if (r > 0) continue;
                                    if (r == 0) break;
                                    var errno = Marshal.GetLastPInvokeError();
                                    if (errno == LibC.EAGAIN) break;
                                    _logger.LogWarning("Unexpected read error from wake pipe: errno={errno}", errno);
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error draining wake pipe");
                            }
                            while (_ops.TryTake(out var op))
                            {
                                try { op(); }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "X op failed");
                                }
                            }
                        }

                        // X fd signaled -> handle pending X events
                        if ((fds[0].revents & LibC.POLLIN) != 0)
                        {
                            while (Xlib.XPending(_display) > 0)
                            {
                                lock (evLock)
                                {
                                    Xlib.XNextEvent(_display, evPtr);
                                    XThreadProcessEvent(evPtr);
                                }
                            }
                        }
                    }
                }
            }
        }
        finally { Marshal.FreeHGlobal(evPtr); }
    }

    private void XThreadAction(Action action)
    {
        _ops.Add(action);
        // wake x thread
        try
        {
            if (_wakePipeW != -1)
            {
                var b = new byte[] { 1 };
                unsafe
                {
                    fixed (byte* pb = b)
                    {
                        var wr = LibC.write(_wakePipeW, pb, b.Length);
                        _logger.LogDebug("Wrote wake byte to pipe (fd={fd}) result={r}", _wakePipeW, wr);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write wake pipe");
        }
    }

    /// <summary>
    /// X11 Error Handler
    /// If Error occurs inside X11 interface, this callback is invoked.
    /// </summary>
    private int OnXError(IntPtr display, ref XErrorEvent ev)
    {
        try
        {
            string text;
            try
            {
                var buffer = new byte[256]; // X11 Error Text Give 256 Bytes
                unsafe
                {
                    fixed (byte* buff = buffer)
                    {
                        Xlib.XGetErrorText(display, ev.error_code, (IntPtr)buff, buffer.Length);
                    }
                }
                text = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get X error text for code {code}", ev.error_code);
                text = $"Unknown error code {ev.error_code}";
            }
            _logger.LogError(
                "X Error: code={code}({errorName}) request={req}({reqName}) minor={minor} resource={res} text={text}",
                ev.error_code,
                GetErrorCodeName(ev.error_code),
                ev.request_code,
                GetRequestCodeName(ev.request_code),
                ev.minor_code,
                ev.resourceid,
                text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle X error");
        }
        return 0;
    }

    private class RegInfo
    {
        public KeyCode Keycode { get; init; }
        public uint Mods { get; init; }
        public Action Handler { get; init; } = () => { };
    }
}