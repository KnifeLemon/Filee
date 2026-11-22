// 3D scenes (dr3d:scene, OpenDocument 1.3 Part 3, 10.5) drawn as flat-shaded faces, a group of filled polygons in
// the slide model, so every output (pictures, PDF, the ODP writer) shows them as LibreOffice's flat shading does:
//
// - Objects: dr3d:cube (min-edge to max-edge), dr3d:sphere (centre and size, its graphic style's horizontal and
//   vertical segments), dr3d:extrude (its svg:d polygons extruded dr3d:depth, the back face scaled by dr3d:back-scale,
//   front and back closed unless dr3d:close-front / -back say otherwise) and dr3d:rotate (the polygons turned about the
//   y axis through dr3d:end-angle in horizontal segments, closed by front and back faces when not a full turn); nested
//   scenes; each through its dr3d:transform (matrix, rotatex/y/z, scale, translate, applied in the order given) inside
//   its scene's.
// - The camera: from dr3d:vrp looking against dr3d:vpn with dr3d:vup up, in perspective (dr3d:projection) when the
//   eye stands in front of the scene, else in parallel. The projection is scaled, unevenly, so the projected bounding
//   box fills the scene's svg:x / svg:y / svg:width / svg:height (10.5.2).
// - Shading: each face lit once (dr3d:shade-mode flat; gouraud and phong drawn flat too) by the scene's ambient colour
//   and its enabled dr3d:light elements (their diffuse colour along their direction, which points to the light in the
//   camera's coordinates), on the fill colour of the object's graphic style. Faces are drawn back to front. Textures,
//   specular highlights, shadows, edge rounding and lines are left out.
//
// Hostile input: segments are cut to 2–256 (as consumers support, 20.85), polygons to OdfEnhancedGeometry.MaxTokens,
// each face counts as an object against the document budget and its points against the deck's path commands, scenes
// nest within the budget's depth.

using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Filee.Engines.Slides;
using SkiaSharp;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf.Draw;

internal sealed partial class OdfDrawReader
{
    /// <summary>Segments of a sphere or rotation object at most (and at least 2).</summary>
    private const int MaxSegments = 256;

    /// <summary>Faces of one scene at most.</summary>
    private const int MaxSceneFaces = 200_000;

    /// <summary>LibreOffice's 3D fill when a style gives none.</summary>
    private static readonly SKColor Default3DFill = new(0x72, 0x9F, 0xCF);

    /// <summary>One face of a scene, in the camera's coordinates.</summary>
    private readonly record struct Face(Vector3[] Points, SKColor Color);

    /// <summary>A scene: its faces projected, shaded and fitted into its box, drawn back to front; null when it shows nothing.</summary>
    private SlideGroup? Scene(XElement scene, PageContext context, OdfScope scope)
    {
        _budget.Object();
        if (Place(scene, context) is not { } place || place.Bounds.Width <= 0 || place.Bounds.Height <= 0)
            return null;
        var faces = new List<(Face Face, Vector3 Normal)>();
        Collect(scene, Matrix4x4.Identity, faces, context, scope);
        if (faces.Count == 0)
            return null;

        // The camera.
        var eye = Vector(Attr(scene, Dr3d + "vrp")) ?? new Vector3(0, 0, 1);
        var normal = Vector(Attr(scene, Dr3d + "vpn")) is { } vpn && vpn.LengthSquared() > 0 ? Vector3.Normalize(vpn) : Vector3.UnitZ;
        var up = Vector(Attr(scene, Dr3d + "vup")) ?? Vector3.UnitY;
        var right = Vector3.Cross(up, normal);
        if (right.LengthSquared() < 1e-12f)
            right = Vector3.Cross(Math.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, normal);
        right = Vector3.Normalize(right);
        var trueUp = Vector3.Cross(normal, right);
        Vector3 View(Vector3 p)
        {
            var d = p - eye;
            // x right, y up, z towards the eye (negative in front of it).
            return new Vector3(Vector3.Dot(d, right), Vector3.Dot(d, trueUp), Vector3.Dot(d, normal));
        }
        var viewed = faces.Select(f => (Points: f.Face.Points.Select(View).ToArray(), f.Face.Color, Normal: new Vector3(Vector3.Dot(f.Normal, right), Vector3.Dot(f.Normal, trueUp), Vector3.Dot(f.Normal, normal)))).ToList();
        var nearest = viewed.SelectMany(f => f.Points).Max(p => p.Z);
        var perspective = Attr(scene, Dr3d + "projection") != "parallel" && nearest < 0;

        // Lights, in the camera's coordinates.
        var ambient = OdfGraphicStyles.Color(Attr(scene, Dr3d + "ambient-color")) ?? new SKColor(0x66, 0x66, 0x66);
        var lights = new List<(Vector3 Direction, SKColor Color)>();
        foreach (var light in scene.Elements(Dr3d + "light").Take(8))
        {
            if (Attr(light, Dr3d + "enabled") == "false" || Vector(Attr(light, Dr3d + "direction")) is not { } direction || direction.LengthSquared() <= 0)
                continue;
            lights.Add((Vector3.Normalize(direction), OdfGraphicStyles.Color(Attr(light, Dr3d + "diffuse-color")) ?? new SKColor(0xCC, 0xCC, 0xCC)));
        }
        if (lights.Count == 0)
            lights.Add((Vector3.UnitZ, new SKColor(0xCC, 0xCC, 0xCC)));

        SKPoint Project(Vector3 p) => perspective ? new SKPoint(p.X / -p.Z, -p.Y / -p.Z) : new SKPoint(p.X, -p.Y);
        var projected = new List<(SKPoint[] Points, float Depth, SKColor Color)>(viewed.Count);
        foreach (var (points, color, n) in viewed)
        {
            // Lit from the side the viewer sees.
            var toEye = perspective ? -points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Length : Vector3.UnitZ;
            var facing = Vector3.Dot(n, toEye) >= 0 ? n : -n;
            float r = ambient.Red, g = ambient.Green, b = ambient.Blue;
            foreach (var (direction, light) in lights)
            {
                var k = Math.Max(0, Vector3.Dot(facing, direction));
                r += light.Red * k;
                g += light.Green * k;
                b += light.Blue * k;
            }
            static byte Lit(byte channel, float light) => (byte)Math.Clamp(Math.Round(channel * light / 255), 0, 255);
            var shaded = new SKColor(Lit(color.Red, r), Lit(color.Green, g), Lit(color.Blue, b), color.Alpha);
            projected.Add((points.Select(Project).ToArray(), points.Average(p => p.Z), shaded));
        }

        // The projection fills the scene's box (10.5.2).
        var all = projected.SelectMany(f => f.Points).Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y)).ToList();
        if (all.Count == 0)
            return null;
        var (minX, maxX, minY, maxY) = (all.Min(p => p.X), all.Max(p => p.X), all.Min(p => p.Y), all.Max(p => p.Y));
        var box = place.Bounds;
        var sx = maxX - minX > 1e-9f ? box.Width / (maxX - minX) : 1;
        var sy = maxY - minY > 1e-9f ? box.Height / (maxY - minY) : 1;
        var group = new SlideGroup { Bounds = box, ChildBounds = box, Rotation = place.Rotation, FlipVertical = place.FlipVertical };
        foreach (var (points, _, color) in projected.OrderBy(f => f.Depth))
        {
            var path = new SKPath();
            path.AddPoly([.. points.Select(p => new SKPoint(box.Left + (p.X - minX) * sx, box.Top + (p.Y - minY) * sy))]);
            var bounds = path.Bounds;
            if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) || !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom)
                || bounds.Width * bounds.Height <= 0)
            {
                path.Dispose();
                continue;
            }
            path.Offset(-bounds.Left, -bounds.Top);
            group.Children.Add(new SlideShape { Bounds = bounds, Paths = [new SlidePath(path, Stroke: false)], Fill = new SolidFill(color) });
        }
        return group.Children.Count == 0 ? null : group;
    }

    /// <summary>The faces of a scene's objects (and nested scenes) in the outer scene's coordinates, with their normals.</summary>
    private void Collect(XElement scene, Matrix4x4 outer, List<(Face Face, Vector3 Normal)> faces, PageContext context, OdfScope scope)
    {
        var transform = Transform3D(Attr(scene, Dr3d + "transform")) * outer;
        using var level = _budget.Enter();
        foreach (var element in scene.Elements())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (element.Name.Namespace != Dr3d || element.Name.LocalName is "light")
                continue;
            Visit();
            if (element.Name.LocalName == "scene")
            {
                Collect(element, transform, faces, context, scope);
                continue;
            }
            var props = OdfGraphicStyles.Props(StyleChain(element, context, scope), "graphic-properties");
            var color = _styles.Fill(props, solid: true) is { } fill ? SlidePaint.Representative(fill) : Default3DFill;
            if (props[Draw + "fill"] is null && props[Draw + "fill-color"] is null)
                color = Default3DFill;
            var local = Transform3D(Attr(element, Dr3d + "transform")) * transform;
            var made = element.Name.LocalName switch
            {
                "cube" => Cube(element),
                "sphere" => Sphere(element, props),
                "extrude" => Extrude(element, props),
                "rotate" => Lathe(element, props),
                _ => [],
            };
            foreach (var points in made)
            {
                if (faces.Count >= MaxSceneFaces)
                    throw Documents.DocumentLimits.Exceeded($"A 3D scene has more than {Documents.DocumentLimits.Count(MaxSceneFaces)} faces.");
                _budget.Object();
                Commands(points.Length);
                var world = points.Select(p => Vector3.Transform(p, local)).ToArray();
                if (Normal(world) is { } n)
                    faces.Add((new Face(world, color), n));
            }
        }
    }

    /// <summary>A face's normal from its first corner that isn't in line with its neighbours; null for a face without area.</summary>
    private static Vector3? Normal(Vector3[] points)
    {
        var sum = Vector3.Zero;
        // Newell's method: robust for any planar polygon.
        for (var i = 0; i < points.Length; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Length]);
            sum += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return sum.LengthSquared() > 1e-18f && float.IsFinite(sum.X) && float.IsFinite(sum.Y) && float.IsFinite(sum.Z) ? Vector3.Normalize(sum) : null;
    }

    private static List<Vector3[]> Cube(XElement cube)
    {
        var min = Vector(Attr(cube, Dr3d + "min-edge")) ?? new Vector3(-2500);
        var max = Vector(Attr(cube, Dr3d + "max-edge")) ?? new Vector3(2500);
        Vector3 C(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        return
        [
            [C(0, 0, 1), C(1, 0, 1), C(1, 1, 1), C(0, 1, 1)], // front
            [C(1, 0, 0), C(0, 0, 0), C(0, 1, 0), C(1, 1, 0)], // back
            [C(0, 0, 0), C(0, 0, 1), C(0, 1, 1), C(0, 1, 0)], // left
            [C(1, 0, 1), C(1, 0, 0), C(1, 1, 0), C(1, 1, 1)], // right
            [C(0, 1, 1), C(1, 1, 1), C(1, 1, 0), C(0, 1, 0)], // top
            [C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1)], // bottom
        ];
    }

    private static int Segments(OdfProps props, string name, int fallback) =>
        int.TryParse(props[Dr3d + name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 2, MaxSegments) : fallback;

    private static List<Vector3[]> Sphere(XElement sphere, OdfProps props)
    {
        var center = Vector(Attr(sphere, Dr3d + "center")) ?? Vector3.Zero;
        // dr3d:size is the sphere's extent along each axis.
        var radius = (Vector(Attr(sphere, Dr3d + "size")) ?? new Vector3(5000)) / 2;
        var around = Segments(props, "horizontal-segments", 24);
        var down = Segments(props, "vertical-segments", 24);
        Vector3 At(int i, int j)
        {
            var longitude = 2 * MathF.PI * i / around;
            var latitude = MathF.PI * j / down - MathF.PI / 2;
            return center + radius * new Vector3(MathF.Cos(latitude) * MathF.Sin(longitude), MathF.Sin(latitude), MathF.Cos(latitude) * MathF.Cos(longitude));
        }
        var result = new List<Vector3[]>(around * down);
        for (var j = 0; j < down; j++)
            for (var i = 0; i < around; i++)
            {
                Vector3[] quad = [At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j + 1)];
                // The poles' triangles.
                result.Add(j == 0 ? [quad[0], quad[2], quad[3]] : j == down - 1 ? [quad[0], quad[1], quad[2]] : quad);
            }
        return result;
    }

    /// <summary>The polygons of a dr3d:extrude or dr3d:rotate (svg:d in svg:viewBox), y up as the scene has it.</summary>
    private List<List<Vector2>> Polygons(XElement element)
    {
        var d = Attr(element, Svg + "d") ?? "";
        if (d.Length == 0)
            return [];
        using var path = OdfPathData.Parse(d, (x, y) => new SKPoint((float)Math.Clamp(x, -1e9, 1e9), (float)Math.Clamp(-y, -1e9, 1e9)), 1, 1,
            OdfEnhancedGeometry.MaxTokens, out var commands);
        Commands(commands + 1);
        return [.. SlideRenderer.Outlines([path]).Select(o => o.Select(p => new Vector2(p.X, p.Y)).ToList()).Where(o => o.Count >= 2)];
    }

    private List<Vector3[]> Extrude(XElement element, OdfProps props)
    {
        var polygons = Polygons(element);
        // In the scene's units (1/100 mm, as LibreOffice writes them).
        var depth = Math.Clamp((Points(props[Dr3d + "depth"]) ?? 0) * 2540 / 72, 0, 1e9f);
        var scale = (float)(OdfNames.Percent(props[Dr3d + "back-scale"]) is { } s ? Math.Clamp(s, 0, 10_000) / 100 : 1);
        var result = new List<Vector3[]>();
        foreach (var polygon in polygons)
        {
            var center = polygon.Aggregate(Vector2.Zero, (a, b) => a + b) / polygon.Count;
            Vector3 Front(Vector2 p) => new(p.X, p.Y, depth);
            Vector3 Back(Vector2 p) => new(center.X + (p.X - center.X) * scale, center.Y + (p.Y - center.Y) * scale, 0);
            if (props[Dr3d + "close-front"] != "false")
                result.Add([.. polygon.Select(Front)]);
            if (depth <= 0)
                continue;
            if (props[Dr3d + "close-back"] != "false")
                result.Add([.. polygon.Select(Back).Reverse()]);
            for (var i = 0; i < polygon.Count; i++)
            {
                var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
                result.Add([Front(a), Back(a), Back(b), Front(b)]);
            }
        }
        return result;
    }

    private List<Vector3[]> Lathe(XElement element, OdfProps props)
    {
        var polygons = Polygons(element);
        var steps = Segments(props, "horizontal-segments", 24);
        // More than a full turn looks like one (the back scale isn't drawn along the turn).
        var end = props[Dr3d + "end-angle"] is { } angle && Degrees(angle, wrap: false) is > 0 and var a ? Math.Min(a, 360) : 360;
        var full = Math.Abs(end - 360) < 1e-3;
        var sweep = end * MathF.PI / 180;
        Vector3 At(Vector2 p, int step)
        {
            var t = sweep * step / steps;
            return new Vector3(p.X * MathF.Cos(t), p.Y, -p.X * MathF.Sin(t));
        }
        var result = new List<Vector3[]>();
        foreach (var polygon in polygons)
        {
            for (var step = 0; step < steps; step++)
                for (var i = 0; i < polygon.Count - (full ? 1 : 0); i++)
                {
                    var (p, q) = (polygon[i], polygon[(i + 1) % polygon.Count]);
                    result.Add([At(p, step), At(q, step), At(q, step + 1), At(p, step + 1)]);
                }
            if (!full)
            {
                if (props[Dr3d + "close-front"] != "false")
                    result.Add([.. polygon.Select(p => At(p, 0))]);
                if (props[Dr3d + "close-back"] != "false")
                    result.Add([.. polygon.Select(p => At(p, steps)).Reverse()]);
            }
        }
        return result;
    }

    /// <summary>A vector3D ("(x y z)"); null when it is none.</summary>
    private static Vector3? Vector(string? value)
    {
        var parts = (value ?? "").Trim().TrimStart('(').TrimEnd(')').Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            return null;
        var numbers = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
                return null;
            numbers[i] = (float)Math.Clamp(v, -1e9, 1e9);
        }
        return new Vector3(numbers[0], numbers[1], numbers[2]);
    }

    /// <summary>
    /// A dr3d:transform (19.107) as a matrix for row vectors (System.Numerics): the transforms applied in the order given
    /// (the first one to the point last, as SVG and draw:transform have it).
    /// </summary>
    private static Matrix4x4 Transform3D(string? transform)
    {
        var result = Matrix4x4.Identity;
        if (string.IsNullOrWhiteSpace(transform))
            return result;
        var at = 0;
        for (var count = 0; count < MaxTransformOperations && at < transform.Length; count++)
        {
            var open = transform.IndexOf('(', at);
            var close = open < 0 ? -1 : transform.IndexOf(')', open);
            if (close < 0)
                break;
            var name = transform[at..open].Trim();
            var values = transform[(open + 1)..close].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? (float)Math.Clamp(n, -1e9, 1e9) : 0f)
                .ToArray();
            at = close + 1;
            float V(int i) => i < values.Length ? values[i] : 0;
            float Angle(int i) => V(i) * MathF.PI / 180;
            Matrix4x4? operation = name switch
            {
                // ( a d g j / b e h k / c f i l ) for column vectors: the transpose for row vectors.
                "matrix" when values.Length >= 12 => new Matrix4x4(V(0), V(1), V(2), 0, V(3), V(4), V(5), 0, V(6), V(7), V(8), 0, V(9), V(10), V(11), 1),
                "rotatex" => Matrix4x4.CreateRotationX(Angle(0)),
                "rotatey" => Matrix4x4.CreateRotationY(Angle(0)),
                "rotatez" => Matrix4x4.CreateRotationZ(Angle(0)),
                "scale" => Matrix4x4.CreateScale(V(0), values.Length > 1 ? V(1) : V(0), values.Length > 2 ? V(2) : V(0)),
                "translate" => Matrix4x4.CreateTranslation(V(0), V(1), V(2)),
                _ => null,
            };
            // The later operation is applied to the point first.
            if (operation is { } op)
                result = op * result;
        }
        return result;
    }
}
