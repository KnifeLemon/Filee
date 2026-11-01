// An HWP 5.0 document opened from its compound file: the FileHeader stream (signature, version and the flags that say
// how the rest is stored), then DocInfo, the BodyText/SectionN streams and BinData, unpacked (raw deflate when the
// document is compressed) and split into records.
//
// What Filee can't read is refused with a message that says what to do, before anything else is read: password-
// protected, DRM and certificate-encrypted documents, distribution documents (배포용, whose text is in encrypted
// ViewText streams) and HWP 3.x files (한글 97 and older, not compound files). Nothing is ever decrypted.
//
// Layout of the header and records as described in Hancom's published "한글 문서 파일 형식 5.0" document.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Binary;

/// <summary>An HWP 5.0 document's streams.</summary>
internal sealed class HwpFile : IDisposable
{
    private const string SignatureText = "HWP Document File";

    private readonly CompoundFile _cfb;

    private HwpFile(CompoundFile cfb, uint version, uint flags)
    {
        _cfb = cfb;
        Version = version;
        Flags = flags;
    }

    /// <summary>The file's format version, 0xMMnnPPrr (5.0.3.0 is 0x05000300).</summary>
    public uint Version { get; }

    /// <summary>FileHeader properties: bit 0 compressed, 1 password, 2 distribution, 4 DRM, ...</summary>
    public uint Flags { get; }

    public bool Compressed => (Flags & 1) != 0;

    public DocumentLimits Limits => _cfb.Limits;

    /// <summary>Opens <paramref name="path"/>, refusing what Filee doesn't read.</summary>
    public static HwpFile Open(string path, DocumentLimits limits)
    {
        if (!CompoundFile.HasSignature(path))
            throw NotCompound(path);
        var cfb = CompoundFile.Open(path, limits);
        try
        {
            return Open(cfb);
        }
        catch
        {
            cfb.Dispose();
            throw;
        }
    }

    private static HwpFile Open(CompoundFile cfb)
    {
        var header = cfb.Read("FileHeader");
        if (header is null || header.Length < 40 || Encoding.ASCII.GetString(header, 0, SignatureText.Length) != SignatureText)
            throw new InvalidDataException("The file is not an HWP document (it has no HWP file header). It may be another program's file with an .hwp name.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36));

        if (version >> 24 != 5)
            throw new InvalidDataException($"The HWP file has format version {version >> 24}.{(version >> 16) & 0xFF}; only HWP 5.x is read. Open it in 한글 (Hangul), save it as HWP or HWPX and convert it again.");
        if ((flags & 0x2) != 0)
            throw new InvalidDataException("The HWP file is password-protected. Open it in 한글 (Hangul), remove the password, save it and convert it again.");
        if ((flags & (0x10 | 0x100 | 0x400)) != 0)
            throw new InvalidDataException("The HWP file is protected by document security (DRM or certificate encryption) and can't be read without it.");
        if ((flags & 0x4) != 0 || cfb.Exists("ViewText"))
            throw new InvalidDataException("The HWP file is a distribution document (배포용 문서), whose text is encrypted. Filee does not decrypt it; ask its author for an editable copy.");
        return new HwpFile(cfb, version, flags);
    }

    /// <summary>The message for a file that is not a compound file: HWP 3.x, an HWPX with an .hwp name, or something else.</summary>
    private static InvalidDataException NotCompound(string path)
    {
        var start = new byte[32];
        using (var file = File.OpenRead(path))
            _ = file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        var text = Encoding.ASCII.GetString(start);
        if (text.StartsWith("HWP Document File V", StringComparison.Ordinal))
            return new InvalidDataException("The file is an HWP 3.x document (한글 97 or older), which Filee does not read. Open it in 한글 (Hangul), save it as HWP or HWPX and convert it again.");
        return new InvalidDataException("The file is not an HWP 5.0 document. It may be damaged or protected by document security (DRM) software.");
    }

    public void Dispose() => _cfb.Dispose();

    /// <summary>True when the bytes start like a ZIP package (an HWPX file named .hwp).</summary>
    public static bool IsZip(string path)
    {
        Span<byte> start = stackalloc byte[4];
        using var file = File.OpenRead(path);
        return file.ReadAtLeast(start, 4, throwOnEndOfStream: false) == 4 && start is [0x50, 0x4B, 0x03, 0x04];
    }

    // ───────────────────────── Streams ─────────────────────────

    /// <summary>The DocInfo records.</summary>
    public List<HwpRecord> DocInfo() =>
        HwpRecord.Parse(Unpack(_cfb.Read("DocInfo") ?? throw new InvalidDataException("The HWP file is incomplete: DocInfo is missing."), Compressed, "DocInfo"), Limits);

    /// <summary>Records of every BodyText section in order (Section0, Section1, ...), stopping at the first missing one.</summary>
    public IEnumerable<List<HwpRecord>> Sections()
    {
        for (var i = 0; ; i++)
        {
            var bytes = _cfb.Read($"BodyText/Section{i}");
            if (bytes is null)
            {
                if (i == 0)
                    throw new InvalidDataException("The HWP file is incomplete: BodyText/Section0 is missing.");
                yield break;
            }
            yield return HwpRecord.Parse(Unpack(bytes, Compressed, $"Section{i}"), Limits);
        }
    }

    /// <summary>
    /// The bytes of an embedded binary (BinData/BIN0001.jpg): unpacked when <paramref name="compressed"/> says so;
    /// null when it is missing, larger than a part may be or can't be unpacked.
    /// </summary>
    public byte[]? BinData(string name, bool compressed)
    {
        byte[]? bytes;
        try
        {
            bytes = _cfb.Read("BinData/" + name);
        }
        catch (InvalidDataException)
        {
            return null; // a damaged picture is left out, the text is still read
        }
        if (bytes is null || !compressed)
            return bytes;
        try
        {
            return Unpack(bytes, true, name);
        }
        catch (InvalidDataException)
        {
            // Some writers (rhwp) store pictures as they are in a compressed document: the bytes are the picture.
            return bytes;
        }
    }

    /// <summary>rhwp wrote the file (it adds a stream with the HWPX it converted): its line layout is rhwp's own estimate.</summary>
    public bool MadeByRhwp => _cfb.Exists("RhwpHwpxOrigin");

    /// <summary>The document has summary information (its title may still be empty).</summary>
    public bool HasSummary => _cfb.Exists("\u0005HwpSummaryInformation");

    /// <summary>The document's title from its summary information (<see cref="PropertySet"/>); null when there is none.</summary>
    public string? Title()
    {
        if (!HasSummary)
            return null;
        try
        {
            return PropertySet.Title(_cfb.Read("\u0005HwpSummaryInformation"));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Raw deflate (no zlib header) when the document is compressed, counted against the limits while it inflates.
    /// </summary>
    private byte[] Unpack(byte[] bytes, bool compressed, string name)
    {
        if (!compressed)
            return bytes;
        using var input = new MemoryStream(bytes, writable: false);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = inflate.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                _cfb.Count(read);
                if (output.Length > Limits.MaxPartBytes)
                    throw DocumentLimits.Exceeded($"The part {name} unpacks to more than {DocumentLimits.Megabytes(Limits.MaxPartBytes)}.");
                if (output.Length > Limits.InflateRatioFloor && output.Length / Math.Max(1, bytes.Length) > Limits.MaxInflateRatio)
                    throw DocumentLimits.Exceeded($"The part {name} unpacks to more than {Limits.MaxInflateRatio} times its packed size.");
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"The HWP file is damaged: {name} can't be unpacked.", ex);
        }
        return output.ToArray();
    }
}

/// <summary>Record tags of DocInfo and BodyText (HWPTAG_BEGIN is 0x10).</summary>
internal static class HwpTag
{
    public const int DocumentProperties = 16;
    public const int IdMappings = 17;
    public const int BinData = 18;
    public const int FaceName = 19;
    public const int BorderFill = 20;
    public const int CharShape = 21;
    public const int TabDef = 22;
    public const int Numbering = 23;
    public const int Bullet = 24;
    public const int ParaShape = 25;
    public const int Style = 26;
    public const int CompatibleDocument = 30;

    public const int ParaHeader = 66;
    public const int ParaText = 67;
    public const int ParaCharShape = 68;
    public const int ParaLineSeg = 69;
    public const int ParaRangeTag = 70;
    public const int CtrlHeader = 71;
    public const int ListHeader = 72;
    public const int PageDef = 73;
    public const int FootnoteShape = 74;
    public const int PageBorderFill = 75;
    public const int ShapeComponent = 76;
    public const int Table = 77;
    public const int ShapeLine = 78;
    public const int ShapeRectangle = 79;
    public const int ShapeEllipse = 80;
    public const int ShapeArc = 81;
    public const int ShapePolygon = 82;
    public const int ShapeCurve = 83;
    public const int ShapeOle = 84;
    public const int ShapePicture = 85;
    public const int ShapeContainer = 86;
    public const int CtrlData = 87;
    public const int EqEdit = 88;
    public const int ShapeTextArt = 90;
}

/// <summary>
/// One record: a tag (what it holds), a level (records of a higher level after it belong to it) and its bytes. The
/// header packs the tag in bits 0-9, the level in bits 10-19 and the size in bits 20-31; a size of 0xFFF says the real
/// size follows in the next four bytes.
/// </summary>
internal sealed record HwpRecord(int Tag, int Level, byte[] Data)
{
    /// <summary>Records under this one (built by <see cref="Tree"/>).</summary>
    public List<HwpRecord> Children { get; } = [];

    /// <summary>Splits a stream into records; a record cut short fails, the limits bound how many there are.</summary>
    public static List<HwpRecord> Parse(byte[] bytes, DocumentLimits limits)
    {
        var records = new List<HwpRecord>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 4)
                throw new InvalidDataException("The HWP file is damaged: a record header is cut short.");
            var header = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            offset += 4;
            long size = header >> 20;
            if (size == 0xFFF)
            {
                if (bytes.Length - offset < 4)
                    throw new InvalidDataException("The HWP file is damaged: a record header is cut short.");
                size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                offset += 4;
            }
            if (size > bytes.Length - offset)
                throw new InvalidDataException("The HWP file is damaged: a record is longer than its stream.");
            if (records.Count >= limits.MaxXmlElements)
                throw DocumentLimits.Exceeded($"The document has more than {DocumentLimits.Count(limits.MaxXmlElements)} records.");
            records.Add(new HwpRecord((int)(header & 0x3FF), (int)((header >> 10) & 0x3FF), bytes.AsSpan(offset, (int)size).ToArray()));
            offset += (int)size;
        }
        return records;
    }

    /// <summary>
    /// The records as a tree: each record holds the records of a higher level that follow it. Returns the top-level
    /// records (the lowest level of the stream).
    /// </summary>
    public static List<HwpRecord> Tree(List<HwpRecord> records)
    {
        var roots = new List<HwpRecord>();
        var open = new Stack<HwpRecord>();
        foreach (var record in records)
        {
            while (open.Count > 0 && open.Peek().Level >= record.Level)
                open.Pop();
            if (open.Count == 0)
                roots.Add(record);
            else
                open.Peek().Children.Add(record);
            open.Push(record);
        }
        return roots;
    }

    public RecordReader Reader() => new(Data);

    public override string ToString() => $"tag {Tag} level {Level} ({Data.Length} bytes)";
}

/// <summary>
/// Little-endian reading of a record's bytes. Reading past the end gives zeros (records grow from version to version:
/// an older file simply lacks the fields at the end), and <see cref="Remaining"/> tells whether a field is there.
/// </summary>
internal ref struct RecordReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; set; }

    public readonly int Remaining => Math.Max(0, _data.Length - Position);

    public byte U8() => Take(1) is { Length: 1 } b ? b[0] : (byte)0;

    public sbyte I8() => (sbyte)U8();

    public ushort U16() => Take(2) is { Length: 2 } b ? BinaryPrimitives.ReadUInt16LittleEndian(b) : (ushort)0;

    public short I16() => (short)U16();

    public uint U32() => Take(4) is { Length: 4 } b ? BinaryPrimitives.ReadUInt32LittleEndian(b) : 0;

    public int I32() => (int)U32();

    public double F64() => Take(8) is { Length: 8 } b ? BinaryPrimitives.ReadDoubleLittleEndian(b) : 0;

    public void Skip(int count) => Position += Math.Max(0, count);

    /// <summary>A string: a WORD length, then that many UTF-16 code units.</summary>
    public string Text()
    {
        var length = U16();
        return Chars(length);
    }

    /// <summary><paramref name="count"/> UTF-16 code units.</summary>
    public string Chars(int count)
    {
        var bytes = Take(count * 2);
        return Encoding.Unicode.GetString(bytes);
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        var start = Math.Min(Position, _data.Length);
        var length = Math.Min(count, _data.Length - start);
        Position += count;
        return _data.Slice(start, Math.Max(0, length));
    }
}
