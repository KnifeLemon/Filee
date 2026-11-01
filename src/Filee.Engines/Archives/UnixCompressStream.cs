// Decodes Unix compress (.Z, .tar.Z, .taz) files: "\x1F\x9D", a flags byte (bits 0-4: the largest code width, 9 to
// 16; bit 7: block mode, where code 256 clears the table), then LZW codes stored low bit first. Codes start 9 bits
// wide and grow by one bit whenever the table fills up their range. The encoder writes codes in groups of eight (one
// group is as many bytes as the code width), and when the width changes or the table is cleared the rest of the
// current group is padding: the next code starts with a new group. Written from the format description of the
// original compress(1); no code is taken from it.

namespace Filee.Engines.Archives;

/// <summary>A read-only stream of the data inside a .Z file.</summary>
internal sealed class UnixCompressStream : Stream
{
    private const int InitialBits = 9;
    private const int Clear = 256;

    private readonly Stream _input;
    private readonly int _maxBits;
    private readonly bool _blockMode;
    private readonly ushort[] _prefix;
    private readonly byte[] _suffix;
    private readonly byte[] _stack;
    private readonly byte[] _group = new byte[17];

    private int _bits = InitialBits;
    private int _maxCode = (1 << InitialBits) - 1;
    private int _groupLength;
    private int _bitPosition;
    private int _nextFree;
    private int _previous = -1;
    private byte _firstByte;
    private int _stackTop;
    private bool _ended;

    public UnixCompressStream(Stream input)
    {
        _input = input;
        Span<byte> header = stackalloc byte[3];
        if (input.ReadAtLeast(header, 3, throwOnEndOfStream: false) < 3 || header[0] != 0x1F || header[1] != 0x9D)
            throw new InvalidDataException("not a .Z (Unix compress) file");
        _maxBits = header[2] & 0x1F;
        _blockMode = (header[2] & 0x80) != 0;
        if (_maxBits is < InitialBits or > 16 || (header[2] & 0x60) != 0)
            throw new InvalidDataException($"unsupported .Z settings (flags 0x{header[2]:X2})");
        var size = 1 << _maxBits;
        _prefix = new ushort[size];
        _suffix = new byte[size];
        _stack = new byte[size];
        for (var i = 0; i < 256; i++)
            _suffix[i] = (byte)i;
        _nextFree = _blockMode ? Clear + 1 : Clear;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            if (_stackTop > 0)
            {
                // The decoded string sits reversed on the stack; hand out as much of it as fits.
                var n = Math.Min(_stackTop, buffer.Length - written);
                for (var i = 0; i < n; i++)
                    buffer[written++] = _stack[--_stackTop];
                continue;
            }
            if (_ended || !DecodeNext())
                break;
        }
        return written;
    }

    /// <summary>Decodes the next code onto the stack; false at the end of the data.</summary>
    private bool DecodeNext()
    {
        while (true)
        {
            if (_nextFree > _maxCode)
            {
                // As compress does it, also past the top width when that is 9 bits (the table is full by then).
                _bits++;
                _maxCode = _bits == _maxBits ? 1 << _maxBits : (1 << _bits) - 1;
                _bitPosition = _groupLength * 8; // the rest of the group is padding
            }
            var code = ReadCode();
            if (code < 0)
            {
                _ended = true;
                return false;
            }
            if (_previous == -1)
            {
                if (code > 255)
                    throw new InvalidDataException("the .Z data starts with an invalid code");
                _previous = code;
                _firstByte = (byte)code;
                _stack[_stackTop++] = (byte)code;
                return true;
            }
            if (code == Clear && _blockMode)
            {
                // The entry made by the next code lands on 256 and is never used, as compress does it.
                _nextFree = Clear;
                _bits = InitialBits;
                _maxCode = (1 << InitialBits) - 1;
                _bitPosition = _groupLength * 8;
                continue;
            }

            var current = code;
            if (code >= _nextFree)
            {
                if (code > _nextFree)
                    throw new InvalidDataException("the .Z data refers to a code not defined yet");
                _stack[_stackTop++] = _firstByte; // the KwKwK case: the string being defined right now
                code = _previous;
            }
            while (code >= 256)
            {
                if (_stackTop >= _stack.Length)
                    throw new InvalidDataException("the .Z code table loops");
                _stack[_stackTop++] = _suffix[code];
                code = _prefix[code];
            }
            _firstByte = _suffix[code];
            _stack[_stackTop++] = _firstByte;

            if (_nextFree < 1 << _maxBits)
            {
                _prefix[_nextFree] = (ushort)_previous;
                _suffix[_nextFree] = _firstByte;
                _nextFree++;
            }
            _previous = current;
            return true;
        }
    }

    /// <summary>Reads one code of the current width, or -1 when the data ends.</summary>
    private int ReadCode()
    {
        if (_bitPosition + _bits > _groupLength * 8)
        {
            _groupLength = _input.ReadAtLeast(_group.AsSpan(0, _bits), _bits, throwOnEndOfStream: false);
            _bitPosition = 0;
            if (_groupLength * 8 < _bits)
                return -1;
        }
        var bytePosition = _bitPosition >> 3;
        var value = _group[bytePosition] | (bytePosition + 1 < _groupLength ? _group[bytePosition + 1] << 8 : 0)
                                         | (bytePosition + 2 < _groupLength ? _group[bytePosition + 2] << 16 : 0);
        var code = (value >> (_bitPosition & 7)) & ((1 << _bits) - 1);
        _bitPosition += _bits;
        return code;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _input.Dispose();
        base.Dispose(disposing);
    }
}
