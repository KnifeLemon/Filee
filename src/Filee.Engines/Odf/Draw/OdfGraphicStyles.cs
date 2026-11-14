// The styles of an ODF presentation or drawing as the draw reader looks them up, and graphic properties as the slide
// model's fills and lines.
//
// - Styles: default styles, named styles (styles.xml office:styles) and automatic styles, those of content.xml for
//   pages and those of styles.xml for master pages (a flat file has one set), followed through style:parent-style-name
//   (at most MaxStyleChain deep; loops end where they repeat). Presentation styles take the graphic default style.
// - Fills (draw:fill): solid (draw:fill-color, draw:opacity), gradient (draw:gradient: linear, axial, radial,
//   ellipsoid, square, rectangular; start / end colours and intensities, angle, border, centre), hatch (draw:hatch as
//   the nearest two-colour pattern), bitmap (draw:fill-image, stretched or tiled). Shapes fill solid unless told
//   otherwise, frames and pages only when their style says so.
// - Lines (draw:stroke): colour, width (0 = hairline), opacity, dashes (draw:stroke-dash), joins, caps and the markers
//   at either end (draw:marker-start / -end, by their kind of shape and width).

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Slides;
using SkiaSharp;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf.Draw;

/// <summary>Where automatic style names are looked up: content.xml (pages) or styles.xml (master pages).</summary>
internal enum OdfScope
{
    Content,
    Styles,
}

/// <summary>Property elements of one kind along a style chain, outermost first: the last one that sets a value wins.</summary>
internal sealed class OdfProps(IReadOnlyList<XElement?> elements)
{
    public static readonly OdfProps None = new([]);

    public IReadOnlyList<XElement?> Elements { get; } = elements;

    public string? this[XName name]
    {
        get
        {
            for (var i = Elements.Count - 1; i >= 0; i--)
            {
                if ((string?)Elements[i]?.Attribute(name) is { } value)
                    return value;
            }
            return null;
        }
    }

    /// <summary>The first child element of that name, from the innermost style out.</summary>
    public XElement? Child(XName name)
    {
        for (var i = Elements.Count - 1; i >= 0; i--)
        {
            if (Elements[i]?.Element(name) is { } child)
                return child;
        }
        return null;
    }

    public OdfProps With(IEnumerable<XElement?> more) => new([.. Elements, .. more]);
}

internal sealed class OdfGraphicStyles
{
    /// <summary>Styles followed through parents at the most.</summary>
    private const int MaxStyleChain = 20;

    /// <summary>Dash and gap lengths of a line at most.</summary>
    private const int MaxDashes = 16;

    /// <summary>LibreOffice's default fill colour, for a solid fill without one.</summary>
    private static readonly SKColor DefaultFill = new(0x72, 0x9F, 0xCF);

    private static readonly XNamespace Office = OdfNames.Office;
    private static readonly XNamespace Style = OdfNames.Style;
    private static readonly XNamespace Text = OdfNames.Text;
    private static readonly XNamespace Draw = OdfNames.Draw;
    private static readonly XNamespace Svg = OdfNames.Svg;
    private static readonly XNamespace Fo = OdfNames.Fo;
    private static readonly XNamespace XLink = OdfNames.XLink;

    private readonly OdfDrawPackage _package;
    private readonly Dictionary<(string Family, string Name), XElement> _named = [];
    private readonly Dictionary<(string Family, string Name), XElement> _contentAutomatic = [];
    private readonly Dictionary<(string Family, string Name), XElement> _stylesAutomatic = [];
    private readonly Dictionary<string, XElement> _defaults = [];
    private readonly Dictionary<string, XElement> _listStyles = [];
    private readonly Dictionary<string, XElement> _contentListStyles = [];
    private readonly Dictionary<string, XElement> _stylesListStyles = [];
    private readonly Dictionary<(string Kind, string Name), XElement> _drawStyles = [];
    private readonly Dictionary<string, XElement> _pageLayouts = [];
    private readonly Dictionary<(string Family, string? Name, OdfScope Scope, bool Default), List<XElement>> _chains = [];

    public OdfGraphicStyles(OdfDrawPackage package)
    {
        _package = package;
        foreach (var root in new[] { package.Styles, package.Content }.Distinct())
        {
            foreach (var face in root?.Element(Office + "font-face-decls")?.Elements(Style + "font-face") ?? [])
            {
                if ((string?)face.Attribute(Style + "name") is { } name
                    && Hwp.Hwpx.Odt.OdtReader.FirstFamily((string?)face.Attribute(Svg + "font-family")) is { } family)
                    FontFaces.TryAdd(name, family);
            }
        }
        var common = package.Styles?.Element(Office + "styles");
        foreach (var element in common?.Elements() ?? [])
        {
            if (element.Name == Style + "default-style" && (string?)element.Attribute(Style + "family") is { } family)
                _defaults.TryAdd(family, element);
            else if (element.Name.Namespace == Draw && (string?)element.Attribute(Draw + "name") is { } name)
                _drawStyles.TryAdd((element.Name.LocalName, name), element);
        }
        Collect(common, _named, _listStyles);
        Collect(package.Styles?.Element(Office + "automatic-styles"), _stylesAutomatic, _stylesListStyles);
        if (!package.Flat)
            Collect(package.Content.Element(Office + "automatic-styles"), _contentAutomatic, _contentListStyles);
        foreach (var container in new[] { common, package.Styles?.Element(Office + "automatic-styles") })
        {
            foreach (var layout in container?.Elements(Style + "page-layout") ?? [])
            {
                if ((string?)layout.Attribute(Style + "name") is { } name)
                    _pageLayouts.TryAdd(name, layout);
            }
        }
        foreach (var master in package.Styles?.Element(Office + "master-styles")?.Elements(Style + "master-page") ?? [])
        {
            if ((string?)master.Attribute(Style + "name") is { } name)
                MasterPages.TryAdd(name, master);
        }
    }

    /// <summary>Font face names (style:font-name) → font family.</summary>
    public Dictionary<string, string> FontFaces { get; } = [];

    /// <summary>Master pages by name, in document order of first appearance.</summary>
    public Dictionary<string, XElement> MasterPages { get; } = [];

    private static void Collect(XElement? container, Dictionary<(string, string), XElement> styles, Dictionary<string, XElement> lists)
    {
        foreach (var element in container?.Elements() ?? [])
        {
            if ((string?)element.Attribute(Style + "name") is not { } name)
                continue;
            if (element.Name == Style + "style")
                styles.TryAdd(((string?)element.Attribute(Style + "family") ?? "", name), element);
            else if (element.Name == Text + "list-style")
                lists.TryAdd(name, element);
        }
    }

    private Dictionary<(string, string), XElement> Automatic(OdfScope scope) =>
        _package.Flat || scope == OdfScope.Styles ? _stylesAutomatic : _contentAutomatic;

    /// <summary>A style by family and name: the automatic styles of the scope first, then the named ones.</summary>
    public XElement? Find(string family, string? name, OdfScope scope) =>
        name is null ? null : Automatic(scope).GetValueOrDefault((family, name)) ?? _named.GetValueOrDefault((family, name));

    /// <summary>A named style (office:styles) of the family.</summary>
    public XElement? Named(string family, string name) => _named.GetValueOrDefault((family, name));

    public XElement? ListStyle(string? name, OdfScope scope)
    {
        if (name is null)
            return null;
        var automatic = _package.Flat || scope == OdfScope.Styles ? _stylesListStyles : _contentListStyles;
        return automatic.GetValueOrDefault(name) ?? _listStyles.GetValueOrDefault(name);
    }

    /// <summary>A page layout's properties (style:page-layout-properties) by name.</summary>
    public XElement? PageLayout(string? name) =>
        name is null ? null : _pageLayouts.GetValueOrDefault(name)?.Element(Style + "page-layout-properties");

    /// <summary>
    /// The style and its parents, outermost first, after the family's default style (the graphic one for presentation
    /// styles) when <paramref name="withDefault"/>. Loops and chains longer than <see cref="MaxStyleChain"/> end where they repeat.
    /// </summary>
    public List<XElement> Chain(string family, string? name, OdfScope scope, bool withDefault)
    {
        var key = (family, name, _package.Flat ? OdfScope.Styles : scope, withDefault);
        if (_chains.TryGetValue(key, out var cached))
            return cached;
        var chain = new List<XElement>();
        var seen = new HashSet<XElement>(ReferenceEqualityComparer.Instance);
        for (var style = Find(family, name, scope); style is not null && chain.Count < MaxStyleChain && seen.Add(style);)
        {
            chain.Insert(0, style);
            var parent = (string?)style.Attribute(Style + "parent-style-name");
            style = parent is null ? null : _named.GetValueOrDefault((family, parent)) ?? Find(family, parent, scope);
        }
        if (withDefault && _defaults.GetValueOrDefault(family is "presentation" ? "graphic" : family) is { } defaults)
            chain.Insert(0, defaults);
        return _chains[key] = chain;
    }

    /// <summary>The property elements of <paramref name="kind"/> (graphic-properties, paragraph-properties ...) along a chain.</summary>
    public static OdfProps Props(IEnumerable<XElement> chain, string kind) => new([.. chain.Select(s => s.Element(Style + kind))]);

    // ───────────────────────── Fills ─────────────────────────

    /// <summary>The fill of graphic or drawing-page properties; null for none.</summary>
    /// <param name="solid">Fill solid when no draw:fill is given (shapes); frames and pages fill only when told.</param>
    public SlideFill? Fill(OdfProps props, bool solid)
    {
        var fill = props[Draw + "fill"];
        var opacity = (float)(OdfNames.Percent(props[Draw + "opacity"]) is { } percent ? Math.Clamp(percent, 0, 100) / 100 : 1);
        if (fill is null)
        {
            if (props[Fo + "background-color"] is { } background && Color(background) is { } backgroundColor)
                return new SolidFill(backgroundColor);
            fill = solid ? "solid" : "none";
        }
        var color = Color(props[Draw + "fill-color"]) ?? DefaultFill;
        return fill switch
        {
            "solid" => new SolidFill(Fade(color, opacity)),
            "gradient" => (SlideFill?)Gradient(props[Draw + "fill-gradient-name"], opacity) ?? new SolidFill(Fade(color, opacity)),
            "hatch" => Hatch(props[Draw + "fill-hatch-name"], props[Draw + "fill-hatch-solid"] == "true" ? Fade(color, opacity) : SKColors.Transparent, opacity),
            "bitmap" => (SlideFill?)Bitmap(props, opacity) ?? new SolidFill(Fade(color, opacity)),
            _ => null,
        };
    }

    private static SKColor Fade(SKColor color, float opacity) => opacity >= 1 ? color : color.WithAlpha((byte)Math.Round(color.Alpha * opacity));

    /// <summary>"#rrggbb" as a colour; null when it is no colour.</summary>
    public static SKColor? Color(string? value) =>
        OdfNames.Color(value) is { } hex ? new SKColor(Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)) : null;

    private GradientFill? Gradient(string? name, float opacity)
    {
        if (name is null || _drawStyles.GetValueOrDefault(("gradient", name)) is not { } gradient)
            return null;
        string? Get(string attribute) => (string?)gradient.Attribute(Draw + attribute);
        SKColor Intense(SKColor c, string attribute)
        {
            var intensity = (float)(OdfNames.Percent(Get(attribute)) is { } p ? Math.Clamp(p, 0, 100) / 100 : 1);
            return Fade(new SKColor((byte)(c.Red * intensity), (byte)(c.Green * intensity), (byte)(c.Blue * intensity)), opacity);
        }
        var start = Intense(Color(Get("start-color")) ?? SKColors.Black, "start-intensity");
        var end = Intense(Color(Get("end-color")) ?? SKColors.White, "end-intensity");
        var border = (float)(OdfNames.Percent(Get("border")) is { } b ? Math.Clamp(b, 0, 100) / 100 : 0);
        var cx = (float)(OdfNames.Percent(Get("cx")) is { } x ? Math.Clamp(x, 0, 100) / 100 : 0.5);
        var cy = (float)(OdfNames.Percent(Get("cy")) is { } y ? Math.Clamp(y, 0, 100) / 100 : 0.5);
        var focus = new SKRect(cx, cy, 1 - cx, 1 - cy);
        switch (Get("style"))
        {
            case "axial":
                // The end colour runs along the middle, the start colour at both sides.
                return new GradientFill([new(0, start), new(border / 2, start), new(0.5f, end), new(1 - border / 2, start), new(1, start)],
                    GradientShape.Linear, 90 - Angle(Get("angle")), false, SKRect.Empty);
            case "radial":
                return new GradientFill([new(0, end), new(1 - border, start), new(1, start)], GradientShape.Circle, 0, false, focus);
            case "ellipsoid" or "square" or "rectangular":
                return new GradientFill([new(0, end), new(1 - border, start), new(1, start)], GradientShape.Rectangle, 0, false, focus);
            default:
                // Linear: at angle 0 the start colour is at the top; the angle turns the axis counter-clockwise.
                return new GradientFill([new(0, start), new(border, start), new(1, end)], GradientShape.Linear, 90 - Angle(Get("angle")), false, SKRect.Empty);
        }
    }

    /// <summary>A draw:angle in degrees: with its unit (deg, rad, grad), or tenths of a degree as LibreOffice writes it.</summary>
    public static float Angle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        var text = value.Trim();
        var (number, factor) = text.EndsWith("deg", StringComparison.Ordinal) ? (text[..^3], 1.0)
            : text.EndsWith("grad", StringComparison.Ordinal) ? (text[..^4], 0.9)
            : text.EndsWith("rad", StringComparison.Ordinal) ? (text[..^3], 180 / Math.PI)
            : (text, 0.1);
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var angle) && double.IsFinite(angle)
            ? (float)(angle * factor % 360)
            : 0;
    }

    /// <summary>A hatch as the two-colour pattern of the nearest direction.</summary>
    private PatternFill? Hatch(string? name, SKColor background, float opacity)
    {
        if (name is null || _drawStyles.GetValueOrDefault(("hatch", name)) is not { } hatch)
            return null;
        var color = Fade(Color((string?)hatch.Attribute(Draw + "color")) ?? SKColors.Black, opacity);
        var angle = Angle((string?)hatch.Attribute(Draw + "rotation")) % 180;
        if (angle < 0)
            angle += 180;
        var crossed = (string?)hatch.Attribute(Draw + "style") is "double" or "triple";
        // Lines at 45° rise to the right ("/"), at 135° fall to the right ("\").
        var preset = (Math.Round(angle / 45) % 4, crossed) switch
        {
            (0, false) => "horz",
            (2, false) => "vert",
            (1, false) => "upDiag",
            (3, false) => "dnDiag",
            (0 or 2, true) => "cross",
            _ => "diagCross",
        };
        return new PatternFill(preset, color, background);
    }

    private PictureFill? Bitmap(OdfProps props, float opacity)
    {
        if (props[Draw + "fill-image-name"] is not { } name || _drawStyles.GetValueOrDefault(("fill-image", name)) is not { } image)
            return null;
        if (Picture(image) is not { } bytes || !SlideImages.CanDraw(bytes, _package.Limits))
            return null;
        var tile = (props[Style + "repeat"] ?? "repeat") == "repeat" ? new PictureTile(0, 0, 1, 1, false, false, "tl") : null;
        return new PictureFill(new SlideImage(bytes), SKRect.Empty, SKRect.Empty, tile, opacity < 1 ? new PictureEffects(Alpha: opacity) : null);
    }

    /// <summary>The bytes of a picture element (draw:image, draw:fill-image): its part, or its office:binary-data.</summary>
    public byte[]? Picture(XElement element) =>
        (string?)element.Attribute(XLink + "href") is { } href ? _package.Bytes(href)
        : element.Element(Office + "binary-data") is { } data ? _package.Inline(data)
        : null;

    // ───────────────────────── Lines ─────────────────────────

    /// <summary>The line of graphic properties; null for none.</summary>
    /// <param name="stroke">Stroke when no draw:stroke is given (shapes and lines); frames only when told.</param>
    public SlideLine? Line(OdfProps props, bool stroke)
    {
        var kind = props[Draw + "stroke"] ?? (stroke ? "solid" : "none");
        if (kind == "none")
            return null;
        var opacity = (float)(OdfNames.Percent(props[Svg + "stroke-opacity"]) is { } percent ? Math.Clamp(percent, 0, 100) / 100 : 1);
        var color = Fade(Color(props[Svg + "stroke-color"]) ?? SKColors.Black, opacity);
        var width = Math.Clamp(Points(props[Svg + "stroke-width"]) ?? 0, 0, 1000);
        var round = false;
        var dash = kind == "dash" ? Dashes(props[Draw + "stroke-dash"], width, out round) : null;
        var cap = props[Svg + "stroke-linecap"] switch
        {
            "round" => SKStrokeCap.Round,
            "square" => SKStrokeCap.Square,
            _ => round ? SKStrokeCap.Round : SKStrokeCap.Butt,
        };
        var join = props[Draw + "stroke-linejoin"] switch
        {
            "miter" => SKStrokeJoin.Miter,
            "bevel" => SKStrokeJoin.Bevel,
            _ => SKStrokeJoin.Round,
        };
        return new SlideLine(new SolidFill(color), width, dash, cap, join,
            Marker(props[Draw + "marker-start"], props[Draw + "marker-start-width"], width),
            Marker(props[Draw + "marker-end"], props[Draw + "marker-end-width"], width));
    }

    /// <summary>An ODF length in points; null for none or a percentage.</summary>
    public static float? Points(string? value) => OdfNames.Length(value) is { } hwp ? hwp / 100f : null;

    /// <summary>The dash and gap lengths of a draw:stroke-dash, as multiples of the line width.</summary>
    private float[]? Dashes(string? name, float width, out bool round)
    {
        round = false;
        if (name is null || _drawStyles.GetValueOrDefault(("stroke-dash", name)) is not { } dash)
            return [4, 3];
        var unit = Math.Max(width, SlideLine.Hairline);
        float Length(string attribute, float fallback)
        {
            var value = (string?)dash.Attribute(Draw + attribute);
            if (OdfNames.Percent(value) is { } percent)
                return (float)Math.Clamp(percent / 100, 0.01, 100);
            return Points(value) is { } points ? Math.Clamp(points / unit, 0.01f, 100) : fallback;
        }
        static int Count(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, MaxDashes) : 0;
        var gap = Length("distance", 1);
        var result = new List<float>();
        for (var i = 0; i < Count((string?)dash.Attribute(Draw + "dots1")) && result.Count < MaxDashes; i++)
            result.AddRange([Length("dots1-length", 1), gap]);
        for (var i = 0; i < Count((string?)dash.Attribute(Draw + "dots2")) && result.Count < MaxDashes; i++)
            result.AddRange([Length("dots2-length", 1), gap]);
        round = (string?)dash.Attribute(Draw + "style") == "round";
        return result.Count == 0 ? [4, 3] : [.. result];
    }

    /// <summary>A line end marker by the kind of its shape (from its name), as wide as draw:marker-*-width.</summary>
    private LineEnd Marker(string? name, string? markerWidth, float lineWidth)
    {
        if (name is null || !_drawStyles.ContainsKey(("marker", name)))
            return LineEnd.None;
        var lower = name.ToLowerInvariant();
        var kind = lower.Contains("circle") || lower.Contains("oval") ? LineEndKind.Oval
            : lower.Contains("square") || lower.Contains("diamond") ? LineEndKind.Diamond
            : lower.Contains("line") ? LineEndKind.Arrow
            : lower.Contains("concave") ? LineEndKind.Stealth
            : LineEndKind.Triangle;
        var size = Points(markerWidth) ?? 0.3f * 72 / 2.54f;
        var multiple = Math.Clamp(size / Math.Max(lineWidth, 1), 1, 20);
        return new LineEnd(kind, multiple, multiple);
    }
}
