// Excel's header and footer codes (the language of headerFooter oddHeader, the XLS HEADER record and Excel's Page
// Setup → Custom Header): &L &C &R start the left, centre and right sections; &P (with +n / -n) the page number,
// &N the number of pages, &D the date, &T the time, &F the file name, &Z its folder, &A the sheet name, &G the
// section's picture; &B &I &U &E &S &X &Y switch bold, italic, underline, double underline, strikethrough,
// superscript and subscript; &"Font,Style" and &nn set the font and its size, &KRRGGBB the colour; && is an ampersand.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Office.Sheets;

/// <summary>A field the page fills in.</summary>
internal enum HeaderField
{
    None,
    Page,
    Pages,
    Date,
    Time,
    FileName,
    Path,
    SheetName,
    Picture,
}

/// <summary>A piece of a header section: text or a field, in a style.</summary>
/// <param name="Offset">Added to the page number (&amp;P+1).</param>
internal sealed record HeaderPiece(string Text, HeaderField Field, int Offset, bool Bold, bool Italic, bool Underline, bool DoubleUnderline,
    bool Strike, int Script, string? Font, double? Size, string? Color);

/// <summary>The left, centre and right sections of a header or footer.</summary>
internal sealed record HeaderSections(IReadOnlyList<HeaderPiece> Left, IReadOnlyList<HeaderPiece> Center, IReadOnlyList<HeaderPiece> Right)
{
    public static readonly HeaderSections Empty = new([], [], []);

    public bool IsEmpty => Left.Count == 0 && Center.Count == 0 && Right.Count == 0;
}

internal static class HeaderFooterText
{
    /// <summary>Characters of one header or footer read at most (Excel allows 255).</summary>
    private const int MaxLength = 4096;

    public static HeaderSections Parse(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return HeaderSections.Empty;
        if (code.Length > MaxLength)
            code = code[..MaxLength];
        var sections = new List<HeaderPiece>[] { [], [], [] };
        var current = 1; // text before any &L / &C / &R is centred
        bool bold = false, italic = false, underline = false, doubleUnderline = false, strike = false;
        var script = 0;
        string? font = null, color = null;
        double? size = null;
        var text = new StringBuilder();

        void Flush()
        {
            if (text.Length > 0)
                sections[current].Add(new HeaderPiece(text.ToString(), HeaderField.None, 0, bold, italic, underline, doubleUnderline, strike, script, font, size, color));
            text.Clear();
        }

        void Field(HeaderField field, int offset = 0)
        {
            Flush();
            sections[current].Add(new HeaderPiece("", field, offset, bold, italic, underline, doubleUnderline, strike, script, font, size, color));
        }

        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch != '&' || i + 1 >= code.Length)
            {
                text.Append(ch);
                continue;
            }
            var next = code[++i];
            switch (char.ToUpperInvariant(next))
            {
                case '&':
                    text.Append('&');
                    break;
                case 'L':
                    Flush();
                    current = 0;
                    break;
                case 'C':
                    Flush();
                    current = 1;
                    break;
                case 'R':
                    Flush();
                    current = 2;
                    break;
                case 'P':
                    var offset = 0;
                    if (i + 1 < code.Length && code[i + 1] is '+' or '-')
                    {
                        var j = i + 2;
                        while (j < code.Length && char.IsAsciiDigit(code[j]) && j - i < 6)
                            j++;
                        if (j > i + 2 && int.TryParse(code.AsSpan(i + 2, j - i - 2), out var amount))
                        {
                            offset = code[i + 1] == '+' ? amount : -amount;
                            i = j - 1;
                        }
                    }
                    Field(HeaderField.Page, offset);
                    break;
                case 'N':
                    Field(HeaderField.Pages);
                    break;
                case 'D':
                    Field(HeaderField.Date);
                    break;
                case 'T':
                    Field(HeaderField.Time);
                    break;
                case 'F':
                    Field(HeaderField.FileName);
                    break;
                case 'Z':
                    Field(HeaderField.Path);
                    break;
                case 'A':
                    Field(HeaderField.SheetName);
                    break;
                case 'G':
                    Field(HeaderField.Picture);
                    break;
                case 'B':
                    Flush();
                    bold = !bold;
                    break;
                case 'I':
                    Flush();
                    italic = !italic;
                    break;
                case 'U':
                    Flush();
                    underline = !underline;
                    break;
                case 'E':
                    Flush();
                    doubleUnderline = !doubleUnderline;
                    break;
                case 'S':
                    Flush();
                    strike = !strike;
                    break;
                case 'X':
                    Flush();
                    script = script == 1 ? 0 : 1;
                    break;
                case 'Y':
                    Flush();
                    script = script == -1 ? 0 : -1;
                    break;
                case 'O' or 'H':
                    break; // outline and shadow: not drawn
                case 'K':
                    Flush();
                    var hex = code.Substring(i + 1, Math.Min(6, code.Length - i - 1));
                    // &KRRGGBB; a theme colour (&KTTSNN) prints in the automatic colour.
                    color = hex.Length == 6 && hex.All(char.IsAsciiHexDigit) ? "#" + hex.ToUpperInvariant() : null;
                    i += hex.Length;
                    break;
                case '"':
                    Flush();
                    var end = code.IndexOf('"', i + 1);
                    if (end < 0)
                        end = code.Length;
                    var spec = code[(i + 1)..end];
                    i = end;
                    var comma = spec.IndexOf(',');
                    var name = (comma < 0 ? spec : spec[..comma]).Trim();
                    var style = comma < 0 ? "" : spec[(comma + 1)..];
                    font = name is "-" or "" ? null : name;
                    bold = style.Contains("Bold", StringComparison.OrdinalIgnoreCase);
                    italic = style.Contains("Italic", StringComparison.OrdinalIgnoreCase) || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    if (char.IsAsciiDigit(next))
                    {
                        Flush();
                        var j = i;
                        while (j < code.Length && char.IsAsciiDigit(code[j]) && j - i < 3)
                            j++;
                        if (double.TryParse(code.AsSpan(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var points) && points is >= 1 and <= 409)
                            size = points;
                        i = j - 1;
                    }
                    else
                    {
                        text.Append('&').Append(next);
                    }
                    break;
            }
        }
        Flush();
        return new HeaderSections(sections[0], sections[1], sections[2]);
    }

    /// <summary>Writes sections back as Excel codes (the ODS reader's header and footer regions).</summary>
    public static string Code(string? left, string? center, string? right)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(left))
            sb.Append("&L").Append(left);
        if (!string.IsNullOrEmpty(center))
            sb.Append("&C").Append(center);
        if (!string.IsNullOrEmpty(right))
            sb.Append("&R").Append(right);
        return sb.ToString();
    }
}
