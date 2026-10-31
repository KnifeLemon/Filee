// Names and units shared by the ODT reader and writer (OpenDocument 1.3): the namespaces, lengths as ODF writes them
// ("2.5cm", "0.5in", "12pt") in HWPUNIT, colours, and the number formats of lists and notes.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Odt;

internal static class Odf
{
    public const string MimeType = "application/vnd.oasis.opendocument.text";
    public const string TemplateMimeType = "application/vnd.oasis.opendocument.text-template";
    public const string Version = "1.3";

    public static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    public static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    public static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    public static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    public static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    public static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    public static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    public static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace Meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    public static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    public static readonly XNamespace MathMl = "http://www.w3.org/1998/Math/MathML";

    /// <summary>The namespaces of every part Filee writes, declared on the root element.</summary>
    public const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
        "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
        "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
        "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
        "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
        "xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" " +
        "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\"";

    /// <summary>Lengths beyond ±500 million HWPUNIT (1.7 km) are cut there, so sums and differences can't overflow.</summary>
    private const double MaxLength = 500_000_000;

    /// <summary>An ODF length ("2.5cm", "0.5in", "12pt", "3mm", "1pc", "10px") in HWPUNIT; null for none or a percentage.</summary>
    public static int? Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        var split = 0;
        while (split < text.Length && (char.IsAsciiDigit(text[split]) || text[split] is '.' or '-' or '+' or 'e' or 'E'))
        {
            // An exponent is only part of the number when a digit follows it ("1e2pt"), not in "1em".
            if (text[split] is 'e' or 'E' && (split + 1 >= text.Length || !(char.IsAsciiDigit(text[split + 1]) || text[split + 1] is '-' or '+')))
                break;
            split++;
        }
        if (!double.TryParse(text.AsSpan(0, split), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            return null;
        var perUnit = text[split..].Trim().ToLowerInvariant() switch
        {
            "in" or "inch" => 7200.0,
            "cm" => 7200 / 2.54,
            "mm" => 720 / 2.54,
            "pt" => 100.0,
            "pc" => 1200.0,
            "px" or "" => 75.0,
            _ => double.NaN, // %, em and the like: not a length
        };
        if (double.IsNaN(perUnit))
            return null;
        return (int)Math.Clamp(Math.Round(number * perUnit), -MaxLength, MaxLength);
    }

    /// <summary>"58%" → 58; null for none or a value that is no percentage.</summary>
    public static double? Percent(string? value)
    {
        if (value is null)
            return null;
        var text = value.Trim();
        if (!text.EndsWith('%'))
            return null;
        return double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : null;
    }

    /// <summary>HWPUNIT as points ("12.34pt"): one point is 100 HWPUNIT, so the value is exact.</summary>
    public static string Points(int hwpUnit) => (hwpUnit / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    /// <summary>A font size in 1/100 pt as points ("10.5pt").</summary>
    public static string FontSize(int size) => Points(size);

    /// <summary>"#rrggbb" in upper case, or null when it is no colour.</summary>
    public static string? Color(string? value)
    {
        if (value is not { Length: 7 } || value[0] != '#')
            return null;
        foreach (var ch in value.AsSpan(1))
        {
            if (!char.IsAsciiHexDigit(ch))
                return null;
        }
        return value.ToUpperInvariant();
    }

    /// <summary>
    /// ODF num-format ("1", "a", "I", LibreOffice's "가, 나, 다, ..." ...) → the model's OWPML numbering type; null for
    /// an empty format (no number at all).
    /// </summary>
    public static string? NumberType(string? format) => format switch
    {
        null => "DIGIT",
        "" => null,
        "a" => "LATIN_SMALL",
        "A" => "LATIN_CAPITAL",
        "i" => "ROMAN_SMALL",
        "I" => "ROMAN_CAPITAL",
        _ when format.StartsWith('가') => "HANGUL_SYLLABLE",
        _ when format.StartsWith('ㄱ') => "HANGUL_JAMO",
        _ when format.StartsWith('①') => "CIRCLED_DIGIT",
        _ when format.StartsWith('일') => "HANGUL_PHONETIC",
        _ when format.StartsWith('一') => "IDEOGRAPH",
        _ => "DIGIT",
    };

    /// <summary>The model's numbering type → ODF num-format (the inverse of <see cref="NumberType"/>).</summary>
    public static string NumberFormat(string type) => type switch
    {
        "LATIN_SMALL" => "a",
        "LATIN_CAPITAL" => "A",
        "ROMAN_SMALL" => "i",
        "ROMAN_CAPITAL" => "I",
        "HANGUL_SYLLABLE" => "가, 나, 다, ...",
        "HANGUL_JAMO" => "ㄱ, ㄴ, ㄷ, ...",
        "CIRCLED_DIGIT" => "①, ②, ③, ...",
        "HANGUL_PHONETIC" => "일, 이, 삼, ...",
        "IDEOGRAPH" => "一, 二, 三, ...",
        _ => "1",
    };
}
