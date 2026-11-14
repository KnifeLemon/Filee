// Writes .xz files (The .xz File Format 1.2.1) with Filee's own LZMA2 encoder (Archives/Lzma): one stream with a
// CRC-64 check, one block whose header names the LZMA2 filter and its dictionary (no sizes, since the data is
// written as it comes), the index with that block's unpadded and unpacked size, and the footer. Nothing is written
// for an empty input but the stream header, an empty index and the footer, as xz does. It is a write-only stream, so a
// TAR can be compressed while it is written (.tar.xz) without a temporary file.

using System.Buffers.Binary;
using Filee.Core.Presets;
using Filee.Engines.Archives.Lzma;

namespace Filee.Engines.Archives;

/// <summary>A write-only stream that compresses into an .xz file.</summary>
internal sealed class XzWriter : Stream
{
    private const int BlockHeaderSize = 12;
    private const int CheckSize = 8;

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly Lzma2Settings _settings;
    private readonly long _expectedSize;
    private readonly CancellationToken _cancellationToken;
    private readonly Crc64 _check = new();
    private readonly byte[] _flags = [0x00, XzFormat.CheckCrc64];
    private Lzma2Writer? _block;
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
        _settings = Lzma2Settings.For(level, expectedSize);
        _expectedSize = expectedSize;
        _cancellationToken = cancellationToken;

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
        if (_block is null)
            StartBlock();
        _check.Append(buffer);
        _unpacked += buffer.Length;
        _block!.Write(buffer);
    }

    private void StartBlock()
    {
        // Size / 4 − 1, flags (one filter, no sizes), filter id 0x21, 1 property byte, the dictionary, padding, CRC-32.
        Span<byte> header = stackalloc byte[BlockHeaderSize];
        header.Clear();
        header[0] = BlockHeaderSize / 4 - 1;
        header[1] = 0x00;
        header[2] = (byte)XzFormat.Lzma2FilterId;
        header[3] = 1;
        header[4] = XzFormat.PropertyFor((uint)_settings.Dictionary);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Crc32.Compute(header[..8]));
        _output.Write(header);
        _block = new Lzma2Writer(_output, _settings, _expectedSize, _cancellationToken);
    }

    /// <summary>Compresses what is left and writes the block's end, the index and the footer.</summary>
    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;

        var records = new List<(long Unpadded, long Unpacked)>();
        if (_block is not null)
        {
            _block.Finish();
            var packed = _block.Written;
            Span<byte> tail = stackalloc byte[3 + CheckSize];
            tail.Clear();
            var padding = (int)((4 - packed % 4) % 4);
            BinaryPrimitives.WriteUInt64LittleEndian(tail[padding..], _check.Value);
            _output.Write(tail[..(padding + CheckSize)]);
            records.Add((BlockHeaderSize + packed + CheckSize, _unpacked));
        }

        // Index: indicator 0, record count, (unpadded, unpacked) per block, padding to four bytes, CRC-32.
        var index = new byte[1 + 9 + records.Count * 18 + 3 + 4];
        var at = 1;
        at += XzFormat.WriteVarint(index.AsSpan(at), (ulong)records.Count);
        foreach (var (unpadded, unpacked) in records)
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
