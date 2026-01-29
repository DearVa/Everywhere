using System.Drawing;
using System.Drawing.Imaging;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Platform;
using Everywhere.Common;
using Everywhere.I18N;
using Everywhere.Interop;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Serilog;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Vector = Avalonia.Vector;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext(IWindowHelper windowHelper) : IVisualElementContext
{
    private static readonly UIA3Automation Automation = new();
    private static readonly ITreeWalker TreeWalker = Automation.TreeWalkerFactory.GetContentViewWalker();

    public IVisualElement? FocusedElement => TryCreateVisualElement(Automation.FocusedElement);

    public IVisualElement? ElementFromPoint(PixelPoint point, ScreenSelectionMode mode = ScreenSelectionMode.Element)
    {
        switch (mode)
        {
            case ScreenSelectionMode.Element:
            {
                return TryCreateVisualElement(() => Automation.FromPoint(new Point(point.X, point.Y)));
            }
            case ScreenSelectionMode.Window:
            {
                IVisualElement? element = TryCreateVisualElement(() => Automation.FromPoint(new Point(point.X, point.Y)));
                while (element is AutomationVisualElementImpl { IsTopLevelWindow: false })
                {
                    element = element.Parent;
                }

                return element;
            }
            case ScreenSelectionMode.Screen:
            {
                var hMonitor = PInvoke.MonitorFromPoint(new Point(point.X, point.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                return hMonitor == HMONITOR.Null ? null : new ScreenVisualElementImpl(hMonitor);
            }
        }

        return null;
    }

    public IVisualElement? ElementFromPointer(ScreenSelectionMode mode = ScreenSelectionMode.Element)
    {
        return !PInvoke.GetCursorPos(out var point) ? null : ElementFromPoint(new PixelPoint(point.X, point.Y), mode);
    }

    public Task<IVisualElement?> PickElementAsync(ScreenSelectionMode? initialMode) => PickerSession.PickAsync(windowHelper, initialMode);

    public Task<Bitmap?> ScreenshotAsync(ScreenSelectionMode? initialMode) => ScreenshotSession.ScreenshotAsync(windowHelper, initialMode);

    private static AutomationVisualElementImpl? TryCreateVisualElement(Func<AutomationElement?> factory)
    {
        try
        {
            if (factory() is { } element) return new AutomationVisualElementImpl(element);
        }
        catch (Exception ex)
        {
            Log.ForContext<VisualElementContext>().Error(
                new HandledException(ex, new DirectResourceKey("Failed to get AutomationElement")),
                "Failed to get AutomationElement");
        }

        return null;
    }

    private static bool IsAutomationException(Exception ex) =>
        ex.GetType().Namespace?.StartsWith("FlaUI.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Captures a screenshot of the specified rectangle on the screen.
    /// </summary>
    /// <param name="rect"></param>
    /// <returns></returns>
    private static Bitmap? CaptureScreen(PixelRect rect)
    {
        var x = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var y = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        var w = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        var h = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        var screenRect = new PixelRect(x, y, w, h);

        rect = rect.Intersect(screenRect);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var gdiBitmap = new System.Drawing.Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(gdiBitmap))
        {
            graphics.CopyFromScreen(rect.X, rect.Y, 0, 0, new Size(rect.Width, rect.Height));
        }

        var data = gdiBitmap.LockBits(
            new Rectangle(0, 0, gdiBitmap.Width, gdiBitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(
                Avalonia.Platform.PixelFormat.Bgra8888,
                AlphaFormat.Opaque,
                data.Scan0,
                rect.Size,
                new Vector(96d, 96d),
                data.Stride);
        }
        finally
        {
            gdiBitmap.UnlockBits(data);
        }

        return bitmap;
    }
}