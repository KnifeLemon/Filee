// Writes .xz files (The .xz File Format 1.2.1) with Filee's own LZMA2 encoder (Archives/Lzma): one stream with a
// CRC-64 check and one block per LZMA2 block (Lzma2Writer cuts the input into blocks of a few dictionaries and codes
// them on several threads). Each block header names the LZMA2 filter, its dictionary and both sizes, so readers can
// unpack the blocks side by side (as xz and 7-Zip do with several threads, and Filee's XzReader); then the block's
// data, padding to four bytes and the CRC-64 of what it unpacks to. The index lists every block's unpadded and
// unpacked size, then the footer. Nothing is written for an empty input but the stream header, an empty index and the
// footer, as xz does. It is a write-only stream, so a TAR can be compressed while it is written (.tar.xz) without a
// temporary file.

using System.Buffers.Binary;
using Filee.Core.Presets;
using Filee.Engines.Archives.Lzma;

namespace Filee.Engines.Archives;

/// <summary>A write-only stream that compresses into an .xz file.</summary>
internal sealed class XzWriter : Stream
{
    private const int CheckSize = 8;

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly Lzma2Writer _lzma2;
    private readonly byte _property;
    private readonly byte[] _flags = [0x00, XzFormat.CheckCrc64];
    private readonly List<(long Unpadded, long Unpacked)> _records = [];
    private readonly CancellationToken _cancellationToken;
    private long _unpacked;
    private bool _finished;

    /// <param name="expectedSize">
    /// The input size when known (-1 otherwise): small inputs get a dictionary no larger than they are, which saves
    /// memory for the writer and for whoever unpacks the file.
    /// </param>
    public XzWriter(Stream output, ArchiveLevel level, long expectedSize = -1, CancellationToken cancellationToken = default,
        bool leaveOpen = false)
    {
        _output = output;
        _leaveOpen = leaveOpen;
        var settings = Lzma2Settings.For(level, expectedSize);
        _property = XzFormat.PropertyFor((uint)settings.Dictionary);
        _cancellationToken = cancellationToken;
        _lzma2 = new Lzma2Writer(output, settings, expectedSize, cancellationToken, WriteBlock);

        Span<byte> header = stackalloc byte[12];
        XzFormat.HeaderMagic.CopyTo(header);
        _flags.CopyTo(header[6..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Crc32.Compute(_flags));
        _output.Write(header);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (buffer.IsEmpty)
            return;
        _cancellationToken.ThrowIfCancellationRequested();
        _unpacked += buffer.Length;
        _lzma2.Write(buffer);
    }

    /// <summary>One .xz block: header with both sizes, the LZMA2 data and its end byte, padding, CRC-64.</summary>
    private void WriteBlock(Lzma2Block block)
    {
        var packed = block.Chunks.Length + 1L;
        // Size / 4 − 1, flags (one filter, both sizes), the sizes, filter id 0x21, 1 property byte, the dictionary,
        // padding to four bytes, CRC-32.
        Span<byte> header = stackalloc byte[32];
        header.Clear();
        header[1] = 0xC0;
        var at = 2;
        at += XzFormat.WriteVarint(header[at..], (ulong)packed);
        at += XzFormat.WriteVarint(header[at..], (ulong)block.Unpacked);
        header[at++] = (byte)XzFormat.Lzma2FilterId;
        header[at++] = 1;
        header[at++] = _property;
        at = (at + 3) & ~3;
        header[0] = (byte)(at / 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[at..], Crc32.Compute(header[..at]));
        at += 4;
        _output.Write(header[..at]);
        _output.Write(block.Chunks);

        Span<byte> tail = stackalloc byte[1 + 3 + CheckSize];
        tail.Clear();
        var padding = (int)((4 - packed % 4) % 4);
        BinaryPrimitives.WriteUInt64LittleEndian(tail[(1 + padding)..], block.Check);
        _output.Write(tail[..(1 + padding + CheckSize)]);
        _records.Add((at + packed + CheckSize, block.Unpacked));
    }

    /// <summary>Compresses what is left and writes the last blocks, the index and the footer.</summary>
    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _lzma2.Finish();

        // Index: indicator 0, record count, (unpadded, unpacked) per block, padding to four bytes, CRC-32.
        var index = new byte[1 + 9 + _records.Count * 18 + 3 + 4];
        var at = 1;
        at += XzFormat.WriteVarint(index.AsSpan(at), (ulong)_records.Count);
        foreach (var (unpadded, unpacked) in _records)
        {
            at += XzFormat.WriteVarint(index.AsSpan(at), (ulong)unpadded);
            at += XzFormat.WriteVarint(index.AsSpan(at), (ulong)unpacked);
        }
        while (at % 4 != 0)
            index[at++] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(at), Crc32.Compute(index.AsSpan(0, at)));
        at += 4;
        _output.Write(index, 0, at);

        // Footer: CRC-32 of the next six bytes, backward size (index size / 4 − 1), the stream flags, "YZ".
        Span<byte> footer = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], (uint)(at / 4 - 1));
        _flags.CopyTo(footer[8..]);
        XzFormat.FooterMagic.CopyTo(footer[10..]);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, Crc32.Compute(footer[4..10]));
        _output.Write(footer);
        _output.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        // A writer disposed while an exception unwinds must not finish a half-written file as if it were whole, so
        // callers call Finish themselves; Dispose only closes.
        if (disposing && !_leaveOpen)
            _output.Dispose();
        _finished = true;
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_finished;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _unpacked; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
