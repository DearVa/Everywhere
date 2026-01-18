using System.Runtime.InteropServices;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Native P/Invoke definitions for X11 and related extensions.
/// </summary>
public static partial class X11Native
{
    public const string LibX11 = "libX11.so.6";
    public const string LibXFixes = "libXfixes.so.3";
    public const X11Window ScanSkipWindow = (X11Window)ulong.MaxValue;
    public const uint CurrentTime = 0;
    public enum ShapeKind
    {
        Bounding = 0,
        Clip = 1,
        Input = 2
    }
    [LibraryImport(LibX11)] internal static partial IntPtr XGetWMHints(IntPtr display, X11Window window);
    [LibraryImport(LibX11)] internal static partial void XSetWMHints(IntPtr display, X11Window window, ref XWMHints hints);
    [LibraryImport(LibX11)] internal static partial int XScreenCount(IntPtr display);
    [LibraryImport(LibX11)] internal static partial int XDisplayWidth(IntPtr display, int screenNumber);
    [LibraryImport(LibX11)] internal static partial int XDisplayHeight(IntPtr display, int screenNumber);
    [LibraryImport(LibX11)] internal static partial IntPtr XKeysymToString(KeySym keysym);
    [LibraryImport(LibX11)] internal static partial void XQueryKeymap(IntPtr display, byte[] keymap);
    [LibraryImport(LibX11)] internal static partial int XGrabKeyboard(IntPtr display, X11Window grabWindow, int ownerEvents, GrabMode pointerMode, GrabMode keyboardMode, uint time);
    [LibraryImport(LibX11)] internal static partial int XUngrabKeyboard(IntPtr display, X11Window grabWindow);
    [LibraryImport(LibX11)] internal static partial int XTranslateCoordinates(IntPtr display, X11Window srcWindow, X11Window destWindow, int srcX, int srcY, out int destXReturn, out int destYReturn, out IntPtr childReturn);
    [LibraryImport(LibX11)] internal static partial int XGetWindowProperty(IntPtr display, X11Window window, Atom property, long offset, long length, int delete, Atom reqType, out Atom actualTypeReturn, out int actualFormatReturn, out ulong nitemsReturn, out ulong bytesAfterReturn, out IntPtr propReturn);
    [LibraryImport(LibX11)] internal static partial int XChangeWindowAttributes(IntPtr display, X11Window window, ulong valueMask, ref XSetWindowAttributes attributes);
    [LibraryImport(LibXFixes)] internal static partial IntPtr XFixesCreateRegion(IntPtr display, XRectangle[] rectangles, int nrectangles);
    [LibraryImport(LibXFixes)] internal static partial void XFixesDestroyRegion(IntPtr display, IntPtr region);
    [LibraryImport(LibXFixes)] internal static partial void XFixesSetWindowShapeRegion(IntPtr display, X11Window window, int shapeKind, int xOffset, int yOffset, IntPtr region);
    [LibraryImport(LibXFixes)] internal static partial int XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public IntPtr background_pixmap;
        public ulong background_pixel;
        public IntPtr border_pixmap;
        public ulong border_pixel;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public ulong backing_planes;
        public ulong backing_pixel;
        public int save_under;
        public ulong event_mask;
        public ulong do_not_propagate_mask;
        public int override_redirect;
        public IntPtr colormap;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XRectangle
    {
        public short x;
        public short y;
        public ushort width;
        public ushort height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWMHints
    {
        public IntPtr flags;
        public int input;
        public int initial_state;
        public IntPtr icon_pixmap;
        public IntPtr icon_window;
        public int icon_x;
        public int icon_y;
        public IntPtr icon_mask;
        public IntPtr window_group;
    }

    public enum MapState
    {
        IsUnmapped = 0,
        IsUnviewable = 1,
        IsViewable = 2
    }

    public static string GetErrorCodeName(int code)
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

    public static string GetRequestCodeName(int requestCode)
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
}
