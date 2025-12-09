using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Everywhere.Common;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop;


public sealed partial class X11WindowBackend : ILinuxWindowBackend, ILinuxEventHelper
{
    private IntPtr _display;
    private IntPtr _rootWindow;
    private IntPtr _scanSkipWindowHandle = IntPtr.Zero;
    private readonly ILogger<X11WindowBackend> _logger = ServiceLocator.Resolve<ILogger<X11WindowBackend>>();
    private readonly ConcurrentDictionary<int, RegInfo> _regs = new();
    private int _nextId = 1;
    private readonly BlockingCollection<Action> _ops = new(new ConcurrentQueue<Action>());
    private Thread? _xThread;
    private volatile bool _running;
    private Action<KeyboardShortcut, EventType>? _keyboardHook;
    private Action<PixelPoint, EventType>? _mouseHook;
    private int _wakePipeR = -1;
    private int _wakePipeW = -1;
    private readonly ConcurrentDictionary<IntPtr, IVisualElement> _windowCache = new();

    public X11WindowBackend()
    { 
        XInitThreads();
        _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero) return;
        _rootWindow = XDefaultRootWindow(_display);
        // select key events on root window so we receive KeyPress/KeyRelease
        XSelectInput(_display, _rootWindow,
            KeyPressMask | KeyReleaseMask | ButtonPressMask | ButtonReleaseMask | ButtonMotionMask | FocusChangeMask);
        // create wake pipe
        try
        {
            var fds = new int[2];
            if (pipe(fds) == 0)
            {
                _wakePipeR = fds[0];
                _wakePipeW = fds[1];
                try
                {
                    // set non-blocking
                    var flags = fcntl(_wakePipeR, F_GETFL, 0);
                    fcntl(_wakePipeR, F_SETFL, flags | O_NONBLOCK);
                    fcntl(_wakePipeW, F_SETFL, flags | O_NONBLOCK);
                    // set close-on-exec
                    fcntl(_wakePipeR, F_SETFD, FD_CLOEXEC);
                    fcntl(_wakePipeW, F_SETFD, FD_CLOEXEC);
                }
                catch (Exception ex)
                {
                    _logger.LogError("XThread fcntl Error: {ex}", ex.Message);
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
                write(_wakePipeW, b, 1);
            }
            if (_xThread?.IsAlive == true) _xThread.Join(500);
            if (_display != IntPtr.Zero)
            {
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }
            if (_wakePipeR != -1) close(_wakePipeR);
            if (_wakePipeW != -1) close(_wakePipeW);
        }
        catch (Exception ex)
        {
            _logger.LogError("X11 Backend Close Error: {ex}", ex.Message);
        }
    }

    private int XKeycode(Key key)
    {
        var ks = XStringToKeysym(key.ToString());
        int keycode = 0;
        if (ks != UIntPtr.Zero) keycode = XKeysymToKeycode(_display, ks);
        if (keycode == 0)
        {
            ks = XStringToKeysym(key.ToString().ToUpperInvariant());
            if (ks != UIntPtr.Zero) keycode = XKeysymToKeycode(_display, ks);
        }
        return keycode;
    }
    
    public bool GetKeyState(KeyModifiers keyModifier)
    {
        // List testList = [, KeyModifiers.Control, KeyModifiers.Shift, KeyModifiers.Meta];
        Dictionary<KeyModifiers, List<Key>> testKey = new()
            {
                [KeyModifiers.Alt] = [Key.LeftAlt, Key.RightAlt],
                [KeyModifiers.Control] = [Key.LeftCtrl, Key.RightCtrl],
                [KeyModifiers.Shift] = [Key.LeftShift, Key.RightShift],
                [KeyModifiers.Meta] = [Key.LWin, Key.RWin],
            };
        var keymap = new byte[32];
        XQueryKeymap(_display, keymap);
        bool state = true;

        bool check(byte[] map, Key key)
        {
            var keycode = XKeycode(key);
            if (keycode == 0) return false;

            var byteIndex = keycode / 8;
            var bitIndex = keycode % 8;
            var pressed = (map[byteIndex] >> bitIndex) & 1;
            return pressed == 1;
        }

        foreach (var key in testKey)
        {
            state &= (check(keymap, key.Value[0]) | check(keymap, key.Value[1]) );
        }
        
        return state;
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
                if (hotkey.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= ShiftMask;
                if (hotkey.Modifiers.HasFlag(KeyModifiers.Control)) mods |= ControlMask;
                if (hotkey.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= Mod1Mask;
                if (hotkey.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= Mod4Mask;
                int keycode = XKeycode(hotkey.Key);
                if (keycode == 0)
                {
                    tcs.SetResult(0);
                    return;
                }

                var variants = new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask };
                foreach (var v in variants) XGrabKey(_display, keycode, mods | v, _rootWindow, 0, GrabModeAsync, GrabModeAsync);
                XFlush(_display);
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
            var variants = new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask };
            foreach (var v in variants) XUngrabKey(_display, info.Keycode, info.Mods | v, _rootWindow);
            XFlush(_display);

        });
    }

    public void GrabKeyHook(Action<KeyboardShortcut, EventType> hook)
    {
        _keyboardHook = hook;
        XThreadAction(() =>
        {
            XGrabKeyboard(_display, _rootWindow,
                0, GrabModeAsync, GrabModeAsync, CurrentTime);
            XFlush(_display);
        });
    }

    public void UngrabKeyHook()
    {
        _keyboardHook = null;
        XThreadAction(() =>
        {
            XUngrabKeyboard(_display, _rootWindow);
            // In X11 Ungrab do not remove focus, set focus to root
            XSetInputFocus(_display, _rootWindow, RevertToParent, CurrentTime);
            XFlush(_display);
        });
    }

    public void SendKeyboardShortcut(KeyboardShortcut shortcut)
    {
        int Modifier2Keycode(KeyModifiers mod)
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

        List<int> keycodes = [];
        List<KeyModifiers> mods = [KeyModifiers.Meta, KeyModifiers.Control, KeyModifiers.Shift, KeyModifiers.Alt];
        keycodes.AddRange(from m in mods where shortcut.Modifiers.HasFlag(m) 
            select Modifier2Keycode(m) into code where code != 0 select code);
        keycodes.Add(XKeycode(shortcut.Key));
        XThreadAction(() =>
        {
            foreach(var k in keycodes)
            {
                if (k == 0)
                {
                    _logger.LogWarning("invalid key?");
                    continue;
                }
                XTestFakeKeyEvent(_display, k, 1, 0);
                Thread.Sleep(10);
            }
            keycodes.Reverse();
            foreach(var k in keycodes)
            {
                if (k == 0)
                {
                    _logger.LogWarning("invalid key?");
                    continue;
                }
                XTestFakeKeyEvent(_display, k, 0, 0);
                Thread.Sleep(10);
            }
            XFlush(_display);
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
            XGrabPointer(_display, _rootWindow,
                0, ButtonPressMask | ButtonReleaseMask | ButtonMotionMask,
                GrabModeAsync, GrabModeAsync, IntPtr.Zero, IntPtr.Zero, CurrentTime);
            XFlush(_display);
        });
    }

    public void UngrabMouseHook()
    {
        _mouseHook = null;
        XThreadAction(() =>
        {
            XUngrabPointer(_display, CurrentTime);
            XFlush(_display);
        });
    }

    public Key KeycodeToAvaloniaKey(uint keycode)
    {
        try
        {
            var ks = XKeycodeToKeysym(_display, (int)keycode, 0);
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
        IntPtr window,
        string propertyName,
        long length,
        IntPtr reqType,
        Action<IntPtr, int, ulong, ulong, IntPtr> propertyCallback)
    {// actualType, actualFormat, nitems, bytesAfter, data
        IntPtr atom = XInternAtom(_display, propertyName, 1);
        if (atom == IntPtr.Zero) return;

        XGetWindowProperty(_display, window, atom,
            0, length, 0, reqType,
            out var actualType, out var actualFormat,
            out var nItems, out var bytesAfter, out var prop);
        try
        {
            propertyCallback(actualType, actualFormat, nItems, bytesAfter, prop);
        }
        finally
        {
            XFree(prop);
        }
    }

    private int XGetWindowPid(IntPtr window)
    {
        int pid = 0;
        XGetProperty(window, "_NET_WM_PID", 1, 
            XA_CARDINAL, (_, _, nItems, _, prop) =>
        {
            if (nItems == 0 || prop == IntPtr.Zero)
                return;
            pid = Marshal.ReadInt32(prop);
        });
        return pid;
    }
    
    private void XForEachTopWindow(Action<IntPtr> handle)
    {
        XGetProperty(_rootWindow, "_NET_CLIENT_LIST", -1
            , XA_WINDOW,
            (type, format, count, _, data) =>
            {
                if (type == XA_WINDOW && format == 32)
                {
                    for (var i = 0; i < ((int)count); i++)
                    {
                        var client = 
                            Marshal.ReadIntPtr(data, i * IntPtr.Size);
                        if (client != IntPtr.Zero)
                        {
                            handle(client);
                        }
                    }
                }
            });
    }

    private IVisualElement GetWindowElement(IntPtr window, Func<IVisualElement> maker)
    {
        // return cached if any
        if (_windowCache.TryGetValue(window, out var cachedElement))
        {
            // _logger.LogDebug("Using cached window element for {window}", window.ToString("X"));
            return cachedElement;
        }

        // create
        var element = maker();
        // _logger.LogInformation("Creating window element for {window}", window.ToString("X"));
        _windowCache[window] = element;
        return element;
    }
    
    private IVisualElement GetWindowElement(IntPtr window)
    {
        return GetWindowElement(window, () => new X11WindowVisualElement(this, window));
    }

    public IVisualElement? GetFocusedWindowElement()
    {
        try
        {
            if (_display == IntPtr.Zero) return null;
            XGetInputFocus(_display, out var focusWindow, out _);
            if (focusWindow == IntPtr.Zero) return null;
            return GetWindowElement(focusWindow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetFocusedWindowElement failed");
            return null;
        }
    }

    private IntPtr GetWindowAtPoint(IntPtr window, int x, int y)
    {
        if (window == _scanSkipWindowHandle)
        {
            return IntPtr.MaxValue;
        }
        XGetWindowAttributes(_display, window, out var attr);
        if (attr.map_state != IsViewable || attr.override_redirect == 1)// skip hidden
        {
            return IntPtr.Zero;
        }
        var rx = x - attr.x;
        var ry = y - attr.y;
        XQueryTree(_display, window, out _, out _, out var childrenPtr, out var count);
        for (var i = count - 1; i >= 0; i--) // backward iter to find the topmost
        {
            var child = Marshal.ReadIntPtr(childrenPtr, i * IntPtr.Size);
            if (child != IntPtr.Zero)
            {
                var sub = GetWindowAtPoint(child, rx, ry);
                if (sub == IntPtr.MaxValue)
                {
                    if (window != _rootWindow)
                    {
                        return IntPtr.MaxValue;
                    }
                    continue;
                }
                if (sub != IntPtr.Zero)
                {
                    _logger.LogInformation("< {window}", 
                        window.ToString("X"));
                    return sub;
                }
            }
        }
        if (x < attr.x || y < attr.y ||
            x >= attr.x + attr.width || y >= attr.y + attr.height)
        {
            return IntPtr.Zero;
        }
        _logger.LogInformation("<< get {window}", 
            window.ToString("X"));
        return window;
    }

    public IVisualElement GetWindowElementAt(PixelPoint point)
    {
        var child = GetWindowAtPoint(_rootWindow, point.X, point.Y);
        // if child = 0 fallback to root
        if (child == IntPtr.Zero)
        {
            _logger.LogDebug("XQueryPointer returned child=0, using root window");
            child = _rootWindow;
        }

        _logger.LogInformation("GetWindowElementAt at ({x},{y}) -> window {window}",
            point.X, point.Y, child.ToString("X"));

        return GetWindowElement(child);
    }
    
    public IVisualElement? GetWindowElementByPid(int pid)
    {
        IntPtr target = IntPtr.Zero;
        XForEachTopWindow((window) =>
        {
            if (XGetWindowPid(window) == pid)
            {
                target = window;
            }
        });
        if (target != IntPtr.Zero)
        {
            return GetWindowElement(target);
        }
        return null;
    }

    public IVisualElement GetScreenElement()
    {
        var screenIdx = XDefaultScreen(_display);
        var screenWindow = XRootWindow(_display, screenIdx);
        return GetWindowElement(screenWindow, () => new X11ScreenVisualElement(this, screenIdx));
    }

    public void SetFocusable(Window window, bool focusable)
    {
        try
        {
            var ph = window.TryGetPlatformHandle();
            if (ph is null) return;
            IntPtr wnd = ph.Handle;

            if (_display == IntPtr.Zero) return;

            IntPtr atomHints = XInternAtom(_display, "WM_HINTS", 0);
            if (atomHints != IntPtr.Zero)
            {
                // WM_HINTS：flags(32 bit) + input(32 bit) + Others...
                var hints = new ulong[2];
                hints[0] = 1u << 0; // InputHint flag
                hints[1] = (focusable) ? 1u : 0u;
                XChangeProperty(_display, wnd, atomHints, XInternAtom(_display, "WM_HINTS", 0), 32, PropModeReplace, hints, 2);
            }

            XFlush(_display);
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
            IntPtr wnd = ph.Handle;

            if (_display == IntPtr.Zero) return;

            XWindowAttributes attrs = new();
            attrs.override_redirect = visible ? 0 : 1;
            XChangeWindowAttributes(_display, wnd, CW.OverrideRedirect, ref attrs);

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
                    XFixesSetWindowShapeRegion(_display, wnd, ShapeInput, 0, 0, IntPtr.Zero);
                }
            }
        }
        catch(Exception ex)
        {
            _logger.LogError("X11 SetHitTestVisible {visible} Failed: {Message}", visible, ex.Message);
        }
    }

    public bool GetEffectiveVisible(Window window)
    {
        var ph = window.TryGetPlatformHandle();
        if (ph is null) return false;
        IntPtr wnd = ph.Handle;
        XGetWindowAttributes(_display, wnd, out var attr);
        if (attr.map_state != IsViewable)
        {
            return false;
        }
        var xaAtom = XInternAtom(_display, "ATOM", 0);
        if (xaAtom == IntPtr.Zero) return false;

        var hiddenAtom = XInternAtom(_display, "_NET_WM_STATE_HIDDEN", 0);
        if (hiddenAtom == IntPtr.Zero) return false;

        bool isHidden = false;

        XGetProperty(
            window: wnd,
            propertyName: "_NET_WM_STATE",
            length: -1,
            reqType: xaAtom,
            propertyCallback: (actualType, actualFormat, nItems, bytesAfter, data) =>
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

        XFlush(_display);
    }

    public bool AnyModelDialogOpened(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return false;
        var ownerWindow = handle.Handle;
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
        XQueryPointer(_display, _rootWindow, out _, out _,
                out var rootX, out var rootY, out _, out _, out _);
        return new PixelPoint(rootX, rootY);
    }

    public void WindowPickerHook(Window overlay, Action<PixelPoint, EventType> hook)
    {
        var handle = overlay.TryGetPlatformHandle()?.Handle;
        _scanSkipWindowHandle = handle?? IntPtr.Zero;
        GrabMouseHook(hook);
    }
    

    public Bitmap Capture(IVisualElement? window, PixelRect rect)
    {
        try
        {

            IntPtr wnd = _rootWindow;
            PixelRect captureRect = rect;
            if (window != null)
            {
                wnd = window.NativeWindowHandle;
                captureRect = window.BoundingRectangle.WithX(0).WithY(0);
            }
            if (wnd == IntPtr.Zero)
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
            _logger.LogError(ex, "Capture(element) failed for element {elementType} {elementId}",
                window?.Type.ToString() ?? "null", window?.Id ?? "null");
            throw;
        }
    }

    private Bitmap StandardXGetImageCapture(IntPtr drawable, PixelRect rect)
    {
        if (!IsValidDrawable(drawable))
        {
            _logger.LogError("Invalid drawable: {drawable}", drawable.ToString("X"));
            throw new InvalidOperationException("Invalid drawable");
        }

        _logger.LogDebug("Calling XGetImage with drawable={drawable}, x={x}, y={y}, w={w}, h={h}",
            drawable.ToString("X"), rect.X, rect.Y, rect.Width, rect.Height);

        var image = XGetImage(_display, drawable, rect.X, rect.Y,
            (uint)rect.Width, (uint)rect.Height, AllPlanes, ZPixmap);
        if (image == IntPtr.Zero)
        {
            _logger.LogError("XGetImage returned null for drawable {drawable} at ({x},{y}) size {width}x{height}",
                drawable.ToString("X"), rect.X, rect.Y, rect.Width, rect.Height);
            throw new InvalidOperationException($"XGetImage failed for drawable {drawable}");
        }

        try
        {
            var ximage = Marshal.PtrToStructure<XImage>(image);

            // check pixel format
            _logger.LogInformation("XImage format: depth={depth}, bits_per_pixel={bpp}, bytes_per_line={bpl}",
                ximage.depth, ximage.bits_per_pixel, ximage.bytes_per_line);

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
            XDestroyImage(image);
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

                        bgraData[dstPixel]     = b;
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
                        ushort pixelValue = (ushort)(pixelData[srcPixel] | (pixelData[srcPixel+1] << 8));

                        byte r = (byte)((pixelValue & ximage.red_mask) >> GetShiftFromMask(ximage.red_mask));
                        byte g = (byte)((pixelValue & ximage.green_mask) >> GetShiftFromMask(ximage.green_mask));
                        byte b = (byte)((pixelValue & ximage.blue_mask) >> GetShiftFromMask(ximage.blue_mask));

                        r = NormalizeChannel(r, ximage.red_mask);
                        g = NormalizeChannel(g, ximage.green_mask);
                        b = NormalizeChannel(b, ximage.blue_mask);

                        bgraData[dstPixel]     = b;
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
    
    // helper: find the bit shift for the first set bit in the mask
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

    private bool IsValidDrawable(IntPtr drawable)
    {
        try
        {
            // 尝试获取drawable的几何信息来验证其有效性
            var result = XGetGeometry(_display, drawable,
                out _, out _, out _, out _, out _, out _, out _);
            return result != 0;
        }
        catch
        {
            return false;
        }
    }

    private class X11WindowVisualElement(
        X11WindowBackend backend,
        IntPtr windowHandle
    ) : IVisualElement
    {
        public string Id => windowHandle.ToString("X");
        public IVisualElement? Parent
        {
            get
            {
                XQueryTree(backend._display, windowHandle, out _, out var parentHandle, out _, out _);
                // while (parentHandle != IntPtr.Zero && parentHandle != backend._rootWindow)
                // {
                //     XQueryTree(backend._display, parentHandle, out _, out var current, out _, out var count);
                //     if (count > 1) break;
                //     parentHandle = current;
                // } TODO: compress ok ? current disabled
                if (parentHandle != IntPtr.Zero
                    && parentHandle != backend._rootWindow)
                {
                    return backend.GetWindowElement(parentHandle);
                }
                return null;
            }
        }
        public IEnumerable<IVisualElement> Children
        {
            get
            {
                XQueryTree(backend._display, windowHandle, out _, out _, out var childrenPtr, out var count);
                for (var i = 0; i < count; i++)
                {
                    var child = Marshal.ReadIntPtr(childrenPtr, i * IntPtr.Size);
                    if (child != IntPtr.Zero)
                    {
                        yield return backend.GetWindowElement(child);
                    }
                }
                XFree(childrenPtr);
            }
        }
        private class X11SiblingAccessor(
            X11WindowVisualElement elem, 
            IntPtr parent, 
            X11WindowBackend backend): VisualElementSiblingAccessor
        {
            private List<IntPtr> _childArr = [];
            private int _childCount = 0;
            private int _selfIndex = 0;
            
            protected override void EnsureResources()
            {
                base.EnsureResources();
                XQueryTree(backend._display, parent, out _, out _, out var childrenPtr, out var count);
                _childCount = count;
                _childArr.Clear();
                int index = 0;
                for (var i = 0; i < count; i++)
                {
                    var child = Marshal.ReadIntPtr(childrenPtr, i * IntPtr.Size);
                    if (child != IntPtr.Zero)
                    {
                        if (child == elem.NativeWindowHandle)
                        {
                            _selfIndex = index;
                        }
                        _childArr.Add(child);
                        index++;
                    }
                }
                _childCount = index;
                XFree(childrenPtr);
            }


            protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
            {
                for (var i = _selfIndex + 1; i < _childCount; i++)
                {
                    var child = _childArr[i];
                    if (child != IntPtr.Zero)
                    {
                        yield return backend.GetWindowElement(child);
                    }
                }
            }

            protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
            {
                for (var i = _selfIndex - 1; i >= 0; i--)
                {
                    var child = _childArr[i];
                    if (child != IntPtr.Zero)
                    {
                        yield return backend.GetWindowElement(child);
                    }
                }
            }
        }
        public VisualElementSiblingAccessor SiblingAccessor
        {
            get
            {
                XQueryTree(backend._display, windowHandle, out _, out var parentHandle, out _, out _);
                return new X11SiblingAccessor(this,  parentHandle, backend);
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
                    var display = backend._display;
                    if (XFetchName(display, windowHandle, out var ptr) != 0 && ptr != IntPtr.Zero)
                    {
                        var name = Marshal.PtrToStringAnsi(ptr);
                        XFree(ptr);
                        return name;
                    }
                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }
        public PixelRect BoundingRectangle
        {
            get
            {
                try
                {
                    var display = backend._display;

                    // 验证窗口句柄是否有效
                    if (windowHandle == IntPtr.Zero)
                    {
                        backend._logger.LogWarning("BoundingRectangle called with zero window handle");
                        return default(PixelRect);
                    }
                    XGetWindowAttributes(backend._display, windowHandle, out var windowAttr);
                    var x = windowAttr.x;
                    var y = windowAttr.y;
                    var w = windowAttr.width;
                    var h = windowAttr.height;
                    var result = XTranslateCoordinates(
                        display,
                        windowHandle,
                        backend._rootWindow,
                        0, 0,
                        out var absX, out var absY,
                        out _);

                    if (result == 0)
                    {
                        backend._logger.LogWarning("XTranslateCoordinates failed for window {window}", windowHandle.ToString("X"));
                        return new PixelRect(x, y, w, h);
                    }

                    return new PixelRect(absX, absY, w, h);
                }
                catch (Exception ex)
                {
                    backend._logger.LogError(ex, "BoundingRectangle failed for window {window}", windowHandle.ToString("X"));
                    return default(PixelRect);
                }
            }
        }
        public int ProcessId
        {
            get
            {
                var pid = 0;
                var win = this;
                while (win != null)
                {
                    pid = backend.XGetWindowPid(win.NativeWindowHandle);
                    if (pid != 0)
                    {
                        break;
                    }
                    win = (X11WindowVisualElement?)win.Parent;
                }
                return pid;
            }
        }
        public IntPtr NativeWindowHandle => windowHandle;
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
            XSetInputFocus(backend._display, windowHandle, RevertToParent, CurrentTime);
            backend.SendKeyboardShortcut(shortcut);
        }

        public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken) => 
            Task.FromResult(backend.Capture(this, BoundingRectangle)).WaitAsync(cancellationToken);
    }

    private class X11ScreenVisualElement(
        X11WindowBackend backend,
        int index
    ) : IVisualElement
    {
        private int ScreenCount => XScreenCount(backend._display);
        public string Id => $"Screen {index}";
        public IVisualElement? Parent => null;
        public IEnumerable<IVisualElement> Children
        {
            get
            {
                var rootWindow = XRootWindow(backend._display, index);
                XQueryTree(backend._display, rootWindow, out _, out _, out var childrenPtr, out var count);
                for (var i = 0; i < count; i++)
                {
                    var child = Marshal.ReadIntPtr(childrenPtr, i * IntPtr.Size);
                    if (child != IntPtr.Zero)
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
        public VisualElementSiblingAccessor SiblingAccessor =>  new X11ScreenSiblingAccessor(backend, index);
        
        public VisualElementType Type => VisualElementType.Screen;
        public VisualElementStates States => VisualElementStates.None;
        public string? Name => Id;
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
        public IntPtr NativeWindowHandle => XRootWindow(backend._display, index);
        public string? GetText(int maxLength = -1) => Name;
        public string? GetSelectionText() => null;

        public void Invoke() { }

        public void SetText(string text) { } // no op

        public void SendShortcut(KeyboardShortcut shortcut) { } // no op

        public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken) => 
            Task.FromResult(backend.Capture(this, BoundingRectangle)).WaitAsync(cancellationToken);
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
                    var norm = state & ~(LockMask | Mod2Mask);
                    var key = KeycodeToAvaloniaKey(evKey.keycode);
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
                        var keycode = (int)evKey.keycode;
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
        XSetErrorHandler(OnXError);
        Lock evLock = new Lock();
        var evPtr = Marshal.AllocHGlobal(256);
        var buf = new byte[4];
        try
        {
            var xfd = XConnectionNumber(_display);
            var fds = new PollFd[2];
            fds[0].fd = xfd; fds[0].events = POLLIN;
            fds[1].fd = _wakePipeR; fds[1].events = POLLIN;


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
                var rc = poll(fds, 2, -1);
                if (rc <= 0) continue;

                // wake pipe signaled -> drain and process ops
                if ((fds[1].revents & POLLIN) != 0)
                {
                    try
                    {
                        while (true)
                        {
                            _logger.LogDebug("About to read from wakePipe fd={fd}", _wakePipeR);
                            int r = read(_wakePipeR, buf, buf.Length);
                            _logger.LogDebug("Read from wakePipe fd={fd} returned={r}", _wakePipeR, r);
                            if (r > 0) continue;
                            if (r == 0) break;
                            // r < 0 -> check errno
                            var errno = Marshal.GetLastPInvokeError();
                            // EAGAIN / EWOULDBLOCK
                            const int eagain = 11;
                            if (errno is eagain) break;
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
                if ((fds[0].revents & POLLIN) != 0)
                {
                    while (XPending(_display) > 0)
                    {
                        lock (evLock)
                        {
                            XNextEvent(_display, evPtr);
                            XThreadProcessEvent(evPtr);
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
                var wr = write(_wakePipeW, b, 1);
                _logger.LogDebug("Wrote wake byte to pipe (fd={fd}) result={r}", _wakePipeW, wr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write wake pipe");
        }
    }

    private class RegInfo
    {
        public int Keycode { get; set; }
        public uint Mods { get; set; }
        public Action Handler { get; set; } = () => { };
    }
}
