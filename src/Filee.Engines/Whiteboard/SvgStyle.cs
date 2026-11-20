// The SVG properties SvgSlideReader draws with, from presentation attributes and style="" declarations (style wins),
// inherited down the tree as SVG inherits them; display and opacity apply to the element only (opacity multiplies down
// in the reader). "inherit" and values Filee can't read keep the parent's value.

using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;

namespace Filee.Engines.Whiteboard;

internal enum SvgPaintKind
{
    None,
    Color,
    CurrentColor,

    /// <summary>A gradient by id; <see cref="SvgPaint.Color"/> is the fallback colour (transparent without one).</summary>
    Url,
}

internal readonly record struct SvgPaint(SvgPaintKind Kind, SKColor Color = default, string? Url = null)
{
    public static readonly SvgPaint None = new(SvgPaintKind.None);
    public static readonly SvgPaint Black = new(SvgPaintKind.Color, SKColors.Black);

    /// <summary>A fill or stroke value; null when it can't be read.</summary>
    public static SvgPaint? Parse(string value)
    {
        var text = value.Trim();
        if (text == "none")
            return None;
        if (text.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
            return new SvgPaint(SvgPaintKind.CurrentColor);
        if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase) && text.IndexOf(')') is var close and > 4)
        {
            var reference = text[4..close].Trim().Trim('\'', '"');
            var fallback = text[(close + 1)..].Trim();
            var color = fallback.Length > 0 && fallback != "none" ? SvgSlideReader.Color(fallback) ?? default : default;
            return reference.StartsWith('#') ? new SvgPaint(SvgPaintKind.Url, color, reference[1..]) : color.Alpha > 0 ? new SvgPaint(SvgPaintKind.Color, color) : None;
        }
        return SvgSlideReader.Color(text) is { } c ? new SvgPaint(SvgPaintKind.Color, c) : null;
    }
}

internal sealed record SvgStyle
{
    /// <summary>The initial values: black fill, no stroke, 16 px text.</summary>
    public static readonly SvgStyle Initial = new();

    public SvgPaint Fill { get; init; } = SvgPaint.Black;
    public float FillOpacity { get; init; } = 1;
    public bool EvenOdd { get; init; }
    public SvgPaint Stroke { get; init; } = SvgPaint.None;
    public float StrokeOpacity { get; init; } = 1;

    /// <summary>In user units.</summary>
    public float StrokeWidth { get; init; } = 1;

    /// <summary>Dash and gap lengths in user units; null = solid.</summary>
    public float[]? Dash { get; init; }

    public SKStrokeCap Cap { get; init; } = SKStrokeCap.Butt;
    public SKStrokeJoin Join { get; init; } = SKStrokeJoin.Miter;
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>The first family named; null = the renderer's default.</summary>
    public string? FontFamily { get; init; }

    /// <summary>In user units.</summary>
    public float FontSize { get; init; } = 16;

    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public string TextAnchor { get; init; } = "start";

    /// <summary>textArea's text-align (start, center, end, justify); null when not set.</summary>
    public string? TextAlign { get; init; }

    public string DisplayAlign { get; init; } = "before";
    public bool Visible { get; init; } = true;

    /// <summary>xml:space="preserve".</summary>
    public bool Preserve { get; init; }

    /// <summary>display:none (this element only).</summary>
    public bool Hidden { get; init; }

    /// <summary>The element's own opacity.</summary>
    public float Opacity { get; init; } = 1;

    /// <summary>The properties <see cref="Apply"/> reads (an element with none of them only resets display and opacity).</summary>
    private static readonly HashSet<string> Properties = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "display", "opacity", "color", "fill", "fill-opacity", "fill-rule", "stroke", "stroke-opacity", "stroke-width",
        "stroke-dasharray", "stroke-linecap", "stroke-linejoin", "font-family", "font-size", "font-weight", "font-style",
        "text-decoration", "text-anchor", "text-align", "display-align", "visibility",
    };

    /// <summary>The style of <paramref name="element"/>, a child of an element with this style.</summary>
    public SvgStyle Apply(XElement element)
    {
        if (!element.Attributes().Any(a => a.Name.NamespaceName.Length == 0 && Properties.Contains(a.Name.LocalName)
                                           || a.Name == XNamespace.Xml + "space"))
            return Hidden || Opacity != 1 ? this with { Hidden = false, Opacity = 1 } : this;
        var css = CssDeclarations.Parse(SvgSlideReader.Attr(element, "style"));
        string? Get(string name)
        {
            var value = css.Get(name) ?? SvgSlideReader.Attr(element, name);
            return value is null || value.Trim() == "inherit" ? null : value.Trim();
        }

        var style = this with { Hidden = Get("display") == "none", Opacity = Math.Clamp(SvgSlideReader.Fraction(Get("opacity")) ?? 1, 0, 1) };
        if (Get("color") is { } color && SvgSlideReader.Color(color) is { } c)
            style = style with { Color = c };
        if (Get("fill") is { } fill && SvgPaint.Parse(fill) is { } fillPaint)
            style = style with { Fill = fillPaint };
        if (SvgSlideReader.Fraction(Get("fill-opacity")) is { } fillOpacity)
            style = style with { FillOpacity = Math.Clamp(fillOpacity, 0, 1) };
        if (Get("fill-rule") is { } rule)
            style = style with { EvenOdd = rule == "evenodd" };
        if (Get("stroke") is { } stroke && SvgPaint.Parse(stroke) is { } strokePaint)
            style = style with { Stroke = strokePaint };
        if (SvgSlideReader.Fraction(Get("stroke-opacity")) is { } strokeOpacity)
            style = style with { StrokeOpacity = Math.Clamp(strokeOpacity, 0, 1) };
        if (SvgSlideReader.Length(Get("stroke-width"), 0, FontSize) is { } width)
            style = style with { StrokeWidth = Math.Max(0, width) };
        if (Get("stroke-dasharray") is { } dash)
            style = style with { Dash = dash == "none" ? null : [.. SvgSlideReader.Numbers(dash).Take(64).Select(d => Math.Max(0, d))] };
        if (Get("stroke-linecap") is { } cap)
            style = style with { Cap = cap switch { "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square, _ => SKStrokeCap.Butt } };
        if (Get("stroke-linejoin") is { } join)
            style = style with { Join = join switch { "round" => SKStrokeJoin.Round, "bevel" => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter } };
        if (Get("font-family") is { } family)
            style = style with { FontFamily = Family(family) };
        if (Get("font-size") is { } size && FontSizeOf(size) is { } px)
            style = style with { FontSize = px };
        if (Get("font-weight") is { } weight)
            style = style with { Bold = weight is "bold" or "bolder" || int.TryParse(weight, out var w) && w >= 600 };
        if (Get("font-style") is { } fontStyle)
            style = style with { Italic = fontStyle is "italic" or "oblique" };
        if (Get("text-decoration") is { } decoration)
            style = style with { Underline = decoration.Contains("underline", StringComparison.Ordinal), Strike = decoration.Contains("line-through", StringComparison.Ordinal) };
        if (Get("text-anchor") is { } anchor)
            style = style with { TextAnchor = anchor };
        if (Get("text-align") is { } align)
            style = style with { TextAlign = align };
        if (Get("display-align") is { } displayAlign)
            style = style with { DisplayAlign = displayAlign };
        if (Get("visibility") is { } visibility)
            style = style with { Visible = visibility is not ("hidden" or "collapse") };
        if ((string?)element.Attribute(XNamespace.Xml + "space") is { } space)
            style = style with { Preserve = space == "preserve" };
        return style;
    }

    /// <summary>The style of an XHTML element in a foreignObject: its inline CSS and the tags that format text.</summary>
    public SvgStyle ApplyHtml(XElement element)
    {
        var css = CssDeclarations.Parse(SvgSlideReader.Attr(element, "style"));
        var style = this with { Hidden = css.Get("display")?.Trim() == "none", TextAlign = css.Get("text-align")?.Trim() ?? SvgSlideReader.Attr(element, "align") };
        switch (element.Name.LocalName.ToLowerInvariant())
        {
            case "b" or "strong" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "th":
                style = style with { Bold = true };
                break;
            case "i" or "em" or "cite":
                style = style with { Italic = true };
                break;
            case "u" or "ins":
                style = style with { Underline = true };
                break;
            case "s" or "strike" or "del":
                style = style with { Strike = true };
                break;
            case "font":
                if (SvgSlideReader.Attr(element, "color") is { } fontColor && SvgSlideReader.Color(fontColor) is { } c)
                    style = style with { Fill = new SvgPaint(SvgPaintKind.Color, c) };
                if (SvgSlideReader.Attr(element, "face") is { } face)
                    style = style with { FontFamily = Family(face) };
                break;
        }
        if (css.Get("color") is { } color && SvgSlideReader.Color(color) is { } textColor)
            style = style with { Fill = new SvgPaint(SvgPaintKind.Color, textColor) };
        if (css.Get("font-family") is { } family)
            style = style with { FontFamily = Family(family) };
        if (css.Get("font-size") is { } size && FontSizeOf(size) is { } px)
            style = style with { FontSize = px };
        if (css.Get("font-weight") is { } weight)
            style = style with { Bold = weight is "bold" or "bolder" || int.TryParse(weight, out var w) && w >= 600 };
        if (css.Get("font-style") is { } fontStyle)
            style = style with { Italic = fontStyle is "italic" or "oblique" };
        if ((css.Get("text-decoration") ?? css.Get("text-decoration-line")) is { } decoration)
            style = style with { Underline = decoration.Contains("underline", StringComparison.Ordinal), Strike = decoration.Contains("line-through", StringComparison.Ordinal) };
        return style;
    }

    /// <summary>A font-size in pixels: a length, a percentage or em of the parent's, or a keyword.</summary>
    private float? FontSizeOf(string value)
    {
        var px = value switch
        {
            "xx-small" => 9f,
            "x-small" => 10f,
            "small" => 13f,
            "medium" => 16f,
            "large" => 18f,
            "x-large" => 24f,
            "xx-large" => 32f,
            "smaller" => FontSize / 1.2f,
            "larger" => FontSize * 1.2f,
            _ => SvgSlideReader.Length(value, FontSize, FontSize),
        };
        return px is > 0 ? Math.Min(px.Value, 10_000) : null;
    }

    /// <summary>The first family of a font-family list, unquoted; generic families as common fonts.</summary>
    private static string? Family(string value)
    {
        var first = value.Split(',')[0].Trim().Trim('\'', '"').Trim();
        return first.ToLowerInvariant() switch
        {
            "" => null,
            "sans-serif" or "system-ui" => "Arial",
            "serif" => "Times New Roman",
            "monospace" => "Courier New",
            "cursive" => "Comic Sans MS",
            _ => first.Length > 128 ? first[..128] : first,
        };
    }
}
