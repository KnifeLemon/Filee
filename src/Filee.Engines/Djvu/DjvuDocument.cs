// A DjVu document (DjVu v3 specification): an IFF85 file ("AT&T" then a FORM chunk) holding one page (FORM:DJVU) or a
// bundled document (FORM:DJVM): a directory (DIRM) of components at offsets in the file — pages (FORM:DJVU), files
// pages include (FORM:DJVI: shared shape dictionaries, annotations) and thumbnails (FORM:THUM). An indirect document,
// whose directory names separate files, is refused with error.djvu_indirect.
//
// A page is read when asked for: INFO (size, resolution, rotation), the mask (Sjbz, with the shape dictionary Djbz of
// the page or of a file it includes with INCL), the background (BG44 chunks, or BGjp), the foreground colours (FG44 or
// FGjp, or the palette FGbz) and the hidden text (TXTz / TXTa). Layers Filee doesn't decode (Smmr masks, JPEG 2000
// BG2k / FG2k) are counted as pictures left out. Chunks are read one at a time, never more than DjvuLimits.MaxChunkBytes.

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

    /// <summary>Layers of the page Filee couldn't decode.</summary>
    public int LeftOut { get; init; }
}

/// <summary>An open DjVu file.</summary>
internal sealed class DjvuDocument : IDisposable
{
    /// <summary>The error key of an indirect document (pages in separate files).</summary>
    public const string IndirectKey = "error.djvu_indirect";

    private sealed record Chunk(string Id, long Offset, int Size);

    private sealed class Component(string id, long offset, long end, string kind)
    {
        public string Id { get; } = id;
        public long Offset { get; } = offset;
        public long End { get; } = end;
        public string Kind { get; } = kind;
        public List<Chunk>? Chunks { get; set; }
    }

    private readonly FileStream _file;
    private readonly DjvuLimits _limits;
    private readonly List<Component> _pages = [];
    private readonly Dictionary<string, Component> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Jb2Dictionary> _dictionaries = new(StringComparer.Ordinal);
    private int _leftOut;

    private DjvuDocument(FileStream file, DjvuLimits limits)
    {
        _file = file;
        _limits = limits;
    }

    public int Count => _pages.Count;

    /// <summary>Layers left out of the pages read so far.</summary>
    public int LeftOut => _leftOut;

    /// <summary>Opens <paramref name="path"/>; refuses a file that isn't a DjVu document Filee reads.</summary>
    public static DjvuDocument Open(string path, DjvuLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var file = File.OpenRead(path);
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

    public void Dispose() => _file.Dispose();

    private void ReadStructure(CancellationToken cancellationToken)
    {
        var start = 0L;
        var magic = ReadBytes(0, Math.Min(4, (int)_file.Length));
        if (magic.Length == 4 && magic.AsSpan().SequenceEqual("AT&T"u8))
            start = 4;
        var (kind, end) = ReadForm(start) ?? throw new InvalidDataException("The file isn't a DjVu document.");
        switch (kind)
        {
            case "DJVU":
                _pages.Add(new Component("", start, end, kind));
                break;
            case "DJVM":
                ReadBundle(start, end, cancellationToken);
                break;
            case "DJVI":
                throw new InvalidDataException("The file is a part DjVu pages include, not a document.");
            default:
                throw new InvalidDataException($"The DjVu file holds a {kind} form, not pages Filee reads.");
        }
        if (_pages.Count == 0)
            throw new InvalidDataException("The DjVu document has no pages.");
    }

    /// <summary>The kind and end of a FORM chunk at <paramref name="offset"/>, or null when there is none.</summary>
    private (string Kind, long End)? ReadForm(long offset)
    {
        if (offset < 0 || offset + 12 > _file.Length)
            return null;
        var header = ReadBytes(offset, 12);
        if (!header.AsSpan(0, 4).SequenceEqual("FORM"u8))
            return null;
        var size = ((long)header[4] << 24) | ((long)header[5] << 16) | ((long)header[6] << 8) | header[7];
        if (size < 4)
            return null;
        // A file cut short keeps the pages it still holds.
        var end = Math.Min(offset + 8 + size, _file.Length);
        return (Encoding.ASCII.GetString(header, 8, 4), end);
    }

    /// <summary>The chunks inside a form (from its data after the kind to its end).</summary>
    private List<Chunk> ListChunks(long offset, long end, CancellationToken cancellationToken)
    {
        var chunks = new List<Chunk>();
        var at = offset + 12;
        var header = new byte[8];
        while (at + 8 <= end)
        {
            if ((chunks.Count & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            _file.Position = at;
            _file.ReadExactly(header);
            var size = ((long)header[4] << 24) | ((long)header[5] << 16) | ((long)header[6] << 8) | header[7];
            var data = at + 8;
            if (data + size > end)
                break; // a cut-short last chunk is left out
            chunks.Add(new Chunk(Encoding.ASCII.GetString(header, 0, 4), data, (int)Math.Min(size, int.MaxValue)));
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
            throw DocumentLimits.Exceeded($"A DjVu chunk is larger than {DocumentLimits.Megabytes(_limits.MaxChunkBytes)}.");
        return ReadBytes(chunk.Offset, chunk.Size);
    }

    private byte[] ReadBytes(long offset, int count)
    {
        var data = new byte[count];
        _file.Position = offset;
        _file.ReadExactly(data);
        return data;
    }

    private void ReadBundle(long start, long end, CancellationToken cancellationToken)
    {
        var chunks = ListChunks(start, end, cancellationToken);
        var dirm = chunks.FirstOrDefault(c => c.Id == "DIRM") ?? throw new InvalidDataException("The DjVu document has no directory.");
        var data = ReadChunk(dirm);
        var reader = new DjvuBytes(data);
        var version = reader.U8();
        if ((version & 0x80) == 0)
            throw new ConversionRefusedException(IndirectKey, "The DjVu document's pages are separate files (an indirect document).");
        version &= 0x7F;
        if (version > 1)
            throw new InvalidDataException("The DjVu directory has a version Filee doesn't know.");
        var count = reader.U16();
        if (count > _limits.MaxComponents)
            throw DocumentLimits.Exceeded($"The DjVu document has more than {DocumentLimits.Count(_limits.MaxComponents)} parts.");
        var offsets = new long[count];
        var sizes = new int[count];
        for (var i = 0; i < count; i++)
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
        for (var i = 0; i < count; i++)
        {
            var id = packed.CString();
            if ((flags[i] & 0x80) != 0)
                packed.CString();
            if ((flags[i] & 0x40) != 0)
                packed.CString();
            if (ReadForm(offsets[i]) is not { } form)
                throw new InvalidDataException("A part of the DjVu document isn't where its directory says.");
            var component = new Component(id, offsets[i], form.End, form.Kind);
            _byId.TryAdd(id, component);
            if ((flags[i] & 0x3F) == 1)
            {
                if (form.Kind != "DJVU")
                    throw new InvalidDataException("A page of the DjVu document isn't a page.");
                if (_pages.Count >= _limits.MaxPages)
                    throw DocumentLimits.Exceeded($"The DjVu document has more than {DocumentLimits.Count(_limits.MaxPages)} pages.");
                _pages.Add(component);
            }
        }
    }

    private List<Chunk> ChunksOf(Component component, CancellationToken cancellationToken) =>
        component.Chunks ??= ListChunks(component.Offset, component.End, cancellationToken);

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
                case "Smmr" or "BG2k" or "FG2k":
                    leftOut++;
                    break;
            }
        }
        if (mask is not null && (mask.Width != width || mask.Height != height))
            throw new InvalidDataException("A DjVu page's mask has another size than the page.");
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
            throw DocumentLimits.Exceeded($"A DjVu page has more than {DocumentLimits.Count(_limits.MaxPagePixels)} pixels.");
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
            var chunks = ChunksOf(component, cancellationToken);
            if (check && chunks.Any(c => c.Id == "Djbz"))
                return component;
            var included = new List<Component>();
            foreach (var include in chunks.Where(c => c.Id == "INCL"))
            {
                var id = Encoding.UTF8.GetString(ReadChunk(include)).Trim('\0', ' ', '\n', '\r');
                if (_byId.TryGetValue(id, out var target) && seen.Add(target))
                    included.Add(target);
            }
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
