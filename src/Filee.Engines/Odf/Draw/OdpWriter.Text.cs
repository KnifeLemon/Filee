// Text of the ODP writer: the model's paragraphs as text:p with an automatic paragraph style (alignment, indents,
// spacing, the line height as the reader stated it, tab stops, right to left) carrying the paragraph mark's character
// format, and text:span with a text style wherever a run differs from it.
//
// The slide model has bullets and numbers already in the text (a label, then a tab to the hanging indent or a space).
// A paragraph that starts so with a symbol, or with a number in a known form (1. 2) (3) a. B) iv.), is written as an
// item of a text:list instead: the label becomes the level's bullet character or number format, its size a share of
// the text's (text:bullet-relative-size), its colour and font the level's text properties, the number the item's
// text:start-value. Levels are the frame's distinct indents of such paragraphs, smallest first.

using System.Globalization;
using System.Text;
using Filee.Engines.Hwp.Hwpx;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf.Draw;

internal sealed partial class OdpWriter
{
    /// <summary>List levels ODF numbers (text:level 1 … 10).</summary>
    private const int ListLevels = 10;

    /// <summary>A paragraph's bullet or number: the label, its format, and whether a tab (else a space) follows it.</summary>
    private sealed record Label(string Text, HCharFormat Format, bool Tab, string? NumberFormat = null, string Prefix = "", string Suffix = "", int Value = 0);

    private void Paragraphs(StringBuilder sb, List<HParagraph> paragraphs)
    {
        var labels = paragraphs.Select(FindLabel).ToArray();
        var last = new (string Format, int Value)?[ListLevels];
        var indents = paragraphs.Where((_, i) => labels[i] is not null).Select(p => p.Format.Left ?? 0).Distinct().Order().ToList();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            _token.ThrowIfCancellationRequested();
            var paragraph = paragraphs[i];
            var style = _styles.Add("paragraph", "P", "", ParagraphProperties(paragraph.Format) + TextProperties(paragraph.MarkFormat));
            if (labels[i] is not { } label)
            {
                sb.Append($"<text:p text:style-name=\"{style}\">");
                Inlines(sb, paragraph.Inlines, new TextState(paragraph.MarkFormat), inLink: false);
                sb.Append("</text:p>");
                continue;
            }
            var level = Math.Min(indents.IndexOf(paragraph.Format.Left ?? 0), ListLevels - 1);
            for (var deeper = level + 1; deeper < ListLevels; deeper++)
                last[deeper] = null;
            label = Numbered(label, last[level]);
            last[level] = label.NumberFormat is { } format ? (format, label.Value) : null;
            var list = _styles.Add("list", "L", "", ListLevel(label, level, paragraph));
            sb.Append($"<text:list text:style-name=\"{list}\">");
            for (var k = 0; k < level; k++)
                sb.Append("<text:list-item><text:list>");
            sb.Append(label.NumberFormat is null ? "<text:list-item>" : $"<text:list-item text:start-value=\"{N(label.Value)}\">");
            sb.Append($"<text:p text:style-name=\"{style}\">");
            Inlines(sb, paragraph.Inlines.Skip(2), new TextState(paragraph.MarkFormat), inLink: false);
            sb.Append("</text:p></text:list-item>");
            for (var k = 0; k < level; k++)
                sb.Append("</text:list></text:list-item>");
            sb.Append("</text:list>");
        }
    }

    // ───────────────────────── Lists ─────────────────────────

    /// <summary>The bullet or number a paragraph starts with in the model, or null.</summary>
    private static Label? FindLabel(HParagraph paragraph)
    {
        var inlines = paragraph.Inlines;
        if (inlines.Count < 3 || inlines[0] is not HText first || first.GetType() != typeof(HText) || first.Text.Length is 0 or > 12)
            return null;
        bool tab;
        if (inlines[1] is HTab separator && separator.Format.Equals(first.Format))
            tab = true;
        else if (inlines[1] is HText space && space.GetType() == typeof(HText) && space.Text == " " && space.Format.Equals(first.Format))
            tab = false;
        else
            return null;
        // A tab goes to the hanging indent; without one the label is followed by a space.
        if (tab != (paragraph.Format.FirstLine ?? 0) < 0)
            return null;
        if (!inlines.Skip(2).Any(i => i is HText { Text: var t } && t.Trim().Length > 0 || i is HLink or HTab))
            return null;
        var text = first.Text;
        if (text.Any(char.IsWhiteSpace))
            return null;
        var single = text.Length == 1 || (text.Length == 2 && char.IsSurrogatePair(text, 0));
        if (single && !char.IsLetterOrDigit(text, 0))
            return new Label(text, first.Format, tab);
        // A number: digits, or letters with a "." or ")" after them, maybe in brackets.
        var at = text.StartsWith('(') ? 1 : 0;
        var end = text.Length;
        var suffix = end > at && text[end - 1] is '.' or ')' ? text[end - 1].ToString() : "";
        end -= suffix.Length;
        var core = text[at..end];
        if (core.Length == 0 || (at == 1 && suffix != ")"))
            return null;
        var prefix = at == 1 ? "(" : "";
        if (core.All(char.IsAsciiDigit))
            return core.Length <= 6 ? new Label(text, first.Format, tab, "1", prefix, suffix, int.Parse(core, CultureInfo.InvariantCulture)) : null;
        if (suffix.Length == 0 || core.Length > 4 || !core.All(char.IsAsciiLetter) || !(core.All(char.IsAsciiLetterLower) || core.All(char.IsAsciiLetterUpper)))
            return null;
        // Letters or Roman numerals: decided with the paragraph before (Numbered).
        return new Label(text, first.Format, tab, core.All(char.IsAsciiLetterLower) ? "a" : "A", prefix, suffix, 0);
    }

    /// <summary>A label of letters as letters or Roman numerals, whichever continues the level's numbering.</summary>
    private static Label Numbered(Label label, (string Format, int Value)? before)
    {
        if (label.NumberFormat is not ("a" or "A"))
            return label;
        var core = label.Text[label.Prefix.Length..^label.Suffix.Length];
        var lower = label.NumberFormat == "a";
        var letters = Letters(core);
        var roman = Roman(core);
        var romanFormat = lower ? "i" : "I";
        if (before is { } previous)
        {
            if (roman is { } r && previous.Format == romanFormat && r == previous.Value + 1)
                return label with { NumberFormat = romanFormat, Value = r };
            if (previous.Format == label.NumberFormat && letters == previous.Value + 1)
                return label with { Value = letters };
        }
        // "i" starts Roman numerals; other single letters are letters.
        if (roman is { } numeral && (core.Length > 1 || core is "i" or "I"))
            return label with { NumberFormat = romanFormat, Value = numeral };
        return label with { Value = letters };
    }

    /// <summary>"a" = 1 … "z" = 26, "aa" = 27, as ODF counts with letters.</summary>
    private static int Letters(string core)
    {
        var value = 0;
        foreach (var ch in core.ToLowerInvariant())
            value = value * 26 + (ch - 'a' + 1);
        return value;
    }

    /// <summary>The value of a Roman numeral written the usual way; null for anything else.</summary>
    private static int? Roman(string core)
    {
        var text = core.ToUpperInvariant();
        var value = 0;
        var at = 0;
        foreach (var (amount, numeral) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
        {
            for (var count = 0; count < 3 && at + numeral.Length <= text.Length && string.CompareOrdinal(text, at, numeral, 0, numeral.Length) == 0; count++)
            {
                value += amount;
                at += numeral.Length;
            }
        }
        return at == text.Length && value > 0 ? value : null;
    }

    /// <summary>The list style of one paragraph's level: its bullet or number, the label's size, colour and font, and the indent.</summary>
    private static string ListLevel(Label label, int level, HParagraph paragraph)
    {
        var left = paragraph.Format.Left ?? 0;
        var firstLine = paragraph.Format.FirstLine ?? 0;
        var textSize = paragraph.MarkFormat.Size ?? label.Format.Size;
        var relative = label.Format.Size is { } size && textSize is > 0 ? $" text:bullet-relative-size=\"{Percent(Math.Clamp(size / (double)textSize.Value, 0.01, 10))}\"" : "";
        var number = N(level + 1);
        var sb = new StringBuilder();
        if (label.NumberFormat is { } format)
        {
            sb.Append($"<text:list-level-style-number text:level=\"{number}\" style:num-format=\"{format}\"");
            if (label.Prefix.Length > 0)
                sb.Append($" style:num-prefix=\"{OdfXml.Escape(label.Prefix)}\"");
            if (label.Suffix.Length > 0)
                sb.Append($" style:num-suffix=\"{OdfXml.Escape(label.Suffix)}\"");
            sb.Append(relative).Append('>');
        }
        else
        {
            sb.Append($"<text:list-level-style-bullet text:level=\"{number}\" text:bullet-char=\"{OdfXml.Escape(label.Text)}\"{relative}>");
        }
        // The label starts at the first line's indent; the text after it at the paragraph's.
        var width = Math.Max(0, -firstLine);
        sb.Append($"<style:list-level-properties text:space-before=\"{Pt((left + Math.Min(firstLine, 0)) / 100.0)}\" text:min-label-width=\"{Pt(width / 100.0)}\"/>");
        var properties = new StringBuilder();
        if (label.Format.Font is { } font)
            properties.Append($" fo:font-family=\"{Family(font)}\"");
        properties.Append(OdfNames.Color(label.Format.Color) is { } color ? $" fo:color=\"{color}\"" : " style:use-window-font-color=\"true\"");
        sb.Append($"<style:text-properties{properties}/>");
        sb.Append(label.NumberFormat is null ? "</text:list-level-style-bullet>" : "</text:list-level-style-number>");
        return sb.ToString();
    }

    // ───────────────────────── Properties ─────────────────────────

    /// <summary>style:paragraph-properties of the members that are set.</summary>
    private static string ParagraphProperties(HParaFormat f)
    {
        var a = new StringBuilder();
        var rtl = f.RightToLeft == true;
        if (f.Align is { } align)
        {
            a.Append(align switch
            {
                HAlign.Center => " fo:text-align=\"center\"",
                HAlign.Right => " fo:text-align=\"end\"",
                HAlign.Justify => " fo:text-align=\"justify\" fo:text-align-last=\"start\"",
                HAlign.Distribute => " fo:text-align=\"justify\" fo:text-align-last=\"justify\"",
                _ => " fo:text-align=\"start\"",
            });
        }
        // Right to left, the model's Left is the start side: the right one.
        if ((rtl ? f.Right : f.Left) is { } left)
            a.Append($" fo:margin-left=\"{Hwp(left)}\"");
        if ((rtl ? f.Left : f.Right) is { } right)
            a.Append($" fo:margin-right=\"{Hwp(right)}\"");
        if (f.FirstLine is { } firstLine)
            a.Append($" fo:text-indent=\"{Hwp(firstLine)}\"");
        if (f.Before is { } before)
            a.Append($" fo:margin-top=\"{Hwp(Math.Max(0, before))}\"");
        if (f.After is { } after)
            a.Append($" fo:margin-bottom=\"{Hwp(Math.Max(0, after))}\"");
        a.Append(LineHeight(f));
        if (f.RightToLeft is { } direction)
            a.Append($" style:writing-mode=\"{(direction ? "rl-tb" : "lr-tb")}\"");
        var tabs = f.Tabs is { Count: > 0 } stops
            ? "<style:tab-stops>" + string.Concat(stops.OrderBy(t => t.Position).Take(64).Select(TabStop)) + "</style:tab-stops>"
            : "";
        return a.Length == 0 && tabs.Length == 0 ? "" : $"<style:paragraph-properties{a}>{tabs}</style:paragraph-properties>";
    }

    /// <summary>The line height as the source stated it (the layout's rule), else the model's spacing.</summary>
    private static string LineHeight(HParaFormat f)
    {
        if (f.Layout?.LineHeight is { } height)
        {
            var value = Math.Max(0, height.Value);
            return height.Rule switch
            {
                // 240 = single lines: ODF's 100 %.
                HLineHeightRule.Multiple => $" fo:line-height=\"{Percent(Math.Clamp(value / 240.0, 0.01, 50))}\"",
                HLineHeightRule.Exact => $" fo:line-height=\"{Hwp(Math.Max(1, value))}\"",
                HLineHeightRule.AtLeast => $" style:line-height-at-least=\"{Hwp(value)}\"",
                HLineHeightRule.BetweenLines => $" style:line-spacing=\"{Hwp(value)}\"",
                _ => $" fo:line-height=\"{Percent(Math.Clamp(value / 100.0 / OdtLinePercent, 0.01, 50))}\"",
            };
        }
        if (f.LineSpacing is { } line)
        {
            return line.Kind switch
            {
                HLineSpacingKind.Fixed => $" fo:line-height=\"{Hwp(Math.Max(1, line.Value))}\"",
                HLineSpacingKind.AtLeast => $" style:line-height-at-least=\"{Hwp(Math.Max(0, line.Value))}\"",
                _ => $" fo:line-height=\"{Percent(Math.Clamp(line.Value / 100.0 / OdtLinePercent, 0.01, 50))}\"",
            };
        }
        return "";
    }

    /// <summary>한글's percent of the font size per ODF percent of the line (the ODT reader's ratio).</summary>
    private const double OdtLinePercent = 1.3;

    private static string TabStop(HTabStop tab)
    {
        var type = tab.Kind switch
        {
            HTabKind.Right => " style:type=\"right\"",
            HTabKind.Center => " style:type=\"center\"",
            HTabKind.Decimal => " style:type=\"char\" style:char=\".\"",
            _ => "",
        };
        return $"<style:tab-stop style:position=\"{Hwp(Math.Max(0, tab.Position))}\"{type}/>";
    }

    /// <summary>style:text-properties of the members that are set.</summary>
    private static string TextProperties(HCharFormat f)
    {
        var a = new StringBuilder();
        if (f.Font is { } font)
            a.Append($" fo:font-family=\"{Family(font)}\" style:font-family-complex=\"{Family(font)}\"");
        if ((f.EastAsianFont ?? f.Font) is { } eastAsian)
            a.Append($" style:font-family-asian=\"{Family(eastAsian)}\"");
        if (f.Size is { } size)
        {
            var points = Hwp(Math.Clamp(size, 100, 163_800));
            a.Append($" fo:font-size=\"{points}\" style:font-size-asian=\"{points}\" style:font-size-complex=\"{points}\"");
        }
        if (f.Bold is { } bold)
        {
            var weight = bold ? "bold" : "normal";
            a.Append($" fo:font-weight=\"{weight}\" style:font-weight-asian=\"{weight}\" style:font-weight-complex=\"{weight}\"");
        }
        if (f.Italic is { } italic)
        {
            var style = italic ? "italic" : "normal";
            a.Append($" fo:font-style=\"{style}\" style:font-style-asian=\"{style}\" style:font-style-complex=\"{style}\"");
        }
        if (f.Underline is { } underline)
            a.Append(underline ? " style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\"" : " style:text-underline-style=\"none\"");
        if (f.Strike is { } strike)
            a.Append($" style:text-line-through-style=\"{(strike ? "solid" : "none")}\"");
        if (f.Superscript == true)
            a.Append(" style:text-position=\"super 58%\"");
        else if (f.Subscript == true)
            a.Append(" style:text-position=\"sub 58%\"");
        else if (f.Superscript == false || f.Subscript == false)
            a.Append(" style:text-position=\"0% 100%\"");
        if (OdfNames.Color(f.Color) is { } color)
            a.Append($" fo:color=\"{color}\"");
        if (f.Shade is not null)
            a.Append($" fo:background-color=\"{OdfNames.Color(f.Shade) ?? "transparent"}\"");
        if (f.Spacing is { } spacing && spacing != 0)
            a.Append($" fo:letter-spacing=\"{Hwp((int)Math.Round(spacing * (f.Size ?? 1800) / 100.0))}\"");
        return a.Length == 0 ? "" : $"<style:text-properties{a}/>";
    }

    /// <summary>A font family as fo:font-family takes it: quoted when it is more than letters and digits.</summary>
    private static string Family(string font)
    {
        var name = font.Replace("'", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        return OdfXml.Escape(name.All(char.IsAsciiLetterOrDigit) ? name : $"'{name}'");
    }

    /// <summary>A length in HWPUNIT (1/100 pt) in points.</summary>
    private static string Hwp(int value) => Pt(value / 100.0);

    // ───────────────────────── Text ─────────────────────────

    /// <summary>
    /// White space within a paragraph: ODF collapses runs of spaces into one and drops spaces at the start, so a space
    /// after another (or first in the paragraph) is written as text:s. <see cref="Mark"/> is the paragraph style's format.
    /// </summary>
    private sealed class TextState(HCharFormat mark)
    {
        public bool Space { get; set; } = true;
        public HCharFormat Mark { get; } = mark;
    }

    private void Inlines(StringBuilder sb, IEnumerable<HInline> inlines, TextState state, bool inLink)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    Span(sb, text.Format, state, () => Text(sb, text.Text, state));
                    break;
                case HLineBreak lineBreak:
                    Span(sb, lineBreak.Format, state, () => sb.Append("<text:line-break/>"));
                    state.Space = true;
                    break;
                case HTab tab:
                    Span(sb, tab.Format, state, () => sb.Append("<text:tab/>"));
                    state.Space = false;
                    break;
                case HLink link when !inLink:
                    sb.Append($"<text:a xlink:type=\"simple\" xlink:href=\"{OdfXml.Escape(link.Target)}\">");
                    Inlines(sb, link.Content, state, inLink: true);
                    sb.Append("</text:a>");
                    break;
                case HLink link:
                    Inlines(sb, link.Content, state, inLink: true);
                    break;
                case HField field:
                    Span(sb, field.Format, state, () => sb.Append(field.Kind == HFieldKind.PageNumber
                        ? "<text:page-number text:select-page=\"current\">1</text:page-number>"
                        : "<text:page-count>1</text:page-count>"));
                    state.Space = false;
                    break;
            }
        }
    }

    /// <summary>Writes <paramref name="content"/> in a text:span when <paramref name="format"/> is not the paragraph's own.</summary>
    private void Span(StringBuilder sb, HCharFormat format, TextState state, Action content)
    {
        var properties = format.Equals(state.Mark) ? "" : TextProperties(format);
        if (properties.Length == 0)
        {
            content();
            return;
        }
        sb.Append($"<text:span text:style-name=\"{_styles.Add("text", "T", "", properties)}\">");
        content();
        sb.Append("</text:span>");
    }

    /// <summary>Plain text in a paragraph (speaker notes).</summary>
    private static void PlainText(StringBuilder sb, string text) => Text(sb, text, new TextState(default));

    /// <summary>Text with tabs and line feeds as their elements and spaces ODF would collapse as text:s.</summary>
    private static void Text(StringBuilder sb, string text, TextState state)
    {
        var segment = new StringBuilder();
        var spaces = 0;
        void FlushSegment()
        {
            if (segment.Length > 0)
                sb.Append(OdfXml.Escape(segment.ToString()));
            segment.Clear();
        }
        void FlushSpaces()
        {
            if (spaces == 0)
                return;
            FlushSegment();
            sb.Append(spaces == 1 ? "<text:s/>" : $"<text:s text:c=\"{N(spaces)}\"/>");
            spaces = 0;
        }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == ' ')
            {
                if (state.Space)
                {
                    spaces++;
                }
                else
                {
                    segment.Append(' ');
                    state.Space = true;
                }
                continue;
            }
            FlushSpaces();
            state.Space = false;
            if (ch is '\t' or '\n' or '\r')
            {
                FlushSegment();
                if (ch == '\t')
                {
                    sb.Append("<text:tab/>");
                }
                else if (ch == '\n' || i + 1 >= text.Length || text[i + 1] != '\n')
                {
                    sb.Append("<text:line-break/>");
                    state.Space = true;
                }
                continue;
            }
            segment.Append(ch);
        }
        FlushSpaces();
        FlushSegment();
    }
}
