using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Input;
using DynamicData;
using Everywhere.Interop;
using Everywhere.Utilities;
using Everywhere.Views;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext
{
    private abstract class ScreenSelectionSession : ScreenSelectionTransparentWindow
    {
        protected IWindowHelper WindowHelper { get; }
        protected ScreenSelectionMaskWindow[] MaskWindows { get; }
        protected ScreenSelectionToolTipWindow ToolTipWindow { get; }

        protected ScreenSelectionMode CurrentMode { get; private set; }
        protected IVisualElement? PickingElement { get; private set; }

        private readonly IReadOnlyList<ScreenSelectionMode> _allowedModes;

        private bool _isRightButtonPressed;
        private IDisposable? _mouseHookSubscription;
        private IDisposable? _keyboardHookSubscription;

        protected ScreenSelectionSession(IWindowHelper windowHelper, IReadOnlyList<ScreenSelectionMode> allowedModes, ScreenSelectionMode initialMode)
        {
            Debug.Assert(allowedModes.Count > 0);

            _allowedModes = allowedModes;
            WindowHelper = windowHelper;
            CurrentMode = initialMode;

            var allScreens = Screens.All;
            MaskWindows = new ScreenSelectionMaskWindow[allScreens.Count];
            var allScreenBounds = new PixelRect();
            for (var i = 0; i < allScreens.Count; i++)
            {
                var screen = allScreens[i];
                allScreenBounds = allScreenBounds.Union(screen.Bounds);
                var maskWindow = new ScreenSelectionMaskWindow(screen.Bounds);
                windowHelper.SetHitTestVisible(maskWindow, false);
                MaskWindows[i] = maskWindow;
            }

            // Cover the entire virtual screen
            SetPlacement(allScreenBounds, out _);

            ToolTipWindow = new ScreenSelectionToolTipWindow(allowedModes, initialMode);
            windowHelper.SetHitTestVisible(ToolTipWindow, false);
        }

        protected override unsafe void OnPointerEntered(PointerEventArgs e)
        {
            // SendInput MouseRightButtonDown, this will:
            // 1. prevent the cursor from changing to the default arrow cursor and interacting with other windows
            // 2. Trigger the OnPointerPressed event to set the window to hit test invisible
            PInvoke.SendInput(
                new ReadOnlySpan<INPUT>(
                [
                    new INPUT
                    {
                        type = INPUT_TYPE.INPUT_MOUSE,
                        Anonymous = new INPUT._Anonymous_e__Union
                        {
                            mi = new MOUSEINPUT
                            {
                                dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN
                            }
                        }
                    },
                ]),
                sizeof(INPUT));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            // This should be triggered by the SendInput above
            if (_isRightButtonPressed || !e.Properties.IsRightButtonPressed) return;

            _isRightButtonPressed = true;
            WindowHelper.SetHitTestVisible(this, false);
            foreach (var maskWindow in MaskWindows) maskWindow.Show(this);
            ToolTipWindow.Show(this);

            // Install a low-level mouse hook to listen for right button down events
            _mouseHookSubscription ??= LowLevelHook.CreateMouseHook(HandleMouseHook, false);
            _keyboardHookSubscription ??= LowLevelHook.CreateKeyboardHook(HandleKeyboardHook, false);

            // Pick the element under the cursor immediately
            PickCursorElement();
        }

        private void HandleMouseHook(WINDOW_MESSAGE msg, ref MSLLHOOKSTRUCT hookStruct, ref bool blockNext)
        {
            switch (msg)
            {
                case WINDOW_MESSAGE.WM_RBUTTONDOWN:
                {
                    blockNext = true;
                    break;
                }
                // Close the window and cancel selection on right button up (Exit the picking mode)
                case WINDOW_MESSAGE.WM_RBUTTONUP:
                {
                    blockNext = true;
                    Cancel();
                    break;
                }
                case WINDOW_MESSAGE.WM_LBUTTONUP:
                {
                    blockNext = true;
                    if (OnLeftButtonUp())
                    {
                        Close();
                    }
                    break;
                }
                case WINDOW_MESSAGE.WM_LBUTTONDOWN:
                {
                    blockNext = true;
                    OnLeftButtonDown();
                    break;
                }
                // Use scroll wheel to change pick mode
                case WINDOW_MESSAGE.WM_MOUSEWHEEL:
                {
                    blockNext = true;
                    OnMouseWheel((int)hookStruct.mouseData >> 16);
                    break;
                }
                case WINDOW_MESSAGE.WM_MOUSEMOVE:
                {
                    // Update drag if necessary
                    SetToolTipWindowPosition(hookStruct.pt);
                    PickElement(hookStruct.pt);
                    break;
                }
                default:
                {
                    blockNext = true; // block all other mouse events
                    break;
                }
            }
        }

        private void HandleKeyboardHook(WINDOW_MESSAGE msg, ref KBDLLHOOKSTRUCT hookStruct, ref bool blockNext)
        {
            // Block all key events
            blockNext = true;

            var isKeyDown = msg is WINDOW_MESSAGE.WM_KEYDOWN or WINDOW_MESSAGE.WM_SYSKEYDOWN;
            if (!isKeyDown) return;

            switch ((VIRTUAL_KEY)hookStruct.vkCode)
            {
                case VIRTUAL_KEY.VK_ESCAPE:
                {
                    Cancel();
                    break;
                }
                case VIRTUAL_KEY.VK_NUMPAD1 or VIRTUAL_KEY.VK_1 or VIRTUAL_KEY.VK_F1:
                {
                    SetMode(ScreenSelectionMode.Screen);
                    break;
                }
                case VIRTUAL_KEY.VK_NUMPAD2 or VIRTUAL_KEY.VK_2 or VIRTUAL_KEY.VK_F2:
                {
                    SetMode(ScreenSelectionMode.Window);
                    break;
                }
                case VIRTUAL_KEY.VK_NUMPAD3 or VIRTUAL_KEY.VK_3 or VIRTUAL_KEY.VK_F3:
                {
                    SetMode(ScreenSelectionMode.Element);
                    break;
                }
                // Add shortcut for Free mode? F4?
                case VIRTUAL_KEY.VK_NUMPAD4 or VIRTUAL_KEY.VK_4 or VIRTUAL_KEY.VK_F4:
                {
                    SetMode(ScreenSelectionMode.Free);
                    break;
                }
            }

            void SetMode(ScreenSelectionMode mode)
            {
                if (!_allowedModes.Contains(mode)) return;

                CurrentMode = mode;
                HandleModeChanged();
            }
        }

        private void OnMouseWheel(int delta)
        {
            var newIndex = _allowedModes.IndexOf(CurrentMode) + (delta > 0 ? -1 : 1);
            if (newIndex < 0) newIndex = _allowedModes.Count - 1;
            else if (newIndex >= _allowedModes.Count) newIndex = 0;
            CurrentMode = _allowedModes[newIndex];
            HandleModeChanged();
        }

        private void HandleModeChanged()
        {
            ToolTipWindow.ToolTip.Mode = CurrentMode;
            PickCursorElement();
        }

        protected override unsafe void OnClosed(EventArgs e)
        {
            DisposeCollector.DisposeToDefault(ref _mouseHookSubscription);
            DisposeCollector.DisposeToDefault(ref _keyboardHookSubscription);

            // right button down event (from SendInput) is not blocked and triggered OnPointerPressed
            // so currently system thinks right button is still pressed
            // we need to let the right button up event go through to avoid stuck right button state
            // Run it on a separate thread to avoid blocking the UI thread
            Task.Run(() =>
            {
                PInvoke.SendInput(
                    new ReadOnlySpan<INPUT>(
                    [
                        new INPUT
                        {
                            type = INPUT_TYPE.INPUT_MOUSE,
                            Anonymous = new INPUT._Anonymous_e__Union
                            {
                                mi = new MOUSEINPUT
                                {
                                    dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP
                                }
                            }
                        },
                    ]),
                    sizeof(INPUT));
            });
        }

        private void PickCursorElement()
        {
            if (!PInvoke.GetCursorPos(out var cursorPos)) return;

            PickElement(cursorPos);
        }

        private void Cancel()
        {
            OnCanceled();
            Close();
        }

        protected virtual void OnCanceled()
        {
            PickingElement = null;
        }

        /// <summary>
        /// Picks the element under the cursor based on the current selection mode.
        /// </summary>
        /// <param name="cursorPos"></param>
        protected virtual void PickElement(Point cursorPos)
        {
            var maskRect = default(PixelRect);
            switch (CurrentMode)
            {
                case ScreenSelectionMode.Screen:
                {
                    var pixelPoint = new PixelPoint(cursorPos.X, cursorPos.Y);
                    var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(pixelPoint));
                    if (screen == null) break;

                    var hMonitor = PInvoke.MonitorFromPoint(cursorPos, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                    if (hMonitor == HMONITOR.Null) break;

                    PickingElement = new ScreenVisualElementImpl(hMonitor);
                    maskRect = screen.Bounds;
                    break;
                }
                case ScreenSelectionMode.Window:
                {
                    var selectedHWnd = PInvoke.WindowFromPoint(cursorPos);
                    if (selectedHWnd == HWND.Null) break;

                    var rootHWnd = PInvoke.GetAncestor(selectedHWnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
                    if (rootHWnd == HWND.Null) break;

                    PickingElement = TryCreateVisualElement(() => Automation.FromHandle(rootHWnd));
                    if (PickingElement == null) break;

                    maskRect = PickingElement.BoundingRectangle;
                    break;
                }
                case ScreenSelectionMode.Element:
                {
                    // BUG: sometimes this only picks the window, not the element under the cursor (e.g. QQ)
                    PickingElement = TryCreateVisualElement(() => Automation.FromPoint(cursorPos));
                    if (PickingElement == null) break;

                    maskRect = PickingElement.BoundingRectangle;
                    break;
                }
            }

            foreach (var maskWindow in MaskWindows) maskWindow.SetMask(maskRect);
            ToolTipWindow.ToolTip.Element = PickingElement;
        }

        /// <summary>
        /// Called when Left Button Down.
        /// </summary>
        protected virtual void OnLeftButtonDown() { }

        /// <summary>
        /// Called when Left Button Up.
        /// Returns true if the picker should close.
        /// </summary>
        protected virtual bool OnLeftButtonUp() => true;

        private void SetToolTipWindowPosition(Point cursorPos)
        {
            const int margin = 16;

            var pointerPoint = new PixelPoint(cursorPos.X, cursorPos.Y);
            var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(pointerPoint));
            if (screen == null) return;

            var screenBounds = screen.Bounds;
            var tooltipSize = ToolTipWindow.Bounds.Size * ToolTipWindow.DesktopScaling;

            var x = (double)pointerPoint.X;
            var y = pointerPoint.Y - margin - tooltipSize.Height;

            // Check if there is enough space above the pointer
            if (y < 0d)
            {
                y = pointerPoint.Y + margin; // place below the pointer
            }

            // Check if there is enough space to the right of the pointer
            if (x + tooltipSize.Width > screenBounds.Right)
            {
                x = pointerPoint.X - tooltipSize.Width; // place to the left of the pointer
            }

            ToolTipWindow.Position = new PixelPoint((int)x, (int)y);
        }
    }
}