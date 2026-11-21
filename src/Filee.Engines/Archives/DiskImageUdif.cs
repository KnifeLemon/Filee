// The disk inside a DMG (UDIF), read in-process from the runs UdifCheck has checked: raw runs straight from the file,
// zero-fill and ignore runs (and sectors no block table covers) as zeros, compressed runs decoded a chunk at a time
// (ADC, zlib, bzip2 with SharpCompress for the randomised blocks of old encoders, LZFSE, and LZMA, which Apple's
// tools write as an .xz stream; bzip2 and .xz chunks are read to their end so their checksums count). Decoded chunks are kept, up to
// 64 MB, so that a file system's structures read here and there don't decode the same chunk over and over.
//
// Each chunk unpacks to at most its block table's buffer (64 MB at most, checked before), a damaged chunk ends in
// Filee's "damaged" message, and every chunk checks for cancellation before it is decoded.

using System.IO.Compression;
using Filee.Engines.Archives.Lzma;
using SharpCompress.Compressors.BZip2;
using SharpCompressMode = SharpCompress.Compressors.CompressionMode;

namespace Filee.Engines.Archives;

/// <summary>One run of a DMG's block tables: <see cref="Sectors"/> disk sectors from <see cref="Sector"/>, stored as <see cref="Type"/>.</summary>
internal readonly record struct UdifRun(long Sector, long Sectors, uint Type, long Offset, long Packed);

/// <summary>A read-only stream of the disk a DMG holds.</summary>
internal sealed class UdifDisk : Stream
{
    public const uint Raw = 1, Adc = 0x80000004, Zlib = 0x80000005, Bzip2 = 0x80000006, Lzfse = 0x80000007, Lzma = 0x80000008;

    private const long CacheBytes = 64L * 1024 * 1024;

    private readonly Stream _file;
    private readonly UdifRun[] _runs;
    private readonly string _name;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<int, LinkedListNode<(int Run, byte[] Data)>> _cache = [];
    private readonly LinkedList<(int Run, byte[] Data)> _recent = new();
    private long _cached;
    private LzfseDecoder? _lzfse;
    private long _position;

    /// <param name="runs">The runs holding data, sorted by sector, none overlapping.</param>
    public UdifDisk(Stream file, UdifRun[] runs, long sectors, string name, CancellationToken cancellationToken)
    {
        _file = file;
        _runs = runs;
        _name = name;
        _cancellationToken = cancellationToken;
        Length = sectors * 512;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.IsEmpty)
            return 0;
        var wanted = (int)Math.Min(buffer.Length, Length - _position);
        var index = Find(_position);
        int count;
        if (index < 0 || _position >= End(_runs[index]))
        {
            // Not covered by a run: zeros up to the next one.
            var next = index + 1 < _runs.Length ? _runs[index + 1].Sector * 512 : Length;
            count = (int)Math.Min(wanted, next - _position);
            buffer[..count].Clear();
        }
        else
        {
            var run = _runs[index];
            var within = _position - run.Sector * 512;
            count = (int)Math.Min(wanted, End(run) - _position);
            if (run.Type == Raw)
            {
                _file.Position = run.Offset + within;
                if (_file.ReadAtLeast(buffer[..count], count, throwOnEndOfStream: false) < count)
                    throw ArchiveSafety.Damaged(_name, "a run lies beyond the end of the file");
            }
            else
            {
                Chunk(index).AsSpan((int)within, count).CopyTo(buffer);
            }
        }
        _position += count;
        return count;
    }

    private static long End(UdifRun run) => (run.Sector + run.Sectors) * 512;

    /// <summary>The last run starting at or before <paramref name="position"/>, or −1.</summary>
    private int Find(long position)
    {
        var sector = position / 512;
        var (low, high) = (0, _runs.Length - 1);
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (_runs[middle].Sector <= sector)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return high;
    }

    /// <summary>The decoded data of compressed run <paramref name="index"/>.</summary>
    private byte[] Chunk(int index)
    {
        if (_cache.TryGetValue(index, out var node))
        {
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Data;
        }
        _cancellationToken.ThrowIfCancellationRequested();
        var run = _runs[index];
        var data = new byte[run.Sectors * 512];
        try
        {
            Decode(run, data);
        }
        catch (Exception ex) when (DiskImageReader.Translate(ex, _name) is { } translated)
        {
            throw translated;
        }
        while (_cached + data.Length > CacheBytes && _recent.Last is { } oldest)
        {
            _recent.RemoveLast();
            _cache.Remove(oldest.Value.Run);
            _cached -= oldest.Value.Data.Length;
        }
        _cache[index] = _recent.AddFirst((index, data));
        _cached += data.Length;
        return data;
    }

    private void Decode(UdifRun run, byte[] data)
    {
        _file.Position = run.Offset;
        var packed = new BoundedStream(_file, run.Packed);
        switch (run.Type)
        {
            case Adc:
                AdcDecode(ReadAll(packed, run.Packed), data);
                break;
            case Zlib:
                using (var zlib = new ZLibStream(packed, CompressionMode.Decompress, leaveOpen: true))
                    zlib.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
                break;
            case Bzip2:
                // SharpCompress reads the randomised blocks of old encoders, which Filee's decoder leaves out.
                using (var bzip2 = Bzip2Decoder.OpenRead(packed, _cancellationToken, () =>
                {
                    _file.Position = run.Offset;
                    return new BZip2Stream(new BoundedStream(_file, run.Packed), SharpCompressMode.Decompress, decompressConcatenated: true);
                }))
                    ReadWhole(bzip2, data);
                break;
            case Lzfse:
                (_lzfse ??= new LzfseDecoder()).Decode(ReadAll(packed, run.Packed), data);
                break;
            case Lzma:
                using (var xz = new XzReader(packed, _name, cancellationToken: _cancellationToken))
                    ReadWhole(xz, data);
                break;
            default:
                throw new NotSupportedException($"DMG runs of type 0x{run.Type:X8}");
        }
    }

    /// <summary>Fills <paramref name="data"/> and reads the stream to its end, so its checksums are compared.</summary>
    private void ReadWhole(Stream decoded, byte[] data)
    {
        decoded.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
        Span<byte> more = stackalloc byte[1];
        if (decoded.Read(more) > 0)
            throw ArchiveSafety.Damaged(_name, "a chunk unpacks to more than its run holds");
    }

    private static byte[] ReadAll(Stream packed, long length)
    {
        var bytes = new byte[length];
        packed.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// Apple Data Compression: 1LLLLLLL copies L + 1 literal bytes; 01LLLLLL and a 16-bit big-endian D copies L + 4
    /// bytes from D + 1 back; 0LLLLDDD with 8 more bits of D copies L + 3 bytes from D + 1 back. Output past the
    /// chunk is cut off.
    /// </summary>
    internal static void AdcDecode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var (p, at) = (0, 0);
        while (p < source.Length && at < destination.Length)
        {
            var op = source[p++];
            if ((op & 0x80) != 0)
            {
                var length = (op & 0x7F) + 1;
                if (length > source.Length - p)
                    throw new InvalidDataException("damaged ADC data: literals run past the end of the chunk");
                length = Math.Min(length, destination.Length - at);
                source.Slice(p, length).CopyTo(destination[at..]);
                p += length;
                at += length;
                continue;
            }
            int count, distance;
            if ((op & 0x40) != 0)
            {
                if (source.Length - p < 2)
                    throw new InvalidDataException("damaged ADC data: a match is cut short");
                count = (op & 0x3F) + 4;
                distance = (source[p] << 8 | source[p + 1]) + 1;
                p += 2;
            }
            else
            {
                if (p >= source.Length)
                    throw new InvalidDataException("damaged ADC data: a match is cut short");
                count = ((op >> 2) & 0x0F) + 3;
                distance = ((op & 3) << 8 | source[p]) + 1;
                p++;
            }
            if (distance > at)
                throw new InvalidDataException("damaged ADC data: a match reaches back before the start");
            count = Math.Min(count, destination.Length - at);
            for (var i = 0; i < count; i++, at++)
                destination[at] = destination[at - distance];
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
