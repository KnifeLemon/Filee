// Resolves what a document leaves to the 한글 style (null format members = "inherit") into concrete values, so the
// layout engine sees every paragraph and run fully specified.
//
// The values are the ones the document gets when HwpxWriter writes it with the same template and a 한글 reader opens
// the file: a paragraph starts from its style's paragraph shape (Normal, "Heading n", or the footnote style inside
// notes), the members the model sets replace the style's, and list items get the indentation the writer gives them.
// Character formats work the same way on the style's character shape, including links (blue and underlined unless
// coloured) and the per-language slots the writer fills from Font / EastAsianFont / Spacing, and spaces as wide as the
// font makes them (the writer turns 한글's "글꼴에 어울리는 빈칸" on). Rendering detail the source set (HParaLayout,
// HCharLayout) wins over the style's.

using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Where a paragraph sits: notes use the template's footnote style as their plain paragraph style.</summary>
internal enum HStyleContext
{
    Body,
    Note,
}

/// <summary>Resolves inherited format members against an HWPX template's styles.</summary>
internal sealed class StyleResolver
{
    /// <summary>Indentation per list level the writer gives list items without their own (see HwpxWriter.ParaPr).</summary>
    private const int ListIndent = 2000;

    private readonly HwpxReader _shapes;
    private readonly HwpxWriter.TemplateStyles _styles;

    private StyleResolver(XElement head)
    {
        // As the writer writes the template's character shapes (HwpxWriter.Header): spaces as wide as the font makes
        // them, as in Word, not 한글's half a character.
        foreach (var shape in head.Descendants(head.Name.Namespace + "charPr"))
            shape.SetAttributeValue("useFontSpace", 1);
        _shapes = HwpxReader.ForHeader(head);
        _styles = HwpxWriter.FindStyles(head);
    }

    /// <summary>A resolver for the styles of <paramref name="template"/>; the built-in blank.hwpx when null.</summary>
    public static StyleResolver FromTemplate(Stream? template = null)
    {
        var stream = template ?? HwpxWriter.OpenBuiltInTemplate();
        try
        {
            using var package = DocumentPackage.Open(stream);
            var entry = package.GetEntry("Contents/header.xml") ?? throw new InvalidDataException("The template has no Contents/header.xml.");
            XDocument header;
            try
            {
                header = package.LoadXml(entry, preserveWhitespace: false);
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"The template's header.xml is not valid XML ({ex.Message}).", ex);
            }
            return new StyleResolver(header.Root ?? throw new InvalidDataException("header.xml has no root element."));
        }
        finally
        {
            if (template is null)
                stream.Dispose();
        }
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>The paragraph's format with every member set.</summary>
    public HParaFormat Paragraph(HParagraph paragraph, HStyleContext context = HStyleContext.Body)
    {
        var style = Style(paragraph, context);
        var shape = _shapes.ParagraphShape(style.ParaPrId);
        var format = paragraph.Format;
        var list = paragraph.List;
        var resolved = shape;

        if (!format.IsEmpty || list is not null)
        {
            // Margins go through 한글's storage like the writer writes them: a negative first-line indent ("intent")
            // keeps the first line at "left", so a style's hanging indent shifts what a model left margin means.
            var shapeIntent = shape.FirstLine ?? 0;
            var shapeLeft = (shape.Left ?? 0) + Math.Min(0, shapeIntent);
            var left = format.Left;
            var firstLine = format.FirstLine;
            if (list is not null && left is null)
            {
                left = (list.Level + 1) * ListIndent;
                firstLine ??= list.Numbered ? -ListIndent : 0;
            }
            var intent = firstLine ?? shapeIntent;
            var hwpLeft = left ?? shapeLeft;
            if (left is not null && firstLine is < 0)
                hwpLeft += firstLine.Value;

            resolved = new HParaFormat(
                Align: format.Align ?? shape.Align,
                Left: intent < 0 ? hwpLeft - intent : hwpLeft,
                Right: format.Right ?? shape.Right,
                FirstLine: intent,
                Before: format.Before ?? shape.Before,
                After: format.After ?? shape.After,
                LineSpacing: format.LineSpacing ?? shape.LineSpacing,
                KeepWithNext: format.KeepWithNext ?? shape.KeepWithNext,
                KeepLines: format.KeepLines ?? shape.KeepLines,
                WidowOrphan: format.WidowOrphan ?? shape.WidowOrphan,
                Tabs: format.Tabs is { Count: > 0 } ? format.Tabs : shape.Tabs);
        }

        // The source's own line rule wins; else the one its writer-side spacing stands for; else the style's.
        var layout = (format.Layout ?? new HParaLayout()).Over(shape.Layout);
        if (format.Layout?.LineHeight is null && format.LineSpacing is { } spacing)
            layout = layout with { LineHeight = HLineHeight.From(spacing) };
        return resolved with { RightToLeft = format.RightToLeft ?? false, Layout = layout };
    }

    // ───────────────────────── Characters ─────────────────────────

    /// <summary>The format of text in <paramref name="paragraph"/> with every member set.</summary>
    /// <param name="link">The text is inside a hyperlink (blue and underlined unless it has its own colour).</param>
    public HCharFormat Character(HCharFormat format, HParagraph paragraph, HStyleContext context = HStyleContext.Body, bool link = false)
    {
        var shape = _shapes.CharacterShape(Style(paragraph, context).CharPrId);
        link = link && format.Color is null;
        if (format.IsEmpty && !link)
            return shape with { Layout = format.Layout is { } own ? own.Over(shape.Layout) : shape.Layout };

        var slots = shape.Layout?.Slots ?? HLanguageSlots.All(default);
        string? latin = shape.Font, eastAsian = shape.EastAsianFont;
        if (format.Font is not null || format.EastAsianFont is not null)
        {
            // As the writer fills the slots: the East Asian font for Hangul, Hanja, Japanese and symbols (CJK
            // punctuation), the Latin font for the rest, each standing in for the other when missing.
            foreach (var language in HLanguageSlots.Languages)
            {
                var face = language is HLanguage.Hangul or HLanguage.Hanja or HLanguage.Japanese or HLanguage.Symbol
                    ? format.EastAsianFont ?? format.Font
                    : format.Font ?? format.EastAsianFont;
                slots = slots.With(language, slots[language] with { Font = face });
            }
            (latin, eastAsian) = (slots.Latin.Font, slots.Hangul.Font);
        }
        if (format.Spacing is { } spacing)
        {
            foreach (var language in HLanguageSlots.Languages)
                slots = slots.With(language, slots[language] with { Spacing = spacing });
        }

        var (superscript, subscript) = format switch
        {
            { Superscript: true } => (true, false),
            { Subscript: true } => (false, true),
            { Superscript: false } or { Subscript: false } => (false, false),
            _ => (shape.Superscript, shape.Subscript),
        };
        var resolved = new HCharFormat(
            Bold: format.Bold ?? shape.Bold,
            Italic: format.Italic ?? shape.Italic,
            Underline: link || format.Underline is not null ? link || format.Underline == true : shape.Underline,
            Strike: format.Strike ?? shape.Strike,
            Superscript: superscript,
            Subscript: subscript,
            Size: format.Size is { } size ? Math.Clamp(size, 100, 409600) : shape.Size,
            Font: latin,
            EastAsianFont: eastAsian,
            Color: link ? "#0000FF" : format.Color ?? shape.Color,
            Shade: format.Shade ?? shape.Shade,
            Spacing: format.Spacing ?? shape.Spacing);
        var styleLayout = (shape.Layout ?? new HCharLayout()) with { Slots = slots };
        return resolved with { Layout = format.Layout is { } layout ? layout.Over(styleLayout) : styleLayout };
    }

    /// <summary>The format of the paragraph mark (the height of an empty paragraph).</summary>
    public HCharFormat Mark(HParagraph paragraph, HStyleContext context = HStyleContext.Body) =>
        Character(paragraph.MarkFormat, paragraph, context);

    /// <summary>The template style a paragraph is written with (see HwpxWriter.Paragraph and HwpxWriter.Note).</summary>
    private (string StyleId, string ParaPrId, string CharPrId) Style(HParagraph paragraph, HStyleContext context)
    {
        if (paragraph.HeadingLevel > 0 && _styles.Headings.TryGetValue(paragraph.HeadingLevel, out var heading))
            return heading;
        return context == HStyleContext.Note && _styles.Footnote is { } footnote ? footnote : _styles.Normal;
    }
}
