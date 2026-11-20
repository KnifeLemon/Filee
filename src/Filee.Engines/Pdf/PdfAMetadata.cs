// Saves a PDF made to keep the PDF/A-2b rules (PdfAConformance) with the metadata PDF/A asks for (ISO 19005-2 6.6):
// an XMP packet that says pdfaid:part 2, conformance B, and holds the same title, author, subject, keywords, creator
// tool, producer and dates as the document information dictionary, uncompressed in the catalog's /Metadata.
//
// PDFsharp can't be stopped from writing metadata of its own when it saves: it puts its name into the producer, sets
// the modification date, and replaces /Metadata with an XMP packet of its own, without pdfaid and with dates that
// don't always match the dictionary (PdfDocument.SetPdfA claims PDF/A-1a, which this isn't). So the PDF is saved by
// PDFsharp first and then given an incremental update (ISO 32000-1 7.5.6, which PDF/A allows) that writes the two
// objects again under their own numbers: the document information and the XMP packet. The catalog keeps pointing to
// the packet's number; the update's trailer has the same /ID PDFsharp wrote and a /Prev to PDFsharp's cross-reference
// table. Text in the dictionary is UTF-16 with a byte order mark; the XMP holds the same characters, with those XML
// can't carry left out of both, so the two always match.

using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;

namespace Filee.Engines.Pdf;

/// <summary>Document information written into a PDF/A; null entries are left out.</summary>
internal sealed record PdfAInfo
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }

    /// <summary>The program the document was made in (Info /Creator, xmp:CreatorTool).</summary>
    public string? Creator { get; init; }

    /// <summary>The program that wrote the PDF (Info /Producer, pdf:Producer).</summary>
    public string? Producer { get; init; }

    public DateTimeOffset? Created { get; init; }
}

/// <summary>Saves a PDF/A with matching XMP and document information.</summary>
internal static partial class PdfAMetadata
{
    /// <summary>Characters of one text entry kept at most (a PDF/A string holds 32767 bytes, 6.1.13).</summary>
    internal const int MaxText = 4000;

    /// <summary>The document information <paramref name="document"/> has now, for <see cref="Save"/>.</summary>
    public static PdfAInfo InfoOf(PdfDocument document)
    {
        var info = document.Info;
        string? Text(string key) => info.Elements.GetString(key) is { Length: > 0 } text ? text : null;
        DateTimeOffset? created = null;
        if (info.Elements.ContainsKey("/CreationDate"))
        {
            try
            {
                if (info.CreationDate is var date && date != DateTime.MinValue)
                    created = new DateTimeOffset(DateTime.SpecifyKind(date, date.Kind == DateTimeKind.Utc ? DateTimeKind.Utc : DateTimeKind.Local));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException)
            {
                // A date PDFsharp can't read: the PDF/A gets today's.
            }
        }
        return new PdfAInfo
        {
            Title = Text("/Title"),
            Author = Text("/Author"),
            Subject = Text("/Subject"),
            Keywords = Text("/Keywords"),
            Creator = Text("/Creator"),
            Producer = Text("/Producer"),
            Created = created,
        };
    }

    /// <summary>
    /// Saves <paramref name="document"/> to <paramref name="output"/> as PDF/A-2b with <paramref name="info"/> (by
    /// default what the document has, read before PDFsharp changes it on saving); the modification date is now.
    /// </summary>
    public static void Save(PdfDocument document, Stream output, PdfAInfo? info = null, DateTimeOffset? now = null)
    {
        info ??= InfoOf(document);
        var modified = Seconds(now ?? DateTimeOffset.Now);
        var created = Seconds(info.Created ?? modified);

        using var buffer = new MemoryStream();
        document.Save(buffer, closeStream: false);
        var saved = buffer.ToArray();

        var metadata = document.Internals.Catalog.Elements.GetReference("/Metadata")
            ?? throw new InvalidOperationException("PDFsharp wrote no XMP packet.");
        var information = document.Info.Reference ?? throw new InvalidOperationException("PDFsharp wrote no document information.");
        var trailer = Trailer.Read(saved);

        var entries = new List<(string Key, string Text)>
        {
            ("/Title", Clean(info.Title)),
            ("/Author", Clean(info.Author)),
            ("/Subject", Clean(info.Subject)),
            ("/Keywords", Clean(info.Keywords)),
            ("/Creator", Clean(info.Creator)),
            ("/Producer", Clean(info.Producer)),
        };
        entries.RemoveAll(e => e.Text.Length == 0);

        using var update = new MemoryStream();
        output.Write(saved);
        var offset = (long)saved.Length;
        if (saved.Length > 0 && saved[^1] is not (byte)'\n' and not (byte)'\r')
            Write(update, "\n");

        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        offsets[information.ObjectNumber] = (offset + update.Length, information.GenerationNumber);
        var dictionary = new StringBuilder($"{information.ObjectNumber} {information.GenerationNumber} obj\n<<");
        foreach (var (key, text) in entries)
            dictionary.Append(key).Append(' ').Append(HexText(text));
        dictionary.Append("/CreationDate ").Append(PdfDate(created)).Append("/ModDate ").Append(PdfDate(modified)).Append(">>\nendobj\n");
        Write(update, dictionary.ToString());

        var xmp = Encoding.UTF8.GetBytes(Xmp(entries.ToDictionary(e => e.Key, e => e.Text), created, modified, trailer.Id[0]));
        offsets[metadata.ObjectNumber] = (offset + update.Length, metadata.GenerationNumber);
        Write(update, $"{metadata.ObjectNumber} {metadata.GenerationNumber} obj\n<</Type/Metadata/Subtype/XML/Length {xmp.Length}>>\nstream\n");
        update.Write(xmp);
        Write(update, "\nendstream\nendobj\n");

        var xref = offset + update.Length;
        var table = new StringBuilder("xref\n");
        var numbers = offsets.Keys.ToList();
        for (var start = 0; start < numbers.Count;)
        {
            var end = start;
            while (end + 1 < numbers.Count && numbers[end + 1] == numbers[end] + 1)
                end++;
            table.Append(CultureInfo.InvariantCulture, $"{numbers[start]} {end - start + 1}\n");
            for (var i = start; i <= end; i++)
            {
                var (at, generation) = offsets[numbers[i]];
                table.Append(CultureInfo.InvariantCulture, $"{at:D10} {generation:D5} n\r\n"); // 20 bytes an entry
            }
            start = end + 1;
        }
        var size = Math.Max(trailer.Size, numbers[^1] + 1);
        table.Append(CultureInfo.InvariantCulture,
            $"trailer\n<</Size {size}/Root {trailer.Root}/Info {information.ObjectNumber} {information.GenerationNumber} R/ID[<{trailer.Id[0]}><{trailer.Id[1]}>]/Prev {trailer.StartXref}>>\nstartxref\n{xref}\n%%EOF\n");
        Write(update, table.ToString());
        update.Position = 0;
        update.CopyTo(output);
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));

    private static DateTimeOffset Seconds(DateTimeOffset date) =>
        new(date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second, date.Offset);

    /// <summary>
    /// Text both the dictionary and XML can carry the same: no control characters but tab and line feed (XML reads a
    /// carriage return as a line feed), lone surrogates as U+FFFD, at most <see cref="MaxText"/> characters.
    /// </summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var kept = new StringBuilder(Math.Min(normalized.Length, MaxText));
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (kept.Length + rune.Utf16SequenceLength > MaxText)
                break;
            if (rune.Value is < 0x20 and not ('\t' or '\n') or 0xFFFE or 0xFFFF)
                continue;
            kept.Append(rune.ToString());
        }
        return kept.ToString().Trim();
    }

    /// <summary>A text string as UTF-16 with a byte order mark, in hex.</summary>
    private static string HexText(string text) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    /// <summary>A PDF date (ISO 32000-1 7.9.4) with its offset from UTC.</summary>
    internal static string PdfDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"(D:{date:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}')");
    }

    /// <summary>The same date as XMP writes it (ISO 8601).</summary>
    internal static string XmpDate(DateTimeOffset date) =>
        date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>The XMP packet: PDF/A identification and the document information's entries in their XMP properties.</summary>
    internal static string Xmp(IReadOnlyDictionary<string, string> entries, DateTimeOffset created, DateTimeOffset modified, string id)
    {
        static string X(string text) => SecurityElement.Escape(text);
        var xmp = new StringBuilder();
        xmp.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        xmp.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        xmp.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"");
        xmp.Append(" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\">\n");
        xmp.Append("<pdfaid:part>2</pdfaid:part>\n<pdfaid:conformance>B</pdfaid:conformance>\n<dc:format>application/pdf</dc:format>\n");
        if (entries.TryGetValue("/Title", out var title))
            xmp.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(X(title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        if (entries.TryGetValue("/Author", out var author))
            xmp.Append("<dc:creator><rdf:Seq><rdf:li>").Append(X(author)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        if (entries.TryGetValue("/Subject", out var subject))
            xmp.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(X(subject)).Append("</rdf:li></rdf:Alt></dc:description>\n");
        if (entries.TryGetValue("/Keywords", out var keywords))
            xmp.Append("<pdf:Keywords>").Append(X(keywords)).Append("</pdf:Keywords>\n");
        if (entries.TryGetValue("/Creator", out var creator))
            xmp.Append("<xmp:CreatorTool>").Append(X(creator)).Append("</xmp:CreatorTool>\n");
        if (entries.TryGetValue("/Producer", out var producer))
            xmp.Append("<pdf:Producer>").Append(X(producer)).Append("</pdf:Producer>\n");
        xmp.Append("<xmp:CreateDate>").Append(XmpDate(created)).Append("</xmp:CreateDate>\n");
        xmp.Append("<xmp:ModifyDate>").Append(XmpDate(modified)).Append("</xmp:ModifyDate>\n");
        xmp.Append("<xmp:MetadataDate>").Append(XmpDate(modified)).Append("</xmp:MetadataDate>\n");
        xmp.Append("<xmpMM:DocumentID>").Append(Uuid(id)).Append("</xmpMM:DocumentID>\n");
        xmp.Append("</rdf:Description>\n</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return xmp.ToString();
    }

    /// <summary>The file identifier as a UUID URN when it has 16 bytes, else as it is.</summary>
    private static string Uuid(string id) => id.Length == 32 && Guid.TryParseExact(id, "N", out var guid)
        ? "uuid:" + guid.ToString("D")
        : "uuid:" + id.ToLowerInvariant();

    /// <summary>What the update needs from the trailer PDFsharp wrote.</summary>
    private sealed record Trailer(string Root, int Size, string[] Id, long StartXref)
    {
        /// <summary>Reads the last trailer of <paramref name="pdf"/>, which must be PDFsharp's cross-reference table.</summary>
        public static Trailer Read(byte[] pdf)
        {
            // The trailer, startxref and %%EOF are in the last kilobytes.
            var tail = Encoding.Latin1.GetString(pdf, Math.Max(0, pdf.Length - 4096), Math.Min(pdf.Length, 4096));
            var at = tail.LastIndexOf("trailer", StringComparison.Ordinal);
            if (at < 0)
                throw new InvalidOperationException("PDFsharp wrote no trailer to update.");
            var text = tail[at..];
            var root = RootEntry().Match(text);
            var size = SizeEntry().Match(text);
            var start = StartXrefEntry().Match(text);
            if (!root.Success || !size.Success || !start.Success)
                throw new InvalidOperationException("PDFsharp's trailer can't be read.");
            var id = IdEntry().Match(text);
            var first = id.Success ? id.Groups[1].Value : Convert.ToHexString(Guid.NewGuid().ToByteArray());
            var second = id.Success ? id.Groups[2].Value : first;
            return new Trailer($"{root.Groups[1].Value} {root.Groups[2].Value} R", int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture),
                [first, second], long.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture));
        }
    }

    [GeneratedRegex(@"/Root\s*(\d+)\s+(\d+)\s+R", RegexOptions.CultureInvariant)]
    private static partial Regex RootEntry();

    [GeneratedRegex(@"/Size\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SizeEntry();

    [GeneratedRegex(@"/ID\s*\[\s*<([0-9A-Fa-f]*)>\s*<([0-9A-Fa-f]*)>\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex IdEntry();

    [GeneratedRegex(@"startxref\s+(\d+)\s+%%EOF\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StartXrefEntry();
}
