using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Controls;
using Everywhere.Interop;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Reference: Powertoys
/// </summary>
public class WindowHelper : IWindowHelper
{
    public void SetFocusable(Window window, bool focusable)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return;
        var windowLong = PInvoke.GetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        if (focusable)
        {
            Win32Properties.AddWindowStylesCallback(window, WindowStylesCallback);
            Win32Properties.AddWndProcHookCallback(window, WndProcHookCallback);

            PInvoke.SetWindowLong(
                (HWND)handle.Handle,
                WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
                windowLong & ~(
                    (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                    (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW));
        }
        else
        {
            Win32Properties.RemoveWindowStylesCallback(window, WindowStylesCallback);
            Win32Properties.RemoveWndProcHookCallback(window, WndProcHookCallback);

            PInvoke.SetWindowLong(
                (HWND)handle.Handle,
                WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
                windowLong |
                (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
        }

        static (uint style, uint exStyle) WindowStylesCallback(uint style, uint exStyle)
        {
            return (style, exStyle |
                (uint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
        }

        IntPtr WndProcHookCallback(IntPtr hWnd, uint msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            // handle and block all activate messages
            // if (msg is not (>= (uint)WINDOW_MESSAGE.WM_MOUSEMOVE and <= (uint)WINDOW_MESSAGE.WM_XBUTTONDBLCLK or (uint)WINDOW_MESSAGE.WM_NCHITTEST))
            //     Console.WriteLine($"{(WINDOW_MESSAGE)msg}\t{wparam}\t{lparam}");
            switch (msg)
            {
                case (uint)WINDOW_MESSAGE.WM_MOUSEACTIVATE:
                    handled = true;
                    return 3; // MA_NOACTIVATE;
                case (uint)WINDOW_MESSAGE.WM_ACTIVATE:
                case (uint)WINDOW_MESSAGE.WM_SETFOCUS:
                case (uint)WINDOW_MESSAGE.WM_KILLFOCUS:
                case (uint)WINDOW_MESSAGE.WM_ACTIVATEAPP:
                case (uint)WINDOW_MESSAGE.WM_NCACTIVATE:
                    handled = true;
                    return IntPtr.Zero;
                default:
                    return IntPtr.Zero;
            }
        }
    }

    public void SetHitTestVisible(Window window, bool visible)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return;
        var windowLong = PInvoke.GetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        if (visible)
        {
            Win32Properties.RemoveWindowStylesCallback(window, WindowStylesCallback);

            PInvoke.SetWindowLong(
                (HWND)handle.Handle,
                WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
                windowLong & ~(
                    (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                    (int)WINDOW_EX_STYLE.WS_EX_LAYERED |
                    (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT));
        }
        else
        {
            Win32Properties.AddWindowStylesCallback(window, WindowStylesCallback);

            PInvoke.SetWindowLong(
                (HWND)handle.Handle,
                WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
                windowLong |
                (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                (int)WINDOW_EX_STYLE.WS_EX_LAYERED |
                (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
            PInvoke.SetLayeredWindowAttributes((HWND)handle.Handle, new COLORREF(), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        }

        static (uint style, uint exStyle) WindowStylesCallback(uint style, uint exStyle)
        {
            return (style, exStyle |
                (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                (uint)WINDOW_EX_STYLE.WS_EX_LAYERED |
                (uint)WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
        }
    }

    public bool GetEffectiveVisible(Window window)
    {
        var isVisible = window.IsVisible;

        if (window.TryGetPlatformHandle() is not { } handle) return isVisible;

        unsafe
        {
            // We need to check if our window is cloaked or not. A cloaked window is still
            // technically visible, because SHOW/HIDE != iconic (minimized) != cloaked
            // (these are all separate states)
            long attr = 0;
            PInvoke.DwmGetWindowAttribute((HWND)handle.Handle, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &attr, sizeof(long));
            if (attr == 1 /* DWM_CLOAKED_APP */)
            {
                isVisible = false;
            }
        }

        return isVisible;
    }

    public void SetCloaked(Window window, bool cloaked)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return;
        var hWnd = (HWND)handle.Handle;
        if (cloaked)
        {
            // We must first hide our Avalonia window, otherwise Avalonia's focus state will get confused
            window.Hide();

            Cloak(hWnd);

            // Then hide our HWND, to make sure that the OS gives the FG / focus back to another app
            // (there's no way for us to guess what the right hwnd might be, only the OS can do it right)
            PInvoke.ShowWindow(hWnd, SHOW_WINDOW_CMD.SW_HIDE);
        }
        else
        {
            // Remember, IsIconic == "minimized", which is entirely different state
            // from "show/hide"
            // If we're currently minimized, restore us first, before we reveal
            // our window. Otherwise, we'd just be showing a minimized window -
            // which would remain not visible to the user.
            if (PInvoke.IsIconic(hWnd))
            {
                // Make sure our HWND is cloaked before any possible window manipulations
                Cloak(hWnd);

                PInvoke.ShowWindow(hWnd, SHOW_WINDOW_CMD.SW_RESTORE);
            }

            // Just to be sure, SHOW our hwnd.
            window.Show();
            if (window.IsLoaded) PInvoke.ShowWindow(hWnd, SHOW_WINDOW_CMD.SW_SHOWNA);

            // Once we're done, uncloak to avoid all animations
            Uncloak(hWnd);

            PInvoke.SetForegroundWindow(hWnd);
            PInvoke.SetActiveWindow(hWnd);
            window.Focus();

            // Push our window to the top of the Z-order and make it the topmost, so that it appears above all other windows.
            // We want to remove the topmost status when we hide the window (because we cloak it instead of hiding it).
            var topMost = window.Topmost;

            // This line is needed to ensure the window can be focused properly.
            PInvoke.SetWindowPos(hWnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);

            window.Topmost = true;
            window.Topmost = topMost;
        }
    }

    public bool AnyModelDialogOpened(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return false;
        var ownerHwnd = (HWND)handle.Handle;
        var dialogFound = false;

        // This is a quick check. When a modal dialog is open, its owner window is usually disabled.
        // If the window is still enabled, then it's likely that there's no modal dialog.
        if (PInvoke.IsWindowEnabled(ownerHwnd))
        {
            return false;
        }

        // Enumerate all top-level windows to find any owned by our window.
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != ownerHwnd ||
                !PInvoke.IsWindowVisible(hwnd) ||
                !PInvoke.IsWindowEnabled(hwnd)) return true;

            dialogFound = true;
            return false;
        }, 0);

        return dialogFound;
    }

    private static void Cloak(HWND hWnd)
    {
        bool wasCloaked;
        unsafe
        {
            BOOL value = true;
            var hr = PInvoke.DwmSetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
            wasCloaked = hr.Succeeded;
        }

        if (wasCloaked)
        {
            // Because we're only cloaking the window, bury it at the bottom in case something can
            // see it - e.g. some accessibility helper (note: this also removes the top-most status).
            PInvoke.SetWindowPos(hWnd, HWND.HWND_BOTTOM, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
        }

    }

    private unsafe static void Uncloak(HWND hWnd)
    {
        BOOL value = false;
        PInvoke.DwmSetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
    }
}