// The device context EMF and WMF records draw through, and the drawing itself on an SKCanvas: GDI's state (selected
// pen, brush and font, colours, modes, the window / viewport mapping and the world transform, the clip region, the
// current position, the path bracket) and its drawing functions as [MS-EMF] and [MS-WMF] describe them. EmfPlayer and
// WmfPlayer read the records and call in here; both formats share everything below the record layer.
//
// Coordinates: records give logical units. The page transform (map mode, window and viewport) and the world transform
// turn them into device units of the reference device, and the canvas the player draws on is in device units with its
// origin at the picture frame's top-left corner. Clip regions are kept in device units, as GDI keeps them.
//
// Where GDI and Skia differ:
//  * GDI draws a line on the pixels below and to the right of its coordinates; when drawing for a raster target the
//    strokes are moved by half an output pixel so they cover the same pixels (pixel = 0, for vector output, leaves
//    them where they are).
//  * Pens thinner than a pixel draw one pixel wide, as GDI never draws a line thinner than that.
//  * Binary raster operations (SetROP2) and the common ternary ones of the blits map to blend modes: AND to multiply,
//    OR to screen, XOR to difference. They are exact for black and white, which is what masks use.
//  * Hatch and pattern brushes repeat every 8 pixels of a 96 dpi device, or of the output for raster targets.

using SkiaSharp;

namespace Filee.Engines.Metafiles;

/// <summary>GDI's device context state; SaveDC keeps a copy.</summary>
internal sealed class DcState
{
    public const int MapText = 1, MapLoMetric = 2, MapHiMetric = 3, MapLoEnglish = 4, MapHiEnglish = 5, MapTwips = 6,
        MapIsotropic = 7, MapAnisotropic = 8;

    public GdiPen Pen { get; set; } = GdiPen.Black;
    public GdiBrush Brush { get; set; } = GdiBrush.White;
    public GdiFont Font { get; set; } = GdiFont.Default;
    public GdiPalette? Palette { get; set; }
    public SKColor TextColor { get; set; } = SKColors.Black;
    public SKColor BackgroundColor { get; set; } = SKColors.White;

    /// <summary>OPAQUE background mode: hatch, dash gaps and text cells are filled with the background colour.</summary>
    public bool OpaqueBackground { get; set; } = true;

    /// <summary>WINDING fill mode (else ALTERNATE, even-odd).</summary>
    public bool Winding { get; set; }

    public int Rop2 { get; set; } = 13; // R2_COPYPEN
    public int TextAlign { get; set; }
    public int StretchMode { get; set; } = 1;
    public int MapMode { get; set; } = MapText;
    public SKPoint WindowOrigin { get; set; }
    public SKPoint ViewportOrigin { get; set; }
    public (double X, double Y) WindowExtent { get; set; } = (1, 1);
    public (double X, double Y) ViewportExtent { get; set; } = (1, 1);
    public SKMatrix World { get; set; } = SKMatrix.Identity;

    /// <summary>The clip region in device units; null = none.</summary>
    public SKPath? Clip { get; set; }

    /// <summary>The meta region (SetMetaRgn) in device units; null = none.</summary>
    public SKPath? MetaClip { get; set; }

    /// <summary>The current position in logical units.</summary>
    public SKPoint Position { get; set; }

    /// <summary>AD_CLOCKWISE: arcs run clockwise (as seen on the page).</summary>
    public bool Clockwise { get; set; }

    public float MiterLimit { get; set; } = 10;
    public SKPoint BrushOrigin { get; set; }
    public float CharExtra { get; set; }

    public DcState Clone() => (DcState)MemberwiseClone();
}

/// <summary>Draws GDI operations onto a canvas in device units.</summary>
internal sealed class GdiPlayer : IDisposable
{
    public const int RgnAnd = 1, RgnOr = 2, RgnXor = 3, RgnDiff = 4, RgnCopy = 5;
    public const int EtoOpaque = 0x0002, EtoClipped = 0x0004, EtoGlyphIndex = 0x0010, EtoRtlReading = 0x0080, EtoPdy = 0x2000;
    private const int TaUpdateCp = 1, TaRight = 2, TaCenter = 6, TaBottom = 8, TaBaseline = 24, TaRtlReading = 256;
    private const int PtCloseFigure = 1, PtLineTo = 2, PtBezierTo = 4, PtMoveTo = 6;

    private readonly SKCanvas _canvas;
    private readonly float _pixel;
    private readonly float _patternUnit;
    private readonly double _deviceUnitsPerMmX, _deviceUnitsPerMmY;
    private readonly SKRect _everything;
    private readonly List<DcState> _saved = [];
    private readonly Dictionary<(SKImage, SKColor, SKColor), SKImage> _coloredPatterns = [];
    private DcState _dc = new();
    private SKPath? _path;
    private bool _inPath, _figureOpen;
    private SKPath? _effectiveClip;
    private bool _clipChanged = true;

    /// <param name="canvas">The canvas, in device units with the picture's origin where it should be.</param>
    /// <param name="pixel">One output pixel in device units, for raster targets; 0 for vector output.</param>
    /// <param name="deviceUnitsPerMm">Device units per millimetre of the reference device (fixed map modes, patterns).</param>
    /// <param name="bounds">The picture in device units (what "the whole page" means to a clip region).</param>
    public GdiPlayer(SKCanvas canvas, float pixel, (double X, double Y) deviceUnitsPerMm, SKRect bounds, MetafileBudget budget, MetafileText text)
    {
        _canvas = canvas;
        _pixel = pixel;
        _deviceUnitsPerMmX = deviceUnitsPerMm.X > 0 ? deviceUnitsPerMm.X : 96 / 25.4;
        _deviceUnitsPerMmY = deviceUnitsPerMm.Y > 0 ? deviceUnitsPerMm.Y : 96 / 25.4;
        // Hatches and cosmetic dashes are in pixels: of the output when it has pixels, else of a 96 dpi screen.
        _patternUnit = pixel > 0 ? pixel : (float)(_deviceUnitsPerMmX * 25.4 / 96);
        var size = Math.Max(bounds.Width, bounds.Height) * 16 + 1;
        _everything = SKRect.Inflate(bounds, size, size);
        Budget = budget;
        Fonts = text;
    }

    public MetafileBudget Budget { get; }

    /// <summary>Fonts and shaping for the text records.</summary>
    public MetafileText Fonts { get; }

    /// <summary>The device context (records change it directly for simple settings).</summary>
    public DcState Dc => _dc;

    // ───────────────────────── Transforms ─────────────────────────

    /// <summary>Logical → device: the page transform after the world transform.</summary>
    public SKMatrix Logical
    {
        get
        {
            var (sx, sy) = PageScale();
            var page = new SKMatrix((float)sx, 0, (float)(_dc.ViewportOrigin.X - _dc.WindowOrigin.X * sx),
                0, (float)sy, (float)(_dc.ViewportOrigin.Y - _dc.WindowOrigin.Y * sy), 0, 0, 1);
            return page.PreConcat(_dc.World);
        }
    }

    private (double X, double Y) PageScale()
    {
        var window = _dc.WindowExtent;
        var viewport = _dc.ViewportExtent;
        var sx = window.X != 0 ? viewport.X / window.X : 1;
        var sy = window.Y != 0 ? viewport.Y / window.Y : 1;
        if (_dc.MapMode == DcState.MapIsotropic)
        {
            var s = Math.Min(Math.Abs(sx), Math.Abs(sy));
            sx = Math.CopySign(s, sx);
            sy = Math.CopySign(s, sy);
        }
        return (double.IsFinite(sx) ? sx : 1, double.IsFinite(sy) ? sy : 1);
    }

    public void SetMapMode(int mode)
    {
        var unitMm = mode switch
        {
            DcState.MapLoMetric => 0.1,
            DcState.MapHiMetric => 0.01,
            DcState.MapLoEnglish => 0.254,
            DcState.MapHiEnglish => 0.0254,
            DcState.MapTwips => 25.4 / 1440,
            _ => 0,
        };
        if (mode == DcState.MapText)
        {
            _dc.WindowExtent = (1, 1);
            _dc.ViewportExtent = (1, 1);
        }
        else if (unitMm > 0)
        {
            _dc.WindowExtent = (1, 1);
            _dc.ViewportExtent = (unitMm * _deviceUnitsPerMmX, -unitMm * _deviceUnitsPerMmY);
        }
        else if (mode is not (DcState.MapIsotropic or DcState.MapAnisotropic))
        {
            return; // not a map mode
        }
        _dc.MapMode = mode;
    }

    private bool Scalable => _dc.MapMode is DcState.MapIsotropic or DcState.MapAnisotropic;

    public void SetWindowOrigin(float x, float y) => _dc.WindowOrigin = new SKPoint(x, y);

    public void SetViewportOrigin(float x, float y) => _dc.ViewportOrigin = new SKPoint(x, y);

    public void OffsetWindowOrigin(float x, float y) => _dc.WindowOrigin += new SKPoint(x, y);

    public void OffsetViewportOrigin(float x, float y) => _dc.ViewportOrigin += new SKPoint(x, y);

    public void SetWindowExtent(double x, double y)
    {
        if (Scalable && x != 0 && y != 0)
            _dc.WindowExtent = (x, y);
    }

    public void SetViewportExtent(double x, double y)
    {
        if (Scalable && x != 0 && y != 0)
            _dc.ViewportExtent = (x, y);
    }

    public void ScaleWindowExtent(double xNum, double xDenom, double yNum, double yDenom)
    {
        if (xDenom != 0 && yDenom != 0)
            SetWindowExtent(_dc.WindowExtent.X * xNum / xDenom, _dc.WindowExtent.Y * yNum / yDenom);
    }

    public void ScaleViewportExtent(double xNum, double xDenom, double yNum, double yDenom)
    {
        if (xDenom != 0 && yDenom != 0)
            SetViewportExtent(_dc.ViewportExtent.X * xNum / xDenom, _dc.ViewportExtent.Y * yNum / yDenom);
    }

    public void SetWorldTransform(SKMatrix matrix)
    {
        if (Finite(matrix))
            _dc.World = matrix;
    }

    /// <summary>ModifyWorldTransform: 1 identity, 2 multiply on the left (applied first), 3 on the right, 4 set.</summary>
    public void ModifyWorldTransform(SKMatrix matrix, int mode)
    {
        if (mode == 1)
        {
            _dc.World = SKMatrix.Identity;
            return;
        }
        if (!Finite(matrix))
            return;
        _dc.World = mode switch
        {
            2 => _dc.World.PreConcat(matrix),
            3 => _dc.World.PostConcat(matrix),
            4 => matrix,
            _ => _dc.World,
        };
    }

    private static bool Finite(SKMatrix m) =>
        float.IsFinite(m.ScaleX) && float.IsFinite(m.SkewX) && float.IsFinite(m.TransX)
        && float.IsFinite(m.SkewY) && float.IsFinite(m.ScaleY) && float.IsFinite(m.TransY);

    // ───────────────────────── State ─────────────────────────

    public void SaveDc()
    {
        if (_saved.Count >= Budget.Limits.MaxSavedStates)
            throw MetafileLimits.Exceeded($"The metafile saves more than {Budget.Limits.MaxSavedStates} device contexts without restoring them.");
        _saved.Add(_dc.Clone());
    }

    /// <summary>RestoreDC: a negative number counts back from the last saved state, a positive one from the first.</summary>
    public void RestoreDc(int which)
    {
        var index = which < 0 ? _saved.Count + which : which - 1;
        if (index < 0 || index >= _saved.Count)
            return;
        _dc = _saved[index];
        _saved.RemoveRange(index, _saved.Count - index);
        _clipChanged = true;
    }

    public void MoveTo(float x, float y)
    {
        _dc.Position = new SKPoint(x, y);
        _figureOpen = false;
    }

    /// <summary>Selects a pen, brush, font or palette; a WMF region becomes the clip region.</summary>
    public void Select(GdiObject item)
    {
        switch (item)
        {
            case GdiPen pen:
                _dc.Pen = pen;
                break;
            case GdiBrush brush:
                _dc.Brush = brush;
                break;
            case GdiFont font:
                _dc.Font = font;
                break;
            case GdiPalette palette:
                _dc.Palette = palette;
                break;
            case GdiRegion region:
                SelectClip(Device(region.Path), RgnCopy);
                break;
        }
    }

    /// <summary>A COLORREF: 0x00BBGGRR, or a palette index (0x01nnnnnn) of the selected palette.</summary>
    public SKColor Color(uint colorRef)
    {
        if ((colorRef >> 24) == 0x01 && _dc.Palette is { Entries.Length: > 0 } palette)
            return palette.Entries[(int)(colorRef & 0xFFFF) % palette.Entries.Length];
        return new SKColor((byte)colorRef, (byte)(colorRef >> 8), (byte)(colorRef >> 16));
    }

    // ───────────────────────── Lines and curves ─────────────────────────

    public void LineTo(float x, float y)
    {
        var from = _dc.Position;
        var to = new SKPoint(x, y);
        _dc.Position = to;
        if (_inPath)
        {
            StartFigure(from);
            _path!.LineTo(Logical.MapPoint(to));
            return;
        }
        using var path = new SKPath();
        path.MoveTo(from);
        path.LineTo(to);
        Stroke(path);
    }

    /// <summary>Polyline, or PolylineTo when <paramref name="fromCurrent"/> (starting at and updating the current position).</summary>
    public void Polyline(ReadOnlySpan<SKPoint> points, bool fromCurrent)
    {
        if (points.Length == 0)
            return;
        if (fromCurrent)
        {
            if (_inPath)
            {
                StartFigure(_dc.Position);
                var m = Logical;
                foreach (var p in points)
                    _path!.LineTo(m.MapPoint(p));
                CountPath();
            }
            else
            {
                using var path = new SKPath();
                path.MoveTo(_dc.Position);
                foreach (var p in points)
                    path.LineTo(p);
                Stroke(path);
            }
            _dc.Position = points[^1];
            return;
        }
        using var line = new SKPath();
        line.AddPoly(points.ToArray(), close: false);
        if (_inPath)
            AddToPath(line);
        else
            Stroke(line);
    }

    public void PolyPolyline(IReadOnlyList<SKPoint[]> lines)
    {
        using var path = new SKPath();
        foreach (var line in lines)
        {
            if (line.Length > 0)
                path.AddPoly(line, close: false);
        }
        if (_inPath)
            AddToPath(path);
        else
            Stroke(path);
    }

    /// <summary>PolyBezier (the first point starts the curve), or PolyBezierTo from the current position.</summary>
    public void PolyBezier(ReadOnlySpan<SKPoint> points, bool fromCurrent)
    {
        if (points.Length == 0)
            return;
        using var path = new SKPath();
        var start = fromCurrent ? _dc.Position : points[0];
        var rest = fromCurrent ? points : points[1..];
        path.MoveTo(start);
        for (var i = 0; i + 2 < rest.Length; i += 3)
            path.CubicTo(rest[i], rest[i + 1], rest[i + 2]);
        if (fromCurrent)
            _dc.Position = rest.Length >= 3 ? rest[(rest.Length / 3 * 3) - 1] : _dc.Position;
        if (_inPath)
        {
            if (fromCurrent)
                ContinuePath(path);
            else
                AddToPath(path);
            return;
        }
        Stroke(path);
    }

    /// <summary>PolyDraw: moves, lines and Bézier curves with figure closes, from the current position.</summary>
    public void PolyDraw(ReadOnlySpan<SKPoint> points, ReadOnlySpan<byte> types)
    {
        using var path = new SKPath();
        path.MoveTo(_dc.Position);
        var count = Math.Min(points.Length, types.Length);
        for (var i = 0; i < count; i++)
        {
            switch (types[i] & ~PtCloseFigure)
            {
                case PtMoveTo:
                    path.MoveTo(points[i]);
                    break;
                case PtLineTo:
                    path.LineTo(points[i]);
                    break;
                case PtBezierTo when i + 2 < count:
                    path.CubicTo(points[i], points[i + 1], points[i + 2]);
                    i += 2;
                    break;
            }
            if ((types[Math.Min(i, count - 1)] & PtCloseFigure) != 0)
                path.Close();
        }
        if (count > 0)
            _dc.Position = points[count - 1];
        if (_inPath)
            ContinuePath(path);
        else
            Stroke(path);
    }

    /// <summary>Arc, Chord and Pie (kind 0, 1, 2) and ArcTo (3): a piece of the ellipse in a box between two radials.</summary>
    public void Arc(int kind, SKRect box, SKPoint start, SKPoint end)
    {
        box = Box(box);
        float cx = box.MidX, cy = box.MidY, rx = box.Width / 2, ry = box.Height / 2;
        if (rx <= 0 || ry <= 0)
            return;
        var a0 = MathF.Atan2((start.Y - cy) / ry, (start.X - cx) / rx) * 180 / MathF.PI;
        var a1 = MathF.Atan2((end.Y - cy) / ry, (end.X - cx) / rx) * 180 / MathF.PI;
        // Seen on the page: y down in logical space unless the transform flips it.
        var increasing = Determinant(Logical) > 0 ? _dc.Clockwise : !_dc.Clockwise;
        var sweep = a1 - a0;
        if (increasing)
        {
            while (sweep <= 0)
                sweep += 360;
        }
        else
        {
            while (sweep >= 0)
                sweep -= 360;
        }
        using var path = new SKPath();
        var full = MathF.Abs(sweep) >= 359.999f;
        switch (kind)
        {
            case 2: // pie
                if (full)
                {
                    path.AddOval(box);
                }
                else
                {
                    path.MoveTo(cx, cy);
                    path.ArcTo(box, a0, sweep, false);
                    path.Close();
                }
                Shape(path);
                return;
            case 1: // chord
                if (full)
                    path.AddOval(box);
                else
                    path.AddArc(box, a0, sweep);
                path.Close();
                Shape(path);
                return;
            case 3: // ArcTo: a line from the current position to the start, then the arc
                path.MoveTo(_dc.Position);
                path.ArcTo(box, a0, full ? 359.999f * MathF.Sign(sweep) : sweep, false);
                var last = path.LastPoint;
                _dc.Position = last;
                if (_inPath)
                    ContinuePath(path);
                else
                    Stroke(path);
                return;
            default:
                path.AddArc(box, a0, full ? 359.999f * MathF.Sign(sweep) : sweep);
                if (_inPath)
                    AddToPath(path);
                else
                    Stroke(path);
                return;
        }
    }

    /// <summary>AngleArc: a line from the current position to a circle, then along it (angles counter-clockwise, degrees).</summary>
    public void AngleArc(SKPoint center, float radius, float startAngle, float sweepAngle)
    {
        if (!float.IsFinite(startAngle) || !float.IsFinite(sweepAngle) || !float.IsFinite(radius))
            return;
        var flip = Determinant(Logical) > 0 ? -1 : 1;
        var box = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        using var path = new SKPath();
        path.MoveTo(_dc.Position);
        var sweep = Math.Clamp(sweepAngle, -359.999f, 359.999f);
        path.ArcTo(box, startAngle * flip, sweep * flip, false);
        _dc.Position = path.LastPoint;
        if (_inPath)
            ContinuePath(path);
        else
            Stroke(path);
    }

    // ───────────────────────── Shapes ─────────────────────────

    public void Rectangle(SKRect box) => BoxShape(box, (path, b) => path.AddRect(b));

    public void Ellipse(SKRect box) => BoxShape(box, (path, b) => path.AddOval(b));

    public void RoundRectangle(SKRect box, float cornerWidth, float cornerHeight) =>
        BoxShape(box, (path, b) => path.AddRoundRect(b, Math.Min(Math.Abs(cornerWidth) / 2, b.Width / 2), Math.Min(Math.Abs(cornerHeight) / 2, b.Height / 2)));

    private void BoxShape(SKRect box, Action<SKPath, SKRect> add)
    {
        box = Box(box);
        if (_dc.Pen.LineStyle == GdiPen.InsideFrame && !_dc.Pen.IsCosmetic)
            box.Inflate(-_dc.Pen.Width / 2, -_dc.Pen.Width / 2);
        using var path = new SKPath();
        add(path, box);
        Shape(path);
    }

    /// <summary>
    /// The bounding box of a rectangle, ellipse or arc. GDI leaves out the box's right and bottom pixel row; for pixels
    /// the box loses one output pixel there too (when the transform keeps rectangles upright).
    /// </summary>
    private SKRect Box(SKRect box)
    {
        box = box.Standardized;
        var matrix = Logical;
        if (_pixel <= 0 || matrix.SkewX != 0 || matrix.SkewY != 0 || !matrix.TryInvert(out var inverse))
            return box;
        var device = matrix.MapRect(box);
        if (device.Width <= _pixel || device.Height <= _pixel)
            return box;
        device.Right -= _pixel;
        device.Bottom -= _pixel;
        return inverse.MapRect(device);
    }

    public void Polygon(ReadOnlySpan<SKPoint> points)
    {
        if (points.Length < 2)
            return;
        using var path = new SKPath();
        path.AddPoly(points.ToArray(), close: true);
        Shape(path);
    }

    public void PolyPolygon(IReadOnlyList<SKPoint[]> polygons)
    {
        using var path = new SKPath();
        foreach (var polygon in polygons)
        {
            if (polygon.Length >= 2)
                path.AddPoly(polygon, close: true);
        }
        Shape(path);
    }

    public void SetPixel(float x, float y, SKColor color)
    {
        if (_inPath)
            return;
        var point = Logical.MapPoint(x, y);
        var size = _pixel > 0 ? _pixel : 1;
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
        Paint(c => c.DrawRect(MathF.Floor(point.X / size) * size, MathF.Floor(point.Y / size) * size, size, size, paint), SKMatrix.Identity);
    }

    /// <summary>A closed shape: filled with the brush, outlined with the pen (or added to the path in a bracket).</summary>
    private void Shape(SKPath logicalPath)
    {
        if (_inPath)
        {
            AddToPath(logicalPath);
            return;
        }
        logicalPath.FillType = _dc.Winding ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
        Fill(logicalPath, _dc.Brush, Logical);
        Stroke(logicalPath);
    }

    private void Fill(SKPath path, GdiBrush brush, SKMatrix matrix, bool rop2 = true, SKPath? clip = null)
    {
        using var paint = FillPaint(brush, matrix, rop2);
        if (paint is not null)
            Paint(c => c.DrawPath(path, paint), matrix, clip: clip);
    }

    private void Stroke(SKPath logicalPath)
    {
        var matrix = Logical;
        using var paint = StrokePaint(_dc.Pen, matrix, out var gaps);
        if (paint is null)
            return;
        Paint(c =>
        {
            if (gaps is not null)
                c.DrawPath(logicalPath, gaps);
            c.DrawPath(logicalPath, paint);
        }, matrix, stroke: true);
        gaps?.Dispose();
    }

    // ───────────────────────── Paths ─────────────────────────

    public void BeginPath()
    {
        _path?.Dispose();
        _path = new SKPath();
        _inPath = true;
        _figureOpen = false;
    }

    public void EndPath() => _inPath = false;

    public void AbortPath()
    {
        _path?.Dispose();
        _path = null;
        _inPath = false;
    }

    public void CloseFigure()
    {
        if (_path is not null && _figureOpen)
        {
            _path.Close();
            _figureOpen = false;
        }
    }

    public void FillPath() => DrawPath(fill: true, stroke: false);

    public void StrokePath() => DrawPath(fill: false, stroke: true);

    public void StrokeAndFillPath() => DrawPath(fill: true, stroke: true);

    private void DrawPath(bool fill, bool stroke)
    {
        if (TakePath() is not { } device)
            return;
        using (device)
        {
            device.FillType = _dc.Winding ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
            if (fill)
                Fill(device, _dc.Brush, SKMatrix.Identity);
            if (stroke && Logical.TryInvert(out var inverse))
            {
                using var logicalPath = new SKPath(device);
                logicalPath.Transform(inverse);
                Stroke(logicalPath);
            }
        }
    }

    /// <summary>WidenPath: the path becomes the outline the pen would draw along it.</summary>
    public void WidenPath()
    {
        if (_path is null || _inPath || !Logical.TryInvert(out var inverse))
            return;
        var matrix = Logical;
        using var paint = StrokePaint(_dc.Pen, matrix, out var gaps);
        gaps?.Dispose();
        if (paint is null)
            return;
        if (paint.StrokeWidth == 0)
            paint.StrokeWidth = 1 / Math.Max(1e-6f, UnitScale(matrix));
        using var logicalPath = new SKPath(_path);
        logicalPath.Transform(inverse);
        using var widened = new SKPath();
        if (paint.GetFillPath(logicalPath, widened))
        {
            widened.Transform(matrix);
            _path.Dispose();
            _path = new SKPath(widened);
        }
    }

    public void SelectClipPath(int mode)
    {
        if (TakePath() is not { } device)
            return;
        device.FillType = _dc.Winding ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
        SelectClip(device, mode);
    }

    /// <summary>The finished path in device units, now no longer the current path; null when there is none.</summary>
    private SKPath? TakePath()
    {
        if (_path is null || _inPath)
            return null;
        var path = _path;
        _path = null;
        return path;
    }

    /// <summary>Starts a figure at <paramref name="start"/> (logical units) unless one is open.</summary>
    private void StartFigure(SKPoint start)
    {
        if (_figureOpen)
            return;
        _path!.MoveTo(Logical.MapPoint(start));
        _figureOpen = true;
    }

    /// <summary>Adds a figure in logical units to the path bracket.</summary>
    private void AddToPath(SKPath logicalPath)
    {
        using var device = new SKPath(logicalPath);
        device.Transform(Logical);
        _path!.AddPath(device, SKPathAddMode.Append);
        _figureOpen = false;
        CountPath();
    }

    /// <summary>
    /// Continues the open figure with a piece that starts where the figure ends (the current position before the
    /// piece), or starts a figure with it.
    /// </summary>
    private void ContinuePath(SKPath logicalPath)
    {
        using var device = new SKPath(logicalPath);
        device.Transform(Logical);
        _path!.AddPath(device, _figureOpen ? SKPathAddMode.Extend : SKPathAddMode.Append);
        _figureOpen = true;
        CountPath();
    }

    private void CountPath()
    {
        if (_path!.PointCount > Budget.Limits.MaxPathPoints)
            throw MetafileLimits.Exceeded($"A path in the metafile has more than {Documents.DocumentLimits.Count(Budget.Limits.MaxPathPoints)} points.");
    }

    // ───────────────────────── Regions and clipping ─────────────────────────

    /// <summary>A path in logical units as device units.</summary>
    public SKPath Device(SKPath logical)
    {
        var device = new SKPath(logical);
        device.Transform(Logical);
        return device;
    }

    /// <summary>Combines <paramref name="device"/> (device units; null = no region) into the clip region.</summary>
    public void SelectClip(SKPath? device, int mode)
    {
        var current = _dc.Clip;
        SKPath? result;
        if (mode == RgnCopy || device is null)
        {
            if (device is null && mode != RgnCopy)
                return;
            result = device is null ? null : new SKPath(device);
        }
        else
        {
            using var everything = new SKPath();
            everything.AddRect(_everything);
            result = mode switch
            {
                RgnAnd => current is null ? new SKPath(device) : Op(current, device, SKPathOp.Intersect),
                RgnOr => current is null ? null : Op(current, device, SKPathOp.Union),
                RgnXor => Op(current ?? everything, device, SKPathOp.Xor),
                RgnDiff => Op(current ?? everything, device, SKPathOp.Difference),
                _ => current,
            };
        }
        _dc.Clip = result;
        _clipChanged = true;
    }

    private static SKPath Op(SKPath a, SKPath b, SKPathOp op) => a.Op(b, op) ?? new SKPath(op == SKPathOp.Intersect ? b : a);

    /// <summary>IntersectClipRect or ExcludeClipRect, the rectangle in logical units.</summary>
    public void ClipRectangle(SKRect box, bool exclude)
    {
        using var path = new SKPath();
        path.AddRect(box.Standardized);
        using var device = Device(path);
        SelectClip(device, exclude ? RgnDiff : RgnAnd);
    }

    public void OffsetClip(float dx, float dy)
    {
        if (_dc.Clip is null)
            return;
        var offset = Logical.MapVector(dx, dy);
        var moved = new SKPath(_dc.Clip);
        moved.Offset(offset);
        _dc.Clip = moved;
        _clipChanged = true;
    }

    /// <summary>SetMetaRgn: the clip region becomes part of the meta region, and the clip region is reset.</summary>
    public void SetMetaRegion()
    {
        _dc.MetaClip = EffectiveClip() is { } clip ? new SKPath(clip) : null;
        _dc.Clip = null;
        _clipChanged = true;
    }

    private SKPath? EffectiveClip()
    {
        if (!_clipChanged)
            return _effectiveClip;
        _clipChanged = false;
        _effectiveClip = (_dc.MetaClip, _dc.Clip) switch
        {
            (null, var clip) => clip,
            (var meta, null) => meta,
            var (meta, clip) => Op(meta, clip, SKPathOp.Intersect),
        };
        return _effectiveClip;
    }

    public void FillRegion(SKPath device, GdiBrush brush)
    {
        if (!_inPath)
            Fill(device, brush, SKMatrix.Identity);
    }

    public void PaintRegion(SKPath device) => FillRegion(device, _dc.Brush);

    /// <summary>FrameRgn: a border of the brush, <paramref name="width"/> × <paramref name="height"/> logical units, inside the region.</summary>
    public void FrameRegion(SKPath device, GdiBrush brush, float width, float height)
    {
        if (_inPath)
            return;
        var size = Math.Max(Math.Abs(Logical.MapVector(width, 0).X), Math.Abs(Logical.MapVector(0, height).Y));
        using var paint = FillPaint(brush, SKMatrix.Identity, true);
        if (paint is null)
            return;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = Math.Max(size, _pixel) * 2;
        Paint(c => c.DrawPath(device, paint), SKMatrix.Identity, clip: device);
    }

    public void InvertRegion(SKPath device)
    {
        if (_inPath)
            return;
        using var paint = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Difference, IsAntialias = true };
        Paint(c => c.DrawPath(device, paint), SKMatrix.Identity);
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>
    /// ExtTextOut: <paramref name="text"/> at the reference point (x, y), or at the current position with TA_UPDATECP.
    /// </summary>
    /// <param name="dx">Advance of each character in logical units, or null.</param>
    /// <param name="dy">Vertical advance of each character (ETO_PDY), or null.</param>
    /// <param name="box">The ETO_OPAQUE / ETO_CLIPPED rectangle in logical units.</param>
    /// <param name="advanced">GM_ADVANCED: the text follows the world transform completely.</param>
    public void Text(float x, float y, string text, float[]? dx, float[]? dy, int options, SKRect? box, bool advanced)
    {
        var logical = Logical;
        var font = _dc.Font;
        var align = _dc.TextAlign;
        var origin = (align & TaUpdateCp) != 0 ? _dc.Position : new SKPoint(x, y);
        if ((options & EtoOpaque) != 0 && box is { } opaque && !_inPath)
        {
            using var path = new SKPath();
            path.AddRect(opaque.Standardized);
            Fill(path, new GdiBrush(GdiBrush.Solid, _dc.BackgroundColor), logical, rop2: false);
        }
        if (text.Length == 0)
            return;

        // GM_COMPATIBLE: glyphs keep their shape whatever the window's aspect ratio; only the advances follow x.
        var (scaleX, scaleY) = (Length(logical.ScaleX, logical.SkewY), Length(logical.SkewX, logical.ScaleY));
        if (!advanced && scaleY > 0 && dx is not null && scaleX != scaleY)
            dx = [.. dx.Select(d => d * scaleX / scaleY)];
        var rightToLeft = (options & EtoRtlReading) != 0 || (align & TaRtlReading) != 0;
        using var line = Fonts.Layout(font, text, (options & EtoGlyphIndex) != 0, dx, dy, _dc.CharExtra, rightToLeft,
            _pixel > 0 ? scaleY / _pixel : 0);
        if (line is null)
            return;

        var device = logical.MapPoint(origin);
        SKMatrix linear;
        if (advanced)
        {
            linear = logical with { TransX = 0, TransY = 0 };
            if (Determinant(linear) < 0)
                linear = linear.PreConcat(SKMatrix.CreateScale(1, -1)); // text stays upright when y grows upwards
        }
        else
        {
            linear = SKMatrix.CreateScale(scaleY, scaleY);
        }
        var placement = SKMatrix.CreateTranslation(device.X, device.Y)
            .PreConcat(SKMatrix.CreateRotationDegrees(-font.Escapement / 10f))
            .PreConcat(linear);
        var shiftX = (align & TaCenter) switch
        {
            TaCenter => -line.Width / 2,
            TaRight => -line.Width,
            _ => 0,
        };
        var shiftY = (align & TaBaseline) switch
        {
            TaBaseline => 0,
            TaBottom => -line.Descent,
            _ => line.Ascent,
        };
        placement = placement.PreConcat(SKMatrix.CreateTranslation(shiftX, shiftY));
        // For pixels, upright text starts on a whole pixel, as GDI puts it (the baseline too).
        var upright = placement.SkewX == 0 && placement.SkewY == 0 && placement.ScaleX > 0 && placement.ScaleY > 0;
        if (_pixel > 0 && upright)
        {
            placement.TransX = MathF.Round(placement.TransX / _pixel) * _pixel;
            placement.TransY = MathF.Round(placement.TransY / _pixel) * _pixel;
        }
        if ((align & TaUpdateCp) != 0)
            _dc.Position = new SKPoint(origin.X + (shiftX == 0 ? line.Width : 0), origin.Y);

        if (_inPath)
        {
            using var outline = new SKPath(line.Path);
            outline.Transform(placement);
            _path!.AddPath(outline, SKPathAddMode.Append);
            _figureOpen = false;
            CountPath();
            return;
        }

        SKPath? clip = null;
        if ((options & EtoClipped) != 0 && box is { } clipBox)
        {
            using var rect = new SKPath();
            rect.AddRect(clipBox.Standardized);
            clip = Device(rect);
        }
        using var ink = new SKPaint { Color = _dc.TextColor, IsAntialias = true, Style = SKPaintStyle.Fill };
        if (line.Bold)
        {
            ink.Style = SKPaintStyle.StrokeAndFill;
            ink.StrokeWidth = line.BoldWidth;
            ink.StrokeJoin = SKStrokeJoin.Round;
        }
        using var background = _dc.OpaqueBackground && (options & EtoOpaque) == 0
            ? new SKPaint { Color = _dc.BackgroundColor, Style = SKPaintStyle.Fill }
            : null;
        // For pixels, upright glyphs are drawn as text so they are hinted to the pixel grid like GDI's; otherwise (and
        // always in vector output) as their outlines.
        using var blob = _pixel > 0 && upright ? Blob(line) : null;
        Paint(c =>
        {
            if (background is not null)
                c.DrawRect(new SKRect(0, -line.Ascent, line.Width, line.Descent), background);
            if (blob is not null)
            {
                ink.Style = SKPaintStyle.Fill;
                c.DrawText(blob, 0, 0, ink);
            }
            else
            {
                c.DrawPath(line.Path, ink);
            }
            ink.Style = SKPaintStyle.Fill;
            if (line.Underline is { } underline)
                c.DrawRect(underline, ink);
            if (line.StrikeOut is { } strike)
                c.DrawRect(strike, ink);
        }, placement, clip: clip);
        clip?.Dispose();
    }

    private static float Length(float x, float y) => MathF.Sqrt(x * x + y * y);

    /// <summary>The line's glyphs as a hinted text blob (runs of one font each).</summary>
    private static SKTextBlob? Blob(TextLine line)
    {
        using var builder = new SKTextBlobBuilder();
        var start = 0;
        while (start < line.Glyphs.Count)
        {
            var font = line.Glyphs[start].Font;
            var end = start;
            while (end < line.Glyphs.Count && ReferenceEquals(line.Glyphs[end].Font, font))
                end++;
            using var skFont = new SKFont(font.Typeface, line.Em)
            {
                ScaleX = line.ScaleX,
                SkewX = line.Skew,
                Embolden = line.Bold,
                Hinting = SKFontHinting.Normal,
                Edging = SKFontEdging.Antialias,
                Subpixel = false,
            };
            var run = builder.AllocatePositionedRun(skFont, end - start);
            var glyphs = run.Glyphs;
            var positions = run.Positions;
            for (var i = start; i < end; i++)
            {
                glyphs[i - start] = line.Glyphs[i].Glyph;
                positions[i - start] = new SKPoint(line.Glyphs[i].X, line.Glyphs[i].Y);
            }
            start = end;
        }
        return builder.Build();
    }

    // ───────────────────────── Bitmaps ─────────────────────────

    /// <summary>
    /// StretchBlt and friends: <paramref name="source"/> (pixels, top-down) of the bitmap onto the logical rectangle
    /// (x, y, width, height; a negative size mirrors), combined with what is there by the ternary raster operation.
    /// </summary>
    public void Bitmap(DibImage image, SKRect source, float x, float y, float width, float height, uint rop, byte alpha = 255)
    {
        if (_inPath || width == 0 || height == 0 || source.Width <= 0 || source.Height <= 0)
            return;
        var code = (rop >> 16) & 0xFF;
        if (code is 0xAA)
            return;
        if (code is 0x00 or 0xFF or 0xF0 or 0x5A or 0x55 or 0xFB or 0x0F)
        {
            PatternBlit(new SKRect(x, y, x + width, y + height), rop);
            return;
        }
        var visible = SKRect.Intersect(source, new SKRect(0, 0, image.Width, image.Height));
        if (visible.IsEmpty)
            return;
        var matrix = Logical
            .PreConcat(SKMatrix.CreateTranslation(x, y))
            .PreConcat(SKMatrix.CreateScale(width / source.Width, height / source.Height))
            .PreConcat(SKMatrix.CreateTranslation(-source.Left, -source.Top));
        if (code is 0xB8 or 0xE2)
        {
            MaskedPattern(image.Image, visible, matrix, invert: code == 0xB8);
            return;
        }
        DrawImage(image.Image, visible, matrix, code, alpha);
    }

    /// <summary>PlgBlt: the source rectangle onto the parallelogram of three logical points (upper left, upper right, lower left).</summary>
    public void Parallelogram(DibImage image, SKRect source, SKPoint upperLeft, SKPoint upperRight, SKPoint lowerLeft)
    {
        if (_inPath || source.Width <= 0 || source.Height <= 0)
            return;
        var visible = SKRect.Intersect(source, new SKRect(0, 0, image.Width, image.Height));
        if (visible.IsEmpty)
            return;
        var map = new SKMatrix(
            (upperRight.X - upperLeft.X) / source.Width, (lowerLeft.X - upperLeft.X) / source.Height, upperLeft.X,
            (upperRight.Y - upperLeft.Y) / source.Width, (lowerLeft.Y - upperLeft.Y) / source.Height, upperLeft.Y, 0, 0, 1);
        var matrix = Logical.PreConcat(map).PreConcat(SKMatrix.CreateTranslation(-source.Left, -source.Top));
        DrawImage(image.Image, visible, matrix, 0xCC, 255);
    }

    private void DrawImage(SKImage image, SKRect source, SKMatrix matrix, uint code, byte alpha)
    {
        using var paint = new SKPaint { IsAntialias = false, Color = SKColors.Black.WithAlpha(alpha) };
        var invert = code is 0x33 or 0xBB or 0x11;
        paint.BlendMode = code switch
        {
            0x88 or 0x11 => SKBlendMode.Multiply, // SRCAND, NOTSRCERASE
            0xEE or 0xBB => SKBlendMode.Screen, // SRCPAINT, MERGEPAINT
            0x66 => SKBlendMode.Difference, // SRCINVERT
            _ => SKBlendMode.SrcOver,
        };
        if (invert)
            paint.ColorFilter = Inverted();
        // Smoothed, as GDI+ draws a metafile's bitmaps.
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        Paint(c => c.DrawImage(image, source, source, sampling, paint), matrix);
    }

    /// <summary>
    /// The blits that use the source as a mask for the brush: PSDPxax (0xB8, the brush where the source is black) and
    /// DSPDxax (0xE2, where it is white).
    /// </summary>
    private void MaskedPattern(SKImage image, SKRect source, SKMatrix matrix, bool invert)
    {
        using var brush = FillPaint(_dc.Brush, matrix, rop2: false);
        if (brush is null)
            return;
        using var mask = new SKPaint { BlendMode = SKBlendMode.DstIn };
        using var luma = SKColorFilter.CreateLumaColor();
        using var inverted = invert ? Inverted() : null;
        mask.ColorFilter = inverted is null ? luma : SKColorFilter.CreateCompose(luma, inverted);
        Paint(c =>
        {
            c.SaveLayer(source, null);
            c.DrawRect(source, brush);
            c.DrawImage(image, source, source, new SKSamplingOptions(SKFilterMode.Nearest), mask);
            c.Restore();
        }, matrix);
    }

    /// <summary>PatBlt and the blits that use no source: the brush, black, white or inverting.</summary>
    public void PatternBlit(SKRect box, uint rop)
    {
        if (_inPath)
            return;
        var code = (rop >> 16) & 0xFF;
        using var path = new SKPath();
        path.AddRect(box.Standardized);
        var matrix = Logical;
        switch (code)
        {
            case 0x00:
                Fill(path, GdiBrush.Black, matrix, rop2: false);
                break;
            case 0xFF:
                Fill(path, GdiBrush.White, matrix, rop2: false);
                break;
            case 0x55: // DSTINVERT
            case 0x5A: // PATINVERT
                using (var paint = code == 0x55 ? new SKPaint { Color = SKColors.White } : FillPaint(_dc.Brush, matrix, rop2: false))
                {
                    if (paint is null)
                        break;
                    paint.BlendMode = SKBlendMode.Difference;
                    Paint(c => c.DrawPath(path, paint), matrix);
                }
                break;
            case 0xAA:
                break;
            default: // PATCOPY, PATPAINT, …
                Fill(path, _dc.Brush, matrix, rop2: false);
                break;
        }
    }

    /// <summary>GradientFill with rectangles: each from its first vertex's colour to its second's, across or down.</summary>
    public void GradientRectangle(SKPoint a, SKColor colorA, SKPoint b, SKColor colorB, bool vertical)
    {
        if (_inPath)
            return;
        var box = new SKRect(a.X, a.Y, b.X, b.Y).Standardized;
        if (box.IsEmpty)
            return;
        var end = vertical ? new SKPoint(box.Left, box.Bottom) : new SKPoint(box.Right, box.Top);
        using var shader = SKShader.CreateLinearGradient(new SKPoint(box.Left, box.Top), end, [colorA, colorB], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = false };
        Paint(c => c.DrawRect(box, paint), Logical);
    }

    /// <summary>
    /// GradientFill with a triangle: the colours of its corners blended across it, drawn as small flat triangles so the
    /// PDF backend (which doesn't write Skia's vertex meshes) keeps it.
    /// </summary>
    public void GradientTriangle(SKPoint a, SKColor ca, SKPoint b, SKColor cb, SKPoint c, SKColor cc)
    {
        if (_inPath)
            return;
        var matrix = Logical;
        var size = Math.Max(Distance(matrix, a, b), Math.Max(Distance(matrix, b, c), Distance(matrix, a, c)));
        var steps = (int)Math.Clamp(size / (_patternUnit * 4), 1, 24);
        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        Paint(canvas =>
        {
            using var piece = new SKPath();
            for (var i = 0; i < steps; i++)
            {
                for (var j = 0; j < steps - i; j++)
                {
                    Cell(i, j, i + 1, j, i, j + 1);
                    if (i + j + 1 < steps)
                        Cell(i + 1, j, i + 1, j + 1, i, j + 1);
                }
            }

            void Cell(int i0, int j0, int i1, int j1, int i2, int j2)
            {
                var (p0, w0) = At(i0, j0);
                var (p1, w1) = At(i1, j1);
                var (p2, w2) = At(i2, j2);
                var w = new[] { (w0[0] + w1[0] + w2[0]) / 3, (w0[1] + w1[1] + w2[1]) / 3, (w0[2] + w1[2] + w2[2]) / 3 };
                paint.Color = new SKColor(Mix(ca.Red, cb.Red, cc.Red, w), Mix(ca.Green, cb.Green, cc.Green, w), Mix(ca.Blue, cb.Blue, cc.Blue, w));
                piece.Rewind();
                piece.MoveTo(p0);
                piece.LineTo(p1);
                piece.LineTo(p2);
                piece.Close();
                canvas.DrawPath(piece, paint);
            }

            (SKPoint, float[]) At(int i, int j)
            {
                float u = (float)i / steps, v = (float)j / steps, t = 1 - u - v;
                return (new SKPoint(a.X * t + b.X * u + c.X * v, a.Y * t + b.Y * u + c.Y * v), [t, u, v]);
            }
        }, matrix);

        static byte Mix(byte x, byte y, byte z, float[] w) => (byte)Math.Clamp(Math.Round(x * w[0] + y * w[1] + z * w[2]), 0, 255);
    }

    private static float Distance(SKMatrix m, SKPoint a, SKPoint b)
    {
        var d = m.MapVector(b.X - a.X, b.Y - a.Y);
        return d.Length;
    }

    // ───────────────────────── Paints ─────────────────────────

    /// <summary>
    /// Draws on the canvas with the clip region (and <paramref name="clip"/>, device units) applied and
    /// <paramref name="matrix"/> mapping what is drawn to device units; strokes move to pixel centres for raster output.
    /// </summary>
    private void Paint(Action<SKCanvas> draw, SKMatrix matrix, bool stroke = false, SKPath? clip = null)
    {
        _canvas.Save();
        if (EffectiveClip() is { } region)
            _canvas.ClipPath(region, SKClipOperation.Intersect, antialias: true);
        if (clip is not null)
            _canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        if (stroke && _pixel > 0)
            _canvas.Translate(_pixel / 2, _pixel / 2);
        _canvas.Concat(in matrix);
        draw(_canvas);
        _canvas.Restore();
    }

    /// <summary>A paint for the brush, its pattern in device space; null for the null brush or R2_NOP.</summary>
    private SKPaint? FillPaint(GdiBrush brush, SKMatrix matrix, bool rop2)
    {
        if (brush.Style == GdiBrush.Null)
            return null;
        var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = _pixel == 0, Color = brush.Color };
        switch (brush.Style)
        {
            case GdiBrush.Hatched:
                paint.Shader = Pattern(Hatch(brush.Hatch, brush.Color, _dc.OpaqueBackground ? _dc.BackgroundColor : SKColors.Transparent), matrix);
                break;
            case GdiBrush.Pattern or GdiBrush.DibPattern or GdiBrush.DibPatternPt when brush.PatternImage is { } image:
                paint.Shader = Pattern(brush.Monochrome ? Colored(image, _dc.TextColor, _dc.BackgroundColor) : image, matrix);
                break;
        }
        if (rop2 && !Rop2(paint))
        {
            paint.Dispose();
            return null;
        }
        return paint;
    }

    /// <summary>
    /// A paint for the pen; null for PS_NULL or R2_NOP. <paramref name="gaps"/> is the background-coloured line under a
    /// dashed pen in OPAQUE mode.
    /// </summary>
    private SKPaint? StrokePaint(GdiPen pen, SKMatrix matrix, out SKPaint? gaps)
    {
        gaps = null;
        if (pen.LineStyle == GdiPen.Null)
            return null;
        var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            Color = pen.Color,
            StrokeCap = pen.IsCosmetic ? SKStrokeCap.Butt : pen.Cap,
            StrokeJoin = pen.IsCosmetic ? SKStrokeJoin.Miter : pen.Join,
            StrokeMiter = Math.Max(1, _dc.MiterLimit),
        };
        if (!Rop2(paint))
        {
            paint.Dispose();
            return null;
        }
        var unit = UnitScale(matrix);
        var width = pen.IsCosmetic || pen.Width <= 0 ? 0 : pen.Width;
        // For pixels, whole pixels as GDI draws them: thinner than one is one pixel (Skia's hairline). Without pixels,
        // a pen of a device unit or less (one pixel of the reference device) is as thin as can be drawn.
        if (width > 0 && _pixel == 0 && !pen.Extended && width * unit <= 1)
            width = 0;
        if (width > 0 && _pixel > 0 && unit > 0)
        {
            var pixels = MathF.Round(width * unit / _pixel);
            width = pixels < 2 ? 0 : pixels * _pixel / unit;
        }
        paint.StrokeWidth = width;
        // One-pixel lines are drawn on whole pixels without smoothing, as GDI draws them.
        if (width == 0 && _pixel > 0)
            paint.IsAntialias = false;

        float[]? dashes = null;
        // CreatePen's dash styles hold only for pens a pixel wide or less: wider ones draw solid.
        var lineStyle = !pen.Extended && width > 0 && pen.LineStyle is GdiPen.Dash or GdiPen.Dot or GdiPen.DashDot or GdiPen.DashDotDot
            ? GdiPen.Solid
            : pen.LineStyle;
        if (width > 0 && lineStyle is GdiPen.Dash or GdiPen.Dot or GdiPen.DashDot or GdiPen.DashDotDot)
        {
            var w = width;
            dashes = lineStyle switch
            {
                GdiPen.Dash => [3 * w, w],
                GdiPen.Dot => [w, w],
                GdiPen.DashDot => [3 * w, w, w, w],
                _ => [3 * w, w, w, w, w, w],
            };
        }
        else
        {
            float[]? pixels = lineStyle switch
            {
                GdiPen.Dash => [18, 6],
                GdiPen.Dot => [3, 3],
                GdiPen.DashDot => [9, 6, 3, 6],
                GdiPen.DashDotDot => [9, 3, 3, 3, 3, 3],
                GdiPen.Alternate => [1, 1],
                _ => null,
            };
            if (pixels is not null && unit > 0)
                dashes = [.. pixels.Select(p => p * _patternUnit / unit)];
            else if (pen.LineStyle == GdiPen.UserStyle && pen.Dashes is { Length: > 0 } user)
                dashes = pen.IsCosmetic && unit > 0 ? [.. user.Select(p => p * _patternUnit / unit)] : user;
        }
        if (dashes is not null)
        {
            if (dashes.Length % 2 == 1)
                dashes = [.. dashes, .. dashes];
            if (dashes.All(d => d >= 0 && float.IsFinite(d)) && dashes.Sum() > 0)
            {
                paint.PathEffect = SKPathEffect.CreateDash(dashes, 0);
                if (_dc.OpaqueBackground)
                    gaps = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, Color = _dc.BackgroundColor, StrokeWidth = width, StrokeCap = paint.StrokeCap };
            }
        }
        return paint;
    }

    /// <summary>Applies the binary raster operation to a paint; false for R2_NOP (draw nothing).</summary>
    private bool Rop2(SKPaint paint)
    {
        switch (_dc.Rop2)
        {
            case 11: // R2_NOP
                return false;
            case 1: // R2_BLACK
                paint.Shader = null;
                paint.Color = SKColors.Black;
                break;
            case 16: // R2_WHITE
                paint.Shader = null;
                paint.Color = SKColors.White;
                break;
            case 4: // R2_NOTCOPYPEN
                paint.Color = Invert(paint.Color);
                if (paint.Shader is not null)
                    paint.ColorFilter = Inverted();
                break;
            case 6: // R2_NOT
                paint.Shader = null;
                paint.Color = SKColors.White;
                paint.BlendMode = SKBlendMode.Difference;
                break;
            case 7: // R2_XORPEN
                paint.BlendMode = SKBlendMode.Difference;
                break;
            case 10: // R2_NOTXORPEN
                paint.Color = Invert(paint.Color);
                paint.BlendMode = SKBlendMode.Difference;
                break;
            case 9: // R2_MASKPEN
            case 3: // R2_MASKNOTPEN
                if (_dc.Rop2 == 3)
                    paint.Color = Invert(paint.Color);
                paint.BlendMode = SKBlendMode.Multiply;
                break;
            case 15: // R2_MERGEPEN
            case 12: // R2_MERGENOTPEN
                if (_dc.Rop2 == 12)
                    paint.Color = Invert(paint.Color);
                paint.BlendMode = SKBlendMode.Screen;
                break;
        }
        return true;
    }

    private static SKColor Invert(SKColor color) => new((byte)(255 - color.Red), (byte)(255 - color.Green), (byte)(255 - color.Blue), color.Alpha);

    private static SKColorFilter Inverted() => SKColorFilter.CreateColorMatrix(
    [
        -1, 0, 0, 0, 1,
        0, -1, 0, 0, 1,
        0, 0, -1, 0, 1,
        0, 0, 0, 1, 0,
    ]);

    /// <summary>A repeating pattern in device space: one pattern pixel per pattern unit, from the brush origin.</summary>
    private SKShader Pattern(SKImage image, SKMatrix matrix)
    {
        var local = SKMatrix.CreateTranslation(_dc.BrushOrigin.X, _dc.BrushOrigin.Y).PreConcat(SKMatrix.CreateScale(_patternUnit, _patternUnit));
        if (matrix.TryInvert(out var inverse))
            local = inverse.PreConcat(local);
        return image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, new SKSamplingOptions(SKFilterMode.Nearest), local);
    }

    /// <summary>An 8 × 8 hatch: HS_HORIZONTAL, HS_VERTICAL, HS_FDIAGONAL, HS_BDIAGONAL, HS_CROSS, HS_DIAGCROSS.</summary>
    private static SKImage Hatch(int hatch, SKColor ink, SKColor paper)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var on = hatch switch
                {
                    0 => y == 7,
                    1 => x == 0,
                    2 => x == y,
                    3 => x == 7 - y,
                    4 => x == 0 || y == 7,
                    _ => x == y || x == 7 - y,
                };
                bitmap.SetPixel(x, y, on ? ink : paper);
            }
        }
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>A monochrome pattern with its black in <paramref name="ink"/> and its white in <paramref name="paper"/>.</summary>
    private SKImage Colored(SKImage image, SKColor ink, SKColor paper)
    {
        if (_coloredPatterns.TryGetValue((image, ink, paper), out var colored))
            return colored;
        using var source = SKBitmap.FromImage(image);
        using var bitmap = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                bitmap.SetPixel(x, y, pixel.Red + pixel.Green + pixel.Blue >= 384 ? paper : ink);
            }
        }
        bitmap.SetImmutable();
        colored = SKImage.FromBitmap(bitmap);
        _coloredPatterns[(image, ink, paper)] = colored;
        return colored;
    }

    private static float Determinant(SKMatrix m) => m.ScaleX * m.ScaleY - m.SkewX * m.SkewY;

    /// <summary>Device units per logical unit (the square root of the transform's area scale).</summary>
    private static float UnitScale(SKMatrix m) => MathF.Sqrt(MathF.Abs(Determinant(m)));

    public void Dispose()
    {
        _path?.Dispose();
        foreach (var image in _coloredPatterns.Values)
            image.Dispose();
    }
}
