using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services.Capture;
using Point = Avalonia.Point;

namespace Filee.App.Views.Capture;

public partial class CaptureOverlayWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_LAYERED = 0x00080000;

    private AnnotationTool _currentTool = AnnotationTool.Pointer;
    private bool _isDrawing;
    private Point _startPoint;
    private Point _currentMousePos;

    private readonly List<Control> _annotationControls = [];
    private Avalonia.Controls.Shapes.Path? _spotlightOverlay;
    private Ellipse? _cursorHighlight;

    private bool _spotlightActive;
    private double _spotlightRadius = 120;
    private bool _cursorHighlightActive = false;

    private IBrush _activeBrush = new SolidColorBrush(Color.FromRgb(255, 59, 48)); // #FF3B30
    private Color _activeColor = Color.FromRgb(255, 59, 48);

    private bool _isToolbarHovered;
    private Rect _toolbarBounds;
    private Control? _activePreviewControl;
    private readonly DispatcherTimer _cursorTrackerTimer;

    public CaptureOverlayWindow()
    {
        InitializeComponent();
        SetupOverlays();

        // 60fps tracking timer: smoothly moves cursor halo and spotlight anywhere on the screen
        _cursorTrackerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _cursorTrackerTimer.Tick += (_, _) => OnCursorTrackerTick();
        _cursorTrackerTimer.Start();

        Loaded += (_, _) =>
        {
            // By default, start with click-through enabled so user can freely interact with Windows 11
            SetClickThrough(true);
        };
    }

    private void SetClickThrough(bool transparent)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return;

            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            if (transparent)
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style.ToInt64() | WS_EX_TRANSPARENT | WS_EX_LAYERED));
            }
            else
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style.ToInt64() & ~WS_EX_TRANSPARENT));
            }
        }
        catch { }
    }

    private void SetupOverlays()
    {
        // Spotlight overlay Path (combines outer screen with transparent circular cutout)
        _spotlightOverlay = new Avalonia.Controls.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            IsHitTestVisible = false,
            IsVisible = false
        };
        OverlayCanvas.Children.Add(_spotlightOverlay);

        // Cursor highlight halo
        _cursorHighlight = new Ellipse
        {
            Width = 48,
            Height = 48,
            Fill = new SolidColorBrush(Color.FromArgb(100, 255, 230, 0)),
            Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 210, 0)),
            StrokeThickness = 2,
            IsHitTestVisible = false,
            IsVisible = _cursorHighlightActive
        };
        OverlayCanvas.Children.Add(_cursorHighlight);
    }

    private void OnCursorTrackerTick()
    {
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var pt))
            return;

        var p = new Point(pt.X, pt.Y);
        _currentMousePos = p;

        if (_cursorHighlight != null && _cursorHighlightActive)
        {
            Canvas.SetLeft(_cursorHighlight, p.X - 24);
            Canvas.SetTop(_cursorHighlight, p.Y - 24);
        }

        if (_spotlightActive)
        {
            UpdateSpotlightGeometry(p);
        }
    }

    public async Task RunCountdownAsync(CancellationToken cancellationToken = default)
    {
        SetClickThrough(true);
        CountdownContainer.IsVisible = true;
        for (int i = 3; i >= 1; i--)
        {
            CountdownNumberText.Text = i.ToString();
            CountdownContainer.Opacity = 1.0;
            await Task.Delay(1000, cancellationToken);
        }
        CountdownContainer.IsVisible = false;
    }

    public void ShowActionToast(string message, string icon = "ℹ")
    {
        // Suppressed during capture sessions so no toast cards appear in the recorded output video
    }

    public void UpdateTool(AnnotationTool tool)
    {
        _currentTool = tool;
        var isPointer = tool == AnnotationTool.Pointer;

        // When in Pointer mode, make overlay window completely transparent to mouse clicks
        // so the user can click desktop icons, apps, and taskbar normally.
        // When drawing with an annotation tool, enable drawing hit-test.
        SetClickThrough(isPointer);
        OverlayCanvas.IsHitTestVisible = !_isToolbarHovered && !isPointer;
    }

    public void UpdateColor(string hex)
    {
        if (Color.TryParse(hex, out var c))
        {
            _activeColor = c;
            _activeBrush = new SolidColorBrush(c);
        }
    }

    public void SetToolbarHover(bool hovered)
    {
        _isToolbarHovered = hovered;
        var isPointer = _currentTool == AnnotationTool.Pointer;
        OverlayCanvas.IsHitTestVisible = !hovered && !isPointer;
    }

    public void UpdateToolbarBounds(Rect bounds)
    {
        _toolbarBounds = bounds;
    }

    public void UpdateSpotlight(bool active, double radius = 120)
    {
        _spotlightActive = active;
        _spotlightRadius = radius;
        if (_spotlightOverlay != null)
        {
            _spotlightOverlay.IsVisible = active;
            if (active)
            {
                UpdateSpotlightGeometry(_currentMousePos);
            }
        }
    }

    public void UpdateCursorHighlight(bool active)
    {
        _cursorHighlightActive = active;
        if (_cursorHighlight != null)
        {
            _cursorHighlight.IsVisible = active;
        }
    }

    public void ClearAnnotations()
    {
        if (_activePreviewControl != null)
        {
            OverlayCanvas.Children.Remove(_activePreviewControl);
            _activePreviewControl = null;
        }
        foreach (var ctrl in _annotationControls)
        {
            OverlayCanvas.Children.Remove(ctrl);
        }
        _annotationControls.Clear();
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isToolbarHovered || _currentTool == AnnotationTool.Pointer)
            return;

        var p = e.GetPosition(OverlayCanvas);

        // Discard clicks physically inside toolbar bounds
        if (_toolbarBounds.Width > 0 && _toolbarBounds.Contains(p))
            return;

        _startPoint = p;
        _isDrawing = true;

        if (_currentTool == AnnotationTool.TextCallout)
        {
            AddTextCallout(p);
            _isDrawing = false;
            return;
        }

        // Live visual feedback: create preview shape immediately starting at point p
        switch (_currentTool)
        {
            case AnnotationTool.Circle:
                _activePreviewControl = new Ellipse
                {
                    Stroke = _activeBrush,
                    StrokeThickness = 3,
                    Fill = new SolidColorBrush(Color.FromArgb(25, _activeColor.R, _activeColor.G, _activeColor.B)),
                    Width = 2,
                    Height = 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_activePreviewControl, p.X);
                Canvas.SetTop(_activePreviewControl, p.Y);
                OverlayCanvas.Children.Add(_activePreviewControl);
                break;

            case AnnotationTool.Rectangle:
                _activePreviewControl = new Rectangle
                {
                    Stroke = _activeBrush,
                    StrokeThickness = 3,
                    RadiusX = 4,
                    RadiusY = 4,
                    Fill = new SolidColorBrush(Color.FromArgb(25, _activeColor.R, _activeColor.G, _activeColor.B)),
                    Width = 2,
                    Height = 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_activePreviewControl, p.X);
                Canvas.SetTop(_activePreviewControl, p.Y);
                OverlayCanvas.Children.Add(_activePreviewControl);
                break;

            case AnnotationTool.Arrow:
                _activePreviewControl = new Line
                {
                    StartPoint = p,
                    EndPoint = p,
                    Stroke = _activeBrush,
                    StrokeThickness = 4,
                    StrokeLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };
                OverlayCanvas.Children.Add(_activePreviewControl);
                break;

            case AnnotationTool.Blur:
                _activePreviewControl = new Rectangle
                {
                    Stroke = Brushes.LightGray,
                    StrokeThickness = 2,
                    StrokeDashArray = [4, 4],
                    Fill = new SolidColorBrush(Color.FromArgb(120, 200, 200, 200)),
                    Width = 2,
                    Height = 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_activePreviewControl, p.X);
                Canvas.SetTop(_activePreviewControl, p.Y);
                OverlayCanvas.Children.Add(_activePreviewControl);
                break;
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(OverlayCanvas);
        _currentMousePos = p;

        if (!_isDrawing)
            return;

        // If freehand pen drawing
        if (_currentTool == AnnotationTool.Pen)
        {
            var line = new Line
            {
                StartPoint = _startPoint,
                EndPoint = p,
                Stroke = _activeBrush,
                StrokeThickness = 4,
                StrokeLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            OverlayCanvas.Children.Add(line);
            _annotationControls.Add(line);
            _startPoint = p;
            return;
        }

        // Dynamic rubber-band expansion/shrinking with mouse movement for shapes
        if (_activePreviewControl != null)
        {
            var minX = Math.Min(_startPoint.X, p.X);
            var minY = Math.Min(_startPoint.Y, p.Y);
            var w = Math.Max(2, Math.Abs(p.X - _startPoint.X));
            var h = Math.Max(2, Math.Abs(p.Y - _startPoint.Y));

            if (_activePreviewControl is Ellipse or Rectangle)
            {
                _activePreviewControl.Width = w;
                _activePreviewControl.Height = h;
                Canvas.SetLeft(_activePreviewControl, minX);
                Canvas.SetTop(_activePreviewControl, minY);
            }
            else if (_activePreviewControl is Line line)
            {
                line.StartPoint = _startPoint;
                line.EndPoint = p;
            }
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDrawing)
            return;

        _isDrawing = false;
        var endPoint = e.GetPosition(OverlayCanvas);

        if (_activePreviewControl != null)
        {
            if (_currentTool is AnnotationTool.Circle or AnnotationTool.Rectangle)
            {
                _annotationControls.Add(_activePreviewControl);
                _activePreviewControl = null;
                return;
            }

            OverlayCanvas.Children.Remove(_activePreviewControl);
            _activePreviewControl = null;
        }

        switch (_currentTool)
        {
            case AnnotationTool.Arrow:
                AddArrow(_startPoint, endPoint);
                break;
            case AnnotationTool.Blur:
                AddBlurMask(_startPoint, endPoint);
                break;
        }
    }

    private void UpdateSpotlightGeometry(Point center)
    {
        if (_spotlightOverlay == null)
            return;

        var w = Bounds.Width > 0 ? Bounds.Width : 3840;
        var h = Bounds.Height > 0 ? Bounds.Height : 2160;

        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, w, h)));
        group.Children.Add(new EllipseGeometry(new Rect(center.X - _spotlightRadius, center.Y - _spotlightRadius, _spotlightRadius * 2, _spotlightRadius * 2)));

        _spotlightOverlay.Data = group;
    }

    private void AddArrow(Point from, Point to)
    {
        var line = new Line
        {
            StartPoint = from,
            EndPoint = to,
            Stroke = _activeBrush,
            StrokeThickness = 4,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        OverlayCanvas.Children.Add(line);
        _annotationControls.Add(line);

        // Arrow head
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var arrowLength = 16.0;
        var arrowAngle = Math.PI / 6;

        var head1 = new Point(to.X - arrowLength * Math.Cos(angle - arrowAngle), to.Y - arrowLength * Math.Sin(angle - arrowAngle));
        var head2 = new Point(to.X - arrowLength * Math.Cos(angle + arrowAngle), to.Y - arrowLength * Math.Sin(angle + arrowAngle));

        var l1 = new Line { StartPoint = to, EndPoint = head1, Stroke = _activeBrush, StrokeThickness = 4, StrokeLineCap = PenLineCap.Round, IsHitTestVisible = false };
        var l2 = new Line { StartPoint = to, EndPoint = head2, Stroke = _activeBrush, StrokeThickness = 4, StrokeLineCap = PenLineCap.Round, IsHitTestVisible = false };

        OverlayCanvas.Children.Add(l1);
        OverlayCanvas.Children.Add(l2);
        _annotationControls.Add(l1);
        _annotationControls.Add(l2);
    }

    private void AddBlurMask(Point p1, Point p2)
    {
        var x = Math.Min(p1.X, p2.X);
        var y = Math.Min(p1.Y, p2.Y);
        var w = Math.Max(30, Math.Abs(p2.X - p1.X));
        var h = Math.Max(24, Math.Abs(p2.Y - p1.Y));

        var container = new Panel
        {
            Width = w,
            Height = h
        };

        // Realistic frosted blur mask: neutral translucent gradient with soft edges and no distracting placeholder text
        var mask = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(220, 32, 34, 40), 0.0),
                    new GradientStop(Color.FromArgb(235, 18, 20, 24), 1.0)
                }
            },
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, Color = Color.FromArgb(60, 0, 0, 0) })
        };
        container.Children.Add(mask);

        // Cross (✕) delete button at top-right - ONLY visible on mouse hover, completely invisible in recorded video
        var closeBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "✕",
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            },
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(220, 255, 59, 48)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false
        };

        closeBtn.Click += (_, _) =>
        {
            OverlayCanvas.Children.Remove(container);
            _annotationControls.Remove(container);
        };

        container.PointerEntered += (_, _) => closeBtn.IsVisible = true;
        container.PointerExited += (_, _) => closeBtn.IsVisible = false;

        container.Children.Add(closeBtn);

        Canvas.SetLeft(container, x);
        Canvas.SetTop(container, y);
        OverlayCanvas.Children.Add(container);
        _annotationControls.Add(container);
    }

    private void AddTextCallout(Point p)
    {
        var currentFontSize = 16.0;

        var container = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
            BorderBrush = _activeBrush,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 4),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, Color = Color.FromArgb(60, 0, 0, 0) })
        };

        var rootStack = new StackPanel { Spacing = 4 };

        // Controls bar (Drag handle, A-, Size, A+, Delete ✕)
        var controlsBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        // Drag handle
        var dragHandle = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        controlsBar.Children.Add(dragHandle);

        // A- button
        var shrinkBtn = new Button
        {
            Content = new TextBlock { Text = "A-", FontSize = 10, FontWeight = FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            Padding = new Thickness(4, 1),
            Height = 20,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(shrinkBtn, "Decrease Text Size");
        Grid.SetColumn(shrinkBtn, 1);
        controlsBar.Children.Add(shrinkBtn);

        // Size badge
        var sizeBadge = new TextBlock
        {
            Text = "16",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.DimGray,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(4, 0)
        };
        Grid.SetColumn(sizeBadge, 2);
        controlsBar.Children.Add(sizeBadge);

        // A+ button
        var growBtn = new Button
        {
            Content = new TextBlock { Text = "A+", FontSize = 10, FontWeight = FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            Padding = new Thickness(4, 1),
            Height = 20,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(growBtn, "Increase Text Size");
        Grid.SetColumn(growBtn, 3);
        controlsBar.Children.Add(growBtn);

        // Close ✕ button
        var closeBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "✕",
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            },
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(220, 255, 59, 48)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(closeBtn, "Remove Text");
        Grid.SetColumn(closeBtn, 5);
        controlsBar.Children.Add(closeBtn);

        rootStack.Children.Add(controlsBar);

        var textBox = new TextBox
        {
            Text = "",
            PlaceholderText = "Type text here...",
            FontSize = currentFontSize,
            FontWeight = FontWeight.Bold,
            Foreground = _activeBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MinWidth = 120,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };

        void SetEditingMode(bool editing)
        {
            controlsBar.IsVisible = editing;
            if (editing)
            {
                container.Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255));
                container.BorderBrush = _activeBrush;
                container.BorderThickness = new Thickness(1.5);
                container.BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, Color = Color.FromArgb(60, 0, 0, 0) });
            }
            else
            {
                container.Background = Brushes.Transparent;
                container.BorderThickness = new Thickness(0);
                container.BoxShadow = default;
                textBox.SelectionStart = 0;
                textBox.SelectionEnd = 0;
            }
        }

        textBox.GotFocus += (_, _) => SetEditingMode(true);
        textBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(textBox.Text))
            {
                OverlayCanvas.Children.Remove(container);
                _annotationControls.Remove(container);
                return;
            }
            SetEditingMode(false);
        };

        textBox.KeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key == Key.Escape || (keyArgs.Key == Key.Enter && !keyArgs.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            {
                SetEditingMode(false);
                Focus();
                keyArgs.Handled = true;
            }
        };

        container.PointerEntered += (_, _) => SetEditingMode(true);
        container.PointerExited += (_, _) =>
        {
            if (!textBox.IsFocused)
            {
                SetEditingMode(false);
            }
        };

        shrinkBtn.Click += (_, _) =>
        {
            if (currentFontSize > 10)
            {
                currentFontSize -= 2;
                textBox.FontSize = currentFontSize;
                sizeBadge.Text = $"{currentFontSize}";
            }
        };

        growBtn.Click += (_, _) =>
        {
            if (currentFontSize < 64)
            {
                currentFontSize += 2;
                textBox.FontSize = currentFontSize;
                sizeBadge.Text = $"{currentFontSize}";
            }
        };

        closeBtn.Click += (_, _) =>
        {
            OverlayCanvas.Children.Remove(container);
            _annotationControls.Remove(container);
        };

        // Drag container by handle
        Point? dragStart = null;
        dragHandle.PointerPressed += (_, args) =>
        {
            dragStart = args.GetPosition(OverlayCanvas);
            args.Handled = true;
        };

        dragHandle.PointerMoved += (_, args) =>
        {
            if (dragStart.HasValue)
            {
                var cur = args.GetPosition(OverlayCanvas);
                var dx = cur.X - dragStart.Value.X;
                var dy = cur.Y - dragStart.Value.Y;
                var left = Canvas.GetLeft(container) + dx;
                var top = Canvas.GetTop(container) + dy;
                Canvas.SetLeft(container, Math.Max(0, left));
                Canvas.SetTop(container, Math.Max(0, top));
                dragStart = cur;
                args.Handled = true;
            }
        };

        dragHandle.PointerReleased += (_, args) =>
        {
            dragStart = null;
            args.Handled = true;
        };

        rootStack.Children.Add(textBox);
        container.Child = rootStack;

        Canvas.SetLeft(container, p.X);
        Canvas.SetTop(container, p.Y);
        OverlayCanvas.Children.Add(container);
        _annotationControls.Add(container);

        Dispatcher.UIThread.Post(() => textBox.Focus());
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cursorTrackerTimer.Stop();
        base.OnClosing(e);
    }
}
