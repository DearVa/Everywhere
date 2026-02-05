using Avalonia;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Linux.Interop.AtspiBackend.Core;
using Everywhere.Linux.Interop.AtspiBackend.Elements;
using Everywhere.Linux.Interop.AtspiBackend.Native;
using GObj = GObject.Object;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop;

/// <summary>
/// Facade entry point for AT-SPI service providing ElementFromPoint, GetFocused, etc.
/// Coordinates between AtspiContext, AtspiEventHub, AtspiCacheManager, and ElementFactory.
/// See: https://gnome.pages.gitlab.gnome.org/at-spi2-core/libatspi/index.html
/// </summary>
public sealed class AtspiService : IDisposable
{
    private readonly AtspiContext _context;
    private readonly AtspiEventHub _eventHub;
    private readonly AtspiCacheManager _cache;
    private readonly AtspiElementFactory _factory;
    private readonly IWindowBackend _windowBackend;
    private readonly ILogger<AtspiService> _logger;
    private readonly GObj _desktop;
    private bool _disposed;

    public AtspiService(IWindowBackend windowBackend, ILogger<AtspiService> logger)
    {
        _windowBackend = windowBackend ?? throw new ArgumentNullException(nameof(windowBackend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _context = new AtspiContext(logger);
        _cache = new AtspiCacheManager(logger);
        _factory = new AtspiElementFactory(_cache, _windowBackend, logger);
        _eventHub = new AtspiEventHub(logger);

        var desktopPtr = AtspiNative.atspi_get_desktop(0);
        if (desktopPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to get AT-SPI desktop root");
        }
        _desktop = GObjectWrapper.WrapSingleton(desktopPtr);

        _eventHub.RegisterEvents();

        _eventHub.ElementDefunct += OnElementDefunct;

        _logger.LogInformation("AtspiService initialized successfully");
    }

    public IVisualElement? ElementFromWindow(PixelPoint point, IVisualElement window)
    {
        EnsureNotDisposed();

        if (window == null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        try
        {
            // Find application element by PID
            var appElement = FindApplicationByPid(window.ProcessId);
            if (appElement == null)
            {
#if DEBUG
                _logger.LogDebug(
                    "Application with PID {ProcessId} not found or does not support AT-SPI",
                    window.ProcessId);
#endif
                return null;
            }

            // Perform hit test from app root
            var element = ElementFromPoint(appElement, point, out _);

#if DEBUG
            if (element == null)
            {
                _logger.LogDebug("No element found at point {Point}", point);
            }
            else
            {
                _logger.LogDebug(
                    "Found element at {Point}: {Name} ({Type})",
                    point,
                    element.Name,
                    element.Type);
            }
#endif

            return element;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding element at point {Point}", point);
            return null;
        }
    }

    public IVisualElement? ElementFocused()
    {
        EnsureNotDisposed();

        try
        {
            var focusedGObj = _eventHub.GetFocusedElement();
            return _factory.GetOrCreateElement(focusedGObj);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting focused element");
            return null;
        }
    }

    private AtspiElement? FindApplicationByPid(int pid)
    {
        var desktopElement = _factory.GetOrCreateElement(_desktop);
        if (desktopElement == null)
        {
            return null;
        }

        // Iterate desktop children to find matching app
        foreach (var child in desktopElement.Children
            .OfType<AtspiElement>()
            .OrderByDescending(e => e.Order))
        {
            if (child.ProcessId == pid)
            {
                return child;
            }
        }

        return null;
    }

    private AtspiElement? ElementFromPoint(
        AtspiElement element,
        PixelPoint point,
        out int depth)
    {
        depth = 0;

        if (element == null)
        {
            return null;
        }

        // Check visibility (skip for root search)
        if (!element.IsVisible(treatApplicationAsVisible: true))
        {
            return null;
        }

        // Check if point is within bounds
        var bounds = element.BoundingRectangle;
        if (bounds.Width > 0 && bounds.Height > 0 && !bounds.Contains(point))
        {
            return null;
        }

        // Search children (ordered by z-order descending - top to bottom)
        AtspiElement? deepestChild = null;
        var maxDepth = -1;

        foreach (var child in element.Children
            .OfType<AtspiElement>()
            .OrderByDescending(e => e.Order))
        {
            var found = ElementFromPoint(child, point, out var childDepth);
            
            if (found != null && childDepth > maxDepth)
            {
                maxDepth = childDepth;
                deepestChild = found;
            }
        }

        // If child found, return it with incremented depth
        if (deepestChild != null)
        {
            depth = maxDepth + 1;
            return deepestChild;
        }

        // No child hit - return this element if point is inside and visible
        if (bounds.Width > 0 && bounds.Height > 0 && 
            bounds.Contains(point) && element.IsVisible())
        {
            return element;
        }

        return null;
    }

    private void OnElementDefunct(object? sender, ElementDefunctEventArgs e)
    {
        if (e.DefunctElement != null)
        {
            _factory.RemoveElement(e.DefunctElement);
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AtspiService));
        }

        _context.EnsureInitialized();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _eventHub.ElementDefunct -= OnElementDefunct;
            _eventHub.Dispose();
            _context.Dispose();
            _cache.Clear();

            _logger.LogInformation("AtspiService disposed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing AtspiService");
        }
    }
}