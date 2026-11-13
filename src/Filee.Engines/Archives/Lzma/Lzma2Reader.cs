// LZMA2 decoding (the .xz format's filter 0x21): a sequence of chunks, each introduced by a control byte.
//   0x00            end of the LZMA2 data
//   0x01 / 0x02     stored chunk of up to 64 KiB, with / without a dictionary reset
//   0x80 … 0xFF     LZMA chunk: bits 0-4 are the top bits of (unpacked size − 1), bits 5-6 say what is reset first
//                   (0 nothing, 1 state, 2 state + new properties byte, 3 everything including the dictionary),
//                   then (unpacked size − 1) low 16 bits and (packed size − 1), big-endian
// An LZMA chunk unpacks to at most 2 MiB from at most 64 KiB, with its own range coder, so each is read whole
// before it is decoded and nothing in it can claim more than those limits.

namespace Filee.Engines.Archives.Lzma;

/// <summary>Buffered reading from a stream that counts the bytes it hands out.</summary>
internal sealed class ByteInput(Stream stream)
{
    private readonly byte[] _buffer = new byte[1 << 16];
    private int _position;
    private int _end;

    /// <summary>Bytes handed out so far.</summary>
    public long Consumed { get; private set; }

    /// <summary>The next byte, or -1 at the end of the stream.</summary>
    public int ReadByte()
    {
        if (_position == _end && !Fill())
            return -1;
        Consumed++;
        return _buffer[_position++];
    }

    /// <summary>Fills <paramref name="target"/>; the stream ending first is damage.</summary>
    public void ReadExactly(Span<byte> target)
    {
        if (ReadAtMost(target) < target.Length)
            throw new InvalidDataException("the data ends too early");
    }

    /// <summary>Reads as much of <paramref name="target"/> as the stream still has.</summary>
    public int ReadAtMost(Span<byte> target)
    {
        var done = 0;
        while (done < target.Length)
        {
            if (_position == _end && !Fill())
                break;
            var count = Math.Min(target.Length - done, _end - _position);
            _buffer.AsSpan(_position, count).CopyTo(target[done..]);
            _position += count;
            done += count;
        }
        Consumed += done;
        return done;
    }

    private bool Fill()
    {
        _position = 0;
        _end = stream.Read(_buffer, 0, _buffer.Length);
        if (_end < 0)
            _end = 0;
        return _end > 0;
    }
}

/// <summary>Decodes LZMA2 data read from a <see cref="ByteInput"/>.</summary>
internal sealed class Lzma2Reader
{
    /// <summary>The most one LZMA chunk unpacks to.</summary>
    public const int MaxChunkUnpacked = 1 << 21;

    /// <summary>The most one LZMA chunk holds packed, and the most a stored chunk holds.</summary>
    public const int MaxChunkPacked = 1 << 16;

    private enum Chunk { None, Stored, Lzma }

    private readonly ByteInput _input;
    private readonly CancellationToken _cancellationToken;
    private readonly LzmaDecoder _decoder;
    private readonly RangeDecoder _rc = new();
    private readonly byte[] _packed = new byte[MaxChunkPacked];
    private Chunk _chunk;
    private int _left;
    private int _packedSize;
    private bool _needDictionaryReset = true;
    private bool _needProperties = true;

    /// <param name="dictionary">The dictionary size from the filter properties, already checked against the limit.</param>
    /// <param name="expectedSize">The unpacked size when a header names it, otherwise -1.</param>
    public Lzma2Reader(ByteInput input, uint dictionary, long expectedSize, CancellationToken cancellationToken)
    {
        _input = input;
        _cancellationToken = cancellationToken;
        _decoder = new LzmaDecoder(dictionary, expectedSize);
    }

    /// <summary>True after the end byte.</summary>
    public bool Ended { get; private set; }

    /// <summary>Decodes into <paramref name="output"/>; 0 only at the end of the LZMA2 data.</summary>
    public int Read(Span<byte> output)
    {
        var n = 0;
        while (n < output.Length)
        {
            if (_left == 0)
            {
                if (_chunk == Chunk.Lzma)
                    FinishLzmaChunk();
                _chunk = Chunk.None;
                if (Ended)
                    break;
                ReadControl();
                continue;
            }
            var part = output.Slice(n, Math.Min(output.Length - n, _left));
            if (_chunk == Chunk.Stored)
            {
                _input.ReadExactly(part);
                _decoder.AppendUncompressed(part);
            }
            else
            {
                if (_decoder.Decode(_rc, part, allowEndMarker: false) != part.Length)
                    throw new InvalidDataException("the LZMA2 chunk is damaged");
            }
            n += part.Length;
            _left -= part.Length;
        }
        if (_left == 0 && _chunk == Chunk.Lzma)
        {
            FinishLzmaChunk();
            _chunk = Chunk.None;
        }
        return n;
    }

    private void FinishLzmaChunk()
    {
        // A match may not run into the next chunk, and the range coder must have used exactly the packed bytes.
        if (_decoder.HasPendingMatch || _rc.Position != _packedSize || !_rc.IsFinishedOk)
            throw new InvalidDataException("an LZMA2 chunk does not end where its header says");
    }

    private void ReadControl()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var control = Next();
        if (control == 0)
        {
            Ended = true;
            return;
        }
        if (control >= 0xE0 || control == 1)
        {
            _needProperties = true;
            _needDictionaryReset = false;
            _decoder.ResetDictionary();
        }
        else if (_needDictionaryReset)
        {
            throw new InvalidDataException("the LZMA2 data does not start with a dictionary reset");
        }

        if (control < 0x80)
        {
            if (control > 2)
                throw new InvalidDataException("the LZMA2 data has an unknown chunk type");
            _left = (Next() << 8 | Next()) + 1;
            _chunk = Chunk.Stored;
            return;
        }

        var unpacked = ((control & 0x1F) << 16 | Next() << 8 | Next()) + 1;
        _packedSize = (Next() << 8 | Next()) + 1;
        if (control >= 0xC0)
        {
            var properties = LzmaProperties.FromByte((byte)Next());
            if (properties is not { } p || p.Lc + p.Lp > 4)
                throw new InvalidDataException("the LZMA2 properties are out of range");
            _decoder.SetProperties(p);
            _decoder.ResetState();
            _needProperties = false;
        }
        else if (_needProperties)
        {
            throw new InvalidDataException("an LZMA2 chunk comes before the properties");
        }
        else if (control >= 0xA0)
        {
            _decoder.ResetState();
        }
        _input.ReadExactly(_packed.AsSpan(0, _packedSize));
        _rc.Start(_packed, _packedSize);
        _left = unpacked;
        _chunk = Chunk.Lzma;
    }

    private int Next()
    {
        var b = _input.ReadByte();
        return b >= 0 ? b : throw new InvalidDataException("the LZMA2 data ends too early");
    }
}
