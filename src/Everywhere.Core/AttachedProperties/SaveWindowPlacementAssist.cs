using Avalonia.Controls;
using Everywhere.Common;
using Everywhere.Configuration;
using Window = ShadUI.Window;

namespace Everywhere.AttachedProperties;

public static class SaveWindowPlacementAssist
{
    /// <summary>
    ///     Defines the attached property for the key used to save and restore the window's placement.
    /// </summary>
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<Window, Window, string?>("Key");

    /// <summary>
    ///     Sets the key used to save and restore the window's placement.
    ///     If null, the window's placement will not be saved.
    /// </summary>
    public static void SetKey(Window obj, string? value) => obj.SetValue(KeyProperty, value);

    /// <summary>
    ///     Gets the key used to save and restore the window's placement.
    ///     If null, the window's placement will not be saved.
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public static string? GetKey(Window obj) => obj.GetValue(KeyProperty);

    private static readonly IKeyValueStorage KeyValueStorage = ServiceLocator.Resolve<IKeyValueStorage>();

    static SaveWindowPlacementAssist()
    {
        KeyProperty.Changed.AddClassHandler<Window>(HandleKeyPropertyChanged);
    }

    private static void HandleKeyPropertyChanged(Window sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is not string { Length: > 0 } key) return;

        // immediately try to restore window placement
        RestoreWindowPlacement(key, sender);

        // subscribe to window events
        sender.PositionChanged += (_, _) => SaveWindowPlacement(key, sender);
        sender.Resized += (_, _) => SaveWindowPlacement(key, sender);
    }

    private static void RestoreWindowPlacement(string key, Window window)
    {
        if (KeyValueStorage.Get<WindowPlacement?>($"TransientWindow.Placement.{key}") is not { } placement) return;

        double x, y;
        if (window.Screens.All.Count == 0)
        {
            x = placement.X;
            y = placement.Y;
        }
        else
        {
            // Calculate scaling based on the target screen
            var targetScreen = window.Screens.ScreenFromPoint(placement.Position)
                ?? window.Screens.Primary
                ?? window.Screens.All.FirstOrDefault();
            var scaling = targetScreen?.Scaling ?? 1.0;

            // Leave a safety margin to avoid window being too close to the edge or taskbar
            const int SafetyPadding = 20;

            var screenBounds = targetScreen?.WorkingArea ?? default;
            var actualWidth = placement.Width <= 0 ? 200d : placement.Width * scaling;
            var actualHeight = placement.Height <= 0 ? 200d : placement.Height * scaling;

            x = Math.Clamp(placement.X,
                screenBounds.X + SafetyPadding,
                Math.Max(screenBounds.X + SafetyPadding, screenBounds.Right - actualWidth - SafetyPadding));

            y = Math.Clamp(placement.Y,
                screenBounds.Y + SafetyPadding,
                Math.Max(screenBounds.Y + SafetyPadding, screenBounds.Bottom - actualHeight - SafetyPadding));
        }

        // Restore bounds first so that maximizing works correctly from the restored position
        window.Position = new PixelPoint((int)x, (int)y);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.SizeToContent = (placement.Width, placement.Height) switch
        {
            (< 0, < 0) => SizeToContent.WidthAndHeight,
            (< 0, _) => SizeToContent.Height,
            (_, < 0) => SizeToContent.Width,
            _ => SizeToContent.Manual
        };

        if (placement.Width > 0) window.Width = placement.Width;
        if (placement.Height > 0) window.Height = placement.Height;

        window.WindowState = placement.WindowState;
    }

    private static void SaveWindowPlacement(string key, Window window)
    {
        // Do not save placement if the window is minimized
        if (window.WindowState == WindowState.Minimized) return;

        key = $"TransientWindow.Placement.{key}";

        // Only save size and position if the window is in Normal state
        if (window.WindowState == WindowState.Normal)
        {
            var (width, height) = window.SizeToContent switch
            {
                SizeToContent.Width => (-1, (int)window.Height),
                SizeToContent.Height => ((int)window.Width, -1),
                SizeToContent.Manual => ((int)window.Width, (int)window.Height),
                _ => (-1, -1)
            };

            var placement = new WindowPlacement(
                window.Position,
                width,
                height,
                window.WindowState);
            KeyValueStorage.Set(key, placement);
        }
        else
        {
            // If maximized/minimized, only update the state, preserving the last normal bounds
            var existing = KeyValueStorage.Get<WindowPlacement?>(key);
            if (!existing.HasValue) return;

            var placement = existing.Value;
            placement.WindowState = window.WindowState;
            KeyValueStorage.Set(key, placement);
        }
    }
}