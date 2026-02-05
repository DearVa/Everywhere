using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Interop;
using Everywhere.Linux.Interop.AtspiBackend.Native;
using GObj = GObject.Object;
using GObjHandle = GObject.Internal.ObjectHandle;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop.AtspiBackend.Elements;

public partial class AtspiElement : IVisualElement
{
    protected readonly GObj _gobject;
    protected readonly AtspiElementFactory _factory;
    protected readonly ILogger _logger;
    private readonly IWindowBackend _windowBackend;
    private readonly List<GObj> _cachedChildren = new();
    private readonly Lock _childrenLock = new();
    private bool _childrenCached;

    public AtspiElement(
        GObj gobject,
        AtspiElementFactory factory,
        IWindowBackend windowBackend,
        ILogger logger)
    {
        _gobject = gobject ?? throw new ArgumentNullException(nameof(gobject));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _windowBackend = windowBackend ?? throw new ArgumentNullException(nameof(windowBackend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    public string? Name
    {
        get
        {
            try
            {
                return AtspiNative.atspi_accessible_get_name(_gobject.Handle, IntPtr.Zero);
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Failed to get element name");
                return null;
            }
        }
    }

    public string Id
    {
        get
        {
            try
            {
                var id = AtspiNative.atspi_accessible_get_accessible_id(_gobject.Handle, IntPtr.Zero);
                return string.IsNullOrEmpty(id) ? _gobject.GetHashCode().ToString("X") : id;
            }
            catch
            {
                return _gobject.GetHashCode().ToString("X");
            }
        }
    }

    public int ProcessId
    {
        get
        {
            try
            {
                return AtspiNative.atspi_accessible_get_process_id(_gobject.Handle, IntPtr.Zero);
            }
            catch
            {
                return 0;
            }
        }
    }

    public VisualElementType Type => GetElementType();

    public VisualElementStates States => GetElementStates();

    public IVisualElement? Parent
    {
        get
        {
            try
            {
                var relatedParent = _factory.TryGetRelationTarget(_gobject, up: true);
                if (relatedParent != null)
                {
                    return _factory.GetOrCreateElement(relatedParent);
                }
                var parentPtr = AtspiNative.atspi_accessible_get_parent(_gobject.Handle, IntPtr.Zero);
                if (parentPtr == IntPtr.Zero)
                {
                    return null;
                }

                var parent = Core.GObjectWrapper.WrapOwned(parentPtr);
                
                // DO NOT expose Application role as parent
                if (parent != null && AtspiNative.atspi_accessible_is_application(parent.Handle) != 0)
                {
                    return null;
                }

                return _factory.GetOrCreateElement(parent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get parent element");
                return null;
            }
        }
    }

    public IEnumerable<IVisualElement> Children
    {
        get
        {
            EnsureChildrenCached();

            foreach (var child in _cachedChildren)
            {
                var element = _factory.GetOrCreateElement(child);
                if (element != null)
                {
                    yield return element;
                }
            }
        }
    }

    public VisualElementSiblingAccessor SiblingAccessor =>
        new AtspiSiblingAccessor(this, Parent as AtspiElement);
    public PixelRect BoundingRectangle => GetBounds(AtspiCoordType.Screen);

    public int Order => CalculateZOrder();

    public nint NativeWindowHandle => OwnerWindow?.NativeWindowHandle ?? IntPtr.Zero;

    public PixelRect GetBounds(AtspiCoordType coordType)
    {
        try
        {
            // If not a component, compute union of children bounds
            if (AtspiNative.atspi_accessible_is_component(_gobject.Handle) == 0)
            {
                return ComputeUnionBounds();
            }

            var rectPtr = AtspiNative.atspi_component_get_extents(
                _gobject.Handle,
                (int)coordType,
                IntPtr.Zero);

            if (rectPtr == IntPtr.Zero)
            {
                return new PixelRect();
            }

            var rect = Marshal.PtrToStructure<AtspiNative.AtspiRect>(rectPtr);
            AtspiNative.g_free(rectPtr);

            return new PixelRect(rect.x, rect.y, rect.width, rect.height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get element bounds");
            return new PixelRect();
        }
    }

    public bool IsVisible(bool treatApplicationAsVisible = false)
    {
        if (States.HasFlag(VisualElementStates.Offscreen))
        {
            return false;
        }

        if (treatApplicationAsVisible &&
            AtspiNative.atspi_accessible_is_application(_gobject.Handle) != 0)
        {
            return true;
        }

        if (AtspiNative.atspi_accessible_is_component(_gobject.Handle) == 0)
        {
            return false;
        }

        var bounds = BoundingRectangle;
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken)
    {
        var bounds = BoundingRectangle;
        
        // Translate to window-relative coordinates if we have owner window
        if (OwnerWindow != null)
        {
            var windowPos = OwnerWindow.BoundingRectangle.Position;
            bounds = bounds.Translate(-(PixelVector)windowPos);
        }

        return Task.FromResult(_windowBackend.Capture(this, bounds));
    }
    internal int GetIndexInParent()
    {
        try
        {
            return AtspiNative.atspi_accessible_get_index_in_parent(_gobject.Handle, IntPtr.Zero);
        }
        catch
        {
            return -1;
        }
    }

    internal void EnsureChildrenCached()
    {
        lock (_childrenLock)
        {
            if (_childrenCached)
            {
                return;
            }

            _cachedChildren.Clear();

            // Check for embedded subwindow relationships
            var relatedChild = _factory.TryGetRelationTarget(_gobject, up: false);
            if (relatedChild != null && IsElementVisible(relatedChild, treatAppAsVisible: true))
            {
                _cachedChildren.Add(relatedChild);
            }
            else
            {
                // Get standard children
                var childCount = AtspiNative.atspi_accessible_get_child_count(
                    _gobject.Handle,
                    IntPtr.Zero);

                for (var i = 0; i < childCount; i++)
                {
                    try
                    {
                        var childPtr = AtspiNative.atspi_accessible_get_child_at_index(
                            _gobject.Handle,
                            i,
                            IntPtr.Zero);

                        if (childPtr == IntPtr.Zero)
                        {
                            continue;
                        }

                        var child = Core.GObjectWrapper.WrapOwned(childPtr);
                        
                        if (child != null && IsElementVisible(child, treatAppAsVisible: true))
                        {
                            _cachedChildren.Add(child);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get child at index {Index}", i);
                    }
                }
            }

            _childrenCached = true;
        }
    }
    private IVisualElement? _ownerWindow;
    
    private IVisualElement? OwnerWindow
    {
        get
        {
            // Cache owner window lookup
            if (_ownerWindow != null)
            {
                return _ownerWindow;
            }

            _ownerWindow = _windowBackend.GetWindowElementByInfo(ProcessId, BoundingRectangle)
                ?? (Parent as AtspiElement)?.OwnerWindow;

            return _ownerWindow;
        }
    }

    private VisualElementType GetElementType()
    {
        try
        {
            var role = (AtspiRole)AtspiNative.atspi_accessible_get_role(_gobject.Handle, IntPtr.Zero);
            
            // Check for top-level windows (Frame/Window under Application)
            if (role is AtspiRole.Frame or AtspiRole.Window)
            {
                var parentPtr = AtspiNative.atspi_accessible_get_parent(_gobject.Handle, IntPtr.Zero);
                if (parentPtr != IntPtr.Zero)
                {
                    var parent = Core.GObjectWrapper.WrapOwned(parentPtr);
                    if (parent != null && AtspiNative.atspi_accessible_is_application(parent.Handle) != 0)
                    {
                        return VisualElementType.TopLevel;
                    }
                }
            }

            return role switch
            {
                AtspiRole.Application => VisualElementType.TopLevel,
                AtspiRole.Button or AtspiRole.ToggleButton or AtspiRole.PushButton 
                    => VisualElementType.Button,
                AtspiRole.CheckBox or AtspiRole.Switch 
                    => VisualElementType.CheckBox,
                AtspiRole.ComboBox 
                    => VisualElementType.ComboBox,
                AtspiRole.DocumentEmail or AtspiRole.DocumentFrame or AtspiRole.DocumentPresentation or 
                AtspiRole.DocumentSpreadsheet or AtspiRole.DocumentText or AtspiRole.DocumentWeb or 
                AtspiRole.HtmlContainer or AtspiRole.Article 
                    => VisualElementType.Document,
                AtspiRole.Entry or AtspiRole.Editbar or AtspiRole.PasswordText 
                    => VisualElementType.TextEdit,
                AtspiRole.Image or AtspiRole.DesktopIcon or AtspiRole.Icon 
                    => VisualElementType.Image,
                AtspiRole.Label or AtspiRole.Text or AtspiRole.Footer or AtspiRole.Caption or 
                AtspiRole.Comment or AtspiRole.DescriptionTerm or AtspiRole.Footnote or 
                AtspiRole.Paragraph or AtspiRole.DescriptionValue 
                    => VisualElementType.Label,
                AtspiRole.Header 
                    => VisualElementType.Header,
                AtspiRole.Link 
                    => VisualElementType.Hyperlink,
                AtspiRole.List or AtspiRole.ListBox or AtspiRole.DescriptionList 
                    => VisualElementType.ListView,
                AtspiRole.ListItem 
                    => VisualElementType.ListViewItem,
                AtspiRole.Menu or AtspiRole.LandMark 
                    => VisualElementType.Menu,
                AtspiRole.MenuItem or AtspiRole.CheckMenuItem or AtspiRole.TearoffMenuItem 
                    => VisualElementType.MenuItem,
                AtspiRole.PageTabList 
                    => VisualElementType.TabControl,
                AtspiRole.PageTab 
                    => VisualElementType.TabItem,
                AtspiRole.Panel or AtspiRole.ScrollPane or AtspiRole.RootPane or AtspiRole.Canvas or 
                AtspiRole.Frame or AtspiRole.Window or AtspiRole.Section 
                    => VisualElementType.Panel,
                AtspiRole.ProgressBar 
                    => VisualElementType.ProgressBar,
                AtspiRole.RadioButton 
                    => VisualElementType.RadioButton,
                AtspiRole.ScrollBar 
                    => VisualElementType.ScrollBar,
                AtspiRole.SpinButton 
                    => VisualElementType.Spinner,
                AtspiRole.SplitPane 
                    => VisualElementType.Splitter,
                AtspiRole.StatusBar 
                    => VisualElementType.StatusBar,
                AtspiRole.Slider 
                    => VisualElementType.Slider,
                AtspiRole.Table or AtspiRole.Form 
                    => VisualElementType.Table,
                AtspiRole.TableRow 
                    => VisualElementType.TableRow,
                AtspiRole.ToolBar 
                    => VisualElementType.ToolBar,
                AtspiRole.Tree 
                    => VisualElementType.TreeViewItem,
                AtspiRole.TreeTable 
                    => VisualElementType.TreeView,
                _ => VisualElementType.Unknown
            };
        }
        catch
        {
            return VisualElementType.Unknown;
        }
    }

    private VisualElementStates GetElementStates()
    {
        try
        {
            var states = VisualElementStates.None;
            var stateSetPtr = AtspiNative.atspi_accessible_get_state_set(_gobject.Handle);
            
            if (stateSetPtr == IntPtr.Zero)
            {
                return states;
            }

            var stateSet = Core.GObjectWrapper.WrapOwned(stateSetPtr);
            if (stateSet == null)
            {
                return states;
            }

            // Check visibility states
            var isVisible = AtspiNative.atspi_state_set_contains(
                stateSet.Handle, (int)AtspiState.Visible) != 0;
            var isShowing = AtspiNative.atspi_state_set_contains(
                stateSet.Handle, (int)AtspiState.Showing) != 0;
            
            if (!isVisible || !isShowing)
            {
                states |= VisualElementStates.Offscreen;
            }

            // Check enabled state
            if (AtspiNative.atspi_state_set_contains(
                stateSet.Handle, (int)AtspiState.Enabled) == 0)
            {
                states |= VisualElementStates.Disabled;
            }

            // Check focus state
            if (AtspiNative.atspi_state_set_contains(
                stateSet.Handle, (int)AtspiState.Focused) != 0)
            {
                states |= VisualElementStates.Focused;
            }

            // Check selected state
            if (AtspiNative.atspi_state_set_contains(
                stateSet.Handle, (int)AtspiState.Selected) != 0)
            {
                states |= VisualElementStates.Selected;
            }

            // Check editable state
            if (AtspiNative.atspi_state_set_contains(
                stateSet.Handle, (int)AtspiState.Editable) == 0)
            {
                states |= VisualElementStates.ReadOnly;
            }

            // Check for password field
            var role = AtspiNative.atspi_accessible_get_role(_gobject.Handle, IntPtr.Zero);
            if (role == (int)AtspiRole.PasswordText)
            {
                states |= VisualElementStates.Password;
            }

            return states;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get element states");
            return VisualElementStates.None;
        }
    }

    private int CalculateZOrder()
    {
        try
        {
            var layer = (AtspiLayer)AtspiNative.atspi_component_get_layer(
                _gobject.Handle,
                IntPtr.Zero);
            
            var mdiZ = AtspiNative.atspi_component_get_mdi_z_order(
                _gobject.Handle,
                IntPtr.Zero);
            
            var indexInParent = GetIndexInParent();

            // Composite z-order: layer (highest weight) + mdi z-order + index (inverted)
            return LayerOrder(layer) * 256 + mdiZ * 16 - indexInParent;
        }
        catch
        {
            return 0;
        }
    }

    private static int LayerOrder(AtspiLayer layer)
    {
        return layer switch
        {
            AtspiLayer.Background => 1,
            AtspiLayer.Window => 2,
            AtspiLayer.Mdi => 3,
            AtspiLayer.Canvas => 4,
            AtspiLayer.Widget => 5,
            AtspiLayer.Popup => 6,
            AtspiLayer.Overlay => 7,
            _ => 0
        };
    }

    private PixelRect ComputeUnionBounds()
    {
        var union = new PixelRect();
        
        foreach (var child in Children)
        {
            union = union.Union(child.BoundingRectangle);
        }

        return union;
    }

    private bool IsElementVisible(GObj? element, bool treatAppAsVisible = false)
    {
        if (element == null)
        {
            return false;
        }

        try
        {
            var stateSetPtr = AtspiNative.atspi_accessible_get_state_set(element.Handle);
            if (stateSetPtr != IntPtr.Zero)
            {
                var stateSet = Core.GObjectWrapper.WrapOwned(stateSetPtr);
                if (stateSet == null)
                {
                    return false;
                }
                
                var isVisible = AtspiNative.atspi_state_set_contains(
                    stateSet.Handle, (int)AtspiState.Visible) != 0;
                var isShowing = AtspiNative.atspi_state_set_contains(
                    stateSet.Handle, (int)AtspiState.Showing) != 0;

                if (!isVisible || !isShowing)
                {
                    return false;
                }
            }

            if (treatAppAsVisible && 
                AtspiNative.atspi_accessible_is_application(element.Handle) != 0)
            {
                return true;
            }

            if (AtspiNative.atspi_accessible_is_component(element.Handle) == 0)
            {
                return false;
            }
            var rectPtr = AtspiNative.atspi_component_get_extents(
                element.Handle,
                (int)AtspiCoordType.Screen,
                IntPtr.Zero);

            if (rectPtr == IntPtr.Zero)
            {
                return false;
            }

            var rect = Marshal.PtrToStructure<AtspiNative.AtspiRect>(rectPtr);
            AtspiNative.g_free(rectPtr);

            return rect.width > 0 && rect.height > 0;
        }
        catch
        {
            return false;
        }
    }
}
internal sealed class AtspiSiblingAccessor : VisualElementSiblingAccessor
{
    private readonly AtspiElement _element;
    private readonly AtspiElement? _parentElement;
    private List<IVisualElement>? _siblings;

    public AtspiSiblingAccessor(AtspiElement element, AtspiElement? parentElement)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _parentElement = parentElement;
    }

    protected override void EnsureResources()
    {
        if (_siblings != null)
        {
            return;
        }

        _siblings = new List<IVisualElement>();
        
        if (_parentElement != null)
        {
            _siblings.AddRange(_parentElement.Children);
        }
    }

    protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
    {
        if (_siblings == null)
        {
            yield break;
        }

        var currentIndex = _siblings.IndexOf(_element);
        if (currentIndex < 0)
        {
            yield break;
        }

        for (var i = currentIndex + 1; i < _siblings.Count; i++)
        {
            yield return _siblings[i];
        }
    }

    protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
    {
        if (_siblings == null)
        {
            yield break;
        }

        var currentIndex = _siblings.IndexOf(_element);
        if (currentIndex <= 0)
        {
            yield break;
        }

        for (var i = currentIndex - 1; i >= 0; i--)
        {
            yield return _siblings[i];
        }
    }

    protected override void ReleaseResources()
    {
        _siblings?.Clear();
        _siblings = null;
    }
}
