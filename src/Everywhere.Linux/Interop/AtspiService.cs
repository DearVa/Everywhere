using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using CSharpMath;
using Everywhere.Common;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop;

/// <summary>
/// AT-SPI（Assistive Technology Service Provider Interface）
/// </summary>
public partial class AtspiService
{
    private readonly bool _initialized;
    private readonly LinuxVisualElementContext _context;
    private readonly ConcurrentDictionary<IntPtr, AtspiVisualElement> _cachedElement = new();
    private readonly ILogger<AtspiService> _logger = ServiceLocator.Resolve<ILogger<AtspiService>>();
    private readonly AtspiEventListenerCallback _eventCallback;
    private IntPtr _eventListener;
    private IntPtr _focusedElement;
    private Lock _focusLock = new();
    
    public AtspiService(LinuxVisualElementContext context)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AT_SPI_BUS")))
            Environment.SetEnvironmentVariable("AT_SPI_BUS", "session");
        
        if (atspi_init() < 0)
            throw new InvalidOperationException("Failed to initialize AT-SPI");
        _eventCallback = OnEvent;
        _eventListener = atspi_event_listener_new(_eventCallback, IntPtr.Zero, IntPtr.Zero);
        atspi_event_listener_register(_eventListener, 
            Marshal.StringToCoTaskMemUTF8("object:state-changed:focused"), IntPtr.Zero);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            atspi_event_main();
        });
        _context = context;
        _initialized = true;
    }

    private void CheckInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("At-SPI not initialized");
        }
    }
    
    ~AtspiService()
    {
        if (_eventListener != IntPtr.Zero)
        {
            atspi_event_listener_deregister(_eventListener, 
                Marshal.StringToCoTaskMemUTF8("object:state-changed:focused"), IntPtr.Zero);
            _eventListener = IntPtr.Zero;
        }
        atspi_exit();
    }

    private readonly static List<string> DesktopAppName = ["plasmashell", "gnome-shell", "xfdesktop", "caja", "nemo", "pcmanfm"];
    private static bool IsAppNameDesktop(string? appName)
    {
        return appName != null && DesktopAppName.Any(appName.Contains);
    }

    private void OnEvent(IntPtr atspiEventPtr, IntPtr userData)
    {
        try
        {
            var ev = Marshal.PtrToStructure<AtspiEvent>(atspiEventPtr);
            var eventType = Marshal.PtrToStringAnsi(ev.type) ?? string.Empty;
            if (eventType.Contains("focused") && ev.source != IntPtr.Zero)
            {
                lock (_focusLock)
                {
                    if (ev.detail1 == 1) // focus in
                    {
                        var element = ev.source;
                        if (_focusedElement != IntPtr.Zero)
                        {
                            g_object_unref(_focusedElement);
                        }
                        // var appName = Process.GetProcessById(ElementPid(element)).ProcessName;
                        // _logger.LogInformation("Focus in: {app} - {Name}", appName, ElementName(element));
                        _focusedElement = element;
                        g_object_ref(_focusedElement);
                    }
                    else
                    {
                        if (_focusedElement != IntPtr.Zero)
                        {
                            g_object_unref(_focusedElement);
                        }
                        _focusedElement = IntPtr.Zero;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnEvent failed: {Message}", ex.Message);
        }
    }

    private static string? ElementName(IntPtr elem)
    {
        try
        {
            var namePtr = atspi_accessible_get_name(elem, IntPtr.Zero);
            if (namePtr == IntPtr.Zero)
            {
                return null;
            }
            var name = Marshal.PtrToStringUTF8(namePtr);
            g_free(namePtr);
            return name;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static int ElementPid(IntPtr elem)
    {
        var pid = atspi_accessible_get_process_id(elem, IntPtr.Zero);
        return pid;
    }
    
    private static PixelRect ElementBounds(IntPtr elem)
    {
        var rectPtr = atspi_component_get_extents(elem, AtspiCoordTypeScreen, IntPtr.Zero);
        var rect = Marshal.PtrToStructure<AtspiRect>(rectPtr);
        g_free(rectPtr);
        return new PixelRect(rect.x, rect.y, rect.width, rect.height);
    }

    private static VisualElementStates ElementState(IntPtr elem)
    {
        try
        {
            var states = VisualElementStates.None;
            var elemStateset = atspi_accessible_get_state_set(elem);
            if ((atspi_state_set_contains(elemStateset, STATE_VISIBLE) == 0)
                || (atspi_state_set_contains(elemStateset, STATE_SHOWING) == 0)) 
                states |= VisualElementStates.Offscreen;
            if (atspi_state_set_contains(elemStateset, STATE_ENABLE) == 0) states |= VisualElementStates.Disabled;
            if (atspi_state_set_contains(elemStateset, STATE_FOCUSED) == 1) states |= VisualElementStates.Focused;
            if (atspi_state_set_contains(elemStateset, STATE_SELECTED) == 1) states |= VisualElementStates.Selected;
            if (atspi_state_set_contains(elemStateset, STATE_EDITABLE) == 0) states |= VisualElementStates.ReadOnly;
            g_object_unref(elemStateset);
            if (atspi_accessible_get_role(elem, IntPtr.Zero) == ROLE_PASSWORD_TEXT) 
                states |= VisualElementStates.Password;
            return states;
        }
        catch (COMException)
        {
            return VisualElementStates.None;
        }
    }
    
    private IntPtr GArrayIndex(IntPtr array, int index)
    {
        // note: in glib garray structure, data pointer is always located at the beginning
        // see glib/garray.h
        IntPtr data = Marshal.ReadIntPtr(array);
        return Marshal.ReadIntPtr(data,  IntPtr.Size * index);
    }

    private int GArrayLength(IntPtr array)
    {
        // garray structure length is located after data pointer
        return Marshal.ReadInt32(array, IntPtr.Size);
    }
        
    private IntPtr TryMatchRelationWindow(IntPtr window, bool down)
    {
        var array = atspi_accessible_get_relation_set(window, IntPtr.Zero);
        var count = GArrayLength(array);
        for (var i = 0; i < count; i++)
        {
            var relation = GArrayIndex(array, i);
            if (relation != IntPtr.Zero)
            {
                var type = atspi_relation_get_relation_type(relation);
                var nTarget = atspi_relation_get_n_targets(relation);
                if (((type is RELATION_EMBEDS or RELATION_SUBWINDOW_OF && down) 
                        ||(type is RELATION_EMBEDDED_BY && (!down)))
                    && nTarget == 1)
                {
                    var target = atspi_relation_get_target(relation, 0);
                    if (target != IntPtr.Zero && ElementVisible(target))
                    {
                        return target;
                    }
                }
            }
        }
        return IntPtr.Zero;
    }
    
    private bool ElementVisible(IntPtr elem, bool includeApp = false)
    {
        if (ElementState(elem).HasFlag(VisualElementStates.Offscreen)) return false;
        if (includeApp && atspi_accessible_is_application(elem) != 0)
        {
            return true;
        }
        if (atspi_accessible_is_component(elem) == 0) return false;
        var rect = ElementBounds(elem);
        return rect is { Height: > 0, Width: > 0 };
    }

    private IEnumerable<AtspiVisualElement> ElementChildren(IntPtr elem)
    {
        var relatedSubWindow = TryMatchRelationWindow(elem, true);
        if (relatedSubWindow != IntPtr.Zero && ElementVisible(relatedSubWindow, true))
        {
            var sub = GetAtspiVisualElement(() => relatedSubWindow);
            if (sub != null)
            {
                yield return sub;
            }
        }
        else
        {
            var count = atspi_accessible_get_child_count(elem, IntPtr.Zero);
            var i = 0;
            while (i < count)
            {
                var child = atspi_accessible_get_child_at_index(elem, i, IntPtr.Zero);
                if (child != IntPtr.Zero && ElementVisible(child, true))
                {
                    var childElem = GetAtspiVisualElement(() => child);
                    if (childElem != null)
                    {
                        yield return childElem;
                    }
                }
                i++;
            }
        }
    }
    
    private AtspiVisualElement? AtspiElementFromPoint(AtspiVisualElement? parent, PixelPoint point, bool root = false)
    {
        
        parent ??= GetAtspiVisualElement(() => atspi_get_desktop(0));
        if (parent is null)
        {
            return null;
        }
        if (!root && !ElementVisible(parent._element, true))
        {
            return null;
        }
        _logger.LogInformation("> {Name} {States}", parent.Name, parent.States);
        var rect = parent.BoundingRectangle;
        if (rect is { Height: > 0, Width: > 0 } && !rect.Contains(point))
        {
            return null;
        }
        foreach (var child in ElementChildren(parent._element)
                     .OrderByDescending(child => child.Order))
        {
            var found = AtspiElementFromPoint(child, point);
            if (found != null)
            {
                return found;
            }
        }
        
        if (rect is { Height: > 0, Width: > 0 } && rect.Contains(point) && ElementVisible(parent._element))
        {
            return parent;
        }
        
        return null;
    }

    private AtspiVisualElement? AtspiAppElementByPid(int pid)
    {
        var root = GetAtspiVisualElement(() => atspi_get_desktop(0));
        if (root is null)
        {
            return null;
        }
        foreach (var child in ElementChildren(root._element)
                     .OrderByDescending(child => child.Order))
        {
            if (child.ProcessId == pid)
            {
                return child;
            }
        }
        return null;
    }

    public IVisualElement? ElementFromPoint(PixelPoint point, int pid)
    {
        CheckInitialized();
        var app = AtspiAppElementByPid(pid);
        if (app == null)
        {
            _logger.LogInformation("App {pid} do not support At-SPI", pid);
            return null;
        }
        var elem = AtspiElementFromPoint(app, point, true);
        if (elem == null)
        { 
            _logger.LogWarning("AtspiElementFromPoint {Point} not found", point);
        }
        else
        {
            _logger.LogInformation("AtspiElementFromPoint {Point} found: {Name}, {Rect}",
                point, elem.Name, elem.BoundingRectangle);
        }
        return elem;
    }

    public IVisualElement? ElementFocused()
    {
        try
        {
            CheckInitialized();
            return GetAtspiVisualElement(() => _focusedElement);
        } 
        catch (Exception ex)
        {
            _logger.LogError(ex, "AtspiFocusedElement failed");
            return null;
        }
    }
    
    private class AtspiVisualElement(AtspiService atspi, IntPtr elementPtr) 
        : IVisualElement, IDisposable
    {
        public readonly IntPtr _element = g_object_ref(elementPtr);
        private readonly List<IntPtr> _cachedAccessibleChildren = [];
        private bool _childrenCached;
        private bool _disposed;
        private readonly Lock _childrenLoading = new ();

        public void Dispose()
        {
            if (!_disposed && _element != IntPtr.Zero)
            {
                g_object_unref(_element);
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~AtspiVisualElement()
        {
            Dispose();
        }
        

        public IVisualElement? Parent
        {
            get
            {
                if (_element == IntPtr.Zero) return null;
                var parent = atspi.TryMatchRelationWindow(_element, false);
                if (parent == IntPtr.Zero)
                {
                    parent = atspi_accessible_get_parent(_element, IntPtr.Zero);
                }
                if (parent == IntPtr.Zero) return null;
                return atspi_accessible_is_application(parent) != 0 ? 
                    null : atspi.GetAtspiVisualElement(() => parent);
            }
        }

        private int IndexInParent => _element == IntPtr.Zero ? 0 
                            : atspi_accessible_get_index_in_parent(_element, IntPtr.Zero);

        private void ensureChildCached()
        {
            lock (_childrenLoading)
            {
                if (!_childrenCached)
                {
                    _cachedAccessibleChildren.Clear();
                    foreach (var elem in atspi.ElementChildren(_element))
                    {
                        _cachedAccessibleChildren.Add(elem._element);
                    }
                    _childrenCached = true;
                }
            }
        }
        
        public IEnumerable<IVisualElement> Children
        {
            get
            {
                if (_element == IntPtr.Zero)
                    yield break;
                ensureChildCached();
                foreach(var child in _cachedAccessibleChildren)
                {
                    yield return atspi._cachedElement[child];
                }
            }
        }

        private class AtspiSiblingAccessor(
            AtspiService atspi,
            AtspiVisualElement? parent, 
            AtspiVisualElement element) : VisualElementSiblingAccessor
        {
            private int _index = 0;
            protected override void EnsureResources()
            {
                base.EnsureResources();
                parent?.ensureChildCached();
                if (parent == null) return;
                int index = 0;
                foreach (var child in parent._cachedAccessibleChildren)
                {
                    if (child == element._element)
                    {
                        _index = index;
                    }
                    index++;
                }
            }

            protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
            {
                if (parent == null)
                {
                    yield break;
                }
                for (var i = _index + 1; i < parent._cachedAccessibleChildren.Count; i++)
                {
                    yield return atspi._cachedElement[parent._cachedAccessibleChildren[i]];
                }
            }

            protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
            {
                if (parent == null)
                {
                    yield break;
                }
                for (var i = _index - 1; i >= 0; i--)
                {
                    yield return atspi._cachedElement[parent._cachedAccessibleChildren[i]];
                }
            }
        }

        public VisualElementSiblingAccessor SiblingAccessor => new AtspiSiblingAccessor(atspi, (AtspiVisualElement?)Parent, this);

        public VisualElementType Type
        {
            get
            {
                try
                {
                    var role = atspi_accessible_get_role(_element, IntPtr.Zero);
                    return role switch
                    {
                        ROLE_APPLICATION or 
                            ROLE_FRAME or 
                            ROLE_CANVAS => VisualElementType.TopLevel,
                        ROLE_BUTTON or
                            ROLE_SPIN_BUTTON or
                            ROLE_TOGGLE_BUTTON or
                            ROLE_PUSH_BUTTON => VisualElementType.Button,
                        ROLE_CHECK_BOX or
                            ROLE_SWITCH => VisualElementType.CheckBox,
                        ROLE_COMBO_BOX => VisualElementType.ComboBox,
                        ROLE_DOCUMENT_EMAIL or
                            ROLE_DOCUMENT_FRAME or
                            ROLE_DOCUMENT_PRESENTATION or
                            ROLE_DOCUMENT_SPREADSHEET or
                            ROLE_DOCUMENT_TEXT or
                            ROLE_DOCUMENT_WEB or
                            ROLE_HTML_CONTAINER or
                            ROLE_PARAGRAPH or
                            ROLE_FORM or
                            ROLE_DESCRIPTION_VALUE => VisualElementType.Document,
                        ROLE_ENTRY or
                            ROLE_EDITBAR or
                            ROLE_PASSWORD_TEXT => VisualElementType.TextEdit,
                        ROLE_IMAGE or
                            ROLE_DESKTOP_ICON or
                            ROLE_ICON => VisualElementType.Image,
                        ROLE_LABEL or
                            ROLE_TEXT or
                            ROLE_HEADER or
                            ROLE_FOOTER or
                            ROLE_CAPTION or
                            ROLE_COMMENT or
                            ROLE_DESCRIPTION_TERM or
                            ROLE_FOOTNOTE => VisualElementType.Label,
                        ROLE_LINK  => VisualElementType.Hyperlink,
                        ROLE_LIST or
                            ROLE_LIST_BOX or
                            ROLE_DESCRIPTION_LIST => VisualElementType.ListView,
                        ROLE_LIST_ITEM => VisualElementType.ListViewItem,
                        ROLE_MENU      => VisualElementType.Menu,
                        ROLE_MENU_ITEM or
                            ROLE_CHECK_MENU_ITEM or 
                            ROLE_TEAROFF_MENU_ITEM => VisualElementType.MenuItem,
                        ROLE_PAGE_TAB  => VisualElementType.TabItem,
                        ROLE_PANEL     => VisualElementType.Panel,
                        ROLE_PROGRESS_BAR => VisualElementType.ProgressBar,
                        ROLE_RADIO_BUTTON => VisualElementType.RadioButton,
                        ROLE_SCROLL_BAR => VisualElementType.ScrollBar,
                        ROLE_SLIDER     => VisualElementType.Slider,
                        ROLE_TABLE      => VisualElementType.Table,
                        ROLE_TABLE_ROW  => VisualElementType.TableRow,
                        ROLE_TREE       => VisualElementType.TreeViewItem,
                        ROLE_TREE_TABLE => VisualElementType.TreeView,
                        _ => VisualElementType.Unknown
                    };
                }
                catch (COMException)
                {
                    return VisualElementType.Unknown;
                }
            }
        }

        public VisualElementStates States => ElementState(_element);

        public string? Name => ElementName(_element);

        public string Id
        {
            get
            {
                var idPtr = atspi_accessible_get_accessible_id(_element, IntPtr.Zero);
                var idStr = Marshal.PtrToStringUTF8(idPtr) ?? "";
                if (idPtr == IntPtr.Zero || idStr.IsEmpty())
                {
                    return _element.ToString("X");
                }
                g_free(idPtr);
                return idStr;
            }
        }

        public int ProcessId => ElementPid(_element);

        public nint NativeWindowHandle
        {
            get
            {
                var win = atspi._context._backend.GetWindowElementByPid(ProcessId);
                return win?.NativeWindowHandle?? IntPtr.Zero;
            }
        }

        public string? GetText(int maxLength = -1)
        {
            if (atspi_accessible_is_text(_element) == 0) return null;
            var objTextCount = atspi_accessible_get_child_count(_element, IntPtr.Zero);
            if (objTextCount > 0)
            {
                // in this case, libatspi return objTextCount char “obj char” (U+FFFC), we just simply return null
                return null;
            }
            var count = atspi_text_get_character_count(_element, IntPtr.Zero);
            var rawText = atspi_text_get_text(_element, 0, maxLength == -1? count: maxLength, IntPtr.Zero);
            if (rawText == IntPtr.Zero)
            {
                return null;
            }
            var text = Marshal.PtrToStringUTF8(rawText);
            g_free(rawText);
            return text;
        }

        public string? GetSelectionText()
        {
            if (atspi_accessible_is_text(_element) == 0) return null;
            var nSelections = atspi_text_get_n_selections(_element,  IntPtr.Zero);
            String selected = "";
            for (var i = 0; i < nSelections; i++)
            {
                var rawRange = atspi_text_get_selection(_element, i, IntPtr.Zero);
                var range = Marshal.PtrToStructure<AtspiRange>(rawRange);
                var rawText = atspi_text_get_text(_element, range.start, range.end, IntPtr.Zero);
                if (rawText == IntPtr.Zero)
                {
                    return null;
                }
                var text = Marshal.PtrToStringUTF8(rawText);
                selected += text;
                g_free(rawText);
                g_free(rawRange);
            }
            return selected;
        }

        public void Invoke()
        {
            if (atspi_accessible_is_action(_element) == 0)
            {
                return;
            }
            var nAction = atspi_action_get_n_actions(_element, IntPtr.Zero);
            if (nAction == 0)
            {
                return;
            }
            atspi_action_do_action(_element, 0, IntPtr.Zero);
        }

        public void SetText(string text)
        {
            if (States.HasFlag(VisualElementStates.ReadOnly))
            {
                return;
            }
            if (atspi_accessible_is_editable_text(_element) == 0)
            {
                return;
            }
            atspi_editable_text_set_text_contents(_element, Marshal.StringToCoTaskMemUTF8(text), IntPtr.Zero);
        }

        public void SendShortcut(KeyboardShortcut shortcut)
        {
            if (atspi_accessible_is_component(_element) == 0)
            {
                return;
            }
            if (atspi_component_grab_focus(_element, IntPtr.Zero) == 0)
            {
                return;
            }
            atspi._context._backend.SendKeyboardShortcut(shortcut);
        }

        private static int LayerOrder(int layer)
        {
            return layer switch
            {
                LAYER_BACKGROUND => 1,
                LAYER_WINDOW => 2,
                LAYER_MDI => 3,
                LAYER_CANVAS => 4,
                LAYER_WIDGET => 5,
                LAYER_POPUP => 6,
                LAYER_OVERLAY => 7,
                _ => 0
            };
        }
        
        public int Order
        {
            get
            {
                var layer = atspi_component_get_layer(_element, IntPtr.Zero);
                var z = atspi_component_get_mdi_z_order(_element, IntPtr.Zero);
                return LayerOrder(layer)*256 + z*16 + IndexInParent;
            }
        }

        public PixelRect BoundingRectangle
        {
            get
            {
                if (atspi_accessible_is_component(_element) == 0)
                {
                    var unionRect = new PixelRect();
                    foreach (var child in atspi.ElementChildren(_element))
                    {
                        unionRect.Union(child.BoundingRectangle);
                    }
                    return unionRect;
                }
                var rect = ElementBounds(_element);
                atspi._logger.LogDebug("Element {Name} BoundingRectangle: {X},{Y} - {W}x{H}",
                    Name, rect.X, rect.Y, rect.Width, rect.Height);
                return rect;
            }
        }
        public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(atspi._context._backend.Capture(this, BoundingRectangle));
        }
    }

    private AtspiVisualElement? GetAtspiVisualElement(Func<IntPtr> provider)
    {
        try
        {
            var element = provider();
            if (element == IntPtr.Zero) return null;
            if (_cachedElement.TryGetValue(element, out var visualElement)) return visualElement;
            var elem = new AtspiVisualElement(this, element);
            _logger.LogInformation("Element add: {Name}({Type})[{States}]", elem.Name, elem.Type, elem.States);
            _cachedElement[element] = elem;
            return elem;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const string LibAtspi = "libatspi.so.0";
    public const int AtspiCoordTypeScreen = 0;
    public const int AtspiCoordTypeWindow = 1;
    public const int AtspiCoordTypeParent = 2;
    [StructLayout(LayoutKind.Sequential)]
    public struct AtspiRect
    {
        public int x;
        public int y;
        public int width;
        public int height;
    }
    // Accessible Impl Type
    
    // Role
    private const int ROLE_INVALID = 0;
    // Label(Text)
    private const int ROLE_LABEL = 29;
    private const int ROLE_TEXT = 61;
    private const int ROLE_HEADER = 71;
    private const int ROLE_FOOTER = 72;
    private const int ROLE_CAPTION = 81;
    private const int ROLE_COMMENT = 97;
    private const int ROLE_DESCRIPTION_TERM = 122;
    private const int ROLE_FOOTNOTE = 124;
    // Button
    private const int ROLE_BUTTON = 43;
    private const int ROLE_SPIN_BUTTON = 52;
    private const int ROLE_TOGGLE_BUTTON = 62;
    private const int ROLE_PUSH_BUTTON = 129;
    // TextEdit
    private const int ROLE_ENTRY = 79; // ATSPI_STATE_EDITABLE else Text Role
    private const int ROLE_EDITBAR = 77;
    private const int ROLE_PASSWORD_TEXT = 40;
    // Document
    private const int ROLE_DOCUMENT_FRAME = 82;
    private const int ROLE_DOCUMENT_SPREADSHEET = 92;
    private const int ROLE_DOCUMENT_PRESENTATION = 93;
    private const int ROLE_DOCUMENT_TEXT = 94;
    private const int ROLE_DOCUMENT_WEB = 95;
    private const int ROLE_DOCUMENT_EMAIL = 96;
    private const int ROLE_HTML_CONTAINER = 25;
    private const int ROLE_PARAGRAPH = 73;
    private const int ROLE_FORM = 87;
    private const int ROLE_DESCRIPTION_VALUE = 123;
    
    // Hyperlink
    private const int ROLE_LINK = 88;
    // Image
    private const int ROLE_IMAGE = 27;
    private const int ROLE_DESKTOP_ICON = 13;
    private const int ROLE_ICON = 26;
    // CheckBox
    private const int ROLE_CHECK_BOX = 7;
    private const int ROLE_SWITCH = 130;
    // RadioButtion
    private const int ROLE_RADIO_BUTTON = 44;
    // ComboBox
    private const int ROLE_COMBO_BOX = 11;
    // ListView
    private const int ROLE_LIST = 31;
    private const int ROLE_LIST_BOX = 98;
    private const int ROLE_DESCRIPTION_LIST = 121;
    // ListViewItem
    private const int ROLE_LIST_ITEM = 32;
    // TreeView
    private const int ROLE_TREE_TABLE = 66;
    // TreeViewItem
    private const int ROLE_TREE = 65;
    // DataGrid
    // DataGridItem
    // TabControl
    
    // TabItem
    private const int ROLE_PAGE_TAB = 37;
    // Table
    private const int ROLE_TABLE = 55;
    // TableRow
    private const int ROLE_TABLE_ROW = 90;
    // Menu
    private const int ROLE_MENU = 33;
    // MenuItem
    private const int ROLE_MENU_ITEM = 35;
    private const int ROLE_CHECK_MENU_ITEM = 8;
    private const int ROLE_TEAROFF_MENU_ITEM = 59;
    // Slider
    private const int ROLE_SLIDER = 51;
    // ScrollBar
    private const int ROLE_SCROLL_BAR = 48;
    // ProgressBar
    private const int ROLE_PROGRESS_BAR = 42;
    // Panel
    private const int ROLE_PANEL = 39;
    // TopLevel
    private const int ROLE_APPLICATION = 75;
    private const int ROLE_FRAME = 23;
    private const int ROLE_CANVAS = 6;
    // Screen
    
    // States
    // Offscreen
    private const int STATE_SHOWING = 25;
    private const int STATE_VISIBLE = 30;
    // Disabled
    private const int STATE_ENABLE = 8;
    // Focused
    private const int STATE_FOCUSED = 12;
    // Selected
    private const int STATE_SELECTED = 23;
    // ReadOnly
    private const int STATE_EDITABLE = 7;
    // Password
    // refer to Role
    
    // Relation Type
    private const int RELATION_SUBWINDOW_OF = 12;
    private const int RELATION_EMBEDS = 13;
    private const int RELATION_EMBEDDED_BY = 14;
    
    // Component Layer
    private const int LAYER_INVALID = 0;
    private const int LAYER_BACKGROUND = 1;
    private const int LAYER_CANVAS = 2;
    private const int LAYER_WIDGET = 3;
    private const int LAYER_MDI = 4;
    private const int LAYER_POPUP = 5;
    private const int LAYER_OVERLAY = 6;
    private const int LAYER_WINDOW = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct AtspiEvent
    {
        public IntPtr type; // char*
        public IntPtr source; // AtspiAccessible*
        public int detail1;
        public int detail2;
        // GValue 
        public IntPtr anyValueType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] anyValueData;
        public IntPtr sender;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AtspiRange
    {
        public int start;
        public int end;
    }
    
    public delegate void AtspiEventListenerCallback(IntPtr atspiEvent, IntPtr userData);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_init();

    [LibraryImport(LibAtspi)]
    public static partial void atspi_exit();

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_event_listener_new(AtspiEventListenerCallback callbackEvent, IntPtr userData, IntPtr callbackDestroyed);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_event_listener_register(IntPtr listener, IntPtr eventTypeChar, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_event_listener_deregister(IntPtr listener, IntPtr eventTypeChar, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial void atspi_event_main();
    
    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_get_desktop(int i);


    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_name(IntPtr accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_role(IntPtr accessible, IntPtr error);
    
    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_state_set(IntPtr accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_state_set_contains(IntPtr set, int state);
    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_parent(IntPtr accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_child_count(IntPtr accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_child_at_index(IntPtr accessible, int index, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_index_in_parent(IntPtr accessible, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_process_id(IntPtr accessible, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_accessible_id(IntPtr accessible, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_relation_set(IntPtr accessible, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_relation_get_n_targets (IntPtr relation);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_relation_get_relation_type(IntPtr relation);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_relation_get_target(IntPtr accessible, int i);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_application(IntPtr accessible);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_component(IntPtr accessible);
    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_component_get_accessible_at_point(IntPtr component, int x, int y, int coordType, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_component_get_layer(IntPtr component, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_component_get_extents(IntPtr component, int coordType, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial short atspi_component_get_mdi_z_order(IntPtr component, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_component_grab_focus(IntPtr component, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_text(IntPtr accessible);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_text_get_character_count(IntPtr text, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_text_get_text(IntPtr text, int start, int end, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_text_get_n_selections(IntPtr text, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_text_get_selection(IntPtr text, int selectionNum, IntPtr error);
    
    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_editable_text(IntPtr accessible);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_editable_text_set_text_contents(IntPtr editable, IntPtr text, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_action(IntPtr accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_action_get_n_actions(IntPtr action, IntPtr error);
    [LibraryImport(LibAtspi)]
    public static partial int atspi_action_do_action(IntPtr action, int i, IntPtr error);
    [LibraryImport("libgobject-2.0.so.0")]
    public static partial IntPtr g_object_ref(IntPtr obj);
    [LibraryImport("libgobject-2.0.so.0")]
    public static partial void g_object_unref(IntPtr obj);

    [LibraryImport("libglib-2.0.so.0")]
    public static partial void g_free(IntPtr mem);
}