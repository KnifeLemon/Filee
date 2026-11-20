// What a mind map file says about how a topic, a branch, a boundary or a relationship looks, as MindMapReader reads it
// for the drawn map (MindMapDrawing): every value optional, the drawing's defaults (MindMapDrawing.Defaults) filling
// what the file leaves out. XMind keeps these as style properties ("svg:fill", "fo:color", "shape-class",
// "line-color", ...: content.json's style.properties and theme, content.xml's styles.xml); FreeMind and Freeplane as
// node attributes (COLOR, BACKGROUND_COLOR, STYLE) and child elements (font, edge, cloud, arrowlink).

using System.Globalization;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.MindMaps;

/// <summary>The outline of a topic.</summary>
internal enum TopicShape
{
    /// <summary>The drawing's default for the topic's place in the map.</summary>
    Default,
    Rect,
    RoundedRect,
    Ellipse,
    Diamond,
    Parallelogram,
    Hexagon,
    Stadium,

    /// <summary>A line under the text (FreeMind's fork, XMind's underline), the branch line running into it.</summary>
    Underline,

    /// <summary>The text alone.</summary>
    None,
}

/// <summary>How a branch line runs from a topic to its subtopic.</summary>
internal enum BranchLine
{
    Default,
    Curve,
    Straight,
    Elbow,
    None,
}

/// <summary>A topic's, branch's, boundary's or relationship's look; null and Default members are left to the drawing.</summary>
internal sealed record MindStyle
{
    public static readonly MindStyle Empty = new();

    public SKColor? Fill { get; init; }
    public SKColor? TextColor { get; init; }
    public SKColor? BorderColor { get; init; }
    public float? BorderWidth { get; init; }
    public TopicShape Shape { get; init; }
    public float? FontSize { get; init; }
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public string? Font { get; init; }

    /// <summary>The line into the topic and, unless they say otherwise, its subtopics' lines.</summary>
    public SKColor? LineColor { get; init; }

    public float? LineWidth { get; init; }
    public BranchLine Line { get; init; }

    /// <summary>A dashed line (boundaries, relationships).</summary>
    public bool? Dashed { get; init; }

    public LineEndKind? ArrowStart { get; init; }
    public LineEndKind? ArrowEnd { get; init; }

    /// <summary>This style with what it leaves out taken from <paramref name="under"/>.</summary>
    public MindStyle Over(MindStyle? under) => under is null ? this : new MindStyle
    {
        Fill = Fill ?? under.Fill,
        TextColor = TextColor ?? under.TextColor,
        BorderColor = BorderColor ?? under.BorderColor,
        BorderWidth = BorderWidth ?? under.BorderWidth,
        Shape = Shape != TopicShape.Default ? Shape : under.Shape,
        FontSize = FontSize ?? under.FontSize,
        Bold = Bold ?? under.Bold,
        Italic = Italic ?? under.Italic,
        Font = Font ?? under.Font,
        LineColor = LineColor ?? under.LineColor,
        LineWidth = LineWidth ?? under.LineWidth,
        Line = Line != BranchLine.Default ? Line : under.Line,
        Dashed = Dashed ?? under.Dashed,
        ArrowStart = ArrowStart ?? under.ArrowStart,
        ArrowEnd = ArrowEnd ?? under.ArrowEnd,
    };

    /// <summary>
    /// XMind's style properties (content.json's style.properties, styles.xml's *-properties attributes), keyed by name
    /// with or without their prefix ("svg:fill" or "fill").
    /// </summary>
    public static MindStyle FromXMind(IEnumerable<KeyValuePair<string, string>> properties)
    {
        var style = new MindStyle();
        foreach (var (rawKey, value) in properties)
        {
            var key = rawKey[(rawKey.IndexOf(':', StringComparison.Ordinal) + 1)..];
            style = key switch
            {
                "fill" => style with { Fill = Color(value) },
                "color" => style with { TextColor = Color(value) },
                "border-line-color" => style with { BorderColor = Color(value) },
                "border-line-width" => style with { BorderWidth = Length(value) },
                "font-size" => style with { FontSize = Length(value) },
                "font-weight" => style with { Bold = value.Trim() is "bold" or "bolder" or "600" or "700" or "800" or "900" },
                "font-style" => style with { Italic = value.Trim() is "italic" or "oblique" },
                "font-family" => style with { Font = FontName(value) },
                "shape-class" => style with { Shape = XMindShape(value) },
                "line-color" => style with { LineColor = Color(value) },
                "line-width" => style with { LineWidth = Length(value) },
                "line-class" => style with { Line = XMindLine(value) },
                "line-pattern" => style with { Dashed = value.Trim() is "dash" or "dot" or "dash-dot" or "dash-dot-dot" },
                "arrow-begin-class" => style with { ArrowStart = XMindArrow(value) },
                "arrow-end-class" => style with { ArrowEnd = XMindArrow(value) },
                _ => style,
            };
        }
        return style;
    }

    private static TopicShape XMindShape(string value)
    {
        var name = value.Trim();
        name = name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant();
        return name switch
        {
            "rect" or "rectangle" => TopicShape.Rect,
            "roundedrect" or "roundrect" => TopicShape.RoundedRect,
            "ellipse" or "ellipserect" or "circle" => TopicShape.Ellipse,
            "diamond" => TopicShape.Diamond,
            "parallelogram" => TopicShape.Parallelogram,
            "hexagon" => TopicShape.Hexagon,
            "stadium" or "capsule" => TopicShape.Stadium,
            "underline" => TopicShape.Underline,
            "noborder" or "none" => TopicShape.None,
            _ => TopicShape.Default,
        };
    }

    private static BranchLine XMindLine(string value)
    {
        var name = value.Trim();
        name = name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant();
        return name switch
        {
            "straight" => BranchLine.Straight,
            "elbow" or "roundedelbow" or "fold" or "roundedfold" => BranchLine.Elbow,
            "none" => BranchLine.None,
            "" => BranchLine.Default,
            _ => BranchLine.Curve,
        };
    }

    private static LineEndKind XMindArrow(string value)
    {
        var name = value.Trim();
        name = name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant();
        return name switch
        {
            "none" or "" => LineEndKind.None,
            "spearhead" or "attached" => LineEndKind.Stealth,
            "diamond" => LineEndKind.Diamond,
            "dot" or "circle" => LineEndKind.Oval,
            "herringbone" or "arrow" => LineEndKind.Arrow,
            _ => LineEndKind.Triangle,
        };
    }

    /// <summary>"#rgb", "#rrggbb" or "#rrggbbaa" (CSS order); "none" and "transparent" are no colour (transparent).</summary>
    public static SKColor? Color(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text is "none" or "transparent")
            return SKColors.Transparent;
        if (text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
            return null;
        return (text.Length - 1) switch
        {
            3 => new SKColor((byte)((number >> 8 & 0xF) * 17), (byte)((number >> 4 & 0xF) * 17), (byte)((number & 0xF) * 17)),
            6 => new SKColor((byte)(number >> 16), (byte)(number >> 8), (byte)number),
            8 => new SKColor((byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number),
            _ => null,
        };
    }

    /// <summary>A length in points: "12", "12pt", "16px" (¾ pt each); null for anything else or outside 0–400.</summary>
    public static float? Length(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
            return null;
        var scale = 1f;
        if (text.EndsWith("pt", StringComparison.Ordinal))
            text = text[..^2];
        else if (text.EndsWith("px", StringComparison.Ordinal))
            (text, scale) = (text[..^2], 0.75f);
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 400
            ? number * scale
            : null;
    }

    /// <summary>
    /// The first family of a font list, without quotes; null for XMind's "inherit" and the like and for Java's logical
    /// fonts FreeMind and Freeplane write ("SansSerif", "Dialog"), which stand for the default font.
    /// </summary>
    public static string? FontName(string value)
    {
        var first = value.Split(',')[0].Trim().Trim('"', '\'').Trim();
        return first.Length is 0 or > 64
               || first.ToLowerInvariant() is "inherit" or "initial" or "nevermind" or "sansserif" or "dialog" or "dialoginput" or "default"
            ? null
            : first;
    }
}

/// <summary>
/// An arrow between two topics (ids): an XMind relationship or a FreeMind / Freeplane arrow link. The control points the
/// files keep belong to the app's own layout and aren't read: the drawing lays the map out anew.
/// </summary>
internal sealed record MindLink(string From, string To, string? Title, MindStyle Style);

/// <summary>
/// An XMind boundary around subtopics <paramref name="Start"/> to <paramref name="End"/> of a topic (attached ones, from
/// 0), or the whole topic with its subtopics (<paramref name="Master"/>).
/// </summary>
internal sealed record MindBoundary(int Start, int End, bool Master, string? Title, MindStyle Style);

/// <summary>An XMind summary of subtopics <paramref name="Start"/> to <paramref name="End"/>, its topic one of the summary children.</summary>
internal sealed record MindSummary(int Start, int End, string? TopicId);

/// <summary>A FreeMind / Freeplane cloud around a topic and its subtopics.</summary>
internal sealed record MindCloud(SKColor? Color);
