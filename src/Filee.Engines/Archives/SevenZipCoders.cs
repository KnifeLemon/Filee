// The decoders of a 7z folder (Archives/SevenZipReader): each coder is a read-only stream over its inputs (a packed
// stream of the archive or the output of another coder), and every coder's output is held to the unpacked size the
// header names. Filee's own LZMA and LZMA2 decoders (Archives/Lzma), the Copy coder, the x86 branch converters BCJ and
// BCJ2 and the Delta filter are written here; Deflate comes from .NET and BZip2 from SharpCompress (MIT), as for the
// .bz2 files Filee reads. Other methods (PPMd, ARM64 …) are reported as unsupported before anything is unpacked.

using System.Buffers.Binary;
using System.IO.Compression;
using Filee.Engines.Archives.Lzma;
using SharpCompress.Compressors.BZip2;
using SharpCompressMode = SharpCompress.Compressors.CompressionMode;

namespace Filee.Engines.Archives;

/// <summary>Method ids of 7z coders (7zFormat.txt, Methods.txt) and the decoders Filee has for them.</summary>
internal static class SevenZipCoders
{
    public const ulong Copy = 0x00;
    public const ulong Delta = 0x03;
    public const ulong Lzma2 = 0x21;
    public const ulong Lzma = 0x030101;
    public const ulong Bcj = 0x03030103;
    public const ulong Bcj2 = 0x0303011B;
    public const ulong Deflate = 0x040108;
    public const ulong Bzip2 = 0x040202;
    public const ulong Aes = 0x06F10701;

    /// <summary>The packed-side streams a method Filee decodes takes; null for methods it does not decode.</summary>
    public static int? InputsOf(ulong method) => method switch
    {
        Copy or Delta or Lzma2 or Lzma or Bcj or Deflate or Bzip2 => 1,
        Bcj2 => 4,
        _ => null,
    };

    /// <summary>A readable name for a method, for messages.</summary>
    public static string NameOf(ulong method) => method switch
    {
        Copy => "Copy",
        Delta => "Delta",
        Lzma2 => "LZMA2",
        Lzma => "LZMA",
        Bcj => "BCJ",
        Bcj2 => "BCJ2",
        Deflate => "Deflate",
        Bzip2 => "BZip2",
        Aes => "AES",
        0x030401 => "PPMd",
        0x040109 => "Deflate64",
        0x0A => "ARM64",
        0x0B => "RISCV",
        0x03030205 => "PPC",
        0x03030401 => "IA64",
        0x03030501 => "ARM",
        0x03030701 => "ARMT",
        0x03030805 => "SPARC",
        _ => $"0x{method:X}",
    };

    /// <summary>
    /// The decoder for <paramref name="coder"/> reading <paramref name="inputs"/>, giving exactly
    /// <paramref name="size"/> bytes (more or fewer is damage). Properties are checked here.
    /// </summary>
    public static Stream Create(SevenZipCoder coder, Stream[] inputs, long size, string name, CancellationToken cancellationToken)
    {
        var props = coder.Properties;
        Stream decoder;
        switch (coder.Method)
        {
            case Copy:
                decoder = inputs[0];
                break;
            case Lzma:
                {
                    if (props.Length < 5 || LzmaProperties.FromByte(props[0]) is not { } p)
                        throw ArchiveSafety.Damaged(name, "the LZMA properties are out of range");
                    var dictionary = BitConverter.ToUInt32(props, 1);
                    CheckDictionary(dictionary, name);
                    decoder = new LzmaRawReader(inputs[0], p, dictionary, size);
                    break;
                }
            case Lzma2:
                {
                    if (props.Length < 1 || XzFormat.DictionaryFromProperty(props[0]) is not { } dictionary)
                        throw ArchiveSafety.Damaged(name, "the LZMA2 dictionary size is out of range");
                    CheckDictionary(dictionary, name);
                    decoder = new Lzma2Stream(new Lzma2Reader(new ByteInput(inputs[0]), dictionary, size, cancellationToken));
                    break;
                }
            case Bcj:
                decoder = new BcjStream(inputs[0], props.Length >= 4 ? BitConverter.ToUInt32(props, 0) : 0);
                break;
            case Bcj2:
                decoder = new Bcj2Stream(inputs[0], inputs[1], inputs[2], inputs[3], size);
                break;
            case Delta:
                decoder = new DeltaStream(inputs[0], props.Length >= 1 ? props[0] + 1 : 1);
                break;
            case Deflate:
                decoder = new DeflateStream(inputs[0], CompressionMode.Decompress, leaveOpen: true);
                break;
            case Bzip2:
                decoder = new BZip2Stream(new NonClosingStream(inputs[0]), SharpCompressMode.Decompress, decompressConcatenated: false);
                break;
            default:
                throw new SevenZipUnsupportedException(NameOf(coder.Method));
        }
        return new SizedStream(decoder, size, name);
    }

    private static void CheckDictionary(uint dictionary, string name)
    {
        if (dictionary > LzmaDecoder.MaxDictionary)
            throw ArchiveSafety.Oversized(name,
                $"it needs a {ArchiveSafety.FormatSize(dictionary)} dictionary, more than the {ArchiveSafety.FormatSize(LzmaDecoder.MaxDictionary)} Filee allows");
    }
}

/// <summary>A 7z method Filee has no decoder for: the archive goes to another reader before anything is written.</summary>
internal sealed class SevenZipUnsupportedException(string method)
    : NotSupportedException($"the 7z method {method}")
{
    public string Method { get; } = method;
}

/// <summary>Base for the read-only decoder streams below.</summary>
internal abstract class ReadOnlyDecoderStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public abstract override int Read(Span<byte> buffer);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Exactly <c>size</c> bytes of a decoder: reading stops there, and a decoder that ends sooner is damage. Disposing
/// it disposes the decoder.
/// </summary>
internal sealed class SizedStream(Stream inner, long size, string name) : ReadOnlyDecoderStream
{
    private long _left = size;

    public override int Read(Span<byte> buffer)
    {
        if (_left == 0 || buffer.IsEmpty)
            return 0;
        if (buffer.Length > _left)
            buffer = buffer[..(int)_left];
        var read = inner.Read(buffer);
        if (read <= 0)
            throw ArchiveSafety.Damaged(name, "a 7z stream unpacks to less than its header says");
        _left -= read;
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A window of a stream others read too (the packed streams of one 7z folder side by side): every read seeks to
/// where this window stopped. Disposing it leaves the stream open.
/// </summary>
internal sealed class SharedWindowStream(Stream shared, long offset, long length) : ReadOnlyDecoderStream
{
    private long _position = offset;
    private readonly long _end = offset + length;

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _end || buffer.IsEmpty)
            return 0;
        if (buffer.Length > _end - _position)
            buffer = buffer[..(int)(_end - _position)];
        if (shared.Position != _position)
            shared.Position = _position;
        var read = shared.Read(buffer);
        if (read <= 0)
            return 0;
        _position += read;
        return read;
    }
}

/// <summary>LZMA2 as a stream: 0 only after the end byte.</summary>
internal sealed class Lzma2Stream(Lzma2Reader reader) : ReadOnlyDecoderStream
{
    public override int Read(Span<byte> buffer) => buffer.IsEmpty ? 0 : reader.Read(buffer);
}

/// <summary>Reads through a CRC-32, for the folder checksum a 7z header may give besides the files' own.</summary>
internal sealed class CrcReadStream(Stream inner) : ReadOnlyDecoderStream
{
    public Crc32 Crc { get; } = new();

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        if (read > 0)
            Crc.Append(buffer[..read]);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The Delta filter: each byte was stored as its difference to the byte <c>distance</c> (1 … 256) places before it.
/// </summary>
internal sealed class DeltaStream(Stream inner, int distance) : ReadOnlyDecoderStream
{
    private readonly byte[] _history = new byte[256];
    private int _position; // index of the next byte in _history (a ring)

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        for (var i = 0; i < read; i++)
        {
            var value = (byte)(buffer[i] + _history[(_position - distance) & 0xFF]);
            buffer[i] = value;
            _history[_position] = value;
            _position = (_position + 1) & 0xFF;
        }
        return read;
    }
}

/// <summary>
/// The x86 branch converter (BCJ): the encoder turned the relative targets of CALL (E8) and JMP (E9) into absolute
/// ones so repeated calls to one function look alike; this turns them back. A target only counts when its top byte is
/// 0x00 or 0xFF, and the opcodes seen in the four bytes before decide whether this E8/E9 is an opcode at all (the
/// same rule the encoder used). Up to four bytes at the end of each read wait for the next one.
/// </summary>
internal sealed class BcjStream(Stream inner, uint start) : ReadOnlyDecoderStream
{
    /// <summary>Whether a conversion is allowed, by the mask of E8/E9 bytes among the three before.</summary>
    private static readonly bool[] Allowed = [true, true, true, false, true, false, false, false];

    /// <summary>Which byte of the target the mask says to look at (counted from the top).</summary>
    private static readonly int[] ByteIndex = [0, 1, 2, 2, 3, 3, 3, 3];

    private readonly byte[] _buffer = new byte[1 << 16];
    private int _converted; // bytes at the front of _buffer ready to hand out
    private int _filled;    // bytes in _buffer
    private uint _ip = start;
    private uint _mask;
    private bool _ended;

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        while (_converted == 0)
        {
            if (_ended)
            {
                // The last few bytes are never converted: no instruction fits in them.
                _converted = _filled;
                if (_converted == 0)
                    return 0;
                break;
            }
            var read = inner.Read(_buffer.AsSpan(_filled));
            if (read <= 0)
                _ended = true;
            else
                _filled += read;
            if (!_ended)
                _converted = Convert(_buffer.AsSpan(0, _filled));
        }
        var count = Math.Min(buffer.Length, _converted);
        _buffer.AsSpan(0, count).CopyTo(buffer);
        _buffer.AsSpan(count, _filled - count).CopyTo(_buffer);
        _filled -= count;
        _converted -= count;
        return count;
    }

    /// <summary>Converts what it can of <paramref name="data"/> (starting at <see cref="_ip"/>); returns how many bytes are done.</summary>
    private int Convert(Span<byte> data)
    {
        if (data.Length < 5)
            return 0;
        var mask = _mask;
        var at = 0;
        var lastOpcode = -1; // position of the E8/E9 met before, relative to data; -1 means "just before data"
        var limit = data.Length - 4;
        while (true)
        {
            while (at < limit && (data[at] & 0xFE) != 0xE8)
                at++;
            if (at >= limit)
                break;
            var gap = at - lastOpcode;
            if (gap > 3)
            {
                mask = 0;
            }
            else
            {
                mask = (mask << (gap - 1)) & 7;
                if (mask != 0 && (!Allowed[mask] || IsTopByte(data[at + 4 - ByteIndex[mask]])))
                {
                    lastOpcode = at;
                    mask = ((mask << 1) & 7) | 1;
                    at++;
                    continue;
                }
            }
            lastOpcode = at;
            if (!IsTopByte(data[at + 4]))
            {
                mask = ((mask << 1) & 7) | 1;
                at++;
                continue;
            }
            var target = (uint)(data[at + 1] | data[at + 2] << 8 | data[at + 3] << 16 | data[at + 4] << 24);
            uint relative;
            while (true)
            {
                relative = target - (_ip + 5 + (uint)at);
                if (mask == 0)
                    break;
                var shift = ByteIndex[mask] * 8;
                if (!IsTopByte((byte)(relative >> (24 - shift))))
                    break;
                target = relative ^ ((1u << (32 - shift)) - 1);
            }
            data[at + 4] = (byte)~(((relative >> 24) & 1) - 1);
            data[at + 3] = (byte)(relative >> 16);
            data[at + 2] = (byte)(relative >> 8);
            data[at + 1] = (byte)relative;
            at += 5;
        }
        // The mask as the next call sees it: shifted by the distance from the last opcode to where this call stops.
        var tail = at - lastOpcode;
        _mask = tail > 3 ? 0 : (mask << (tail - 1)) & 7;
        _ip += (uint)at;
        return at;
    }

    private static bool IsTopByte(byte b) => b is 0 or 0xFF;
}

/// <summary>
/// The x86 BCJ2 converter: the main stream holds the code with every converted CALL (E8) and JMP (E9 or 0F 8x) target
/// taken out; the targets, absolute and big-endian, come from a call and a jump stream, and a range-coded stream says
/// for each such opcode whether its target was taken out (with one probability per byte before an E8, one for E9 and
/// one for the 0F 8x jumps).
/// </summary>
internal sealed class Bcj2Stream : ReadOnlyDecoderStream
{
    private readonly ByteInput _main;
    private readonly ByteInput _call;
    private readonly ByteInput _jump;
    private readonly RangeDecoder _rc = new();
    private readonly ushort[] _probabilities = new ushort[256 + 2];
    private readonly long _size;
    private readonly byte[] _pending = new byte[4];
    private readonly byte[] _target = new byte[4];
    private int _pendingAt = 4;
    private long _done;
    private byte _previous;

    public Bcj2Stream(Stream main, Stream call, Stream jump, Stream rc, long size)
    {
        _main = new ByteInput(main);
        _call = new ByteInput(call);
        _jump = new ByteInput(jump);
        _size = size;
        RangeCoder.Reset(_probabilities);
        _rc.Start(rc, new byte[1 << 12]);
    }

    public override int Read(Span<byte> buffer)
    {
        var n = 0;
        while (n < buffer.Length && _done < _size)
        {
            if (_pendingAt < 4)
            {
                buffer[n++] = _pending[_pendingAt++];
                _done++;
                continue;
            }
            var b = _main.ReadByte();
            if (b < 0)
                throw new InvalidDataException("the BCJ2 main stream ends too early");
            buffer[n++] = (byte)b;
            _done++;
            if (!IsJump(_previous, (byte)b) || _done == _size)
            {
                _previous = (byte)b;
                continue;
            }
            var index = b == 0xE8 ? _previous : b == 0xE9 ? 256 : 257;
            if (_rc.DecodeBit(ref _probabilities[index]) == 0)
            {
                _previous = (byte)b;
                continue;
            }
            (b == 0xE8 ? _call : _jump).ReadExactly(_target);
            var relative = BinaryPrimitives.ReadUInt32BigEndian(_target) - (uint)(_done + 4);
            _pending[0] = (byte)relative;
            _pending[1] = (byte)(relative >> 8);
            _pending[2] = (byte)(relative >> 16);
            _pending[3] = (byte)(relative >> 24);
            _pendingAt = 0;
            _previous = _pending[3];
        }
        return n;
    }

    private static bool IsJump(byte previous, byte b) => (b & 0xFE) == 0xE8 || previous == 0x0F && (b & 0xF0) == 0x80;
}
