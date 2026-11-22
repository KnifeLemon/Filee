// Writes a placeable Windows Metafile ([MS-WMF], with the Aldus placeable header of section 2.3.2.3) from
// MetafileWriter's drawing calls; record layouts are those of the specification's section 2.3. What the calls become:
// MetafileWriter.cs.
//
// WMF is the older, smaller format, and the writer works within it:
// - Units: 16-bit logical coordinates. The placeable header's box is the drawing at "Inch" units per inch: 1440 (twips)
//   up to 22 inches a side, fewer for larger drawings so every coordinate fits; MM_ANISOTROPIC with the window on that
//   box, which is how Word, PowerPoint and LibreOffice place a WMF.
// - No curves, paths or world transforms: outlines are flattened into polygons (a tenth of a logical unit off at most)
//   in page coordinates.
// - No clip paths: clips are applied to the geometry itself (path intersections), so a clipped fill is the part
//   inside; a clipped picture keeps only its pixels inside (masked, see below).
// - Pens: GDI draws wide WMF pens with round caps and joins and no dashes, so only such lines are pens; other lines
//   (dashes, butt or square caps, miter or bevel joins, clipped lines) are filled as their outlines, which keeps
//   their exact form.
// - No alpha: translucent colours are mixed with white; a picture with transparent pixels is drawn through a mask
//   (SRCAND of the mask, then SRCPAINT of the picture on black), keeping the pixels at least half opaque.
// - Text: ExtTextOut in one ANSI code page, picked by what encodes the run (Western, then Korean, Japanese, Simplified
//   and Traditional Chinese, Cyrillic, Central European, Greek, Turkish, Baltic); upright or turned text only (the
//   font's escapement), not squeezed or clipped; anything else is drawn as outlines.

using System.Text;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Metafiles;

internal sealed class WmfWriter : MetafileWriter
{
    /// <summary>Logical units per inch for drawings up to 22 inches a side.</summary>
    private const int Twips = 1440;

    /// <summary>The largest coordinate the box may reach (room is left for drawing just outside it).</summary>
    private const int MaxCoordinate = 30_000;

    /// <summary>How far a flattened curve may stray from the curve, in logical units.</summary>
    private const float Flatness = 0.1f;

    /// <summary>The ANSI code pages text is tried in, with the font character set each stands for.</summary>
    private static readonly (int CodePage, byte CharSet)[] CodePages =
    [
        (1252, 0), (949, 129), (932, 128), (936, 134), (950, 136), (1251, 204), (1250, 238), (1253, 161), (1254, 162), (1257, 186),
    ];

    static WmfWriter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly MemoryStream _body = new();
    private readonly BinaryWriter _out;
    private readonly float _scale;
    private readonly int _unitsPerInch;
    private readonly (short Width, short Height) _box;
    private uint _maxRecordWords;

    // The object table: WMF objects take the lowest free entry.
    private readonly SortedSet<ushort> _free = [];
    private ushort _nextObject;
    private int _peakObjects;
    private ushort _nullPen, _nullBrush;
    private (string Key, ushort Handle)? _pen, _brush, _font;

    private SKColor _textColor = SKColors.Black;
    private int _fillMode = 1;

    /// <summary>The clip in page points (null = none), and the ones saved.</summary>
    private SKPath? _clip;
    private readonly Stack<SKPath?> _clips = new();

    public WmfWriter(float width, float height, MetafileLimits? limits = null) : base(width, height, limits)
    {
        _out = new BinaryWriter(_body);
        var inches = Math.Max(width, height) / 72;
        _unitsPerInch = (int)Math.Clamp(Math.Floor(Math.Min(Twips, MaxCoordinate / inches)), 1, Twips);
        _scale = _unitsPerInch / 72f;
        _box = ((short)Math.Clamp(Math.Round(width * _scale), 1, MaxCoordinate), (short)Math.Clamp(Math.Round(height * _scale), 1, MaxCoordinate));
        Record(0x0103, 8); // META_SETMAPMODE MM_ANISOTROPIC
        Record(0x020B, 0, 0); // META_SETWINDOWORG y, x
        Record(0x020C, _box.Height, _box.Width); // META_SETWINDOWEXT y, x
        Record(0x0102, 1); // META_SETBKMODE TRANSPARENT
        Record(0x012E, 24); // META_SETTEXTALIGN TA_BASELINE
        Record(0x0106, 1); // META_SETPOLYFILLMODE ALTERNATE
        Record(0x0107, 3); // META_SETSTRETCHBLTMODE COLORONCOLOR
        _nullPen = Create(() => Pen(5, 0, SKColors.Black)); // PS_NULL
        _nullBrush = Create(() => BrushRecord(1, SKColors.Black)); // BS_NULL
    }

    // ───────────────────────── Records ─────────────────────────

    private long Begin(ushort function)
    {
        var at = _body.Position;
        _out.Write(0u);
        _out.Write(function);
        return at;
    }

    private void End(long at)
    {
        if (_body.Position % 2 != 0)
            _out.Write((byte)0);
        var end = _body.Position;
        var words = (uint)((end - at) / 2);
        _body.Position = at;
        _out.Write(words);
        _body.Position = end;
        _maxRecordWords = Math.Max(_maxRecordWords, words);
        Count(end - at);
    }

    /// <summary>A record of 16-bit parameters.</summary>
    private void Record(ushort function, params ReadOnlySpan<int> parameters)
    {
        var at = Begin(function);
        foreach (var parameter in parameters)
            _out.Write((short)parameter);
        End(at);
    }

    private short X(float points) => (short)Math.Clamp(Math.Round(points * _scale), short.MinValue, short.MaxValue);

    private void Color(SKColor color)
    {
        color = OnWhite(color);
        _out.Write(ColorRef(color));
    }

    // ───────────────────────── Objects ─────────────────────────

    private ushort Create(Action write)
    {
        var handle = _free.Count > 0 ? _free.Min : _nextObject++;
        _free.Remove(handle);
        _peakObjects = Math.Max(_peakObjects, _nextObject);
        write();
        return handle;
    }

    private void Delete(ushort handle)
    {
        Record(0x01F0, handle); // META_DELETEOBJECT
        _free.Add(handle);
    }

    private void Select(ushort handle) => Record(0x012D, handle); // META_SELECTOBJECT

    /// <summary>Selects the object named <paramref name="key"/> into <paramref name="slot"/>, made by <paramref name="write"/>; the one it replaces is deleted.</summary>
    private void Use(ref (string Key, ushort Handle)? slot, string key, Action write)
    {
        if (slot is { } current && current.Key == key)
            return;
        var handle = Create(write);
        Select(handle);
        if (slot is { } old && old.Handle != _nullPen && old.Handle != _nullBrush)
            Delete(old.Handle);
        slot = (key, handle);
    }

    private void NullPen()
    {
        if (_pen?.Handle == _nullPen)
            return;
        Select(_nullPen);
        if (_pen is { } old && old.Handle != _nullPen)
            Delete(old.Handle);
        _pen = ("null", _nullPen);
    }

    private void NullBrush()
    {
        if (_brush?.Handle == _nullBrush)
            return;
        Select(_nullBrush);
        if (_brush is { } old && old.Handle != _nullBrush)
            Delete(old.Handle);
        _brush = ("null", _nullBrush);
    }

    private void Pen(ushort style, int width, SKColor color)
    {
        var at = Begin(0x02FA); // META_CREATEPENINDIRECT
        _out.Write(style);
        _out.Write((short)width);
        _out.Write((short)0);
        Color(color);
        End(at);
    }

    private void BrushRecord(ushort style, SKColor color)
    {
        var at = Begin(0x02FC); // META_CREATEBRUSHINDIRECT
        _out.Write(style);
        Color(color);
        _out.Write((ushort)0);
        End(at);
    }

    private void SolidBrush(SKColor color)
    {
        var shown = OnWhite(color);
        Use(ref _brush, $"b{(uint)shown}", () => BrushRecord(0, shown));
    }

    // ───────────────────────── Clips ─────────────────────────

    public override void Save() => _clips.Push(_clip is null ? null : new SKPath(_clip));

    public override void Restore()
    {
        if (!_clips.TryPop(out var clip))
            return;
        _clip?.Dispose();
        _clip = clip;
    }

    public override void Clip(SKPath path)
    {
        var next = _clip is null ? new SKPath(path) : _clip.Op(path, SKPathOp.Intersect) ?? new SKPath();
        _clip?.Dispose();
        _clip = next;
    }

    /// <summary>True when the clip leaves <paramref name="area"/> (points) whole.</summary>
    private bool Unclipped(SKRect area)
    {
        if (_clip is null)
            return true;
        if (_clip.IsEmpty)
            return false;
        using var box = new SKPath();
        box.AddRect(area);
        using var outside = box.Op(_clip, SKPathOp.Difference);
        return outside is null || outside.IsEmpty || outside.Bounds.Width * outside.Bounds.Height < 1e-4f;
    }

    // ───────────────────────── Shapes ─────────────────────────

    public override void Fill(SKPath path, SKColor color)
    {
        if (color.Alpha == 0 || path.IsEmpty)
            return;
        using var clipped = _clip is null ? null : path.Op(_clip, SKPathOp.Intersect);
        var shape = _clip is null ? path : clipped;
        if (shape is null || shape.IsEmpty)
            return;
        var polygons = Polygons(shape);
        if (polygons.Count == 0)
            return;
        SolidBrush(color);
        NullPen();
        var mode = shape.FillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd ? 1 : 2;
        if (mode != _fillMode)
        {
            Record(0x0106, mode); // META_SETPOLYFILLMODE
            _fillMode = mode;
        }
        PolyPolygon(polygons);
    }

    public override void Stroke(SKPath path, MetafilePen pen)
    {
        if (pen.Color.Alpha == 0 || path.IsEmpty)
            return;
        var width = Math.Max(1, (int)Math.Round(pen.Width * _scale));
        var bounds = path.Bounds;
        bounds.Inflate(pen.Width, pen.Width);
        var round = pen.Cap == SKStrokeCap.Round && pen.Join == SKStrokeJoin.Round || width <= 1 && pen.Cap != SKStrokeCap.Square;
        if (!round || pen.Dash is { Length: >= 2 } || !Unclipped(bounds))
        {
            using var outline = pen.Outline(path);
            Fill(outline, pen.Color);
            return;
        }
        var shown = OnWhite(pen.Color);
        Use(ref _pen, $"p{width}/{(uint)shown}", () => Pen(0, width, shown)); // PS_SOLID
        NullBrush();
        foreach (var (points, closed) in Figures(path))
        {
            if (points.Count < 2)
                continue;
            if (closed)
                PolyPolygon([points]);
            else
                Poly(0x0325, points); // META_POLYLINE
        }
    }

    /// <summary>META_POLYGON for one outline, META_POLYPOLYGON for several.</summary>
    private void PolyPolygon(List<List<(short X, short Y)>> polygons)
    {
        polygons = [.. polygons.Where(p => p.Count >= 3).SelectMany(Split)];
        if (polygons.Count == 0)
            return;
        if (polygons.Count == 1)
        {
            Poly(0x0324, polygons[0]); // META_POLYGON
            return;
        }
        // META_POLYPOLYGON holds its counts in 16 bits: outlines past that go in records of their own.
        foreach (var chunk in polygons.Chunk(1000))
        {
            var at = Begin(0x0538);
            _out.Write((ushort)chunk.Length);
            foreach (var polygon in chunk)
                _out.Write((ushort)polygon.Count);
            foreach (var polygon in chunk)
            {
                foreach (var (x, y) in polygon)
                {
                    _out.Write(x);
                    _out.Write(y);
                }
            }
            End(at);
        }
    }

    /// <summary>An outline of more points than a record's 16-bit count, in parts (drawn the same where they don't overlap).</summary>
    private static IEnumerable<List<(short X, short Y)>> Split(List<(short X, short Y)> polygon)
    {
        const int Max = 16_000;
        if (polygon.Count <= Max)
        {
            yield return polygon;
            yield break;
        }
        // A fan of parts from the first point keeps the outline's area for the simple outlines this comes up with.
        for (var start = 1; start < polygon.Count - 1; start += Max - 2)
        {
            var part = new List<(short X, short Y)> { polygon[0] };
            part.AddRange(polygon.Skip(start).Take(Max - 1));
            yield return part;
        }
    }

    private void Poly(ushort function, List<(short X, short Y)> points)
    {
        foreach (var part in points.Count <= 16_000 ? [points] : points.Chunk(16_000).Select(c => c.ToList()))
        {
            var at = Begin(function);
            _out.Write((short)part.Count);
            foreach (var (x, y) in part)
            {
                _out.Write(x);
                _out.Write(y);
            }
            End(at);
        }
    }

    /// <summary>The path's outlines in logical units (open ones taken as closed, as a fill closes them).</summary>
    private List<List<(short X, short Y)>> Polygons(SKPath path) => [.. Figures(path).Select(f => f.Points)];

    /// <summary>The path's figures flattened into logical units, and whether each was closed.</summary>
    private List<(List<(short X, short Y)> Points, bool Closed)> Figures(SKPath path)
    {
        var figures = new List<(List<(short X, short Y)> Points, bool Closed)>();
        List<(short X, short Y)>? current = null;
        void Add(SKPoint point)
        {
            current ??= [];
            var p = (X(point.X), X(point.Y));
            if (current.Count == 0 || current[^1] != p)
                current.Add(p);
        }
        void Finish(bool closed)
        {
            if (current is { Count: > 0 })
                figures.Add((current, closed));
            current = null;
        }
        var tolerance = Flatness / _scale;
        using var iterator = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Finish(false);
                    Add(pts[0]);
                    break;
                case SKPathVerb.Line:
                    Add(pts[1]);
                    break;
                case SKPathVerb.Quad:
                    Curve(pts[0], Toward(pts[0], pts[1]), Toward(pts[2], pts[1]), pts[2], tolerance, Add);
                    break;
                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], iterator.ConicWeight(), 3);
                    for (var i = 0; i + 2 < quads.Length; i += 2)
                        Curve(quads[i], Toward(quads[i], quads[i + 1]), Toward(quads[i + 2], quads[i + 1]), quads[i + 2], tolerance, Add);
                    break;
                case SKPathVerb.Cubic:
                    Curve(pts[0], pts[1], pts[2], pts[3], tolerance, Add);
                    break;
                case SKPathVerb.Close:
                    Finish(true);
                    break;
            }
        }
        Finish(false);
        return figures;
    }

    private static float Sq(float v) => v * v;

    /// <summary>The control point of a cubic that draws a quadratic's curve: two thirds of the way to its control point.</summary>
    private static SKPoint Toward(SKPoint end, SKPoint control) => new(end.X + (control.X - end.X) * 2 / 3, end.Y + (control.Y - end.Y) * 2 / 3);

    /// <summary>A cubic Bézier as line segments within <paramref name="tolerance"/> of it (its end points included, not its start).</summary>
    private static void Curve(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, float tolerance, Action<SKPoint> add)
    {
        // The control polygon's deviation bounds the curve's: segments enough that each strays at most the tolerance.
        var dd = Math.Max(MathF.Sqrt(Sq(p0.X - 2 * p1.X + p2.X) + Sq(p0.Y - 2 * p1.Y + p2.Y)), MathF.Sqrt(Sq(p1.X - 2 * p2.X + p3.X) + Sq(p1.Y - 2 * p2.Y + p3.Y)));
        var steps = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(0.75 * dd / Math.Max(tolerance, 1e-4f))), 1, 256);
        for (var i = 1; i <= steps; i++)
        {
            var t = (float)i / steps;
            var u = 1 - t;
            add(new SKPoint(
                u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y));
        }
    }

    public override void Gradient(SKPath path, MetafileGradient gradient) => Bands(path, gradient, OnWhite);

    // ───────────────────────── Pictures ─────────────────────────

    protected override void Picture(SKBitmap bitmap, SKRect dest)
    {
        var (x, y) = (X(dest.Left), X(dest.Top));
        var (cx, cy) = ((short)Math.Max(1, X(dest.Right) - x), (short)Math.Max(1, X(dest.Bottom) - y));
        var clipped = !Unclipped(dest);
        if (!clipped && !HasAlpha(bitmap))
        {
            StretchDib(bitmap, x, y, cx, cy, 0x00CC0020, mask: false); // SRCCOPY
            return;
        }
        // Pixels outside the clip are made transparent; then the mask (white where transparent) is ANDed in and the
        // picture (black where transparent) ORed over it.
        using var masked = clipped ? Clipped(bitmap, dest) : null;
        var picture = masked ?? bitmap;
        StretchDib(picture, x, y, cx, cy, 0x008800C6, mask: true); // SRCAND
        StretchDib(picture, x, y, cx, cy, 0x00EE0086, mask: false, blackWhereTransparent: true); // SRCPAINT
    }

    /// <summary>The picture with its pixels outside the clip made transparent.</summary>
    private SKBitmap Clipped(SKBitmap bitmap, SKRect dest)
    {
        var copy = bitmap.Copy();
        using var mask = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(mask))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(bitmap.Width / dest.Width, bitmap.Height / dest.Height);
            canvas.Translate(-dest.Left, -dest.Top);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            if (_clip is { } clip)
                canvas.DrawPath(clip, paint);
        }
        var coverage = mask.GetPixelSpan();
        unsafe
        {
            var pixels = new Span<byte>((void*)copy.GetPixels(), copy.RowBytes * copy.Height);
            for (var y = 0; y < copy.Height; y++)
                for (var x = 0; x < copy.Width; x++)
                {
                    ref var alpha = ref pixels[y * copy.RowBytes + x * 4 + 3];
                    alpha = (byte)(alpha * coverage[y * mask.RowBytes + x] / 255);
                }
        }
        return copy;
    }

    /// <summary>META_STRETCHDIB of a 24-bit DIB: the picture, its mask, or the picture black where it is transparent.</summary>
    private void StretchDib(SKBitmap bitmap, short x, short y, short width, short height, uint rop, bool mask, bool blackWhereTransparent = false)
    {
        var stride = (bitmap.Width * 3 + 3) & ~3;
        var bits = new byte[(long)stride * bitmap.Height];
        var pixels = bitmap.GetPixelSpan();
        for (var row = 0; row < bitmap.Height; row++)
        {
            var target = (bitmap.Height - 1 - row) * stride;
            for (var column = 0; column < bitmap.Width; column++)
            {
                var source = row * bitmap.RowBytes + column * 4;
                var opaque = pixels[source + 3] >= 128;
                for (var c = 0; c < 3; c++)
                {
                    bits[target + column * 3 + c] = mask ? (opaque ? (byte)0 : (byte)255)
                        : blackWhereTransparent && !opaque ? (byte)0
                        : pixels[source + c];
                }
            }
        }
        var header = EmfWriter.InfoHeader(bitmap.Width, bitmap.Height, 24, bits.Length);
        var at = Begin(0x0F43);
        _out.Write(rop);
        _out.Write((ushort)0); // DIB_RGB_COLORS
        _out.Write((short)bitmap.Height);
        _out.Write((short)bitmap.Width);
        _out.Write((short)0);
        _out.Write((short)0);
        _out.Write(height);
        _out.Write(width);
        _out.Write(y);
        _out.Write(x);
        _out.Write(header);
        _out.Write(bits);
        End(at);
    }

    // ───────────────────────── Text ─────────────────────────

    protected override bool TryText(GlyphRun run, MetafileFace face, IReadOnlyList<MetafileTextSegment> segments)
    {
        if (run.ScaleX != 1)
            return false;
        // Upright or turned only, at one scale: the font's escapement turns it.
        var m = run.Transform ?? SKMatrix.Identity;
        if (Math.Abs(m.ScaleX - m.ScaleY) > 1e-3f || Math.Abs(m.SkewX + m.SkewY) > 1e-3f || m.ScaleX * m.ScaleY - m.SkewX * m.SkewY <= 0)
            return false;
        var scale = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
        var angle = MathF.Atan2(m.SkewY, m.ScaleX);
        var text = string.Concat(segments.Select(s => s.Text));
        if (Encode(text) is not var (codePage, charSet))
            return false;
        // Clipped text can't be cut in WMF.
        using (var outlines = Outlines(run))
        {
            var bounds = outlines.Bounds;
            bounds.Inflate(1, 1);
            if (!Unclipped(bounds))
                return false;
        }
        var encoding = Encoding.GetEncoding(codePage);
        var height = (short)Math.Clamp(Math.Round(run.Size * scale * _scale), 1, short.MaxValue);
        var escapement = (short)Math.Round(-angle * 1800 / MathF.PI);
        var key = $"f{face.Name}/{height}/{face.Weight}/{face.Italic}/{charSet}/{escapement}";
        Use(ref _font, key, () => Font(face, height, escapement, charSet, encoding));
        var shown = OnWhite(run.Color);
        if (shown != _textColor)
        {
            var at = Begin(0x0209); // META_SETTEXTCOLOR
            Color(shown);
            End(at);
            _textColor = shown;
        }
        foreach (var segment in segments)
            TextRecord(segment, m, scale, encoding);
        return true;
    }

    /// <summary>The first code page that holds all of <paramref name="text"/>, or null.</summary>
    private static (int CodePage, byte CharSet)? Encode(string text)
    {
        foreach (var (codePage, charSet) in CodePages)
        {
            var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            try
            {
                encoding.GetBytes(text);
                return (codePage, charSet);
            }
            catch (EncoderFallbackException)
            {
            }
        }
        return null;
    }

    private void TextRecord(MetafileTextSegment segment, SKMatrix frame, float scale, Encoding encoding)
    {
        var origin = frame.MapPoint(segment.Origin);
        var bytes = new List<byte>();
        var dx = new List<short>();
        var total = 0.0;
        var placed = 0;
        for (var i = 0; i < segment.Text.Length; i++)
        {
            var length = char.IsHighSurrogate(segment.Text[i]) && i + 1 < segment.Text.Length ? 2 : 1;
            var encoded = encoding.GetBytes(segment.Text.Substring(i, length));
            var advance = segment.Advances[i] + (length == 2 ? segment.Advances[i + 1] : 0);
            total += advance * scale * _scale;
            var next = (int)Math.Round(total);
            // The advance on the character's first byte, nothing on the others.
            for (var b = 0; b < encoded.Length; b++)
            {
                bytes.Add(encoded[b]);
                dx.Add((short)(b == 0 ? next - placed : 0));
            }
            placed = next;
            i += length - 1;
        }
        foreach (var part in Chunks(bytes.Count, 8_000))
        {
            var at = Begin(0x0A32); // META_EXTTEXTOUT
            var start = part.Start;
            var shift = dx.Take(start).Sum(d => d);
            var along = new SKPoint(MathF.Cos(MathF.Atan2(frame.SkewY, frame.ScaleX)), MathF.Sin(MathF.Atan2(frame.SkewY, frame.ScaleX)));
            _out.Write(X(origin.Y + along.Y * shift / _scale));
            _out.Write(X(origin.X + along.X * shift / _scale));
            _out.Write((short)part.Length);
            _out.Write((ushort)0);
            _out.Write(bytes.GetRange(start, part.Length).ToArray());
            if (part.Length % 2 != 0)
                _out.Write((byte)0);
            foreach (var d in dx.GetRange(start, part.Length))
                _out.Write(d);
            End(at);
        }
    }

    private static IEnumerable<(int Start, int Length)> Chunks(int count, int size)
    {
        for (var start = 0; start < count; start += size)
            yield return (start, Math.Min(size, count - start));
    }

    private void Font(MetafileFace face, short height, short escapement, byte charSet, Encoding encoding)
    {
        var at = Begin(0x02FB); // META_CREATEFONTINDIRECT
        _out.Write((short)-height);
        _out.Write((short)0);
        _out.Write(escapement);
        _out.Write(escapement);
        _out.Write((short)face.Weight);
        _out.Write(face.Italic ? (byte)1 : (byte)0);
        _out.Write((byte)0);
        _out.Write((byte)0);
        _out.Write(charSet);
        _out.Write((byte)7); // OUT_TT_ONLY_PRECIS
        _out.Write((byte)0);
        _out.Write((byte)5); // CLEARTYPE_QUALITY
        _out.Write((byte)0);
        var name = new byte[32];
        byte[] encoded;
        try
        {
            encoded = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(face.Name);
        }
        catch (EncoderFallbackException)
        {
            encoded = Encoding.ASCII.GetBytes(face.Name);
        }
        encoded.AsSpan(0, Math.Min(encoded.Length, 31)).CopyTo(name);
        _out.Write(name);
        End(at);
    }

    // ───────────────────────── File ─────────────────────────

    public override byte[] ToArray()
    {
        var eof = Begin(0x0000); // META_EOF
        End(eof);
        const int Placeable = 22, MetaHeader = 18;
        var output = new byte[Placeable + MetaHeader + _body.Length];
        using (var writer = new BinaryWriter(new MemoryStream(output)))
        {
            writer.Write(WmfHeader.PlaceableKey);
            writer.Write((ushort)0);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write(_box.Width);
            writer.Write(_box.Height);
            writer.Write((ushort)_unitsPerInch);
            writer.Write(0u);
            ushort checksum = 0;
            for (var i = 0; i < 20; i += 2)
                checksum ^= (ushort)(output[i] | output[i + 1] << 8);
            writer.Write(checksum);
            writer.Write((ushort)1); // MEMORYMETAFILE
            writer.Write((ushort)9);
            writer.Write((ushort)0x0300);
            writer.Write((uint)((MetaHeader + _body.Length) / 2));
            writer.Write((ushort)Math.Max(1, _peakObjects));
            writer.Write(_maxRecordWords);
            writer.Write((ushort)0);
        }
        Array.Copy(_body.GetBuffer(), 0, output, Placeable + MetaHeader, _body.Length);
        return output;
    }
}
