using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Handles screen capture and pixel format conversions.
/// </summary>
public sealed class X11Screenshot
{
    private readonly ILogger _logger;
    private readonly X11Context _context;
    private readonly X11CoreServices _coreServices;

    public X11Screenshot(ILogger logger, X11Context context, X11CoreServices coreServices)
    {
        _logger = logger;
        _context = context;
        _coreServices = coreServices;
    }

    private bool IsValidDrawable(X11Window drawable, ref uint w, ref uint h)
    {
        try
        {
            // First try getwindowattributes
            var state = Xlib.XGetWindowAttributes(
                _context.Display,
                drawable,
                out var attr);
            if (state == 0) return false;
            if (attr.width == 0 || attr.height == 0) return false;
            if ((X11Native.MapState)attr.map_state != X11Native.MapState.IsViewable) return false;
            w = (uint)attr.width;
            h = (uint)attr.height;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsValidDrawable failed");
            return false;
        }
    }

    public Bitmap Capture(X11Window drawable, PixelRect rect)
    {
        var ximage = Xlib.XGetImage(_context.Display, drawable, rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height, (ulong)Planes.AllPlanes, PixmapFormat.ZPixmap);
        if (ximage.data == IntPtr.Zero) throw new InvalidOperationException("XGetImage returned null");

        try
        {
            int stride = ximage.bytes_per_line;
            int bufferSize = stride * ximage.height;
            byte[] pixelData = new byte[bufferSize];
            Marshal.Copy(ximage.data, pixelData, 0, bufferSize);

            _coreServices.ConvertPixelFormat(pixelData, ximage, rect.Width, rect.Height, stride);

            unsafe
            {
                fixed (byte* p = pixelData)
                {
                    return new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul, 
                        new nint(p), new PixelSize(rect.Width, rect.Height), new Vector(96, 96), stride);
                }
            }
        }
        finally
        {
            Xutil.XDestroyImage(ref ximage);
        }
    }
}
