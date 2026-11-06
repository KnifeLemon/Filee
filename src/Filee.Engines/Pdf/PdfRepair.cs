// Mends the damage that some PDF writers (a few Chinese OA / ERP "print to PDF" exporters, issue #1175) leave behind and
// that PDFsharp refuses to read, so merge, split and compress failed on those files: an object closed with one ">>" too
// many (the bookmark entries in the file from #1175).
//
// The repair only runs after PDFsharp has failed on a file. It overwrites each surplus ">>" with two spaces, so every
// byte keeps its place and the file's own xref table stays valid; nothing is rebuilt. It only looks at objects the xref
// table lists (never at what a stream holds, such as an attached PDF), skips objects that have a stream, and does not
// count ">>" inside strings. Files it cannot judge (encrypted, or with an xref stream) are returned as null and fail as
// before. The file on disk is never touched.

using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Pdf;

internal static class PdfRepair
{
    /// <summary>Above this size the repair is not tried (it holds the file as text).</summary>
    private const int MaxBytes = 256 * 1024 * 1024;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static readonly Regex PrevRef = new(@"/Prev\s+(\d+)", RegexOptions.CultureInvariant);

    /// <summary>Reads <paramref name="path"/> and repairs it; null when it is too big, unreadable, or out of reach of the repair.</summary>
    public static byte[]? RepairFile(string path)
    {
        try
        {
            return new FileInfo(path).Length is 0 or > MaxBytes ? null : Repair(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <returns>A repaired copy of the PDF, or null when there is nothing to mend or the file is out of reach of this repair.</returns>
    public static byte[]? Repair(byte[] pdf)
    {
        if (pdf.Length is 0 or > MaxBytes)
            return null;

        var text = Latin1.GetString(pdf); // one char per byte, so a string index is a file offset
        if (text.Contains("/Encrypt", StringComparison.Ordinal))
            return null;

        byte[]? repaired = null;
        foreach (var offset in ListedObjects(text))
        {
            foreach (var at in SurplusClose(text, offset))
            {
                repaired ??= (byte[])pdf.Clone();
                repaired[at] = (byte)' ';
                repaired[at + 1] = (byte)' ';
            }
        }
        return repaired;
    }

    /// <summary>The offsets of the objects the file's classic xref table(s) list as in use; empty for an xref stream or no table.</summary>
    private static HashSet<int> ListedObjects(string text)
    {
        var found = new HashSet<int>();
        var visited = new HashSet<int>();
        var at = LastStartXref(text);
        while (at is { } offset && visited.Add(offset))
            at = ReadTable(text, offset, found);
        return found;
    }

    private static int? LastStartXref(string text)
    {
        var keyword = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (keyword < 0)
            return null;
        var pos = keyword + "startxref".Length;
        return TryNumber(text, ref pos, out var offset) ? offset : null;
    }

    /// <summary>Adds the listed objects of the table at <paramref name="offset"/> to <paramref name="found"/>; returns the /Prev table, if any.</summary>
    private static int? ReadTable(string text, int offset, HashSet<int> found)
    {
        if (offset < 0 || offset >= text.Length)
            return null;
        var pos = offset;
        SkipWhite(text, ref pos);
        if (string.CompareOrdinal(text, pos, "xref", 0, 4) != 0)
            return null; // an xref stream, or not a table at all

        pos += 4;
        while (true)
        {
            SkipWhite(text, ref pos);
            if (string.CompareOrdinal(text, pos, "trailer", 0, 7) == 0)
            {
                pos += 7;
                break;
            }
            if (!TryNumber(text, ref pos, out _) || !TryNumber(text, ref pos, out var count))
                return null;
            for (var i = 0; i < count; i++)
            {
                if (!TryNumber(text, ref pos, out var entryOffset) || !TryNumber(text, ref pos, out _))
                    return null;
                SkipWhite(text, ref pos);
                if (pos >= text.Length)
                    return null;
                if (text[pos++] == 'n' && entryOffset > 0)
                    found.Add(entryOffset);
            }
        }

        var end = text.IndexOf("startxref", pos, StringComparison.Ordinal);
        var prev = PrevRef.Match(text, pos, (end < 0 ? text.Length : end) - pos);
        return prev.Success && int.TryParse(prev.Groups[1].Value, out var previous) ? previous : null;
    }

    /// <summary>
    /// The offsets of the ">>" an object has more than "&lt;&lt;": the last ones. Objects with a stream are left alone, and
    /// so are strings, hex strings and comments.
    /// </summary>
    private static List<int> SurplusClose(string text, int offset)
    {
        var none = new List<int>();
        var header = text.IndexOf("obj", offset, StringComparison.Ordinal);
        if (header < 0 || header - offset > 24)
            return none;
        for (var i = offset; i < header; i++) // what sits at the offset must be "N G obj"
            if (!char.IsAsciiDigit(text[i]) && text[i] != ' ')
                return none;

        var start = header + 3;
        var end = text.IndexOf("endobj", start, StringComparison.Ordinal);
        if (end < 0 || text.AsSpan(start, end - start).IndexOf("stream", StringComparison.Ordinal) >= 0)
            return none;

        var open = 0;
        var closes = new List<int>();
        for (var i = start; i < end; i++)
        {
            switch (text[i])
            {
                case '(':
                    i = SkipLiteralString(text, i, end);
                    break;
                case '%':
                    while (i + 1 < end && text[i + 1] is not ('\r' or '\n'))
                        i++;
                    break;
                case '<' when i + 1 < end && text[i + 1] == '<':
                    open++;
                    i++;
                    break;
                case '<': // a hex string
                    var greater = text.IndexOf('>', i, end - i);
                    if (greater < 0)
                        return none;
                    i = greater;
                    break;
                case '>' when i + 1 < end && text[i + 1] == '>':
                    closes.Add(i);
                    i++;
                    break;
            }
        }
        return closes.Count <= open ? none : closes.GetRange(open, closes.Count - open);
    }

    /// <summary>The index of the ")" that ends the literal string opened at <paramref name="open"/> (parentheses nest, "\" escapes).</summary>
    private static int SkipLiteralString(string text, int open, int end)
    {
        var depth = 1;
        for (var i = open + 1; i < end; i++)
        {
            switch (text[i])
            {
                case '\\':
                    i++;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                        return i;
                    break;
            }
        }
        return end;
    }

    private static void SkipWhite(string text, ref int pos)
    {
        while (pos < text.Length && text[pos] is ' ' or '\t' or '\r' or '\n' or '\f' or '\0')
            pos++;
    }

    private static bool TryNumber(string text, ref int pos, out int value)
    {
        SkipWhite(text, ref pos);
        var from = pos;
        while (pos < text.Length && char.IsAsciiDigit(text[pos]))
            pos++;
        value = 0;
        return pos > from && pos - from <= 10 && int.TryParse(text.AsSpan(from, pos - from), out value);
    }
}
