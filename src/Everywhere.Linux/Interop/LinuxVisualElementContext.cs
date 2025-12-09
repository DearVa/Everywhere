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
public partial class LinuxVisualElementContext: IVisualElementContext
{
    public readonly ILinuxWindowBackend _backend;
    public readonly ILinuxEventHelper _eventHelper;
    private readonly AtspiService _atspi;
    private readonly ILogger<LinuxVisualElementContext> _logger;
    public IVisualElement? KeyboardFocusedElement {
        get
        {
            try
            {
                // some app do not support atspi focus event
                // ensure the app is correct by pid
                // however, atspi element is more detailed, so return null when incorrect
                // to let atspi pointer search do the work
                var focused = _atspi.ElementFocused();
                var focusedWindow =  _backend.GetFocusedWindowElement();
                // for Non X11 session, the window may get null, and not equal to that atspi gives
                return (focusedWindow == null || (focused?.ProcessId == focusedWindow?.ProcessId)) ? focused: null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AtspiFocusedElement failed");
                return null;
            }
        }
    }

    public event IVisualElementContext.KeyboardFocusedElementChangedHandler? KeyboardFocusedElementChanged;

    public LinuxVisualElementContext(
        ILinuxWindowBackend backend, 
        ILinuxEventHelper eventHelper,
        ILogger<LinuxVisualElementContext> logger)
    {
        _backend = backend;
        _eventHelper = eventHelper;
        _atspi = new AtspiService(this);
        _logger = logger;
    }
    

    public IVisualElement? ElementFromPoint(PixelPoint point, PickElementMode mode = PickElementMode.Element)
    {
        try
        {
            switch (mode)
            {
                case PickElementMode.Element:
                    var win = _backend.GetWindowElementAt(point);
                    var elem = _atspi.ElementFromPoint(point, win.ProcessId);
                    return elem ?? win; // fallback to window mode
                    
                case PickElementMode.Window:
                    return _backend.GetWindowElementAt(point);
                    
                case PickElementMode.Screen:
                    return _backend.GetScreenElement();
                    
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ElementFromPoint failed for point {point}, mode {mode}", point, mode);
            return null;
        }
    }

    public IVisualElement? ElementFromPointer(PickElementMode mode = PickElementMode.Element)
    {
        var point = _backend.GetPointer();
        return ElementFromPoint(point, mode);
    }

    public async Task<IVisualElement?> PickElementAsync(PickElementMode mode)
    {
        if (Application.Current is not { ApplicationLifetime: ClassicDesktopStyleApplicationLifetime desktopLifetime })
        {
            return null;
        }

        var windows = desktopLifetime.Windows.AsValueEnumerable().Where(w => w.IsVisible).ToList();
        foreach (var window in windows) window.Hide();
        var result = await ElementPicker.PickAsync(this, _backend, mode);
        foreach (var window in windows) window.IsVisible = true;
        return result;
    }

    
}
