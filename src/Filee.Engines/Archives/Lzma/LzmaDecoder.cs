// LZMA decoding, written from the LZMA SDK's lzma-specification.txt: a range-coded stream of literals (coded with
// the previous byte and, after a match, the byte at the last distance as context), matches (length + distance slot +
// distance bits) and repeats of one of the last four distances, driven by a 12-state machine.
//
// Memory: the dictionary named in the header is only a ceiling. The window starts small and doubles as output arrives,
// so a header that claims 1.5 GiB for a 10 KB file costs 10 KB, and callers refuse dictionaries past their limit
// before anything is allocated (see Lzma2Reader and XzReader).

using System.Runtime.CompilerServices;

namespace Filee.Engines.Archives.Lzma;

/// <summary>The lc / lp / pb properties of an LZMA stream.</summary>
internal readonly record struct LzmaProperties(int Lc, int Lp, int Pb)
{
    /// <summary>lc=3, lp=0, pb=2: what xz and 7-Zip use for most data.</summary>
    public static readonly LzmaProperties Default = new(3, 0, 2);

    /// <summary>The properties byte: (pb × 5 + lp) × 9 + lc.</summary>
    public byte ToByte() => (byte)((Pb * 5 + Lp) * 9 + Lc);

    /// <summary>Reads a properties byte; null when it is out of range.</summary>
    public static LzmaProperties? FromByte(byte value)
    {
        if (value >= 9 * 5 * 5)
            return null;
        return new LzmaProperties(value % 9, value / 9 % 5, value / 45);
    }
}

/// <summary>The probabilities of a match or repeat length (2 … 273).</summary>
internal sealed class LengthModel
{
    public const int LowBits = 3;
    public const int MidBits = 3;
    public const int HighBits = 8;
    public const int LowSymbols = 1 << LowBits;
    public const int MidSymbols = 1 << MidBits;

    public readonly ushort[] Choice = new ushort[2];
    public readonly ushort[] Low = new ushort[LzmaModel.MaxPosStates << LowBits];
    public readonly ushort[] Mid = new ushort[LzmaModel.MaxPosStates << MidBits];
    public readonly ushort[] High = new ushort[1 << HighBits];

    public void Reset()
    {
        RangeCoder.Reset(Choice);
        RangeCoder.Reset(Low);
        RangeCoder.Reset(Mid);
        RangeCoder.Reset(High);
    }

    /// <summary>A length minus 2 (0 … 271).</summary>
    public uint Decode(RangeDecoder rc, int posState)
    {
        if (rc.DecodeBit(ref Choice[0]) == 0)
            return rc.DecodeTree(Low.AsSpan(posState << LowBits, LowSymbols), LowBits);
        if (rc.DecodeBit(ref Choice[1]) == 0)
            return LowSymbols + rc.DecodeTree(Mid.AsSpan(posState << MidBits, MidSymbols), MidBits);
        return LowSymbols + MidSymbols + rc.DecodeTree(High, HighBits);
    }

    public void Encode(RangeEncoder rc, uint length, int posState)
    {
        if (length < LowSymbols)
        {
            rc.EncodeBit(ref Choice[0], 0);
            rc.EncodeTree(Low.AsSpan(posState << LowBits, LowSymbols), LowBits, length);
            return;
        }
        rc.EncodeBit(ref Choice[0], 1);
        if (length < LowSymbols + MidSymbols)
        {
            rc.EncodeBit(ref Choice[1], 0);
            rc.EncodeTree(Mid.AsSpan(posState << MidBits, MidSymbols), MidBits, length - LowSymbols);
            return;
        }
        rc.EncodeBit(ref Choice[1], 1);
        rc.EncodeTree(High, HighBits, length - LowSymbols - MidSymbols);
    }

    /// <summary>The cost of <see cref="Encode"/> in 1/16 bit.</summary>
    public int Price(uint length, int posState)
    {
        if (length < LowSymbols)
            return BitPrice.Of(Choice[0], 0) + BitPrice.Tree(Low.AsSpan(posState << LowBits, LowSymbols), LowBits, length);
        if (length < LowSymbols + MidSymbols)
            return BitPrice.Of(Choice[0], 1) + BitPrice.Of(Choice[1], 0)
                   + BitPrice.Tree(Mid.AsSpan(posState << MidBits, MidSymbols), MidBits, length - LowSymbols);
        return BitPrice.Of(Choice[0], 1) + BitPrice.Of(Choice[1], 1)
               + BitPrice.Tree(High, HighBits, length - LowSymbols - MidSymbols);
    }
}

/// <summary>The adaptive model shared by the LZMA encoder and decoder: probabilities, state and the last four distances.</summary>
internal class LzmaModel
{
    public const int States = 12;
    public const int MaxPosStates = 1 << 4;
    public const int MatchMinLength = 2;
    public const int MatchMaxLength = 273;
    public const int LengthStates = 4;
    public const int PosSlotBits = 6;
    public const int StartPosModelIndex = 4;
    public const int EndPosModelIndex = 14;
    public const int FullDistances = 1 << (EndPosModelIndex >> 1);
    public const int AlignBits = 4;
    public const int LiteralStates = 7;

    protected readonly ushort[] IsMatch = new ushort[States * MaxPosStates];
    protected readonly ushort[] IsRep = new ushort[States];
    protected readonly ushort[] IsRepG0 = new ushort[States];
    protected readonly ushort[] IsRepG1 = new ushort[States];
    protected readonly ushort[] IsRepG2 = new ushort[States];
    protected readonly ushort[] IsRep0Long = new ushort[States * MaxPosStates];
    protected readonly ushort[] PosSlot = new ushort[LengthStates << PosSlotBits];
    protected readonly ushort[] PosSpecial = new ushort[1 + FullDistances - EndPosModelIndex];
    protected readonly ushort[] Align = new ushort[1 << AlignBits];
    protected readonly LengthModel MatchLength = new();
    protected readonly LengthModel RepLength = new();
    protected ushort[] Literal = [];

    protected int Lc;
    protected int LpMask;
    protected int PbMask;
    protected int State;
    protected uint Rep0, Rep1, Rep2, Rep3;

    /// <summary>Sets lc / lp / pb (the literal table is resized when lc + lp changes); call <see cref="ResetState"/> after.</summary>
    public void SetProperties(LzmaProperties properties)
    {
        if (properties.Lc is < 0 or > 8 || properties.Lp is < 0 or > 4 || properties.Pb is < 0 or > 4)
            throw new InvalidDataException("the LZMA properties are out of range");
        Lc = properties.Lc;
        LpMask = (1 << properties.Lp) - 1;
        PbMask = (1 << properties.Pb) - 1;
        var size = 0x300 << (properties.Lc + properties.Lp);
        if (Literal.Length != size)
            Literal = new ushort[size];
    }

    /// <summary>Probabilities to one half, state 0, all four distances 1.</summary>
    public virtual void ResetState()
    {
        RangeCoder.Reset(IsMatch);
        RangeCoder.Reset(IsRep);
        RangeCoder.Reset(IsRepG0);
        RangeCoder.Reset(IsRepG1);
        RangeCoder.Reset(IsRepG2);
        RangeCoder.Reset(IsRep0Long);
        RangeCoder.Reset(PosSlot);
        RangeCoder.Reset(PosSpecial);
        RangeCoder.Reset(Align);
        RangeCoder.Reset(Literal);
        MatchLength.Reset();
        RepLength.Reset();
        State = 0;
        Rep0 = Rep1 = Rep2 = Rep3 = 0;
    }

    protected static int AfterLiteral(int state) => state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
    protected static int AfterMatch(int state) => state < LiteralStates ? 7 : 10;
    protected static int AfterRep(int state) => state < LiteralStates ? 8 : 11;
    protected static int AfterShortRep(int state) => state < LiteralStates ? 9 : 11;

    /// <summary>Offset of the 0x300 literal probabilities for a byte at <paramref name="position"/> after <paramref name="previous"/>.</summary>
    protected int LiteralBase(long position, int previous) =>
        0x300 * (((int)position & LpMask) << Lc | previous >> (8 - Lc));
}

/// <summary>
/// Decodes LZMA symbols into a sliding window and the caller's buffer. LZMA2 drives it chunk by chunk; raw LZMA
/// (.lzma, lzip) through <see cref="LzmaRawReader"/>.
/// </summary>
internal sealed class LzmaDecoder : LzmaModel
{
    /// <summary>The smallest window LZMA uses, whatever the header says.</summary>
    public const uint MinDictionary = 1 << 12;

    /// <summary>The largest dictionary any caller may pass: 1.5 GiB, the most xz itself writes.</summary>
    public const uint MaxDictionary = 1536u << 20;

    private const int FirstWindow = 1 << 16;

    private readonly uint _dictionary;
    private byte[] _window;
    private int _pos;
    private bool _full;
    private long _total;
    private int _pending; // bytes of the current match not yet copied out

    /// <param name="dictionary">The window size from the header (already checked against the caller's limit).</param>
    /// <param name="expectedSize">The output size when the header names it; the window never grows past it.</param>
    public LzmaDecoder(uint dictionary, long expectedSize = -1)
    {
        if (dictionary > MaxDictionary)
            throw new ArgumentOutOfRangeException(nameof(dictionary), "callers refuse dictionaries past their limit first");
        _dictionary = Math.Max(dictionary, MinDictionary);
        var ceiling = expectedSize >= 0 ? Math.Min(_dictionary, Math.Max(expectedSize, 1)) : _dictionary;
        _window = new byte[Math.Min(ceiling, FirstWindow)];
    }

    /// <summary>True after the end marker (only raw LZMA allows one).</summary>
    public bool EndMarkerSeen { get; private set; }

    /// <summary>True when a match was cut by the end of the caller's buffer and continues in the next call.</summary>
    public bool HasPendingMatch => _pending > 0;

    /// <summary>Bytes decoded since the last dictionary reset.</summary>
    public long Total => _total;

    public override void ResetState()
    {
        base.ResetState();
        _pending = 0;
    }

    /// <summary>Forgets the window (LZMA2 dictionary reset).</summary>
    public void ResetDictionary()
    {
        _pos = 0;
        _full = false;
        _total = 0;
        _pending = 0;
    }

    /// <summary>Appends stored bytes (an LZMA2 uncompressed chunk) to the window.</summary>
    public void AppendUncompressed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            Put(b);
    }

    /// <summary>
    /// Decodes until <paramref name="output"/> is full or, with <paramref name="allowEndMarker"/>, the end marker
    /// comes. Returns the bytes written.
    /// </summary>
    public int Decode(RangeDecoder rc, Span<byte> output, bool allowEndMarker)
    {
        // Range and code live in locals here and go back to rc around the multi-bit decoders (which keep their own).
        rc.Load(out var range, out var code);
        var n = 0;
        if (_pending > 0)
            n = CopyMatch(output, 0, _pending);

        while (n < output.Length)
        {
            var posState = (int)_total & PbMask;
            var state2 = (State << 4) + posState;

            if (RangeDecoder.Bit(ref range, ref code, ref IsMatch[state2], rc) == 0)
            {
                var previous = IsEmpty ? 0 : ByteAt(0);
                var probs = Literal.AsSpan(LiteralBase(_total, previous), 0x300);
                // After a match the byte at the last distance steers the first bits (see DecodeMatchedLiteral).
                rc.Store(range, code);
                var symbol = State >= LiteralStates ? rc.DecodeMatchedLiteral(probs, ByteAt(Rep0)) : rc.DecodeTree(probs, 8);
                rc.Load(out range, out code);
                Put((byte)symbol);
                output[n++] = (byte)symbol;
                State = AfterLiteral(State);
                continue;
            }

            uint length;
            if (RangeDecoder.Bit(ref range, ref code, ref IsRep[State], rc) != 0)
            {
                if (IsEmpty)
                    throw new InvalidDataException("the LZMA data repeats bytes before any were written");
                if (RangeDecoder.Bit(ref range, ref code, ref IsRepG0[State], rc) == 0)
                {
                    if (RangeDecoder.Bit(ref range, ref code, ref IsRep0Long[state2], rc) == 0)
                    {
                        // Short repeat: one byte from the last distance.
                        State = AfterShortRep(State);
                        var b = ByteAt(Rep0);
                        Put(b);
                        output[n++] = b;
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (RangeDecoder.Bit(ref range, ref code, ref IsRepG1[State], rc) == 0)
                    {
                        distance = Rep1;
                    }
                    else
                    {
                        if (RangeDecoder.Bit(ref range, ref code, ref IsRepG2[State], rc) == 0)
                        {
                            distance = Rep2;
                        }
                        else
                        {
                            distance = Rep3;
                            Rep3 = Rep2;
                        }
                        Rep2 = Rep1;
                    }
                    Rep1 = Rep0;
                    Rep0 = distance;
                }
                rc.Store(range, code);
                length = RepLength.Decode(rc, posState);
                rc.Load(out range, out code);
                State = AfterRep(State);
            }
            else
            {
                Rep3 = Rep2;
                Rep2 = Rep1;
                Rep1 = Rep0;
                rc.Store(range, code);
                length = MatchLength.Decode(rc, posState);
                State = AfterMatch(State);
                Rep0 = DecodeDistance(rc, length);
                rc.Load(out range, out code);
                if (Rep0 == 0xFFFFFFFF)
                {
                    if (!allowEndMarker)
                        throw new InvalidDataException("the LZMA data has an end marker where none is allowed");
                    EndMarkerSeen = true;
                    rc.Store(range, code);
                    return n;
                }
            }
            if (!IsValidDistance(Rep0))
                throw new InvalidDataException("the LZMA data refers to bytes before the start of the window");
            n = CopyMatch(output, n, (int)length + MatchMinLength);
        }
        rc.Store(range, code);
        return n;
    }

    private uint DecodeDistance(RangeDecoder rc, uint length)
    {
        var lengthState = (int)Math.Min(length, LengthStates - 1);
        var slot = rc.DecodeTree(PosSlot.AsSpan(lengthState << PosSlotBits, 1 << PosSlotBits), PosSlotBits);
        if (slot < StartPosModelIndex)
            return slot;
        var footerBits = (int)(slot >> 1) - 1;
        var distance = (2 | (slot & 1)) << footerBits;
        if (slot < EndPosModelIndex)
            return distance + rc.DecodeReverseTree(PosSpecial.AsSpan((int)(distance - slot)), footerBits);
        distance += rc.DecodeDirectBits(footerBits - AlignBits) << AlignBits;
        return distance + rc.DecodeReverseTree(Align, AlignBits);
    }

    private bool IsEmpty => _pos == 0 && !_full;

    private bool IsValidDistance(uint distance) =>
        distance < _dictionary && (_full ? distance < (uint)_window.Length : distance < (uint)_pos);

    /// <summary>The byte <paramref name="distance"/> + 1 back.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ByteAt(uint distance)
    {
        var index = _pos - (long)distance - 1;
        if (index < 0)
            index += _window.Length;
        return _window[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Put(byte b)
    {
        _window[_pos++] = b;
        _total++;
        if (_pos == _window.Length)
            WindowEnd();
    }

    /// <summary>The write position reached the end of the window: grow it while it is below the dictionary, else wrap.</summary>
    private void WindowEnd()
    {
        if ((uint)_window.Length < _dictionary)
        {
            // Still filling: grow (twice the size, up to the dictionary) instead of wrapping.
            Array.Resize(ref _window, (int)Math.Min(_dictionary, (long)_window.Length * 2));
        }
        else
        {
            _pos = 0;
            _full = true;
        }
    }

    /// <summary>
    /// Copies up to <paramref name="length"/> bytes from distance Rep0; what doesn't fit stays pending. Runs that reach
    /// neither end of the window are copied in one go: as a block move when source and target don't overlap (or the
    /// source lies after the target, where a forward copy reads every byte before it is overwritten), byte by byte when
    /// the match repeats bytes it is still writing.
    /// </summary>
    private int CopyMatch(Span<byte> output, int n, int length)
    {
        var count = Math.Min(length, output.Length - n);
        _pending = length - count;
        var distance = (int)Rep0 + 1; // Rep0 is below the window size (IsValidDistance), which fits an int
        while (count > 0)
        {
            var window = _window;
            var source = _pos - distance;
            if (source < 0)
                source += window.Length;
            var chunk = Math.Min(count, Math.Min(window.Length - _pos, window.Length - source));
            if (chunk >= 16 && (source > _pos || distance >= chunk))
            {
                window.AsSpan(source, chunk).CopyTo(window.AsSpan(_pos, chunk));
                window.AsSpan(_pos, chunk).CopyTo(output.Slice(n, chunk));
            }
            else
            {
                var target = output.Slice(n, chunk);
                for (var i = 0; i < target.Length; i++)
                    target[i] = window[_pos + i] = window[source + i];
            }
            n += chunk;
            count -= chunk;
            _pos += chunk;
            _total += chunk;
            if (_pos == window.Length)
                WindowEnd();
        }
        return n;
    }
}

/// <summary>
/// Raw LZMA (5 property bytes are read by the caller): either a stated number of bytes, or up to the end marker
/// when the size is unknown (-1). Reads its input in blocks, so it may read past the LZMA data.
/// </summary>
internal sealed class LzmaRawReader : Stream
{
    private readonly LzmaDecoder _decoder;
    private readonly RangeDecoder _rc = new();
    private readonly long _size;
    private long _done;
    private bool _ended;

    public LzmaRawReader(Stream input, LzmaProperties properties, uint dictionary, long size)
    {
        _size = size;
        _decoder = new LzmaDecoder(dictionary, size);
        _decoder.SetProperties(properties);
        _decoder.ResetState();
        _rc.Start(input, new byte[1 << 16]);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_ended || buffer.IsEmpty)
            return 0;
        if (_size >= 0 && buffer.Length > _size - _done)
            buffer = buffer[..(int)(_size - _done)];
        var n = _decoder.Decode(_rc, buffer, allowEndMarker: true);
        _done += n;
        if (_decoder.EndMarkerSeen)
        {
            _ended = true;
            if (_size >= 0 && _done != _size)
                throw new InvalidDataException("the LZMA data ends before its stated size");
            if (!_rc.IsFinishedOk)
                throw new InvalidDataException("the LZMA data is damaged");
        }
        else if (_size >= 0 && _done == _size)
        {
            _ended = true;
        }
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _done; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
