// Wisenet WAVE / Nx Witness exports (.nov). Network Optix's client (nx_open, MPL-2.0: vms/libs/nx_vms_common/src/nx/core/
// layout/layout_file_info.h) writes a layout file as a series of named streams, each a NUL-terminated UTF-8 name followed
// by its data, then an index of where each stream starts:
//   version 2: | name | data | ... | Header | CryptoInfo (if protected) | StreamIndexEntry ... | Footer (20 bytes) |
//   version 1: | StreamIndexV1 (magic, version, count, 256 entries) | CryptoInfo (if protected) | name | data | ... |
// Every camera's recording is a Matroska stream named "<camera id>_<start ms>.mkv"; the others hold the layout, its time
// range and the watermark. FFmpeg reads the recording where it lies through the subfile protocol, so nothing is copied.
// A password-protected export is encrypted (AES-256 with a key from the password): it is refused.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Filee.Core.Conversion;

namespace Filee.Engines.Media;

internal static class NovExport
{
    private const ulong FooterMagic = 0xdea5489c3f44ca16;
    private const ulong IndexMagicV1 = 0xfed8260da9eebc04;
    private const ulong CryptedIndexMagicV1 = 0xfed8260da9eebc03;
    private const int FooterSize = 20;
    private const int HeaderSize = 8;
    private const int EntrySize = 16;
    private const int CryptoInfoSize = 256;
    private const int MaxStreamsV1 = 256;
    private const int IndexSizeV1 = 16 + MaxStreamsV1 * EntrySize;

    /// <summary>More streams than any export holds (a few per camera); a larger count is a damaged index.</summary>
    internal const int MaxStreams = 65_536;

    /// <summary>Longest stream name read; the client itself reads names into a 1024-byte buffer.</summary>
    private const int MaxNameBytes = 1024;

    /// <summary>A stream of the export: its name and where its data lies in the file.</summary>
    internal sealed record Stream(string Name, long Start, long End)
    {
        public long Length => End - Start;
    }

    /// <summary>The streams of an export, and whether it is password-protected (its data encrypted).</summary>
    internal sealed record Index(bool Encrypted, IReadOnlyList<Stream> Streams);

    /// <summary>
    /// The input to give FFmpeg for the export at <paramref name="path"/>: its largest camera recording, through the
    /// subfile protocol. Refuses a password-protected export and one without a recording; a damaged index throws
    /// <see cref="InvalidDataException"/>. When the export holds other recordings (other cameras, or pieces of one with
    /// gaps), <paramref name="notice"/> hears that they are left out.
    /// </summary>
    public static string InputFor(string path, Action<string>? notice = null)
    {
        Index index;
        using (var file = File.OpenRead(path))
            index = Read(file);
        if (index.Encrypted)
            throw new ConversionRefusedException("error.nov_password");
        var recordings = index.Streams
            .Where(s => s.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) && s.Length > 0)
            .ToList();
        var recording = recordings.MaxBy(s => s.Length) ?? throw new ConversionRefusedException("error.nov_no_video");
        if (recordings.Count > 1)
            notice?.Invoke("notice.nov_recordings_left_out");
        return string.Create(CultureInfo.InvariantCulture,
            $"subfile,,start,{recording.Start},end,{recording.End},,:{path}");
    }

    /// <summary>Reads the index of an export (version 2, or the older version 1).</summary>
    public static Index Read(System.IO.Stream file)
    {
        var length = file.Length;
        if (length >= FooterSize)
        {
            Span<byte> footer = stackalloc byte[FooterSize];
            ReadAt(file, length - FooterSize, footer);
            if (BinaryPrimitives.ReadUInt64LittleEndian(footer) == FooterMagic)
                return ReadVersion2(file, footer);
        }
        if (length >= 8)
        {
            Span<byte> magic = stackalloc byte[8];
            ReadAt(file, 0, magic);
            if (BinaryPrimitives.ReadUInt64LittleEndian(magic) is IndexMagicV1 or CryptedIndexMagicV1)
                return ReadVersion1(file);
        }
        throw new InvalidDataException("This is not a Wisenet WAVE / Nx Witness export: its index is missing.");
    }

    private static Index ReadVersion2(System.IO.Stream file, ReadOnlySpan<byte> footer)
    {
        var length = file.Length;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
        if (version != 2)
            throw new InvalidDataException($"This export's index is version {version}, which Filee can't read.");
        var headerAt = BinaryPrimitives.ReadInt64LittleEndian(footer[12..]);
        if (headerAt < 0 || headerAt > length - FooterSize - HeaderSize)
            throw Damaged();
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadAt(file, headerAt, header);
        var encrypted = BinaryPrimitives.ReadUInt32LittleEndian(header) != 0;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var entriesAt = headerAt + HeaderSize + (encrypted ? CryptoInfoSize : 0);
        // The entries must fill the room between the header and the footer, before anything is allocated for them.
        if (count > MaxStreams || entriesAt + (long)count * EntrySize != length - FooterSize)
            throw Damaged();
        // A protected export's streams are encrypted: there is nothing to read without the password.
        if (encrypted)
            return new Index(true, []);
        return new Index(false, Streams(file, ReadOffsets(file, entriesAt, (int)count), headerAt));
    }

    private static Index ReadVersion1(System.IO.Stream file)
    {
        var length = file.Length;
        if (length < IndexSizeV1)
            throw Damaged();
        Span<byte> head = stackalloc byte[16];
        ReadAt(file, 0, head);
        var encrypted = BinaryPrimitives.ReadUInt64LittleEndian(head) == CryptedIndexMagicV1;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        if (version != 1)
            throw new InvalidDataException($"This export's index is version {version}, which Filee can't read.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        if (count > MaxStreamsV1)
            throw Damaged();
        if (encrypted)
            return new Index(true, []);
        return new Index(false, Streams(file, ReadOffsets(file, 16, (int)count), length));
    }

    private static long[] ReadOffsets(System.IO.Stream file, long at, int count)
    {
        var entries = new byte[count * EntrySize];
        ReadAt(file, at, entries);
        var offsets = new long[count];
        for (var i = 0; i < count; i++)
            offsets[i] = BinaryPrimitives.ReadInt64LittleEndian(entries.AsSpan(i * EntrySize));
        return offsets;
    }

    /// <summary>
    /// The streams starting at <paramref name="offsets"/>, each running to the next one (the last to
    /// <paramref name="dataEnd"/>), as the client reads them.
    /// </summary>
    private static List<Stream> Streams(System.IO.Stream file, long[] offsets, long dataEnd)
    {
        var streams = new List<Stream>(offsets.Length);
        var name = new byte[MaxNameBytes];
        for (var i = 0; i < offsets.Length; i++)
        {
            var start = offsets[i];
            var end = i + 1 < offsets.Length ? offsets[i + 1] : dataEnd;
            if (start < 0 || end < start || end > dataEnd)
                throw Damaged();
            var read = ReadAt(file, start, name.AsSpan(0, (int)Math.Min(MaxNameBytes, end - start)), exactly: false);
            var nul = Array.IndexOf(name, (byte)0, 0, read);
            if (nul < 0)
                throw Damaged();
            streams.Add(new Stream(Encoding.UTF8.GetString(name, 0, nul), start + nul + 1, end));
        }
        return streams;
    }

    private static int ReadAt(System.IO.Stream file, long at, Span<byte> buffer, bool exactly = true)
    {
        file.Position = at;
        var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (exactly && read != buffer.Length)
            throw Damaged();
        return read;
    }

    private static InvalidDataException Damaged() =>
        new("This Wisenet WAVE / Nx Witness export is damaged: its index doesn't match the file.");
}
