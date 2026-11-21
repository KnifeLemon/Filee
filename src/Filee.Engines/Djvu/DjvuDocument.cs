// A DjVu document (DjVu v3 specification): an IFF85 file ("AT&T" then a FORM chunk) holding one page (FORM:DJVU) or a
// multi-page document (FORM:DJVM): a directory (DIRM) of components — pages (FORM:DJVU), files pages include
// (FORM:DJVI: shared shape dictionaries, annotations) and thumbnails (FORM:THUM) — and the bookmarks (NAVM). A bundled
// document holds the components at offsets in the file. An indirect one names them as separate files next to the
// index file: each is read only from inside the index file's folder (no absolute paths, no "..", no links on the way),
// and a missing page or included file refuses the document with error.djvu_indirect (thumbnails may be missing).
//
// A page is read when asked for: INFO (size, resolution, rotation), the mask (Sjbz, with the shape dictionary Djbz of
// the page or of a file it includes with INCL; or the G4-coded Smmr), the background (BG44 chunks, or BGjp), the
// foreground colours (FG44 or FGjp, or the palette FGbz), the hidden text (TXTz / TXTa) and the hyperlinks of the
// annotations (ANTa / ANTz, also those of the files the page includes). Layers Filee doesn't decode (JPEG 2000 BG2k /
// FG2k) are counted as pictures left out. Chunks are read one at a time, never more than DjvuLimits.MaxChunkBytes.

using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using SkiaSharp;

namespace Filee.Engines.Djvu;

/// <summary>A page's foreground palette: its colours and the colour of each blit of the mask.</summary>
internal sealed record DjvuPalette(SKColor[] Colors, int[]? BlitColors);

/// <summary>One decoded page.</summary>
internal sealed class DjvuPage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Dpi { get; init; }

    /// <summary>Quarter turns counter-clockwise the page is shown with (INFO's orientation).</summary>
    public int Rotation { get; init; }

    public Jb2Image? Mask { get; init; }
    public DjvuPixmap? Background { get; init; }
    public DjvuPixmap? Foreground { get; init; }
    public DjvuPalette? Palette { get; init; }
    public IReadOnlyList<DjvuLine> Text { get; init; } = [];

    /// <summary>Link areas Filee follows: to a page of the document (<see cref="DjvuLink.Page"/>) or a web address.</summary>
    public IReadOnlyList<DjvuLink> Links { get; init; } = [];

    /// <summary>Layers of the page Filee couldn't decode.</summary>
    public int LeftOut { get; init; }
}

/// <summary>An open DjVu file.</summary>
internal sealed class DjvuDocument : IDisposable
{
    /// <summary>The error key of an indirect document (pages in separate files) whose files Filee can't all read.</summary>
    public const string IndirectKey = "error.djvu_indirect";

    /// <summary>A chunk of the opened file (<paramref name="Path"/> null) or of a file of an indirect document.</summary>
    private sealed record Chunk(string Id, long Offset, int Size, string? Path);

    private sealed class Component(string id, string? path, long offset, long end, string kind)
    {
        public string Id { get; } = id;

        /// <summary>The component's own file in an indirect document; null for a part of the opened file.</summary>
        public string? Path { get; } = path;

        public long Offset { get; } = offset;
        public long End { get; } = end;
        public string Kind { get; } = kind;
        public List<Chunk>? Chunks { get; set; }
    }

    /// <summary>Names Windows keeps for devices; a component file may not be called one.</summary>
    private static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    private readonly FileStream _file;
    private readonly string _folder;
    private readonly DjvuLimits _limits;
    private readonly List<Component> _pages = [];
    private readonly Dictionary<string, Component> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pageByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Jb2Dictionary> _dictionaries = new(StringComparer.Ordinal);
    private FileStream? _part;
    private int _leftOut;

    private DjvuDocument(FileStream file, DjvuLimits limits)
    {
        _file = file;
        _folder = System.IO.Path.GetDirectoryName(file.Name) ?? "";
        _limits = limits;
    }

    public int Count => _pages.Count;

    /// <summary>Layers left out of the pages read so far.</summary>
    public int LeftOut => _leftOut;

    /// <summary>The document outline (NAVM) with its URLs as written; <see cref="PageOf"/> follows them.</summary>
    public IReadOnlyList<DjvuBookmark> Bookmarks { get; private set; } = [];

    /// <summary>Opens <paramref name="path"/>; refuses a file that isn't a DjVu document Filee reads.</summary>
    public static DjvuDocument Open(string path, DjvuLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var file = File.OpenRead(System.IO.Path.GetFullPath(path));
        var document = new DjvuDocument(file, limits ?? DjvuLimits.Default);
        try
        {
            document.ReadStructure(cancellationToken);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _file.Dispose();
        _part?.Dispose();
    }

    /// <summary>
    /// The page a link into the document points to: "#" and a page's id, name or title, its number counted from 1, or
    /// "+n" / "-n" pages from page <paramref name="from"/>; -1 when it points to no page.
    /// </summary>
    public int PageOf(string url, int from = -1)
    {
        if (url.Length < 2 || url[0] != '#')
            return -1;
        var target = url[1..];
        if (target[0] is '+' or '-' && long.TryParse(target.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var delta))
        {
            var page = from < 0 ? -1 : target[0] == '+' ? from + delta : from - delta;
            return page >= 0 && page < Count ? (int)page : -1;
        }
        if (_pageByName.TryGetValue(target, out var named))
            return named;
        return int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= Count ? number - 1 : -1;
    }

    private void ReadStructure(CancellationToken cancellationToken)
    {
        var start = FormStart(null);
        var (kind, end) = ReadForm(null, start) ?? throw new InvalidDataException("The file isn't a DjVu document.");
        switch (kind)
        {
            case "DJVU":
                _pages.Add(new Component("", null, start, end, kind));
                break;
            case "DJVM":
                ReadDirectory(start, end, cancellationToken);
                break;
            case "DJVI":
                throw new InvalidDataException("The file is a part DjVu pages include, not a document.");
            default:
                throw new InvalidDataException($"The DjVu file holds a {kind} form, not pages Filee reads.");
        }
        if (_pages.Count == 0)
            throw new InvalidDataException("The DjVu document has no pages.");
    }

    /// <summary>Where a DjVu file's FORM chunk starts: after "AT&amp;T" when the file starts with it.</summary>
    private long FormStart(string? path)
    {
        var magic = ReadBytes(path, 0, (int)Math.Min(4, StreamOf(path).Length));
        return magic.AsSpan().SequenceEqual("AT&T"u8) ? 4 : 0;
    }

    /// <summary>The kind and end of a FORM chunk at <paramref name="offset"/>, or null when there is none.</summary>
    private (string Kind, long End)? ReadForm(string? path, long offset)
    {
        var length = StreamOf(path).Length;
        if (offset < 0 || offset + 12 > length)
            return null;
        var header = ReadBytes(path, offset, 12);
        if (!header.AsSpan(0, 4).SequenceEqual("FORM"u8))
            return null;
        var size = ((long)header[4] << 24) | ((long)header[5] << 16) | ((long)header[6] << 8) | header[7];
        if (size < 4)
            return null;
        // A file cut short keeps the pages it still holds.
        var end = Math.Min(offset + 8 + size, length);
        return (Encoding.ASCII.GetString(header, 8, 4), end);
    }

    /// <summary>The chunks inside a form (from its data after the kind to its end).</summary>
    private List<Chunk> ListChunks(string? path, long offset, long end, CancellationToken cancellationToken)
    {
        var chunks = new List<Chunk>();
        var at = offset + 12;
        var header = new byte[8];
        var file = StreamOf(path);
        while (at + 8 <= end)
        {
            if ((chunks.Count & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            file.Position = at;
            file.ReadExactly(header);
            var size = ((long)header[4] << 24) | ((long)header[5] << 16) | ((long)header[6] << 8) | header[7];
            var data = at + 8;
            if (data + size > end)
                break; // a cut-short last chunk is left out
            chunks.Add(new Chunk(Encoding.ASCII.GetString(header, 0, 4), data, (int)Math.Min(size, int.MaxValue), path));
            if (chunks.Count > 1_000_000)
                throw DocumentLimits.Exceeded("A DjVu form has more than 1,000,000 chunks.");
            at = data + size;
            if ((at & 1) != 0)
                at++;
        }
        return chunks;
    }

    private byte[] ReadChunk(Chunk chunk)
    {
        if (chunk.Size > _limits.MaxChunkBytes)
            throw DocumentLimits.TooLarge($"A DjVu chunk is larger than {DocumentLimits.Megabytes(_limits.MaxChunkBytes)}.");
        return ReadBytes(chunk.Path, chunk.Offset, chunk.Size);
    }

    private byte[] ReadBytes(string? path, long offset, int count)
    {
        var data = new byte[count];
        var file = StreamOf(path);
        file.Position = offset;
        file.ReadExactly(data);
        return data;
    }

    /// <summary>The opened file, or the file of an indirect document's component (one of them kept open at a time).</summary>
    private FileStream StreamOf(string? path)
    {
        if (path is null)
            return _file;
        if (_part is null || _part.Name != path)
        {
            _part?.Dispose();
            _part = null;
            _part = File.OpenRead(path);
        }
        return _part;
    }

    /// <summary>
    /// The file an indirect document's component <paramref name="id"/> names (as written, or URL-decoded): a file below
    /// the index file's folder that exists, reached through no link (a symbolic link or junction could lead out of the
    /// folder); null when there is none.
    /// </summary>
    private string? PartPath(string id)
    {
        var unescaped = Uri.UnescapeDataString(id);
        string[] names = unescaped == id ? [id] : [id, unescaped];
        var root = System.IO.Path.TrimEndingDirectorySeparator(_folder) + System.IO.Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var name in names)
        {
            if (name.Length is 0 or > 1024 || name.Contains(':') || name.Any(char.IsControl) || System.IO.Path.IsPathRooted(name))
                continue;
            var segments = name.Split('/', '\\');
            if (segments.Any(s => s.Length == 0 || s is "." or ".." || DeviceNames.Contains(s.Split('.')[0].TrimEnd())))
                continue;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine([_folder, .. segments]));
            if (!full.StartsWith(root, comparison))
                continue;
            var safe = true;
            for (var at = full; safe && at.Length > root.Length; at = System.IO.Path.GetDirectoryName(at) ?? "")
            {
                FileSystemInfo info = at == full ? new FileInfo(at) : new DirectoryInfo(at);
                safe = info.Exists && info.LinkTarget is null && (info.Attributes & FileAttributes.ReparsePoint) == 0;
            }
            if (safe)
                return full;
        }
        return null;
    }

    private void ReadDirectory(long start, long end, CancellationToken cancellationToken)
    {
        var chunks = ListChunks(null, start, end, cancellationToken);
        var dirm = chunks.FirstOrDefault(c => c.Id == "DIRM") ?? throw new InvalidDataException("The DjVu document has no directory.");
        var data = ReadChunk(dirm);
        var reader = new DjvuBytes(data);
        var version = reader.U8();
        // Bundled: the components are in this file, at the offsets the directory gives. Indirect: each is a file.
        var bundled = (version & 0x80) != 0;
        version &= 0x7F;
        if (version > 1)
            throw new InvalidDataException("The DjVu directory has a version Filee doesn't know.");
        var count = reader.U16();
        if (count > _limits.MaxComponents)
            throw DocumentLimits.TooLarge($"The DjVu document has more than {DocumentLimits.Count(_limits.MaxComponents)} parts.");
        var offsets = new long[count];
        var sizes = new int[count];
        for (var i = 0; bundled && i < count; i++)
        {
            offsets[i] = reader.U32();
            if (version == 0)
                sizes[i] = reader.U24();
        }
        var table = BzzDecoder.Decode(data, reader.Position, data.Length - reader.Position, _limits.MaxUnpackedBytes, cancellationToken);
        var packed = new DjvuBytes(table);
        if (version > 0)
        {
            for (var i = 0; i < count; i++)
                sizes[i] = packed.U24();
        }
        var flags = new int[count];
        for (var i = 0; i < count; i++)
        {
            flags[i] = packed.U8();
            if (version == 0)
                flags[i] = ((flags[i] & 1) != 0 ? 1 : 0) | ((flags[i] & 2) != 0 ? 0x80 : 0) | ((flags[i] & 4) != 0 ? 0x40 : 0);
        }
        var names = new (string Id, string? Name, string? Title)[count];
        for (var i = 0; i < count; i++)
        {
            var id = packed.CString();
            var name = (flags[i] & 0x80) != 0 ? packed.CString() : null;
            var title = (flags[i] & 0x40) != 0 ? packed.CString() : null;
            names[i] = (id, name, title);
        }
        // Links name a page by its id, else its name, else its title (the first page of each).
        var byName = new[] { new List<(string, int)>(), [], [] };
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var (id, name, title) = names[i];
            var type = flags[i] & 0x3F;
            string? path = null;
            var offset = offsets[i];
            if (!bundled)
            {
                path = PartPath(id);
                if (path is null)
                {
                    if (type == 2)
                        continue; // thumbnails aren't needed
                    throw new ConversionRefusedException(IndirectKey, $"The file '{id}' of the indirect DjVu document isn't in the folder of its index file.");
                }
                offset = FormStart(path);
            }
            if (ReadForm(path, offset) is not { } form)
                throw new InvalidDataException("A part of the DjVu document isn't where its directory says.");
            var component = new Component(id, path, offset, form.End, form.Kind);
            _byId.TryAdd(id, component);
            if (type == 1)
            {
                if (form.Kind != "DJVU")
                    throw new InvalidDataException("A page of the DjVu document isn't a page.");
                if (_pages.Count >= _limits.MaxPages)
                    throw DocumentLimits.TooLarge($"The DjVu document has more than {DocumentLimits.Count(_limits.MaxPages)} pages.");
                byName[0].Add((id, _pages.Count));
                if (name is not null)
                    byName[1].Add((name, _pages.Count));
                if (title is not null)
                    byName[2].Add((title, _pages.Count));
                _pages.Add(component);
            }
        }
        foreach (var (key, page) in byName.SelectMany(list => list))
            _pageByName.TryAdd(key, page);

        if (chunks.FirstOrDefault(c => c.Id == "NAVM") is { } navm)
        {
            try
            {
                Bookmarks = DjvuAnnotations.ReadOutline(BzzDecoder.Decode(ReadChunk(navm), _limits.MaxUnpackedBytes, cancellationToken));
            }
            catch (InvalidDataException)
            {
                // Damaged bookmarks leave the PDF without an outline; the pages are still read.
            }
        }
    }

    private List<Chunk> ChunksOf(Component component, CancellationToken cancellationToken) =>
        component.Chunks ??= ListChunks(component.Path, component.Offset, component.End, cancellationToken);

    /// <summary>Page <paramref name="index"/> decoded.</summary>
    public DjvuPage ReadPage(int index, CancellationToken cancellationToken = default)
    {
        var component = _pages[index];
        var own = ChunksOf(component, cancellationToken);
        var info = own.FirstOrDefault(c => c.Id == "INFO") ?? throw new InvalidDataException("A DjVu page has no INFO chunk.");
        var (width, height, dpi, rotation) = ReadInfo(ReadChunk(info));

        var leftOut = 0;
        Jb2Image? mask = null;
        DjvuPalette? palette = null;
        var background = new Iw44Decoder(_limits);
        var foreground = new Iw44Decoder(_limits);
        DjvuPixmap? backgroundJpeg = null, foregroundJpeg = null;
        List<DjvuLine> text = [];
        foreach (var chunk in own)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (chunk.Id)
            {
                case "Sjbz":
                    mask = Jb2Decoder.DecodeImage(ReadChunk(chunk), Dictionary(component, cancellationToken), _limits, cancellationToken);
                    break;
                case "Smmr":
                    mask = ReadSmmr(ReadChunk(chunk), cancellationToken);
                    break;
                case "BG44":
                    background.Decode(ReadChunk(chunk), cancellationToken);
                    break;
                case "FG44":
                    foreground.Decode(ReadChunk(chunk), cancellationToken);
                    break;
                case "BGjp":
                    backgroundJpeg = DecodeJpeg(ReadChunk(chunk));
                    if (backgroundJpeg is null)
                        leftOut++;
                    break;
                case "FGjp":
                    foregroundJpeg = DecodeJpeg(ReadChunk(chunk));
                    if (foregroundJpeg is null)
                        leftOut++;
                    break;
                case "FGbz":
                    palette = ReadPalette(ReadChunk(chunk), cancellationToken);
                    break;
                case "TXTz":
                    text = ReadText(BzzDecoder.Decode(ReadChunk(chunk), _limits.MaxUnpackedBytes, cancellationToken));
                    break;
                case "TXTa":
                    text = ReadText(ReadChunk(chunk));
                    break;
                case "BG2k" or "FG2k":
                    leftOut++;
                    break;
            }
        }
        if (mask is not null && (mask.Width != width || mask.Height != height))
            throw new InvalidDataException("A DjVu page's mask has another size than the page.");
        // The links of the page and of the files it includes (annotations shared by several pages).
        var links = new List<DjvuLink>();
        foreach (var chunk in own.Concat(Included(component, cancellationToken).SelectMany(c => ChunksOf(c, cancellationToken))))
        {
            if (chunk.Id is "ANTa" or "ANTz")
                links.AddRange(ReadLinks(chunk, index, cancellationToken));
        }
        _leftOut += leftOut;
        return new DjvuPage
        {
            Width = width,
            Height = height,
            Dpi = dpi,
            Rotation = rotation,
            Mask = mask,
            Background = background.HasData ? background.ToPixmap(cancellationToken) : backgroundJpeg,
            Foreground = foreground.HasData ? foreground.ToPixmap(cancellationToken) : foregroundJpeg,
            Palette = palette,
            Text = text,
            Links = links,
            LeftOut = leftOut,
        };
    }

    private (int Width, int Height, int Dpi, int Rotation) ReadInfo(byte[] data)
    {
        if (data.Length < 5)
            throw new InvalidDataException("A DjVu page's INFO chunk is damaged.");
        var width = (data[0] << 8) | data[1];
        var height = (data[2] << 8) | data[3];
        var dpi = data.Length >= 8 && data[7] != 0xFF ? (data[7] << 8) | data[6] : 300;
        if (dpi is < 25 or > 6000)
            dpi = 300;
        var flags = data.Length >= 10 ? data[9] : 0;
        var rotation = (flags & 7) switch { 6 => 1, 2 => 2, 5 => 3, _ => 0 };
        if (width == 0 || height == 0)
            throw new InvalidDataException("A DjVu page has no size.");
        if ((long)width * height > _limits.MaxPagePixels)
            throw DocumentLimits.TooLarge($"A DjVu page has more than {DocumentLimits.Count(_limits.MaxPagePixels)} pixels.");
        return (width, height, dpi, rotation);
    }

    private List<DjvuLine> ReadText(byte[] data)
    {
        try
        {
            return DjvuText.Read(data, _limits);
        }
        catch (InvalidDataException)
        {
            // Damaged hidden text leaves the page without its text layer; the picture is still the page.
            return [];
        }
    }

    /// <summary>The links of an annotation chunk Filee follows: to a page of the document, or to a web address.</summary>
    private List<DjvuLink> ReadLinks(Chunk chunk, int page, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            var data = ReadChunk(chunk);
            if (chunk.Id == "ANTz")
                data = BzzDecoder.Decode(data, _limits.MaxUnpackedBytes, cancellationToken);
            text = Encoding.UTF8.GetString(data);
        }
        catch (InvalidDataException)
        {
            // Damaged annotations leave the page without links.
            return [];
        }
        var links = new List<DjvuLink>();
        foreach (var link in DjvuAnnotations.ReadLinks(text, _limits))
        {
            if (link.Url.StartsWith('#'))
            {
                if (PageOf(link.Url, page) is var target and >= 0)
                    links.Add(link with { Page = target });
            }
            else if (DjvuAnnotations.IsWebAddress(link.Url))
            {
                links.Add(link);
            }
        }
        return links;
    }

    /// <summary>
    /// An Smmr mask: "MMR", flags (bit 0: black and white swapped; bit 1: in stripes), width and height, then the G4
    /// data — in stripes, the rows per stripe, then each stripe's size and data, each coded on its own.
    /// </summary>
    private Jb2Image ReadSmmr(byte[] data, CancellationToken cancellationToken)
    {
        var reader = new DjvuBytes(data);
        if (reader.U8() != 'M' || reader.U8() != 'M' || reader.U8() != 'R')
            throw new InvalidDataException("A DjVu Smmr mask doesn't start with MMR.");
        var flags = reader.U8();
        var width = reader.U16();
        var height = reader.U16();
        if (width == 0 || height == 0)
            throw new InvalidDataException("A DjVu Smmr mask has no size.");
        if ((long)width * height > Math.Min(_limits.MaxPagePixels, _limits.MaxShapePixels))
            throw DocumentLimits.TooLarge($"A DjVu mask has more than {DocumentLimits.Count(Math.Min(_limits.MaxPagePixels, _limits.MaxShapePixels))} pixels.");
        var stride = (width + 7) / 8;
        var bits = new byte[(long)stride * height];
        if ((flags & 2) == 0)
        {
            CcittG4.Decode(data.AsSpan(reader.Position), width, height, bits, 0, cancellationToken);
        }
        else
        {
            var rows = reader.U16();
            if (rows == 0)
                throw new InvalidDataException("A DjVu Smmr mask has empty stripes.");
            for (var y = 0; y < height && reader.Remaining > 0; y += rows)
            {
                var size = reader.U32();
                if (size > reader.Remaining)
                    throw new InvalidDataException("A DjVu Smmr stripe is cut short.");
                CcittG4.Decode(data.AsSpan(reader.Position, (int)size), width, Math.Min(rows, height - y), bits, y, cancellationToken);
                reader.Bytes((int)size);
            }
        }
        // One shape covering the page, its row 0 at the bottom as JB2 shapes have it.
        var shape = new Jb2Shape(width, height);
        var invert = (flags & 1) != 0;
        for (var y = 0; y < height; y++)
        {
            if ((y & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var row = (long)y * stride;
            var target = (long)(height - 1 - y) * width;
            for (var x = 0; x < width; x++)
            {
                var black = (bits[row + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                shape.Bits[target + x] = (byte)(black != invert ? 1 : 0);
            }
        }
        return new Jb2Image(width, height, [shape], [new Jb2Blit(0, 0, 0)]);
    }

    private DjvuPalette ReadPalette(byte[] data, CancellationToken cancellationToken)
    {
        var reader = new DjvuBytes(data);
        var version = reader.U8();
        if ((version & 0x7F) != 0)
            throw new InvalidDataException("A DjVu palette has a version Filee doesn't know.");
        var size = reader.U16();
        if (size > _limits.MaxPaletteColors)
            throw DocumentLimits.Exceeded("A DjVu palette has too many colours.");
        var colors = new SKColor[size];
        for (var i = 0; i < size; i++)
        {
            // Stored blue, green, red.
            var b = (byte)reader.U8();
            var g = (byte)reader.U8();
            var r = (byte)reader.U8();
            colors[i] = new SKColor(r, g, b);
        }
        int[]? indices = null;
        if ((version & 0x80) != 0)
        {
            var count = reader.U24();
            if (count > _limits.MaxBlits)
                throw DocumentLimits.Exceeded($"A DjVu palette colours more than {DocumentLimits.Count(_limits.MaxBlits)} shapes.");
            var packed = BzzDecoder.Decode(data, reader.Position, data.Length - reader.Position, Math.Min(_limits.MaxUnpackedBytes, 2L * count), cancellationToken);
            if (packed.Length < 2L * count)
                throw new InvalidDataException("A DjVu palette's colour indices are cut short.");
            indices = new int[count];
            for (var i = 0; i < count; i++)
            {
                var index = (short)((packed[2 * i] << 8) | packed[2 * i + 1]);
                if (index < 0 || index >= size)
                    throw new InvalidDataException("A DjVu palette's colour index is out of range.");
                indices[i] = index;
            }
        }
        return new DjvuPalette(colors, indices);
    }

    /// <summary>The files <paramref name="start"/> includes with INCL chunks (it names them by id).</summary>
    private List<Component> Includes(Component start, HashSet<Component> seen, CancellationToken cancellationToken)
    {
        var included = new List<Component>();
        foreach (var include in ChunksOf(start, cancellationToken).Where(c => c.Id == "INCL"))
        {
            var id = Encoding.UTF8.GetString(ReadChunk(include)).Trim('\0', ' ', '\n', '\r');
            if (_byId.TryGetValue(id, out var target) && seen.Add(target))
                included.Add(target);
        }
        return included;
    }

    /// <summary>The files <paramref name="page"/> includes, directly or through other includes, each once, depth first.</summary>
    private List<Component> Included(Component page, CancellationToken cancellationToken)
    {
        var result = new List<Component>();
        var seen = new HashSet<Component> { page };
        var pending = new Stack<Component>();
        pending.Push(page);
        while (pending.Count > 0)
        {
            var component = pending.Pop();
            if (!ReferenceEquals(component, page))
                result.Add(component);
            var included = Includes(component, seen, cancellationToken);
            for (var i = included.Count - 1; i >= 0; i--)
                pending.Push(included[i]);
        }
        return result;
    }

    /// <summary>
    /// The first file with a Djbz chunk among <paramref name="start"/> (when <paramref name="includeSelf"/>) and the
    /// files it includes, depth first in the order of its INCL chunks, skipping files already <paramref name="seen"/>.
    /// </summary>
    private Component? FindDictionary(Component start, bool includeSelf, HashSet<Component> seen, CancellationToken cancellationToken)
    {
        var pending = new Stack<(Component Component, bool Check)>();
        pending.Push((start, includeSelf));
        seen.Add(start);
        while (pending.Count > 0)
        {
            var (component, check) = pending.Pop();
            if (check && ChunksOf(component, cancellationToken).Any(c => c.Id == "Djbz"))
                return component;
            var included = Includes(component, seen, cancellationToken);
            for (var i = included.Count - 1; i >= 0; i--)
                pending.Push((included[i], true));
        }
        return null;
    }

    /// <summary>A JPEG layer (BGjp, FGjp), or null when Skia can't decode it or it is too large.</summary>
    private DjvuPixmap? DecodeJpeg(byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data));
        if (codec is null || (long)codec.Info.Width * codec.Info.Height > _limits.MaxIw44Pixels)
            return null;
        using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        if (bitmap is null)
            return null;
        var pixels = bitmap.GetPixelSpan();
        var rgb = new byte[(long)bitmap.Width * bitmap.Height * 3];
        for (int i = 0, o = 0; o < rgb.Length; i += 4, o += 3)
        {
            rgb[o] = pixels[i];
            rgb[o + 1] = pixels[i + 1];
            rgb[o + 2] = pixels[i + 2];
        }
        return new DjvuPixmap(bitmap.Width, bitmap.Height, rgb);
    }

    /// <summary>
    /// The shape dictionary a page's mask may build on: the page's own Djbz, or that of the first file it includes
    /// (directly or through other includes) that has one. Decoded once per file.
    /// </summary>
    private Jb2Dictionary? Dictionary(Component page, CancellationToken cancellationToken)
    {
        // The chain of files whose dictionaries build on each other, each file looked at once.
        var chain = new List<Component>();
        var seen = new HashSet<Component>();
        var holder = FindDictionary(page, includeSelf: true, seen, cancellationToken);
        while (holder is not null)
        {
            chain.Add(holder);
            holder = FindDictionary(holder, includeSelf: false, seen, cancellationToken);
        }
        Jb2Dictionary? dictionary = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var component = chain[i];
            var key = component.Id + "@" + component.Offset;
            if (!_dictionaries.TryGetValue(key, out var decoded))
            {
                var djbz = ChunksOf(component, cancellationToken).First(c => c.Id == "Djbz");
                decoded = Jb2Decoder.DecodeDictionary(ReadChunk(djbz), dictionary, _limits, cancellationToken);
                // A few shared dictionaries serve a whole book; keep only the latest ones.
                if (_dictionaries.Count >= 8)
                    _dictionaries.Clear();
                _dictionaries[key] = decoded;
            }
            dictionary = decoded;
        }
        return dictionary;
    }
}
