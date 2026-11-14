// The decoders of a 7z folder (Archives/SevenZipReader): each coder is a read-only stream over its inputs (a packed
// stream of the archive or the output of another coder), and every coder's output is held to the unpacked size the
// header names. Filee's own LZMA and LZMA2 decoders (Archives/Lzma), the Copy coder, the x86 branch converters BCJ and
// BCJ2, the branch converters for ARM64, ARM, ARM Thumb, PowerPC, SPARC and IA-64 (7-Zip picks ARM64 for ARM64
// programs by itself) and the Delta filter are written here; Deflate comes from .NET and BZip2 from SharpCompress
// (MIT), as for the .bz2 files Filee reads. Other methods (PPMd, RISC-V …) are reported as unsupported before anything
// is unpacked.

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
    public const ulong Ppc = 0x03030205;
    public const ulong Ia64 = 0x03030401;
    public const ulong Arm = 0x03030501;
    public const ulong ArmThumb = 0x03030701;
    public const ulong Sparc = 0x03030805;
    public const ulong Arm64 = 0x0A;
    public const ulong Deflate = 0x040108;
    public const ulong Bzip2 = 0x040202;
    public const ulong Aes = 0x06F10701;

    /// <summary>The packed-side streams a method Filee decodes takes; null for methods it does not decode.</summary>
    public static int? InputsOf(ulong method) => method switch
    {
        Copy or Delta or Lzma2 or Lzma or Bcj or Deflate or Bzip2 => 1,
        Ppc or Ia64 or Arm or ArmThumb or Sparc or Arm64 => 1,
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
        Arm64 => "ARM64",
        0x0B => "RISCV",
        Ppc => "PPC",
        Ia64 => "IA64",
        Arm => "ARM",
        ArmThumb => "ARMT",
        Sparc => "SPARC",
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
            case Ppc or Ia64 or Arm or ArmThumb or Sparc or Arm64:
                decoder = new BranchStream(inputs[0], coder.Method, props.Length >= 4 ? BitConverter.ToUInt32(props, 0) : 0);
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

/// <summary>
/// The branch converters for other processors. Their encoders turned the relative targets of call instructions (on
/// ARM64 also of ADRP) into absolute ones, so repeated calls to one function look alike; this turns them back. Each
/// works on aligned instructions of 2, 4 or 16 bytes, positions counted from the start of the stream plus the start the
/// properties may give; a tail shorter than an instruction stays as it is.
/// </summary>
internal sealed class BranchStream(Stream inner, ulong method, uint start) : ReadOnlyDecoderStream
{
    /// <summary>Which of the three 41-bit slots of an IA-64 bundle may hold a branch, by the bundle's template.</summary>
    private static readonly byte[] Ia64Slots =
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 6, 6, 0, 0, 7, 7, 4, 4, 0, 0, 4, 4, 0, 0];

    private readonly byte[] _buffer = new byte[1 << 16];
    private int _converted; // bytes at the front of _buffer ready to hand out
    private int _filled;    // bytes in _buffer
    private uint _ip = start;
    private bool _ended;

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        while (_converted == 0)
        {
            if (_ended)
            {
                // What is left is shorter than an instruction.
                _converted = _filled;
                if (_converted == 0)
                    return 0;
                break;
            }
            var read = inner.Read(_buffer.AsSpan(_filled));
            if (read <= 0)
            {
                _ended = true;
                continue;
            }
            _filled += read;
            _converted = Convert(_buffer.AsSpan(0, _filled), _ip);
            _ip += (uint)_converted;
        }
        var count = Math.Min(buffer.Length, _converted);
        _buffer.AsSpan(0, count).CopyTo(buffer);
        _buffer.AsSpan(count, _filled - count).CopyTo(_buffer);
        _filled -= count;
        _converted -= count;
        return count;
    }

    /// <summary>Converts the whole instructions at the front of <paramref name="data"/>; returns how many bytes are done.</summary>
    private int Convert(Span<byte> data, uint ip) => method switch
    {
        SevenZipCoders.Arm => Arm(data, ip),
        SevenZipCoders.ArmThumb => ArmThumb(data, ip),
        SevenZipCoders.Arm64 => Arm64(data, ip),
        SevenZipCoders.Ppc => PowerPc(data, ip),
        SevenZipCoders.Sparc => Sparc(data, ip),
        _ => Ia64(data, ip),
    };

    /// <summary>ARM: BL with the condition "always" (top byte 0xEB), a 24-bit word offset from the instruction + 8.</summary>
    private static int Arm(Span<byte> data, uint ip)
    {
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            if (data[i + 3] != 0xEB)
                continue;
            var target = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16) << 2;
            var offset = (target - (ip + (uint)i + 8)) >> 2;
            data[i] = (byte)offset;
            data[i + 1] = (byte)(offset >> 8);
            data[i + 2] = (byte)(offset >> 16);
        }
        return i;
    }

    /// <summary>ARM Thumb: the two halves of BL (F000 and F800, 11 bits each), a halfword offset from the pair + 4.</summary>
    private static int ArmThumb(Span<byte> data, uint ip)
    {
        var i = 0;
        for (; i + 4 <= data.Length; i += 2)
        {
            if ((data[i + 1] & 0xF8) != 0xF0 || (data[i + 3] & 0xF8) != 0xF8)
                continue;
            var target = (((data[i + 1] & 7u) << 19) | (uint)data[i] << 11 | ((data[i + 3] & 7u) << 8) | data[i + 2]) << 1;
            var offset = (target - (ip + (uint)i + 4)) >> 1;
            data[i + 1] = (byte)(0xF0 | ((offset >> 19) & 7));
            data[i] = (byte)(offset >> 11);
            data[i + 3] = (byte)(0xF8 | ((offset >> 8) & 7));
            data[i + 2] = (byte)offset;
            i += 2;
        }
        return i;
    }

    /// <summary>
    /// ARM64: BL (a 26-bit word offset) and ADRP (a 21-bit page offset, converted only within ±512 MiB, as the encoder
    /// did).
    /// </summary>
    private static int Arm64(Span<byte> data, uint ip)
    {
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            var pc = ip + (uint)i;
            if (instruction >> 26 == 0x25)
            {
                instruction = 0x94000000 | ((instruction - (pc >> 2)) & 0x03FFFFFF);
                BinaryPrimitives.WriteUInt32LittleEndian(data[i..], instruction);
            }
            else if ((instruction & 0x9F000000) == 0x90000000)
            {
                var page = ((instruction >> 29) & 3) | ((instruction >> 3) & 0x001FFFFC);
                if (((page + 0x00020000) & 0x001C0000) != 0)
                    continue;
                page -= pc >> 12;
                instruction &= 0x9000001F;
                instruction |= (page & 3) << 29;
                instruction |= (page & 0x0003FFFC) << 3;
                instruction |= (0u - (page & 0x00020000)) & 0x00E00000;
                BinaryPrimitives.WriteUInt32LittleEndian(data[i..], instruction);
            }
        }
        return i;
    }

    /// <summary>PowerPC (big-endian): "bl" (opcode 18, AA = 0, LK = 1), a 24-bit word offset.</summary>
    private static int PowerPc(Span<byte> data, uint ip)
    {
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            if (data[i] >> 2 != 0x12 || (data[i + 3] & 3) != 1)
                continue;
            var target = ((data[i] & 3u) << 24) | (uint)data[i + 1] << 16 | (uint)data[i + 2] << 8 | (data[i + 3] & 0xFCu);
            var offset = target - (ip + (uint)i);
            data[i] = (byte)(0x48 | ((offset >> 24) & 3));
            data[i + 1] = (byte)(offset >> 16);
            data[i + 2] = (byte)(offset >> 8);
            data[i + 3] = (byte)((data[i + 3] & 3u) | (offset & 0xFF));
        }
        return i;
    }

    /// <summary>SPARC: "call" with its word offset, converted when the offset fits in 23 bits (the top bits all equal).</summary>
    private static int Sparc(Span<byte> data, uint ip)
    {
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            if (!(data[i] == 0x40 && (data[i + 1] & 0xC0) == 0x00 || data[i] == 0x7F && (data[i + 1] & 0xC0) == 0xC0))
                continue;
            var target = BinaryPrimitives.ReadUInt32BigEndian(data[i..]) << 2;
            var offset = (target - (ip + (uint)i)) >> 2;
            offset = (((0u - ((offset >> 22) & 1)) << 22) & 0x3FFFFFFF) | (offset & 0x3FFFFF) | 0x40000000;
            BinaryPrimitives.WriteUInt32BigEndian(data[i..], offset);
        }
        return i;
    }

    /// <summary>
    /// IA-64: in each 16-byte bundle, the slots its template marks whose instruction is a relative branch (a 21-bit
    /// bundle offset).
    /// </summary>
    private static int Ia64(Span<byte> data, uint ip)
    {
        var i = 0;
        for (; i + 16 <= data.Length; i += 16)
        {
            var slots = Ia64Slots[data[i] & 0x1F];
            for (int slot = 0, bit = 5; slot < 3; slot++, bit += 41)
            {
                if (((slots >> slot) & 1) == 0)
                    continue;
                var at = i + (bit >> 3);
                var shift = bit & 7;
                ulong raw = 0;
                for (var j = 0; j < 6; j++)
                    raw |= (ulong)data[at + j] << (8 * j);
                var instruction = raw >> shift;
                if (((instruction >> 37) & 0xF) != 0x5 || ((instruction >> 9) & 0x7) != 0)
                    continue;
                var target = ((uint)(instruction >> 13) & 0xFFFFF) | ((uint)(instruction >> 36) & 1) << 20;
                var offset = ((target << 4) - (ip + (uint)i)) >> 4;
                instruction &= ~(0x8FFFFFUL << 13);
                instruction |= (ulong)(offset & 0xFFFFF) << 13;
                instruction |= (ulong)(offset & 0x100000) << (36 - 20);
                raw = (raw & ((1UL << shift) - 1)) | instruction << shift;
                for (var j = 0; j < 6; j++)
                    data[at + j] = (byte)(raw >> (8 * j));
            }
        }
        return i;
    }
}
