using System.Drawing;
using System.Runtime.CompilerServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Everywhere.Patches.Contracts.Interop;
using Everywhere.Utilities;
using Everywhere.Views;
using SkiaSharp;

namespace Everywhere.Windows.Interop;

internal sealed class ChatWindowShadow
{
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const float ShadowPadding = 12;
    private const float ShadowBlurRadius = 12;
    private const byte ActiveShadowAlpha = 100;
    private const byte InactiveShadowAlpha = 60;

    private static readonly ConditionalWeakTable<ChatWindow, ChatWindowShadow> Shadows = new();

    private readonly ChatWindow _window;
    private readonly HWND _owner;
    private readonly IWindowCornerRadiusFeature _cornerRadiusFeature;

    private HWND _shadow;
    private bool? _windowFrameSuppressed;
    private IDisposable? _cornerRadiusOverride;
    private IDisposable? _borderThicknessOverride;
    private bool _shadowVisible;
    private int _shadowFrameWidth;
    private int _shadowFrameHeight;
    private double _shadowScaling;
    private float _shadowRadius = float.NaN;
    private byte _shadowAlpha;
    private bool _isActive;
    private bool _isDisposed;

    private ChatWindowShadow(ChatWindow window, HWND owner, IWindowCornerRadiusFeature cornerRadiusFeature)
    {
        _window = window;
        _owner = owner;
        _cornerRadiusFeature = cornerRadiusFeature;
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

        // Fail closed until the shadow HWND and DWM policy have both been established. A later
        // SetCornerRadius call can still store the requested radius without exposing a clipped
        // content-only window.
        cornerRadiusFeature.SetCornerRadiusSuppressed(true);
        var shadow = new ChatWindowShadow(window, owner, cornerRadiusFeature);
        if (shadow.Attach())
        {
            Shadows.Add(window, shadow);
        }
    }

    private unsafe bool Attach()
    {
        using var hInstance = PInvoke.GetModuleHandle();
        _shadow = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_LAYERED |
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
            WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
            WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
            "STATIC",
            "Everywhere.ChatWindowShadow",
            WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_DISABLED,
            0,
            0,
            1,
            1,
            hWndParent: _owner,
            hInstance: hInstance);

        if (_shadow.IsNull)
        {
            return false;
        }

        fixed (char* propertyName = "UIA_WindowVisibilityOverridden")
        {
            // Keep the auxiliary HWND visible to desktop composition while hiding it from UIA-
            // based window discovery, so capture tools can resolve the owner as the real window.
            PInvoke.SetProp(_shadow, new PCWSTR(propertyName), new HANDLE(2));
        }

        if (!TryDisableNativeFrameRendering())
        {
            PInvoke.DestroyWindow(_shadow);
            _shadow = HWND.Null;
            return false;
        }

        _cornerRadiusFeature.SetNativeFrameRenderingSuppressed(true);
        Win32Properties.AddWindowStylesCallback(_window, WindowStylesCallback);
        ApplyNativeBehaviorStyles();
        Win32Properties.AddWndProcHookCallback(_window, WndProcHookCallback);
        Update();
        return true;
    }

    private unsafe bool TryDisableNativeFrameRendering()
    {
        const int dwmNcRenderingPolicyDisabled = 1;
        var policy = dwmNcRenderingPolicyDisabled;
        var result = PInvoke.DwmSetWindowAttribute(
            _owner,
            DWMWINDOWATTRIBUTE.DWMWA_NCRENDERING_POLICY,
            &policy,
            sizeof(int));
        if (result.Failed)
        {
            return false;
        }

        var transitionsForcedDisabled = 0;
        PInvoke.DwmSetWindowAttribute(
            _owner,
            DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED,
            &transitionsForcedDisabled,
            sizeof(int));

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var borderColor = DwmColorNone;
            PInvoke.DwmSetWindowAttribute(
                _owner,
                DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR,
                &borderColor,
                sizeof(uint));
        }

        return true;
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
            case WINDOW_MESSAGE.WM_ACTIVATE:
                _isActive = (wParam.ToInt64() & 0xffff) != 0;
                Update();
                break;
            case WINDOW_MESSAGE.WM_WINDOWPOSCHANGED:
                Update();
                break;
            case WINDOW_MESSAGE.WM_NCDESTROY:
                Dispose();
                break;
        }

        return IntPtr.Zero;
    }

    private void Update()
    {
        var suppressWindowFrame =
            !PInvoke.IsWindowVisible(_owner) ||
            PInvoke.IsIconic(_owner) ||
            PInvoke.IsZoomed(_owner) ||
            PInvoke.IsWindowArranged(_owner) ||
            _window.WindowState == WindowState.FullScreen;

        if (_windowFrameSuppressed != suppressWindowFrame)
        {
            _windowFrameSuppressed = suppressWindowFrame;
            _cornerRadiusFeature.SetCornerRadiusSuppressed(suppressWindowFrame);
            if (suppressWindowFrame)
            {
                // Animation priority creates temporary value frames above the AXAML local
                // values. Disposing them restores whichever frame values are underneath.
                _cornerRadiusOverride = _window.SetValue(
                    TemplatedControl.CornerRadiusProperty,
                    default,
                    BindingPriority.Animation);
                _borderThicknessOverride = _window.SetValue(
                    TemplatedControl.BorderThicknessProperty,
                    default,
                    BindingPriority.Animation);
            }
            else
            {
                DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
                DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
            }

            _window.InvalidateVisual();
        }

        if (suppressWindowFrame ||
            !_cornerRadiusFeature.TryGetEffectiveCornerRadius(out var logicalRadius) ||
            !TryGetClientBounds(out var frame))
        {
            Hide();
            return;
        }

        var scaling = _window.RenderScaling;
        var radius = (float)(logicalRadius * scaling);
        radius = Math.Min(radius, Math.Min(frame.Width, frame.Height) / 2f);
        var padding = (int)Math.Ceiling(ShadowPadding * scaling);
        var width = frame.Width + padding * 2;
        var height = frame.Height + padding * 2;
        var shadowAlpha = _isActive ? ActiveShadowAlpha : InactiveShadowAlpha;
        var redraw =
            _shadowFrameWidth != frame.Width ||
            _shadowFrameHeight != frame.Height ||
            Math.Abs(_shadowScaling - scaling) > double.Epsilon ||
            float.IsNaN(_shadowRadius) ||
            Math.Abs(_shadowRadius - radius) > float.Epsilon ||
            _shadowAlpha != shadowAlpha;

        if (redraw)
        {
            if (!Render(
                    frame.X - padding,
                    frame.Y - padding,
                    width,
                    height,
                    padding,
                    radius,
                    scaling,
                    shadowAlpha))
            {
                Hide();
                return;
            }

            _shadowFrameWidth = frame.Width;
            _shadowFrameHeight = frame.Height;
            _shadowScaling = scaling;
            _shadowRadius = radius;
            _shadowAlpha = shadowAlpha;
        }
        else
        {
            PInvoke.SetWindowPos(
                _shadow,
                HWND.Null,
                frame.X - padding,
                frame.Y - padding,
                width,
                height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        }

        if (!_shadowVisible)
        {
            PInvoke.ShowWindow(_shadow, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            _shadowVisible = true;
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

    private unsafe bool Render(
        int x,
        int y,
        int width,
        int height,
        int padding,
        float radius,
        double scaling,
        byte shadowAlpha)
    {
        var screenDc = PInvoke.GetDC(HWND.Null);
        if (screenDc.IsNull)
        {
            return false;
        }

        var memoryDc = PInvoke.CreateCompatibleDC(screenDc);
        if (memoryDc.IsNull)
        {
            PInvoke.ReleaseDC(HWND.Null, screenDc);
            return false;
        }

        HBITMAP bitmap = default;
        HGDIOBJ previousBitmap = default;
        try
        {
            var bitmapInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = (uint)BI_COMPRESSION.BI_RGB
                }
            };
            void* pixels = null;
            bitmap = PInvoke.CreateDIBSection(
                memoryDc,
                &bitmapInfo,
                DIB_USAGE.DIB_RGB_COLORS,
                &pixels,
                default,
                0);

            if (bitmap.IsNull || pixels is null)
            {
                return false;
            }

            previousBitmap = PInvoke.SelectObject(memoryDc, bitmap);
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), (IntPtr)pixels, width * 4);
            if (surface is null)
            {
                return false;
            }

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var frame = new SKRect(padding, padding, width - padding, height - padding);
            using var roundRect = new SKRoundRect(frame, radius, radius);
            canvas.Save();
            canvas.ClipRoundRect(roundRect, SKClipOperation.Difference, antialias: true);

            var sigma = BlurRadiusToSigma(ShadowBlurRadius) * (float)scaling;
            using var shadowFilter = SKImageFilter.CreateBlur(sigma, sigma);
            using var shadowPaint = new SKPaint();
            shadowPaint.IsAntialias = true;
            shadowPaint.Color = new SKColor(0, 0, 0, shadowAlpha);
            shadowPaint.ImageFilter = shadowFilter;
            canvas.DrawRoundRect(roundRect, shadowPaint);
            canvas.Restore();
            canvas.Flush();

            var destination = new Point(x, y);
            var size = new SIZE(width, height);
            var source = new Point(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,
                SourceConstantAlpha = byte.MaxValue,
                AlphaFormat = 1
            };

            return PInvoke.UpdateLayeredWindow(
                _shadow,
                screenDc,
                &destination,
                &size,
                memoryDc,
                &source,
                default,
                &blend,
                UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
        }
        finally
        {
            if (!previousBitmap.IsNull)
            {
                PInvoke.SelectObject(memoryDc, previousBitmap);
            }

            if (!bitmap.IsNull)
            {
                PInvoke.DeleteObject(bitmap);
            }

            PInvoke.DeleteDC(memoryDc);
            PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }

    private static float BlurRadiusToSigma(float radius) =>
        radius <= 0 ? 0 : 0.288675f * radius + 0.5f;

    private void Hide()
    {
        if (_shadowVisible)
        {
            PInvoke.ShowWindow(_shadow, SHOW_WINDOW_CMD.SW_HIDE);
            _shadowVisible = false;
        }
    }

    private void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (Shadows.TryGetValue(_window, out var currentShadow) && ReferenceEquals(currentShadow, this))
        {
            Shadows.Remove(_window);
        }

        Win32Properties.RemoveWndProcHookCallback(_window, WndProcHookCallback);
        Win32Properties.RemoveWindowStylesCallback(_window, WindowStylesCallback);
        DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
        DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
        if (!_shadow.IsNull)
        {
            PInvoke.DestroyWindow(_shadow);
            _shadow = HWND.Null;
        }
    }
}