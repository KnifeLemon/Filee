// Fixes two kinds of damage that some PDF writers (a few Chinese OA / ERP "print to PDF" exporters, issue #1175) leave
// behind and that PDFsharp refuses to read, so merge, split and compress failed on those files:
//   1. "stream" followed by a lone CR. The spec allows only CRLF or LF there.
//   2. An object closed with one ">>" too many (the bookmark entries in the file from #1175).
// The repair works on the bytes in memory and writes a fresh classic xref table, so the file on disk is never touched.
// Files it cannot judge (encrypted, or using object / xref streams) are returned as null and fail as before.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Pdf;

internal static class PdfRepair
{
    /// <summary>Above this size the repair is not tried (it holds the file as text).</summary>
    private const int MaxBytes = 256 * 1024 * 1024;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static readonly Regex LoneCrStream = new(@"(?<=>)[ \t\r\n]*stream\r(?!\n)", RegexOptions.CultureInvariant);
    private static readonly Regex ObjectBody = new(@"((?:^|(?<=[\r\n]))\d+ \d+ obj)(.*?)(endobj)", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectHeader = new(@"(?:^|(?<=[\r\n]))(\d+) (\d+) obj\b", RegexOptions.CultureInvariant);
    private static readonly Regex RootRef = new(@"/Root\s+(\d+)\s+(\d+)\s+R", RegexOptions.CultureInvariant);
    private static readonly Regex InfoRef = new(@"/Info\s+(\d+)\s+(\d+)\s+R", RegexOptions.CultureInvariant);

    /// <summary>True when a "stream" keyword is followed by a lone CR: cheap to check, so it runs on every file.</summary>
    public static bool HasLoneCrStream(ReadOnlySpan<byte> pdf)
    {
        ReadOnlySpan<byte> keyword = "stream\r"u8;
        var from = 0;
        while (from < pdf.Length)
        {
            var found = pdf[from..].IndexOf(keyword);
            if (found < 0)
                return false;
            var at = from + found;
            var next = at + keyword.Length;
            var isEndstream = at >= 3 && pdf[at - 3] == (byte)'e' && pdf[at - 2] == (byte)'n' && pdf[at - 1] == (byte)'d';
            if (!isEndstream && (next >= pdf.Length || pdf[next] != (byte)'\n'))
                return true;
            from = next;
        }
        return false;
    }

    /// <returns>The repaired PDF, or null when the file is out of reach of this repair.</returns>
    public static byte[]? Repair(byte[] pdf)
    {
        if (pdf.Length is 0 or > MaxBytes)
            return null;

        var text = Latin1.GetString(pdf); // one char per byte, so a string index is a file offset
        if (text.Contains("/Encrypt", StringComparison.Ordinal) || text.Contains("/ObjStm", StringComparison.Ordinal)
            || text.Contains("/XRef", StringComparison.Ordinal))
            return null;

        text = LoneCrStream.Replace(text, match => match.Value + "\n");
        text = ObjectBody.Replace(text, DropSurplusClose);

        var objects = new SortedDictionary<int, (int Offset, int Generation)>();
        foreach (Match match in ObjectHeader.Matches(text))
            objects[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] =
                (match.Index, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)); // the last copy of an object wins

        var root = RootRef.Matches(text).LastOrDefault();
        if (objects.Count == 0 || root is null)
            return null;
        var info = InfoRef.Matches(text).LastOrDefault();

        var size = objects.Keys.Max() + 1;
        var xrefOffset = text.Length;
        var table = new StringBuilder();
        table.Append("\nxref\n0 ").Append(size).Append('\n');
        table.Append("0000000000 65535 f\r\n");
        for (var number = 1; number < size; number++)
        {
            if (objects.TryGetValue(number, out var entry))
                table.Append(entry.Offset.ToString("D10", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(entry.Generation.ToString("D5", CultureInfo.InvariantCulture)).Append(" n\r\n");
            else
                table.Append("0000000000 00000 f\r\n");
        }
        xrefOffset += 1; // the leading "\n" of the table
        table.Append("trailer\n<< /Size ").Append(size)
            .Append(" /Root ").Append(root.Groups[1].Value).Append(' ').Append(root.Groups[2].Value).Append(" R");
        if (info is not null)
            table.Append(" /Info ").Append(info.Groups[1].Value).Append(' ').Append(info.Groups[2].Value).Append(" R");
        table.Append(" >>\nstartxref\n").Append(xrefOffset.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");

        return Latin1.GetBytes(text + table);
    }

    /// <summary>An object without a stream that has more ">>" than "&lt;&lt;" loses the surplus from its end.</summary>
    private static string DropSurplusClose(Match match)
    {
        var body = match.Groups[2].Value;
        if (body.Contains("stream", StringComparison.Ordinal))
            return match.Value;

        var open = Count(body, "<<");
        var close = Count(body, ">>");
        if (close <= open)
            return match.Value;

        var builder = new StringBuilder(body);
        for (; close > open; close--)
            builder.Remove(builder.ToString().LastIndexOf(">>", StringComparison.Ordinal), 2);
        return match.Groups[1].Value + builder + match.Groups[3].Value;
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
