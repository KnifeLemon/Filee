// SNB (Shanda Bambook) → Book. The container (all numbers big-endian):
//  * a 44-byte header: "SNBP000B", three version words, the file count, the size of the file table unpacked and
//    packed, the size of the binary stream and of the text stream unpacked, and a reserved word;
//  * the file table (zlib): per file its attributes, the offset of its name and its size, then the names, each ended
//    by a zero byte;
//  * the binary stream (pictures, stored), then the text stream in 32 KB blocks, each bzip2-compressed unless that
//    did not make it smaller;
//  * a tail (zlib) with the offset of every block, located by the last 16 bytes: its size, its offset and "SNBP000B".
// Text files are XML: snbf/book.snbf (title, author, language), snbf/toc.snbf (the chapters in order) and one
// snbc/*.snbc per chapter (<text> paragraphs and <img> pictures from snbc/images).

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Engines.Archives;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class SnbReader
{
    private const int BlockSize = 0x8000;
    private static readonly byte[] Magic = "SNBP000B"u8.ToArray();

    public static HDocument Read(string path, string workFolder) => ReadBook(path, workFolder).Document;

    public static Book ReadBook(string path, string workFolder, CancellationToken cancellationToken = default)
    {
        var limits = DocumentLimits.Default;
        if (new FileInfo(path).Length > limits.MaxPackageBytes)
            throw DocumentLimits.Exceeded($"The SNB file is larger than {DocumentLimits.Megabytes(limits.MaxPackageBytes)}.");
        var data = File.ReadAllBytes(path);
        var folder = EbookFiles.NewFolder(workFolder, "snb");
        var files = Unpack(data, limits, cancellationToken);
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in files)
            if (EbookFiles.SafePath(folder, name) is { } target && EbookFiles.TryWrite(target, bytes))
                written[name.Replace('\\', '/')] = target;

        var metadata = new BookMetadata();
        if (written.TryGetValue("snbf/book.snbf", out var bookFile) && Load(bookFile)?.Root is { } book)
        {
            metadata.Title = Value(book, "name");
            metadata.Language = Value(book, "language")?.ToLowerInvariant();
            if (Value(book, "author") is { } author)
                metadata.Authors.Add(author);
            metadata.Publisher = Value(book, "publisher");
            metadata.Description = Value(book, "abstract");
            if (Value(book, "cover") is { } cover && Picture(written, cover) is { } coverFile)
                metadata.CoverImage = coverFile;
        }

        // Chapters in the order of toc.snbf, else every chapter file in the order of the container.
        var chapters = new List<(string File, string? Title)>();
        if (written.TryGetValue("snbf/toc.snbf", out var tocFile) && Load(tocFile)?.Root is { } toc)
            foreach (var chapter in toc.Descendants().Where(e => e.Name.LocalName == "chapter"))
                if ((string?)chapter.Attribute("src") is { Length: > 0 } src && written.TryGetValue("snbc/" + src.TrimStart('/'), out var file))
                    chapters.Add((file, chapter.Value.Trim()));
        if (chapters.Count == 0)
            chapters.AddRange(written.Where(w => w.Key.EndsWith(".snbc", StringComparison.OrdinalIgnoreCase)).Select(w => (w.Value, (string?)null)));
        if (chapters.Count == 0)
            throw new InvalidDataException("The SNB file contains no chapters.");

        var pages = new List<string>();
        foreach (var (file, title) in chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = Path.ChangeExtension(file, ".html");
            File.WriteAllText(page, Chapter(file, title, written, Path.GetDirectoryName(page)!), new UTF8Encoding(true));
            pages.Add(page);
        }
        var document = ChapterReader.Read(pages, Path.Combine(folder, "~media"), cancellationToken);
        document.Title = metadata.Title ?? document.Title;
        return new Book(document, metadata);
    }

    /// <summary>A chapter as HTML: its title, its text paragraphs and pictures.</summary>
    private static string Chapter(string file, string? tocTitle, Dictionary<string, string> written, string pageFolder)
    {
        var html = new StringBuilder("<html><head><meta charset=\"utf-8\"/></head><body>\n");
        var root = Load(file)?.Root;
        var title = (root is null ? null : Value(root, "title")) ?? tocTitle;
        if (!string.IsNullOrWhiteSpace(title))
            html.Append("<h1>").Append(XhtmlWriter.Escape(title)).Append("</h1>\n");
        var body = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
        foreach (var element in body?.Elements() ?? [])
        {
            if (element.Name.LocalName == "text")
            {
                foreach (var line in element.Value.Replace("\r\n", "\n").Split('\n'))
                    if (line.Trim().Length > 0)
                        html.Append("<p>").Append(XhtmlWriter.Escape(line.Trim())).Append("</p>\n");
            }
            else if (element.Name.LocalName == "img" && Picture(written, element.Value.Trim()) is { } picture)
            {
                html.Append("<p style=\"text-align:center\"><img src=\"")
                    .Append(XhtmlWriter.Escape(Path.GetRelativePath(pageFolder, picture).Replace('\\', '/'))).Append("\" alt=\"\"/></p>\n");
            }
        }
        return html.Append("</body></html>\n").ToString();
    }

    private static string? Picture(Dictionary<string, string> written, string name)
    {
        name = name.Replace('\\', '/').TrimStart('/');
        foreach (var candidate in new[] { "snbc/images/" + name, "snbc/" + name, name })
            if (written.TryGetValue(candidate, out var file))
                return file;
        return null;
    }

    private static XDocument? Load(string path)
    {
        try
        {
            return OpfPackage.LoadXml(path);
        }
        catch (System.Xml.XmlException)
        {
            return null; // a damaged part: the book reads without it
        }
    }

    private static string? Value(XElement root, string name) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == name) is { } element && element.Value.Trim() is { Length: > 0 } value ? value : null;

    // ───────────────────────── Container ─────────────────────────

    /// <summary>The files of an SNB container, in the order of its file table.</summary>
    internal static List<(string Name, byte[] Bytes)> Unpack(byte[] data, DocumentLimits limits, CancellationToken cancellationToken)
    {
        if (data.Length < 44 + 16 || !data.AsSpan().StartsWith(Magic))
            throw new InvalidDataException("This is not an SNB file.");
        int Int(int offset) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
        var fileCount = Int(20);
        var tableSize = Int(24);
        var tablePacked = Int(28);
        var binarySize = Int(32);
        var textSize = Int(36);
        if (fileCount < 0 || tableSize < 0 || tablePacked < 0 || binarySize < 0 || textSize < 0 || 44L + tablePacked > data.Length)
            throw new InvalidDataException("The SNB file's header is damaged.");
        if (fileCount > limits.MaxPackageEntries)
            throw DocumentLimits.Exceeded($"The SNB file has more than {DocumentLimits.Count(limits.MaxPackageEntries)} files.");
        if ((long)binarySize + textSize > limits.MaxPackageBytes || tableSize > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The SNB file unpacks to more than {DocumentLimits.Megabytes(limits.MaxPackageBytes)}.");

        var table = Inflate(data.AsSpan(44, tablePacked), tableSize, "file table");
        if (table.Length < fileCount * 12L)
            throw new InvalidDataException("The SNB file's file table is damaged.");
        var names = Encoding.UTF8.GetString(table, fileCount * 12, table.Length - fileCount * 12).Split('\0');

        // The tail, found from the end of the file.
        var tailSize = Int(data.Length - 16);
        var tailOffset = Int(data.Length - 12);
        if (!data.AsSpan(data.Length - 8).SequenceEqual(Magic) || tailSize < 0 || tailOffset < 44 + tablePacked || (long)tailOffset + tailSize > data.Length - 16)
            throw new InvalidDataException("The SNB file's block table is damaged.");
        var binaryBlocks = (binarySize + BlockSize - 1) / BlockSize;
        var textBlocks = (textSize + BlockSize - 1) / BlockSize;
        var tail = Inflate(data.AsSpan(tailOffset, tailSize), (binaryBlocks + textBlocks) * 4L + fileCount * 8L, "block table");
        if (tail.Length < (binaryBlocks + textBlocks) * 4L)
            throw new InvalidDataException("The SNB file's block table is damaged.");
        int Block(int index) => BinaryPrimitives.ReadInt32BigEndian(tail.AsSpan(index * 4));

        // The text stream: each block is bzip2 when it is smaller than a block, else stored.
        using var text = new MemoryStream();
        for (var i = 0; i < textBlocks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = Block(binaryBlocks + i);
            var end = i + 1 < textBlocks ? Block(binaryBlocks + i + 1) : tailOffset;
            if (start < 44 || end < start || end > tailOffset)
                throw new InvalidDataException("The SNB file's block table is damaged.");
            var block = data.AsMemory(start, end - start);
            if (block.Length < BlockSize)
            {
                using var input = new MemoryStream(block.ToArray());
                var room = Math.Min(BlockSize, textSize - text.Length);
                try
                {
                    Bzip2Decoder.Decompress(input, text, alzFraming: false, room, cancellationToken);
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidDataException("A text block of the SNB file is cut short.", ex);
                }
            }
            else
            {
                text.Write(block.Span[..Math.Min(block.Length, (int)Math.Max(0, textSize - text.Length))]);
            }
        }
        if (text.Length > limits.InflateRatioFloor && text.Length / Math.Max(1, data.Length) > limits.MaxInflateRatio)
            throw DocumentLimits.Exceeded($"The SNB file's text unpacks to more than {limits.MaxInflateRatio} times its size.");

        var files = new List<(string, byte[])>();
        var binaryStart = 44L + tablePacked;
        long binaryPosition = 0, textPosition = 0;
        var textBytes = text.GetBuffer();
        for (var i = 0; i < fileCount; i++)
        {
            var attributes = BinaryPrimitives.ReadInt32BigEndian(table.AsSpan(i * 12));
            var size = BinaryPrimitives.ReadInt32BigEndian(table.AsSpan(i * 12 + 8));
            var name = i < names.Length ? names[i] : "";
            if (size < 0)
                throw new InvalidDataException("The SNB file's file table is damaged.");
            if ((attributes & 0x41000000) == 0x41000000)
            {
                if (textPosition + size > text.Length)
                    throw new InvalidDataException($"The file {name} lies outside the SNB file's text.");
                files.Add((name, textBytes.AsSpan((int)textPosition, size).ToArray()));
                textPosition += size;
            }
            else if ((attributes & 0x01000000) != 0)
            {
                if (binaryPosition + size > binarySize || binaryStart + binaryPosition + size > data.Length)
                    throw new InvalidDataException($"The file {name} lies outside the SNB file.");
                files.Add((name, data.AsSpan((int)(binaryStart + binaryPosition), size).ToArray()));
                binaryPosition += size;
            }
        }
        return files;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> packed, long expected, string what)
    {
        try
        {
            using var input = new MemoryStream(packed.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > expected)
                    throw new InvalidDataException($"The SNB file's {what} is larger than its header says.");
            }
            return output.ToArray();
        }
        catch (IOException ex)
        {
            throw new InvalidDataException($"The SNB file's {what} is damaged.", ex);
        }
    }
}
