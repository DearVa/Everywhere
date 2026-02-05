using System.Runtime.InteropServices;
using Avalonia.Input;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Provides core X11 services: Property reading, Coordinate translation, Atom resolution, Key conversion, etc.
/// </summary>
public sealed class X11CoreServices
{
    private readonly IntPtr _display;
    private readonly AtomCache _atomCache;
    private readonly X11Context _context;

    public X11CoreServices(X11Context context)
    {
        _display = context.Display;
        _atomCache = context.AtomCache;
        _context = context;
    }

    public Atom GetAtom(string name, bool onlyIfExists = false) => _context.GetAtom(name, onlyIfExists);

    public KeyCode XKeycode(Key key)
    {
        var ks = Xlib.XStringToKeysym(key.ToString());
        KeyCode keycode = 0;
        if (ks != 0) keycode = Xlib.XKeysymToKeycode(_display, ks);
        if (keycode != 0) return keycode;
        ks = Xlib.XStringToKeysym(key.ToString().ToUpperInvariant());
        if (ks != 0) keycode = Xlib.XKeysymToKeycode(_display, ks);
        return keycode;
    }

    public Key KeycodeToAvaloniaKey(KeyCode keycode)
    {
        try
        {
            var ks = Xlib.XKeycodeToKeysym(_display, keycode, 0);
            var name = KeysymToString(ks);
            if (!string.IsNullOrEmpty(name) && name.Length == 1 && char.IsLetter(name[0]))
                return (Key)Enum.Parse(typeof(Key), name.ToUpperInvariant());
            return Key.None;
        }
        catch { return Key.None; }
    }

    private static string KeysymToString(KeySym ks)
    {
        try
        {
            var p = X11Native.XKeysymToString(ks);
            return p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(p) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    public void GetProperty(X11Window window, string propertyName, long length, Atom reqType, Action<Atom, int, ulong, ulong, IntPtr> callback)
    {
        var atom = GetAtom(propertyName, true);
        if (atom == Atom.None) return;

        X11Native.XGetWindowProperty(_display, window, atom, 0, length, 0, reqType,
            out var actualType, out var actualFormat, out var nItems, out var bytesAfter, out var prop);
        
        try
        {
            callback(actualType, actualFormat, nItems, bytesAfter, prop);
        }
        finally
        {
            Xlib.XFree(prop);
        }
    }
    
    public bool TranslateCoordinates(X11Window src, X11Window dst, int srcX, int srcY, out int dstX, out int dstY)
    {
        var result = X11Native.XTranslateCoordinates(_display, src, dst, srcX, srcY, out dstX, out dstY, out _);
        return result != 0;
    }

    public void ConvertPixelFormat(byte[] pixelData, XImage ximage, int width, int height, int stride)
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
            throw new NotSupportedException($"Unsupported pixel format: bpp={ximage.bits_per_pixel}, depth={ximage.depth}");
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
}
