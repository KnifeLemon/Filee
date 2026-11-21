// Book → SNB (Shanda Bambook), the container SnbReader reads (all numbers big-endian):
//  * the 44-byte header: "SNBP000B", the version words, the file count, the file table's size unpacked and packed,
//    the sizes of the binary and the text stream, a reserved word;
//  * the file table (zlib): per file its attributes (0x41000000 text, 0x01000000 binary), the offset of its name and
//    its size, then the names, each ended by a zero byte, sorted by name;
//  * the binary stream (the pictures, stored), then the text stream (the XML files) in 32 KB blocks, each
//    bzip2-compressed with Filee's own encoder (a full block that bzip2 doesn't make smaller is stored);
//  * the tail (zlib): the offset of every block (binary blocks first), then per file its block and its offset in it;
//    the last 16 bytes are the tail's size, its offset and "SNBP000B".
// The files: snbf/book.snbf (title, authors, language, publisher, description, cover), snbf/toc.snbf (the
// chapters in order) and one snbc/chNNNN.snbc per chapter: its title and <text> paragraphs and <img> pictures
// (JPEG or PNG in snbc/images). SNB text has no styles: chapters start at the book's top heading level, other
// headings, list items (with their markers), table rows (cells separated by " | ") and notes (numbered, at the end of
// their chapter) are paragraphs.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class SnbWriter
{
    private const int BlockSize = 0x8000;
    private const int TextAttributes = 0x41000000;
    private const int BinaryAttributes = 0x01000000;
    private static readonly byte[] Magic = "SNBP000B"u8.ToArray();

    /// <summary>A file of the container.</summary>
    internal sealed record SnbFile(string Name, byte[] Data, bool Text);

    public static void Write(Book book, string outputPath, string sourcePath, string workFolder, CancellationToken cancellationToken = default)
    {
        Nesting.Limit(book.Document);
        var title = book.DisplayTitle(sourcePath);
        var convert = EbookFiles.NewFolder(workFolder, "snb-pictures");
        var files = new List<SnbFile>();
        var pictures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? Picture(string path)
        {
            if (pictures.TryGetValue(path, out var known))
                return known;
            if (EbookFiles.CommonImage(path, convert, gifAndSvg: false) is not ({ } file, { } mediaType))
                return null;
            var name = $"img{pictures.Count + 1:0000}.{(mediaType == "image/png" ? "png" : "jpg")}";
            files.Add(new SnbFile("snbc/images/" + name, File.ReadAllBytes(file), Text: false));
            return pictures[path] = name;
        }

        var cover = book.Metadata.CoverImage is { } coverFile && File.Exists(coverFile) ? Picture(coverFile) : null;
        var chapters = Chapters(book.Document, title, Picture, cancellationToken);
        var toc = new XElement("body");
        for (var i = 0; i < chapters.Count; i++)
        {
            var name = $"ch{i + 1:0000}.snbc";
            toc.Add(new XElement("chapter", new XAttribute("src", name), Clean(chapters[i].Title)));
            files.Add(new SnbFile("snbc/" + name, Xml(new XElement("snbc",
                new XElement("head", new XElement("title", Clean(chapters[i].Title))),
                new XElement("body", chapters[i].Content))), Text: true));
        }
        files.Add(new SnbFile("snbf/toc.snbf", Xml(new XElement("toc-snbf",
            new XElement("head", new XElement("chapters", chapters.Count.ToString(CultureInfo.InvariantCulture))), toc)), Text: true));
        files.Add(new SnbFile("snbf/book.snbf", Xml(new XElement("book-snbf", new XAttribute("version", "1.0"),
            new XElement("head",
                new XElement("name", Clean(title)),
                new XElement("author", Clean(string.Join(", ", book.Metadata.Authors))),
                new XElement("language", book.DisplayLanguage().ToUpperInvariant()),
                new XElement("rights"),
                new XElement("publisher", Clean(book.Metadata.Publisher ?? "")),
                new XElement("generator", "Filee"),
                new XElement("created", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new XElement("abstract", Clean(book.Metadata.Description ?? "")),
                new XElement("cover", cover ?? "")))), Text: true));

        var snb = Container(files, cancellationToken);
        WriterTempFile.Write(outputPath, stream => stream.Write(snb));
    }

    // ───────────────────────── Chapters ─────────────────────────

    /// <param name="Leading">The content before the first heading (left out when there is none).</param>
    private sealed record Chapter(string Title, List<XElement> Content, bool Leading = false);

    /// <summary>
    /// The chapters: a new one at every heading of the book's top level (1 when there is one); content before the
    /// first is a chapter of its own, named after the book.
    /// </summary>
    private static List<Chapter> Chapters(HDocument document, string bookTitle, Func<string, string?> picture, CancellationToken cancellationToken)
    {
        var blocks = document.Sections.SelectMany(s => s.Blocks).ToList();
        var levels = blocks.OfType<HParagraph>().Where(p => p.HeadingLevel > 0 && HDocumentWalker.Text(p.Inlines).Length > 0).Select(p => p.HeadingLevel).ToList();
        var top = levels.Count > 0 ? levels.Min() : 0;
        var chapters = new List<Chapter>();
        var current = new Chapter(bookTitle, [], Leading: true);
        var notes = new List<string>();
        var numbers = new ListNumbers();

        void Close()
        {
            for (var i = 0; i < notes.Count; i++)
                current.Content.Add(TextElement($"[{i + 1}] {notes[i]}"));
            notes.Clear();
            if (current.Content.Count > 0 || !current.Leading)
                chapters.Add(current);
        }

        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block is HParagraph { HeadingLevel: > 0 } heading && heading.HeadingLevel == top && HDocumentWalker.Text(heading.Inlines) is { Length: > 0 } headingText)
            {
                Close();
                current = new Chapter(headingText, []);
                continue;
            }
            switch (block)
            {
                case HParagraph paragraph:
                    {
                        var marker = paragraph.List is { } list ? new string(' ', list.Level * 2) + (list.Numbered ? numbers.Next(list) : "•") + " " : "";
                        Paragraph(paragraph.Inlines, marker, current.Content, notes, picture);
                        break;
                    }
                case HTable table:
                    foreach (var row in table.Rows)
                    {
                        var cells = row.Cells.Select(c => string.Join(" ", c.Blocks.OfType<HParagraph>().Select(p => HDocumentWalker.Text(p.Inlines)).Where(t => t.Length > 0)));
                        if (string.Join(" | ", cells) is var line && line.Replace("|", "").Trim().Length > 0)
                            current.Content.Add(TextElement(line));
                    }
                    break;
            }
        }
        Close();
        if (chapters.Count == 0)
            chapters.Add(new Chapter(bookTitle, []));
        return chapters;
    }

    /// <summary>A paragraph's lines as &lt;text&gt; elements, its pictures as &lt;img&gt; between them.</summary>
    private static void Paragraph(List<HInline> inlines, string marker, List<XElement> content, List<string> notes, Func<string, string?> picture)
    {
        var line = new StringBuilder(marker);
        void Flush()
        {
            if (line.ToString().Trim().Length > 0)
                content.Add(TextElement(line.ToString().TrimEnd()));
            line.Clear();
        }
        void Walk(List<HInline> list)
        {
            foreach (var inline in list)
            {
                switch (inline)
                {
                    case HText text:
                        line.Append(text.Text);
                        break;
                    case HTab:
                        line.Append('\t');
                        break;
                    case HLineBreak:
                        Flush();
                        break;
                    case HLink link:
                        Walk(link.Content);
                        break;
                    case HImage image:
                        if (picture(image.Path) is { } name)
                        {
                            Flush();
                            content.Add(new XElement("img", name));
                        }
                        break;
                    case HNote note:
                        notes.Add(string.Join(" ", note.Blocks.OfType<HParagraph>().Select(p => HDocumentWalker.Text(p.Inlines)).Where(t => t.Length > 0)));
                        line.Append('[').Append(notes.Count).Append(']');
                        break;
                    case HTextBox box:
                        Flush();
                        foreach (var paragraph in box.Blocks.OfType<HParagraph>())
                        {
                            Walk(paragraph.Inlines);
                            Flush();
                        }
                        break;
                }
            }
        }
        Walk(inlines);
        Flush();
    }

    /// <summary>A &lt;text&gt; paragraph, its text in CDATA as Bambook books have it.</summary>
    private static XElement TextElement(string text)
    {
        var clean = Clean(text);
        return new XElement("text", clean.Contains("]]>", StringComparison.Ordinal) ? new XText(clean) : new XCData(clean));
    }

    /// <summary>Text without the characters XML 1.0 forbids and without line breaks.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                sb.Append(c).Append(text[++i]);
            else if (c is '\r' or '\n')
                sb.Append(' ');
            else if (XmlConvert.IsXmlChar(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static byte[] Xml(XElement root)
    {
        using var stream = new MemoryStream();
        using (var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(xml);
        return stream.ToArray();
    }

    // ───────────────────────── Container ─────────────────────────

    /// <summary>The SNB container of <paramref name="files"/> (sorted by name here).</summary>
    internal static byte[] Container(List<SnbFile> files, CancellationToken cancellationToken = default)
    {
        var limits = DocumentLimits.Default;
        files = [.. files.OrderBy(f => f.Name, StringComparer.Ordinal)];
        if (files.Count > limits.MaxPackageEntries)
            throw DocumentLimits.TooLarge($"The SNB book would have more than {DocumentLimits.Count(limits.MaxPackageEntries)} files.");
        if (files.Sum(f => (long)f.Data.Length) > limits.MaxPackageBytes)
            throw DocumentLimits.TooLarge($"The SNB book would be larger than {DocumentLimits.Megabytes(limits.MaxPackageBytes)}.");

        // Where each file lies in its stream, and the file table.
        using var binary = new MemoryStream();
        using var text = new MemoryStream();
        using var table = new MemoryStream();
        using var names = new MemoryStream();
        var placed = new List<(bool Text, long Offset)>();
        foreach (var file in files)
        {
            var stream = file.Text ? text : binary;
            placed.Add((file.Text, stream.Length));
            stream.Write(file.Data);
            Be32(table, file.Text ? TextAttributes : BinaryAttributes);
            Be32(table, (int)names.Length);
            Be32(table, file.Data.Length);
            names.Write(Encoding.UTF8.GetBytes(file.Name));
            names.WriteByte(0);
        }
        names.WriteTo(table);
        if (table.Length > limits.MaxPartBytes)
            throw DocumentLimits.TooLarge($"The SNB book's file table would be larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        var packedTable = Zlib(table.ToArray());

        var blocks = TextBlocks(text, cancellationToken);
        var textSize = (int)text.Length; // padded by TextBlocks when it had to be
        var binaryBlocks = (int)((binary.Length + BlockSize - 1) / BlockSize);

        using var output = new MemoryStream();
        output.Write(Magic);
        Be32(output, 0x00008000);
        Be32(output, 0x00A3A3A3);
        Be32(output, 0);
        Be32(output, files.Count);
        Be32(output, (int)table.Length);
        Be32(output, packedTable.Length);
        Be32(output, (int)binary.Length);
        Be32(output, textSize);
        Be32(output, 0);
        output.Write(packedTable);

        using var tail = new MemoryStream();
        var binaryStart = output.Length;
        for (var i = 0; i < binaryBlocks; i++)
            Be32(tail, (int)(binaryStart + (long)i * BlockSize));
        binary.WriteTo(output);
        foreach (var block in blocks)
        {
            Be32(tail, (int)output.Length);
            output.Write(block);
            if (output.Length > int.MaxValue - 1024)
                throw DocumentLimits.TooLarge("The SNB book would be larger than 2 GB.");
        }
        foreach (var (isText, offset) in placed)
        {
            Be32(tail, (int)(offset / BlockSize) + (isText ? binaryBlocks : 0));
            Be32(tail, (int)(offset % BlockSize));
        }
        var packedTail = Zlib(tail.ToArray());
        var tailOffset = (int)output.Length;
        output.Write(packedTail);
        Be32(output, packedTail.Length);
        Be32(output, tailOffset);
        output.Write(Magic);
        return output.ToArray();
    }

    /// <summary>
    /// The text stream in 32 KB blocks, each bzip2-compressed, or stored when that doesn't make a full block smaller
    /// (a block shorter than 32 KB is read as compressed, so a short last block that bzip2 can't shrink is padded
    /// with spaces to a full stored one: the files end before the padding).
    /// </summary>
    private static List<byte[]> TextBlocks(MemoryStream text, CancellationToken cancellationToken)
    {
        var blocks = new List<byte[]>();
        for (long offset = 0; offset < text.Length; offset += BlockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = (int)Math.Min(BlockSize, text.Length - offset);
            var block = text.GetBuffer().AsSpan((int)offset, length);
            using var packed = new MemoryStream();
            using (var bzip2 = new Bzip2Writer(packed, ArchiveLevel.Normal, cancellationToken, leaveOpen: true, threads: 1))
            {
                bzip2.Write(block);
                bzip2.Finish();
            }
            if (packed.Length < BlockSize)
            {
                blocks.Add(packed.ToArray());
                continue;
            }
            if (length < BlockSize)
            {
                text.Position = text.Length;
                text.Write(Enumerable.Repeat((byte)' ', BlockSize - length).ToArray());
                block = text.GetBuffer().AsSpan((int)offset, BlockSize);
            }
            blocks.Add(block.ToArray());
        }
        return blocks;
    }

    private static void Be32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }
}
