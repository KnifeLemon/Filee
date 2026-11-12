using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
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

    public void UpdateTool(AnnotationTool tool)
    {
        _currentTool = tool;
        // If pointer, make canvas pass through clicks when not annotating
        OverlayCanvas.IsHitTestVisible = tool != AnnotationTool.Pointer;
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
        foreach (var ctrl in _annotationControls)
        {
            OverlayCanvas.Children.Remove(ctrl);
        }
        _annotationControls.Clear();
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_currentTool == AnnotationTool.Pointer)
            return;

        var p = e.GetPosition(OverlayCanvas);
        _startPoint = p;
        _isDrawing = true;

        if (_currentTool == AnnotationTool.TextCallout)
        {
            AddTextCallout(p);
            _isDrawing = false;
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
                Stroke = Brushes.Red,
                StrokeThickness = 4,
                StrokeLineCap = PenLineCap.Round
            };
            OverlayCanvas.Children.Add(line);
            _annotationControls.Add(line);
            _startPoint = p;
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDrawing)
            return;

        _isDrawing = false;
        var endPoint = e.GetPosition(OverlayCanvas);

        switch (_currentTool)
        {
            case AnnotationTool.Arrow:
                AddArrow(_startPoint, endPoint);
                break;
            case AnnotationTool.Rectangle:
                AddRectangle(_startPoint, endPoint);
                break;
            case AnnotationTool.Circle:
                AddCircle(_startPoint, endPoint);
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
            Stroke = Brushes.Red,
            StrokeThickness = 4,
            StrokeLineCap = PenLineCap.Round
        };
        OverlayCanvas.Children.Add(line);
        _annotationControls.Add(line);

        // Arrow head
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var arrowLength = 16.0;
        var arrowAngle = Math.PI / 6;

        var head1 = new Point(to.X - arrowLength * Math.Cos(angle - arrowAngle), to.Y - arrowLength * Math.Sin(angle - arrowAngle));
        var head2 = new Point(to.X - arrowLength * Math.Cos(angle + arrowAngle), to.Y - arrowLength * Math.Sin(angle + arrowAngle));

        var l1 = new Line { StartPoint = to, EndPoint = head1, Stroke = Brushes.Red, StrokeThickness = 4, StrokeLineCap = PenLineCap.Round };
        var l2 = new Line { StartPoint = to, EndPoint = head2, Stroke = Brushes.Red, StrokeThickness = 4, StrokeLineCap = PenLineCap.Round };

        OverlayCanvas.Children.Add(l1);
        OverlayCanvas.Children.Add(l2);
        _annotationControls.Add(l1);
        _annotationControls.Add(l2);
    }

    private void AddRectangle(Point p1, Point p2)
    {
        var rect = new Rectangle
        {
            Width = Math.Max(4, Math.Abs(p2.X - p1.X)),
            Height = Math.Max(4, Math.Abs(p2.Y - p1.Y)),
            Stroke = Brushes.Red,
            StrokeThickness = 3,
            RadiusX = 4,
            RadiusY = 4
        };
        Canvas.SetLeft(rect, Math.Min(p1.X, p2.X));
        Canvas.SetTop(rect, Math.Min(p1.Y, p2.Y));
        OverlayCanvas.Children.Add(rect);
        _annotationControls.Add(rect);
    }

    private void AddCircle(Point p1, Point p2)
    {
        var circle = new Ellipse
        {
            Width = Math.Max(4, Math.Abs(p2.X - p1.X)),
            Height = Math.Max(4, Math.Abs(p2.Y - p1.Y)),
            Stroke = Brushes.Red,
            StrokeThickness = 3
        };
        Canvas.SetLeft(circle, Math.Min(p1.X, p2.X));
        Canvas.SetTop(circle, Math.Min(p1.Y, p2.Y));
        OverlayCanvas.Children.Add(circle);
        _annotationControls.Add(circle);
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
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 235, 59)),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4),
            Child = new TextBlock
            {
                Text = "Note: Focus here",
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.Black
            }
        };
        Canvas.SetLeft(border, p.X);
        Canvas.SetTop(border, p.Y);
        OverlayCanvas.Children.Add(border);
        _annotationControls.Add(border);
    }
}
