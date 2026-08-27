using System.Drawing;
using System.Runtime.CompilerServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;
using Everywhere.Patches.Contracts.Interop;
using Everywhere.Utilities;
using Everywhere.Views;

namespace Everywhere.Windows.Interop;

internal sealed class ChatWindowShadow
{
    private const uint DwmColorNone = 0xFFFFFFFE;

    private static readonly ConditionalWeakTable<ChatWindow, ChatWindowShadow> Shadows = new();

    private readonly ChatWindow _window;
    private readonly HWND _owner;
    private readonly IWindowCornerRadiusFeature _cornerRadiusFeature;
    private readonly ChatWindowShadowRenderer _renderer;

    private bool? _cornerRadiusSuppressed;
    private bool? _borderSuppressed;
    private IDisposable? _cornerRadiusOverride;
    private IDisposable? _borderThicknessOverride;
    private bool _stateUpdateQueued;
    private bool _isActive;
    private bool _isDisposed;

    private ChatWindowShadow(
        ChatWindow window,
        HWND owner,
        IWindowCornerRadiusFeature cornerRadiusFeature,
        ChatWindowShadowRenderer renderer)
    {
        _window = window;
        _owner = owner;
        _cornerRadiusFeature = cornerRadiusFeature;
        _renderer = renderer;
        _isActive = PInvoke.GetForegroundWindow() == owner;
    }

    public static void Attach(ChatWindow window)
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (window.PlatformImpl is not IWindowCornerRadiusFeature cornerRadiusFeature)
        {
            return;
        }

        if (window.TryGetPlatformHandle() is not { } handle)
        {
            cornerRadiusFeature.SetCornerRadiusSuppressed(true);
            return;
        }

        var owner = (HWND)handle.Handle;
        if (Shadows.TryGetValue(window, out var existingShadow))
        {
            if (existingShadow._owner == owner)
            {
                existingShadow.Update();
                return;
            }

            existingShadow.Dispose();
        }

        // Fail closed until the shadow renderer is available. A later SetCornerRadius call can
        // still store the requested radius without exposing a clipped content-only window.
        cornerRadiusFeature.SetCornerRadiusSuppressed(true);
        var renderer = ChatWindowShadowRenderer.TryCreate(owner);
        if (renderer is null)
        {
            return;
        }

        var shadow = new ChatWindowShadow(window, owner, cornerRadiusFeature, renderer);
        shadow.Attach();
        Shadows.Add(window, shadow);
    }

    private void Attach()
    {
        ConfigureDwmFrame();
        Win32Properties.AddWindowStylesCallback(_window, WindowStylesCallback);
        Win32Properties.AddWndProcHookCallback(_window, WndProcHookCallback);
        ApplyNativeBehaviorStyles();
        Update();
    }

    private unsafe void ConfigureDwmFrame()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var borderColor = DwmColorNone;
        PInvoke.DwmSetWindowAttribute(
            _owner,
            DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR,
            &borderColor,
            sizeof(uint));
    }

    private void ApplyNativeBehaviorStyles()
    {
        var style = PInvoke.GetWindowLong(_owner, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var updatedStyle = style | (int)(WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU);
        if (updatedStyle != style)
        {
            PInvoke.SetWindowLong(_owner, WINDOW_LONG_PTR_INDEX.GWL_STYLE, updatedStyle);
        }

        PInvoke.SetWindowPos(
            _owner,
            HWND.Null,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED |
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private static (uint style, uint exStyle) WindowStylesCallback(uint style, uint exStyle) =>
        (style | (uint)(WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU), exStyle);

    private IntPtr WndProcHookCallback(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        switch ((WINDOW_MESSAGE)msg)
        {
            case WINDOW_MESSAGE.WM_NCCALCSIZE:
                if (!IsWindowMaximized((HWND)hWnd))
                {
                    // Keep WS_CAPTION for native composition behavior without allowing its
                    // non-client metrics to shrink the restored or arranged client area.
                    handled = true;
                }

                break;
            case WINDOW_MESSAGE.WM_ACTIVATE:
                _isActive = (wParam.ToInt64() & 0xffff) != 0;
                Update();
                break;
            case WINDOW_MESSAGE.WM_WINDOWPOSCHANGED:
                Update();
                QueueStateUpdate();
                break;
            case WINDOW_MESSAGE.WM_EXITSIZEMOVE:
                QueueStateUpdate();
                break;
            case WINDOW_MESSAGE.WM_NCDESTROY:
                Dispose();
                break;
        }

        return IntPtr.Zero;
    }

    private static bool IsWindowMaximized(HWND window)
    {
        var placement = new WINDOWPLACEMENT();
        return PInvoke.GetWindowPlacement(window, ref placement) &&
            placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED;
    }

    private void Update()
    {
        var presentation = GetFramePresentation();
        ApplyFramePresentation(presentation);

        if (!presentation.ShowShadow ||
            !_cornerRadiusFeature.TryGetEffectiveCornerRadius(out var logicalRadius) ||
            !TryGetClientBounds(out var frame) ||
            !_renderer.Update(
                frame,
                _window.RenderScaling,
                logicalRadius,
                _isActive))
        {
            _renderer.Hide();
        }
    }

    private WindowFramePresentation GetFramePresentation()
    {
        var isUnavailable = !PInvoke.IsWindowVisible(_owner) || PInvoke.IsIconic(_owner);
        var isFullScreen = _window.WindowState == WindowState.FullScreen;
        var isMaximized = PInvoke.IsZoomed(_owner) || _window.WindowState == WindowState.Maximized;
        var isArranged = !isMaximized && PInvoke.IsWindowArranged(_owner);

        return new WindowFramePresentation(
            SuppressCornerRadius: isUnavailable || isFullScreen || isMaximized || isArranged,
            SuppressBorder: isFullScreen || isMaximized,
            ShowShadow: !isUnavailable && !isFullScreen && !isMaximized);
    }

    private void ApplyFramePresentation(WindowFramePresentation presentation)
    {
        var invalidateVisual = false;
        if (_cornerRadiusSuppressed != presentation.SuppressCornerRadius)
        {
            _cornerRadiusSuppressed = presentation.SuppressCornerRadius;
            _cornerRadiusFeature.SetCornerRadiusSuppressed(presentation.SuppressCornerRadius);
            if (presentation.SuppressCornerRadius)
            {
                // Animation priority creates a temporary value above the AXAML local value.
                // Disposing it restores whichever corner radius is underneath.
                _cornerRadiusOverride = _window.SetValue(
                    TemplatedControl.CornerRadiusProperty,
                    default,
                    BindingPriority.Animation);
            }
            else
            {
                DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
            }

            invalidateVisual = true;
        }

        if (_borderSuppressed != presentation.SuppressBorder)
        {
            _borderSuppressed = presentation.SuppressBorder;
            if (presentation.SuppressBorder)
            {
                _borderThicknessOverride = _window.SetValue(
                    TemplatedControl.BorderThicknessProperty,
                    default,
                    BindingPriority.Animation);
            }
            else
            {
                DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
            }

            invalidateVisual = true;
        }

        if (invalidateVisual)
        {
            _window.InvalidateVisual();
        }
    }

    private void QueueStateUpdate()
    {
        if (_stateUpdateQueued || _isDisposed)
        {
            return;
        }

        _stateUpdateQueued = true;
        Dispatcher.UIThread.Post(ProcessQueuedStateUpdate, DispatcherPriority.Background);
    }

    private void ProcessQueuedStateUpdate()
    {
        _stateUpdateQueued = false;
        if (!_isDisposed)
        {
            // Win32 can finalize the arranged state after WM_WINDOWPOSCHANGED enters our hook.
            Update();
        }
    }

    private bool TryGetClientBounds(out RECT frame)
    {
        if (!PInvoke.GetClientRect(_owner, out var clientRect) || clientRect is not { Width: > 0, Height: > 0 })
        {
            frame = default;
            return false;
        }

        var origin = new Point(clientRect.left, clientRect.top);
        if (!PInvoke.ClientToScreen(_owner, ref origin))
        {
            frame = default;
            return false;
        }

        frame = new RECT(origin.X, origin.Y, origin.X + clientRect.Width, origin.Y + clientRect.Height);
        return true;
    }

    private void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cornerRadiusFeature.SetCornerRadiusSuppressed(false);

        if (Shadows.TryGetValue(_window, out var currentShadow) && ReferenceEquals(currentShadow, this))
        {
            Shadows.Remove(_window);
        }

        Win32Properties.RemoveWndProcHookCallback(_window, WndProcHookCallback);
        Win32Properties.RemoveWindowStylesCallback(_window, WindowStylesCallback);
        DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
        DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
        _renderer.Dispose();
    }

    private readonly record struct WindowFramePresentation(
        bool SuppressCornerRadius,
        bool SuppressBorder,
        bool ShowShadow
    );
}