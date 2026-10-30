// A paragraph as the layout reads it: the text of its inlines in one string, and for each piece of it the character
// format it is drawn with, then that format resolved per character into what the shaper and the drawing take
// (TextStyle): the font family of the character's slot, size, width, spacing, raise, colour and decorations.
//
// - Tabs are '\t', line breaks U+2028 (a mandatory break for the line breaker), links keep their target on the pieces
//   inside them, bookmarks their position. A list item's number or bullet starts the text, followed by a tab when the
//   paragraph has a hanging indent (the text then starts at the indent, as in 한글 and Word) and a space otherwise.
// - Objects (pictures, text boxes, shapes), note references and fields belong to later layout milestones and are left
//   out; an equation is drawn as its linear text.
// - 한글's language slots: each character takes the font, 장평, 자간, relative size and 글자 위치 of its slot
//   (FontSlots.Hwp); a slot without a font takes the format's Latin font (영문, 외국어, 사용자) or East Asian font
//   (한글, 한자, 일어, 기호). Word's run fonts: the character's run font (FontSlots.Word), its scale (w:w), absolute
//   letter spacing (w:spacing) and raise (w:position).

using System.Globalization;
using System.Text;
using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>A piece of a paragraph's text in one character format.</summary>
/// <param name="Link">The target of the link the piece is in, or null.</param>
internal readonly record struct TextSpan(int Start, int Length, HCharFormat Format, string? Link)
{
    public int End => Start + Length;
}

/// <summary>How a character is drawn, with every value resolved.</summary>
/// <param name="Family">Font family the document names; null for the language's default font.</param>
/// <param name="Size">Font size in points, relative size and super- / subscript applied.</param>
/// <param name="Scale">Glyph width (한글 장평, Word w:w): 1 = as designed.</param>
/// <param name="LetterSpacing">Space added after each character, in points (negative = tighter).</param>
/// <param name="Rise">Baseline shift in points, upwards.</param>
/// <param name="FontSize">The format's own size in points (what 한글's line height is measured from).</param>
/// <param name="Language">"ko", "ja", "zh-Hans", "zh-Hant" or "": picks fonts and language forms.</param>
/// <param name="SpaceWidth">Width of a space before <paramref name="Scale"/>, or null for the font's own.</param>
/// <param name="Kerning">The font's kerning applies.</param>
internal sealed record TextStyle(
    string? Family,
    FontStyle Style,
    float Size,
    float Scale,
    float LetterSpacing,
    float Rise,
    float FontSize,
    SKColor Color,
    SKColor? Shade,
    bool Underline,
    bool Strike,
    string Language,
    float? SpaceWidth = null,
    bool Kerning = true);

/// <summary>A paragraph's text with the character format of each piece.</summary>
internal sealed class ParagraphText
{
    /// <summary>A line break inside a paragraph (U+2028 LINE SEPARATOR).</summary>
    public const char LineSeparator = (char)0x2028;

    /// <summary>Size of text whose format leaves it open, in points (한글's default).</summary>
    public const float DefaultSize = 10;

    private ParagraphText(string text, List<TextSpan> spans, List<(int Position, string Name)> bookmarks)
    {
        Text = text;
        Spans = spans;
        Bookmarks = bookmarks;
    }

    public string Text { get; }

    /// <summary>Pieces in text order, covering the text without gaps.</summary>
    public IReadOnlyList<TextSpan> Spans { get; }

    /// <summary>Bookmarks by the text position they mark.</summary>
    public IReadOnlyList<(int Position, string Name)> Bookmarks { get; }

    /// <param name="marker">The list item's number or bullet, or null.</param>
    public static ParagraphText From(HParagraph paragraph, string? marker = null)
    {
        var text = new StringBuilder();
        var spans = new List<TextSpan>();
        var bookmarks = new List<(int, string)>();

        void Add(string piece, HCharFormat format, string? link)
        {
            if (piece.Length == 0)
                return;
            spans.Add(new TextSpan(text.Length, piece.Length, format, link));
            text.Append(piece);
        }

        void Inlines(IEnumerable<HInline> inlines, string? link)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case HText run:
                        // Paragraph and line feeds inside a run are line breaks; other controls are left out.
                        Add(Clean(run.Text), run.Format, link);
                        break;
                    case HTab tab:
                        Add("\t", tab.Format, link);
                        break;
                    case HLineBreak lineBreak:
                        Add(LineSeparator.ToString(), lineBreak.Format, link);
                        break;
                    case HLink nested:
                        Inlines(nested.Content, nested.Target);
                        break;
                    case HBookmark bookmark:
                        bookmarks.Add((text.Length, bookmark.Name));
                        break;
                }
            }
        }

        if (marker is { Length: > 0 })
        {
            var format = paragraph.Inlines.OfType<HText>().FirstOrDefault()?.Format ?? paragraph.MarkFormat;
            Add(marker + ((paragraph.Format.FirstLine ?? 0) < 0 ? "\t" : " "), format, null);
        }
        Inlines(paragraph.Inlines, null);
        return new ParagraphText(text.ToString(), spans, bookmarks);
    }

    private static string Clean(string text)
    {
        if (!text.Any(c => c < ' '))
            return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                continue;
            if (c is '\n' or '\r' or '\v')
                sb.Append(LineSeparator);
            else if (c == '\t' || c >= ' ')
                sb.Append(c);
        }
        return sb.ToString();
    }

    // ───────────────────────── Styles ─────────────────────────

    /// <summary>
    /// Splits a piece into runs of one style: by the character's 한글 language slot, or by Word run font when the
    /// format has Word's fonts.
    /// </summary>
    /// <param name="language">The paragraph's language ("ko", "ja", "zh-Hans", "zh-Hant", ""), for Han characters.</param>
    public List<(int Start, int Length, TextStyle Style)> Styles(TextSpan span, string language)
    {
        var format = span.Format;
        var result = new List<(int, int, TextStyle)>();
        if (format.Layout?.WordFonts is { } word)
        {
            foreach (var run in FontSlots.WordRuns(Text, span.Start, span.Length, FontSlots.Hint(word.Hint)))
                result.Add((run.Start, run.Length, WordStyle(format, word, run.Slot, language)));
        }
        else
        {
            foreach (var run in FontSlots.HwpRuns(Text, span.Start, span.Length))
                result.Add((run.Start, run.Length, HwpStyle(format, run.Slot, language)));
        }
        return result;
    }

    /// <summary>The style of a character in 한글 language slot <paramref name="slot"/>.</summary>
    internal static TextStyle HwpStyle(HCharFormat format, HLanguage slot, string language)
    {
        var own = format.Layout?.Slots?[slot] ?? default;
        var eastAsian = slot is HLanguage.Hangul or HLanguage.Hanja or HLanguage.Japanese or HLanguage.Symbol;
        var family = own.Font ?? (eastAsian ? format.EastAsianFont ?? format.Font : format.Font ?? format.EastAsianFont);
        var fontSize = (format.Size ?? DefaultSize * 100) / 100f;
        var size = fontSize * (own.RelativeSize ?? 100) / 100f;
        var scale = (own.Ratio ?? format.Layout?.Scale ?? 100) / 100f;
        var spacing = (own.Spacing ?? format.Spacing ?? 0) / 100f * size;
        // 글자 위치: percent of the size, negative raises.
        var rise = -(own.Offset ?? 0) / 100f * size + (format.Layout?.Position ?? 0) / 100f;
        var slotLanguage = slot switch
        {
            HLanguage.Hangul => "ko",
            HLanguage.Japanese => "ja",
            HLanguage.Hanja => language,
            _ => "",
        };
        return Finish(format, family, fontSize, size, scale, spacing, rise, slotLanguage);
    }

    /// <summary>The style of a character drawn with Word run font <paramref name="slot"/>.</summary>
    internal static TextStyle WordStyle(HCharFormat format, HWordFonts fonts, WordFontSlot slot, string language)
    {
        var family = fonts.Font(slot) ?? (slot == WordFontSlot.EastAsia ? format.EastAsianFont ?? format.Font : format.Font);
        var fontSize = (format.Size ?? DefaultSize * 100) / 100f;
        var scale = (format.Layout?.Scale ?? 100) / 100f;
        var spacing = format.Layout?.LetterSpacing is { } absolute ? absolute / 100f : (format.Spacing ?? 0) / 100f * fontSize;
        var rise = (format.Layout?.Position ?? 0) / 100f;
        return Finish(format, family, fontSize, fontSize, scale, spacing, rise, slot == WordFontSlot.EastAsia ? language : "");
    }

    private static TextStyle Finish(HCharFormat format, string? family, float fontSize, float size, float scale, float spacing, float rise, string language)
    {
        // Super- and subscript: two thirds of the size, raised a third of it or lowered a seventh.
        if (format.Superscript == true)
        {
            rise += size / 3;
            size *= 2f / 3;
        }
        else if (format.Subscript == true)
        {
            rise -= size / 7;
            size *= 2f / 3;
        }
        size = Math.Max(0.5f, size);
        return new TextStyle(
            family,
            FontStyle.From(format.Bold == true, format.Italic == true),
            size,
            scale > 0 ? scale : 1,
            spacing,
            rise,
            fontSize,
            Color(format.Color) ?? SKColors.Black,
            Color(format.Shade),
            format.Underline == true,
            format.Strike == true,
            language,
            // 한글 without "글꼴에 어울리는 빈칸": a space is half the size wide.
            format.Layout?.FontSpace == false ? size / 2 : null,
            format.Layout?.Kerning != false);
    }

    /// <summary>A "#RRGGBB" colour, or null for none or an unreadable one.</summary>
    internal static SKColor? Color(string? value) =>
        value is { Length: 7 } && value[0] == '#' && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;
}
