using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services.Capture;
using Point = Avalonia.Point;

namespace Filee.App.Views.Capture;

public partial class CaptureOverlayWindow : Window
{
    private AnnotationTool _currentTool = AnnotationTool.Pointer;
    private bool _isDrawing;
    private Point _startPoint;
    private Point _currentMousePos;

    private readonly List<Control> _annotationControls = [];
    private Avalonia.Controls.Shapes.Path? _spotlightOverlay;
    private Ellipse? _cursorHighlight;

    private bool _spotlightActive;
    private double _spotlightRadius = 120;
    private bool _cursorHighlightActive = true;

    private IBrush _activeBrush = new SolidColorBrush(Color.FromRgb(255, 59, 48)); // #FF3B30
    private Color _activeColor = Color.FromRgb(255, 59, 48);

    private bool _isToolbarHovered;
    private Rect _toolbarBounds;
    private Control? _activePreviewControl;

    public CaptureOverlayWindow()
    {
        InitializeComponent();
        SetupOverlays();
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

    private DispatcherTimer? _toastTimer;

    public async Task RunCountdownAsync(CancellationToken cancellationToken = default)
    {
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
        Dispatcher.UIThread.Post(() =>
        {
            _toastTimer?.Stop();
            ToastIcon.Text = icon;
            ToastMessage.Text = message;
            ActionToast.IsVisible = true;
            ActionToast.Opacity = 1.0;

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                ActionToast.IsVisible = false;
                ActionToast.Opacity = 0.0;
            };
            _toastTimer.Start();
        });
    }

    public void UpdateTool(AnnotationTool tool)
    {
        _currentTool = tool;
        OverlayCanvas.IsHitTestVisible = !_isToolbarHovered && tool != AnnotationTool.Pointer;
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
        OverlayCanvas.IsHitTestVisible = !hovered && _currentTool != AnnotationTool.Pointer;
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
            UpdateSpotlightGeometry(_currentMousePos);
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

        // Update cursor highlight position
        if (_cursorHighlight != null && _cursorHighlightActive)
        {
            Canvas.SetLeft(_cursorHighlight, p.X - 24);
            Canvas.SetTop(_cursorHighlight, p.Y - 24);
        }

        // Update spotlight position
        if (_spotlightActive)
        {
            UpdateSpotlightGeometry(p);
        }

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
                // Shape is finalized in place
                _annotationControls.Add(_activePreviewControl);
                _activePreviewControl = null;
                return;
            }

            // Remove preview wireframe before creating composite shapes
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
        var border = new Border
        {
            Width = Math.Max(10, Math.Abs(p2.X - p1.X)),
            Height = Math.Max(10, Math.Abs(p2.Y - p1.Y)),
            Background = new SolidColorBrush(Color.FromArgb(235, 30, 30, 35)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "░░ MASKED ░░",
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.Gray,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(border, Math.Min(p1.X, p2.X));
        Canvas.SetTop(border, Math.Min(p1.Y, p2.Y));
        OverlayCanvas.Children.Add(border);
        _annotationControls.Add(border);
    }

    private void AddTextCallout(Point p)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            BorderBrush = _activeBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, Color = Color.FromArgb(80, 0, 0, 0) }),
            Child = new TextBlock
            {
                Text = "Note",
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = _activeBrush
            }
        };
        Canvas.SetLeft(border, p.X);
        Canvas.SetTop(border, p.Y);
        OverlayCanvas.Children.Add(border);
        _annotationControls.Add(border);
    }
}
