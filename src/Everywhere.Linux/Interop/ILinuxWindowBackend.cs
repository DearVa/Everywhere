using Avalonia;
using Avalonia.Media.Imaging;
using Everywhere.Interop;

namespace Everywhere.Linux.Interop;



/// <summary>
/// Abstract display backend contract (X11[Xorg] / Wayland Compositor / other).
/// </summary>
public interface ILinuxWindowBackend: IWindowHelper
{
    void SendKeyboardShortcut(KeyboardShortcut shortcut);

    PixelPoint GetPointer();

    IVisualElement? GetFocusedWindowElement();

    IVisualElement GetWindowElementAt(PixelPoint point);

    IVisualElement? GetWindowElementByPid(int pid);

    IVisualElement GetScreenElement();
    
    /// <summary>
    /// Capture screen bitmap of window within rect
    /// </summary>
    /// <param name="window">capture target</param>
    /// <param name="rect"> used for capturing full screen</param>
    /// <returns></returns>
    Bitmap Capture(IVisualElement? window, PixelRect rect);
}