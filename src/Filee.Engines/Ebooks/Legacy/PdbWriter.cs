// Book → PDB as PalmDOC (TEXtREAd), from the PalmDOC format description: the book's text (PlainTextWriter, newline
// line ends, Windows-1252 or UTF-8 as LegacyWriting.Encode picks) in records of 4,096 bytes, each PalmDOC-compressed
// (the LZ77 variant MobiWriter uses too). Record 0 is the 16-byte PalmDOC header: compression 2, the text length,
// the number of text records, the record size and the reading position 0. After the text come bookmark records,
// one per heading of the top three levels, 16 bytes of name and the heading's byte offset in the text: Palm readers
// show them as the book's contents. The database name is the title.

using System.Buffers.Binary;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class PdbWriter
{
    private const int RecordSize = 4096;
    private const int BookmarkNameLength = 16;

    public static void Write(Book book, string outputPath, string sourcePath, CancellationToken cancellationToken = default)
    {
        var text = LegacyWriting.PlainText(book.Document, "\n");
        var (bytes, encoding) = LegacyWriting.Encode(text);
        LegacyWriting.CheckText(bytes.Length, "PDB");

        var records = new List<byte[]> { Header(bytes.Length, (bytes.Length + RecordSize - 1) / RecordSize) };
        for (var offset = 0; offset < bytes.Length; offset += RecordSize)
        {
            if ((offset & 0xFFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            records.Add(PalmDocCompression.Compress(bytes.AsSpan(offset, Math.Min(RecordSize, bytes.Length - offset))));
        }
        foreach (var (name, position) in Bookmarks(book.Document, text, encoding))
        {
            if (records.Count >= PalmDatabase.MaxRecords)
                break;
            records.Add(Bookmark(name, position));
        }
        var file = PalmDatabase.Build(book.DisplayTitle(sourcePath), "TEXtREAd", records);
        cancellationToken.ThrowIfCancellationRequested();
        WriterTempFile.Write(outputPath, stream => stream.Write(file));
    }

    /// <summary>Record 0: compression 2 (PalmDOC), unused, text length, text records, record size, position.</summary>
    private static byte[] Header(int textLength, int textRecords)
    {
        var header = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(header, 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)textLength);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)textRecords);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), RecordSize);
        return header;
    }

    /// <summary>A bookmark record: the name in 16 bytes (Windows-1252, '?' for what it lacks, zero-padded) and the offset.</summary>
    private static byte[] Bookmark(string name, long position)
    {
        var record = new byte[BookmarkNameLength + 4];
        var bytes = LegacyWriting.Windows1252.GetBytes(name);
        bytes.AsSpan(0, Math.Min(bytes.Length, BookmarkNameLength - 1)).CopyTo(record);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(BookmarkNameLength), (uint)position);
        return record;
    }

    /// <summary>
    /// The headings of the top three levels with the byte offset of their text in <paramref name="text"/>, found in
    /// order (a heading the text doesn't hold as written, e.g. one with a line break, is left out).
    /// </summary>
    internal static IEnumerable<(string Name, long Position)> Bookmarks(HDocument document, string text, System.Text.Encoding encoding)
    {
        var from = 0;
        long bytes = 0;
        foreach (var heading in document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Where(p => p.HeadingLevel is >= 1 and <= 3))
        {
            var title = HDocumentWalker.Text(heading.Inlines).Trim();
            if (title.Length == 0)
                continue;
            var at = text.IndexOf(title, from, StringComparison.Ordinal);
            if (at < 0)
                continue;
            bytes += encoding.GetByteCount(text.AsSpan(from, at - from));
            from = at;
            yield return (title, bytes);
        }
    }
}
