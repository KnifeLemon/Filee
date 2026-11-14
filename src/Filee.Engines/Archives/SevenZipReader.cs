// Reads .7z archives (7zFormat.txt from the LZMA SDK) with Filee's own decoders, without 7-Zip.
//
// Layout: a 32-byte signature header (signature, version, the CRC of the start header, and the start header: offset,
// size and CRC of the header at the end), the packed streams, then the header. The header may itself be packed (an
// "encoded header": a streams description whose single folder unpacks to the real header). The header lists the
// packed streams, the folders (each a small graph of coders joined by bind pairs, fed by one or more packed streams,
// with an unpacked size per coder output and maybe a CRC), how many files each folder holds and their sizes and
// CRC-32s (substreams), and the files info: names, which entries have no data (folders, empty files, anti-items),
// times and attributes.
//
// Unpacking goes through the files in order, and so through each folder's files in the order they were packed: a
// solid block is decoded once, front to back. Coders are in SevenZipCoders. An archive with a method Filee has no
// decoder for is reported (SevenZipUnsupportedException) before anything is written, so another reader can take it;
// one with an AES coder is refused as password-protected.
//
// Limits: the header is refused past MaxHeaderSize before it is read or unpacked, every count is checked against
// what the bytes left in the header can hold (and the entry cap) before anything is allocated, dictionaries against
// the decoder's limit, the folders' unpacked sizes against the bomb ratio, and the listing goes through
// ArchiveSafety.CheckListing like every other reader's. What is written is counted by UnpackBudget.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>One coder of a 7z folder.</summary>
/// <param name="InStreams">Packed-side streams (BCJ2 has four).</param>
/// <param name="OutStreams">Unpacked-side streams (one for every method Filee knows).</param>
internal sealed record SevenZipCoder(ulong Method, int InStreams, int OutStreams, byte[] Properties);

/// <summary>A 7z folder: coders, how they are joined, and what they unpack to.</summary>
internal sealed class SevenZipFolder
{
    public required SevenZipCoder[] Coders { get; init; }

    /// <summary>Bind pairs: a coder's packed-side stream (by index over all coders) is fed by an unpacked-side one.</summary>
    public required (int In, int Out)[] BindPairs { get; init; }

    /// <summary>The packed-side stream each of the folder's packed streams feeds, in order.</summary>
    public required int[] PackedStreams { get; init; }

    /// <summary>The unpacked size of every coder output.</summary>
    public long[] UnpackSizes { get; set; } = [];

    /// <summary>The output that is the folder's result (the one no bind pair takes).</summary>
    public required int MainOut { get; init; }

    public uint? Crc { get; set; }

    /// <summary>The first of the archive's packed streams this folder reads.</summary>
    public int FirstPackStream { get; set; }

    /// <summary>Files whose data the folder holds, one after the other.</summary>
    public int SubStreams { get; set; } = 1;

    public long UnpackSize => UnpackSizes[MainOut];

    /// <summary>The first method in the folder Filee does not decode, or null.</summary>
    public string? Unsupported =>
        Coders.FirstOrDefault(c => SevenZipCoders.InputsOf(c.Method) != c.InStreams || c.OutStreams != 1) is { } coder
            ? SevenZipCoders.NameOf(coder.Method) : null;

    public bool IsEncrypted => Coders.Any(c => c.Method == SevenZipCoders.Aes);
}

/// <summary>One entry of a 7z archive.</summary>
internal sealed class SevenZipItem
{
    public string Name { get; set; } = "";
    public bool HasStream { get; set; } = true;
    public bool IsFolder { get; set; }
    public bool IsAnti { get; set; }
    public long Size { get; set; }
    public uint? Crc { get; set; }
    public DateTime? Modified { get; set; }
    public uint? Attributes { get; set; }

    /// <summary>
    /// A symbolic link or junction: the Windows reparse-point attribute, or a Unix mode of type link in the high 16 bits
    /// (with the 0x8000 flag that says they hold one).
    /// </summary>
    public bool IsLink => Attributes is { } a && ((a & 0x400) != 0 || (a & 0x8000) != 0 && (a >> 16 & 0xF000) == 0xA000);
}

/// <summary>What a 7z header says.</summary>
internal sealed class SevenZipListing
{
    /// <summary>Offset of every packed stream in the archive, and its size.</summary>
    public long[] PackOffsets { get; init; } = [];
    public long[] PackSizes { get; init; } = [];
    public SevenZipFolder[] Folders { get; init; } = [];
    public SevenZipItem[] Items { get; init; } = [];

    /// <summary>Size and CRC of every file with data, folder by folder (from the substreams info).</summary>
    public List<(long Size, uint? Crc)> SubStreams { get; set; } = [];

    /// <summary>The first method Filee does not decode, or null when it decodes them all.</summary>
    public string? Unsupported => Folders.Select(f => f.Unsupported).FirstOrDefault(m => m is not null);
}

/// <summary>Lists and unpacks 7z archives.</summary>
internal static class SevenZipReader
{
    private const int SignatureHeaderSize = 32;

    /// <summary>The largest header (packed or not) read: a million entries with long names stay far below.</summary>
    public const long MaxHeaderSize = 512L << 20;

    /// <summary>Coders and coder streams in one folder, as 7-Zip allows.</summary>
    private const int MaxCoders = 64;

    private static ReadOnlySpan<byte> Signature => [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C];

    // Property ids of 7zFormat.txt.
    private const byte IdEnd = 0x00;
    private const byte IdHeader = 0x01;
    private const byte IdArchiveProperties = 0x02;
    private const byte IdAdditionalStreamsInfo = 0x03;
    private const byte IdMainStreamsInfo = 0x04;
    private const byte IdFilesInfo = 0x05;
    private const byte IdPackInfo = 0x06;
    private const byte IdUnpackInfo = 0x07;
    private const byte IdSubStreamsInfo = 0x08;
    private const byte IdSize = 0x09;
    private const byte IdCrc = 0x0A;
    private const byte IdFolder = 0x0B;
    private const byte IdCodersUnpackSize = 0x0C;
    private const byte IdNumUnpackStream = 0x0D;
    private const byte IdEmptyStream = 0x0E;
    private const byte IdEmptyFile = 0x0F;
    private const byte IdAnti = 0x10;
    private const byte IdName = 0x11;
    private const byte IdMTime = 0x14;
    private const byte IdWinAttributes = 0x15;
    private const byte IdEncodedHeader = 0x17;

    /// <summary>True when <paramref name="stream"/> starts with the 7z signature (its position is put back).</summary>
    public static bool HasSignature(Stream stream)
    {
        var position = stream.Position;
        Span<byte> head = stackalloc byte[6];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = position;
        return read == head.Length && head.SequenceEqual(Signature);
    }

    /// <summary>
    /// True for a 7z file whose header can be read and that uses a method Filee does not decode (PPMd, RISC-V …), so the
    /// bundled 7-Zip should unpack it. False for anything else, damage included: the in-process reader gives the reason.
    /// </summary>
    public static bool NeedsOtherDecoder(string archive)
    {
        try
        {
            using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return HasSignature(file) && Read(file, Path.GetFileName(archive), CancellationToken.None).Unsupported is not null;
        }
        catch (SevenZipUnsupportedException)
        {
            return true; // the header itself is packed with such a method
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Unpacks the 7z archive in <paramref name="stream"/> (seekable, starting at position 0) into
    /// <paramref name="folder"/>. Throws <see cref="SevenZipUnsupportedException"/>, before writing anything, when it
    /// uses a method Filee does not decode.
    /// </summary>
    public static void Extract(Stream stream, string name, string folder, CancellationToken cancellationToken)
    {
        var listing = Read(stream, name, cancellationToken);
        if (listing.Unsupported is { } method)
            throw new SevenZipUnsupportedException(method);
        var length = stream.Length;
        ArchiveSafety.CheckListing([.. listing.Items.Where(i => !i.IsAnti).Select(i => new ArchiveItem(i.Name, i.IsFolder, i.IsFolder ? 0 : i.Size, IsLink: i.IsLink))],
            name, length, folder);

        var writer = new EntryWriter(folder, new UnpackBudget(name, length, folder), cancellationToken);
        using var folders = new FolderSequence(stream, listing, name, cancellationToken);
        foreach (var item in listing.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsAnti)
                continue;
            if (!item.HasStream)
            {
                if (item.IsFolder)
                    writer.AddFolder(item.Name, item.Modified);
                else if (!item.IsLink)
                    writer.AddFile(item.Name, Stream.Null, 0, null, item.Modified);
                continue;
            }
            var data = folders.Next(item.Size);
            if (item.IsLink)
                Skip(data, cancellationToken);
            else
                writer.AddFile(item.Name, data, item.Size, item.Crc, item.Modified);
        }
        folders.Finish();
        writer.Finish();
    }

    private static void Skip(Stream data, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        while (data.Read(buffer) > 0)
            cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Reads the header of the 7z archive in <paramref name="stream"/> (seekable, starting at position 0). An archive
    /// with an AES coder (or an encrypted header) is refused as password-protected.
    /// </summary>
    public static SevenZipListing Read(Stream stream, string name, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        Span<byte> start = stackalloc byte[SignatureHeaderSize];
        if (stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) < start.Length || !start[..6].SequenceEqual(Signature))
            throw ArchiveSafety.NotAnArchive(name);
        if (start[6] != 0)
            throw new SevenZipUnsupportedException($"format version {start[6]}.{start[7]}");
        if (BinaryPrimitives.ReadUInt32LittleEndian(start[8..]) != Crc32.Compute(start[12..]))
            throw ArchiveSafety.Damaged(name, "the start header checksum does not match");
        var nextOffset = BinaryPrimitives.ReadUInt64LittleEndian(start[12..]);
        var nextSize = BinaryPrimitives.ReadUInt64LittleEndian(start[20..]);
        var nextCrc = BinaryPrimitives.ReadUInt32LittleEndian(start[28..]);
        if (nextSize == 0)
            return nextOffset == 0 ? new SevenZipListing() : throw ArchiveSafety.Damaged(name, "the header is missing");
        var length = stream.Length;
        if (nextOffset > (ulong)length || nextSize > (ulong)length || SignatureHeaderSize + (long)nextOffset + (long)nextSize > length)
            throw ArchiveSafety.Damaged(name, "the archive ends before its header");
        if ((long)nextSize > MaxHeaderSize)
            throw HeaderTooLarge(name);

        var header = new byte[nextSize];
        stream.Position = SignatureHeaderSize + (long)nextOffset;
        stream.ReadExactly(header);
        if (Crc32.Compute(header) != nextCrc)
            throw ArchiveSafety.Damaged(name, "the header checksum does not match");

        for (var round = 0; ; round++)
        {
            var reader = new HeaderReader(header, name);
            var id = reader.Byte();
            if (id == IdHeader)
            {
                var listing = ReadHeader(reader, name, length);
                if (listing.Folders.Any(f => f.IsEncrypted))
                    throw ArchiveSafety.Encrypted(name);
                return listing;
            }
            if (id != IdEncodedHeader || round == 4)
                throw ArchiveSafety.Damaged(name, "the header is not a 7z header");
            var streams = ReadStreamsInfo(reader, name, length);
            header = UnpackHeader(stream, streams, name, length, cancellationToken);
        }
    }

    private static Exception HeaderTooLarge(string name) =>
        ArchiveSafety.Oversized(name, $"its header is larger than the {ArchiveSafety.FormatSize(MaxHeaderSize)} Filee reads");

    /// <summary>Unpacks an encoded header: the single folder of <paramref name="streams"/>.</summary>
    private static byte[] UnpackHeader(Stream stream, SevenZipListing streams, string name, long length, CancellationToken cancellationToken)
    {
        if (streams.Folders.Length != 1)
            throw ArchiveSafety.Damaged(name, "the packed header is not one folder");
        var folder = streams.Folders[0];
        if (folder.IsEncrypted)
            throw ArchiveSafety.Encrypted(name);
        if (folder.Unsupported is { } method)
            throw new SevenZipUnsupportedException(method);
        if (folder.UnpackSize > MaxHeaderSize)
            throw HeaderTooLarge(name);
        ArchiveSafety.CheckRatio(folder.UnpackSize, length, name);
        using var data = FolderSequence.Open(stream, streams, 0, name, cancellationToken);
        var header = new byte[folder.UnpackSize];
        data.ReadExactly(header);
        if (folder.Crc is { } crc && Crc32.Compute(header) != crc)
            throw ArchiveSafety.Damaged(name, "the packed header checksum does not match");
        return header;
    }

    private static SevenZipListing ReadHeader(HeaderReader r, string name, long length)
    {
        var id = r.Byte();
        if (id == IdArchiveProperties)
        {
            while (r.Byte() != IdEnd)
                r.Skip(r.Size());
            id = r.Byte();
        }
        if (id == IdAdditionalStreamsInfo)
            throw new SevenZipUnsupportedException("additional streams");
        var streams = new SevenZipListing();
        if (id == IdMainStreamsInfo)
        {
            streams = ReadStreamsInfo(r, name, length);
            id = r.Byte();
        }
        var items = Array.Empty<SevenZipItem>();
        if (id == IdFilesInfo)
        {
            items = ReadFilesInfo(r, streams, name);
            id = r.Byte();
        }
        if (id != IdEnd)
            throw ArchiveSafety.Damaged(name, "the header has an unknown part");
        // Files with data were matched with the substreams in ReadFilesInfo; nothing may be left over.
        var withData = items.Count(i => i.HasStream);
        if (withData != streams.Folders.Sum(f => (long)f.SubStreams))
            throw ArchiveSafety.Damaged(name, "the header lists more data streams than files");
        return new SevenZipListing { PackOffsets = streams.PackOffsets, PackSizes = streams.PackSizes, Folders = streams.Folders, Items = items, SubStreams = streams.SubStreams };
    }

    /// <summary>Pack info, coders info and substreams info; the files' sizes and CRCs go into the substream list.</summary>
    private static SevenZipListing ReadStreamsInfo(HeaderReader r, string name, long length)
    {
        long[] offsets = [];
        long[] sizes = [];
        SevenZipFolder[] folders = [];
        var id = r.Byte();
        if (id == IdPackInfo)
        {
            var position = SignatureHeaderSize + r.Size();
            var count = r.Count(r.Left);
            sizes = new long[count];
            var haveSizes = count == 0;
            for (id = r.Byte(); id != IdEnd; id = r.Byte())
            {
                if (id == IdSize)
                {
                    for (var i = 0; i < count; i++)
                        sizes[i] = r.Size();
                    haveSizes = true;
                }
                else if (id == IdCrc)
                {
                    r.Digests(count);
                }
                else
                {
                    r.Skip(r.Size());
                }
            }
            if (!haveSizes)
                throw ArchiveSafety.Damaged(name, "the header gives no packed sizes");
            offsets = new long[count];
            for (var i = 0; i < count; i++)
            {
                offsets[i] = position;
                position += sizes[i];
                if (position < 0 || position > length)
                    throw ArchiveSafety.Damaged(name, "the archive ends before its packed data");
            }
            id = r.Byte();
        }
        if (id == IdUnpackInfo)
        {
            folders = ReadUnpackInfo(r, name);
            var next = 0;
            foreach (var folder in folders)
            {
                folder.FirstPackStream = next;
                next += folder.PackedStreams.Length;
                if (next > sizes.Length)
                    throw ArchiveSafety.Damaged(name, "the folders need more packed streams than there are");
            }
            // The largest coder output of each folder, added up: a lying header is a bomb before anything is unpacked.
            long total = 0;
            foreach (var folder in folders)
                total = Math.Min(long.MaxValue / 2, total + folder.UnpackSizes.Max());
            ArchiveSafety.CheckRatio(total, length, name);
            id = r.Byte();
        }
        var listing = new SevenZipListing { PackOffsets = offsets, PackSizes = sizes, Folders = folders };
        if (id == IdSubStreamsInfo)
        {
            listing.SubStreams = ReadSubStreams(r, folders, name);
            id = r.Byte();
        }
        else
        {
            listing.SubStreams = [.. folders.Select(f => (f.UnpackSize, f.Crc))];
        }
        if (id != IdEnd)
            throw ArchiveSafety.Damaged(name, "the streams info has an unknown part");
        return listing;
    }

    private static SevenZipFolder[] ReadUnpackInfo(HeaderReader r, string name)
    {
        if (r.Byte() != IdFolder)
            throw ArchiveSafety.Damaged(name, "the coders info has no folders");
        var count = r.Count(r.Left / 2);
        if (r.Byte() != 0)
            throw new SevenZipUnsupportedException("folders kept outside the header");
        var folders = new SevenZipFolder[count];
        for (var i = 0; i < count; i++)
            folders[i] = ReadFolder(r, name);

        if (r.Byte() != IdCodersUnpackSize)
            throw ArchiveSafety.Damaged(name, "the coders info has no unpacked sizes");
        foreach (var folder in folders)
        {
            var outputs = new long[folder.Coders.Sum(c => c.OutStreams)];
            for (var i = 0; i < outputs.Length; i++)
                outputs[i] = r.Size();
            folder.UnpackSizes = outputs;
        }
        for (var id = r.Byte(); id != IdEnd; id = r.Byte())
        {
            if (id == IdCrc)
            {
                var crcs = r.Digests(count);
                for (var i = 0; i < count; i++)
                    folders[i].Crc = crcs[i];
            }
            else
            {
                r.Skip(r.Size());
            }
        }
        return folders;
    }

    private static SevenZipFolder ReadFolder(HeaderReader r, string name)
    {
        var coderCount = r.Count(MaxCoders);
        if (coderCount == 0)
            throw ArchiveSafety.Damaged(name, "a folder has no coders");
        var coders = new SevenZipCoder[coderCount];
        int totalIn = 0, totalOut = 0;
        for (var i = 0; i < coderCount; i++)
        {
            var flags = r.Byte();
            if ((flags & 0xC0) != 0)
                throw new SevenZipUnsupportedException("alternative coders");
            var idSize = flags & 0x0F;
            if (idSize > 8)
                throw new SevenZipUnsupportedException("a long method id");
            ulong method = 0;
            for (var k = 0; k < idSize; k++)
                method = method << 8 | r.Byte();
            int inputs = 1, outputs = 1;
            if ((flags & 0x10) != 0)
            {
                inputs = r.Count(MaxCoders);
                outputs = r.Count(MaxCoders);
            }
            var properties = (flags & 0x20) != 0 ? r.Bytes(r.Count(r.Left)) : [];
            coders[i] = new SevenZipCoder(method, inputs, outputs, properties);
            totalIn += inputs;
            totalOut += outputs;
            if (totalIn > MaxCoders || totalOut > MaxCoders)
                throw ArchiveSafety.Damaged(name, "a folder has too many coder streams");
        }
        if (totalOut == 0 || totalOut - 1 > totalIn)
            throw ArchiveSafety.Damaged(name, "a folder's coders don't fit together");

        var pairs = new (int In, int Out)[totalOut - 1];
        var boundIn = new bool[totalIn];
        var boundOut = new bool[totalOut];
        for (var i = 0; i < pairs.Length; i++)
        {
            var input = r.Count(totalIn - 1);
            var output = r.Count(totalOut - 1);
            if (boundIn[input] || boundOut[output])
                throw ArchiveSafety.Damaged(name, "a folder's coders don't fit together");
            boundIn[input] = boundOut[output] = true;
            pairs[i] = (input, output);
        }
        var packedCount = totalIn - pairs.Length;
        var packed = new int[packedCount];
        if (packedCount == 1)
        {
            packed[0] = Array.IndexOf(boundIn, false);
        }
        else
        {
            for (var i = 0; i < packedCount; i++)
            {
                var input = r.Count(totalIn - 1);
                if (boundIn[input])
                    throw ArchiveSafety.Damaged(name, "a folder's coders don't fit together");
                boundIn[input] = true;
                packed[i] = input;
            }
        }
        return new SevenZipFolder { Coders = coders, BindPairs = pairs, PackedStreams = packed, MainOut = Array.IndexOf(boundOut, false) };
    }

    private static List<(long Size, uint? Crc)> ReadSubStreams(HeaderReader r, SevenZipFolder[] folders, string name)
    {
        var id = r.Byte();
        long total = folders.Length;
        if (id == IdNumUnpackStream)
        {
            total = 0;
            foreach (var folder in folders)
            {
                folder.SubStreams = r.Count(ArchiveSafety.EntryLimit);
                total += folder.SubStreams;
                if (total > ArchiveSafety.EntryLimit)
                    throw ArchiveSafety.TooManyEntries(name);
            }
            id = r.Byte();
        }
        if (id != IdSize && folders.Any(f => f.SubStreams > 1))
            throw ArchiveSafety.Damaged(name, "the header gives no file sizes");

        var streams = new List<(long Size, uint? Crc)>((int)total);
        foreach (var folder in folders)
        {
            if (folder.SubStreams == 0)
                continue;
            long sum = 0;
            for (var i = 1; i < folder.SubStreams; i++)
            {
                var size = r.Size();
                sum += size;
                if (sum < 0 || sum > folder.UnpackSize)
                    throw ArchiveSafety.Damaged(name, "the files are larger than their folder");
                streams.Add((size, null));
            }
            streams.Add((folder.UnpackSize - sum, null));
        }
        if (id == IdSize)
            id = r.Byte();

        // CRCs come for every file but the single files of folders that have their own CRC.
        var digests = 0;
        foreach (var folder in folders)
            if (folder.SubStreams != 1 || folder.Crc is null)
                digests += folder.SubStreams;
        uint?[] read = [];
        for (; id != IdEnd; id = r.Byte())
        {
            if (id == IdCrc)
                read = r.Digests(digests);
            else
                r.Skip(r.Size());
        }
        int at = 0, next = 0;
        foreach (var folder in folders)
        {
            if (folder.SubStreams == 1 && folder.Crc is { } crc)
            {
                streams[at] = (streams[at].Size, crc);
                at++;
                continue;
            }
            for (var i = 0; i < folder.SubStreams; i++, at++, next++)
                streams[at] = (streams[at].Size, next < read.Length ? read[next] : null);
        }
        return streams;
    }

    private static SevenZipItem[] ReadFilesInfo(HeaderReader r, SevenZipListing streams, string name)
    {
        var count = r.Count(int.MaxValue);
        if (count > ArchiveSafety.EntryLimit)
            throw ArchiveSafety.TooManyEntries(name);
        var items = new SevenZipItem[count];
        for (var i = 0; i < count; i++)
            items[i] = new SevenZipItem();
        bool[]? emptyStream = null, emptyFile = null, anti = null;
        var emptyCount = 0;
        var names = false;
        for (var type = r.Byte(); type != IdEnd; type = r.Byte())
        {
            var size = r.Size();
            if (size > r.Left)
                throw ArchiveSafety.Damaged(name, "the files info is cut short");
            var p = new HeaderReader(r.Bytes((int)size), name);
            switch (type)
            {
                case IdEmptyStream:
                    emptyStream = p.Bits(count);
                    emptyCount = emptyStream.Count(b => b);
                    break;
                case IdEmptyFile:
                    emptyFile = p.Bits(emptyCount);
                    break;
                case IdAnti:
                    anti = p.Bits(emptyCount);
                    break;
                case IdName:
                    if (p.Byte() != 0)
                        throw new SevenZipUnsupportedException("names kept outside the header");
                    ReadNames(p, items, name);
                    names = true;
                    break;
                case IdMTime:
                    {
                        var defined = p.Defined(count);
                        if (p.Byte() != 0)
                            throw new SevenZipUnsupportedException("times kept outside the header");
                        for (var i = 0; i < count; i++)
                            if (defined[i])
                                items[i].Modified = FileTime(p.UInt64());
                        break;
                    }
                case IdWinAttributes:
                    {
                        var defined = p.Defined(count);
                        if (p.Byte() != 0)
                            throw new SevenZipUnsupportedException("attributes kept outside the header");
                        for (var i = 0; i < count; i++)
                            if (defined[i])
                                items[i].Attributes = p.UInt32();
                        break;
                    }
                default:
                    break; // creation and access times, start positions, padding
            }
        }
        if (!names && count > 0)
            throw ArchiveSafety.Damaged(name, "the entries have no names");

        var subStreams = streams.SubStreams;
        int empty = 0, stream = 0;
        foreach (var item in items.Select((item, i) => (item, i)))
        {
            if (emptyStream?[item.i] == true)
            {
                item.item.HasStream = false;
                // (The empty-file and anti bits may come before the empty-stream ones, sized for another count.)
                item.item.IsFolder = !(emptyFile is not null && empty < emptyFile.Length && emptyFile[empty]);
                item.item.IsAnti = anti is not null && empty < anti.Length && anti[empty];
                empty++;
                continue;
            }
            if (stream >= subStreams.Count)
                throw ArchiveSafety.Damaged(name, "the header lists more files with data than data streams");
            (item.item.Size, item.item.Crc) = subStreams[stream++];
        }
        return items;
    }

    private static void ReadNames(HeaderReader p, SevenZipItem[] items, string name)
    {
        var bytes = p.Rest();
        if (bytes.Length % 2 != 0)
            throw ArchiveSafety.Damaged(name, "the entry names are cut short");
        var at = 0;
        for (var i = 0; i < items.Length; i++)
        {
            var end = at;
            while (end + 1 < bytes.Length && (bytes[end] | bytes[end + 1]) != 0)
                end += 2;
            if (end + 1 >= bytes.Length)
                throw ArchiveSafety.Damaged(name, "the entry names are cut short");
            if ((end - at) / 2 > ArchiveSafety.MaxPathLength)
                throw ArchiveSafety.NameTooLong(name);
            items[i].Name = Encoding.Unicode.GetString(bytes[at..end]);
            at = end + 2;
        }
    }

    /// <summary>A FILETIME (100 ns since 1601, UTC); null when out of the range .NET can hold.</summary>
    private static DateTime? FileTime(ulong value) =>
        value is 0 or > 2_650_467_743_999_999_999 ? null : DateTime.FromFileTimeUtc((long)value);

    /// <summary>Hands out the data of each folder's files in order, decoding every folder once.</summary>
    private sealed class FolderSequence(Stream stream, SevenZipListing listing, string name, CancellationToken cancellationToken) : IDisposable
    {
        private int _folder = -1;
        private int _left;
        private Stream? _current;
        private CrcReadStream? _check;

        /// <summary>The next file's data: a window of <paramref name="size"/> bytes of its folder's output.</summary>
        public Stream Next(long size)
        {
            while (_left == 0)
            {
                FinishFolder();
                if (++_folder >= listing.Folders.Length)
                    throw ArchiveSafety.Damaged(name, "the header lists more files with data than data streams");
                _left = listing.Folders[_folder].SubStreams;
                if (_left > 0)
                    OpenFolder();
            }
            _left--;
            return new BoundedStream(_current!, size);
        }

        /// <summary>Checks the folder CRC of the last folder; call after the last file.</summary>
        public void Finish() => FinishFolder();

        private void OpenFolder()
        {
            var folder = listing.Folders[_folder];
            var data = Open(stream, listing, _folder, name, cancellationToken);
            // A folder of several files may carry a CRC of them all (with one file, it is that file's own).
            if (folder.Crc is not null && folder.SubStreams > 1)
                data = _check = new CrcReadStream(data);
            _current = data;
        }

        private void FinishFolder()
        {
            if (_check is not null && _check.Crc.Value != listing.Folders[_folder].Crc)
                throw ArchiveSafety.Damaged(name, "a folder checksum does not match");
            _check = null;
            _current?.Dispose();
            _current = null;
        }

        public void Dispose() => _current?.Dispose();

        /// <summary>The decoded output of folder <paramref name="index"/>: its coders joined as its bind pairs say.</summary>
        public static Stream Open(Stream stream, SevenZipListing listing, int index, string name, CancellationToken cancellationToken)
        {
            var folder = listing.Folders[index];
            var firstIn = new int[folder.Coders.Length];
            var firstOut = new int[folder.Coders.Length];
            for (var i = 1; i < folder.Coders.Length; i++)
            {
                firstIn[i] = firstIn[i - 1] + folder.Coders[i - 1].InStreams;
                firstOut[i] = firstOut[i - 1] + folder.Coders[i - 1].OutStreams;
            }
            var opened = new bool[folder.Coders.Length];
            return OpenCoder(Array.IndexOf(firstOut, folder.MainOut));

            Stream OpenCoder(int coder)
            {
                if (coder < 0 || opened[coder])
                    throw ArchiveSafety.Damaged(name, "a folder's coders don't fit together");
                opened[coder] = true;
                var c = folder.Coders[coder];
                var inputs = new Stream[c.InStreams];
                for (var j = 0; j < inputs.Length; j++)
                {
                    var input = firstIn[coder] + j;
                    var pair = Array.FindIndex(folder.BindPairs, p => p.In == input);
                    if (pair >= 0)
                    {
                        inputs[j] = OpenCoder(Array.IndexOf(firstOut, folder.BindPairs[pair].Out));
                        continue;
                    }
                    var pack = folder.FirstPackStream + Array.IndexOf(folder.PackedStreams, input);
                    inputs[j] = new SharedWindowStream(stream, listing.PackOffsets[pack], listing.PackSizes[pack]);
                }
                return SevenZipCoders.Create(c, inputs, folder.UnpackSizes[firstOut[coder]], name, cancellationToken);
            }
        }
    }

    /// <summary>Reads the numbers, bit vectors and digests of a 7z header; running out of bytes is damage.</summary>
    private sealed class HeaderReader(byte[] data, string name)
    {
        private int _at;

        public int Left => data.Length - _at;

        public byte Byte() => _at < data.Length ? data[_at++] : throw CutShort();

        public byte[] Bytes(int count)
        {
            if (count > Left)
                throw CutShort();
            var bytes = data.AsSpan(_at, count).ToArray();
            _at += count;
            return bytes;
        }

        public byte[] Rest() => Bytes(Left);

        public void Skip(long count)
        {
            if (count > Left)
                throw CutShort();
            _at += (int)count;
        }

        public uint UInt32()
        {
            if (Left < 4)
                throw CutShort();
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_at));
            _at += 4;
            return value;
        }

        public ulong UInt64()
        {
            if (Left < 8)
                throw CutShort();
            var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_at));
            _at += 8;
            return value;
        }

        /// <summary>A 7z number: the leading 1 bits of the first byte say how many little-endian bytes follow.</summary>
        public ulong Number()
        {
            var first = Byte();
            ulong value = 0;
            for (var i = 0; i < 8; i++)
            {
                var bit = 0x80 >> i;
                if ((first & bit) == 0)
                    return value | (ulong)(first & (bit - 1)) << (8 * i);
                value |= (ulong)Byte() << (8 * i);
            }
            return value;
        }

        /// <summary>A size or offset: a number that fits a long.</summary>
        public long Size()
        {
            var value = Number();
            return value <= long.MaxValue ? (long)value : throw ArchiveSafety.Damaged(name, "a size in the header is out of range");
        }

        /// <summary>A count or index up to <paramref name="max"/> (what the bytes left can hold, or a fixed cap).</summary>
        public int Count(long max)
        {
            var value = Number();
            return value <= (ulong)Math.Max(max, 0) && value <= int.MaxValue
                ? (int)value
                : throw ArchiveSafety.Damaged(name, "a count in the header is out of range");
        }

        /// <summary><paramref name="count"/> bits, most significant first.</summary>
        public bool[] Bits(int count)
        {
            if ((count + 7) / 8 > Left)
                throw CutShort();
            var bits = new bool[count];
            for (var i = 0; i < count; i++)
                bits[i] = (data[_at + i / 8] & (0x80 >> (i % 8))) != 0;
            _at += (count + 7) / 8;
            return bits;
        }

        /// <summary>The "all defined" byte, then a bit vector when it is 0.</summary>
        public bool[] Defined(int count)
        {
            if (Byte() != 0)
            {
                var all = new bool[count];
                Array.Fill(all, true);
                return all;
            }
            return Bits(count);
        }

        /// <summary>CRCs for <paramref name="count"/> items; null for those without one.</summary>
        public uint?[] Digests(int count)
        {
            var defined = Defined(count);
            var crcs = new uint?[count];
            for (var i = 0; i < count; i++)
                if (defined[i])
                    crcs[i] = UInt32();
            return crcs;
        }

        private Exception CutShort() => ArchiveSafety.Damaged(name, "the header is cut short");
    }
}
