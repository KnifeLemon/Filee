// Writes an Enhanced Metafile ([MS-EMF]) from MetafileWriter's drawing calls; record layouts are those of the
// specification's section 2.3. What the calls become, and why there is no EMF+: MetafileWriter.cs.
//
// Units: the reference device is 1440 dpi (a device unit is 1/20 point; szlDevice 288,000 pixels over szlMillimeters
// 5,080 mm, 200 inches, with szlMicrometers for readers that take it), the map mode MM_TEXT, so a page point is 20
// logical units. The picture frame (rclFrame, 0.01 mm) is the drawing's size, which is what Word, PowerPoint and
// LibreOffice insert it at; rclBounds covers the same area in device units.
//
// Paths are written in device units with the identity world transform; the world transform is set only for text (a
// run's own frame: turned and squeezed text) and put back after it. Clips are path brackets selected with RGN_AND
// between SaveDC and RestoreDC. Objects (brushes, pens, fonts) are made once and reused; when many are alive the ones no
// saved state still selects are deleted so the handle table stays small.

using System.Text;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Metafiles;

internal sealed class EmfWriter : MetafileWriter
{
    /// <summary>Device (and logical) units per point.</summary>
    private const float Units = 20;

    /// <summary>The reference device: 200 inches a side at 1440 dpi.</summary>
    private const int DevicePixels = 288_000, DeviceMillimeters = 5_080;

    private const uint NullBrush = 0x80000005, NullPen = 0x80000008;

    /// <summary>Objects alive at most before those no state selects are deleted.</summary>
    private const int MaxLiveObjects = 1_024;

    private readonly MemoryStream _body = new();
    private readonly BinaryWriter _out;
    private readonly Dictionary<string, uint> _objects = [];
    private readonly SortedSet<uint> _freeHandles = [];
    private uint _nextHandle = 1;
    private State _state = new(NullBrush, NullPen, 0, SKColors.Black, 1, 10);
    private readonly Stack<State> _saved = new();

    /// <summary>The device context's selections and settings the writer keeps track of (SaveDC / RestoreDC bring them back).</summary>
    private readonly record struct State(uint Brush, uint Pen, uint Font, SKColor TextColor, int FillMode, float Miter);

    public EmfWriter(float width, float height, MetafileLimits? limits = null) : base(width, height, limits)
    {
        _out = new BinaryWriter(_body);
        Simple(18, 1); // EMR_SETBKMODE TRANSPARENT: text has no background box
        Simple(22, 24); // EMR_SETTEXTALIGN TA_BASELINE | TA_LEFT | TA_NOUPDATECP
        Simple(19, 1); // EMR_SETPOLYFILLMODE ALTERNATE (the state above)
        Simple(58, 10); // EMR_SETMITERLIMIT (GDI's default, the state above)
        Simple(21, 4); // EMR_SETSTRETCHBLTMODE HALFTONE: stretched pictures are smoothed
        Simple(24, ColorRef(SKColors.Black)); // EMR_SETTEXTCOLOR
    }

    // ───────────────────────── Records ─────────────────────────

    private long Begin(uint type)
    {
        var at = _body.Position;
        _out.Write(type);
        _out.Write(0u);
        return at;
    }

    private void End(long at)
    {
        while (_body.Position % 4 != 0)
            _out.Write((byte)0);
        var end = _body.Position;
        var size = end - at;
        _body.Position = at + 4;
        _out.Write((uint)size);
        _body.Position = end;
        Count(size);
    }

    /// <summary>A record of one 32-bit value.</summary>
    private void Simple(uint type, uint value)
    {
        var at = Begin(type);
        _out.Write(value);
        End(at);
    }

    private void Empty(uint type) => End(Begin(type));

    private static int D(float points) => (int)Math.Clamp(Math.Round(points * Units), -1_000_000_000, 1_000_000_000);

    private void Rect(SKRect points)
    {
        _out.Write(D(points.Left));
        _out.Write(D(points.Top));
        _out.Write(Math.Max(D(points.Left), D(points.Right) - 1));
        _out.Write(Math.Max(D(points.Top), D(points.Bottom) - 1));
    }

    private void Matrix(SKMatrix m)
    {
        // XFORM: eM11, eM12, eM21, eM22, eDx, eDy (x' = x·eM11 + y·eM21 + eDx).
        _out.Write(m.ScaleX);
        _out.Write(m.SkewY);
        _out.Write(m.SkewX);
        _out.Write(m.ScaleY);
        _out.Write(m.TransX);
        _out.Write(m.TransY);
    }

    // ───────────────────────── State ─────────────────────────

    public override void Save()
    {
        Empty(33); // EMR_SAVEDC
        _saved.Push(_state);
    }

    public override void Restore()
    {
        if (!_saved.TryPop(out var state))
            return;
        Simple(34, unchecked((uint)-1)); // EMR_RESTOREDC
        _state = state;
    }

    private void FillMode(SKPath path)
    {
        var mode = path.FillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd ? 1 : 2;
        if (mode == _state.FillMode)
            return;
        Simple(19, (uint)mode); // EMR_SETPOLYFILLMODE
        _state = _state with { FillMode = mode };
    }

    private void Select(uint handle)
    {
        var at = Begin(37); // EMR_SELECTOBJECT
        _out.Write(handle);
        End(at);
    }

    /// <summary>The handle of the object named <paramref name="key"/>, made by <paramref name="create"/> the first time.</summary>
    private uint Object(string key, Action<uint> create)
    {
        if (_objects.TryGetValue(key, out var handle))
            return handle;
        if (_objects.Count >= MaxLiveObjects)
            Sweep();
        handle = _freeHandles.Count > 0 ? _freeHandles.Min : _nextHandle++;
        _freeHandles.Remove(handle);
        if (handle > Limits.MaxObjects)
            throw MetafileLimits.Exceeded($"The drawing needs more than {Limits.MaxObjects} metafile objects at once.");
        create(handle);
        _objects[key] = handle;
        return handle;
    }

    /// <summary>Deletes the objects no state selects.</summary>
    private void Sweep()
    {
        var kept = new HashSet<uint>(_saved.SelectMany(s => new[] { s.Brush, s.Pen, s.Font })) { _state.Brush, _state.Pen, _state.Font };
        foreach (var (key, handle) in _objects.Where(o => !kept.Contains(o.Value)).ToList())
        {
            var at = Begin(40); // EMR_DELETEOBJECT
            _out.Write(handle);
            End(at);
            _objects.Remove(key);
            _freeHandles.Add(handle);
        }
    }

    private void Brush(uint handle)
    {
        if (_state.Brush == handle)
            return;
        Select(handle);
        _state = _state with { Brush = handle };
    }

    private void Brush(SKColor color) => Brush(Object($"b{(uint)color.WithAlpha(255)}", handle =>
    {
        var at = Begin(39); // EMR_CREATEBRUSHINDIRECT
        _out.Write(handle);
        _out.Write(0u); // BS_SOLID
        _out.Write(ColorRef(color));
        _out.Write(0u);
        End(at);
    }));

    private void Pen(uint handle)
    {
        if (_state.Pen == handle)
            return;
        Select(handle);
        _state = _state with { Pen = handle };
    }

    private void Pen(MetafilePen pen)
    {
        var width = Math.Max(1, D(pen.Width));
        var dashes = pen.Dash is { Length: >= 2 } d ? (d.Length % 2 == 0 ? d : [.. d, .. d]).Select(v => Math.Max(1, D(v))).ToArray() : null;
        var style = 0x00010000u // PS_GEOMETRIC
            | (dashes is null ? 0u : 7u) // PS_SOLID or PS_USERSTYLE
            | pen.Cap switch { SKStrokeCap.Round => 0u, SKStrokeCap.Square => 0x100u, _ => 0x200u }
            | pen.Join switch { SKStrokeJoin.Round => 0u, SKStrokeJoin.Bevel => 0x1000u, _ => 0x2000u };
        var key = $"p{style:x}/{width}/{ColorRef(pen.Color):x}/{(dashes is null ? "" : string.Join(',', dashes))}";
        Pen(Object(key, handle =>
        {
            var at = Begin(95); // EMR_EXTCREATEPEN
            _out.Write(handle);
            _out.Write(0u); // no brush bitmap
            _out.Write(0u);
            _out.Write(0u);
            _out.Write(0u);
            _out.Write(style);
            _out.Write((uint)width);
            _out.Write(0u); // BS_SOLID
            _out.Write(ColorRef(pen.Color));
            _out.Write(0u);
            _out.Write((uint)(dashes?.Length ?? 0));
            foreach (var dash in dashes ?? [])
                _out.Write((uint)dash);
            End(at);
        }));
        if (pen.Join == SKStrokeJoin.Miter && Math.Abs(_state.Miter - pen.Miter) > 1e-3f)
        {
            Simple(58, (uint)Math.Clamp(Math.Round(pen.Miter), 1, 100)); // EMR_SETMITERLIMIT
            _state = _state with { Miter = (float)Math.Clamp(Math.Round(pen.Miter), 1, 100) };
        }
    }

    // ───────────────────────── Paths ─────────────────────────

    /// <summary>The path as a BeginPath … EndPath bracket in device units.</summary>
    private void Bracket(SKPath path)
    {
        Empty(59); // EMR_BEGINPATH
        var pending = new List<SKPoint>();
        var bezier = false;
        void Flush()
        {
            if (pending.Count == 0)
                return;
            var points = pending.Select(p => (X: D(p.X), Y: D(p.Y))).ToList();
            var small = points.All(p => p.X is >= short.MinValue and <= short.MaxValue && p.Y is >= short.MinValue and <= short.MaxValue);
            // EMR_POLYBEZIERTO(16) / EMR_POLYLINETO(16)
            var at = Begin(bezier ? (small ? 88u : 5u) : (small ? 89u : 6u));
            _out.Write(points.Min(p => p.X));
            _out.Write(points.Min(p => p.Y));
            _out.Write(points.Max(p => p.X));
            _out.Write(points.Max(p => p.Y));
            _out.Write((uint)points.Count);
            foreach (var (x, y) in points)
            {
                if (small)
                {
                    _out.Write((short)x);
                    _out.Write((short)y);
                }
                else
                {
                    _out.Write(x);
                    _out.Write(y);
                }
            }
            End(at);
            pending.Clear();
        }
        void Add(bool curve, params ReadOnlySpan<SKPoint> points)
        {
            if (pending.Count > 0 && bezier != curve)
                Flush();
            bezier = curve;
            foreach (var point in points)
                pending.Add(point);
        }
        void Cubic(SKPoint p0, SKPoint q, SKPoint p2) =>
            Add(true, p0 + new SKPoint((q.X - p0.X) * 2 / 3, (q.Y - p0.Y) * 2 / 3), p2 + new SKPoint((q.X - p2.X) * 2 / 3, (q.Y - p2.Y) * 2 / 3), p2);

        using var iterator = path.CreateRawIterator();
        var p = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(p)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Flush();
                    var at = Begin(27); // EMR_MOVETOEX
                    _out.Write(D(p[0].X));
                    _out.Write(D(p[0].Y));
                    End(at);
                    break;
                case SKPathVerb.Line:
                    Add(false, p[1]);
                    break;
                case SKPathVerb.Quad:
                    Cubic(p[0], p[1], p[2]);
                    break;
                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(p[0], p[1], p[2], iterator.ConicWeight(), 2);
                    for (var i = 0; i + 2 < quads.Length; i += 2)
                        Cubic(quads[i], quads[i + 1], quads[i + 2]);
                    break;
                case SKPathVerb.Cubic:
                    Add(true, p[1], p[2], p[3]);
                    break;
                case SKPathVerb.Close:
                    Flush();
                    Empty(61); // EMR_CLOSEFIGURE
                    break;
            }
        }
        Flush();
        Empty(60); // EMR_ENDPATH
    }

    private void PathRecord(uint type, SKRect bounds)
    {
        var at = Begin(type);
        Rect(bounds);
        End(at);
    }

    public override void Clip(SKPath path)
    {
        FillMode(path);
        Bracket(path);
        Simple(67, 1); // EMR_SELECTCLIPPATH RGN_AND
    }

    public override void Fill(SKPath path, SKColor color)
    {
        if (color.Alpha == 0 || path.IsEmpty)
            return;
        if (color.Alpha < 255)
        {
            // GDI brushes have no alpha: one pixel of the colour blended over the path's bounds, clipped to the path.
            Save();
            Clip(path);
            Blend(color, path.Bounds);
            Restore();
            return;
        }
        Brush(color);
        Pen(NullPen);
        FillMode(path);
        Bracket(path);
        PathRecord(62, path.Bounds); // EMR_FILLPATH
    }

    public override void Stroke(SKPath path, MetafilePen pen)
    {
        if (pen.Color.Alpha == 0 || path.IsEmpty)
            return;
        if (pen.Color.Alpha < 255)
        {
            using var outline = pen.Outline(path);
            Fill(outline, pen.Color);
            return;
        }
        Pen(pen);
        Brush(NullBrush);
        Bracket(path);
        var bounds = path.Bounds;
        bounds.Inflate(pen.Width, pen.Width);
        PathRecord(64, bounds); // EMR_STROKEPATH
    }

    // ───────────────────────── Gradients ─────────────────────────

    public override void Gradient(SKPath path, MetafileGradient gradient)
    {
        if (gradient.Stops.Count == 0 || path.IsEmpty)
            return;
        if (!gradient.Opaque)
        {
            // Translucent: a bitmap with the gradient's alpha, clipped to the path.
            Save();
            Clip(path);
            using (var shader = gradient.Shader())
            using (var paint = new SKPaint { Shader = shader })
            {
                var bounds = path.Bounds;
                Rasterize(bounds, 1.5f, canvas => canvas.DrawRect(bounds, paint));
            }
            Restore();
            return;
        }
        var frame = gradient.Frame;
        var across = Math.Abs(frame.SkewX) < 1e-6f && Math.Abs(frame.SkewY) < 1e-6f && Math.Abs(frame.ScaleX) > 1e-6f;
        var down = Math.Abs(frame.ScaleX) < 1e-6f && Math.Abs(frame.ScaleY) < 1e-6f && Math.Abs(frame.SkewY) > 1e-6f;
        if (gradient.Kind != MetafileGradientKind.Linear || !(across || down))
        {
            Bands(path, gradient, c => c);
            return;
        }
        // An axis-aligned linear gradient: GradientFill rectangles from stop to stop across the path's bounds.
        var box = path.Bounds;
        var (scale, offset) = across ? (frame.ScaleX, frame.TransX) : (frame.SkewY, frame.TransY);
        var (low, high) = across ? (box.Left, box.Right) : (box.Top, box.Bottom);
        var (t0, t1) = ((low - offset) / scale, (high - offset) / scale);
        if (t0 > t1)
            (t0, t1) = (t1, t0);
        var marks = new List<float> { t0 };
        marks.AddRange(gradient.Stops.Select(s => s.Position).Where(p => p > t0 && p < t1));
        marks.Add(t1);
        Save();
        Clip(path);
        var at = Begin(118); // EMR_GRADIENTFILL
        Rect(box);
        var rectangles = marks.Count - 1;
        _out.Write((uint)(rectangles * 2));
        _out.Write((uint)rectangles);
        _out.Write(across ? 0u : 1u); // GRADIENT_FILL_RECT_H / _V
        var overlap = 0.5f / Math.Abs(scale); // half a point into the one before, so smoothed edges leave no seam
        for (var i = 0; i < rectangles; i++)
        {
            var (a, b) = (marks[i], marks[i + 1]);
            var (ca, cb) = (gradient.At(a), gradient.At(b));
            var from = i == 0 ? a : Math.Max(marks[i - 1], a - Math.Min(overlap, (b - a) / 4));
            var (pa, pb) = (offset + from * scale, offset + b * scale);
            if (pa > pb)
            {
                (pa, pb) = (pb, pa);
                (ca, cb) = (cb, ca);
            }
            Vertex(across ? new SKPoint(pa, box.Top) : new SKPoint(box.Left, pa), ca);
            Vertex(across ? new SKPoint(pb, box.Bottom) : new SKPoint(box.Right, pb), cb);
        }
        for (var i = 0; i < rectangles; i++)
        {
            _out.Write((uint)(i * 2));
            _out.Write((uint)(i * 2 + 1));
        }
        // GDI sizes the objects of every mode as triangles (12 bytes) and refuses a shorter record: Windows' own
        // GradientFill records end with that room.
        _out.Write(new byte[rectangles * 4]);
        End(at);
        Restore();
    }

    private void Vertex(SKPoint point, SKColor color)
    {
        _out.Write(D(point.X));
        _out.Write(D(point.Y));
        _out.Write((ushort)(color.Red << 8));
        _out.Write((ushort)(color.Green << 8));
        _out.Write((ushort)(color.Blue << 8));
        _out.Write((ushort)0);
    }

    // ───────────────────────── Pictures ─────────────────────────

    protected override void Picture(SKBitmap bitmap, SKRect dest)
    {
        var alpha = HasAlpha(bitmap);
        var (header, bits) = Dib(bitmap, alpha);
        if (alpha)
        {
            AlphaBlend(dest, header, bits, bitmap.Width, bitmap.Height, 255, perPixel: true);
            return;
        }
        var (x, y) = (D(dest.Left), D(dest.Top));
        var (cx, cy) = (Math.Max(1, D(dest.Right) - x), Math.Max(1, D(dest.Bottom) - y));
        var at = Begin(81); // EMR_STRETCHDIBITS
        Rect(dest);
        _out.Write(x);
        _out.Write(y);
        _out.Write(0);
        _out.Write(0);
        _out.Write(bitmap.Width);
        _out.Write(bitmap.Height);
        _out.Write(80u);
        _out.Write((uint)header.Length);
        _out.Write((uint)(80 + header.Length));
        _out.Write((uint)bits.Length);
        _out.Write(0u); // DIB_RGB_COLORS
        _out.Write(0x00CC0020u); // SRCCOPY
        _out.Write(cx);
        _out.Write(cy);
        _out.Write(header);
        _out.Write(bits);
        End(at);
    }

    /// <summary>One pixel of <paramref name="color"/> blended over <paramref name="area"/> at its opacity.</summary>
    private void Blend(SKColor color, SKRect area)
    {
        var header = InfoHeader(1, 1, 32);
        AlphaBlend(area, header, [color.Blue, color.Green, color.Red, 255], 1, 1, color.Alpha, perPixel: false);
    }

    private void AlphaBlend(SKRect dest, byte[] header, byte[] bits, int width, int height, byte constant, bool perPixel)
    {
        var (x, y) = (D(dest.Left), D(dest.Top));
        var (cx, cy) = (Math.Max(1, D(dest.Right) - x), Math.Max(1, D(dest.Bottom) - y));
        var at = Begin(114); // EMR_ALPHABLEND
        Rect(dest);
        _out.Write(x);
        _out.Write(y);
        _out.Write(cx);
        _out.Write(cy);
        // BLENDFUNCTION: AC_SRC_OVER, no flags, the constant alpha, AC_SRC_ALPHA for premultiplied pixels with alpha.
        _out.Write((byte)0);
        _out.Write((byte)0);
        _out.Write(constant);
        _out.Write(perPixel ? (byte)1 : (byte)0);
        _out.Write(0);
        _out.Write(0);
        Matrix(SKMatrix.Identity);
        _out.Write(0u); // BkColorSrc
        _out.Write(0u); // DIB_RGB_COLORS
        _out.Write(108u);
        _out.Write((uint)header.Length);
        _out.Write((uint)(108 + header.Length));
        _out.Write((uint)bits.Length);
        _out.Write(width);
        _out.Write(height);
        _out.Write(header);
        _out.Write(bits);
        End(at);
    }

    /// <summary>
    /// A bitmap as a bottom-up DIB: 24 bits per pixel when opaque, else 32 with premultiplied alpha (what AlphaBlend's
    /// AC_SRC_ALPHA takes).
    /// </summary>
    internal static (byte[] Header, byte[] Bits) Dib(SKBitmap bitmap, bool alpha)
    {
        var (width, height) = (bitmap.Width, bitmap.Height);
        var bytesPerPixel = alpha ? 4 : 3;
        var stride = (width * bytesPerPixel + 3) & ~3;
        var bits = new byte[(long)stride * height];
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            var row = (height - 1 - y) * stride;
            for (var x = 0; x < width; x++)
            {
                var source = y * bitmap.RowBytes + x * 4;
                var (b, g, r, a) = (pixels[source], pixels[source + 1], pixels[source + 2], pixels[source + 3]);
                var target = row + x * bytesPerPixel;
                if (alpha)
                {
                    bits[target] = (byte)((b * a + 127) / 255);
                    bits[target + 1] = (byte)((g * a + 127) / 255);
                    bits[target + 2] = (byte)((r * a + 127) / 255);
                    bits[target + 3] = a;
                }
                else
                {
                    bits[target] = b;
                    bits[target + 1] = g;
                    bits[target + 2] = r;
                }
            }
        }
        return (InfoHeader(width, height, bytesPerPixel * 8, bits.Length), bits);
    }

    /// <summary>A BITMAPINFOHEADER of a bottom-up BI_RGB bitmap.</summary>
    internal static byte[] InfoHeader(int width, int height, int bitCount, int imageSize = 0)
    {
        var header = new byte[40];
        var span = header.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[4..], width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[8..], height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)bitCount);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[20..], imageSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[24..], 3780); // 96 dpi
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[28..], 3780);
        return header;
    }

    // ───────────────────────── Text ─────────────────────────

    protected override bool TryText(GlyphRun run, MetafileFace face, IReadOnlyList<MetafileTextSegment> segments)
    {
        if (run.Color.Alpha < 255)
            return false; // text colours have no alpha: the outlines take the blend
        var height = Math.Max(1, (int)Math.Round(run.Size * Units));
        var font = Object($"f{face.Name}/{height}/{face.Weight}/{face.Italic}", handle => Font(handle, face, height));
        if (_state.Font != font)
        {
            Select(font);
            _state = _state with { Font = font };
        }
        if (_state.TextColor != run.Color)
        {
            Simple(24, ColorRef(run.Color)); // EMR_SETTEXTCOLOR
            _state = _state with { TextColor = run.Color };
        }
        var frame = run.Transform ?? SKMatrix.Identity;
        foreach (var segment in segments)
        {
            // The world transform: the run's frame at the segment's origin, squeezed glyphs squeezed, in 1/20 points.
            var world = SKMatrix.CreateScale(Units, Units).PreConcat(frame)
                .PreConcat(SKMatrix.CreateTranslation(segment.Origin.X, segment.Origin.Y))
                .PreConcat(SKMatrix.CreateScale(run.ScaleX / Units, 1 / Units));
            var at = Begin(35); // EMR_SETWORLDTRANSFORM
            Matrix(world);
            End(at);
            TextRecord(run, segment, world);
            at = Begin(36); // EMR_MODIFYWORLDTRANSFORM MWT_IDENTITY
            Matrix(SKMatrix.Identity);
            _out.Write(1u);
            End(at);
        }
        return true;
    }

    private void TextRecord(GlyphRun run, MetafileTextSegment segment, SKMatrix world)
    {
        var text = segment.Text;
        // Advances in the frame's units (1/20 point, before the squeeze), rounded so they add up to where each glyph is.
        var dx = new int[text.Length];
        var total = 0.0;
        var placed = 0;
        for (var i = 0; i < text.Length; i++)
        {
            total += segment.Advances[i] * Units / run.ScaleX;
            var next = (int)Math.Round(total);
            dx[i] = next - placed;
            placed = next;
        }
        var device = world.MapRect(new SKRect(0, -run.Size * Units, placed, run.Size * Units * 0.3f));
        device = new SKRect(device.Left / Units, device.Top / Units, device.Right / Units, device.Bottom / Units);
        var stringOffset = 76u;
        var stringBytes = text.Length * 2;
        var dxOffset = (uint)(stringOffset + ((stringBytes + 3) & ~3));
        var at = Begin(84); // EMR_EXTTEXTOUTW
        Rect(device);
        _out.Write(2u); // GM_ADVANCED: the world transform turns and scales the glyphs
        _out.Write(0f);
        _out.Write(0f);
        _out.Write(0); // reference point (the segment's origin)
        _out.Write(0);
        _out.Write((uint)text.Length);
        _out.Write(stringOffset);
        _out.Write(0u); // no ETO_OPAQUE / ETO_CLIPPED
        _out.Write(0);
        _out.Write(0);
        _out.Write(-1);
        _out.Write(-1);
        _out.Write(dxOffset);
        _out.Write(Encoding.Unicode.GetBytes(text));
        while ((_body.Position - at) % 4 != 0)
            _out.Write((byte)0);
        foreach (var advance in dx)
            _out.Write(advance);
        End(at);
    }

    /// <summary>EMR_EXTCREATEFONTINDIRECTW with a LogFontPanose (its fixed 320-byte form).</summary>
    private void Font(uint handle, MetafileFace face, int height)
    {
        var at = Begin(82);
        _out.Write(handle);
        _out.Write(-height); // lfHeight < 0: the em height
        _out.Write(0); // lfWidth: the font's own
        _out.Write(0); // lfEscapement
        _out.Write(0); // lfOrientation
        _out.Write(face.Weight);
        _out.Write(face.Italic ? (byte)1 : (byte)0);
        _out.Write((byte)0); // underline (drawn as lines of its own)
        _out.Write((byte)0); // strike out
        _out.Write((byte)1); // DEFAULT_CHARSET: Unicode text
        _out.Write((byte)7); // OUT_TT_ONLY_PRECIS
        _out.Write((byte)0);
        _out.Write((byte)5); // CLEARTYPE_QUALITY
        _out.Write((byte)0);
        WriteName(face.Name, 32);
        WriteName(face.Name, 64); // elfFullName
        WriteName("", 32); // elfStyle
        _out.Write(new byte[16 + 4 + 4 + 10 + 2]); // version, style size, match, reserved, vendor, culture, PANOSE, padding
        End(at);
    }

    private void WriteName(string name, int characters)
    {
        var buffer = new byte[characters * 2];
        var length = Math.Min(name.Length, characters - 1);
        Encoding.Unicode.GetBytes(name.AsSpan(0, length), buffer);
        _out.Write(buffer);
    }

    // ───────────────────────── File ─────────────────────────

    public override byte[] ToArray()
    {
        while (_saved.Count > 0)
            Restore();
        var eof = Begin(14); // EMR_EOF
        _out.Write(0u); // no palette
        _out.Write(16u);
        _out.Write(20u);
        End(eof);

        const int HeaderSize = 108;
        var records = Records + 1;
        var total = HeaderSize + _body.Length;
        var output = new byte[total];
        var header = new BinaryWriter(new MemoryStream(output));
        header.Write(1u); // EMR_HEADER
        header.Write((uint)HeaderSize);
        // rclBounds in device units, rclFrame in 0.01 mm (right and bottom included).
        var (right, bottom) = (Math.Max(1, D(Width)), Math.Max(1, D(Height)));
        header.Write(0);
        header.Write(0);
        header.Write(right - 1);
        header.Write(bottom - 1);
        var (frameRight, frameBottom) = (Math.Max(1, (int)Math.Round(Width * 2540 / 72.0)), Math.Max(1, (int)Math.Round(Height * 2540 / 72.0)));
        header.Write(0);
        header.Write(0);
        header.Write(frameRight - 1);
        header.Write(frameBottom - 1);
        header.Write(EmfHeader.Signature);
        header.Write(0x00010000u); // version
        header.Write((uint)total);
        header.Write((uint)records);
        header.Write((ushort)Math.Max(1, _nextHandle)); // the handle table: entry 0 and every handle used
        header.Write((ushort)0);
        header.Write(0u); // no description
        header.Write(0u);
        header.Write(0u); // no palette
        header.Write(DevicePixels);
        header.Write(DevicePixels);
        header.Write(DeviceMillimeters);
        header.Write(DeviceMillimeters);
        header.Write(0u); // no pixel format
        header.Write(0u);
        header.Write(0u); // no OpenGL
        header.Write(DeviceMillimeters * 1000);
        header.Write(DeviceMillimeters * 1000);
        _body.Position = 0;
        _body.CopyTo(new MemoryStream(output, HeaderSize, (int)_body.Length));
        return output;
    }
}
