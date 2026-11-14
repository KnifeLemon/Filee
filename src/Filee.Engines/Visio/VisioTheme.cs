// A Visio drawing's theme ([MS-VSDX] 2.2.7.4 dynamic themes, the DrawingML theme part of visio/theme/): what a cell
// stored as "Themed" stands for. The quick style cells of a shape (through its masters and styles) pick the pieces:
//
// - Colours (QuickStyleFillColor, QuickStyleLineColor, QuickStyleFontColor, QuickStyleShadowColor): 0 dk1, 1 lt1, 2–7
//   accent1–6, 8 the background colour (lt1 here), 100–106 the colours of a variant's colour scheme
//   (vt:variationClrScheme varColor1–7), the scheme chosen by VariationColorIndex (65534: the page's).
// - Fill and line matrices (QuickStyleFillMatrix, QuickStyleLineMatrix, 1–6; 100–103 through a variant's style scheme,
//   vt:varStyle fillIdx / lineIdx, chosen by VariationStyleIndex): the theme's fill style or line style of that index
//   (counting round when the theme has fewer), drawn with the quick style colour in place of phClr.
// - Fonts: the theme's minor (body) font.
//
// A drawing without a theme part keeps the stored or inherited values, as before.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Visio;

internal sealed class VisioTheme
{
    private static readonly XNamespace A = DrawingColors.A;

    /// <summary>The value a cell holds when its dynamic theme gives it.</summary>
    public const string Themed = "Themed";

    private static readonly string[] Slots = ["dk1", "lt1", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "lt1"];

    private readonly List<SKColor[]> _variantColors = [];
    private readonly List<(int Fill, int Line)[]> _variantStyles = [];
    private readonly List<XElement> _fills;
    private readonly List<XElement> _lines;
    private readonly List<XElement> _effects;

    private VisioTheme(XElement theme)
    {
        Colors = DrawingColors.FromTheme(theme);
        var elements = theme.Element(A + "themeElements");
        foreach (var scheme in theme.Descendants().Where(e => e.Name.LocalName == "variationClrScheme").Take(16))
        {
            var colors = new SKColor[7];
            for (var i = 0; i < 7; i++)
                colors[i] = Colors.Color(scheme.Elements().FirstOrDefault(e => e.Name.LocalName == $"varColor{i + 1}")) ?? Colors.Scheme(Slots[Math.Min(i + 2, 7)]);
            _variantColors.Add(colors);
        }
        foreach (var scheme in theme.Descendants().Where(e => e.Name.LocalName == "variationStyleScheme").Take(16))
        {
            _variantStyles.Add([.. scheme.Elements().Where(e => e.Name.LocalName == "varStyle").Take(4)
                .Select(s => (Index(s, "fillIdx"), Index(s, "lineIdx")))]);
        }
        var format = elements?.Element(A + "fmtScheme");
        _fills = format?.Element(A + "fillStyleLst")?.Elements().ToList() ?? [];
        _lines = format?.Element(A + "lnStyleLst")?.Elements(A + "ln").ToList() ?? [];
        _effects = format?.Element(A + "effectStyleLst")?.Elements(A + "effectStyle").ToList() ?? [];
        MinorFont = (string?)elements?.Element(A + "fontScheme")?.Element(A + "minorFont")?.Element(A + "latin")?.Attribute("typeface") is { Length: > 0 } font
            ? font : null;
    }

    private static int Index(XElement element, string name) =>
        int.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static VisioTheme? From(XElement? theme) => theme is null || theme.Element(A + "themeElements") is null ? null : new VisioTheme(theme);

    public DrawingColors Colors { get; }

    /// <summary>The theme's body font; null when it names none.</summary>
    public string? MinorFont { get; }

    /// <summary>A quick style colour: <paramref name="index"/> as the QuickStyle*Color cells take it, in variant colour scheme <paramref name="variant"/>.</summary>
    public SKColor Color(int index, int variant)
    {
        if (index is >= 100 and <= 106)
        {
            return _variantColors.Count > 0
                ? _variantColors[Math.Clamp(variant, 0, _variantColors.Count - 1)][index - 100]
                : Colors.Scheme(Slots[Math.Min(index - 100 + 2, 7)]);
        }
        return Colors.Scheme(Slots[Math.Clamp(index, 0, Slots.Length - 1)]);
    }

    /// <summary>The fill style a fill matrix value picks (in variant style scheme <paramref name="variant"/>); null for none.</summary>
    public XElement? FillStyle(int matrix, int variant) => Pick(_fills, Matrix(matrix, variant, fill: true));

    /// <summary>The line style a line matrix value picks; null for none.</summary>
    public XElement? LineStyle(int matrix, int variant) => Pick(_lines, Matrix(matrix, variant, fill: false));

    private int Matrix(int matrix, int variant, bool fill)
    {
        if (matrix is < 100 or > 103)
            return matrix;
        if (_variantStyles.Count == 0)
            return 1;
        var styles = _variantStyles[Math.Clamp(variant, 0, _variantStyles.Count - 1)];
        if (matrix - 100 >= styles.Length)
            return 1;
        return fill ? styles[matrix - 100].Fill : styles[matrix - 100].Line;
    }

    private static XElement? Pick(List<XElement> styles, int index) => index <= 0 || styles.Count == 0 ? null : styles[(index - 1) % styles.Count];

    /// <summary>The effect style an effects matrix value picks has effects (shadows, glow, reflection, soft edges, 3D).</summary>
    public bool HasEffects(int matrix, int variant)
    {
        var index = matrix is >= 100 and <= 103 ? Matrix(matrix, variant, fill: true) : matrix;
        if (Pick(_effects, index) is not { } style)
            return false;
        return style.Descendants().Any(e => e.Name.LocalName is "outerShdw" or "innerShdw" or "prstShdw" or "glow" or "reflection" or "softEdge" or "blur"
            or "sp3d" or "scene3d");
    }

    /// <summary>A fill style drawn with <paramref name="color"/> for phClr: solid, a gradient, or null for no fill.</summary>
    public SlideFill? Fill(XElement style, SKColor color)
    {
        switch (style.Name.LocalName)
        {
            case "noFill":
                return null;
            case "gradFill":
                var stops = new List<GradientStop>();
                foreach (var stop in style.Element(A + "gsLst")?.Elements(A + "gs") ?? [])
                {
                    var position = int.TryParse((string?)stop.Attribute("pos"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos) ? pos : 0;
                    stops.Add(new GradientStop(Math.Clamp(position / 100000f, 0, 1), Colors.Color(stop, color) ?? color));
                }
                if (stops.Count == 0)
                    return new SolidFill(color);
                stops.Sort((a, b) => a.Position.CompareTo(b.Position));
                if (style.Element(A + "path") is { } path)
                    return new GradientFill(stops, (string?)path.Attribute("path") == "circle" ? GradientShape.Circle : GradientShape.Rectangle, 0, false,
                        new SKRect(0.5f, 0.5f, 0.5f, 0.5f));
                var angle = int.TryParse((string?)style.Element(A + "lin")?.Attribute("ang"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ang) ? ang / 60000f : 90;
                return new GradientFill(stops, GradientShape.Linear, angle, false, SKRect.Empty);
            default: // solidFill and what Filee draws in its colour (patterns, pictures)
                return new SolidFill(Colors.Color(style, color) ?? color);
        }
    }

    /// <summary>A line style's colour (phClr = <paramref name="color"/>), width in points and whether it is drawn.</summary>
    public (SKColor Color, float? Width, bool Drawn) Line(XElement style, SKColor color)
    {
        var width = int.TryParse((string?)style.Attribute("w"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w / 12700f : (float?)null;
        if (style.Element(A + "noFill") is not null)
            return (color, width, false);
        var fill = style.Element(A + "solidFill") ?? style.Element(A + "gradFill")?.Element(A + "gsLst")?.Element(A + "gs");
        return (fill is null ? color : Colors.Color(fill, color) ?? color, width, true);
    }
}
