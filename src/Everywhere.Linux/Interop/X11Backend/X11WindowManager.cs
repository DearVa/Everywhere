using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Everywhere.Interop;
using Everywhere.Extensions;
using Microsoft.Extensions.Logging;
using Tmds.Linux;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Responsible for window tree traversal, PID queries, focus management and hit testing.
/// </summary>
public sealed class X11WindowManager
{
    private readonly ILogger _logger;
    private readonly X11Context _context;
    private readonly X11CoreServices _services;
    
    private readonly int _currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
    public X11Window ScanSkipWindow { get; set; } = X11Window.None;

    public X11WindowManager(ILogger logger, X11Context context, X11CoreServices services)
    {
        _logger = logger;
        _context = context;
        _services = services;
    }

    public int GetWindowPid(X11Window window)
    {
        if (window == X11Window.None) return 0;

        int pid = 0;

        // local helper to try read _NET_WM_PID from a window
        bool TryReadPidFromWindow(X11Window w)
        {
            pid = 0;
            _services.GetProperty(w, "_NET_WM_PID", 1, Atom.Cardinal, (_, _, nItems, _, prop) =>
            {
                if (nItems > 0 && prop != IntPtr.Zero) pid = Marshal.ReadInt32(prop);
            });
            return pid != 0;
        }
        // 1) Try read PID directly from the window
        if (TryReadPidFromWindow(window)) return pid;

        // 2) Walk up the parent chain to look for a PID on an ancestor
        try
        {
            var root = X11Window.None;
            var parent = X11Window.None;
            Xlib.XQueryTree(_context.Display, window, ref root, ref parent, out var children);
            var current = parent;
            while (current != X11Window.None && current != _context.RootWindow)
            {
                if (TryReadPidFromWindow(current)) return pid;
                Xlib.XQueryTree(_context.Display, current, ref root, ref parent, out children);
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
                Xlib.XQueryTree(_context.Display, w, ref root, ref parent, out var children);
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
        X11Native.XQueryKeymap(_context.Display, keymap);
        
        // The modifier is considered pressed if either the left or right key is pressed.
        return Check(keymap, keys[0]) || Check(keymap, keys[1]);

        bool Check(byte[] map, Key key)
        {
            var keycode = _services.XKeycode(key);
            if (keycode == 0) return false;

            var byteIndex = (byte)keycode / 8;
            var bitIndex = (byte)keycode % 8;
            var pressed = (map[byteIndex] >> bitIndex) & 1;
            return pressed == 1;
        }
    }

    private bool TryReadPid(X11Window w, out int pid)
    {
        int foundPid = 0;
        _services.GetProperty(w, "_NET_WM_PID", 1, Atom.Cardinal, (_, _, nItems, _, prop) =>
        {
            if (nItems > 0 && prop != IntPtr.Zero) foundPid = Marshal.ReadInt32(prop);
        });
        pid = foundPid;
        return pid != 0;
    }

    public X11Window GetWindowAtPoint(int x, int y)
    {
        return GetWindowAtPointInternal(_context.RootWindow, x, y);
    }

    private X11Window GetWindowAtPointInternal(X11Window window, int x, int y)
    {
        if (window == ScanSkipWindow) return X11Native.ScanSkipWindow;
        
        Xlib.XGetWindowAttributes(_context.Display, window, out var attr);
        if (GetWindowPid(window) == _currentProcessId) return X11Window.None;
        if ((X11Native.MapState)attr.map_state != X11Native.MapState.IsViewable || attr.override_redirect != false) return X11Window.None;

        // Recursion logic for children (Backward iteration)
        X11Window parent = X11Window.None;
        X11Window root = X11Window.None;
        Xlib.XQueryTree(_context.Display, window, ref root, ref parent, out var children);
        var rx = x - attr.x;
        var ry = y - attr.y;
        
        foreach (var child in Enumerable.Reverse(children))
        {
            if (child != X11Window.None)
            {
                var sub = GetWindowAtPointInternal(child, rx, ry);
                if (sub == X11Native.ScanSkipWindow){
                     if (window != _context.RootWindow) return X11Native.ScanSkipWindow;
                     else continue;
                }
                if (sub != X11Window.None) return sub;
            }
        }

        if (x >= attr.x && x < attr.x + attr.width && y >= attr.y && y < attr.y + attr.height) return window;
        return X11Window.None;
    }

    public PixelRect GetWindowBounds(X11Window window)
    {
        if (window == X11Window.None) return default;
        Xlib.XGetWindowAttributes(_context.Display, window, out var attr);
        
        if (_services.TranslateCoordinates(window, _context.RootWindow, 0, 0, out int absX, out int absY))
            return new PixelRect(absX, absY, (int)attr.width, (int)attr.height);

        return new PixelRect(attr.x, attr.y, (int)attr.width, (int)attr.height);
    }

    public void ForEachTopLevelWindow(Action<X11Window> action)
    {
        _services.GetProperty(_context.RootWindow, "_NET_CLIENT_LIST", -1, Atom.Window, (type, format, count, _, data) =>
        {
            if (type == Atom.Window && format == 32)
            {
                for (var i = 0; i < (int)count; i++)
                {
                    var ptr = new IntPtr(data.ToInt64() + i * sizeof(long)); // X11Window is usually long on 64bit
                    var w = (X11Window)Marshal.ReadInt64(ptr);
                    if (w != X11Window.None) action(w);
                }
            }
        });
    }
    public void SetFocusable(X11Window wnd, bool focusable)
    {
        var atomHints = _services.GetAtom("WM_HINTS");
        if (atomHints == Atom.None) return;
        var hints = new ulong[] { 1u << 0, focusable ? 1u : 0u }; // InputHint
        unsafe { fixed (ulong* p = hints) Xlib.XChangeProperty(_context.Display, wnd, atomHints, atomHints, 32, (int)PropertyMode.Replace, (IntPtr)p, 2); }
        Xlib.XFlush(_context.Display);
    }

    public void SetHitTestVisible(X11Window window, bool visible, ushort width, ushort height)
    {
        try
        {
            if (_context.Display == IntPtr.Zero) return;

            if (X11Native.XFixesQueryExtension(_context.Display, out _, out _) != 0)
            {
                if (visible)
                {
                    IntPtr fullRegion = X11Native.XFixesCreateRegion(
                        _context.Display,
                        new[]
                        {
                            new X11Native.XRectangle { x = 0, y = 0, width = width, height = height }
                        },
                        1);
                    X11Native.XFixesSetWindowShapeRegion(_context.Display, window, (int)X11Native.ShapeKind.Input, 0, 0, fullRegion);
                    X11Native.XFixesDestroyRegion(_context.Display, fullRegion);
                }
                else
                {
                    IntPtr emptyRegion = X11Native.XFixesCreateRegion(
                        _context.Display,
                        [],
                        0);
                    X11Native.XFixesSetWindowShapeRegion(_context.Display, window, (int)X11Native.ShapeKind.Input, 0, 0, emptyRegion);
                    X11Native.XFixesDestroyRegion(_context.Display, emptyRegion);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("X11 SetHitTestVisible {visible} Failed: {Message}", visible, ex.Message);
        }
    }

    public void SetOverrideRedirect(X11Window window, bool redirect)
    {
        try
        {
            var swa = new X11Native.XSetWindowAttributes
            {
                override_redirect = redirect ? 1 : 0
            };

            const ulong CWOverrideRedirect = 1UL << 9; // CWOverrideRedirect from X11/X.h
            X11Native.XChangeWindowAttributes(_context.Display, window, CWOverrideRedirect, ref swa);
            Xlib.XFlush(_context.Display);
        }
        catch (Exception ex)
        {
            _logger.LogError("X11 SetOverrideRedirect Failed: {Message}", ex.Message);
        }
    }

    public bool GetEffectiveVisible(X11Window window)
    {
        Xlib.XGetWindowAttributes(_context.Display, window, out var attr);
        if ((X11Native.MapState)attr.map_state != X11Native.MapState.IsViewable)
        {
            return false;
        }
        var xaAtom = _context.AtomCache.GetAtom("ATOM", onlyIfExists: false);
        if (xaAtom == Atom.None) return false;

        var hiddenAtom = _context.AtomCache.GetAtom("_NET_WM_STATE_HIDDEN", onlyIfExists: false);
        if (hiddenAtom == Atom.None) return false;

        bool isHidden = false;

        _services.GetProperty(window, "_NET_WM_STATE", 0, xaAtom, (type, format, count, _, data) =>
        {
            if (type == xaAtom && format == 32)
            {
                for (var i = 0; i < (int)count; i++)
                {
                    var ptr = new IntPtr(data.ToInt64() + i * sizeof(long)); // X11Window is usually long on 64bit
                    var atom = (Atom)Marshal.ReadInt64(ptr);
                    if (atom == hiddenAtom)
                    {
                        isHidden = true;
                        break;
                    }
                }
            }
        });
        return !isHidden;
    }

    public bool AnyModelDialogOpened(X11Window window)
    {
        var dialogFound = false;
        ForEachTopLevelWindow((win) =>
        {
            if (win == window)
            {
                dialogFound = true;
            }
        });
        return dialogFound;
    }
}
