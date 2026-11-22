// The slide renderer's effects and 3D (SlideEffects, SlideThreeD), drawn about a shape's outlines with Skia's filters:
//
// - Shadow: the shape's silhouette in the shadow colour, blurred and moved by the offset on the page (it doesn't turn
//   with the shape), under the shape.
// - Glow: the silhouette grown by part of the radius and blurred over the rest, in the glow colour, under the shape.
// - Soft edges: the shape faded towards its outline: its silhouette shrunk by half the radius and blurred, as a mask.
// - Reflection: the shape mirrored below its box (after the gap), faded from the start to the end opacity over its
//   share of the height, and blurred.
// - 3D: the front face turned in space about the box's centre and projected (parallel, oblique or in perspective), so
//   fills, pictures and lines turn with it; an extrusion as the sides between the front outline and the same outline
//   the depth behind, and the back face, flat-shaded under a light from the upper left and drawn back to front. Text
//   turns with the face's flat part. Bevels, materials and lighting rigs are not drawn.

using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Slides;

internal sealed partial class SlideRenderer
{
    /// <summary>Points of one outline drawn as an extrusion's edge at most (curves are cut into straight pieces).</summary>
    private const int MaxExtrusionPoints = 8192;

    /// <summary>Straight pieces per curve of an outline drawn as an extrusion's edge.</summary>
    private const int CurvePieces = 8;

    /// <summary>The viewer's distance in perspective, in sizes of the shape's larger side.</summary>
    private const float ViewerDistance = 3;

    /// <summary>The light of extrusions: from the upper left, in front (y down, z towards the viewer).</summary>
    private static readonly (float X, float Y, float Z) Light = Normalize(-0.4f, -0.6f, 0.7f);

    /// <summary>
    /// The front face of a shape turned in space, mapping the box's own coordinates onto the flat box: null for a flat
    /// shape.
    /// </summary>
    internal static SKMatrix? Front(SlideShape shape)
    {
        if (shape.ThreeD is not { IsFlat: false } three)
            return null;
        var (w, h) = (shape.Bounds.Width, shape.Bounds.Height);
        var r = Rotation(three);
        SKMatrix face;
        if (three.Perspective)
        {
            var d = ViewerDistance * Math.Max(1, Math.Max(w, h));
            face = new SKMatrix(r[0], r[1], 0, r[3], r[4], 0, -r[6] / d, -r[7] / d, 1);
        }
        else
        {
            var (sx, sy) = Skew(three);
            face = new SKMatrix(r[0] + sx * r[6], r[1] + sx * r[7], 0, r[3] + sy * r[6], r[4] + sy * r[7], 0, 0, 0, 1);
        }
        return SKMatrix.CreateTranslation(w / 2, h / 2).PreConcat(face).PreConcat(SKMatrix.CreateTranslation(-w / 2, -h / 2));
    }

    /// <summary>The flat part of <paramref name="front"/> (text is laid out flat and turned with it).</summary>
    private static SKMatrix Flat(SKMatrix front) => new(front.ScaleX, front.SkewX, front.TransX, front.SkewY, front.ScaleY, front.TransY, 0, 0, 1);

    /// <summary>The turn of a 3D shape, row-major 3 × 3: about z first, then y, then x (ODF's order).</summary>
    private static float[] Rotation(SlideThreeD three)
    {
        static float Rad(float degrees) => float.IsFinite(degrees) ? degrees * MathF.PI / 180 : 0;
        var (a, b, c) = (Rad(three.RotationX), Rad(three.RotationY), Rad(three.RotationZ));
        // x: y' = y cos a − z sin a, z' = y sin a + z cos a (the top goes away for a > 0, y being down).
        float[] rx = [1, 0, 0, 0, MathF.Cos(a), -MathF.Sin(a), 0, MathF.Sin(a), MathF.Cos(a)];
        // y: x' = x cos b + z sin b, z' = −x sin b + z cos b (the right side goes away).
        float[] ry = [MathF.Cos(b), 0, MathF.Sin(b), 0, 1, 0, -MathF.Sin(b), 0, MathF.Cos(b)];
        // z: clockwise on the page.
        float[] rz = [MathF.Cos(c), -MathF.Sin(c), 0, MathF.Sin(c), MathF.Cos(c), 0, 0, 0, 1];
        return Multiply(rx, Multiply(ry, rz));
    }

    private static float[] Multiply(float[] m, float[] n)
    {
        var result = new float[9];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                result[i * 3 + j] = m[i * 3] * n[j] + m[i * 3 + 1] * n[3 + j] + m[i * 3 + 2] * n[6 + j];
        return result;
    }

    /// <summary>How far a point moves on the page per point of depth towards the viewer, in an oblique parallel view.</summary>
    private static (float X, float Y) Skew(SlideThreeD three)
    {
        if (three.Perspective || !(three.SkewAmount > 0) || !float.IsFinite(three.SkewAngle))
            return (0, 0);
        var amount = Math.Min(three.SkewAmount, 10);
        var angle = three.SkewAngle * MathF.PI / 180;
        // A point behind (z < 0) moves along the angle, counter-clockwise from the right with y up.
        return (-amount * MathF.Cos(angle), amount * MathF.Sin(angle));
    }

    private static (float X, float Y, float Z) Normalize(float x, float y, float z)
    {
        var length = MathF.Sqrt(x * x + y * y + z * z);
        return length > 0 ? (x / length, y / length, z / length) : (0, 0, 1);
    }

    /// <summary>The sides and back of an extruded shape, under its front face (drawn by <see cref="Shape"/> through <see cref="Front"/>).</summary>
    private void Extrusion(PageContent page, SlideShape shape, SKMatrix matrix)
    {
        if (shape.ThreeD is not { Depth: > 0 } three || !float.IsFinite(three.Depth))
            return;
        var outlines = Outlines(shape.Paths.Where(p => p.Fill != PathFill.None || p.Stroke).Select(p => p.Path));
        if (outlines.Count == 0)
            return;
        var (w, h) = (shape.Bounds.Width, shape.Bounds.Height);
        var depth = Math.Min(three.Depth, 10 * Math.Max(1, Math.Max(w, h)));
        var r = Rotation(three);
        var (sx, sy) = Skew(three);
        var d = ViewerDistance * Math.Max(1, Math.Max(w, h));
        var (cx, cy) = (w / 2, h / 2);

        (SKPoint Point, float Z) Project(SKPoint p, float z)
        {
            var (x, y) = (p.X - cx, p.Y - cy);
            var X = r[0] * x + r[1] * y + r[2] * z;
            var Y = r[3] * x + r[4] * y + r[5] * z;
            var Z = r[6] * x + r[7] * y + r[8] * z;
            if (three.Perspective)
            {
                var s = d / Math.Max(d - Z, d * 0.05f);
                return (new SKPoint(cx + X * s, cy + Y * s), Z);
            }
            return (new SKPoint(cx + X + sx * Z, cy + Y + sy * Z), Z);
        }

        var baseColor = three.Color ?? (shape.Fill is { } fill ? SlidePaint.Representative(fill) : shape.Line is { } line ? SlidePaint.Representative(line.Fill) : SKColors.Gray);
        SKColor Shade(float x, float y, float z)
        {
            // The side's normal turned towards the viewer, lit by the light.
            var nx = r[0] * x + r[1] * y + r[2] * z;
            var ny = r[3] * x + r[4] * y + r[5] * z;
            var nz = r[6] * x + r[7] * y + r[8] * z;
            (nx, ny, nz) = Normalize(nx, ny, nz);
            if (nz < 0)
                (nx, ny, nz) = (-nx, -ny, -nz);
            var light = Math.Max(0, nx * Light.X + ny * Light.Y + nz * Light.Z);
            var k = 0.35f + 0.55f * light;
            return new SKColor((byte)(baseColor.Red * k), (byte)(baseColor.Green * k), (byte)(baseColor.Blue * k), baseColor.Alpha);
        }

        var faces = new List<(SKPoint[] Points, float Z, SKColor Color)>();
        foreach (var outline in outlines)
        {
            for (var i = 0; i < outline.Count; i++)
            {
                var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
                if (a == b)
                    continue;
                var (fa, za) = Project(a, 0);
                var (fb, zb) = Project(b, 0);
                var (bb, zbb) = Project(b, -depth);
                var (ba, zba) = Project(a, -depth);
                faces.Add(([fa, fb, bb, ba], (za + zb + zbb + zba) / 4, Shade(b.Y - a.Y, a.X - b.X, 0)));
            }
        }
        // Farthest first: the back face, then the sides by their depth.
        faces.Sort((x, y) => x.Z.CompareTo(y.Z));
        var backColor = Shade(0, 0, -1);
        page.Draw(canvas =>
        {
            canvas.Save();
            canvas.Concat(in matrix);
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, StrokeJoin = SKStrokeJoin.Round };
            using (var back = new SKPath { FillType = SKPathFillType.EvenOdd })
            {
                foreach (var outline in outlines)
                    back.AddPoly([.. outline.Select(p => Project(p, -depth).Point)]);
                paint.Color = backColor;
                canvas.DrawPath(back, paint);
            }
            foreach (var (points, _, color) in faces)
            {
                using var quad = new SKPath();
                quad.AddPoly(points);
                paint.Color = color;
                edge.Color = color;
                canvas.DrawPath(quad, paint);
                // The same colour round each side closes the hairline gaps between neighbours.
                canvas.DrawPath(quad, edge);
            }
            canvas.Restore();
        });
    }

    /// <summary>The contours of <paramref name="paths"/> as polygons, curves cut into straight pieces, up to <see cref="MaxExtrusionPoints"/> points in all.</summary>
    internal static List<List<SKPoint>> Outlines(IEnumerable<SKPath> paths)
    {
        var result = new List<List<SKPoint>>();
        var total = 0;
        var points = new SKPoint[4];
        foreach (var path in paths)
        {
            using var iterator = path.CreateRawIterator();
            List<SKPoint>? current = null;
            SKPathVerb verb;
            while ((verb = iterator.Next(points)) != SKPathVerb.Done && total < MaxExtrusionPoints)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        if (current is { Count: > 1 })
                            result.Add(current);
                        current = [points[0]];
                        total++;
                        break;
                    case SKPathVerb.Line:
                        current?.Add(points[1]);
                        total++;
                        break;
                    case SKPathVerb.Quad:
                        for (var i = 1; i <= CurvePieces && current is not null; i++)
                        {
                            var t = i / (float)CurvePieces;
                            var u = 1 - t;
                            current.Add(new SKPoint(u * u * points[0].X + 2 * u * t * points[1].X + t * t * points[2].X,
                                u * u * points[0].Y + 2 * u * t * points[1].Y + t * t * points[2].Y));
                        }
                        total += CurvePieces;
                        break;
                    case SKPathVerb.Conic:
                        {
                            var quads = SKPath.ConvertConicToQuads(points[0], points[1], points[2], iterator.ConicWeight(), 1);
                            for (var q = 0; q + 2 < quads.Length && current is not null; q += 2)
                            {
                                for (var i = 1; i <= CurvePieces; i++)
                                {
                                    var t = i / (float)CurvePieces;
                                    var u = 1 - t;
                                    current.Add(new SKPoint(u * u * quads[q].X + 2 * u * t * quads[q + 1].X + t * t * quads[q + 2].X,
                                        u * u * quads[q].Y + 2 * u * t * quads[q + 1].Y + t * t * quads[q + 2].Y));
                                }
                                total += CurvePieces;
                            }
                            break;
                        }
                    case SKPathVerb.Cubic:
                        for (var i = 1; i <= CurvePieces && current is not null; i++)
                        {
                            var t = i / (float)CurvePieces;
                            var u = 1 - t;
                            current.Add(new SKPoint(
                                u * u * u * points[0].X + 3 * u * u * t * points[1].X + 3 * u * t * t * points[2].X + t * t * t * points[3].X,
                                u * u * u * points[0].Y + 3 * u * u * t * points[1].Y + 3 * u * t * t * points[2].Y + t * t * t * points[3].Y));
                        }
                        total += CurvePieces;
                        break;
                }
            }
            if (current is { Count: > 1 })
                result.Add(current);
        }
        return result;
    }

    /// <summary>A shape's extrusion (sides and back) drawn, and the front face's projection returned; null for a flat shape.</summary>
    private SKMatrix? Extruded(PageContent page, SlideShape shape, SKMatrix matrix)
    {
        var front = Front(shape);
        if (front is not null)
            Extrusion(page, shape, matrix);
        return front;
    }

    // ───────────────────────── Effects ─────────────────────────

    /// <summary>A shape's outlines filled (with the veils of 3D-looking presets) and stroked through <paramref name="matrix"/>, with its effects.</summary>
    private void DrawOutlines(SKCanvas canvas, SlideShape shape, SKMatrix matrix, SKRect box)
    {
        // The outlines through a matrix; as a silhouette (opaque black) for the masks of effects.
        void Body(SKCanvas target, SKMatrix at, bool silhouette)
        {
            target.Save();
            target.Concat(in at);
            if (shape.Fill is { } fill)
            {
                using var black = silhouette ? new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill } : null;
                foreach (var path in shape.Paths.Where(p => p.Fill != PathFill.None))
                {
                    if (black is not null)
                    {
                        target.DrawPath(path.Path, black);
                        continue;
                    }
                    SlidePaint.Fill(target, path.Path, fill, box, _images);
                    Veil(target, path);
                }
            }
            if (shape.Line is { } line)
                Stroke(target, shape.Paths.Where(p => p.Stroke).Select(p => p.Path).ToList(), silhouette ? line with { Fill = new SolidFill(SKColors.Black) } : line, box);
            target.Restore();
        }
        if (shape.Effects is { IsNone: false } effects)
            WithEffects(canvas, effects, matrix, box.Size, Body);
        else
            Body(canvas, matrix, false);
    }

    /// <summary>Blur sigma for a blur radius in points.</summary>
    private static float Sigma(float radius) => float.IsFinite(radius) ? Math.Clamp(radius, 0, 500) / 2 : 0;

    /// <summary>
    /// Draws a shape's outlines (<paramref name="body"/>, through <paramref name="matrix"/>) with its effects: reflection,
    /// shadow and glow under it, the shape itself softened at its edges.
    /// </summary>
    private static void WithEffects(SKCanvas canvas, SlideEffects effects, SKMatrix matrix, SKSize size, Action<SKCanvas, SKMatrix, bool> body)
    {
        if (effects.Reflection is { } reflection)
            Reflect(canvas, reflection, matrix, size, body);
        if (effects.Shadow is { } shadow && shadow.Color.Alpha > 0)
        {
            var offset = (X: float.IsFinite(shadow.OffsetX) ? shadow.OffsetX : 0, Y: float.IsFinite(shadow.OffsetY) ? shadow.OffsetY : 0);
            using var filter = SKImageFilter.CreateDropShadowOnly(offset.X, offset.Y, Sigma(shadow.Blur), Sigma(shadow.Blur), shadow.Color);
            using var paint = new SKPaint { ImageFilter = filter };
            canvas.SaveLayer(paint);
            body(canvas, matrix, false);
            canvas.Restore();
        }
        if (effects.Glow is { Radius: > 0 } glow && float.IsFinite(glow.Radius) && glow.Color.Alpha > 0)
        {
            var radius = Math.Min(glow.Radius, 500);
            using var dilate = SKImageFilter.CreateDilate(radius / 2, radius / 2);
            using var blur = SKImageFilter.CreateBlur(radius / 3, radius / 3, dilate);
            using var tint = SKColorFilter.CreateBlendMode(glow.Color, SKBlendMode.SrcIn);
            using var filter = SKImageFilter.CreateColorFilter(tint, blur);
            using var paint = new SKPaint { ImageFilter = filter };
            canvas.SaveLayer(paint);
            body(canvas, matrix, true);
            canvas.Restore();
        }
        if (effects.SoftEdge > 0 && float.IsFinite(effects.SoftEdge))
        {
            var radius = Math.Min(effects.SoftEdge, 500);
            canvas.SaveLayer();
            body(canvas, matrix, false);
            using var erode = SKImageFilter.CreateErode(radius / 2, radius / 2);
            using var blur = SKImageFilter.CreateBlur(radius / 4, radius / 4, erode);
            using var mask = new SKPaint { BlendMode = SKBlendMode.DstIn, ImageFilter = blur };
            canvas.SaveLayer(mask);
            body(canvas, matrix, true);
            canvas.Restore();
            canvas.Restore();
            return;
        }
        body(canvas, matrix, false);
    }

    /// <summary>The shape mirrored under its box, faded out downwards and blurred.</summary>
    private static void Reflect(SKCanvas canvas, SlideReflection reflection, SKMatrix matrix, SKSize size, Action<SKCanvas, SKMatrix, bool> body)
    {
        var h = size.Height;
        var gap = float.IsFinite(reflection.Distance) ? Math.Clamp(reflection.Distance, -h, 10 * Math.Max(h, 1)) : 0;
        var mirror = matrix.PreConcat(SKMatrix.CreateTranslation(0, 2 * h + gap)).PreConcat(SKMatrix.CreateScale(1, -1));
        using var blur = reflection.Blur > 0 ? SKImageFilter.CreateBlur(Sigma(reflection.Blur), Sigma(reflection.Blur)) : null;
        using var layer = new SKPaint { ImageFilter = blur };
        canvas.SaveLayer(layer);
        body(canvas, mirror, false);
        // The fade, in the shape's own coordinates: from just under the box down the share of its height.
        static byte Alpha(float a) => (byte)Math.Round(Math.Clamp(float.IsFinite(a) ? a : 0, 0, 1) * 255);
        var top = h + gap;
        var end = Math.Clamp(float.IsFinite(reflection.EndPosition) ? reflection.EndPosition : 1, 0.01f, 1);
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + end * h),
            [SKColors.Black.WithAlpha(Alpha(reflection.StartAlpha)), SKColors.Black.WithAlpha(Alpha(reflection.EndAlpha))], null, SKShaderTileMode.Clamp);
        using var fade = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = shader };
        canvas.Save();
        canvas.Concat(in matrix);
        var reach = Math.Max(size.Width, h);
        canvas.DrawRect(new SKRect(-reach, top - 1, size.Width + reach, top + h + reach), fade);
        canvas.Restore();
        // What lies outside the fade's rectangle (a turned shape's corners) is faded out too.
        using var clear = new SKPaint { BlendMode = SKBlendMode.DstIn, Color = SKColors.Black.WithAlpha(Alpha(reflection.EndAlpha)) };
        canvas.Save();
        canvas.Concat(in matrix);
        using var outside = new SKPath { FillType = SKPathFillType.InverseWinding };
        outside.AddRect(new SKRect(-reach, top - 1, size.Width + reach, top + h + reach));
        canvas.DrawPath(outside, clear);
        canvas.Restore();
        canvas.Restore();
    }
}
