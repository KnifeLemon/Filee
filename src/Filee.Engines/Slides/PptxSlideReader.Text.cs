// Text of PPTX shapes and table cells into the document model's paragraphs, for the layout engine.
//
// Every paragraph and run property is looked up along PowerPoint's chain: the paragraph or run itself → the shape's
// list style → the layout placeholder's → the master placeholder's → [the shape style's font and colour] → the master's
// title / body / other text style → the presentation's default text style. Body properties (insets, anchoring,
// wrapping, columns, vertical text, autofit) come from the shape's bodyPr, then the placeholders'.
//
// - Sizes: "shrink text on overflow" (normAutofit) stores the scale and the line spacing reduction PowerPoint applied;
//   both are applied here, so the text takes the room it took in PowerPoint.
// - Line spacing: a percentage is a multiple of the font's own line height (the layout engine's Word rule, from the
//   fonts' metrics as PowerPoint measures), points are exact. Space before and after in points, or a percentage of a
//   line of the paragraph's first size.
// - Bullets and numbers become the paragraph's first text with the bullet's own font, colour and size, followed by a
//   tab to the hanging indent (marL + indent). Numbers count per level as PowerPoint counts them.
// - Fonts: the run's Latin, East Asian and complex-script typefaces are the layout engine's Word fonts (FontSlots
//   picks by character); theme references resolve against the theme's font scheme, an empty East Asian font to the
//   theme's font for the run's language (Hang, Jpan, Hans, Hant). Symbol fonts become the Unicode characters they show.
// - Hyperlinks take the theme's hyperlink colour and are underlined; slide number fields show the slide's number.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using SkiaSharp;

namespace Filee.Engines.Slides;

internal sealed partial class PptxSlideReader
{
    /// <summary>PowerPoint's default text size, 18 pt.</summary>
    private const int DefaultSize = 1800;

    /// <summary>The theme's fonts: major (headings) and minor (body), each Latin, East Asian, complex script and per script.</summary>
    private sealed record ThemeFonts(ThemeFontSet Major, ThemeFontSet Minor)
    {
        public static ThemeFonts From(XElement? theme)
        {
            var scheme = theme?.Descendants(A + "fontScheme").FirstOrDefault();
            return new ThemeFonts(ThemeFontSet.From(scheme?.Element(A + "majorFont")), ThemeFontSet.From(scheme?.Element(A + "minorFont")));
        }
    }

    private sealed record ThemeFontSet(string? Latin, string? EastAsian, string? ComplexScript, IReadOnlyDictionary<string, string> Scripts)
    {
        public static ThemeFontSet From(XElement? set)
        {
            string? Face(string name) => NonEmpty((string?)set?.Element(A + name)?.Attribute("typeface"));
            var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var font in set?.Elements(A + "font") ?? [])
            {
                if ((string?)font.Attribute("script") is { } script && NonEmpty((string?)font.Attribute("typeface")) is { } face)
                    scripts[script] = face;
            }
            return new ThemeFontSet(Face("latin"), Face("ea"), Face("cs"), scripts);
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The level styles for one paragraph level, before and after the shape style's font and colour.</summary>
    private sealed record LevelStyles(List<XElement> Specific, List<XElement> General);

    // ───────────────────────── Text frames ─────────────────────────

    private SlideTextFrame? TextFrame(SlideContext context, string part, XElement body, XElement? ph, XElement? layoutPh, XElement? masterPh,
        XElement? fontRef)
    {
        if (!body.Elements(A + "p").Any(p => p.Elements().Any(e => e.Name.LocalName is "r" or "fld" or "br")))
            return null;
        var bodies = new[] { body, Body(layoutPh), Body(masterPh) }.OfType<XElement>().ToList();
        var properties = bodies.Select(b => b.Element(A + "bodyPr")).OfType<XElement>().ToList();
        string? Attribute(string name) => properties.Select(p => (string?)p.Attribute(name)).FirstOrDefault(v => v is not null);
        float Inset(string name, double fallback) =>
            Points(double.TryParse(Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? Math.Clamp(v, 0, 51206400) : fallback);

        var frame = new SlideTextFrame
        {
            InsetLeft = Inset("lIns", 91440),
            InsetTop = Inset("tIns", 45720),
            InsetRight = Inset("rIns", 91440),
            InsetBottom = Inset("bIns", 45720),
            Anchor = Attribute("anchor") switch
            {
                "ctr" => TextAnchor.Middle,
                "b" => TextAnchor.Bottom,
                _ => TextAnchor.Top,
            },
            AnchorCenter = Attribute("anchorCtr") is "1" or "true",
            Wrap = Attribute("wrap") != "none",
            Direction = Attribute("vert") switch
            {
                "vert" or "eaVert" or "wordArtVert" or "mongolianVert" => TextDirection.Down,
                "vert270" => TextDirection.Up,
                _ => TextDirection.Horizontal,
            },
            Columns = Math.Clamp(int.TryParse(Attribute("numCol"), out var columns) ? columns : 1, 1, 16),
            ColumnGap = Inset("spcCol", 0),
            Rotation = (float)((double.TryParse(Attribute("rot"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rot) && double.IsFinite(rot) ? rot : 0) / 60000 % 360),
        };

        // Autofit: the shape's own setting, else the placeholders'.
        var autofit = properties.Select(p => p.Elements().FirstOrDefault(e => e.Name.LocalName is "normAutofit" or "spAutoFit" or "noAutofit")).FirstOrDefault(e => e is not null);
        var fontScale = autofit?.Name.LocalName == "normAutofit" ? Math.Clamp(Number(autofit.Attribute("fontScale"), 100000) / 100000, 0.01, 1) : 1;
        var spacingReduction = autofit?.Name.LocalName == "normAutofit" ? Math.Clamp(Number(autofit.Attribute("lnSpcReduction")) / 100000, 0, 0.9) : 0;

        var counters = new int[10];
        var numbered = new bool[10];
        var first = true;
        foreach (var p in body.Elements(A + "p"))
        {
            _budget.Paragraph();
            var paragraph = Paragraph(context, part, p, ph, bodies, fontRef, fontScale, spacingReduction, counters, numbered, first);
            frame.Paragraphs.Add(paragraph);
            first = false;
        }
        return frame;
    }

    private static XElement? Body(XElement? placeholder) => placeholder?.Element(P + "txBody") ?? placeholder?.Element(Dsp + "txBody");

    private LevelStyles Styles(SlideContext context, XElement? ph, List<XElement> bodies, int level)
    {
        var name = A + $"lvl{level}pPr";
        var specific = bodies.Select(b => b.Element(A + "lstStyle")?.Element(name)).OfType<XElement>().ToList();
        var general = new List<XElement>();
        // Placeholders take the master's title, body or other text style; other text only the presentation's default.
        if (ph is not null)
        {
            var styles = context.Master?.Element(P + "txStyles");
            var style = Kind((string?)ph.Attribute("type")) switch
            {
                "title" => styles?.Element(P + "titleStyle"),
                "body" => styles?.Element(P + "bodyStyle"),
                _ => styles?.Element(P + "otherStyle"),
            };
            if (style?.Element(name) is { } fromMaster)
                general.Add(fromMaster);
        }
        if (_defaultTextStyle?.Element(name) is { } fromPresentation)
            general.Add(fromPresentation);
        return new LevelStyles(specific, general);
    }

    private HParagraph Paragraph(SlideContext context, string part, XElement p, XElement? ph, List<XElement> bodies, XElement? fontRef,
        double fontScale, double spacingReduction, int[] counters, bool[] numbered, bool first)
    {
        var pPr = p.Element(A + "pPr");
        var level = Math.Clamp((int)Number(pPr?.Attribute("lvl")), 0, 8);
        var styles = Styles(context, ph, bodies, level + 1);
        var chain = new List<XElement>();
        if (pPr is not null)
            chain.Add(pPr);
        chain.AddRange(styles.Specific);
        chain.AddRange(styles.General);
        string? Attribute(string name) => chain.Select(e => (string?)e.Attribute(name)).FirstOrDefault(v => v is not null);
        XElement? Child(params string[] names) => chain.Select(e => e.Elements().FirstOrDefault(c => names.Contains(c.Name.LocalName))).FirstOrDefault(c => c is not null);

        var runs = p.Elements().Where(e => e.Name.LocalName is "r" or "br" or "fld").ToList();
        var firstRun = runs.FirstOrDefault(r => r.Name.LocalName != "br");
        var markFormat = Format(context, p.Element(A + "endParaRPr") is { } end && firstRun is null ? end : firstRun?.Element(A + "rPr") ?? p.Element(A + "endParaRPr"),
            pPr, styles, fontRef, fontScale, "");
        var size = markFormat.Size ?? DefaultSize;

        var lineSpacing = Child("lnSpc");
        var spacing = HLineHeightRule.Multiple;
        var spacingValue = 240;
        if (lineSpacing?.Element(A + "spcPts") is { } points)
        {
            spacing = HLineHeightRule.Exact;
            spacingValue = (int)Math.Clamp(Number(points.Attribute("val")) * fontScale, 1, 158400);
        }
        else if (lineSpacing?.Element(A + "spcPct") is { } percent)
        {
            spacingValue = (int)Math.Clamp(Math.Round((Number(percent.Attribute("val"), 100000) / 100000 - spacingReduction) * 240), 24, 24000);
        }
        else if (spacingReduction > 0)
        {
            spacingValue = (int)Math.Round((1 - spacingReduction) * 240);
        }
        var rightToLeft = Attribute("rtl") is "1" or "true";
        var align = Attribute("algn") switch
        {
            "ctr" => HAlign.Center,
            "r" => HAlign.Right,
            "just" or "justLow" => HAlign.Justify,
            "dist" or "thaiDist" => HAlign.Distribute,
            _ => HAlign.Left,
        };
        // PowerPoint's left and right are the frame's sides; the layout's are a right-to-left paragraph's start and end.
        if (rightToLeft && align is HAlign.Left or HAlign.Right)
            align = align == HAlign.Left ? HAlign.Right : HAlign.Left;
        var marL = Number(chain.Select(e => e.Attribute("marL")).FirstOrDefault(a => a is not null));
        var indent = Number(chain.Select(e => e.Attribute("indent")).FirstOrDefault(a => a is not null));
        var paragraph = new HParagraph
        {
            Format = new HParaFormat(
                Align: align,
                Left: Hwp(marL),
                FirstLine: Hwp(indent),
                // PowerPoint leaves out the space before a frame's first paragraph.
                Before: first ? 0 : Space(Child("spcBef"), size, fontScale),
                After: Space(Child("spcAft"), size, fontScale),
                LineSpacing: new HLineSpacing(spacing == HLineHeightRule.Exact ? HLineSpacingKind.Fixed : HLineSpacingKind.Percent,
                    spacing == HLineHeightRule.Exact ? spacingValue : spacingValue * 120 / 240),
                Tabs: Tabs(Child("tabLst")),
                RightToLeft: rightToLeft ? true : null,
                Layout: new HParaLayout(LineHeight: new HLineHeight(spacing, spacingValue))),
            MarkFormat = markFormat,
        };

        var hasText = runs.Any(r => (r.Element(A + "t")?.Value.Length ?? 0) > 0);
        Bullet(context, paragraph, Child("buNone", "buChar", "buAutoNum", "buBlip"), Child("buClr", "buClrTx"), Child("buSzPct", "buSzPts", "buSzTx"),
            Child("buFont", "buFontTx"), markFormat, hasText, level, indent, counters, numbered);

        foreach (var run in runs)
        {
            var rPr = run.Element(A + "rPr");
            var text = run.Name.LocalName switch
            {
                "br" => null,
                "fld" when (string?)run.Attribute("type") == "slidenum" => context.Number.ToString(CultureInfo.InvariantCulture),
                _ => run.Element(A + "t")?.Value ?? "",
            };
            var format = Format(context, rPr, pPr, styles, fontRef, fontScale, text ?? "");
            if (text is null)
            {
                paragraph.Inlines.Add(new HLineBreak(format));
                continue;
            }
            if (text.Length == 0)
                continue;
            text = Capitals(text, rPr, pPr, styles);
            var target = _package.ExternalTarget(part, (string?)rPr?.Element(A + "hlinkClick")?.Attribute(R + "id"));
            if (target is null)
            {
                paragraph.Inlines.AddRange(SymbolFonts.Runs(text, format));
                continue;
            }
            var link = new HLink(target);
            link.Content.AddRange(SymbolFonts.Runs(text, format with { Color = Hex(context.Colors.Scheme("hlink")), Underline = true }));
            paragraph.Inlines.Add(link);
        }
        return paragraph;
    }

    /// <summary>EMU → HWPUNIT (1/100 pt).</summary>
    private static int Hwp(double emu) => (int)Math.Clamp(Math.Round(emu / 127), -10_000_000, 10_000_000);

    private static int Space(XElement? spacing, int size, double fontScale)
    {
        if (spacing?.Element(A + "spcPts") is { } points)
            return (int)Math.Clamp(Number(points.Attribute("val")) * fontScale, 0, 158400);
        if (spacing?.Element(A + "spcPct") is { } percent)
            return (int)Math.Clamp(Number(percent.Attribute("val")) / 100000 * size * 1.2, 0, 158400);
        return 0;
    }

    private static IReadOnlyList<HTabStop>? Tabs(XElement? list)
    {
        if (list is null)
            return null;
        return [.. list.Elements(A + "tab").Take(64).Select(tab => new HTabStop(Hwp(Number(tab.Attribute("pos"))), (string?)tab.Attribute("algn") switch
        {
            "r" => HTabKind.Right,
            "ctr" => HTabKind.Center,
            "dec" => HTabKind.Decimal,
            _ => HTabKind.Left,
        }, "NONE")).OrderBy(t => t.Position)];
    }

    /// <summary>Adds a paragraph's bullet or number with the tab after it.</summary>
    private void Bullet(SlideContext context, HParagraph paragraph, XElement? kind, XElement? color, XElement? size, XElement? font,
        HCharFormat text, bool hasText, int level, double indent, int[] counters, bool[] numbered)
    {
        for (var deeper = level + 1; deeper < counters.Length; deeper++)
        {
            counters[deeper] = 0;
            numbered[deeper] = false;
        }
        if (!hasText || kind is null || kind.Name.LocalName == "buNone")
        {
            if (hasText)
                numbered[level] = false;
            return;
        }
        string label;
        var bulletFont = font?.Name.LocalName == "buFont" ? Typeface(context, (string?)font.Attribute("typeface"), null) : null;
        switch (kind.Name.LocalName)
        {
            case "buAutoNum":
                var start = Math.Clamp((int)Number(kind.Attribute("startAt"), 1), 1, 32767);
                counters[level] = numbered[level] ? counters[level] + 1 : start;
                numbered[level] = true;
                label = PptxReader.Number((string?)kind.Attribute("type") ?? "arabicPeriod", counters[level]);
                bulletFont = null;
                break;
            case "buChar":
                numbered[level] = false;
                var character = (string?)kind.Attribute("char") is { Length: > 0 } c ? c : "•";
                if (SymbolFonts.Map(bulletFont, character) is { } mapped)
                {
                    character = mapped;
                    bulletFont = null;
                }
                label = character;
                break;
            default:
                numbered[level] = false;
                label = "•";
                bulletFont = null;
                break;
        }
        var bulletSize = text.Size ?? DefaultSize;
        if (size?.Name.LocalName == "buSzPct")
            bulletSize = (int)Math.Clamp(bulletSize * Number(size.Attribute("val"), 100000) / 100000, 100, 400000);
        else if (size?.Name.LocalName == "buSzPts")
            bulletSize = (int)Math.Clamp(Number(size.Attribute("val"), bulletSize), 100, 400000);
        var bulletColor = color?.Name.LocalName == "buClr" && context.Colors.Color(color) is { } c2 ? Hex(c2) : text.Color;
        var format = new HCharFormat(Size: bulletSize, Color: bulletColor, Font: bulletFont ?? text.Font, EastAsianFont: bulletFont ?? text.EastAsianFont,
            Layout: bulletFont is null ? text.Layout : new HCharLayout(WordFonts: new HWordFonts(bulletFont, bulletFont, bulletFont, bulletFont)));
        paragraph.Inlines.Add(new HText(label, format));
        // A hanging bullet is followed by a tab to the text's indent; one without a hanging indent by a space.
        paragraph.Inlines.Add(indent < 0 ? new HTab(format) : new HText(" ", format));
    }

    /// <summary>The character format of a run (or an empty paragraph's mark).</summary>
    private HCharFormat Format(SlideContext context, XElement? rPr, XElement? pPr, LevelStyles styles, XElement? fontRef, double fontScale, string text)
    {
        // Run → paragraph default → specific level styles → [shape style] → general level styles.
        var chain = new List<XElement>();
        if (rPr is not null)
            chain.Add(rPr);
        if (pPr?.Element(A + "defRPr") is { } paragraphDefault)
            chain.Add(paragraphDefault);
        chain.AddRange(styles.Specific.Select(s => s.Element(A + "defRPr")).OfType<XElement>());
        var specific = chain.Count;
        chain.AddRange(styles.General.Select(s => s.Element(A + "defRPr")).OfType<XElement>());
        string? Attribute(string name) => chain.Select(e => (string?)e.Attribute(name)).FirstOrDefault(v => v is not null);

        SKColor? color = null;
        for (var i = 0; i <= chain.Count && color is null; i++)
        {
            if (i == specific && fontRef is not null && context.Colors.Color(fontRef) is { } styleColor)
            {
                color = styleColor;
                break;
            }
            if (i == chain.Count)
                break;
            var fill = chain[i].Elements().FirstOrDefault(e => e.Name.LocalName is "solidFill" or "gradFill" or "noFill");
            color = fill?.Name.LocalName switch
            {
                "solidFill" => context.Colors.Color(fill),
                "gradFill" => context.Colors.Color(fill.Element(A + "gsLst")?.Element(A + "gs")),
                "noFill" => SKColors.Transparent,
                _ => null,
            };
        }
        color ??= context.Colors.Scheme("tx1");

        // Typefaces: run chain (specific), the shape style's font (fontRef idx="major" / "minor"), general styles.
        var styleFont = (string?)fontRef?.Attribute("idx") is "major" ? "+mj" : fontRef is not null ? "+mn" : null;
        string? Face(string element)
        {
            for (var i = 0; i < chain.Count; i++)
            {
                if (i == specific && styleFont is not null)
                    return $"{styleFont}-{element switch { "latin" => "lt", "ea" => "ea", _ => "cs" }}";
                if (NonEmpty((string?)chain[i].Element(A + element)?.Attribute("typeface")) is { } face)
                    return face;
            }
            return styleFont is not null && specific == chain.Count ? $"{styleFont}-{element switch { "latin" => "lt", "ea" => "ea", _ => "cs" }}" : null;
        }
        var language = Attribute("lang");
        var alternate = Attribute("altLang");
        var latin = Typeface(context, Face("latin"), null) ?? context.Fonts.Minor.Latin;
        var eastAsian = Typeface(context, Face("ea"), Script(language, alternate, text)) ?? ScriptFont(context.Fonts.Minor, Script(language, alternate, text));
        var complex = Typeface(context, Face("cs"), null) ?? context.Fonts.Minor.ComplexScript;

        var sizeValue = double.TryParse(Attribute("sz"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sz) && double.IsFinite(sz) ? sz : DefaultSize;
        var size = (int)Math.Clamp(Math.Round(sizeValue * fontScale), 100, 400000);
        var baseline = Number(chain.Select(e => e.Attribute("baseline")).FirstOrDefault(a => a is not null));
        var spacing = Number(chain.Select(e => e.Attribute("spc")).FirstOrDefault(a => a is not null)) * fontScale;
        var highlight = chain.Select(e => e.Element(A + "highlight")).FirstOrDefault(e => e is not null);
        return new HCharFormat(
            Bold: Flag(chain.Select(e => e.Attribute("b")).FirstOrDefault(a => a is not null)) ? true : null,
            Italic: Flag(chain.Select(e => e.Attribute("i")).FirstOrDefault(a => a is not null)) ? true : null,
            Underline: Attribute("u") is { } u && u != "none" ? true : null,
            Strike: Attribute("strike") is { } s && s != "noStrike" ? true : null,
            Superscript: baseline > 0 ? true : null,
            Subscript: baseline < 0 ? true : null,
            Size: size,
            Font: latin,
            EastAsianFont: eastAsian,
            Color: color.Value.Alpha == 0 ? null : Hex(color.Value),
            Shade: highlight is not null && context.Colors.Color(highlight) is { } shade ? Hex(shade) : null,
            Layout: new HCharLayout(WordFonts: new HWordFonts(latin, latin, eastAsian, complex),
                LetterSpacing: spacing != 0 ? (int)Math.Clamp(spacing, -400000, 400000) : null));
    }

    /// <summary>All capitals or small capitals (cap="all" / "small") as upper-case text.</summary>
    private static string Capitals(string text, XElement? rPr, XElement? pPr, LevelStyles styles)
    {
        var cap = new[] { rPr, pPr?.Element(A + "defRPr") }.Concat(styles.Specific.Concat(styles.General).Select(s => s.Element(A + "defRPr")))
            .Select(e => (string?)e?.Attribute("cap")).FirstOrDefault(v => v is not null);
        return cap is "all" or "small" ? text.ToUpperInvariant() : text;
    }

    /// <summary>A typeface name with theme references (+mj-lt, +mn-ea, ...) resolved.</summary>
    private static string? Typeface(SlideContext context, string? face, string? script)
    {
        if (NonEmpty(face) is not { } name)
            return null;
        if (!name.StartsWith('+'))
            return name;
        var set = name.StartsWith("+mj", StringComparison.Ordinal) ? context.Fonts.Major : context.Fonts.Minor;
        return name[^2..] switch
        {
            "lt" => set.Latin,
            "ea" => set.EastAsian ?? ScriptFont(set, script),
            "cs" => set.ComplexScript,
            _ => null,
        };
    }

    private static string? ScriptFont(ThemeFontSet set, string? script) =>
        script is not null && set.Scripts.TryGetValue(script, out var face) ? face : set.EastAsian;

    /// <summary>The theme script tag for East Asian text: by the run's language, else by the text's own letters.</summary>
    private static string? Script(string? language, string? alternate, string text)
    {
        foreach (var tag in new[] { language, alternate })
        {
            var script = tag switch
            {
                null => null,
                _ when tag.StartsWith("ko", StringComparison.OrdinalIgnoreCase) => "Hang",
                _ when tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase) => "Jpan",
                _ when tag.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) || tag.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) => "Hant",
                _ when tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) => "Hans",
                _ => null,
            };
            if (script is not null)
                return script;
        }
        foreach (var c in text)
        {
            if (c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏')
                return "Hang";
            if (c is >= '぀' and <= 'ヿ')
                return "Jpan";
            if (c is >= '一' and <= '鿿')
                return "Hans";
        }
        return null;
    }

    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}
