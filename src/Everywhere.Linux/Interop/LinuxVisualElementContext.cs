using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Linux.Interop;

/// <summary>
/// Linux IVisualElementContext Impl:
/// Using ILinuxWindowBackend for keyboard/mouse event and windows info.
/// Using AtspiService to access UI elements inside window.
/// </summary>
public partial class LinuxVisualElementContext(
    ILinuxWindowBackend backend,
    ILinuxEventHelper eventHelper,
    ILogger<LinuxVisualElementContext> logger
)
    : IVisualElementContext
{
    public IVisualElement? KeyboardFocusedElement
    {
        get
        {
            try
            {
                // some app do not support atspi focus event
                // ensure the app is correct by pid
                // however, atspi element is more detailed, so return null when incorrect
                // to let atspi pointer search do the work
                var focused = _atspi.ElementFocused();
                var focusedWindow = backend.GetFocusedWindowElement();
                // for Non X11 session, the window may get null, and not equal to that atspi gives
                return (focusedWindow == null || (focused?.ProcessId == focusedWindow?.ProcessId)) ? focused : null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AtspiFocusedElement failed");
                return null;
            }
        }
    }

    private readonly ILinuxEventHelper _eventHelper = eventHelper;
    private readonly AtspiService _atspi = new(backend);

    public IVisualElement? ElementFromPoint(PixelPoint point, ElementPickMode mode = ElementPickMode.Element)
    {
        try
        {
            switch (mode)
            {
                case ElementPickMode.Element:
                    var win = backend.GetWindowElementAt(point);
                    var elem = _atspi.ElementFromPoint(point, win.ProcessId);
                    return elem ?? win; // fallback to window mode

                case ElementPickMode.Window:
                    return backend.GetWindowElementAt(point);

                case ElementPickMode.Screen:
                    return backend.GetScreenElement();

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ElementFromPoint failed for point {point}, mode {mode}", point, mode);
            return null;
        }
    }

    public IVisualElement? ElementFromPointer(ElementPickMode mode = ElementPickMode.Element)
    {
        var point = backend.GetPointer();
        return ElementFromPoint(point, mode);
    }

    public async Task<IVisualElement?> PickElementAsync(ElementPickMode mode)
    {
        if (Application.Current is not { ApplicationLifetime: ClassicDesktopStyleApplicationLifetime desktopLifetime })
        {
            return null;
        }

        var windows = desktopLifetime.Windows.AsValueEnumerable().Where(w => w.IsVisible).ToList();
        foreach (var window in windows) window.Hide();
        var result = await ElementPicker.PickAsync(this, backend, mode);
        foreach (var window in windows) window.IsVisible = true;
        return result;
    }


}