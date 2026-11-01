// RB (NuvoMedia Rocket eBook, later the REB 1100) → Book. The file starts with B0 0C B0 0C, version 2 and "NUVO";
// the little-endian offset of its table of contents is at 0x18. The table lists the files: a 32-byte name, length,
// offset and flags (1 compressed, 2 encrypted, 8 the info page). A compressed file is its chunk count, its size,
// the size of each chunk and the chunks, each a zlib stream of up to 4 KB of the file. The info page is text lines
// of KEY=value (TITLE, AUTHOR, BODY = the HTML page to read); the pages are read through HtmlReader.
// Encrypted books are refused as DRM.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class RocketBookReader
{
    private const int MaxFiles = 10_000;
    private const int MaxChunk = 64 * 1024;

    public static HDocument Read(string path, string workFolder) => ReadBook(path, workFolder).Document;

    public static Book ReadBook(string path, string workFolder, CancellationToken cancellationToken = default)
    {
        var limits = DocumentLimits.Default;
        if (new FileInfo(path).Length > limits.MaxPackageBytes)
            throw DocumentLimits.Exceeded($"The RB file is larger than {DocumentLimits.Megabytes(limits.MaxPackageBytes)}.");
        var data = File.ReadAllBytes(path);
        if (data.Length < 0x20 || !data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xB0, 0x0C, 0xB0, 0x0C]) || !data.AsSpan(6, 4).SequenceEqual("NUVO"u8))
            throw new InvalidDataException("This is not a Rocket eBook (RB) file.");
        var tocOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x18));
        if (tocOffset < 0x20 || tocOffset + 4L > data.Length)
            throw new InvalidDataException("The RB file's table of contents is damaged.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)tocOffset));
        if (count > MaxFiles)
            throw DocumentLimits.Exceeded($"The RB file lists more than {DocumentLimits.Count(MaxFiles)} files.");
        if (tocOffset + 4L + count * 44L > data.Length)
            throw new InvalidDataException("The RB file's table of contents is damaged.");

        var folder = EbookFiles.NewFolder(workFolder, "rb");
        var files = new List<(string Name, string Path)>();
        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = data.AsSpan((int)tocOffset + 4 + i * 44, 44);
            var nameBytes = entry[..32];
            var nul = nameBytes.IndexOf((byte)0);
            var name = Encoding.Latin1.GetString(nul < 0 ? nameBytes : nameBytes[..nul]).Trim();
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[32..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(entry[40..]);
            if (offset + (long)length > data.Length)
                throw new InvalidDataException($"The file {name} lies outside the RB file.");
            if ((flags & 2) != 0)
                throw new InvalidOperationException(EbookFiles.DrmMessage);
            var raw = data.AsSpan((int)offset, (int)length);
            var bytes = (flags & 1) != 0 ? Inflate(raw, name, limits) : raw.ToArray();
            if ((total += bytes.Length) > limits.MaxPackageBytes)
                throw DocumentLimits.Exceeded($"The RB file unpacks to more than {DocumentLimits.Megabytes(limits.MaxPackageBytes)}.");
            if ((flags & 8) != 0)
            {
                foreach (var line in Encoding.Latin1.GetString(bytes).Split('\n', '\r'))
                    if (line.IndexOf('=') is var equals and > 0)
                        info.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim());
                continue;
            }
            if (EbookFiles.SafePath(folder, name) is not { } target || !EbookFiles.TryWrite(target, bytes))
                continue;
            files.Add((name, target));
        }

        var pages = new List<string>();
        if (info.TryGetValue("BODY", out var body) && files.FirstOrDefault(f => f.Name.Equals(body, StringComparison.OrdinalIgnoreCase)) is { Path: { } bodyPath })
            pages.Add(bodyPath);
        else
            pages.AddRange(files.Where(f => f.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path));
        if (pages.Count == 0)
            throw new InvalidDataException("The RB file contains no HTML page.");

        var document = ChapterReader.Read(pages, Path.Combine(folder, "~media"), cancellationToken);
        var metadata = new BookMetadata { Title = info.GetValueOrDefault("TITLE") is { Length: > 0 } title ? title : document.Title };
        if (info.GetValueOrDefault("AUTHOR") is { Length: > 0 } author)
            metadata.Authors.Add(author);
        document.Title = metadata.Title ?? document.Title;
        return new Book(document, metadata);
    }

    /// <summary>A compressed file: chunk count, size, chunk sizes, then the zlib chunks.</summary>
    private static byte[] Inflate(ReadOnlySpan<byte> raw, string name, DocumentLimits limits)
    {
        if (raw.Length < 8)
            throw new InvalidDataException($"The file {name} of the RB file is damaged.");
        var chunks = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        if (size > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The file {name} of the RB file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        if (8 + chunks * 4L > raw.Length)
            throw new InvalidDataException($"The file {name} of the RB file is damaged.");
        var output = new MemoryStream();
        var position = 8 + (int)chunks * 4;
        for (var i = 0; i < chunks; i++)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[(8 + i * 4)..]);
            if (length < 0 || position + (long)length > raw.Length)
                throw new InvalidDataException($"The file {name} of the RB file is damaged.");
            var chunk = raw.Slice(position, length).ToArray();
            position += length;
            using var input = new MemoryStream(chunk);
            // zlib streams (78 xx), or bare deflate data.
            using Stream inflater = chunk is [0x78, _, ..] ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
            var buffer = new byte[MaxChunk + 1];
            var read = inflater.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read > MaxChunk)
                throw DocumentLimits.Exceeded($"A chunk of the file {name} of the RB file unpacks to more than {MaxChunk / 1024} KB.");
            output.Write(buffer, 0, read);
            if (output.Length > size)
                break;
        }
        return output.Length > size ? output.ToArray()[..(int)size] : output.ToArray();
    }
}
