using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ShadUI;
using Window = Avalonia.Controls.Window;
using Everywhere.Extensions;
using Everywhere.I18N;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop;

public partial class LinuxVisualElementContext
{
    private class ElementPicker : Window
    {
        public static Task<IVisualElement?> PickAsync(
            LinuxVisualElementContext context,
            ILinuxWindowBackend backend,
            PickElementMode mode)
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
        private readonly PickElementMode _mode;
        private readonly PixelRect _screenBounds;
        private readonly Border _maskBorder;
        private readonly Border _elementBoundsBorder;
        private readonly double _scale;
        private readonly Window _tooltipWindow;
        private readonly TextBlock _elementNameTextBlock;
        private readonly Badge _screenPickModeBadge;
        private readonly Badge _windowPickModeBadge;
        private readonly Badge _elementPickModeBadge;
        private Rect? _previousMaskRect;
        private IVisualElement? _selectedElement;

        private ElementPicker(
            LinuxVisualElementContext context,
            ILinuxWindowBackend backend,
            PickElementMode mode)
        {
            _context = context;
            _mode = mode;
            var allScreens = Screens.All;
            _screenBounds = allScreens.Aggregate(default(PixelRect), (current, screen) => current.Union(screen.Bounds));
            if (_screenBounds.Width <= 0 || _screenBounds.Height <= 0)
            {
                throw new InvalidOperationException("No valid screen bounds found.");
            }

            Content = new Panel
            {
                IsHitTestVisible = false,
                Children =
                {
                    (_maskBorder = new Border
                    {
                        Background = Brushes.Black,
                        Opacity = 0.4
                    }),
                    (_elementBoundsBorder = new Border
                    {
                        BorderThickness = new Thickness(2),
                        BorderBrush = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top
                    })
                }
            };

            SetWindowStyles(this);
            Background = Brushes.Transparent;
            Cursor = new Cursor(StandardCursorType.Cross);
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

            Position = _screenBounds.Position;
            _scale = DesktopScaling; // we must set Position first to get the correct scaling factor
            Width = _screenBounds.Width / _scale;
            Height = _screenBounds.Height / _scale;

            _tooltipWindow = new Window
            {
                TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent],
                ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome,
                ExtendClientAreaToDecorationsHint = true,
                Content = new ExperimentalAcrylicBorder
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 6),
                    Material = new ExperimentalAcrylicMaterial
                    {
                        FallbackColor = Color.FromArgb(153, 0, 0, 0),
                        MaterialOpacity = 0.7,
                        TintColor = Color.FromArgb(119, 34, 34, 34),
                        TintOpacity = 0.7
                    },
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 4d,
                        Children =
                        {
                            (_elementNameTextBlock = new TextBlock
                            {
                                FontWeight = FontWeight.Bold,
                                Foreground = Brushes.White
                            }),
                            new TextBlock
                            {
                                Foreground = Brushes.White,
                                Text = LocaleKey.VisualElementPicker_ToolTipWindow_TipTextBlock_Text
                            },
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 4d,
                                Children =
                                {
                                    (_screenPickModeBadge = new Badge
                                    {
                                        Background = Brushes.DimGray,
                                        Content = LocaleKey.VisualElementPicker_ToolTipWindow_ScreenPickModeBadge_Content
                                    }),
                                    (_windowPickModeBadge = new Badge
                                    {
                                        Background = Brushes.DimGray,
                                        Content = LocaleKey.VisualElementPicker_ToolTipWindow_WindowPickModeBadge_Content
                                    }),
                                    (_elementPickModeBadge = new Badge
                                    {
                                        Background = Brushes.DimGray,
                                        Content = LocaleKey.VisualElementPicker_ToolTipWindow_ElementPickModeBadge_Content
                                    })
                                }
                            }
                        }
                    }
                }
            };

            SetWindowStyles(_tooltipWindow);
            backend.SetHitTestVisible(_tooltipWindow, false);
            _tooltipWindow.SizeToContent = SizeToContent.WidthAndHeight;
            SetPickModeTextBlockStyles();
        }
        
        private static void SetWindowStyles(Window window)
        {
            window.Topmost = true;
            window.CanResize = false;
            window.ShowInTaskbar = false;
            window.SystemDecorations = SystemDecorations.None;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }
        
        private void SetPickModeTextBlockStyles()
        {
            var (t, f0, f1) = _mode switch
            {
                PickElementMode.Screen => (_screenPickModeBadge, _windowPickModeBadge, _elementPickModeBadge),
                PickElementMode.Window => (_windowPickModeBadge, _screenPickModeBadge, _elementPickModeBadge),
                _ => (_elementPickModeBadge, _screenPickModeBadge, _windowPickModeBadge),
            };

            t.Background = Brushes.DarkGreen;
            t.SetValue(TextElement.FontWeightProperty, FontWeight.Bold);
            f0.Background = f1.Background = Brushes.DimGray;
            f0.SetValue(TextElement.FontWeightProperty, FontWeight.Normal);
            f1.SetValue(TextElement.FontWeightProperty, FontWeight.Normal);
        }

        protected override void OnOpened(EventArgs e)
        {
            bool leftPressed = false;
            
            _context._eventHelper.WindowPickerHook(this, (point, type) =>
            {
                switch (type)
                {
                    case EventType.MouseDown:
                        if (!leftPressed)
                        {
                            leftPressed = true;
                            Dispatcher.UIThread.Post(() => Pick(point), DispatcherPriority.Default);
                        }
                        break;
                    case EventType.MouseDrag:
                        if (leftPressed)
                        { 
                            Dispatcher.UIThread.Post(() => Pick(point), DispatcherPriority.Default);
                        }
                        break;
                    case EventType.MouseUp:
                        leftPressed = false;
                        Dispatcher.UIThread.Post(Close, DispatcherPriority.Default);
                        break;
                }
            });
        }
        
        protected override void OnClosed(EventArgs e)
        {
            _context._eventHelper.UngrabMouseHook();
            _pickingPromise.TrySetResult(_selectedElement);
        }

        private void Pick(PixelPoint pixelPoint)
        {
            SetToolTipWindowPosition(pixelPoint);
            if (_selectedElement != null && _selectedElement.BoundingRectangle.Contains(pixelPoint))
            {
                return;
            }
            _selectedElement = _context.ElementFromPoint(pixelPoint, _mode);
            if (_selectedElement == null) return;
            var maskRect = _selectedElement.BoundingRectangle
                .Translate(-(PixelVector)_screenBounds.Position).ToRect(_scale);
            SetMask(maskRect);
            _elementNameTextBlock.Text = GetElementDescription(_selectedElement);
        }
        
        private void SetMask(Rect rect)
        {
            if (_previousMaskRect == rect) return;
            if (rect.Width < 0 || rect.Height < 0)
            {
                _context._logger.LogError("invalid rect: {Rect}", rect);
            }
            _maskBorder.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(Bounds), new RectangleGeometry(rect));
            _elementBoundsBorder.Margin = new Thickness(rect.X, rect.Y, 0, 0);
            _elementBoundsBorder.Width = rect.Width;
            _elementBoundsBorder.Height = rect.Height;
            _previousMaskRect = rect;
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
            var tooltipSize = _tooltipWindow.Bounds.Size * _scale;

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

            _tooltipWindow.Position = new PixelPoint((int)x, (int)y);
        }

        private readonly Dictionary<int, string> _processNameCache = new();

        private string? GetElementDescription(IVisualElement? element)
        {
            if (element is null) return LocaleKey.Common_None;

            DynamicResourceKey key;
            var elementTypeKey = new DynamicResourceKey($"VisualElementType_{element.Type}");
            if (element.ProcessId != 0)
            {
                if (!_processNameCache.TryGetValue(element.ProcessId, out var processName))
                {
                    try
                    {
                        using var process = Process.GetProcessById(element.ProcessId);
                        processName = process.ProcessName;
                    }
                    catch
                    {
                        processName = string.Empty;
                    }
                    _processNameCache[element.ProcessId] = processName;
                }

                key = processName.IsNullOrWhiteSpace() ?
                    elementTypeKey :
                    new FormattedDynamicResourceKey("{0} - {1}", new DirectResourceKey(processName), elementTypeKey);
            }
            else
            {
                key = elementTypeKey;
            }

            return key.ToString();
        }
    }
}