// Function-based (type 1) and mesh (types 4–7) shadings for editable slides (PdfSlideDrawing, PdfDocumentReader.Slides),
// which PdfPig parses but doesn't paint (PDF 32000-1 8.7.4.5.2 and 8.7.4.5.5–8.7.4.5.8):
//
// - A function-based shading is sampled over its Domain (at most MaxFunctionSide samples a side, about two a point as
//   shown), a mesh is decoded (free-form and lattice Gouraud triangles; Coons and tensor-product patches cut into
//   triangles, each corner's colour, or parameter t through the Function, interpolated) and drawn with Skia into a
//   picture of the part of the page it covers. The picture is the shading wherever it has to be drawn; nothing is
//   painted outside it.
// - The colours are then compared with a linear and a radial gradient (16 bands along the direction the colour changes
//   most, or around the best of a few centres): when every sample is within a few levels of one, the shading has a
//   look (PdfShadingLook) a DrawingML gradient can take, for shapes the shading fully covers.
//
// Limits (PdfDrawingBudget, shared by the page's pattern cells and masks): MaxPictures shading pictures a page, sides of
// at most MaxSide pixels, MaxTriangles triangles and MaxSamples function samples a page; a mesh past them is drawn as far
// as it got. Data that ends early ends the mesh there; numbers that aren't finite leave the shading out.

using SkiaSharp;
using UglyToad.PdfPig.Graphics.Colors;

namespace Filee.Engines.Pdf;

/// <summary>A drawn shading: its picture and the matrix from its pixels to slide coordinates (nothing outside it).</summary>
internal sealed record PdfShadingPicture(SKImage Image, SKMatrix Space)
{
    /// <summary>A Skia shader painting the picture where it lies, transparent elsewhere.</summary>
    public SKShader Shader() => Image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, new SKSamplingOptions(SKFilterMode.Linear), Space);
}

/// <summary>
/// A gradient that looks like a shading, in slide coordinates: linear from <paramref name="Start"/> to
/// <paramref name="End"/>, or (<paramref name="Radial"/>) circles around <paramref name="Start"/> out to the distance of
/// <paramref name="End"/>; <paramref name="Colors"/> evenly along the way (the last beyond it).
/// </summary>
internal sealed record PdfShadingLook(bool Radial, SKPoint Start, SKPoint End, SKColor[] Colors, Func<SKPoint, bool> Paints)
{
    /// <summary>The colour a share <paramref name="s"/> of the way along (clamped to 0–1).</summary>
    public SKColor At(double s)
    {
        var at = Math.Clamp(double.IsFinite(s) ? s : 0, 0, 1) * (Colors.Length - 1);
        var low = Math.Min((int)at, Colors.Length - 2);
        var f = (float)(at - low);
        var (a, b) = (Colors[low], Colors[low + 1]);
        return new SKColor(Mix(a.Red, b.Red), Mix(a.Green, b.Green), Mix(a.Blue, b.Blue), Mix(a.Alpha, b.Alpha));
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * f);
    }

    /// <summary>Whether the shading paints all of <paramref name="box"/> (a grid of points in it, its corners included).</summary>
    public bool Covers(SKRect box)
    {
        const int Steps = 8;
        for (var i = 0; i <= Steps; i++)
        {
            for (var j = 0; j <= Steps; j++)
            {
                var point = new SKPoint(box.Left + box.Width * i / Steps, box.Top + box.Height * j / Steps);
                // Points on the very edge are moved in a little: the shading's own edge is anti-aliased.
                point = new SKPoint(Math.Clamp(point.X, box.Left + 0.25f, Math.Max(box.Left + 0.25f, box.Right - 0.25f)),
                    Math.Clamp(point.Y, box.Top + 0.25f, Math.Max(box.Top + 0.25f, box.Bottom - 0.25f)));
                if (!Paints(point))
                    return false;
            }
        }
        return true;
    }
}

internal static class PdfShadingPictures
{
    /// <summary>Shading pictures one page draws at most; later function and mesh shadings are left out.</summary>
    internal const int MaxPictures = 32;

    /// <summary>The largest side of a shading picture, in pixels.</summary>
    internal const int MaxSide = 1024;

    /// <summary>Triangles of meshes one page draws at most.</summary>
    internal const int MaxTriangles = 200_000;

    /// <summary>Function evaluations of function-based shadings one page makes at most.</summary>
    internal const int MaxSamples = 1_000_000;

    /// <summary>Samples a side of a function-based shading at most.</summary>
    private const int MaxFunctionSide = 256;

    /// <summary>Pixels per point of a mesh's picture (less for a large one).</summary>
    private const float PixelsPerPoint = 2;

    /// <summary>Largest difference (of 255) of one sample from the gradient that stands for the shading.</summary>
    private const double MaxError = 10;

    /// <summary>Largest root mean square difference (of 255) from that gradient.</summary>
    private const double MaxMeanError = 4;

    /// <summary>
    /// <paramref name="shading"/> (function-based or a mesh) drawn under <paramref name="space"/> (its space to slide
    /// coordinates) over the part of <paramref name="area"/> it covers, and the gradient it looks like; nulls when it
    /// is another type, paints nothing there or is past the budget.
    /// </summary>
    public static (PdfShadingPicture? Picture, PdfShadingLook? Look) Draw(Shading shading, SKMatrix space, SKRect area, PdfDrawingBudget budget)
    {
        if (shading is not (FunctionBasedShading or FreeFormGouraudShading or LatticeFormGouraudShading or CoonsPatchMeshesShading or TensorProductPatchMeshesShading)
            || budget.ShadingPictures >= MaxPictures || !Finite(space))
            return (null, null);
        try
        {
            var result = shading is FunctionBasedShading function ? Function(function, space, area, budget) : Mesh(shading, space, area, budget);
            if (result.Picture is not null)
                budget.ShadingPictures++;
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException or NotSupportedException
                                       or InvalidCastException or NullReferenceException or OverflowException)
        {
            return (null, null); // a shading PdfPig or Filee can't evaluate is left out
        }
    }

    private static bool Finite(SKMatrix m) =>
        PdfShadings.Finite(m.ScaleX, m.SkewX, m.TransX, m.SkewY, m.ScaleY, m.TransY) && Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY) > 1e-12;

    /// <summary>A colour of the shading's space (after its Function, if any), as sRGB.</summary>
    private static SKColor Rgb(Shading shading, ReadOnlySpan<double> values)
    {
        double[] components = shading.Functions is { Length: > 0 } ? shading.Eval([.. values]) : [.. values];
        shading.ColorSpace.GetRgb(components, out var r, out var g, out var b);
        return new SKColor(PdfShadings.Byte(r), PdfShadings.Byte(g), PdfShadings.Byte(b));
    }

    // ───────────────────────── Function-based ─────────────────────────

    private static (PdfShadingPicture? Picture, PdfShadingLook? Look) Function(FunctionBasedShading shading, SKMatrix space, SKRect area, PdfDrawingBudget budget)
    {
        var (x0, x1, y0, y1) = shading.Domain is [var a, var b, var c, var d, ..] ? (a, b, c, d) : (0.0, 1.0, 0.0, 1.0);
        if (!PdfShadings.Finite(x0, x1, y0, y1) || x1 <= x0 || y1 <= y0)
            return (null, null);
        var m = shading.Matrix;
        var toShading = new SKMatrix((float)m.A, (float)m.C, (float)m.E, (float)m.B, (float)m.D, (float)m.F, 0, 0, 1);
        var domainToSlide = toShading.PostConcat(space);
        if (!Finite(domainToSlide))
            return (null, null);
        var domain = new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
        var shown = domainToSlide.MapRect(domain);
        if (!shown.IntersectsWith(area))
            return (null, null);
        // About two samples a point along each side of the domain as shown, within the limits.
        var across = domainToSlide.MapVector((float)(x1 - x0), 0).Length;
        var up = domainToSlide.MapVector(0, (float)(y1 - y0)).Length;
        var columns = Math.Clamp((int)Math.Ceiling(across * PixelsPerPoint), 2, MaxFunctionSide);
        var rows = Math.Clamp((int)Math.Ceiling(up * PixelsPerPoint), 2, MaxFunctionSide);
        while ((long)columns * rows > MaxSamples - budget.Samples && (columns > 2 || rows > 2))
            (columns, rows) = (Math.Max(2, columns / 2), Math.Max(2, rows / 2));
        if ((long)columns * rows > MaxSamples - budget.Samples)
            return (null, null);
        budget.Samples += columns * rows;

        using var bitmap = new SKBitmap(new SKImageInfo(columns, rows, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = bitmap.GetPixelSpan();
        Span<double> point = stackalloc double[2];
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < columns; i++)
            {
                point[0] = x0 + (i + 0.5) * (x1 - x0) / columns;
                point[1] = y0 + (j + 0.5) * (y1 - y0) / rows;
                var color = Rgb(shading, point);
                var at = (j * columns + i) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (color.Red, color.Green, color.Blue, 255);
            }
        }
        var toDomain = new SKMatrix((float)((x1 - x0) / columns), 0, (float)x0, 0, (float)((y1 - y0) / rows), (float)y0, 0, 0, 1);
        var samplesToSlide = toDomain.PostConcat(domainToSlide);
        var samples = new List<(SKPoint, SKColor)>();
        var (stepX, stepY) = (Math.Max(1, columns / 24), Math.Max(1, rows / 24));
        for (var j = 0; j < rows; j += stepY)
        {
            for (var i = 0; i < columns; i += stepX)
                samples.Add((samplesToSlide.MapPoint(i + 0.5f, j + 0.5f), bitmap.GetPixel(i, j)));
        }

        // The samples smoothed over the domain as shown, cut sharply at its edges, nothing outside it.
        using var quad = new SKPath();
        quad.AddPoly([domainToSlide.MapPoint((float)x0, (float)y0), domainToSlide.MapPoint((float)x1, (float)y0),
            domainToSlide.MapPoint((float)x1, (float)y1), domainToSlide.MapPoint((float)x0, (float)y1)]);
        using var image = SKImage.FromBitmap(bitmap);
        using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear), samplesToSlide);
        var (picture, paints) = Raster(SKRect.Intersect(quad.Bounds, area), (canvas, paint) =>
        {
            paint.Shader = shader;
            paint.IsAntialias = true;
            canvas.DrawPath(quad, paint);
        });
        return picture is null ? (null, null) : (picture, Fit(samples, paints!));
    }

    /// <summary>
    /// A picture of what <paramref name="draw"/> paints (in slide coordinates) over <paramref name="bounds"/>, about
    /// PixelsPerPoint pixels a point and at most MaxSide a side, and whether it paints a slide point fully; nulls
    /// for an empty area.
    /// </summary>
    private static (PdfShadingPicture? Picture, Func<SKPoint, bool>? Paints) Raster(SKRect bounds, Action<SKCanvas, SKPaint> draw)
    {
        if (bounds.Width < 0.5f || bounds.Height < 0.5f)
            return (null, null);
        var scale = Math.Min(PixelsPerPoint, MaxSide / Math.Max(bounds.Width, bounds.Height));
        var (width, height) = ((int)Math.Max(1, Math.Ceiling(bounds.Width * scale)), (int)Math.Max(1, Math.Ceiling(bounds.Height * scale)));
        var toPixels = SKMatrix.CreateTranslation(-bounds.Left, -bounds.Top).PostConcat(SKMatrix.CreateScale(scale, scale));
        if (!toPixels.TryInvert(out var toSlide))
            return (null, null);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(toPixels);
            using var paint = new SKPaint { Color = SKColors.White };
            draw(canvas, paint);
        }
        var alpha = new byte[width * height];
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = pixels[i * 4 + 3];
        bool Paints(SKPoint p)
        {
            var q = toPixels.MapPoint(p);
            var (x, y) = ((int)Math.Floor(q.X), (int)Math.Floor(q.Y));
            return x >= 0 && y >= 0 && x < width && y < height && alpha[y * width + x] >= 250;
        }
        return (new PdfShadingPicture(SKImage.FromBitmap(bitmap), toSlide), Paints);
    }

    // ───────────────────────── Meshes ─────────────────────────

    /// <summary>A mesh's triangles: three corners (shading space) and their colours each.</summary>
    private sealed class Triangles
    {
        public List<SKPoint> Points { get; } = [];
        public List<SKColor> Colors { get; } = [];
        public int Count => Points.Count / 3;

        public void Add(SKPoint a, SKColor ca, SKPoint b, SKColor cb, SKPoint c, SKColor cc)
        {
            Points.Add(a);
            Points.Add(b);
            Points.Add(c);
            Colors.Add(ca);
            Colors.Add(cb);
            Colors.Add(cc);
        }
    }

    /// <summary>Reads a mesh's stream bit by bit, most significant bit first.</summary>
    private sealed class Bits(ReadOnlyMemory<byte> data)
    {
        private long _bit;

        public bool Has(int bits) => _bit + bits <= data.Length * 8L;

        public ulong Read(int bits)
        {
            ulong value = 0;
            var span = data.Span;
            for (var i = 0; i < bits; i++, _bit++)
                value = value << 1 | (uint)((span[(int)(_bit >> 3)] >> (7 - (int)(_bit & 7))) & 1);
            return value;
        }

        public void Align() => _bit = (_bit + 7) & ~7L;
    }

    /// <summary>The layout of a mesh's vertices: bits of each field and the Decode ranges.</summary>
    private sealed record Layout(int Coordinate, int Component, int Flag, int Components, double[] Decode)
    {
        public int VertexBits => 2 * Coordinate + Components * Component;

        public SKPoint Point(Bits bits) => new((float)Value(bits.Read(Coordinate), Coordinate, Decode[0], Decode[1]),
            (float)Value(bits.Read(Coordinate), Coordinate, Decode[2], Decode[3]));

        public double[] Values(Bits bits)
        {
            var values = new double[Components];
            for (var i = 0; i < Components; i++)
                values[i] = Value(bits.Read(Component), Component, Decode[4 + 2 * i], Decode[5 + 2 * i]);
            return values;
        }

        private static double Value(ulong raw, int bits, double min, double max) => min + raw * (max - min) / ((1UL << bits) - 1);
    }

    private static (PdfShadingPicture? Picture, PdfShadingLook? Look) Mesh(Shading shading, SKMatrix space, SKRect area, PdfDrawingBudget budget)
    {
        var (coordinate, component, flag, decode, data) = shading switch
        {
            FreeFormGouraudShading s => (s.BitsPerCoordinate, s.BitsPerComponent, s.BitsPerFlag, s.Decode, s.Data),
            LatticeFormGouraudShading s => (s.BitsPerCoordinate, s.BitsPerComponent, 0, s.Decode, s.Data),
            CoonsPatchMeshesShading s => (s.BitsPerCoordinate, s.BitsPerComponent, s.BitsPerFlag, s.Decode, s.Data),
            TensorProductPatchMeshesShading s => (s.BitsPerCoordinate, s.BitsPerComponent, s.BitsPerFlag, s.Decode, s.Data),
            _ => (0, 0, 0, Array.Empty<double>(), ReadOnlyMemory<byte>.Empty),
        };
        var components = shading.Functions is { Length: > 0 } ? 1 : shading.ColorSpace.NumberOfColorComponents;
        if (coordinate is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32) || component is not (1 or 2 or 4 or 8 or 12 or 16)
            || (shading is not LatticeFormGouraudShading && flag is not (2 or 4 or 8)) || components is < 1 or > 32
            || decode is null || decode.Length < 4 + 2 * components || !PdfShadings.Finite(decode))
            return (null, null);
        var layout = new Layout(coordinate, component, flag, components, decode);
        var bits = new Bits(data);
        var triangles = new Triangles();
        var room = MaxTriangles - budget.Triangles;
        switch (shading)
        {
            case FreeFormGouraudShading:
                FreeForm(shading, layout, bits, triangles, room);
                break;
            case LatticeFormGouraudShading lattice:
                Lattice(shading, layout, bits, triangles, room, lattice.VerticesPerRow);
                break;
            default:
                Patches(shading, layout, bits, triangles, room, shading is TensorProductPatchMeshesShading, space);
                break;
        }
        budget.Triangles += triangles.Count;
        if (triangles.Count == 0)
            return (null, null);

        var points = triangles.Points.Select(space.MapPoint).ToArray();
        if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y)))
            return (null, null);
        var bounds = SKRect.Create(points[0], SKSize.Empty);
        foreach (var p in points)
            bounds = SKRect.Union(bounds, SKRect.Create(p, SKSize.Empty));
        bounds.Intersect(area);
        var colors = triangles.Colors.ToArray();
        var (picture, paints) = Raster(bounds, (canvas, paint) => canvas.DrawVertices(SKVertexMode.Triangles, points, colors, paint));
        if (picture is null)
            return (null, null);

        // Colours to compare with gradients: a grid of the picture's fully painted pixels.
        using var bitmap = SKBitmap.FromImage(picture.Image);
        var samples = new List<(SKPoint, SKColor)>();
        var (stepX, stepY) = (Math.Max(1, bitmap.Width / 24), Math.Max(1, bitmap.Height / 24));
        for (var y = stepY / 2; y < bitmap.Height; y += stepY)
        {
            for (var x = stepX / 2; x < bitmap.Width; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha == 255)
                    samples.Add((picture.Space.MapPoint(x + 0.5f, y + 0.5f), pixel));
            }
        }
        return (picture, Fit(samples, paints!));
    }

    /// <summary>Type 4: vertices with a flag each; 0 starts a triangle, 1 and 2 go on from the last one's edges.</summary>
    private static void FreeForm(Shading shading, Layout layout, Bits bits, Triangles triangles, int room)
    {
        var (points, colors) = (new List<SKPoint>(), new List<SKColor>());
        var left = 0;
        while (triangles.Count < room && bits.Has(layout.Flag + layout.VertexBits))
        {
            var flag = (int)bits.Read(layout.Flag);
            var point = layout.Point(bits);
            var color = Rgb(shading, layout.Values(bits));
            bits.Align();
            if (left == 0)
            {
                switch (flag)
                {
                    case 0:
                        left = 3;
                        points.Clear();
                        colors.Clear();
                        break;
                    case 1 when points.Count >= 3:
                        (points, colors) = ([points[^2], points[^1]], [colors[^2], colors[^1]]);
                        left = 1;
                        break;
                    case 2 when points.Count >= 3:
                        (points, colors) = ([points[^3], points[^1]], [colors[^3], colors[^1]]);
                        left = 1;
                        break;
                    default:
                        return; // a flag that doesn't fit: the mesh ends here
                }
            }
            points.Add(point);
            colors.Add(color);
            if (--left == 0)
                triangles.Add(points[^3], colors[^3], points[^2], colors[^2], points[^1], colors[^1]);
        }
    }

    /// <summary>Type 5: rows of <paramref name="perRow"/> vertices, each cell two triangles.</summary>
    private static void Lattice(Shading shading, Layout layout, Bits bits, Triangles triangles, int room, int perRow)
    {
        if (perRow < 2)
            return;
        var previous = new List<(SKPoint P, SKColor C)>();
        var row = new List<(SKPoint P, SKColor C)>();
        while (triangles.Count + 2 * perRow <= room && bits.Has(layout.VertexBits))
        {
            row.Add((layout.Point(bits), Rgb(shading, layout.Values(bits))));
            if (row.Count < perRow)
                continue;
            if (previous.Count == perRow)
            {
                for (var i = 0; i + 1 < perRow; i++)
                {
                    var (a, b, c, d) = (previous[i], previous[i + 1], row[i], row[i + 1]);
                    triangles.Add(a.P, a.C, b.P, b.C, c.P, c.C);
                    triangles.Add(b.P, b.C, d.P, d.C, c.P, c.C);
                }
            }
            previous = [.. row];
            row.Clear();
        }
    }

    /// <summary>Types 6 and 7: patches of 12 (Coons) or 16 (tensor) control points, cut into triangles.</summary>
    private static void Patches(Shading shading, Layout layout, Bits bits, Triangles triangles, int room, bool tensor, SKMatrix space)
    {
        var boundary = new SKPoint[12];
        var inner = new SKPoint[4];
        var corners = new double[4][];
        var started = false;
        while (triangles.Count < room && bits.Has(layout.Flag))
        {
            var flag = (int)bits.Read(layout.Flag);
            if (flag is < 0 or > 3 || (flag != 0 && !started))
                return;
            var newPoints = (flag == 0 ? 12 : 8) + (tensor ? 4 : 0);
            var newColors = flag == 0 ? 4 : 2;
            if (!bits.Has(newPoints * 2 * layout.Coordinate + newColors * layout.Components * layout.Component))
                return;
            if (flag != 0)
            {
                // The new patch's first edge and corner colours are an edge of the last one.
                var (from, c0, c1) = flag switch { 1 => (3, 1, 2), 2 => (6, 2, 3), _ => (9, 3, 0) };
                var shared = Enumerable.Range(0, 4).Select(k => boundary[(from + k) % 12]).ToArray();
                (corners[0], corners[1]) = (corners[c0], corners[c1]);
                shared.CopyTo(boundary, 0);
            }
            for (var k = flag == 0 ? 0 : 4; k < 12; k++)
                boundary[k] = layout.Point(bits);
            if (tensor)
            {
                for (var k = 0; k < 4; k++)
                    inner[k] = layout.Point(bits);
            }
            for (var k = flag == 0 ? 0 : 2; k < 4; k++)
                corners[k] = layout.Values(bits);
            bits.Align();
            started = true;

            var p = Tensor(boundary, tensor ? inner : null);
            // About one cut every four pixels of the patch's extent as shown, 2 to 16 a side.
            var extent = 0f;
            for (var k = 0; k < 12; k++)
                extent = Math.Max(extent, (space.MapPoint(boundary[k]) - space.MapPoint(boundary[(k + 1) % 12])).Length * 3);
            var steps = Math.Clamp((int)Math.Ceiling(extent * PixelsPerPoint / 4), 2, 16);
            if (triangles.Count + 2 * steps * steps > room)
                steps = Math.Max(1, (int)Math.Sqrt(Math.Max(0, room - triangles.Count) / 2.0));
            if (steps < 1 || triangles.Count + 2 * steps * steps > room)
                return;
            var grid = new (SKPoint P, SKColor C)[steps + 1, steps + 1];
            var values = new double[corners[0].Length];
            for (var i = 0; i <= steps; i++)
            {
                for (var j = 0; j <= steps; j++)
                {
                    double u = i / (double)steps, v = j / (double)steps;
                    // Corners: c00 at (0, 0), c03 at (0, 1), c33 at (1, 1), c30 at (1, 0).
                    for (var k = 0; k < values.Length && k < corners[1].Length && k < corners[2].Length && k < corners[3].Length; k++)
                        values[k] = (1 - u) * (1 - v) * corners[0][k] + (1 - u) * v * corners[1][k] + u * v * corners[2][k] + u * (1 - v) * corners[3][k];
                    grid[i, j] = (Surface(p, u, v), Rgb(shading, values));
                }
            }
            for (var i = 0; i < steps; i++)
            {
                for (var j = 0; j < steps; j++)
                {
                    var (a, b, c, d) = (grid[i, j], grid[i + 1, j], grid[i, j + 1], grid[i + 1, j + 1]);
                    triangles.Add(a.P, a.C, b.P, b.C, c.P, c.C);
                    triangles.Add(b.P, b.C, d.P, d.C, c.P, c.C);
                }
            }
        }
    }

    /// <summary>
    /// The 4 × 4 control points p[i, j] (i along u, j along v) of a patch from its boundary (p00 p01 p02 p03 p13 p23 p33
    /// p32 p31 p30 p20 p10) and, for a tensor patch, its inner points (p11 p12 p22 p21); a Coons patch's inner points
    /// follow from its boundary (PDF 32000-1 8.7.4.5.8).
    /// </summary>
    private static SKPoint[,] Tensor(SKPoint[] b, SKPoint[]? inner)
    {
        var p = new SKPoint[4, 4];
        (p[0, 0], p[0, 1], p[0, 2], p[0, 3], p[1, 3], p[2, 3]) = (b[0], b[1], b[2], b[3], b[4], b[5]);
        (p[3, 3], p[3, 2], p[3, 1], p[3, 0], p[2, 0], p[1, 0]) = (b[6], b[7], b[8], b[9], b[10], b[11]);
        if (inner is not null)
        {
            (p[1, 1], p[1, 2], p[2, 2], p[2, 1]) = (inner[0], inner[1], inner[2], inner[3]);
            return p;
        }
        SKPoint Sum(params (float W, SKPoint P)[] terms) =>
            new(terms.Sum(t => t.W * t.P.X) / 9, terms.Sum(t => t.W * t.P.Y) / 9);
        p[1, 1] = Sum((-4, p[0, 0]), (6, p[0, 1]), (6, p[1, 0]), (-2, p[0, 3]), (-2, p[3, 0]), (3, p[3, 1]), (3, p[1, 3]), (-1, p[3, 3]));
        p[1, 2] = Sum((-4, p[0, 3]), (6, p[0, 2]), (6, p[1, 3]), (-2, p[0, 0]), (-2, p[3, 3]), (3, p[3, 2]), (3, p[1, 0]), (-1, p[3, 0]));
        p[2, 1] = Sum((-4, p[3, 0]), (6, p[3, 1]), (6, p[2, 0]), (-2, p[3, 3]), (-2, p[0, 0]), (3, p[0, 1]), (3, p[2, 3]), (-1, p[0, 3]));
        p[2, 2] = Sum((-4, p[3, 3]), (6, p[3, 2]), (6, p[2, 3]), (-2, p[3, 0]), (-2, p[0, 3]), (3, p[0, 2]), (3, p[2, 0]), (-1, p[0, 0]));
        return p;
    }

    private static SKPoint Surface(SKPoint[,] p, double u, double v)
    {
        Span<double> bu = [Bernstein(0, u), Bernstein(1, u), Bernstein(2, u), Bernstein(3, u)];
        Span<double> bv = [Bernstein(0, v), Bernstein(1, v), Bernstein(2, v), Bernstein(3, v)];
        double x = 0, y = 0;
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                var w = bu[i] * bv[j];
                x += w * p[i, j].X;
                y += w * p[i, j].Y;
            }
        }
        return new SKPoint((float)x, (float)y);

        static double Bernstein(int i, double t) => i switch
        {
            0 => (1 - t) * (1 - t) * (1 - t),
            1 => 3 * t * (1 - t) * (1 - t),
            2 => 3 * t * t * (1 - t),
            _ => t * t * t,
        };
    }

    // ───────────────────────── Gradients that look the same ─────────────────────────

    /// <summary>
    /// A linear or radial gradient within <see cref="MaxError"/> of every sample (slide point, colour), the linear one
    /// first; null when neither is close enough.
    /// </summary>
    internal static PdfShadingLook? Fit(List<(SKPoint Point, SKColor Color)> samples, Func<SKPoint, bool> paints)
    {
        if (samples.Count < 4)
            return null;
        double cx = samples.Average(s => s.Point.X), cy = samples.Average(s => s.Point.Y);
        // The direction the colour changes most: least squares of each channel over x and y.
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var (point, _) in samples)
        {
            double dx = point.X - cx, dy = point.Y - cy;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }
        var determinant = sxx * syy - sxy * sxy;
        double mxx = 0, mxy = 0, myy = 0;
        if (Math.Abs(determinant) > 1e-9)
        {
            foreach (var channel in new Func<SKColor, double>[] { c => c.Red, c => c.Green, c => c.Blue })
            {
                double mean = samples.Average(s => channel(s.Color)), sxc = 0, syc = 0;
                foreach (var (point, color) in samples)
                {
                    sxc += (point.X - cx) * (channel(color) - mean);
                    syc += (point.Y - cy) * (channel(color) - mean);
                }
                var gx = (syy * sxc - sxy * syc) / determinant;
                var gy = (sxx * syc - sxy * sxc) / determinant;
                mxx += gx * gx;
                mxy += gx * gy;
                myy += gy * gy;
            }
        }
        // The principal eigenvector of the gradients' outer products; any direction for a flat colour.
        var angle = 0.5 * Math.Atan2(2 * mxy, mxx - myy);
        var direction = new SKPoint((float)Math.Cos(angle), (float)Math.Sin(angle));
        var centre = new SKPoint((float)cx, (float)cy);
        if (Bands(samples, p => Dot(p - centre, direction)) is { } linear)
        {
            var start = centre + Scale(direction, (float)linear.Min);
            var end = centre + Scale(direction, (float)linear.Max);
            return new PdfShadingLook(false, start, end, linear.Colors, paints);
        }
        // Circles: the best of a few centres over the samples' box (and a little beyond).
        var box = new SKRect(samples.Min(s => s.Point.X), samples.Min(s => s.Point.Y), samples.Max(s => s.Point.X), samples.Max(s => s.Point.Y));
        (double Error, SKPoint Centre, (double Min, double Max, SKColor[] Colors) Bands)? best = null;
        const int Grid = 6;
        for (var i = -1; i <= Grid + 1; i++)
        {
            for (var j = -1; j <= Grid + 1; j++)
            {
                var candidate = new SKPoint(box.Left + box.Width * i / Grid, box.Top + box.Height * j / Grid);
                if (Bands(samples, p => (p - candidate).Length, fromZero: true) is { } bands && (best is null || bands.Error < best.Value.Error))
                    best = (bands.Error, candidate, (bands.Min, bands.Max, bands.Colors));
            }
        }
        if (best is not { } found || found.Bands.Max <= 0)
            return null;
        return new PdfShadingLook(true, found.Centre, found.Centre + new SKPoint((float)found.Bands.Max, 0), found.Bands.Colors, paints);
    }

    private static double Dot(SKPoint a, SKPoint b) => a.X * b.X + a.Y * b.Y;

    private static SKPoint Scale(SKPoint p, float factor) => new(p.X * factor, p.Y * factor);

    /// <summary>
    /// The samples' colours in 16 bands of <paramref name="along"/> (from its least value, or from 0), each band's
    /// colour linear between band centres; null when a sample is further from that than the limits allow.
    /// </summary>
    private static (double Min, double Max, SKColor[] Colors, double Error)? Bands(List<(SKPoint Point, SKColor Color)> samples, Func<SKPoint, double> along,
        bool fromZero = false)
    {
        const int Count = 16;
        var values = samples.Select(s => along(s.Point)).ToArray();
        var min = fromZero ? 0 : values.Min();
        var max = values.Max();
        var span = max - min;
        var sums = new double[Count + 1, 3];
        var counts = new int[Count + 1];
        int Band(double value) => span < 1e-6 ? 0 : (int)Math.Clamp(Math.Round((value - min) / span * Count), 0, Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var band = Band(values[i]);
            sums[band, 0] += samples[i].Color.Red;
            sums[band, 1] += samples[i].Color.Green;
            sums[band, 2] += samples[i].Color.Blue;
            counts[band]++;
        }
        var colors = new double[Count + 1][];
        for (var b = 0; b <= Count; b++)
            colors[b] = counts[b] > 0 ? [sums[b, 0] / counts[b], sums[b, 1] / counts[b], sums[b, 2] / counts[b]] : null!;
        // Empty bands take their neighbours' colours.
        for (var b = 0; b <= Count; b++)
        {
            if (colors[b] is not null)
                continue;
            int before = b - 1, after = b + 1;
            while (before >= 0 && colors[before] is null)
                before--;
            while (after <= Count && colors[after] is null)
                after++;
            colors[b] = before >= 0 && after <= Count
                ? [.. Enumerable.Range(0, 3).Select(k => colors[before][k] + (colors[after][k] - colors[before][k]) * (b - before) / (after - before))]
                : before >= 0 ? colors[before] : colors[after];
        }
        double worst = 0, squares = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var at = span < 1e-6 ? 0 : (values[i] - min) / span * Count;
            var low = Math.Min((int)at, Count - 1);
            var f = at - low;
            var c = samples[i].Color;
            ReadOnlySpan<double> actual = [c.Red, c.Green, c.Blue];
            for (var k = 0; k < 3; k++)
            {
                var expected = colors[low][k] + (colors[low + 1][k] - colors[low][k]) * f;
                var error = Math.Abs(expected - actual[k]);
                worst = Math.Max(worst, error);
                squares += error * error;
            }
        }
        var mean = Math.Sqrt(squares / (3.0 * samples.Count));
        if (worst > MaxError || mean > MaxMeanError)
            return null;
        var result = colors.Select(c => new SKColor((byte)Math.Round(Math.Clamp(c[0], 0, 255)), (byte)Math.Round(Math.Clamp(c[1], 0, 255)),
            (byte)Math.Round(Math.Clamp(c[2], 0, 255)))).ToArray();
        return (min, span < 1e-6 ? min + 1 : max, result, mean + worst / 100);
    }
}
