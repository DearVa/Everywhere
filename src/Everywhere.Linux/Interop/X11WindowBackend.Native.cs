using System.Runtime.InteropServices;
using Avalonia.Input;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop;

/// <summary>
/// X11 Backend Impl
/// </summary>
public partial class X11WindowBackend
{
    #region X11 P/Invoke Declarations

    // Core X11 functions
    [LibraryImport(LibX11)] private static partial IntPtr XOpenDisplay(IntPtr displayName);
    [LibraryImport(LibX11)] private static partial int XInitThreads();
    [LibraryImport(LibX11)] private static partial int XCloseDisplay(IntPtr display);
    [LibraryImport(LibX11)] private static partial IntPtr XDefaultGC(IntPtr display, int screenNumber);
    [LibraryImport(LibX11)] private static partial int XDefaultScreen(IntPtr display);
    [LibraryImport(LibX11)] private static partial IntPtr XDefaultRootWindow(IntPtr display);
    [LibraryImport(LibX11)] private static partial IntPtr XRootWindow(IntPtr display, int screenNumber);
    [LibraryImport(LibX11)] private static partial int XScreenCount(IntPtr display);
    [LibraryImport(LibX11)] private static partial int XDisplayWidth(IntPtr display, int screenNumber);
    [LibraryImport(LibX11)] private static partial int XDisplayHeight(IntPtr display, int screenNumber);
    [LibraryImport(LibX11)] private static partial int XChangeWindowAttributes(IntPtr display, IntPtr window, int valueMask, ref XWindowAttributes attributes);
    [LibraryImport(LibX11)] private static partial int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);
    [LibraryImport(LibX11)] private static partial int XMatchVisualInfo(IntPtr display, int screen, int depth, int class_visual, out XVisualInfo vinfo);
    [LibraryImport(LibX11)]
    private static partial IntPtr XCreateColormap(IntPtr display, IntPtr window, IntPtr visual, int alloc);
    [LibraryImport(LibX11)]
    private static partial IntPtr XCreateWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height, uint border_width, int depth, uint class_visual, IntPtr visual, uint valuemask, ref XSetWindowAttributes attributes);

    [LibraryImport(LibX11)]
    private static partial int XMapWindow(IntPtr display, IntPtr window);

    [LibraryImport(LibX11)]
    private static partial int XRaiseWindow(IntPtr display, IntPtr window);

    [LibraryImport(LibX11)]
    private static partial int XUnmapWindow(IntPtr display, IntPtr window);

    [LibraryImport(LibX11)]
    private static partial int XDestroyWindow(IntPtr display, IntPtr window);
    [LibraryImport(LibX11)]
    private static partial int XClearArea(IntPtr display, IntPtr window, int x, int y, uint width, uint height, int exposures);

    [LibraryImport(LibX11)]
    private static partial int XDrawRectangle(IntPtr display, IntPtr drawable, IntPtr gc, int x, int y, uint width, uint height);
    [LibraryImport(LibX11)]
    private static partial ulong XWhitePixel(IntPtr display, int screenNumber);

    [LibraryImport(LibX11)]
    private static partial ulong XBlackPixel(IntPtr display, int screenNumber);

    // Atoms and Properties
    [LibraryImport(LibX11)] private static partial UIntPtr XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string s);
    [LibraryImport(LibX11)] private static partial IntPtr XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPStr)] string atomName, int onlyIfExists);

    // Events and Input
    [LibraryImport(LibX11)] private static partial int XSelectInput(IntPtr display, IntPtr window, uint eventMask);
    [LibraryImport(LibX11)] private static partial int XFlush(IntPtr display);
    [LibraryImport(LibX11)] private static partial int XNextEvent(IntPtr display, IntPtr ev);
    [LibraryImport(LibX11)] private static partial IntPtr XSetErrorHandler(XErrorHandlerFunc handler);
    [LibraryImport(LibX11)] private static partial int XPending(IntPtr display);
    [LibraryImport(LibX11)] private static partial int XConnectionNumber(IntPtr display);

    // Keyboard and Mouse
    [LibraryImport(LibX11)] private static partial int XKeysymToKeycode(IntPtr display, UIntPtr keysym);
    [LibraryImport(LibX11)] private static partial UIntPtr XKeycodeToKeysym(IntPtr display, int keycode, int index);
    [LibraryImport(LibX11)] private static partial IntPtr XKeysymToString(UIntPtr keysym);
    [LibraryImport(LibX11)] private static partial void XQueryKeymap(IntPtr display, byte[] keymap);
    [LibraryImport(LibX11)] private static partial int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [LibraryImport(LibX11)] private static partial int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
    [LibraryImport(LibX11)] private static partial int XGrabKeyboard(IntPtr display, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode, uint time);
    [LibraryImport(LibX11)] private static partial int XUngrabKeyboard(IntPtr display, IntPtr grabWindow);
    [LibraryImport(LibX11)] private static partial int XGrabPointer(IntPtr display, IntPtr grabWindow, int ownerEvents, uint eventMask, int pointerMode, int keyboardMode, IntPtr confineTo, IntPtr cursor, uint time);
    [LibraryImport(LibX11)] private static partial int XUngrabPointer(IntPtr display, uint time);

    // Window and Geometry operations
    [LibraryImport(LibX11)] private static partial int XTranslateCoordinates(IntPtr display, IntPtr srcWindow, IntPtr destWindow, int srcX, int srcY, out int destXReturn, out int destYReturn, out IntPtr childReturn);
    [LibraryImport(LibX11)] private static partial int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr rootReturn, out int x, out int y, out uint width, out uint height, out uint borderWidth, out uint depth);
    [LibraryImport(LibX11)] private static partial int XFetchName(IntPtr display, IntPtr window, out IntPtr windowName);
    [LibraryImport(LibX11)] private static partial void XFree(IntPtr data);
    [LibraryImport(LibX11)] private static partial int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, long offset, long length, int delete, IntPtr reqType, out IntPtr actualTypeReturn, out int actualFormatReturn, out ulong nitemsReturn, out ulong bytesAfterReturn, out IntPtr propReturn);

    // Focus and Input
    [LibraryImport(LibX11)] private static partial void XGetInputFocus(IntPtr display, out IntPtr focusReturn, out int revertToReturn);
    [LibraryImport(LibX11)] private static partial int XQueryPointer(IntPtr display, IntPtr window, out IntPtr rootReturn, out IntPtr childReturn, out int rootXReturn, out int rootYReturn, out int winXReturn, out int winYReturn, out uint maskReturn);
    [LibraryImport(LibX11)] private static partial int XQueryTree(IntPtr display, IntPtr window, out IntPtr rootReturn, out IntPtr parentReturn, out IntPtr childrenReturn, out int nchildrenReturn);

    // Window properties and state
    private const int RevertToPointerRoot = 1;
    private const int RevertToParent = 2;
    [LibraryImport(LibX11)] private static partial void XSetInputFocus(IntPtr display, IntPtr window, int revert_to, uint time);
    private const int PropModeReplace = 0;
    [LibraryImport(LibX11)] private static partial void XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, ulong[] data, int nelements);

    // Error handling
    [LibraryImport(LibX11)] private static partial void XGetErrorText(IntPtr display, int code, byte[] buffer, int length);

    // Screen capture
    private const int ZPixmap = 2;
    private const ulong AllPlanes = ~0UL;
    [LibraryImport(LibX11)] private static partial IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y, uint width, uint height, ulong planeMask, int format);
    [LibraryImport(LibX11)] private static partial void XDestroyImage(IntPtr ximage);

    // Window shaping and transparency
    private const int ShapeBounding = 0;
    private const int ShapeClip = 1;
    private const int ShapeInput = 2;
    private const int ShapeSet = 0;
    private const int ShapeUnion = 1;
    
    [StructLayout(LayoutKind.Sequential)]
    private struct XRectangle
    {
        public short x;
        public short y;
        public ushort width;
        public ushort height;
    }

    [LibraryImport("libXfixes.so.3")] private static partial IntPtr XFixesCreateRegion(IntPtr display, XRectangle[] rectangles, int nrectangles);
    [LibraryImport("libXfixes.so.3")] private static partial void XFixesDestroyRegion(IntPtr display, IntPtr region);
    [LibraryImport("libXfixes.so.3")] private static partial void XFixesSetWindowShapeRegion(IntPtr display, IntPtr window, int shapeKind, int xOffset, int yOffset, IntPtr region);
    [LibraryImport("libXfixes.so.3")] private static partial int XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [LibraryImport("libXtst.so.6")] private static partial int XTestFakeKeyEvent(IntPtr display, int keycode, int press, uint delay);

    // System calls for pipes and polling
    [LibraryImport("libc", SetLastError = true)] private static partial int pipe([MarshalAs(UnmanagedType.LPArray, SizeConst = 2)] int[] fds);
    [LibraryImport("libc", SetLastError = true)] private static partial int write(int fd, byte[] buf, IntPtr count);
    [LibraryImport("libc", SetLastError = true)] private static partial int read(int fd, byte[] buf, IntPtr count);
    [LibraryImport("libc", SetLastError = true)] private static partial int close(int fd);
    [LibraryImport("libc", SetLastError = true)] private static partial int fcntl(int fd, int cmd, int arg);

    // Constants for fcntl
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    private const int O_NONBLOCK = 0x800;

    // Polling structure and function
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int fd; public short events; public short revents; }
    private const short POLLIN = 0x0001;
    [LibraryImport("libc", SetLastError = true)] private static partial int poll([In, Out] PollFd[] fds, uint nfds, int timeout);

    #endregion

    #region X11 Constants and Structures

    // Key and Modifiers Masks in state
    private const int AnyKey = 0;
    private const uint ShiftMask = 1u << 0;
    private const uint LockMask = 1u << 1;
    private const uint ControlMask = 1u << 2;
    private const uint Mod1Mask = 1u << 3; // Alt
    private const uint Mod2Mask = 1u << 4; // Num Lock
    private const uint Mod4Mask = 1u << 6; // Super/Meta/Win
    private const uint Button1Mask = 1u << 8;
    private const uint Button2Mask = 1u << 9;
    private const uint Button3Mask = 1u << 10;
    private const uint Button4Mask = 1u << 11;
    private const uint Button5Mask = 1u << 12;
    private const uint AnyModifiers = 1u << 15;

    // Key/Buttion detail


    // Event Masks
    private const uint KeyPressMask = 1u << 0;
    private const uint KeyReleaseMask = 1u << 1;
    private const uint ButtonPressMask = 1u << 2;
    private const uint ButtonReleaseMask = 1u << 3;
    private const uint PointerMotionMask = 1u << 6;
    private const uint ButtonMotionMask = 1u << 13;
    private const uint FocusChangeMask = 1u << 21;

    // Event Types
    private const int KeyPress = 2;
    private const int KeyRelease = 3;
    private const int ButtonPress = 4;
    private const int ButtonRelease = 5;
    private const int MotionNotify = 6;
    private const int FocusIn = 9;
    private const int FocusOut = 10;
    
    // Window Attr
    private const int IsUnmapped = 0;
    private const int IsUnviewable = 1;
    private const int IsViewable = 2;

    private static class CW
    {
        public const int BackPixmap = 1 << 0;
        public const int BackPixel = 1 << 1;
        public const int BorderPixmap = 1 << 2;
        public const int BorderPixel = 1 << 3;
        public const int BitGravity = 1 << 4;
        public const int WinGravity = 1 << 5;
        public const int BackingStore = 1 << 6;
        public const int BackingPlanes = 1 << 7;
        public const int BackingPixel = 1 << 8;
        public const int SaveUnder = 1 << 9;
        public const int EventMask = 1 << 10;
        public const int DontPropagate = 1 << 11;
        public const int OverrideRedirect = 1 << 15;
        public const int Colormap = 1 << 13;
        public const int Cursor = 1 << 14;
    }
    private static class CreateWindowArgs
    {
        public const int InputOutput = 1;
        public const int InputOnly = 2;
    }
    // Other Constants
    private const int TrueColor = 4;
    private const int GrabModeAsync = 1;
    private const uint CurrentTime = 0;
    private const string LibX11 = "libX11.so.6";
    private const int XA_ATOM = 4;
    private const int XA_CARDINAL = 6;
    private const int XA_WINDOW = 33;
    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int x, y;
        public int width, height;
        public int border_width;
        public int depth;
        public IntPtr visual;
        public IntPtr root;
        public int class_visual;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public IntPtr backing_planes;
        public IntPtr backing_pixel;
        public int save_under;
        public IntPtr colormap;
        public int map_installed;
        public int map_state;
        public IntPtr all_event_masks;
        public IntPtr your_event_mask;
        public IntPtr do_not_propagate_mask;
        public int override_redirect;
        public IntPtr screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XSetWindowAttributes
    {
        public IntPtr background_pixmap;
        public IntPtr background_pixel;
        public IntPtr border_pixmap;
        public IntPtr border_pixel;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public IntPtr backing_planes;
        public IntPtr backing_pixel;
        public int save_under;
        public IntPtr event_mask;
        public IntPtr do_not_propagate_mask;
        public int override_redirect;
        public IntPtr colormap;
        public IntPtr cursor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XVisualInfo
    {
        public IntPtr visual;
        public IntPtr visualid;
        public int screen;
        public int depth;
        public int class_visual;
        public ulong red_mask;
        public ulong green_mask;
        public ulong blue_mask;
        public int colormap_size;
        public int bits_per_rgb;
    }

    // X11 Structures
    [StructLayout(LayoutKind.Sequential)]
    private struct XAnyEvent
    {
        public int type;
        public ulong serial;
        public int sendEvent;
        public IntPtr display;
        public IntPtr window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XKeyEvent
    {
        public int type;
        public ulong serial;
        public int sendEvent;
        public IntPtr display;
        public IntPtr window;
        public IntPtr root;
        public IntPtr subwindow;
        public ulong time;
        public int x, y;
        public int x_root, y_root;
        public uint state;
        public uint keycode;
        public bool sameScreen;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct XButtonEvent
    {
        public int type;
        public ulong serial;
        public int sendEvent;
        public IntPtr display;
        public IntPtr window;
        public IntPtr root;
        public IntPtr subwindow;
        public ulong time;
        public int x, y;
        public int x_root, y_root;
        public uint state;
        public uint button;
        public bool sameScreen;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct XMotionEvent
    {
        public int type;
        public ulong serial;
        public int sendEvent;
        public IntPtr display;
        public IntPtr window;
        public IntPtr root;
        public IntPtr subwindow;
        public ulong time;
        public int x, y;
        public int x_root, y_root;
        public uint state;
        public uint is_hint;
        public bool sameScreen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XErrorEvent
    {
        public int type;
        public IntPtr display;
        public ulong resourceid;
        public ulong serial;
        public byte errorCode;
        public byte request_code;
        public byte minor_code;
        public byte pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int width;
        public int height;
        public int xoffset;
        public int format;
        public IntPtr data;
        public int byte_order;
        public int bitmap_unit;
        public int bitmap_bit_order;
        public int bitmap_pad;
        public int depth;
        public int bytes_per_line;
        public int bits_per_pixel;
        public ulong red_mask;
        public ulong green_mask;
        public ulong blue_mask;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct XGCValues
    {
        public IntPtr function;       // 逻辑操作函数
        public ulong plane_mask;      // 平面掩码
        public ulong foreground;      // 前景色
        public ulong background;      // 背景色
        public int line_width;        // 线宽
        public int line_style;        // 线型
        public int cap_style;         // 线端样式
        public int join_style;        // 线连接样式
        public int fill_style;        // 填充样式
        public int fill_rule;         // 填充规则
        public IntPtr tile;           // 平铺位图
        public IntPtr stipple;        // 点画位图
        public int ts_x_origin;       // 点画X原点
        public int ts_y_origin;       // 点画Y原点
        public IntPtr font;           // 字体
        public int subwindow_mode;    // 子窗口模式
        public int graphics_exposures; // 图形暴露
        public int clip_x_origin;     // 裁剪X原点
        public int clip_y_origin;     // 裁剪Y原点
        public IntPtr clip_mask;      // 裁剪掩码
        public int dash_offset;       // 虚线偏移
        public IntPtr dashes;         // 虚线模式
        public int arc_mode;          // 弧模式
    }
    private static class GCFunction
    {
        public const int GXclear = 0x0;        // 0
        public const int GXand = 0x1;          // src AND dst
        public const int GXandReverse = 0x2;   // src AND NOT dst
        public const int GXandInverted = 0x4;  // NOT src AND dst
        public const int GXnoop = 0x5;         // dst
        public const int GXxor = 0x6;          // src XOR dst
        public const int GXor = 0x7;           // src OR dst
        public const int GXnor = 0x8;          // NOT src AND NOT dst
        public const int GXequiv = 0x9;        // NOT src XOR dst
        public const int GXinvert = 0xa;       // NOT dst
        public const int GXorReverse = 0xb;    // src OR NOT dst
        public const int GXcopyInverted = 0xc; // NOT src
        public const int GXorInverted = 0xd;   // NOT src OR dst
        public const int GXnand = 0xe;         // NOT src OR NOT dst
        public const int GXset = 0xf;          // 1
    }

    private static class GCMask
    {
        public const int GCFunction = 1 << 0;
        public const int GCPlaneMask = 1 << 1;
        public const int GCForeground = 1 << 2;
        public const int GCBackground = 1 << 3;
        public const int GCLineWidth = 1 << 4;
        public const int GCLineStyle = 1 << 5;
        public const int GCCapStyle = 1 << 6;
        public const int GCJoinStyle = 1 << 7;
        public const int GCFillStyle = 1 << 8;
        public const int GCFillRule = 1 << 9;
        public const int GCTile = 1 << 10;
        public const int GCStipple = 1 << 11;
        public const int GCTileStipXOrigin = 1 << 12;
        public const int GCTileStipYOrigin = 1 << 13;
        public const int GCFont = 1 << 14;
        public const int GCSubwindowMode = 1 << 15;
        public const int GCGraphicsExposures = 1 << 16;
        public const int GCClipXOrigin = 1 << 17;
        public const int GCClipYOrigin = 1 << 18;
        public const int GCClipMask = 1 << 19;
        public const int GCDashOffset = 1 << 20;
        public const int GCDashList = 1 << 21;
        public const int GCArcMode = 1 << 22;
    }

    private const int IncludeInferiors = 1;

    private delegate int XErrorHandlerFunc(IntPtr display, IntPtr errorEventPtr);
    #endregion
    // X11 Error Handler
    private int OnXError(IntPtr d, IntPtr errorEventPtr)
    {
        try
        {
            var ev = Marshal.PtrToStructure<XErrorEvent>(errorEventPtr);
            string text;
            try
            {
                // XGetErrorText Buffer
                var buffer = new byte[256]; // X11 Error Text Give 256 Bytes
                XGetErrorText(d, ev.errorCode, buffer, buffer.Length);
                text = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get X error text for code {code}", ev.errorCode);
                text = $"Unknown error code {ev.errorCode}";
            }

            _logger.LogError(
                "X Error: code={code}({errorName}) request={req}({reqName}) minor={minor} resource={res} text={text}",
                ev.errorCode,
                GetErrorCodeName(ev.errorCode),
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

    #region X11 Code Conversion Functions
    private static string GetErrorCodeName(int code)
    {
        return code switch
        {
            1 => "BadRequest",
            2 => "BadValue",
            3 => "BadWindow",
            4 => "BadPixmap",
            5 => "BadAtom",
            6 => "BadCursor",
            7 => "BadFont",
            8 => "BadMatch",
            9 => "BadDrawable",
            10 => "BadAccess",
            11 => "BadAlloc",
            12 => "BadColor",
            13 => "BadGC",
            14 => "BadIDChoice",
            15 => "BadName",
            16 => "BadLength",
            17 => "BadImplementation",
            _ => $"Unknown({code})"
        };
    }

    // /// <summary>
    // /// X11 Error Discription
    // /// </summary>
    // private static string GetErrorDescription(int code)
    // {
    //     return code switch
    //     {
    //         1 => "The request code is invalid",
    //         2 => "Some numeric value falls outside the allowed range",
    //         3 => "A parameter for a Window request does not refer to a valid Window",
    //         4 => "A parameter for a Pixmap request does not refer to a valid Pixmap",
    //         5 => "A parameter for an Atom request does not refer to a valid Atom",
    //         6 => "A parameter for a Cursor request does not refer to a valid Cursor",
    //         7 => "A parameter for a Font request does not refer to a valid Font",
    //         8 => "The input shape does not match the drawable",
    //         9 => "A parameter for a Drawable request does not refer to a valid Drawable",
    //         10 => "The client attempted to access a resource it does not have permission to access",
    //         11 => "The server failed to allocate the requested resource",
    //         12 => "A value for a Colormap request does not refer to a valid Colormap",
    //         13 => "A parameter for a GContext request does not refer to a valid GContext",
    //         14 => "The choice is not in the allowed range",
    //         15 => "A font or color name does not exist",
    //         16 => "The length of a request is shorter or longer than required",
    //         17 => "The server does not implement some aspect of the request",
    //         _ => $"No description available for error code {code}"
    //     };
    // }

    private static string GetRequestCodeName(int requestCode)
    {
        return requestCode switch
        {
            // Core protocol requests
            1 => "CreateWindow",
            2 => "ChangeWindowAttributes",
            3 => "GetWindowAttributes",
            4 => "DestroyWindow",
            5 => "DestroySubwindows",
            6 => "ChangeSaveSet",
            7 => "ReparentWindow",
            8 => "MapWindow",
            9 => "MapSubwindows",
            10 => "UnmapWindow",
            11 => "UnmapSubwindows",
            12 => "ConfigureWindow",
            13 => "CirculateWindow",
            14 => "GetGeometry",
            15 => "QueryTree",
            16 => "InternAtom",
            17 => "GetAtomName",
            18 => "ChangeProperty",
            19 => "DeleteProperty",
            20 => "GetProperty",
            21 => "ListProperties",
            22 => "SetSelectionOwner",
            23 => "GetSelectionOwner",
            24 => "ConvertSelection",
            25 => "SendEvent",
            26 => "GrabPointer",
            27 => "UngrabPointer",
            28 => "GrabButton",
            29 => "UngrabButton",
            30 => "ChangeActivePointerGrab",
            31 => "GrabKeyboard",
            32 => "UngrabKeyboard",
            33 => "GrabKey",
            34 => "Ungrab",
            35 => "AllowEvents",
            36 => "GrabServer",
            37 => "UngrabServer",
            38 => "QueryPointer",
            39 => "GetMotionEvents",
            40 => "TranslateCoords",
            41 => "WarpPointer",
            42 => "SetInputFocus",
            43 => "GetInputFocus",
            44 => "QueryKeymap",
            45 => "OpenFont",
            46 => "CloseFont",
            47 => "QueryFont",
            48 => "QueryTextExtents",
            49 => "ListFonts",
            50 => "ListFontsWithInfo",
            51 => "SetFontPath",
            52 => "GetFontPath",
            53 => "CreatePixmap",
            54 => "FreePixmap",
            55 => "CreateGC",
            56 => "ChangeGC",
            57 => "CopyGC",
            58 => "SetDashes",
            59 => "SetClipRectangles",
            60 => "FreeGC",
            61 => "ClearArea",
            62 => "CopyArea",
            63 => "CopyPlane",
            64 => "PolyPoint",
            65 => "PolyLine",
            66 => "PolySegment",
            67 => "PolyRectangle",
            68 => "PolyArc",
            69 => "FillPoly",
            70 => "PolyFillRectangle",
            71 => "PolyFillArc",
            72 => "PutImage",
            73 => "GetImage",
            74 => "PolyText8",
            75 => "PolyText16",
            76 => "ImageText8",
            77 => "ImageText16",
            78 => "CreateColormap",
            79 => "FreeColormap",
            80 => "CopyColormapAndFree",
            81 => "InstallColormap",
            82 => "UninstallColormap",
            83 => "ListInstalledColormaps",
            84 => "AllocColor",
            85 => "AllocNamedColor",
            86 => "AllocColorCells",
            87 => "AllocColorPlanes",
            88 => "FreeColors",
            89 => "StoreColors",
            90 => "StoreNamedColor",
            91 => "QueryColors",
            92 => "LookupColor",
            93 => "CreateCursor",
            94 => "CreateGlyphCursor",
            95 => "FreeCursor",
            96 => "RecolorCursor",
            97 => "QueryBestSize",
            98 => "QueryExtension",
            99 => "ListExtensions",
            100 => "ChangeKeyboardMapping",
            101 => "GetKeyboardMapping",
            102 => "ChangeKeyboardControl",
            103 => "GetKeyboardControl",
            104 => "Bell",
            105 => "ChangePointerControl",
            106 => "GetPointerControl",
            107 => "SetScreenSaver",
            108 => "GetScreenSaver",
            109 => "ChangeHosts",
            110 => "ListHosts",
            111 => "SetAccessControl",
            112 => "SetCloseDownMode",
            113 => "KillClient",
            114 => "RotateProperties",
            115 => "ForceScreenSaver",
            116 => "SetPointerMapping",
            117 => "GetPointerMapping",
            118 => "SetModifierMapping",
            119 => "GetModifierMapping",
            120 => "NoOperation",

            // Common extensions
            128 => "X_QueryExtension", // Usually for extensions
            129 => "X_ListExtensions",

            // XFixes extension (major codes vary, but common ones)
            140 => "XFixes_QueryVersion",
            141 => "XFixes_SetWindowShapeRegion",

            // XShape extension
            142 => "XShape_QueryVersion",
            143 => "XShape_Rectangles",

            _ => $"UnknownRequest{requestCode}"
        };
    }
    
    internal static string KeysymToString(UIntPtr ks)
    {
        try
        {
            var p = XKeysymToString(ks);
            if (p == IntPtr.Zero) return string.Empty;
            return Marshal.PtrToStringAnsi(p) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    internal static KeyModifiers KeyStateToModifier(uint state)
    {
        KeyModifiers mod = KeyModifiers.None;
        if ((state & ShiftMask) != 0) mod |= KeyModifiers.Shift;
        if ((state & ControlMask) != 0) mod |= KeyModifiers.Control;
        if ((state & Mod1Mask) != 0) mod |= KeyModifiers.Alt;
        if ((state & Mod4Mask) != 0) mod |= KeyModifiers.Meta;
        return mod;
    }
    private EventType GetEventType(IntPtr rawEvent)
    {
        var ev = Marshal.PtrToStructure<XAnyEvent>(rawEvent);
        // _logger.LogInformation("X recv event type={ev}", ev.type);
        switch (ev.type)
        {
            case KeyPress: return EventType.KeyDown;
            case KeyRelease: return EventType.KeyUp;
            case ButtonPress:
            case ButtonRelease:
                {
                    var buttonEvent = Marshal.PtrToStructure<XButtonEvent>(rawEvent);
                    switch (buttonEvent.button)
                    {
                        case 2: return EventType.MouseMiddle;
                        case 3: return EventType.MouseRight;
                        case 4: return EventType.MouseWheelUp;
                        case 5: return EventType.MouseWheelDown;
                        case 6: return EventType.MouseWheelLeft;
                        case 7: return EventType.MouseWheelRight;
                    }
                    return ev.type == ButtonPress ? EventType.MouseDown : EventType.MouseUp;
                }
            case MotionNotify:
                return EventType.MouseDrag;
            case FocusIn:
            case FocusOut:
                return EventType.FocusChange;

        }
        return EventType.Unknown;
    }

    #endregion
}