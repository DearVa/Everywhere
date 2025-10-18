using Avalonia.Media.Imaging;

namespace Everywhere.Interop;

public enum VisualElementType
{
    Unknown,
    Label,
    TextEdit,
    Document,
    Button,
    Hyperlink,
    Image,
    CheckBox,
    RadioButton,
    ComboBox,
    ListView,
    ListViewItem,
    TreeView,
    TreeViewItem,
    DataGrid,
    DataGridItem,
    TabControl,
    TabItem,
    Table,
    TableRow,
    Menu,
    MenuItem,
    Slider,
    ScrollBar,
    ProgressBar,
    Panel,
    TopLevel,
    Screen
}

[Flags]
public enum VisualElementStates
{
    None = 0,
    Offscreen = 1 << 0,
    Disabled = 1 << 1,
    Focused = 1 << 2,
    Selected = 1 << 3,
    ReadOnly = 1 << 4,
    Password = 1 << 5,
}

public interface IVisualElement
{
    IVisualElementContext Context { get; }

    /// <summary>
    /// Unique identifier in one Visual Tree.
    /// </summary>
    string Id { get; }

    IVisualElement? Parent { get; }

    IEnumerable<IVisualElement> Children { get; }

    IVisualElement? PreviousSibling { get; }

    IVisualElement? NextSibling { get; }

    VisualElementType Type { get; }

    VisualElementStates States { get; }

    string? Name { get; }

    /// <summary>
    /// Relative to the screen pixels, regardless of the parent element.
    /// </summary>
    PixelRect BoundingRectangle { get; }

    int ProcessId { get; }

    nint NativeWindowHandle { get; }

    /// <summary>
    /// get text content of the visual element.
    /// </summary>
    /// <param name="maxLength">allowed max length of the text, -1 means no limit.</param>
    /// <returns></returns>
    /// <remarks>
    /// set maxLength to 1 can check if the text is null or empty, with minimal performance impact.
    /// </remarks>
    string? GetText(int maxLength = -1);

    /// <summary>
    /// Invokes the default action on the visual element using UI Automation patterns.
    /// </summary>
    Task InvokeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the textual content of the visual element using UI Automation patterns.
    /// </summary>
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends virtual key input to the visual element using UI Automation patterns.
    /// Supports common keys and shortcuts like Enter, Ctrl+C, or Ctrl+V even when the window is minimized.
    /// </summary>
    Task SendShortcutAsync(VisualElementShortcut shortcut, CancellationToken cancellationToken = default);

    Task<Bitmap> CaptureAsync();
}

public readonly record struct VisualElementShortcut
{
    public VisualElementShortcut(VirtualKey key, VirtualKey modifiers = VirtualKey.None)
    {
        Key = key;
        Modifiers = modifiers;
    }

    public VirtualKey Key { get; }

    public VirtualKey Modifiers { get; }

    public override string ToString() => Modifiers == VirtualKey.None ? Key.ToString() : $"{Modifiers}+{Key}";
}

[Flags]
public enum VirtualKey
{
    None = 0,
    Enter = 0x0D,
    Backspace = 0x08,
    Tab = 0x09,
    Escape = 0x1B,
    Space = 0x20,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    Delete = 0x2E,

    A = 0x41,
    B = 0x42,
    C = 0x43,
    V = 0x56,
    X = 0x58,
    Y = 0x59,
    Z = 0x5A,

    Shift = 0x0100,
    Control = 0x0200,
    Alt = 0x0400,
    Windows = 0x0800
}

public static class VisualElementExtension
{
    public static IEnumerable<IVisualElement> GetDescendants(this IVisualElement element, bool includeSelf = false)
    {
        if (includeSelf)
        {
            yield return element;
        }

        foreach (var child in element.Children)
        {
            yield return child;
            foreach (var descendant in child.GetDescendants())
            {
                yield return descendant;
            }
        }
    }

    public static IEnumerable<IVisualElement> GetAncestors(this IVisualElement element, bool includeSelf = false)
    {
        var current = includeSelf ? element : element.Parent;
        while (current != null)
        {
            yield return current;
            current = current.Parent;
        }
    }
}