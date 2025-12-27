using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Views;

namespace Everywhere.Linux.Interop;

public partial class LinuxVisualElementContext
{
    private class ElementPicker : VisualElementPickerTransparentWindow
    {
        public static Task<IVisualElement?> PickAsync(
            LinuxVisualElementContext context,
            ILinuxWindowBackend backend,
            ElementPickMode mode)
        {
            var window = new ElementPicker(context, backend, mode);
            window.Show();
            return window._pickingPromise.Task;
        }

        /// <summary>
        /// A promise that resolves to the picked visual element.
        /// </summary>
        private readonly TaskCompletionSource<IVisualElement?> _pickingPromise = new();
        private readonly LinuxVisualElementContext _context;
        private readonly ILinuxWindowBackend _backend;
        private readonly PixelRect _allScreenBounds;
        private readonly VisualElementPickerMaskWindow[] _visualElementPickerMaskWindows;
        private readonly VisualElementPickerToolTipWindow _toolTipWindow;

        private ElementPickMode _elementPickMode;
        private IVisualElement? _selectedElement;

        private ElementPicker(
            LinuxVisualElementContext context,
            ILinuxWindowBackend backend,
            ElementPickMode elementPickMode)
        {
            _context = context;
            _backend = backend;
            _elementPickMode = elementPickMode;
            var allScreens = Screens.All;
            _visualElementPickerMaskWindows = new VisualElementPickerMaskWindow[allScreens.Count];
            for (var i = 0; i < allScreens.Count; i++)
            {
                var screen = allScreens[i];
                _allScreenBounds = _allScreenBounds.Union(screen.Bounds);
                var visualElementPickerMaskWindow = new VisualElementPickerMaskWindow(screen.Bounds);
                backend.SetHitTestVisible(visualElementPickerMaskWindow, false);
                _visualElementPickerMaskWindows[i] = visualElementPickerMaskWindow;
            }

            SetPlacement(_allScreenBounds, out _);
            _toolTipWindow = new VisualElementPickerToolTipWindow(_elementPickMode);

            backend.SetHitTestVisible(_toolTipWindow, false);
        }

        protected override void OnOpened(EventArgs e)
        {
            _backend.SetPickerWindow(this);
            base.OnOpened(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);

            e.Handled = true;
            e.Pointer.Capture(null);

            if (point.Properties.IsRightButtonPressed)
            {
                _selectedElement = null;
                Close();
                return;
            }

            if (point.Properties.IsLeftButtonPressed)
            {
                // Confirm selection
                Close();
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            _elementPickMode = (ElementPickMode)((int)(_elementPickMode + (e.Delta.Y > 0 ? -1 : 1)) switch
            {
                > 2 => 0,
                < 0 => 2,
                var v => v
            });
            HandlePickModeChanged();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _selectedElement = null;
                    Close();
                    break;
                case Key.D1:
                case Key.NumPad1:
                case Key.F1:
                    _elementPickMode = ElementPickMode.Screen;
                    HandlePickModeChanged();
                    break;
                case Key.D2:
                case Key.NumPad2:
                case Key.F2:
                    _elementPickMode = ElementPickMode.Window;
                    HandlePickModeChanged();
                    break;
                case Key.D3:
                case Key.NumPad3:
                case Key.F3:
                    _elementPickMode = ElementPickMode.Element;
                    HandlePickModeChanged();
                    break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            HandlePointerMoved();
        }

        private void HandlePickModeChanged()
        {
            HandlePointerMoved();
            Dispatcher.UIThread.Post(
                () =>
                {
                    _toolTipWindow.ToolTip.Mode = _elementPickMode;
                },
                DispatcherPriority.Background);
        }

        private void HandlePointerMoved()
        {
            var point = _backend.GetPointer();
            PickElement(point);
            SetToolTipWindowPosition(point);
        }

        protected override void OnClosed(EventArgs e)
        {
            _backend.SetPickerWindow(null);
            _pickingPromise.TrySetResult(_selectedElement);
            base.OnClosed(e);
        }

        private void PickElement(PixelPoint pixelPoint)
        {
            SetToolTipWindowPosition(pixelPoint);
            _selectedElement = _context.ElementFromPoint(pixelPoint, _elementPickMode);
            if (_selectedElement == null) return;
            var maskRect = _selectedElement.BoundingRectangle;
            foreach (var maskWindow in _visualElementPickerMaskWindows) maskWindow.SetMask(maskRect);
            _toolTipWindow.ToolTip.Element = _selectedElement;
        }

        /// <summary>
        /// Set the position of the tooltip window based on the pointer position.
        /// </summary>
        /// <remarks>
        /// The margin between the pointer and the tooltip window is 16 pixels.
        /// It trys to keep the tooltip window within the screen bounds.
        /// Default: left edge of the tooltip is aligned with the pointer, above the pointer.
        /// </remarks>
        /// <param name="pointerPoint"></param>
        private void SetToolTipWindowPosition(PixelPoint pointerPoint)
        {
            const int margin = 16;

            var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(pointerPoint));
            if (screen == null) return;

            var screenBounds = screen.Bounds;
            var tooltipSize = _toolTipWindow.Bounds.Size * _toolTipWindow.DesktopScaling;

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

            _toolTipWindow.Position = new PixelPoint((int)x, (int)y);
        }
    }
}