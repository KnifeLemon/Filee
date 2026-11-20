// Book → RB (NuvoMedia Rocket eBook), the layout RocketBookReader reads: a 0x128-byte header (B0 0C B0 0C, version
// 2, "NUVO", the offset of the table of contents at 0x18 and the file's length at 0x1C, little-endian), the table
// (the file count, then per file a 32-byte name, its length, offset and flags) and the files:
//  * the info page (flag 8, stored): TITLE, AUTHOR and BODY lines, in Windows-1252 when they fit, else UTF-8;
//  * the book as one HTML page (flag 1, compressed): the chunk count, the page's size, each chunk's size, then the
//    chunks, each a zlib stream of 4 KB of the page. The page is the XhtmlWriter body (headings, bold, italic,
//    links, anchors, lists, tables, notes) without a style sheet, every character beyond ASCII written as a
//    character reference, so it reads the same in any character set;
//  * the pictures (stored) as PNG, "img0001.png" and on.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class RocketBookWriter
{
    private const int TocOffset = 0x128;
    private const int ChunkSize = 4096;
    private const int NameLength = 32;

    /// <summary>Files RocketBookReader takes in one book.</summary>
    private const int MaxFiles = 10_000;

    private const string PageName = "book.html";

    public static void Write(Book book, string outputPath, string sourcePath, string workFolder, CancellationToken cancellationToken = default)
    {
        var limits = DocumentLimits.Default;
        var title = book.DisplayTitle(sourcePath);
        var convert = EbookFiles.NewFolder(workFolder, "rb-pictures");
        var pictures = new List<(string Name, string File)>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? Picture(string path)
        {
            if (names.TryGetValue(path, out var known))
                return known;
            if (pictures.Count >= MaxFiles - 2 || LegacyWriting.Png(path, convert) is not { } file)
                return null;
            var name = $"img{pictures.Count + 1:0000}.png";
            pictures.Add((name, file));
            return names[path] = name;
        }

        var body = XhtmlWriter.Write(book.Document, new XhtmlOptions { ImageSource = Picture }).Single().Body;
        cancellationToken.ThrowIfCancellationRequested();
        var html = Ascii($"<html>\n<head>\n<title>{XhtmlWriter.Escape(title)}</title>\n</head>\n<body>\n{body}</body>\n</html>\n");
        LegacyWriting.CheckText(html.Length, "RB");

        var info = new StringBuilder();
        info.Append("TITLE=").Append(OneLine(title)).Append("\r\n");
        if (book.Metadata.Authors.Count > 0)
            info.Append("AUTHOR=").Append(OneLine(string.Join(", ", book.Metadata.Authors))).Append("\r\n");
        info.Append("BODY=").Append(PageName).Append("\r\n");

        var files = new List<(string Name, byte[] Data, int Flags)>
        {
            (Path.ChangeExtension(PageName, ".info"), LegacyWriting.Encode(info.ToString()).Bytes, 8),
            (PageName, Compress(Encoding.ASCII.GetBytes(html), cancellationToken), 1),
        };
        long total = files.Sum(f => (long)f.Data.Length);
        foreach (var (name, file) in pictures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = File.ReadAllBytes(file);
            if ((total += data.Length) > limits.MaxPackageBytes)
                throw DocumentLimits.Exceeded($"The RB book would be larger than {DocumentLimits.Megabytes(limits.MaxPackageBytes)}.");
            files.Add((name, data, 0));
        }
        var rb = Container(files);
        WriterTempFile.Write(outputPath, stream => stream.Write(rb));
    }

    /// <summary>The header, the table of contents and the files, in order.</summary>
    internal static byte[] Container(IReadOnlyList<(string Name, byte[] Data, int Flags)> files)
    {
        var dataOffset = TocOffset + 4 + files.Count * 44;
        var length = dataOffset + files.Sum(f => (long)f.Data.Length);
        if (length > int.MaxValue)
            throw DocumentLimits.Exceeded("The RB book would be larger than 2 GB.");
        var rb = new byte[length];
        ((ReadOnlySpan<byte>)[0xB0, 0x0C, 0xB0, 0x0C, 0x02, 0x00]).CopyTo(rb);
        "NUVO"u8.CopyTo(rb.AsSpan(6));
        BinaryPrimitives.WriteUInt32LittleEndian(rb.AsSpan(0x18), TocOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(rb.AsSpan(0x1C), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(rb.AsSpan(TocOffset), (uint)files.Count);
        var offset = dataOffset;
        for (var i = 0; i < files.Count; i++)
        {
            var (name, data, flags) = files[i];
            var entry = rb.AsSpan(TocOffset + 4 + i * 44, 44);
            var nameBytes = Encoding.ASCII.GetBytes(name);
            nameBytes.AsSpan(0, Math.Min(nameBytes.Length, NameLength - 1)).CopyTo(entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[32..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[40..], (uint)flags);
            data.CopyTo(rb, offset);
            offset += data.Length;
        }
        return rb;
    }

    /// <summary>A compressed file: chunk count, size, chunk sizes, then the zlib chunks of 4 KB each.</summary>
    internal static byte[] Compress(byte[] data, CancellationToken cancellationToken = default)
    {
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < data.Length; offset += ChunkSize)
        {
            if ((offset & 0xFFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            using var chunk = new MemoryStream();
            using (var zlib = new ZLibStream(chunk, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(data, offset, Math.Min(ChunkSize, data.Length - offset));
            chunks.Add(chunk.ToArray());
        }
        using var output = new MemoryStream();
        var number = new byte[4];
        void Le32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(number, value);
            output.Write(number);
        }
        Le32(chunks.Count);
        Le32(data.Length);
        foreach (var chunk in chunks)
            Le32(chunk.Length);
        foreach (var chunk in chunks)
            output.Write(chunk);
        return output.ToArray();
    }

    /// <summary>The page with every character beyond ASCII as a numeric character reference.</summary>
    private static string Ascii(string html)
    {
        var sb = new StringBuilder(html.Length + html.Length / 8);
        for (var i = 0; i < html.Length; i++)
        {
            var c = html[i];
            if (c < 0x80)
                sb.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < html.Length && char.IsLowSurrogate(html[i + 1]))
                sb.Append("&#").Append(char.ConvertToUtf32(c, html[++i])).Append(';');
            else if (!char.IsSurrogate(c))
                sb.Append("&#").Append((int)c).Append(';');
        }
        return sb.ToString();
    }

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
