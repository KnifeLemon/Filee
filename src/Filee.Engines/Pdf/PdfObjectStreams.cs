// Packs the objects of a PDF PDFsharp saved into object streams with a cross-reference stream (PDF 1.5), which
// PDFsharp can read but doesn't write. Every object that isn't a stream (pages, fonts, annotations, outlines …) goes
// into a deflated object stream, and the cross-reference table becomes a deflated stream too: a document of many
// small objects shrinks by a tenth or more. Streams are copied as they are. The file is rewritten only when it gets
// smaller; an encrypted file, or one that isn't laid out as PDFsharp writes it, stays as it is.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Pdf;

/// <summary>Rewrites a saved PDF with object streams and a cross-reference stream.</summary>
internal static partial class PdfObjectStreams
{
    /// <summary>Objects per object stream: big enough to deflate well, small enough to read one object quickly.</summary>
    private const int ObjectsPerStream = 200;

    /// <summary>Rewrites <paramref name="path"/> in place when that makes it smaller.</summary>
    /// <returns>True when the file was rewritten.</returns>
    public static bool Rewrite(string path)
    {
        var data = File.ReadAllBytes(path);
        if (Pack(data) is not { } packed || packed.Length >= data.Length)
            return false;
        File.WriteAllBytes(path, packed);
        return true;
    }

    /// <summary>The packed file, or null when <paramref name="data"/> can't be packed.</summary>
    internal static byte[]? Pack(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        var startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (startxref < 0 || !int.TryParse(Token(text, startxref + "startxref".Length), CultureInfo.InvariantCulture, out var xref)
            || xref <= 0 || xref >= startxref || !text.AsSpan(xref).StartsWith("xref"))
            return null; // already a cross-reference stream, or not PDFsharp's layout
        var trailerAt = text.IndexOf("trailer", xref, StringComparison.Ordinal);
        if (trailerAt < 0 || trailerAt > startxref)
            return null;
        // PDFsharp writes a comment after the dictionary.
        var trailerEnd = text.LastIndexOf(">>", startxref, startxref - trailerAt, StringComparison.Ordinal);
        if (trailerEnd < 0)
            return null;
        var trailer = text[(trailerAt + "trailer".Length)..(trailerEnd + 2)].Trim();
        // An object in an object stream is encrypted with the stream, not on its own: PDFsharp encrypted each one.
        if (!trailer.StartsWith("<<", StringComparison.Ordinal) || !trailer.EndsWith(">>", StringComparison.Ordinal) || trailer.Contains("/Encrypt"))
            return null;
        if (ReadTable(text, xref, trailerAt) is not { } offsets)
            return null;

        // Each object's text between "n g obj" and "endobj"; objects follow each other, so one ends before the next.
        var starts = offsets.Values.Append(xref).Order().ToArray();
        // The document information stays readable as it is; it changes on every save (ModDate), and packed with the
        // rest it would make the packed size wobble by a byte from one save of the same document to the next.
        var info = TrailerEntry(trailer, "/Info") is { } infoRef && int.TryParse(infoRef.Split(' ')[0], CultureInfo.InvariantCulture, out var infoNumber) ? infoNumber : -1;
        var plain = new List<(int Number, string Body)>();
        var streams = new List<(int Number, int Generation, int Start, int End)>();
        foreach (var (number, offset) in offsets)
        {
            var end = starts[Array.BinarySearch(starts, offset) + 1];
            var header = ObjectHeader().Match(text, offset);
            var close = text.LastIndexOf("endobj", end - 1, end - offset, StringComparison.Ordinal);
            if (!header.Success || header.Index != offset || int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture) != number || close < header.Index + header.Length)
                return null;
            var generation = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
            var body = text[(header.Index + header.Length)..close].Trim();
            if (generation == 0 && number != info && !body.EndsWith("endstream", StringComparison.Ordinal))
                plain.Add((number, body));
            else
                streams.Add((number, generation, offset, close + "endobj".Length));
        }
        if (plain.Count == 0)
            return null;

        var size = Math.Max(offsets.Keys.Max() + 1, TrailerInteger(trailer, "/Size") ?? 0);
        using var output = new MemoryStream(data.Length);
        var version = PdfVersion(text);
        Write(output, $"%PDF-{(version < 15 ? "1.5" : $"{version / 10}.{version % 10}")}\n%âãÏÓ\n");

        // Type 1: at an offset in the file; type 2: in an object stream, at an index.
        var entries = new Dictionary<int, (int Type, long Field2, int Field3)>();
        foreach (var (number, generation, start, end) in streams)
        {
            entries[number] = (1, output.Position, generation);
            output.Write(data, start, end - start);
            Write(output, "\n");
        }

        var next = size;
        foreach (var chunk in plain.Chunk(ObjectsPerStream))
        {
            var number = next++;
            var header = new StringBuilder();
            var body = new StringBuilder();
            for (var i = 0; i < chunk.Length; i++)
            {
                header.Append(CultureInfo.InvariantCulture, $"{chunk[i].Number} {body.Length} ");
                body.Append(chunk[i].Body).Append('\n');
                entries[chunk[i].Number] = (2, number, i);
            }
            var first = header.Length;
            var packed = Deflate(Encoding.Latin1.GetBytes(header.Append(body).ToString()));
            entries[number] = (1, output.Position, 0);
            Write(output, $"{number} 0 obj\n<</Type/ObjStm/N {chunk.Length}/First {first}/Filter/FlateDecode/Length {packed.Length}>>\nstream\n");
            output.Write(packed);
            Write(output, "\nendstream\nendobj\n");
        }

        // The cross-reference stream lists itself too.
        var xrefNumber = next++;
        var xrefOffset = output.Position;
        entries[xrefNumber] = (1, xrefOffset, 0);
        var rows = new byte[next * 7];
        for (var n = 0; n < next; n++)
        {
            var (type, field2, field3) = entries.TryGetValue(n, out var entry) ? entry : (0, 0L, n == 0 ? 65535 : 0);
            var row = rows.AsSpan(n * 7, 7);
            row[0] = (byte)type;
            for (var b = 0; b < 4; b++)
                row[1 + b] = (byte)(field2 >> (24 - 8 * b));
            row[5] = (byte)(field3 >> 8);
            row[6] = (byte)field3;
        }
        var table = Deflate(rows);
        var keep = new StringBuilder();
        foreach (var key in new[] { "/Root", "/Info", "/ID" })
        {
            if (TrailerEntry(trailer, key) is { } value)
                keep.Append(key).Append(' ').Append(value);
        }
        Write(output, $"{xrefNumber} 0 obj\n<</Type/XRef/Size {next}/W[1 4 2]{keep}/Filter/FlateDecode/Length {table.Length}>>\nstream\n");
        output.Write(table);
        Write(output, $"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>The in-use objects of a classic cross-reference table and their offsets; null when damaged.</summary>
    private static Dictionary<int, int>? ReadTable(string text, int xref, int end)
    {
        var offsets = new Dictionary<int, int>();
        var lines = text[(xref + 4)..end].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var number = 0;
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var first))
            {
                number = first;
                continue;
            }
            if (parts.Length != 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var offset))
                return null;
            if (parts[2] == "n" && offset > 0)
                offsets[number] = offset;
            number++;
        }
        return offsets.Count > 0 ? offsets : null;
    }

    /// <summary>The value of a trailer entry: a reference, an array or a name, as written.</summary>
    private static string? TrailerEntry(string trailer, string key)
    {
        var match = Regex.Match(trailer, Regex.Escape(key) + @"(?![A-Za-z])\s*(\d+\s+\d+\s+R|\[[^\]]*\]|/[^\s/<>\[\]()]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int? TrailerInteger(string trailer, string key) =>
        Regex.Match(trailer, Regex.Escape(key) + @"\s*(\d+)(?!\s+\d+\s+R)") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>The header's version as 14 for 1.4.</summary>
    private static int PdfVersion(string text)
    {
        var match = Regex.Match(text.Length > 32 ? text[..32] : text, @"%PDF-(\d)\.(\d)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 10 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 14;
    }

    private static string Token(string text, int start)
    {
        var span = text.AsSpan(start).TrimStart();
        var length = 0;
        while (length < span.Length && char.IsAsciiDigit(span[length]))
            length++;
        return span[..length].ToString();
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    [GeneratedRegex(@"(\d+)\s+(\d+)\s+obj\b")]
    private static partial Regex ObjectHeader();
}
