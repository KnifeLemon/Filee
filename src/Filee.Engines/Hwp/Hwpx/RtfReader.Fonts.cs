// RtfReader: the fonts an RTF file's text is set in, for the missing-font notice (DocumentFonts) before a conversion.
//
// One pass over the tokens, without building the document: the font table gives each \fN its name (decoded with its
// \fcharset or \cpg) and its other name (\falt); a font counts when text is set in it, the \f in effect (after \plain,
// the \deff font) where text other than spaces comes. Groups the reader skips (style sheet, lists, pictures, \* groups
// ...) don't count, so a font table listing fonts nothing uses gives no notice.

using System.Text;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class RtfReader
{
    /// <summary>A font name longer than this is damage: the rest is dropped.</summary>
    private const int MaxFontName = 256;

    /// <summary>
    /// The names of the fonts the text of <paramref name="rtf"/> is set in, and the other name (\falt) of those that
    /// have one; empty for a file that isn't RTF. Damage ends the scan with what it found.
    /// </summary>
    internal static (IReadOnlyList<string> Fonts, IReadOnlyDictionary<string, string> Alternates) Fonts(byte[] rtf, DocumentLimits limits)
    {
        var alternates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rtf.Length > limits.MaxPackageBytes || !rtf.AsSpan().TrimStart(" \t\r\n"u8).StartsWith("{\\rtf"u8))
            return ([], alternates);
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var table = new Dictionary<int, (string Name, string? Alternate)>();
        var used = new HashSet<int>();
        var lexer = new Lexer(rtf);
        var groups = new Stack<(FontArea Area, int Font, int Uc)>();
        var (area, font, uc) = (FontArea.Text, -1, 1);
        var ignorable = false;
        // Bytes after a \uN that stand for it in programs without Unicode (\ucN of them).
        var fallback = 0;
        var codePage = 1252;
        var defaultFont = -1;

        // The font table entry being read.
        int index = -1, charset = -1, entryCodePage = 0;
        var name = new StringBuilder();
        var alternate = new StringBuilder();
        var bytes = new List<byte>();

        int EntryCodePage() => entryCodePage > 0 ? entryCodePage : CharsetCodePage(charset) is > 0 and var page ? page : codePage;

        void Decode()
        {
            if (bytes.Count == 0)
                return;
            var text = CodePageEncoding(EntryCodePage()).GetString([.. bytes]);
            bytes.Clear();
            var target = area == FontArea.Alternate ? alternate : name;
            foreach (var c in text)
            {
                if (c == ';' && area == FontArea.FontTable)
                {
                    EndEntry();
                    continue;
                }
                if (target.Length < MaxFontName)
                    target.Append(c);
            }
        }

        void EndEntry()
        {
            if (index >= 0 && table.Count < limits.MaxObjects && name.ToString().Trim() is { Length: > 0 } named)
                table[index] = (named, alternate.ToString().Trim() is { Length: > 0 } other ? other : null);
            name.Clear();
            alternate.Clear();
            index = charset = -1;
            entryCodePage = 0;
        }

        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.End)
                break;
            if (area is FontArea.FontTable or FontArea.Alternate && token.Kind is not (TokenKind.Text or TokenKind.Hex))
                Decode();
            switch (token.Kind)
            {
                case TokenKind.Open:
                    if (groups.Count >= limits.MaxXmlDepth)
                        return Found();
                    groups.Push((area, font, uc));
                    ignorable = false;
                    break;
                case TokenKind.Close:
                    if (groups.Count == 0)
                        return Found();
                    if (area == FontArea.Alternate)
                        Decode();
                    (area, font, uc) = groups.Pop();
                    ignorable = false;
                    fallback = 0;
                    break;
                case TokenKind.Symbol when token.Word == "*":
                    ignorable = true;
                    break;
                case TokenKind.Word when area != FontArea.Skip:
                    var starred = ignorable;
                    ignorable = false;
                    var value = token.Parameter ?? 0;
                    switch (token.Word)
                    {
                        case "fonttbl":
                            area = FontArea.FontTable;
                            continue;
                        case "falt" when area == FontArea.FontTable:
                            area = FontArea.Alternate;
                            continue;
                        case "ansicpg":
                            codePage = value;
                            continue;
                        case "deff":
                            defaultFont = value;
                            continue;
                        case "f" when area == FontArea.FontTable:
                            index = value;
                            continue;
                        case "fcharset" when area == FontArea.FontTable:
                            charset = value;
                            continue;
                        case "cpg" when area == FontArea.FontTable:
                            entryCodePage = value;
                            continue;
                        case "uc":
                            uc = Math.Clamp(value, 0, 16);
                            continue;
                        case "u" when area is FontArea.FontTable or FontArea.Alternate:
                            var target = area == FontArea.Alternate ? alternate : name;
                            if (target.Length < MaxFontName)
                                target.Append((char)(value < 0 ? value + 65536 : value));
                            fallback = uc;
                            continue;
                        case "f" when area == FontArea.Text:
                            font = value;
                            continue;
                        case "plain" when area == FontArea.Text:
                            font = -1;
                            continue;
                        case "u" when area == FontArea.Text:
                            used.Add(font >= 0 ? font : defaultFont);
                            fallback = uc;
                            continue;
                    }
                    if (starred && area != FontArea.Text || starred && token.Word is not ("shppict" or "shpinst" or "nesttableprops")
                        || token.Word is "stylesheet" or "listtable" or "listoverridetable" or "info" or "colortbl" or "pict" or "nonshppict"
                            or "listtext" or "pntext" or "xe" or "tc" or "txe" or "rxe" or "annotation" or "atnid" or "atnauthor"
                            or "template" or "objdata" or "datafield" or "formfield" or "fname" or "panose" or "nonesttables" or "shptxt")
                        area = FontArea.Skip;
                    break;
                case TokenKind.Hex when fallback > 0:
                    fallback--;
                    break;
                case TokenKind.Text when fallback > 0:
                    var skipped = Math.Min(fallback, token.Length);
                    fallback -= skipped;
                    if (area is FontArea.FontTable or FontArea.Alternate)
                        bytes.AddRange(rtf.AsSpan(token.Start + skipped, token.Length - skipped));
                    break;
                case TokenKind.Text when area == FontArea.Text:
                    if (rtf.AsSpan(token.Start, token.Length).IndexOfAnyExcept(" \t"u8) >= 0)
                        used.Add(font >= 0 ? font : defaultFont);
                    break;
                case TokenKind.Hex when area == FontArea.Text:
                    used.Add(font >= 0 ? font : defaultFont);
                    break;
                case TokenKind.Text when area is FontArea.FontTable or FontArea.Alternate:
                    bytes.AddRange(rtf.AsSpan(token.Start, token.Length));
                    break;
                case TokenKind.Hex when area is FontArea.FontTable or FontArea.Alternate:
                    bytes.Add(token.Byte);
                    break;
            }
        }
        return Found();

        (IReadOnlyList<string>, IReadOnlyDictionary<string, string>) Found()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var number in used)
            {
                if (!table.TryGetValue(number, out var entry))
                    continue;
                names.Add(entry.Name);
                if (entry.Alternate is { } other && !other.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                    alternates.TryAdd(entry.Name, other);
            }
            return ([.. names], alternates);
        }
    }

    private enum FontArea
    {
        /// <summary>Text: \f sets the font text is in.</summary>
        Text,
        FontTable,

        /// <summary>A font's other name (\*\falt).</summary>
        Alternate,

        /// <summary>A group whose text isn't the document's.</summary>
        Skip,
    }
}
