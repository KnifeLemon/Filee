// Reads a FixedPage part into XPS elements (XpsModel): Canvas, Path and Glyphs with their properties written as
// attributes ("Fill=\"#FF0000\"", "RenderTransform=\"1,0,0,1,10,10\"", abbreviated geometry) or as property elements
// (<Path.Fill><LinearGradientBrush …/></Path.Fill>), resources from FixedPage.Resources and Canvas.Resources (inline
// or in a remote ResourceDictionary part) by "{StaticResource key}", and markup compatibility's AlternateContent
// (the Fallback, else the first Choice). The XPS 1.0 and OpenXPS namespaces are both read: elements are matched by
// their local names in the page's own namespace.
//
// Bounds (XpsLimits): canvases and visual brushes nest at most MaxNestingDepth deep (also what stops a visual brush
// that draws itself), elements count as objects, path and glyph points against the document's budget.

using System.Xml.Linq;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Xps;

internal sealed class XpsPageReader(XpsDocument document, string part, CancellationToken cancellationToken)
{
    private static readonly XNamespace Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>Resources already read, by their element.</summary>
    private readonly Dictionary<XElement, object?> _resources = [];

    private XpsBudget Budget => document.Budget;

    /// <summary>A resource dictionary in scope: its entries, the part they were written in, and the scope around it.</summary>
    private sealed class Scope(Scope? parent)
    {
        public Dictionary<string, (XElement Element, string Part, Scope Scope)> Entries { get; } = new(StringComparer.Ordinal);

        public (XElement Element, string Part, Scope Scope)? Find(string key)
        {
            for (var scope = this; scope is not null; scope = scope.Parent)
            {
                if (scope.Entries.TryGetValue(key, out var entry))
                    return entry;
            }
            return null;
        }

        public Scope? Parent { get; } = parent;
    }

    /// <summary>The page's elements as one canvas.</summary>
    public XpsCanvas Read(XElement page)
    {
        var root = new XpsCanvas();
        var scope = Resources(page, new Scope(null), part);
        foreach (var child in Children(page))
        {
            if (Element(child, scope, part, aliased: false) is { } node)
                root.Children.Add(node);
        }
        return root;
    }

    // ───────────────────────── Elements ─────────────────────────

    /// <summary>The element's children with AlternateContent replaced by what it chooses, property elements left out.</summary>
    private static IEnumerable<XElement> Children(XElement element)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name == Compatibility + "AlternateContent")
            {
                var chosen = child.Element(Compatibility + "Fallback") ?? child.Element(Compatibility + "Choice");
                if (chosen is null)
                    continue;
                foreach (var inner in Children(chosen))
                    yield return inner;
                continue;
            }
            if (!child.Name.LocalName.Contains('.'))
                yield return child;
        }
    }

    private XpsNode? Element(XElement element, Scope scope, string where, bool aliased)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A canvas's resources are in scope for its own properties too.
        if (element.Name.LocalName == "Canvas")
            scope = Resources(element, scope, where);
        XpsNode? node = element.Name.LocalName switch
        {
            "Canvas" => Canvas(element, scope, where, aliased),
            "Path" => Path(element, scope, where),
            "Glyphs" => Glyphs(element, scope, where),
            _ => null,
        };
        if (node is null)
            return null;
        Budget.Document.Object();
        node.Aliased = aliased || Aliased(element);
        node.Transform = Transform(element, "RenderTransform", scope) ?? SKMatrix.Identity;
        node.Clip = Geometry(element, "Clip", scope);
        node.Opacity = Math.Clamp(XpsValues.Number((string?)element.Attribute("Opacity")) ?? 1, 0, 1);
        node.OpacityMask = Brush(element, "OpacityMask", scope, where);
        node.Name = (string?)element.Attribute("Name");
        node.NavigateUri = (string?)element.Attribute("FixedPage.NavigateUri");
        return node;
    }

    private static bool Aliased(XElement element) =>
        string.Equals((string?)element.Attribute("RenderOptions.EdgeMode"), "Aliased", StringComparison.OrdinalIgnoreCase);

    private XpsCanvas Canvas(XElement element, Scope scope, string where, bool aliased)
    {
        using var level = Budget.Document.Enter();
        var canvas = new XpsCanvas();
        var childAliased = aliased || Aliased(element);
        foreach (var child in Children(element))
        {
            if (Element(child, scope, where, childAliased) is { } node)
                canvas.Children.Add(node);
        }
        return canvas;
    }

    private XpsPath? Path(XElement element, Scope scope, string where)
    {
        var data = Geometry(element, "Data", scope);
        if (data is null)
            return null;
        var fill = Brush(element, "Fill", scope, where);
        var stroke = Brush(element, "Stroke", scope, where);
        var thickness = XpsValues.Number((string?)element.Attribute("StrokeThickness")) ?? 1;
        float[]? dashes = null;
        if (XpsValues.Numbers((string?)element.Attribute("StrokeDashArray")) is { Length: > 0 } pattern
            && pattern.Length <= Budget.Limits.MaxDashes && pattern.All(d => d >= 0) && pattern.Sum() > 0)
        {
            // In multiples of the thickness; an odd count is repeated to make pairs.
            var pairs = pattern.Length % 2 == 0 ? pattern : [.. pattern, .. pattern];
            dashes = [.. pairs.Select(d => d * thickness)];
        }
        return new XpsPath
        {
            Data = data,
            Fill = fill,
            Stroke = stroke,
            StrokeStyle = new XpsStrokeStyle(
                thickness,
                Cap((string?)element.Attribute("StrokeStartLineCap")),
                Cap((string?)element.Attribute("StrokeEndLineCap")),
                Cap((string?)element.Attribute("StrokeDashCap") ?? "Square"),
                ((string?)element.Attribute("StrokeLineJoin"))?.Trim() switch
                {
                    "Round" => SKStrokeJoin.Round,
                    "Bevel" => SKStrokeJoin.Bevel,
                    _ => SKStrokeJoin.Miter,
                },
                XpsValues.Number((string?)element.Attribute("StrokeMiterLimit")) ?? 10,
                dashes,
                (XpsValues.Number((string?)element.Attribute("StrokeDashOffset")) ?? 0) * thickness),
        };
    }

    /// <summary>Flat and Triangle ends are drawn flat (Skia has no triangle cap).</summary>
    private static SKStrokeCap Cap(string? value) => value?.Trim() switch
    {
        "Round" => SKStrokeCap.Round,
        "Square" => SKStrokeCap.Square,
        _ => SKStrokeCap.Butt,
    };

    // ───────────────────────── Glyphs ─────────────────────────

    private XpsGlyphs? Glyphs(XElement element, Scope scope, string where)
    {
        var size = XpsValues.Number((string?)element.Attribute("FontRenderingEmSize"));
        // Larger than any page; such a run draws nothing anyone could read.
        if (size is not (> 0 and <= MaxEmSize))
            return null;
        var text = (string?)element.Attribute("UnicodeString") ?? "";
        if (text.StartsWith("{}", StringComparison.Ordinal))
            text = text[2..];
        var indices = (string?)element.Attribute("Indices");
        if (text.Length == 0 && string.IsNullOrWhiteSpace(indices))
            return null;
        var uri = (string?)element.Attribute("FontUri");
        var embedded = document.Font(XpsPackage.Resolve(where, uri), XpsPackage.Fragment(uri));
        var font = embedded ?? document.FallbackFont;
        if (font is null)
            return null;
        // Counted before the glyphs are laid out: at most a glyph per character and one per Indices entry.
        Budget.Points(text.Length + (indices?.Count(c => c == ';') + 1 ?? 0));
        var simulations = ((string?)element.Attribute("StyleSimulations"))?.Trim() ?? "";
        var rightToLeft = (int)(XpsValues.Number((string?)element.Attribute("BidiLevel")) ?? 0) % 2 == 1;
        var origin = new SKPoint(XpsValues.Number((string?)element.Attribute("OriginX")) ?? 0, XpsValues.Number((string?)element.Attribute("OriginY")) ?? 0);
        // Glyph numbers of another font mean nothing in the fallback: it draws the text's characters.
        var layout = XpsGlyphLayout.Lay(font.Typeface, size.Value, origin, text, embedded is null ? StripIndices(indices) : indices, rightToLeft);
        if (layout.Glyphs.Length == 0 || !layout.Positions.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)))
            return null;
        return new XpsGlyphs
        {
            Font = font,
            Size = size.Value,
            Glyphs = layout.Glyphs,
            Positions = layout.Positions,
            Text = text,
            Clusters = layout.Clusters,
            Fill = Brush(element, "Fill", scope, where),
            Bold = simulations.Contains("Bold", StringComparison.OrdinalIgnoreCase),
            Italic = simulations.Contains("Italic", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Largest FontRenderingEmSize drawn (in 1/96 inch: some 26 metres).</summary>
    private const float MaxEmSize = 100_000;

    /// <summary>Indices with their glyph numbers taken out, advances and cluster maps kept.</summary>
    private static string? StripIndices(string? indices)
    {
        if (string.IsNullOrEmpty(indices))
            return indices;
        return string.Join(';', indices.Split(';').Select(entry =>
        {
            var cluster = entry.StartsWith('(') && entry.IndexOf(')') is var close and > 0 ? entry[..(close + 1)] : "";
            var rest = entry[cluster.Length..];
            var comma = rest.IndexOf(',');
            return cluster + (comma < 0 ? "" : rest[comma..]);
        }));
    }

    // ───────────────────────── Properties ─────────────────────────

    /// <summary>A property as an attribute's text, or as the first element inside its property element.</summary>
    private static (string? Text, XElement? Element) Property(XElement element, string name)
    {
        if ((string?)element.Attribute(name) is { } text)
            return (text, null);
        var property = element.Element(element.Name.Namespace + $"{element.Name.LocalName}.{name}");
        return (null, property is null ? null : Children(property).FirstOrDefault());
    }

    /// <summary>The key of "{StaticResource key}", or null.</summary>
    private static string? ResourceKey(string text)
    {
        text = text.Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}') || text.StartsWith("{}", StringComparison.Ordinal))
            return null;
        var inner = text[1..^1].Trim();
        return inner.StartsWith("StaticResource", StringComparison.Ordinal) ? inner["StaticResource".Length..].Trim() : null;
    }

    private SKMatrix? Transform(XElement element, string name, Scope scope)
    {
        var (text, value) = Property(element, name);
        if (text is not null)
        {
            if (ResourceKey(text) is not { } key)
                return Finite(XpsValues.Matrix(text));
            value = scope.Find(key)?.Element;
        }
        return value?.Name.LocalName == "MatrixTransform" ? Finite(XpsValues.Matrix((string?)value.Attribute("Matrix"))) : null;
    }

    /// <summary>A matrix Skia can draw with: finite and invertible (a singular one draws nothing).</summary>
    private static SKMatrix? Finite(SKMatrix? matrix) =>
        matrix is { } m && float.IsFinite(m.ScaleX + m.SkewX + m.TransX + m.SkewY + m.ScaleY + m.TransY) ? m : null;

    private XpsGeometry? Geometry(XElement element, string name, Scope scope)
    {
        var (text, value) = Property(element, name);
        if (text is not null)
        {
            if (ResourceKey(text) is not { } key)
                return document.Track(XpsGeometry.Parse(text, Budget));
            if (scope.Find(key) is not { } entry)
                return null;
            if (_resources.TryGetValue(entry.Element, out var known))
                return known as XpsGeometry;
            var shared = entry.Element.Name.LocalName == "PathGeometry" ? PathGeometry(entry.Element, entry.Scope) : null;
            _resources[entry.Element] = shared;
            return shared;
        }
        return value?.Name.LocalName == "PathGeometry" ? PathGeometry(value, scope) : null;
    }

    private XpsGeometry PathGeometry(XElement element, Scope scope) =>
        document.Track(XpsGeometry.FromElement(element, Budget, Transform(element, "Transform", scope)));

    // ───────────────────────── Brushes ─────────────────────────

    private XpsBrush? Brush(XElement element, string name, Scope scope, string where)
    {
        var (text, value) = Property(element, name);
        if (text is not null)
        {
            if (ResourceKey(text) is not { } key)
                return XpsValues.Color(text) is { } color ? new XpsSolidBrush(color) : null;
            if (scope.Find(key) is not { } entry)
                return null;
            if (_resources.TryGetValue(entry.Element, out var known))
                return known as XpsBrush;
            // Marked before it is read, so a brush that names itself through a visual ends there.
            _resources[entry.Element] = null;
            var shared = BrushElement(entry.Element, entry.Scope, entry.Part);
            _resources[entry.Element] = shared;
            return shared;
        }
        return value is null ? null : BrushElement(value, scope, where);
    }

    private XpsBrush? BrushElement(XElement brush, Scope scope, string where)
    {
        var opacity = Math.Clamp(XpsValues.Number((string?)brush.Attribute("Opacity")) ?? 1, 0, 1);
        var transform = Transform(brush, "Transform", scope) ?? SKMatrix.Identity;
        switch (brush.Name.LocalName)
        {
            case "SolidColorBrush":
                return XpsValues.Color((string?)brush.Attribute("Color")) is { } color
                    ? new XpsSolidBrush(color) { Opacity = opacity }
                    : null;
            case "LinearGradientBrush":
            case "RadialGradientBrush":
                {
                    var radial = brush.Name.LocalName == "RadialGradientBrush";
                    var (colors, offsets) = Stops(brush);
                    var spread = ((string?)brush.Attribute("SpreadMethod"))?.Trim() switch
                    {
                        "Reflect" => SKShaderTileMode.Mirror,
                        "Repeat" => SKShaderTileMode.Repeat,
                        _ => SKShaderTileMode.Clamp,
                    };
                    return new XpsGradientBrush
                    {
                        Opacity = opacity,
                        Transform = transform,
                        Colors = colors,
                        Offsets = offsets,
                        Spread = spread,
                        Start = XpsValues.Point((string?)brush.Attribute(radial ? "GradientOrigin" : "StartPoint")) ?? default,
                        End = XpsValues.Point((string?)brush.Attribute(radial ? "Center" : "EndPoint")) ?? default,
                        Radius = radial
                            ? new SKSize(XpsValues.Number((string?)brush.Attribute("RadiusX")) ?? 0, XpsValues.Number((string?)brush.Attribute("RadiusY")) ?? 0)
                            : null,
                    };
                }
            case "ImageBrush":
                {
                    if (document.Image(ImageSource((string?)brush.Attribute("ImageSource"), where)) is not { } image)
                        return null;
                    return new XpsImageBrush
                    {
                        Opacity = opacity,
                        Transform = transform,
                        Image = image,
                        Viewbox = XpsValues.Rect((string?)brush.Attribute("Viewbox")) ?? SKRect.Create(image.Width, image.Height),
                        Viewport = XpsValues.Rect((string?)brush.Attribute("Viewport")) ?? SKRect.Create(image.Width, image.Height),
                        Tile = Tile((string?)brush.Attribute("TileMode")),
                    };
                }
            case "VisualBrush":
                {
                    using var level = Budget.Document.Enter();
                    var (text, value) = Property(brush, "Visual");
                    var visualScope = scope;
                    var visualPart = where;
                    if (text is not null && ResourceKey(text) is { } key && scope.Find(key) is { } entry)
                        (value, visualScope, visualPart) = (entry.Element, entry.Scope, entry.Part);
                    if (value is null || Element(value, visualScope, visualPart, aliased: false) is not { } visual)
                        return null;
                    return new XpsVisualBrush
                    {
                        Opacity = opacity,
                        Transform = transform,
                        Visual = visual,
                        Viewbox = XpsValues.Rect((string?)brush.Attribute("Viewbox")) ?? SKRect.Empty,
                        Viewport = XpsValues.Rect((string?)brush.Attribute("Viewport")) ?? SKRect.Empty,
                        Tile = Tile((string?)brush.Attribute("TileMode")),
                    };
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// The part an ImageSource names: a part reference, or the picture of "{ColorConvertedBitmap picture source
    /// profile}" (drawn without the colour conversion).
    /// </summary>
    private static string? ImageSource(string? source, string where)
    {
        if (source is null)
            return null;
        source = source.Trim();
        if (source.StartsWith("{ColorConvertedBitmap", StringComparison.Ordinal) && source.EndsWith('}'))
            source = source["{ColorConvertedBitmap".Length..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return XpsPackage.Resolve(where, source);
    }

    private static XpsTile Tile(string? mode) => mode?.Trim() switch
    {
        "Tile" => XpsTile.Tile,
        "FlipX" => XpsTile.FlipX,
        "FlipY" => XpsTile.FlipY,
        "FlipXY" => XpsTile.FlipXY,
        _ => XpsTile.None,
    };

    /// <summary>
    /// A gradient's stops sorted by offset, cut to 0…1 with the colours there found between the stops around them
    /// (Skia takes positions in 0…1 only).
    /// </summary>
    private static (SKColor[] Colors, float[] Offsets) Stops(XElement brush)
    {
        var stops = new List<(float Offset, SKColor Color)>();
        var property = brush.Element(brush.Name.Namespace + $"{brush.Name.LocalName}.GradientStops");
        foreach (var stop in property?.Elements(brush.Name.Namespace + "GradientStop") ?? [])
        {
            if (XpsValues.Color((string?)stop.Attribute("Color")) is { } color && XpsValues.Number((string?)stop.Attribute("Offset")) is { } offset)
                stops.Add((offset, color));
        }
        if (stops.Count == 0)
            return ([], []);
        var sorted = stops.Select((s, i) => (s, i)).OrderBy(p => p.s.Offset).ThenBy(p => p.i).Select(p => p.s).ToList();
        if (sorted.All(s => s.Offset is >= 0 and <= 1))
            return ([.. sorted.Select(s => s.Color)], [.. sorted.Select(s => s.Offset)]);
        var inside = sorted.Where(s => s.Offset is > 0 and < 1).ToList();
        inside.Insert(0, (0, ColorAt(sorted, 0)));
        inside.Add((1, ColorAt(sorted, 1)));
        return ([.. inside.Select(s => s.Color)], [.. inside.Select(s => s.Offset)]);
    }

    private static SKColor ColorAt(List<(float Offset, SKColor Color)> stops, float at)
    {
        if (at <= stops[0].Offset)
            return stops[0].Color;
        for (var i = 1; i < stops.Count; i++)
        {
            if (at > stops[i].Offset)
                continue;
            var (a, b) = (stops[i - 1], stops[i]);
            var t = b.Offset == a.Offset ? 1 : (at - a.Offset) / (b.Offset - a.Offset);
            return new SKColor(Mix(a.Color.Red, b.Color.Red, t), Mix(a.Color.Green, b.Color.Green, t), Mix(a.Color.Blue, b.Color.Blue, t), Mix(a.Color.Alpha, b.Color.Alpha, t));
        }
        return stops[^1].Color;

        static byte Mix(byte from, byte to, float t) => (byte)Math.Clamp(MathF.Round(from + (to - from) * t), 0, 255);
    }

    // ───────────────────────── Resources ─────────────────────────

    /// <summary>
    /// The scope inside an element with resources (FixedPage.Resources, Canvas.Resources): their ResourceDictionary,
    /// inline or the remote one its Source names. Returns <paramref name="scope"/> when there are none.
    /// </summary>
    private Scope Resources(XElement element, Scope scope, string where)
    {
        var property = element.Element(element.Name.Namespace + $"{element.Name.LocalName}.Resources");
        var dictionary = property?.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceDictionary");
        if (dictionary is null)
            return scope;
        var inner = new Scope(scope);
        var entriesPart = where;
        var entries = dictionary;
        if ((string?)dictionary.Attribute("Source") is { } source && XpsPackage.Resolve(where, source) is { } remote)
        {
            entriesPart = remote;
            entries = document.Dictionary(remote) ?? dictionary;
        }
        foreach (var entry in entries.Elements())
        {
            var key = entry.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
            if (key is not null)
                inner.Entries.TryAdd(key, (entry, entriesPart, inner));
        }
        return inner;
    }
}
