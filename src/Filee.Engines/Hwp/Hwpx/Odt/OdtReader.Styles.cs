// Formatting of an ODT: default styles, named styles (styles.xml office:styles) and automatic styles (office:automatic-
// styles of content.xml for the body, of styles.xml for headers and footers), followed through style:parent-style-name.
// Properties are folded in that order onto a bag of nullable values, so what no style sets keeps ODF's default.

using System.Globalization;
using System.Xml.Linq;
using static Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Hwp.Hwpx.Odt;

internal sealed partial class OdtReader
{
    // As members, the namespaces win over the Filee.Engines.Office / .Text / ... namespaces of the same names.
    private static readonly XNamespace Office = Odf.Office;
    private static readonly XNamespace Style = Odf.Style;
    private static readonly XNamespace Text = Odf.Text;
    private static readonly XNamespace Table = Odf.Table;
    private static readonly XNamespace Draw = Odf.Draw;
    private static readonly XNamespace Fo = Odf.Fo;
    private static readonly XNamespace XLink = Odf.XLink;
    private static readonly XNamespace Svg = Odf.Svg;
    private static readonly XNamespace Dc = Odf.Dc;
    private static readonly XNamespace Manifest = Odf.Manifest;
    private static readonly XNamespace MathMl = Odf.MathMl;

    /// <summary>
    /// ODF's proportional line height is a share of the font's own line height (100% = single), 한글's percentage one
    /// of the font size; single lines are about 130% of the size, as the DOCX reader takes Word's single lines.
    /// </summary>
    internal const double LineToHwpPercent = 1.3;

    /// <summary>Styles followed through parents at the most (real chains are a few long; damaged files loop).</summary>
    private const int MaxStyleChain = 20;

    /// <summary>Where style names are looked up: the automatic styles of content.xml or of styles.xml.</summary>
    private enum Scope
    {
        Content,
        Styles,
    }

    private readonly Dictionary<(string Family, string Name), XElement> _named = [];
    private readonly Dictionary<(string Family, string Name), XElement> _contentAutomatic = [];
    private readonly Dictionary<(string Family, string Name), XElement> _stylesAutomatic = [];
    private readonly Dictionary<string, XElement> _defaults = [];
    private readonly Dictionary<string, XElement> _listStyles = [];
    private readonly Dictionary<string, XElement> _contentListStyles = [];
    private readonly Dictionary<string, XElement> _stylesListStyles = [];
    private readonly Dictionary<string, XElement> _pageLayouts = [];
    private readonly Dictionary<string, XElement> _masterPages = [];
    private readonly Dictionary<string, string> _fontFaces = [];
    private readonly Dictionary<string, XElement> _gradients = [];
    private readonly Dictionary<(Scope, string?), ParagraphStyle> _paragraphStyles = [];
    private XElement? _firstMasterPage;
    private Scope _scope = Scope.Content;

    private void LoadStyles(XDocument? styles, XDocument content)
    {
        foreach (var root in new[] { styles?.Root, content.Root })
        {
            foreach (var face in root?.Element(Office + "font-face-decls")?.Elements(Style + "font-face") ?? [])
            {
                if (Attr(face, Style + "name") is { } name && FirstFamily(Attr(face, Svg + "font-family")) is { } family)
                    _fontFaces.TryAdd(name, family);
            }
        }

        var common = styles?.Root?.Element(Office + "styles");
        foreach (var element in common?.Elements() ?? [])
        {
            if (element.Name == Style + "default-style" && Attr(element, Style + "family") is { } family)
                _defaults.TryAdd(family, element);
            else if (element.Name == Draw + "gradient" && Attr(element, Draw + "name") is { } gradient)
                _gradients.TryAdd(gradient, element);
        }
        Collect(common, _named, _listStyles);
        Collect(styles?.Root?.Element(Office + "automatic-styles"), _stylesAutomatic, _stylesListStyles);
        Collect(content.Root?.Element(Office + "automatic-styles"), _contentAutomatic, _contentListStyles);

        foreach (var container in new[] { common, styles?.Root?.Element(Office + "automatic-styles") })
        {
            foreach (var layout in container?.Elements(Style + "page-layout") ?? [])
            {
                if (Attr(layout, Style + "name") is { } name)
                    _pageLayouts.TryAdd(name, layout);
            }
        }
        foreach (var master in styles?.Root?.Element(Office + "master-styles")?.Elements(Style + "master-page") ?? [])
        {
            _firstMasterPage ??= master;
            if (Attr(master, Style + "name") is { } name)
                _masterPages.TryAdd(name, master);
        }
    }

    private static void Collect(XElement? container, Dictionary<(string, string), XElement> styles, Dictionary<string, XElement> lists)
    {
        foreach (var element in container?.Elements() ?? [])
        {
            if (Attr(element, Style + "name") is not { } name)
                continue;
            if (element.Name == Style + "style")
                styles.TryAdd((Attr(element, Style + "family") ?? "", name), element);
            else if (element.Name == Text + "list-style")
                lists.TryAdd(name, element);
        }
    }

    /// <summary>A style by family and name: the automatic styles of the part being read first, then the named ones.</summary>
    private XElement? Find(string family, string? name)
    {
        if (name is null)
            return null;
        var automatic = _scope == Scope.Content ? _contentAutomatic : _stylesAutomatic;
        return automatic.GetValueOrDefault((family, name)) ?? _named.GetValueOrDefault((family, name));
    }

    private XElement? FindListStyle(string? name)
    {
        if (name is null)
            return null;
        var automatic = _scope == Scope.Content ? _contentListStyles : _stylesListStyles;
        return automatic.GetValueOrDefault(name) ?? _listStyles.GetValueOrDefault(name);
    }

    /// <summary>
    /// The style and its parents, outermost parent first, after the family's default style. Loops and chains longer
    /// than <see cref="MaxStyleChain"/> end where they repeat.
    /// </summary>
    private List<XElement> Chain(string family, string? name, bool withDefault = true)
    {
        var chain = new List<XElement>();
        var seen = new HashSet<XElement>();
        for (var style = Find(family, name); style is not null && chain.Count < MaxStyleChain && seen.Add(style);)
        {
            chain.Insert(0, style);
            var parent = Attr(style, Style + "parent-style-name");
            style = parent is null ? null : _named.GetValueOrDefault((family, parent)) ?? Find(family, parent);
        }
        if (withDefault && _defaults.TryGetValue(family, out var defaults))
            chain.Insert(0, defaults);
        return chain;
    }

    /// <summary>The first attribute of that name along a chain of property elements, the last one winning.</summary>
    private static string? Inherited(IEnumerable<XElement?> chain, XName name)
    {
        string? value = null;
        foreach (var element in chain)
            value = Attr(element, name) ?? value;
        return value;
    }

    // ───────────────────────── Paragraph styles ─────────────────────────

    /// <summary>A paragraph style with everything it inherits.</summary>
    /// <param name="OwnIndent">The style is automatic (direct formatting) and sets an indent: it wins over the list's.</param>
    private sealed record ParagraphStyle(ParaProps Para, TextProps Text, string? MasterPage, string? PageNumber, string? ListStyle,
        int? OutlineLevel, bool OwnIndent);

    private ParagraphStyle ParagraphStyleOf(string? name)
    {
        if (_paragraphStyles.TryGetValue((_scope, name), out var cached))
            return cached;
        var chain = Chain("paragraph", name);
        var para = new ParaProps();
        var text = new TextProps();
        foreach (var style in chain)
        {
            para = para.Apply(style.Element(Style + "paragraph-properties"));
            text = text.Apply(style.Element(Style + "text-properties"), _fontFaces);
        }
        var styles = chain.Where(s => s.Name == Style + "style").ToList();
        var master = Inherited(styles, Style + "master-page-name");
        var outline = int.TryParse(Inherited(styles, Style + "default-outline-level"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? level : (int?)null;

        var own = name is null ? null : (_scope == Scope.Content ? _contentAutomatic : _stylesAutomatic).GetValueOrDefault(("paragraph", name));
        var ownProps = own?.Element(Style + "paragraph-properties");
        var ownIndent = ownProps is not null && (ownProps.Attribute(Fo + "margin-left") ?? ownProps.Attribute(Fo + "margin-right")
            ?? ownProps.Attribute(Fo + "text-indent") ?? ownProps.Attribute(Fo + "margin")) is not null;

        return _paragraphStyles[(_scope, name)] = new ParagraphStyle(para, text, master is { Length: > 0 } ? master : null,
            Inherited(chain.Select(s => s.Element(Style + "paragraph-properties")), Style + "page-number"),
            Inherited(styles, Style + "list-style-name"), outline, ownIndent);
    }

    /// <summary>Text properties of a span (or link) style applied over <paramref name="outer"/>.</summary>
    private TextProps SpanProps(TextProps outer, string? styleName)
    {
        if (styleName is null)
            return outer;
        var text = outer;
        foreach (var style in Chain("text", styleName, withDefault: false))
            text = text.Apply(style.Element(Style + "text-properties"), _fontFaces);
        return text;
    }

    /// <summary>The model's paragraph format; <paramref name="listIndent"/> is the list level's indent, used unless the paragraph has its own.</summary>
    private static HParaFormat ParaFormat(ParagraphStyle style, (int Left, int FirstLine)? listIndent)
    {
        var p = style.Para;
        var rtl = p.WritingMode is "rl-tb" or "rl";
        var align = p.Align switch
        {
            "center" => HAlign.Center,
            "justify" => p.AlignLast == "justify" ? HAlign.Distribute : HAlign.Justify,
            "end" => HAlign.Right,
            "right" => rtl ? HAlign.Left : HAlign.Right,
            "left" => rtl ? HAlign.Right : HAlign.Left,
            _ => HAlign.Left,
        };
        // Right to left, the model's Left is the start side (the right), as Word's w:bidi paragraphs have it.
        var left = rtl ? p.MarginRight : p.MarginLeft;
        var right = rtl ? p.MarginLeft : p.MarginRight;
        var firstLine = p.TextIndent;
        if (listIndent is { } list && !style.OwnIndent)
        {
            left = list.Left;
            firstLine = list.FirstLine;
        }

        var (spacing, height) = p.Line switch
        {
            { Kind: LineKind.Fixed } line => (new HLineSpacing(HLineSpacingKind.Fixed, (int)line.Value), new HLineHeight(HLineHeightRule.Exact, (int)line.Value)),
            { Kind: LineKind.AtLeast } line => (new HLineSpacing(HLineSpacingKind.AtLeast, (int)line.Value), new HLineHeight(HLineHeightRule.AtLeast, (int)line.Value)),
            { Kind: LineKind.Leading } line => (new HLineSpacing(HLineSpacingKind.Percent, (int)Math.Round(100 * LineToHwpPercent)), new HLineHeight(HLineHeightRule.BetweenLines, (int)line.Value)),
            { Kind: LineKind.Proportional } line => (new HLineSpacing(HLineSpacingKind.Percent, Math.Clamp((int)Math.Round(line.Value * LineToHwpPercent), 1, 5000)),
                new HLineHeight(HLineHeightRule.Multiple, Math.Clamp((int)Math.Round(line.Value * 2.4), 1, 24000))),
            _ => (new HLineSpacing(HLineSpacingKind.Percent, (int)Math.Round(100 * LineToHwpPercent)), new HLineHeight(HLineHeightRule.Multiple, 240)),
        };

        return new HParaFormat(
            Align: align,
            Left: left ?? 0,
            Right: right ?? 0,
            FirstLine: firstLine ?? 0,
            Before: Math.Max(0, p.MarginTop ?? 0),
            After: Math.Max(0, p.MarginBottom ?? 0),
            LineSpacing: spacing,
            KeepWithNext: p.KeepWithNext == "always",
            KeepLines: p.KeepTogether == "always",
            WidowOrphan: p.Widows is null && p.Orphans is null || (p.Widows ?? 0) > 0 || (p.Orphans ?? 0) > 0,
            Tabs: TabStops(p.Tabs),
            RightToLeft: rtl,
            Layout: new HParaLayout(LineHeight: height));
    }

    private static List<HTabStop> TabStops(XElement? tabs)
    {
        var stops = new List<HTabStop>();
        foreach (var tab in tabs?.Elements(Style + "tab-stop") ?? [])
        {
            if (Length(Attr(tab, Style + "position")) is not { } position)
                continue;
            var kind = Attr(tab, Style + "type") switch
            {
                "right" => HTabKind.Right,
                "center" => HTabKind.Center,
                "char" => HTabKind.Decimal,
                _ => HTabKind.Left,
            };
            var leaderText = Attr(tab, Style + "leader-text");
            var leader = (Attr(tab, Style + "leader-style"), leaderText) switch
            {
                (_, ".") or ("dotted", null) => "DOT",
                (_, "-") or ("dash" or "long-dash", null) => "DASH",
                (_, "_") or ("solid", null) => "SOLID",
                ("none" or null, _) or (_, " ") => "NONE",
                _ => "DOT",
            };
            stops.Add(new HTabStop(Math.Max(0, position), kind, leader));
        }
        return stops;
    }

    private enum LineKind
    {
        Proportional,
        Fixed,
        AtLeast,
        Leading,
    }

    private readonly record struct LineRule(LineKind Kind, double Value);

    /// <summary>Paragraph properties; null = not set by any style so far.</summary>
    private sealed record ParaProps
    {
        public string? Align { get; init; }
        public string? AlignLast { get; init; }
        public int? MarginLeft { get; init; }
        public int? MarginRight { get; init; }
        public int? TextIndent { get; init; }
        public int? MarginTop { get; init; }
        public int? MarginBottom { get; init; }
        public LineRule? Line { get; init; }
        public string? KeepWithNext { get; init; }
        public string? KeepTogether { get; init; }
        public int? Widows { get; init; }
        public int? Orphans { get; init; }
        public string? BreakBefore { get; init; }
        public string? BreakAfter { get; init; }
        public XElement? Tabs { get; init; }
        public string? WritingMode { get; init; }

        public ParaProps Apply(XElement? props)
        {
            if (props is null)
                return this;
            var result = this;
            if (Length(Attr(props, Fo + "margin")) is { } margin)
                result = result with { MarginLeft = margin, MarginRight = margin, MarginTop = margin, MarginBottom = margin };
            result = result with
            {
                Align = Attr(props, Fo + "text-align") ?? Align,
                AlignLast = Attr(props, Fo + "text-align-last") ?? AlignLast,
                MarginLeft = Length(Attr(props, Fo + "margin-left")) ?? result.MarginLeft,
                MarginRight = Length(Attr(props, Fo + "margin-right")) ?? result.MarginRight,
                TextIndent = Length(Attr(props, Fo + "text-indent")) ?? TextIndent,
                MarginTop = Length(Attr(props, Fo + "margin-top")) ?? result.MarginTop,
                MarginBottom = Length(Attr(props, Fo + "margin-bottom")) ?? result.MarginBottom,
                KeepWithNext = Attr(props, Fo + "keep-with-next") ?? KeepWithNext,
                KeepTogether = Attr(props, Fo + "keep-together") ?? KeepTogether,
                Widows = Int(Attr(props, Fo + "widows")) ?? Widows,
                Orphans = Int(Attr(props, Fo + "orphans")) ?? Orphans,
                BreakBefore = Attr(props, Fo + "break-before") ?? BreakBefore,
                BreakAfter = Attr(props, Fo + "break-after") ?? BreakAfter,
                Tabs = props.Element(Style + "tab-stops") ?? Tabs,
                WritingMode = Attr(props, Style + "writing-mode") is { } mode and not "page" ? mode : WritingMode,
            };
            // The three ways of giving a line height replace each other.
            if (Attr(props, Fo + "line-height") is { } height)
            {
                if (height == "normal")
                    result = result with { Line = new LineRule(LineKind.Proportional, 100) };
                else if (Percent(height) is { } percent)
                    result = result with { Line = new LineRule(LineKind.Proportional, Math.Clamp(percent, 1, 1000)) };
                else if (Length(height) is { } fixedHeight)
                    result = result with { Line = new LineRule(LineKind.Fixed, Math.Clamp(fixedHeight, 1, 1_000_000)) };
            }
            else if (Length(Attr(props, Style + "line-height-at-least")) is { } atLeast)
            {
                result = result with { Line = new LineRule(LineKind.AtLeast, Math.Clamp(atLeast, 0, 1_000_000)) };
            }
            else if (Length(Attr(props, Style + "line-spacing")) is { } leading)
            {
                result = result with { Line = new LineRule(LineKind.Leading, Math.Clamp(leading, -1_000_000, 1_000_000)) };
            }
            return result;
        }
    }

    // ───────────────────────── Text styles ─────────────────────────

    /// <summary>Character properties; null = not set by any style so far.</summary>
    private sealed record TextProps
    {
        public bool? Bold { get; init; }
        public bool? Italic { get; init; }
        public bool? Underline { get; init; }
        public bool? Strike { get; init; }

        /// <summary>Baseline shift in percent of the font size (positive raises); null = not set.</summary>
        public double? Position { get; init; }
        public double? Size { get; init; }
        public string? Font { get; init; }
        public string? EastAsianFont { get; init; }
        public string? Color { get; init; }

        /// <summary>Background colour; "" = explicitly none.</summary>
        public string? Shade { get; init; }

        /// <summary>Letter spacing in HWPUNIT (0 = normal).</summary>
        public int? LetterSpacing { get; init; }
        public int? Scale { get; init; }
        public bool Hidden { get; init; }

        public TextProps Apply(XElement? props, Dictionary<string, string> fontFaces)
        {
            if (props is null)
                return this;
            var size = Size;
            if (Attr(props, Fo + "font-size") is { } fontSize)
            {
                if (Percent(fontSize) is { } percent)
                    size = (size ?? 12) * Math.Clamp(percent, 1, 1000) / 100;
                else if (Length(fontSize) is { } length)
                    size = length / 100.0;
                size = size is { } s ? Math.Clamp(s, 1, 1638) : null;
            }
            var position = Position;
            if (Attr(props, Style + "text-position") is { } textPosition)
            {
                var first = textPosition.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                position = first switch
                {
                    "super" => 33,
                    "sub" => -33,
                    _ => Percent(first) ?? 0,
                };
            }
            string? FontOf(XName name, XName family) =>
                Attr(props, name) is { } face ? fontFaces.GetValueOrDefault(face) ?? face : FirstFamily(Attr(props, family));

            var underline = Attr(props, Style + "text-underline-style");
            var underlineType = Attr(props, Style + "text-underline-type");
            var strike = Attr(props, Style + "text-line-through-style");
            var strikeType = Attr(props, Style + "text-line-through-type");
            var color = Attr(props, Fo + "color");
            var background = Attr(props, Fo + "background-color");
            var spacing = Attr(props, Fo + "letter-spacing");
            return this with
            {
                Bold = Weight(Attr(props, Fo + "font-weight") ?? Attr(props, Style + "font-weight-asian")) ?? Bold,
                Italic = (Attr(props, Fo + "font-style") ?? Attr(props, Style + "font-style-asian")) is { } style ? style is "italic" or "oblique" : Italic,
                Underline = underlineType == "none" ? false : underline is not null ? underline != "none" : underlineType is not null ? underlineType != "none" : Underline,
                Strike = strikeType == "none" ? false : strike is not null ? strike != "none" : strikeType is not null ? strikeType != "none" : Strike,
                Position = position,
                Size = size,
                Font = FontOf(Style + "font-name", Fo + "font-family") ?? Font,
                EastAsianFont = FontOf(Style + "font-name-asian", Style + "font-family-asian") ?? EastAsianFont,
                Color = Attr(props, Style + "use-window-font-color") == "true" ? "#000000" : Odf.Color(color) ?? Color,
                Shade = background == "transparent" ? "" : Odf.Color(background) ?? Shade,
                LetterSpacing = spacing == "normal" ? 0 : Length(spacing) is { } letters ? Math.Clamp(letters, -100_000, 100_000) : LetterSpacing,
                Scale = Percent(Attr(props, Style + "text-scale")) is { } scale ? (int)Math.Clamp(Math.Round(scale), 1, 1000) : Scale,
                Hidden = Attr(props, Text + "display") is { } display ? display == "none" : Hidden,
            };
        }

        private static bool? Weight(string? weight) => weight switch
        {
            null => null,
            "bold" or "bolder" => true,
            _ => int.TryParse(weight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value >= 600 : false,
        };
    }

    /// <summary>The model's character format of resolved text properties.</summary>
    private static HCharFormat CharFormat(TextProps t)
    {
        var size = t.Size ?? 12;
        var position = t.Position ?? 0;
        var letterSpacing = t.LetterSpacing ?? 0;
        return new HCharFormat(
            Bold: t.Bold ?? false,
            Italic: t.Italic ?? false,
            Underline: t.Underline ?? false,
            Strike: t.Strike ?? false,
            Superscript: position > 0,
            Subscript: position < 0,
            Size: (int)Math.Round(size * 100),
            Font: t.Font,
            EastAsianFont: t.EastAsianFont,
            Color: t.Color ?? "#000000",
            Shade: t.Shade is { Length: > 0 } shade ? shade : null,
            // 한글's letter spacing is a percentage of the font size, ODF's a length.
            Spacing: letterSpacing == 0 ? 0 : Math.Clamp((int)Math.Round(letterSpacing / size), -50, 50),
            Layout: t.Scale is not null || letterSpacing != 0 || position != 0
                ? new HCharLayout(Scale: t.Scale, LetterSpacing: letterSpacing, Position: (int)Math.Round(position * size))
                : null);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static string? Attr(XElement? element, XName name) => (string?)element?.Attribute(name);

    private static int? Int(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>The first family of a font family list, without quotes ("'Liberation Serif', serif" → "Liberation Serif").</summary>
    private static string? FirstFamily(string? families)
    {
        if (string.IsNullOrWhiteSpace(families))
            return null;
        var first = families.Split(',')[0].Trim().Trim('\'', '"').Trim();
        return first.Length > 0 ? first : null;
    }
}
