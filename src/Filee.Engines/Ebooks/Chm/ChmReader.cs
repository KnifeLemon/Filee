// CHM (Microsoft Compiled HTML Help) → Book, written from the published descriptions of the ITSF container:
//  * the ITSF header names the directory (ITSP header + PMGL listing chunks) and where the content starts;
//  * every file is in section 0 (stored) or section 1 ("MSCompressed": one LZX stream, decoded by LzxDecoder with the
//    window and reset interval of its ControlData);
//  * #SYSTEM gives the title, the language (and so the code page of pages without a charset), the contents file
//    (.hhc) and the default topic;
//  * the pages are read through HtmlReader in the order of the contents file (a sitemap of <param name="Local">
//    entries); a book without one has its default topic first, then its other pages in directory order.
// Limits: entries, bytes per file and in total, the decompression ratio of the LZX section, chunk sizes inside the
// file; a CHM past one is refused with error.document_too_complex, a damaged one with InvalidDataException.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static partial class ChmReader
{
    private const string ContentName = "::DataSpace/Storage/MSCompressed/Content";
    private const string ControlDataName = "::DataSpace/Storage/MSCompressed/ControlData";

    public static HDocument Read(string path, string workFolder) => ReadBook(path, workFolder).Document;

    public static Book ReadBook(string path, string workFolder, CancellationToken cancellationToken = default)
    {
        var limits = DocumentLimits.Default;
        var folder = EbookFiles.NewFolder(workFolder, "chm");
        using var stream = File.OpenRead(path);
        var chm = ChmFile.Open(stream, limits);

        var system = chm.Find("/#SYSTEM") is { } systemEntry ? SystemFile.Parse(chm.Read(systemEntry, folder, cancellationToken)) : new SystemFile();
        var lcid = system.Lcid ?? chm.Lcid;
        var encoding = CodePage(lcid);

        // Unpack the book's own files (not the container's "::" and "#" / "$" internals).
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        long total = 0;
        foreach (var entry in chm.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Name.StartsWith('/') || entry.Name.EndsWith('/') || entry.Name.StartsWith("/#", StringComparison.Ordinal)
                || entry.Name.StartsWith("/$", StringComparison.Ordinal) || entry.Length == 0)
                continue;
            if (EbookFiles.SafePath(folder, entry.Name) is not { } target)
                continue;
            if (entry.Length > limits.MaxPartBytes)
                throw DocumentLimits.Exceeded($"The file {entry.Name} of the CHM is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
            if ((total += entry.Length) > limits.MaxPackageBytes)
                throw DocumentLimits.Exceeded($"The CHM unpacks to more than {DocumentLimits.Megabytes(limits.MaxPackageBytes)}.");
            if (!EbookFiles.TryWrite(target, chm.Read(entry, folder, cancellationToken)))
                continue;
            files.TryAdd(entry.Name, target);
            order.Add(entry.Name);
        }

        var pages = Pages(chm, system, files, order, encoding, folder, cancellationToken);
        if (pages.Count == 0)
            throw new InvalidDataException("The CHM file contains no HTML pages.");
        foreach (var page in pages)
            File.WriteAllText(page, DecodePage(File.ReadAllBytes(page), encoding), new UTF8Encoding(true));

        var document = ChapterReader.Read(pages, Path.Combine(folder, "~media"), cancellationToken);
        var metadata = new BookMetadata
        {
            Title = string.IsNullOrWhiteSpace(system.Title) ? document.Title : system.Title.Trim(),
            Language = Language(lcid),
        };
        document.Title = metadata.Title ?? document.Title;
        return new Book(document, metadata);
    }

    /// <summary>The pages in reading order: the contents file's order, else the default topic and the other pages.</summary>
    private static List<string> Pages(ChmFile chm, SystemFile system, Dictionary<string, string> files, List<string> order,
        Encoding encoding, string folder, CancellationToken cancellationToken)
    {
        var pages = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name)
        {
            if (IsPage(name) && files.TryGetValue(name, out var file) && seen.Add(name))
                pages.Add(file);
        }

        var contents = system.ContentsFile is { Length: > 0 } named ? chm.Find(Absolute(named, "/")) : null;
        contents ??= chm.Entries.FirstOrDefault(e => e.Name.EndsWith(".hhc", StringComparison.OrdinalIgnoreCase));
        if (contents is not null)
        {
            var sitemap = DecodePage(chm.Read(contents, folder, cancellationToken), encoding);
            var directory = contents.Name[..(contents.Name.LastIndexOf('/') + 1)];
            foreach (Match param in Param().Matches(sitemap))
            {
                var tag = param.Value;
                if (Attribute(tag, "name") is { } name && name.Equals("Local", StringComparison.OrdinalIgnoreCase)
                    && Attribute(tag, "value") is { Length: > 0 } local)
                    Add(Absolute(local, directory));
            }
        }
        if (pages.Count == 0)
        {
            if (system.DefaultTopic is { Length: > 0 } topic)
                Add(Absolute(topic, "/"));
            foreach (var name in order)
                Add(name);
        }
        return pages;
    }

    private static bool IsPage(string name) =>
        name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase);

    /// <summary>A file name of the CHM ("/html/a.htm") for a link in it: "a.htm", "../a.htm#x", "ms-its:b.chm::/a.htm".</summary>
    internal static string Absolute(string link, string directory)
    {
        var path = link.Trim();
        var outside = path.LastIndexOf("::", StringComparison.Ordinal);
        if (outside >= 0)
            path = path[(outside + 2)..];
        var cut = path.IndexOfAny(['#', '?']);
        if (cut >= 0)
            path = path[..cut];
        try
        {
            path = Uri.UnescapeDataString(path.Replace('\\', '/'));
        }
        catch (UriFormatException)
        {
        }
        var parts = new List<string>();
        foreach (var part in ((path.StartsWith('/') ? "" : directory) + path).Split('/'))
        {
            if (part is "" or ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    private static string? Attribute(string tag, string name)
    {
        var match = (name == "name" ? NameAttribute() : ValueAttribute()).Match(tag);
        if (!match.Success)
            return null;
        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        return System.Net.WebUtility.HtmlDecode(value);
    }

    [GeneratedRegex(@"\bname\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex NameAttribute();

    [GeneratedRegex(@"\bvalue\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex ValueAttribute();

    [GeneratedRegex(@"<param\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Param();

    /// <summary>
    /// A page's text: its byte order mark or declared charset, else UTF-8 when it is valid UTF-8, else the code page
    /// of the CHM's language (help files were compiled on PCs of that language).
    /// </summary>
    private static string DecodePage(byte[] bytes, Encoding fallback)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..] or [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            return HtmlReader.Decode(bytes);
        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        if (Declared().IsMatch(head))
            return HtmlReader.Decode(bytes);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return fallback.GetString(bytes);
        }
    }

    [GeneratedRegex(@"charset\s*=|<\?xml[^>]+encoding", RegexOptions.IgnoreCase)]
    private static partial Regex Declared();

    private static Encoding CodePage(int lcid)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            var codePage = CultureInfo.GetCultureInfo(lcid).TextInfo.ANSICodePage;
            return Encoding.GetEncoding(codePage is 0 ? 1252 : codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or CultureNotFoundException or NotSupportedException)
        {
            return Encoding.GetEncoding(1252);
        }
    }

    private static string? Language(int lcid)
    {
        try
        {
            return lcid is 0 or 0x7F or 0x400 or 0x800 ? null : CultureInfo.GetCultureInfo(lcid).Name is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is ArgumentException or CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The #SYSTEM file: records of a 16-bit code, a 16-bit length and data.</summary>
    private sealed class SystemFile
    {
        public string? ContentsFile { get; private set; }
        public string? DefaultTopic { get; private set; }
        public string? Title { get; private set; }
        public int? Lcid { get; private set; }

        public static SystemFile Parse(byte[] data)
        {
            var system = new SystemFile();
            var strings = new List<(int Code, byte[] Bytes)>();
            for (var position = 4; position + 4 <= data.Length;)
            {
                var code = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
                var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 2));
                position += 4;
                if (position + length > data.Length)
                    break;
                var value = data.AsSpan(position, length);
                if (code == 4 && length >= 4)
                    system.Lcid = BinaryPrimitives.ReadInt32LittleEndian(value);
                else if (code is 0 or 2 or 3)
                    strings.Add((code, value.ToArray()));
                position += length;
            }
            var encoding = CodePage(system.Lcid ?? 0x409);
            foreach (var (code, bytes) in strings)
            {
                var end = Array.IndexOf(bytes, (byte)0);
                var text = encoding.GetString(bytes, 0, end < 0 ? bytes.Length : end).Trim();
                switch (code)
                {
                    case 0:
                        system.ContentsFile = text;
                        break;
                    case 2:
                        system.DefaultTopic = text;
                        break;
                    default:
                        system.Title = text;
                        break;
                }
            }
            return system;
        }
    }

    // ───────────────────────── Container ─────────────────────────

    internal sealed record ChmEntry(string Name, long Section, long Offset, long Length);

    /// <summary>An ITSF file: its directory, and the bytes of its files.</summary>
    internal sealed class ChmFile
    {
        /// <summary>Offsets and lengths beyond this are damage (and could overflow when added).</summary>
        private const long MaxOffset = 1L << 48;

        private readonly Stream _stream;
        private readonly DocumentLimits _limits;
        private readonly Dictionary<string, ChmEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
        private long _contentOffset;
        private string? _section1;

        private ChmFile(Stream stream, DocumentLimits limits)
        {
            _stream = stream;
            _limits = limits;
        }

        public List<ChmEntry> Entries { get; } = [];
        public int Lcid { get; private set; }

        public ChmEntry? Find(string name) => _byName.GetValueOrDefault(name);

        public static ChmFile Open(Stream stream, DocumentLimits limits)
        {
            var chm = new ChmFile(stream, limits);
            var header = chm.ReadAt(0, 0x60, allowShort: true);
            if (header.Length < 0x58 || !header.AsSpan(0, 4).SequenceEqual("ITSF"u8))
                throw new InvalidDataException("This is not a CHM file (no ITSF header).");
            var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            chm.Lcid = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
            var directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x48));
            var directoryLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x50));
            chm._contentOffset = version >= 3 && header.Length >= 0x60 ? BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x58)) : directoryOffset + directoryLength;
            if (directoryOffset <= 0 || directoryLength < 0x54 || directoryOffset + directoryLength > stream.Length
                || chm._contentOffset < 0 || chm._contentOffset > stream.Length)
                throw new InvalidDataException("The CHM file's header is damaged.");
            chm.ReadDirectory(directoryOffset, directoryLength);
            return chm;
        }

        private void ReadDirectory(long offset, long length)
        {
            var itsp = ReadAt(offset, 0x54);
            if (!itsp.AsSpan(0, 4).SequenceEqual("ITSP"u8))
                throw new InvalidDataException("The CHM file's directory is damaged.");
            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(itsp.AsSpan(8));
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(itsp.AsSpan(16));
            var chunks = BinaryPrimitives.ReadInt32LittleEndian(itsp.AsSpan(44));
            if (headerLength is < 0x54 or > 0x1000 || chunkSize is < 0x20 or > 1 << 20 || chunks < 0 || headerLength + (long)chunks * chunkSize > length)
                throw new InvalidDataException("The CHM file's directory is damaged.");
            for (var i = 0; i < chunks; i++)
            {
                var chunk = ReadAt(offset + headerLength + (long)i * chunkSize, chunkSize);
                if (chunk.AsSpan(0, 4).SequenceEqual("PMGL"u8))
                    ReadListing(chunk);
            }
        }

        private void ReadListing(byte[] chunk)
        {
            var free = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            var end = free < chunk.Length - 20 ? chunk.Length - (int)free : 20;
            for (var position = 20; position < end;)
            {
                var nameLength = EncInt(chunk, ref position, end);
                if (nameLength <= 0 || nameLength > end - position)
                    break;
                var name = Encoding.UTF8.GetString(chunk, position, (int)nameLength);
                position += (int)nameLength;
                var section = EncInt(chunk, ref position, end);
                var fileOffset = EncInt(chunk, ref position, end);
                var fileLength = EncInt(chunk, ref position, end);
                if (section < 0 || fileOffset is < 0 or > MaxOffset || fileLength is < 0 or > MaxOffset)
                    break;
                if (Entries.Count >= _limits.MaxPackageEntries)
                    throw DocumentLimits.Exceeded($"The CHM file has more than {DocumentLimits.Count(_limits.MaxPackageEntries)} files.");
                var entry = new ChmEntry(name, section, fileOffset, fileLength);
                if (_byName.TryAdd(name, entry))
                    Entries.Add(entry);
            }
        }

        /// <summary>CHM's variable-length number: 7 bits per byte, most significant first, high bit = more follow.</summary>
        private static long EncInt(byte[] data, ref int position, int end)
        {
            long value = 0;
            for (var i = 0; i < 9; i++)
            {
                if (position >= end)
                    return -1;
                var b = data[position++];
                value = (value << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0)
                    return value;
            }
            return -1;
        }

        /// <summary>The bytes of a file; files of the compressed section are decoded once, into the work folder.</summary>
        public byte[] Read(ChmEntry entry, string workFolder, CancellationToken cancellationToken)
        {
            if (entry.Length > _limits.MaxPartBytes)
                throw DocumentLimits.Exceeded($"The file {entry.Name} of the CHM is larger than {DocumentLimits.Megabytes(_limits.MaxPartBytes)}.");
            switch (entry.Section)
            {
                case 0:
                    if (_contentOffset + entry.Offset + entry.Length > _stream.Length)
                        throw new InvalidDataException($"The file {entry.Name} lies outside the CHM file.");
                    return ReadAt(_contentOffset + entry.Offset, (int)entry.Length);
                case 1:
                    {
                        _section1 ??= DecodeSection1(workFolder, cancellationToken);
                        using var decoded = File.OpenRead(_section1);
                        if (entry.Offset + entry.Length > decoded.Length)
                            throw new InvalidDataException($"The file {entry.Name} lies outside the CHM's compressed data.");
                        decoded.Position = entry.Offset;
                        var bytes = new byte[entry.Length];
                        decoded.ReadExactly(bytes);
                        return bytes;
                    }
                default:
                    throw new InvalidDataException($"The file {entry.Name} is in an unknown section of the CHM.");
            }
        }

        /// <summary>Decodes the LZX section as far as its files reach.</summary>
        private string DecodeSection1(string workFolder, CancellationToken cancellationToken)
        {
            if (Find(ContentName) is not { Section: 0 } content || Find(ControlDataName) is not { Section: 0 } control)
                throw new InvalidDataException("The CHM file's compressed section is missing.");
            var controlData = Read(control, workFolder, cancellationToken);
            if (controlData.Length < 24 || !controlData.AsSpan(4, 4).SequenceEqual("LZXC"u8))
                throw new InvalidDataException("The CHM file's compression settings are damaged.");
            var version = BinaryPrimitives.ReadUInt32LittleEndian(controlData.AsSpan(8));
            long resetInterval = BinaryPrimitives.ReadUInt32LittleEndian(controlData.AsSpan(12));
            long windowSize = BinaryPrimitives.ReadUInt32LittleEndian(controlData.AsSpan(16));
            if (version == 2)
            {
                resetInterval *= LzxDecoder.FrameSize;
                windowSize *= LzxDecoder.FrameSize;
            }
            else if (version != 1)
            {
                throw new InvalidDataException($"The CHM file uses an unknown LZX version ({version}).");
            }
            if (windowSize <= 0 || (windowSize & (windowSize - 1)) != 0 || resetInterval <= 0 || resetInterval % LzxDecoder.FrameSize != 0)
                throw new InvalidDataException("The CHM file's compression settings are damaged.");
            var windowBits = System.Numerics.BitOperations.Log2((ulong)windowSize);

            var needed = Entries.Where(e => e.Section == 1).Select(e => e.Offset + e.Length).DefaultIfEmpty(0).Max();
            if (needed > _limits.MaxPackageBytes)
                throw DocumentLimits.Exceeded($"The CHM unpacks to more than {DocumentLimits.Megabytes(_limits.MaxPackageBytes)}.");
            if (needed > _limits.InflateRatioFloor && needed / Math.Max(1, content.Length) > _limits.MaxInflateRatio)
                throw DocumentLimits.Exceeded($"The CHM's compressed data unpacks to more than {_limits.MaxInflateRatio} times its size.");
            if (_contentOffset + content.Offset + content.Length > _stream.Length)
                throw new InvalidDataException("The CHM file's compressed data lies outside the file.");

            var path = Path.Combine(workFolder, $"chm-section1-{Guid.NewGuid():N}.bin");
            using var output = File.Create(path);
            var input = new LzxInput(_stream, _contentOffset + content.Offset, content.Length);
            var resetFrames = (int)Math.Min(int.MaxValue, resetInterval / LzxDecoder.FrameSize);
            new LzxDecoder(input, windowBits, resetFrames).Decompress(output, needed, cancellationToken);
            return path;
        }

        private byte[] ReadAt(long offset, int length, bool allowShort = false)
        {
            if (offset < 0 || offset > _stream.Length || (!allowShort && offset + length > _stream.Length))
                throw new InvalidDataException("The CHM file is cut short.");
            var bytes = new byte[(int)Math.Min(length, _stream.Length - offset)];
            _stream.Position = offset;
            _stream.ReadExactly(bytes);
            return bytes;
        }
    }
}
