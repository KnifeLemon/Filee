// Document body of an RTF file: paragraphs (\pard ... \par), their character formatting, links, bookmarks, fields,
// notes and tables.
//
// RTF paragraphs carry all of their formatting: after \pard\plain each one repeats what its style has (the heading
// styles' bold and size, Normal's 10 pt and line spacing), then what the model sets. A run is a group whose control
// words are only those that differ from the paragraph mark's, so the run's formatting ends with its group. Text is
// split where it changes from Latin to East Asian script, each part in the font of its slot (RTF has one \f at a time).
//
// A table is a run of rows: \trowd with the row's cell definitions (\cellx at each cell's right edge), then the
// cells' paragraphs (\intbl), each cell ending with \cell and the row with \row. Cells spanning columns are \clmgf
// with \clmrg cells after them, cells spanning rows \clvmgf with \clvmrg cells below, so every row has a cell at each
// edge of the grid. Tables in cells are nested tables (\itapN, \nestcell, \nestrow with their \nesttableprops).

using System.Text;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class RtfWriter
{
    /// <summary>What is being written: notes are only allowed in the body (and its tables).</summary>
    private enum Story
    {
        Body,
        Note,
        Header,
        TextBox,
    }

    /// <summary>How the last paragraph of a block list ends: with the story (no mark) or the cell (\cell, \nestcell).</summary>
    private enum BlockEnd
    {
        Story,
        Cell,
    }

    /// <summary>Width (HWPUNIT) available to the content being written: column, table cell or text box.</summary>
    private int _width = DefaultPage.TextWidth;

    // ───────────────────────── Blocks ─────────────────────────

    /// <param name="depth">Tables around the blocks (0 outside a table, 1 in a cell of a table in the text ...).</param>
    private void Blocks(IReadOnlyList<HBlock> blocks, Story story, int depth, BlockEnd end)
    {
        // A table's caption is its paragraphs after it.
        if (blocks.Any(b => b is HTable { Caption.Count: > 0 }))
            blocks = blocks.SelectMany(b => b is HTable { Caption.Count: > 0 } t ? t.Caption.Prepend(b) : [b]).ToList();
        for (var i = 0; i < blocks.Count; i++)
        {
            _token.ThrowIfCancellationRequested();
            var last = i == blocks.Count - 1;
            switch (blocks[i])
            {
                case HParagraph paragraph:
                    var mark = !last ? "\\par" : end == BlockEnd.Cell ? CellEnd(depth) : "";
                    Paragraph(paragraph, story, depth, mark);
                    break;
                case HTable table:
                    // Two tables in a row would become one: a paragraph keeps them apart.
                    if (i > 0 && blocks[i - 1] is HTable)
                        EmptyParagraph(depth, "\\par");
                    Table(table, story, depth + 1);
                    if (last)
                        EmptyParagraph(depth, end == BlockEnd.Cell ? CellEnd(depth) : "");
                    break;
            }
        }
        if (blocks.Count == 0 && end == BlockEnd.Cell)
            EmptyParagraph(depth, CellEnd(depth));
    }

    private static string CellEnd(int depth) => depth > 1 ? "\\nestcell" : "\\cell";

    /// <summary>A paragraph without text (at a table's end it also resets the paragraph properties the table had).</summary>
    private void EmptyParagraph(int depth, string mark)
    {
        _out.Control("\\pard\\plain" + InTable(depth) + mark);
        _out.Line();
    }

    private static string InTable(int depth) => depth == 0 ? "" : depth == 1 ? "\\intbl" : $"\\intbl\\itap{Number(depth)}";

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>The formatting a paragraph's style gives it, which the paragraph repeats.</summary>
    private readonly record struct StyleBase(int Style, bool Bold, int HalfPoints, int Before, int After, HLineSpacing Line, bool Keep,
        IReadOnlyList<HTabStop>? Tabs);

    /// <summary>Normal's line spacing: 160 % of the font size in 한글's terms (Word's 1.23 lines, \sl295\slmult1).</summary>
    private static readonly HLineSpacing NormalLine = new(HLineSpacingKind.Percent, 160);

    /// <summary>Single lines (headers, footers and notes, as the DOCX writer's styles).</summary>
    private static readonly HLineSpacing SingleLine = new(HLineSpacingKind.Percent, 130);

    /// <summary>The tab stops of the DOCX writer's header and footer styles: centre and right of an A4 text width.</summary>
    private static readonly HTabStop[] HeaderTabs = [new(45130, HTabKind.Center, "NONE"), new(90260, HTabKind.Right, "NONE")];

    private static StyleBase BaseOf(HParagraph paragraph, Story story)
    {
        if (paragraph.HeadingLevel is >= 1 and <= 9 and var level)
            return new StyleBase(level, true, HeadingSizes[level - 1], level <= 2 ? 1200 : 800, 400, NormalLine, true, null);
        return story switch
        {
            Story.Note => new StyleBase(0, false, 18, 0, 0, SingleLine, false, null),
            Story.Header => new StyleBase(0, false, DefaultHalfPoints, 0, 0, SingleLine, false, HeaderTabs),
            _ => new StyleBase(0, false, DefaultHalfPoints, 0, 0, NormalLine, false, null),
        };
    }

    /// <param name="mark">What ends the paragraph: \par, \cell, \nestcell, or nothing for the last one of its story.</param>
    private void Paragraph(HParagraph paragraph, Story story, int depth, string mark)
    {
        var o = _out;
        var style = BaseOf(paragraph, story);
        o.Control("\\pard\\plain" + ParagraphProperties(paragraph, style, depth));

        // The paragraph mark's formatting is the paragraph's: runs change only what differs from it.
        var markChars = Resolve(paragraph.MarkFormat, style);
        o.Control(Difference(PlainChars, markChars) + $"\\f{Number(FontIndex(markChars.Latin, 0))}");
        var state = new RunState(markChars, FontIndex(markChars.Latin, 0));
        if (paragraph.ColumnBreakBefore)
            o.Control("\\column");
        if (story == Story.Note && _noteNumber)
        {
            _noteNumber = false;
            // The note's own number first (the model's note numbers are where the source had them; they are left out).
            o.Open();
            o.Control("\\super\\chftn");
            o.Close();
            o.Raw(" ");
        }
        Inlines(paragraph.Inlines, state, style, story, depth);
        o.Control(mark);
        o.Line();
    }

    /// <summary>The note being written still needs its number (\chftn) in front of its first paragraph.</summary>
    private bool _noteNumber;

    /// <summary>Paragraph control words after \pard: style, table, list, alignment, indents, spacing, keeps, tabs, borders.</summary>
    private string ParagraphProperties(HParagraph paragraph, StyleBase style, int depth)
    {
        var f = paragraph.Format;
        var sb = new StringBuilder();
        if (style.Style > 0)
            sb.Append($"\\s{Number(style.Style)}\\outlinelevel{Number(style.Style - 1)}");
        sb.Append(InTable(depth));
        if (paragraph.List is { Numbered: true } list)
        {
            sb.Append($"\\ls{Number(ListNumber(list.Numbering))}\\ilvl{Number(Math.Clamp(list.Level, 0, 8))}");
            KeepMarker(list, style);
        }

        // Right to left, the model's Align, Left and Right name the start and end side; RTF's \ql, \li ... are the
        // left and right ones, and \lin / \rin the start and end.
        var rtl = f.RightToLeft == true;
        if (rtl)
            sb.Append("\\rtlpar");
        var align = f.Align ?? HAlign.Left;
        if (rtl && align is HAlign.Left or HAlign.Right)
            align = align == HAlign.Left ? HAlign.Right : HAlign.Left;
        sb.Append(align switch
        {
            HAlign.Center => "\\qc",
            HAlign.Right => "\\qr",
            HAlign.Justify => "\\qj",
            HAlign.Distribute => "\\qd",
            _ => "\\ql",
        });

        var (left, firstLine) = (f.Left, f.FirstLine);
        if (paragraph.List is { } item && left is null)
        {
            // List paragraphs take their level's indent; continuation paragraphs line up with the item's text.
            left = ListIndent(item.Level) * 5;
            firstLine ??= item.Numbered ? -2000 : 0;
        }
        var start = Twips(left ?? 0);
        var end = Twips(f.Right ?? 0);
        sb.Append($"\\li{Number(rtl ? end : start)}\\ri{Number(rtl ? start : end)}\\lin{Number(start)}\\rin{Number(end)}\\fi{Number(Twips(firstLine ?? 0))}");
        sb.Append($"\\sb{Number(Math.Max(0, Twips(f.Before ?? style.Before)))}\\sa{Number(Math.Max(0, Twips(f.After ?? style.After)))}");
        var line = f.LineSpacing ?? style.Line;
        sb.Append(line.Kind switch
        {
            HLineSpacingKind.Fixed => $"\\sl-{Number(Math.Max(20, Twips(line.Value)))}\\slmult0",
            HLineSpacingKind.AtLeast => $"\\sl{Number(Math.Max(20, Twips(line.Value)))}\\slmult0",
            // 한글 percentages are of the font size, RTF's multiples of the font's line (≈ 1.3 em), as in DOCX.
            _ => $"\\sl{Number(Math.Clamp((int)Math.Round(line.Value * 240 / 130.0), 24, 2400))}\\slmult1",
        });
        if (f.KeepWithNext ?? style.Keep)
            sb.Append("\\keepn");
        if (f.KeepLines ?? style.Keep)
            sb.Append("\\keep");
        sb.Append(f.WidowOrphan == false ? "\\nowidctlpar" : "\\widctlpar");
        if (paragraph.PageBreakBefore)
            sb.Append("\\pagebb");

        foreach (var tab in (f.Tabs ?? style.Tabs ?? []).OrderBy(t => t.Position))
        {
            sb.Append(tab.Kind switch
            {
                HTabKind.Right => "\\tqr",
                HTabKind.Center => "\\tqc",
                HTabKind.Decimal => "\\tqdec",
                _ => "",
            });
            sb.Append(tab.Leader switch
            {
                "DOT" => "\\tldot",
                "DASH" => "\\tlhyph",
                "SOLID" => "\\tlul",
                _ => "",
            });
            // The model measures tab stops from the paragraph's indent, RTF from the margin.
            sb.Append($"\\tx{Number(Math.Max(0, Twips(tab.Position + (left ?? 0))))}");
        }

        // A Word paragraph's box (read for layout) gives each line's distance from the text, the line between
        // paragraphs of one box and the background; else 1 pt.
        var box = f.Layout?.Border;
        if (f.Borders is { } borders)
        {
            var space = box?.Offsets;
            foreach (var (side, border, distance) in new[]
                     {
                         ("t", borders.Top, space?.Top), ("l", borders.Left, space?.Left), ("b", borders.Bottom, space?.Bottom),
                         ("r", borders.Right, space?.Right),
                     })
            {
                if (border.Style != HBorderStyle.None)
                    sb.Append($"\\brdr{side}").Append(Border(border)).Append($"\\brsp{Number(distance is { } twips ? Math.Clamp(Twips(twips), 0, 31680) : 20)}");
            }
            if (box?.Between is { Style: not HBorderStyle.None } between)
                sb.Append("\\brdrbtw").Append(Border(between));
        }
        else if (f.LeftBar is { } bar && bar.Style != HBorderStyle.None)
        {
            sb.Append("\\brdrl").Append(Border(bar)).Append("\\brsp120");
        }
        if (ColorIndex(f.Fill ?? box?.Fill) is > 0 and var fill)
            sb.Append($"\\cbpat{Number(fill)}");
        return sb.ToString();
    }

    /// <summary>\brdrs\brdrw15\brdrcf1: a border's style, width (twips, at most 75) and colour.</summary>
    private string Border(HBorder border)
    {
        var style = border.Style switch
        {
            HBorderStyle.Dash => "\\brdrdash",
            HBorderStyle.Dot => "\\brdrdot",
            HBorderStyle.DashDot => "\\brdrdashd",
            HBorderStyle.Double => "\\brdrdb",
            _ => "\\brdrs",
        };
        var width = Math.Clamp((int)Math.Round(border.WidthMm / 25.4 * 1440), 1, 75);
        var color = ColorIndex(border.Color);
        return $"{style}\\brdrw{Number(width)}{(color > 0 ? $"\\brdrcf{Number(color)}" : "")}";
    }

    /// <summary>
    /// The format each list level's number is drawn in (its first item's), written into the level's \listlevel: Word
    /// draws a number in the paragraph mark's format with the level's over it. Its font and colour go into their tables
    /// here, while the body is written, as the tables come before the body in the file.
    /// </summary>
    private readonly Dictionary<(HNumbering Numbering, int Level), (Chars Chars, int Font)> _markers = [];

    private void KeepMarker(HListRef list, StyleBase style)
    {
        var level = Math.Clamp(list.Level, 0, 8);
        if (_markers.ContainsKey((list.Numbering, level)))
            return;
        var marker = list.MarkerFormat ?? (level < list.Numbering.Levels.Count ? list.Numbering.Levels[level].MarkerFormat : null);
        if (marker is not { } format)
            return;
        var chars = Resolve(format, style);
        _markers[(list.Numbering, level)] = (chars, FontIndex(chars.Latin, 0));
    }

    // ───────────────────────── Character formatting ─────────────────────────

    /// <summary>Character formatting with every member decided (the model's null ones from the paragraph's style).</summary>
    /// <param name="Script">0 normal, 1 superscript, 2 subscript.</param>
    /// <param name="Expand">Letter spacing in twips (\expndtw).</param>
    private readonly record struct Chars(bool Bold, bool Italic, bool Underline, bool Strike, int Script, int HalfPoints,
        string Latin, string EastAsian, int Color, int Shade, int Expand);

    /// <summary>What \plain leaves: RTF's 12 pt in the default font, nothing else.</summary>
    private static readonly Chars PlainChars = new(false, false, false, false, 0, 24, DefaultLatinFont, DefaultEastAsianFont, 0, 0, 0);

    private Chars Resolve(HCharFormat f, StyleBase style)
    {
        var halfPoints = f.Size is { } size ? Math.Clamp((int)Math.Round(size / 50.0), 2, 3276) : style.HalfPoints;
        // 한글 letter spacing is a percentage of the font size, RTF's an absolute distance in twips.
        var expand = f.Spacing is { } spacing and not 0 ? (int)Math.Round(spacing * halfPoints * 10 / 100.0) : 0;
        return new Chars(
            f.Bold ?? style.Bold,
            f.Italic ?? false,
            f.Underline ?? false,
            f.Strike ?? false,
            f.Superscript == true ? 1 : f.Subscript == true ? 2 : 0,
            halfPoints,
            f.Font is { Length: > 0 } latin ? latin : DefaultLatinFont,
            f.EastAsianFont is { Length: > 0 } eastAsian ? eastAsian : f.Font is { Length: > 0 } both ? both : DefaultEastAsianFont,
            ColorIndex(f.Color),
            ColorIndex(f.Shade),
            expand);
    }

    /// <summary>The control words that turn <paramref name="from"/> into <paramref name="to"/> (fonts are set per piece of text).</summary>
    private static string Difference(Chars from, Chars to)
    {
        var sb = new StringBuilder();
        if (from.Bold != to.Bold)
            sb.Append(to.Bold ? "\\b" : "\\b0");
        if (from.Italic != to.Italic)
            sb.Append(to.Italic ? "\\i" : "\\i0");
        if (from.Underline != to.Underline)
            sb.Append(to.Underline ? "\\ul" : "\\ulnone");
        if (from.Strike != to.Strike)
            sb.Append(to.Strike ? "\\strike" : "\\strike0");
        if (from.Script != to.Script)
            sb.Append(to.Script switch { 1 => "\\super", 2 => "\\sub", _ => "\\nosupersub" });
        if (from.HalfPoints != to.HalfPoints)
            sb.Append($"\\fs{Number(to.HalfPoints)}");
        if (from.Color != to.Color)
            sb.Append($"\\cf{Number(to.Color)}");
        if (from.Shade != to.Shade)
            sb.Append($"\\chshdng0\\chcbpat{Number(to.Shade)}");
        if (from.Expand != to.Expand)
            sb.Append($"\\expndtw{Number(to.Expand)}");
        return sb.ToString();
    }

    /// <summary>The formatting in effect where runs are written (the paragraph mark's), and the \f set there.</summary>
    private sealed record RunState(Chars Chars, int Font);

    private void Inlines(IEnumerable<HInline> inlines, RunState state, StyleBase style, Story story, int depth)
    {
        var o = _out;
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text: // equations too: their TeX source or linear text
                    Run(text.Format, state, style, () => Text(text.Text, Resolve(text.Format, style), state.Font));
                    break;
                case HLineBreak lineBreak:
                    Run(lineBreak.Format, state, style, () => o.Control("\\line"));
                    break;
                case HTab tab:
                    Run(tab.Format, state, style, () => o.Control("\\tab"));
                    break;
                case HLink link:
                    Link(link, state, style, story, depth);
                    break;
                case HBookmark bookmark:
                    Bookmark(bookmark.Name);
                    break;
                case HField field:
                    Run(field.Format, state, style, () =>
                    {
                        o.Open();
                        o.Control("\\field");
                        o.Open();
                        o.Control("\\*\\fldinst");
                        o.Raw(field.Kind == HFieldKind.PageNumber ? "PAGE" : "NUMPAGES");
                        o.Close();
                        o.Open();
                        o.Control("\\fldrslt");
                        o.Text(field.Stored is { Length: > 0 } stored ? stored : "1", 1252);
                        o.Close();
                        o.Close();
                    });
                    break;
                case HNote note:
                    Note(note, state, style, story);
                    break;
                case HImage image:
                    Picture(image);
                    break;
                case HTextBox box:
                    TextBox(box, state, style);
                    break;
                case HShape shape:
                    Shape(shape);
                    break;
            }
        }
    }

    /// <summary>A group with the formatting of <paramref name="format"/> where it differs from the paragraph mark's.</summary>
    private void Run(HCharFormat format, RunState state, StyleBase style, Action content)
    {
        _out.Open();
        _out.Control(Difference(state.Chars, Resolve(format, style)));
        content();
        _out.Close();
    }

    /// <summary>
    /// Text in pieces of one script: East Asian text in the East Asian font (of an East Asian \fcharset), other text
    /// in the Latin font of its script's \fcharset; spaces and punctuation stay with the piece around them. Arabic
    /// and Hebrew pieces are marked right to left (\rtlch), as Word does.
    /// </summary>
    private void Text(string text, Chars chars, int paragraphFont)
    {
        var o = _out;
        var current = paragraphFont;
        var start = 0;
        var kind = Script.Neutral;
        for (var i = 0; i <= text.Length; i++)
        {
            var next = i < text.Length ? ScriptOf(text, i) : Script.Neutral;
            if (i < text.Length && (next == Script.Neutral || next == kind || kind == Script.Neutral))
            {
                if (kind == Script.Neutral)
                    kind = next;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
                    i++;
                continue;
            }
            if (i > start)
            {
                var piece = text[start..i];
                var (font, codePage) = FontFor(kind, chars, piece);
                if (font != current)
                {
                    o.Control($"\\f{Number(font)}");
                    current = font;
                }
                if (kind is Script.Arabic or Script.Hebrew)
                    o.Control("\\rtlch");
                o.Text(piece, codePage);
                if (kind is Script.Arabic or Script.Hebrew)
                    o.Control("\\ltrch");
            }
            start = i;
            kind = next;
        }
    }

    /// <summary>The scripts text pieces are split by (each has an \fcharset of its own).</summary>
    private enum Script
    {
        /// <summary>Spaces, digits and punctuation: they go with the text around them.</summary>
        Neutral,
        Latin,
        Greek,
        Cyrillic,
        Hebrew,
        Arabic,
        Thai,

        /// <summary>Hangul, kana and Chinese characters, with CJK punctuation and full-width forms.</summary>
        EastAsian,
    }

    private static Script ScriptOf(string text, int i)
    {
        var c = text[i];
        if (char.IsHighSurrogate(c))
            return c is >= '\uD840' and <= '\uD87F' ? Script.EastAsian : Script.Latin; // CJK extensions B..F
        if (char.IsLowSurrogate(c))
            return Script.Neutral;
        if (c < '\u0080')
            return char.IsAsciiLetter(c) ? Script.Latin : Script.Neutral;
        return c switch
        {
            >= '\u0370' and <= '\u03FF' => Script.Greek,
            >= '\u0400' and <= '\u052F' => Script.Cyrillic,
            >= '\u0590' and <= '\u05FF' => Script.Hebrew,
            >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' => Script.Arabic,
            >= '\u0E00' and <= '\u0E7F' => Script.Thai,
            >= '\u1100' and <= '\u11FF' or >= '\u3000' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF'
                or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF' => Script.EastAsian,
            >= '\u2000' and <= '\u206F' or '\u00A0' => Script.Neutral,
            _ => Script.Latin,
        };
    }

    /// <summary>The font index and code page of a piece of text of <paramref name="script"/>.</summary>
    private (int Font, int CodePage) FontFor(Script script, Chars chars, string piece)
    {
        if (script == Script.EastAsian)
        {
            // The text that first uses the font decides its \fcharset: Korean, Japanese, else by the font's name.
            var charset = piece.Any(c => c is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F') ? 129
                : piece.Any(c => c is >= '\u3040' and <= '\u30FF' or >= '\u31F0' and <= '\u31FF') ? 128
                : HanCharset(chars.EastAsian);
            var index = EastAsianFontIndex(chars.EastAsian, charset);
            return (index, CodePage(_fonts[index].Charset));
        }
        var latin = script switch
        {
            Script.Greek => 161,
            Script.Cyrillic => 204,
            Script.Hebrew => 177,
            Script.Arabic => 178,
            Script.Thai => 222,
            _ => 0,
        };
        return (FontIndex(chars.Latin, latin), CodePage(latin));
    }

    /// <summary>The \fcharset of Chinese characters in a font: Korean, Japanese or Chinese by the font's name.</summary>
    private static int HanCharset(string font)
    {
        if (font.Any(c => c is >= '\uAC00' and <= '\uD7AF') || font.Contains("Gothic", StringComparison.OrdinalIgnoreCase) && font.Contains("Malgun", StringComparison.OrdinalIgnoreCase)
            || font is "Batang" or "BatangChe" or "Gulim" or "GulimChe" or "Dotum" or "DotumChe" or "Gungsuh")
            return 129;
        if (font.Any(c => c is >= '\u3040' and <= '\u30FF') || font.StartsWith("MS ", StringComparison.Ordinal) || font.StartsWith("Yu ", StringComparison.Ordinal)
            || font.StartsWith("Meiryo", StringComparison.Ordinal))
            return 128;
        if (font.Contains("Ming", StringComparison.OrdinalIgnoreCase) || font is "新細明體" or "細明體" or "標楷體" or "微軟正黑體" or "Microsoft JhengHei" or "DFKai-SB")
            return 136;
        if (font.Any(c => c is >= '\u4E00' and <= '\u9FFF') || font.StartsWith("Sim", StringComparison.Ordinal) || font.Contains("YaHei", StringComparison.Ordinal))
            return 134;
        return 129; // the default East Asian font, 맑은 고딕
    }

    // ───────────────────────── Links, bookmarks, notes ─────────────────────────

    /// <summary>A HYPERLINK field: its instruction (\l for a bookmark in the document) and the link's text as its result.</summary>
    private void Link(HLink link, RunState state, StyleBase style, Story story, int depth)
    {
        var o = _out;
        var target = link.Target.Replace("\"", "%22", StringComparison.Ordinal);
        o.Open();
        o.Control("\\field");
        o.Open();
        o.Control("\\*\\fldinst");
        o.Raw("HYPERLINK ");
        if (target.StartsWith('#'))
        {
            o.Text("\\l \"" + target[1..] + "\"", 1252);
        }
        else
        {
            o.Text("\"" + target + "\"", 1252);
        }
        o.Close();
        o.Open();
        o.Control("\\fldrslt");
        Inlines(link.Content, state, style, story, depth);
        o.Close();
        o.Close();
    }

    private void Bookmark(string name)
    {
        foreach (var kind in new[] { "\\*\\bkmkstart", "\\*\\bkmkend" })
        {
            _out.Open();
            _out.Control(kind);
            _out.Text(name, 1252);
            _out.Close();
        }
    }

    /// <summary>
    /// A footnote or endnote (\footnote, \ftnalt for an endnote) after its reference mark (\chftn). Notes are only in
    /// the body text and its tables, as in Word: elsewhere the note's text stays in place, in brackets.
    /// </summary>
    private void Note(HNote note, RunState state, StyleBase style, Story story)
    {
        var o = _out;
        if (story != Story.Body)
        {
            var text = string.Concat(note.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text));
            Run(note.Format ?? default, state, style, () => Text($" ({text})", Resolve(note.Format ?? default, style), state.Font));
            return;
        }
        var reference = Resolve(note.Format ?? default, style) with { Script = 1 };
        o.Open();
        o.Control(Difference(state.Chars, reference) + "\\chftn");
        o.Open();
        o.Control(note.Endnote ? "\\footnote\\ftnalt" : "\\footnote");
        var outer = _width;
        _noteNumber = true;
        Blocks(note.Blocks.Count > 0 ? note.Blocks : [new HParagraph()], Story.Note, 0, BlockEnd.Story);
        _width = outer;
        _noteNumber = false;
        o.Close();
        o.Close();
    }

    // ───────────────────────── Tables ─────────────────────────

    /// <summary>Cell padding where neither the cell nor the table gives one: Word's (and the DOCX writer's) 0.19 cm left and right.</summary>
    private static readonly HInsets DefaultCellMargin = new(540, 540, 0, 0);

    /// <param name="depth">The table's level: 1 in the text, 2 in a cell of such a table ...</param>
    private void Table(HTable table, Story story, int depth)
    {
        var o = _out;
        if (table.Rows.Count == 0)
            return;
        var (placed, columnCount) = HwpxWriter.PlaceCells(table);
        var available = Math.Max(4000, _width - (table.Align == HAlign.Left ? table.Indent : 0));
        var widths = HwpxWriter.ColumnWidths(table, columnCount, available).Select(w => Math.Max(1, w)).ToArray();
        // Each grid edge in twips from the margin, rounded once so the cells add up.
        var left = table.Align == HAlign.Left ? table.Indent : 0;
        var edges = new int[columnCount + 1];
        long position = left;
        edges[0] = Twips(left);
        for (var c = 0; c < columnCount; c++)
        {
            position += widths[c];
            edges[c + 1] = Math.Max(edges[c] + 1, (int)Math.Round(position / 5.0));
        }

        // Per column: the row a cell written so far covers it to (exclusive), and that cell with its first column.
        var coveredTo = new int[columnCount];
        var coveredBy = new (HCell Cell, int Column)[columnCount];
        var margin = table.CellMargin ?? DefaultCellMargin;
        var headerRows = table.Rows.TakeWhile(r => r.Header).Count();
        var outerWidth = _width;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            _token.ThrowIfCancellationRequested();
            var row = table.Rows[r];
            var starts = r < placed.Count ? placed[r].GroupBy(p => p.Column).ToDictionary(g => g.Key, g => g.First().Cell) : [];

            // The row's cells: (definition, content, the cell's text width) for each grid position.
            var cells = new List<(string Definition, HCell? Content, int Width)>();
            for (var column = 0; column < columnCount;)
            {
                if (starts.TryGetValue(column, out var cell))
                {
                    var span = Math.Clamp(cell.ColSpan, 1, columnCount - column);
                    var rowSpan = Math.Clamp(cell.RowSpan, 1, table.Rows.Count - r);
                    for (var c = column; c < column + span; c++)
                    {
                        coveredTo[c] = r + rowSpan;
                        coveredBy[c] = (cell, column);
                    }
                    var cellMargin = cell.CellMargin ?? margin;
                    var width = Math.Max(1000, widths.Skip(column).Take(span).Sum() - cellMargin.Left - cellMargin.Right);
                    for (var k = 0; k < span; k++)
                    {
                        var merge = (rowSpan > 1 ? "\\clvmgf" : "") + (span > 1 ? k == 0 ? "\\clmgf" : "\\clmrg" : "");
                        cells.Add((CellDefinition(cell, table, merge, edges[column + k + 1], edges[column + k + 1] - edges[column + k]), k == 0 ? cell : null, width));
                    }
                    column += span;
                }
                else if (coveredTo[column] > r && coveredBy[column] is (HCell origin, var originColumn) && originColumn == column)
                {
                    // Below a cell spanning rows: continuation cells, as many columns wide.
                    var span = Math.Clamp(origin.ColSpan, 1, columnCount - column);
                    for (var k = 0; k < span; k++)
                    {
                        var merge = "\\clvmrg" + (span > 1 ? k == 0 ? "\\clmgf" : "\\clmrg" : "");
                        cells.Add((CellDefinition(origin, table, merge, edges[column + k + 1], edges[column + k + 1] - edges[column + k]), null, 0));
                    }
                    column += span;
                }
                else
                {
                    // A gap in a ragged row (or inside a span that started left of it): an empty cell keeps the grid.
                    cells.Add(($"\\cellx{Number(edges[column + 1])}", null, 0));
                    column++;
                }
            }

            var definition = RowDefinition(table, row, r < headerRows, margin, edges[0]) + string.Concat(cells.Select(c => c.Definition));
            if (depth == 1)
            {
                o.Control(definition);
                o.Line();
            }
            var firstCell = true;
            foreach (var (_, content, width) in cells)
            {
                if (content is null)
                {
                    EmptyParagraph(depth, CellEnd(depth));
                    continue;
                }
                _width = width;
                var blocks = content.Blocks.Count > 0 ? content.Blocks : [new HParagraph()];
                if (firstCell && r == 0 && table.PageBreakBefore && blocks[0] is HParagraph opening)
                {
                    // The page break goes with the table's first paragraph.
                    var copy = new HParagraph { Format = opening.Format, MarkFormat = opening.MarkFormat, HeadingLevel = opening.HeadingLevel, List = opening.List, PageBreakBefore = true };
                    copy.Inlines.AddRange(opening.Inlines);
                    blocks = [copy, .. blocks.Skip(1)];
                }
                firstCell = false;
                Blocks(blocks, story, depth, BlockEnd.Cell);
            }
            _width = outerWidth;
            if (depth == 1)
            {
                o.Control("\\row");
            }
            else
            {
                // A nested table's row ends with its definition.
                o.Open();
                o.Control("\\*\\nesttableprops" + definition + "\\nestrow");
                o.Close();
            }
            o.Line();
        }
    }

    /// <summary>\trowd and the row's own properties: padding, indent, alignment, height, header and keep.</summary>
    private string RowDefinition(HTable table, HRow row, bool header, HInsets margin, int leftEdge)
    {
        var sb = new StringBuilder("\\trowd");
        sb.Append($"\\trgaph{Number(Twips(margin.Left))}\\trleft{Number(leftEdge)}");
        sb.Append(table.Align switch
        {
            HAlign.Center => "\\trqc",
            HAlign.Right => "\\trqr",
            _ => "\\trql",
        });
        if (table.RightToLeft)
            sb.Append("\\rtlrow");
        if (row.Height is > 0 and var height)
            sb.Append($"\\trrh{Number(Twips(height))}");
        if (header)
            sb.Append("\\trhdr");
        if (row.CantSplit)
            sb.Append("\\trkeep");
        sb.Append($"\\trpaddl{Number(Twips(margin.Left))}\\trpaddr{Number(Twips(margin.Right))}\\trpaddt{Number(Twips(margin.Top))}\\trpaddb{Number(Twips(margin.Bottom))}")
          .Append("\\trpaddfl3\\trpaddfr3\\trpaddft3\\trpaddfb3");
        // A Word table sized by its content (autofit) stays so (\trautofit1), with its preferred width; others keep their columns.
        if (table.Sizing is { } sizing)
        {
            if (!sizing.Fixed)
                sb.Append("\\trautofit1");
            sb.Append(PreferredWidth("tr", sizing.Width));
        }
        if (table.Float is { } anchor)
            sb.Append(TablePosition(anchor, table.FloatDistance));
        return sb.ToString();
    }

    /// <summary>
    /// A floating table's place (Word w:tblpPr): what it is measured from (\tph*, \tpv*), its offsets (\tposx, \tposnegx
    /// ...) or alignment (\tposxc, \tposyb ...), the distance text keeps from it (\tdfrmtxt*) and whether other floating
    /// tables may overlap it (\tabsnoovrlp).
    /// </summary>
    private static string TablePosition(HAnchor anchor, HInsets distance)
    {
        var sb = new StringBuilder(anchor.HorizontalRelativeTo switch
        {
            "PAPER" => "\\tphpg",
            "PAGE" => "\\tphmrg",
            _ => "\\tphcol",
        });
        sb.Append(anchor.VerticalRelativeTo switch
        {
            "PAPER" => "\\tpvpg",
            "PAGE" => "\\tpvmrg",
            _ => "\\tpvpara",
        });
        var x = Twips(anchor.HorizontalOffset);
        sb.Append(anchor.HorizontalOffset != 0 ? x < 0 ? $"\\tposnegx{Number(x)}" : $"\\tposx{Number(x)}" : anchor.HorizontalAlign switch
        {
            "CENTER" => "\\tposxc",
            "RIGHT" => "\\tposxr",
            "INSIDE" => "\\tposxi",
            "OUTSIDE" => "\\tposxo",
            _ => "\\tposx0",
        });
        var y = Twips(anchor.VerticalOffset);
        sb.Append(anchor.VerticalOffset != 0 ? y < 0 ? $"\\tposnegy{Number(y)}" : $"\\tposy{Number(y)}" : anchor.VerticalAlign switch
        {
            "CENTER" => "\\tposyc",
            "BOTTOM" => "\\tposyb",
            "INSIDE" => "\\tposyin",
            "OUTSIDE" => "\\tposyout",
            _ => "\\tposy0",
        });
        sb.Append($"\\tdfrmtxtLeft{Number(Twips(distance.Left))}\\tdfrmtxtRight{Number(Twips(distance.Right))}")
          .Append($"\\tdfrmtxtTop{Number(Twips(distance.Top))}\\tdfrmtxtBottom{Number(Twips(distance.Bottom))}");
        if (!anchor.AllowOverlap)
            sb.Append("\\tabsnoovrlp");
        return sb.ToString();
    }

    /// <summary>\trftsWidth / \clftsWidth and the width: 1 auto, 2 fiftieths of a percent, 3 twips.</summary>
    private static string PreferredWidth(string prefix, HPreferredWidth width) => width.Type switch
    {
        HWidthType.Percent => $"\\{prefix}ftsWidth2\\{prefix}wWidth{Number((int)Math.Clamp(Math.Round(width.Value * 5000), 0, 50000))}",
        HWidthType.Absolute => $"\\{prefix}ftsWidth3\\{prefix}wWidth{Number(Math.Max(1, Twips((int)Math.Clamp(width.Value, 0, int.MaxValue))))}",
        _ => $"\\{prefix}ftsWidth1\\{prefix}wWidth0",
    };

    /// <summary>A cell's definition up to its \cellx: merges, vertical alignment, borders, shading, padding and width.</summary>
    private string CellDefinition(HCell cell, HTable table, string merge, int right, int width)
    {
        var sb = new StringBuilder(merge);
        sb.Append(cell.VerticalAlign switch
        {
            HVerticalAlign.Center => "\\clvertalc",
            HVerticalAlign.Bottom => "\\clvertalb",
            _ => "\\clvertalt",
        });
        var borders = cell.Borders ?? table.Borders ?? HBorders.Grid;
        foreach (var (side, border) in new[] { ("t", borders.Top), ("l", borders.Left), ("b", borders.Bottom), ("r", borders.Right) })
        {
            if (border.Style != HBorderStyle.None)
                sb.Append($"\\clbrdr{side}").Append(Border(border));
        }
        if (ColorIndex(cell.Fill) is > 0 and var fill)
            sb.Append($"\\clcbpat{Number(fill)}");
        if (cell.CellMargin is { } m)
        {
            // Word reads \clpadl as the top padding and \clpadt as the left one, and writes them so; so does this.
            sb.Append($"\\clpadt{Number(Twips(m.Left))}\\clpadft3\\clpadr{Number(Twips(m.Right))}\\clpadfr3")
              .Append($"\\clpadl{Number(Twips(m.Top))}\\clpadfl3\\clpadb{Number(Twips(m.Bottom))}\\clpadfb3");
        }
        // An autofit table's cells keep their own preferred widths (none: auto); other cells their column's.
        var preferred = table.Sizing is { Fixed: false } ? cell.Width ?? HPreferredWidth.Auto : new HPreferredWidth(HWidthType.Absolute, Math.Max(1, width) * 5.0);
        sb.Append(PreferredWidth("cl", preferred)).Append($"\\cellx{Number(right)}");
        return sb.ToString();
    }
}
