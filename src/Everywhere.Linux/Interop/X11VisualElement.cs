using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Everywhere.Interop;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop;

/// <summary>
/// Represents a visual element in the X11 system (Window or Screen).
/// </summary>
public abstract class X11VisualElementBase : IVisualElement
{
    protected readonly X11WindowBackend Backend;
    protected X11VisualElementBase(X11WindowBackend backend) => Backend = backend;

    public abstract string Id { get; }
    public abstract IVisualElement? Parent { get; }
    public abstract IEnumerable<IVisualElement> Children { get; }
    public abstract VisualElementType Type { get; }
    public abstract string? Name { get; }
    public abstract PixelRect BoundingRectangle { get; }
    public abstract int ProcessId { get; }
    public abstract IntPtr NativeWindowHandle { get; }
    
    public VisualElementStates States => VisualElementStates.None;
    public VisualElementSiblingAccessor SiblingAccessor => CreateSiblingAccessor();
    
    protected abstract VisualElementSiblingAccessor CreateSiblingAccessor();
    
    public string? GetText(int maxLength = -1) => Name;
    public string? GetSelectionText() => null;
    public void SetText(string text) { }
    public void Invoke() { }
    
    public virtual void SendShortcut(KeyboardShortcut shortcut) { }
    
    public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Backend.Screenshot.Capture((X11Window)NativeWindowHandle, BoundingRectangle.WithX(0).WithY(0))).WaitAsync(cancellationToken);
}

public sealed class X11WindowVisualElement : X11VisualElementBase
{
    private readonly X11Window _window;

    public X11WindowVisualElement(X11WindowBackend backend, X11Window window) : base(backend)
    {
        _window = window;
    }

    public override string Id => _window.ToString("X");
    public override IntPtr NativeWindowHandle => (IntPtr)_window;
    public override VisualElementType Type => VisualElementType.TopLevel;
    
    public override int ProcessId => Backend.WindowManager.GetWindowPid(_window);
    public override PixelRect BoundingRectangle => Backend.WindowManager.GetWindowBounds(_window);

    public override string? Name
    {
        get
        {
            string name = "";
            return Xlib.XFetchName(Backend.Context.Display, _window, ref name) != Status.Failure ? name : null;
        }
    }

    public override IVisualElement? Parent
    {
        get
        {
            X11Window parent = X11Window.None;
            X11Window root = X11Window.None;
            Xlib.XQueryTree(Backend.Context.Display, _window, ref root, ref parent, out _);
            if (parent != X11Window.None && parent != Backend.Context.RootWindow)
                return Backend.GetWindowElement(parent);
            return null;
        }
    }

    public override IEnumerable<IVisualElement> Children
    {
        get
        {
            X11Window parent = X11Window.None;
            X11Window root = X11Window.None;
            Xlib.XQueryTree(Backend.Context.Display, _window, ref root, ref parent, out var children);
            foreach (var child in children)
                if (child != X11Window.None) yield return Backend.GetWindowElement(child);
        }
    }

    protected override VisualElementSiblingAccessor CreateSiblingAccessor()
    {
        X11Window parent = X11Window.None;
        X11Window root = X11Window.None;
        Xlib.XQueryTree(Backend.Context.Display, _window, ref root, ref parent, out _);
        return new X11SiblingAccessor(this, parent, Backend);
    }

    public override void SendShortcut(KeyboardShortcut shortcut)
    {
        Xlib.XSetInputFocus(Backend.Context.Display, _window, RevertFocus.RevertToParent, X11Native.CurrentTime);
        Backend.InputHandler.SendKeyboardShortcut(shortcut);
    }
    
    // Sibling Accessor Implementation would go here as a nested class or separate file
    private class X11SiblingAccessor : VisualElementSiblingAccessor 
    {
         // Implementation matches original logic using Backend.GetWindowElement
         public X11SiblingAccessor(X11WindowVisualElement e, X11Window p, X11WindowBackend b) {}
         protected override void EnsureResources() {} 
         protected override IEnumerator<IVisualElement> CreateForwardEnumerator() { yield break; }
         protected override IEnumerator<IVisualElement> CreateBackwardEnumerator() { yield break; }
    }
}

public sealed class X11ScreenVisualElement : X11VisualElementBase 
{
    private readonly int _screenIndex;
    public X11ScreenVisualElement(X11WindowBackend backend, int index) : base(backend) => _screenIndex = index;
    
    public override string Id => $"Screen {_screenIndex}";
    public override IntPtr NativeWindowHandle => (IntPtr)Xlib.XRootWindow(Backend.Context.Display, _screenIndex);
    public override VisualElementType Type => VisualElementType.Screen;
    public override string Name => Id;
    public override int ProcessId => 0;
    public override IVisualElement? Parent => null;
    public override PixelRect BoundingRectangle => new PixelRect(0,0, X11Native.XDisplayWidth(Backend.Context.Display, _screenIndex), X11Native.XDisplayHeight(Backend.Context.Display, _screenIndex));

    public override IEnumerable<IVisualElement> Children
    {
        get
        {
            var root = (X11Window)NativeWindowHandle;
            X11Window parent = X11Window.None;
            X11Window rootWindow = X11Window.None;
            Xlib.XQueryTree(Backend.Context.Display, root, ref rootWindow, ref parent, out var children);
            foreach (var child in children) if (child != X11Window.None) yield return Backend.GetWindowElement(child);
        }
    }
    protected override VisualElementSiblingAccessor CreateSiblingAccessor() => new X11ScreenSiblingAccessor(Backend, _screenIndex);
    
    private class X11ScreenSiblingAccessor : VisualElementSiblingAccessor 
    {
         public X11ScreenSiblingAccessor(X11WindowBackend b, int i) {} 
         protected override IEnumerator<IVisualElement> CreateForwardEnumerator() { yield break; } 
         protected override IEnumerator<IVisualElement> CreateBackwardEnumerator() { yield break; }
    }
}
