// Mends the damage that some PDF writers (a few Chinese OA / ERP "print to PDF" exporters, issue #1175) leave behind and
// that PDFsharp refuses to read, so merge, split and compress failed on those files: an object closed with one ">>" too
// many (the bookmark entries in the file from #1175).
//
// The repair only runs after PDFsharp has failed on a file. It overwrites each surplus ">>" with two spaces, so every
// byte keeps its place and the file's own xref table stays valid; nothing is rebuilt. It only looks at objects the xref
// table lists (never at what a stream holds, such as an attached PDF), skips objects that have a stream (a "stream"
// keyword outside strings, names and comments), and does not count ">>" inside strings. Files it cannot judge
// (encrypted, or with an xref stream) are returned as null and fail as before. The file on disk is never touched.
//
// It works on the bytes (no text copy of the file), and each object is only searched up to the next listed object (and
// never more than MaxObjectBytes), so a file that fails for another reason is judged in one pass over it, not one per
// object. It can be cancelled.

namespace Filee.Engines.Pdf;

internal static class PdfRepair
{
    /// <summary>Above this size the repair is not tried (it holds the whole file in memory).</summary>
    private const int MaxBytes = 128 * 1024 * 1024;

    /// <summary>The most an object without a stream is searched for its "endobj".</summary>
    private const int MaxObjectBytes = 4 * 1024 * 1024;

    /// <summary>The most bytes "N G obj" may take at the start of an object.</summary>
    private const int MaxHeaderBytes = 32;

    /// <summary>Reads <paramref name="path"/> and repairs it; null when it is too big, unreadable, or out of reach of the repair.</summary>
    public static byte[]? RepairFile(string path, CancellationToken cancellationToken = default)
    {
        byte[] pdf;
        try
        {
            if (new FileInfo(path).Length is 0 or > MaxBytes)
                return null;
            pdf = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Repair(pdf, cancellationToken);
    }

    /// <returns>A repaired copy of the PDF, or null when there is nothing to mend or the file is out of reach of this repair.</returns>
    public static byte[]? Repair(byte[] pdf, CancellationToken cancellationToken = default)
    {
        if (pdf.Length is 0 or > MaxBytes)
            return null;
        if (pdf.AsSpan().IndexOf("/Encrypt"u8) >= 0)
            return null;

        // In file order, so that an object ends at the latest where the next listed one starts.
        var offsets = ListedObjects(pdf).Order().ToArray();
        byte[]? repaired = null;
        for (var k = 0; k < offsets.Length; k++)
        {
            if ((k & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var bound = k + 1 < offsets.Length ? offsets[k + 1] : pdf.Length;
            foreach (var at in SurplusClose(pdf, offsets[k], bound))
            {
                repaired ??= (byte[])pdf.Clone();
                repaired[at] = (byte)' ';
                repaired[at + 1] = (byte)' ';
            }
        }
        return repaired;
    }

    /// <summary>The offsets of the objects the file's classic xref table(s) list as in use; empty for an xref stream or no table.</summary>
    private static HashSet<int> ListedObjects(byte[] pdf)
    {
        var found = new HashSet<int>();
        var visited = new HashSet<int>();
        var at = LastStartXref(pdf);
        while (at is { } offset && visited.Add(offset))
            at = ReadTable(pdf, offset, found);
        return found;
    }

    private static int? LastStartXref(byte[] pdf)
    {
        var keyword = pdf.AsSpan().LastIndexOf("startxref"u8);
        if (keyword < 0)
            return null;
        var pos = keyword + "startxref".Length;
        return TryNumber(pdf, ref pos, out var offset) ? offset : null;
    }

    /// <summary>Adds the listed objects of the table at <paramref name="offset"/> to <paramref name="found"/>; returns the /Prev table, if any.</summary>
    private static int? ReadTable(byte[] pdf, int offset, HashSet<int> found)
    {
        if (offset < 0 || offset >= pdf.Length)
            return null;
        var pos = offset;
        SkipWhite(pdf, ref pos);
        if (!pdf.AsSpan(pos).StartsWith("xref"u8))
            return null; // an xref stream, or not a table at all

        pos += 4;
        while (true)
        {
            SkipWhite(pdf, ref pos);
            if (pdf.AsSpan(pos).StartsWith("trailer"u8))
            {
                pos += 7;
                break;
            }
            if (!TryNumber(pdf, ref pos, out _) || !TryNumber(pdf, ref pos, out var count))
                return null;
            for (var i = 0; i < count; i++)
            {
                if (!TryNumber(pdf, ref pos, out var entryOffset) || !TryNumber(pdf, ref pos, out _))
                    return null;
                SkipWhite(pdf, ref pos);
                if (pos >= pdf.Length)
                    return null;
                if (pdf[pos++] == (byte)'n' && entryOffset > 0)
                    found.Add(entryOffset);
            }
        }

        var trailer = pdf.AsSpan(pos);
        var end = trailer.IndexOf("startxref"u8);
        if (end >= 0)
            trailer = trailer[..end];
        var prev = trailer.IndexOf("/Prev"u8);
        if (prev < 0)
            return null;
        var from = pos + prev + "/Prev".Length;
        return TryNumber(pdf, ref from, out var previous) ? previous : null;
    }

    /// <summary>
    /// The offsets of the ">>" an object has more than "&lt;&lt;": the last ones. The object is only searched up to
    /// <paramref name="bound"/> (where the next listed object starts). Objects with a stream are left alone, and so are
    /// strings, hex strings and comments.
    /// </summary>
    private static List<int> SurplusClose(byte[] pdf, int offset, int bound)
    {
        var none = new List<int>();
        if (offset < 0 || offset >= pdf.Length)
            return none;

        var header = pdf.AsSpan(offset, Math.Min(MaxHeaderBytes, pdf.Length - offset)).IndexOf("obj"u8);
        if (header < 0)
            return none;
        for (var i = offset; i < offset + header; i++) // what sits at the offset must be "N G obj"
            if (!char.IsAsciiDigit((char)pdf[i]) && pdf[i] != (byte)' ')
                return none;

        var start = offset + header + 3;
        var stop = Math.Min(Math.Min(bound, pdf.Length), start + MaxObjectBytes);
        if (stop <= start)
            return none;
        var length = pdf.AsSpan(start, stop - start).IndexOf("endobj"u8);
        if (length < 0)
            return none;
        var end = start + length;

        var open = 0;
        var closes = new List<int>();
        for (var i = start; i < end; i++)
        {
            switch ((char)pdf[i])
            {
                case '(':
                    i = SkipLiteralString(pdf, i, end);
                    break;
                case '%':
                    while (i + 1 < end && pdf[i + 1] is not ((byte)'\r' or (byte)'\n'))
                        i++;
                    break;
                case '<' when i + 1 < end && pdf[i + 1] == (byte)'<':
                    open++;
                    i++;
                    break;
                case '<': // a hex string
                    var greater = pdf.AsSpan(i, end - i).IndexOf((byte)'>');
                    if (greater < 0)
                        return none;
                    i += greater;
                    break;
                case '>' when i + 1 < end && pdf[i + 1] == (byte)'>':
                    closes.Add(i);
                    i++;
                    break;
                case 's' when IsStreamKeyword(pdf, i, start):
                    return none; // outside strings and comments, so a title like "Upstream plan" does not count
            }
        }
        return closes.Count <= open ? none : closes.GetRange(open, closes.Count - open);
    }

    /// <summary>True when "stream" at <paramref name="at"/> is the keyword: after a ">>" or white space, before the end of the line.</summary>
    private static bool IsStreamKeyword(byte[] pdf, int at, int start)
    {
        if (!pdf.AsSpan(at).StartsWith("stream"u8) || at + 6 >= pdf.Length || at <= start)
            return false;
        var before = pdf[at - 1];
        return (before is (byte)'>' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0)
            && pdf[at + 6] is (byte)'\r' or (byte)'\n';
    }

    /// <summary>The index of the ")" that ends the literal string opened at <paramref name="open"/> (parentheses nest, "\" escapes).</summary>
    private static int SkipLiteralString(byte[] pdf, int open, int end)
    {
        var depth = 1;
        for (var i = open + 1; i < end; i++)
        {
            switch ((char)pdf[i])
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

    private static void SkipWhite(byte[] pdf, ref int pos)
    {
        while (pos < pdf.Length && pdf[pos] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0)
            pos++;
    }

    private static bool TryNumber(byte[] pdf, ref int pos, out int value)
    {
        SkipWhite(pdf, ref pos);
        var from = pos;
        while (pos < pdf.Length && char.IsAsciiDigit((char)pdf[pos]))
            pos++;
        value = 0;
        if (pos == from || pos - from > 10)
            return false;
        foreach (var digit in pdf.AsSpan(from, pos - from))
        {
            var next = (long)value * 10 + (digit - '0');
            if (next > int.MaxValue)
                return false;
            value = (int)next;
        }
        return true;
    }
}
