// LZMA decoding, written from the LZMA SDK's lzma-specification.txt: a range-coded stream of literals (coded with
// the previous byte and, after a match, the byte at the last distance as context), matches (length + distance slot +
// distance bits) and repeats of one of the last four distances, driven by a 12-state machine.
//
// Memory: the dictionary named in the header is only a ceiling. The window starts small and doubles as output arrives,
// so a header that claims 1.5 GiB for a 10 KB file costs 10 KB, and callers refuse dictionaries past their limit
// before anything is allocated (see Lzma2Reader and XzReader).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

/// <summary>The probabilities of a match or repeat length (2 … 273), as the encoder keeps them.</summary>
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

/// <summary>
/// The encoder's adaptive model: probabilities, state and the last four distances. The decoder keeps the same model
/// in one array of its own (<see cref="LzmaDecoder"/>); the constants here are shared.
/// </summary>
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
/// Decodes LZMA symbols into a sliding window and hands what it decoded to the caller's buffer in blocks. LZMA2 drives
/// it chunk by chunk; raw LZMA (.lzma, lzip, 7z, NSIS) through <see cref="LzmaRawReader"/>.
/// </summary>
/// <remarks>
/// Speed:
/// - Symbols are decoded into the window up to the end of the caller's buffer or of the window, and copied out in one
///   block. Runs of literals and runs of matches are decoded by two methods that keep the range coder, the input
///   position and the state in locals (registers) for the whole run and hand over when the IsMatch bit changes.
/// - Input: while 64 bytes or more are left (a symbol reads at most 48), bytes are read without checks; the last ones
///   of a chunk or of the stream are read with checks (a second copy of the same code, picked by a type parameter).
/// - Every probability lives in one array (offsets below), reached through refs whose offsets are bounded by
///   construction: state &lt; 12, pos state &lt; 16, tree nodes &lt; 2^bits, literal contexts &lt; 2^(lc + lp).
/// - The bits of literals and of the length, slot and distance trees are close to random, so they are decoded with
///   masks instead of a branch on the bit value; literals read both children's probabilities before the bit is known.
/// The output, the errors and the limits are those of the plain decoder in the LZMA specification. A decoder that
/// threw is not used again.
/// </remarks>
internal sealed class LzmaDecoder
{
    /// <summary>The smallest window LZMA uses, whatever the header says.</summary>
    public const uint MinDictionary = 1 << 12;

    /// <summary>The largest dictionary any caller may pass: 1.5 GiB, the most xz itself writes.</summary>
    public const uint MaxDictionary = 1536u << 20;

    private const int FirstWindow = 1 << 16;

    // The probabilities, in one array: the single-bit decisions, the slot and distance trees, the two length models
    // and, last, the literal coders (0x300 per context, 2^(lc + lp) contexts).
    private const int States = LzmaModel.States;
    private const int PosStates = LzmaModel.MaxPosStates;
    private const int IsMatchAt = 0;
    private const int IsRepAt = IsMatchAt + States * PosStates;
    private const int IsRepG0At = IsRepAt + States;
    private const int IsRepG1At = IsRepG0At + States;
    private const int IsRepG2At = IsRepG1At + States;
    private const int IsRep0LongAt = IsRepG2At + States;
    private const int PosSlotAt = IsRep0LongAt + States * PosStates;
    private const int PosSpecialAt = PosSlotAt + (LzmaModel.LengthStates << LzmaModel.PosSlotBits);
    private const int AlignAt = PosSpecialAt + 1 + LzmaModel.FullDistances - LzmaModel.EndPosModelIndex;
    private const int MatchLengthAt = AlignAt + (1 << LzmaModel.AlignBits);
    private const int RepLengthAt = MatchLengthAt + LengthSize;
    private const int LiteralAt = RepLengthAt + LengthSize;
    private const int LiteralSize = 0x300;

    // One length model: the two choice bits, then the low and mid trees (8 symbols per pos state) and the high tree.
    private const int Choice2At = 1;
    private const int LowAt = 2;
    private const int MidAt = LowAt + (PosStates << LengthModel.LowBits);
    private const int HighAt = MidAt + (PosStates << LengthModel.MidBits);
    private const int LengthSize = HighAt + (1 << LengthModel.HighBits);

    private const uint Top = RangeCoder.TopValue;

    private readonly uint _dictionary;
    private byte[] _window;
    private int _pos;
    private bool _full;
    private long _total;
    private int _pending; // bytes of the current match not yet copied out

    private ushort[] _probabilities = [];
    private int _runStop;      // where the current Run stops
    private int _runTotalBase; // pos + this has the low bits of the total
    private int _lc;
    private int _lpMask;
    private int _pbMask;
    private int _state;
    private uint _rep0, _rep1, _rep2, _rep3;

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

    /// <summary>Sets lc / lp / pb (the literal coders are resized when lc + lp changes); call <see cref="ResetState"/> after.</summary>
    public void SetProperties(LzmaProperties properties)
    {
        if (properties.Lc is < 0 or > 8 || properties.Lp is < 0 or > 4 || properties.Pb is < 0 or > 4)
            throw new InvalidDataException("the LZMA properties are out of range");
        _lc = properties.Lc;
        _lpMask = (1 << properties.Lp) - 1;
        _pbMask = (1 << properties.Pb) - 1;
        // LiteralSize more at the end: the literal loops read both children of a node before knowing the bit, and
        // after the last bit of a matched literal those (unused) reads may reach past the last coder.
        var size = LiteralAt + (LiteralSize << (properties.Lc + properties.Lp)) + LiteralSize;
        if (_probabilities.Length != size)
            _probabilities = new ushort[size];
    }

    /// <summary>Probabilities to one half, state 0, all four distances 1, no match pending.</summary>
    public void ResetState()
    {
        RangeCoder.Reset(_probabilities);
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
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
        while (!data.IsEmpty)
        {
            var count = Math.Min(data.Length, _window.Length - _pos);
            data[..count].CopyTo(_window.AsSpan(_pos));
            _pos += count;
            _total += count;
            data = data[count..];
            if (_pos == _window.Length)
                WindowEnd();
        }
    }

    /// <summary>
    /// Decodes until <paramref name="output"/> is full or, with <paramref name="allowEndMarker"/>, the end marker
    /// comes. Returns the bytes written.
    /// </summary>
    public int Decode(RangeDecoder rc, Span<byte> output, bool allowEndMarker)
    {
        // The literal coders come with the properties; without them the probabilities below would not be there.
        if (_probabilities.Length <= LiteralAt)
            throw new InvalidOperationException("SetProperties comes before Decode");
        var n = 0;
        while (n < output.Length)
        {
            // Decode into the window up to the end of the output or of the window, then copy that block out.
            var start = _pos;
            var stop = start + Math.Min(output.Length - n, _window.Length - start);
            var ended = false;
            while (_pos < stop && !ended)
            {
                // Symbols read at most 48 input bytes: with InputMargin of them at hand none is checked for; the last
                // few bytes of a chunk or of the input are read with checks.
                ended = rc.TopUp(InputMargin) >= InputMargin
                    ? Run<UncheckedInput>(rc, stop, allowEndMarker)
                    : Run<CheckedInput>(rc, stop, allowEndMarker);
            }
            var count = _pos - start;
            _window.AsSpan(start, count).CopyTo(output[n..]);
            n += count;
            if (_pos == _window.Length)
                WindowEnd();
            if (ended)
            {
                EndMarkerSeen = true;
                break;
            }
        }
        return n;
    }

    /// <summary>
    /// Decodes symbols into the window from <see cref="_pos"/> until <paramref name="stop"/> (at most the window's
    /// end); a match that doesn't fit stays pending. With <see cref="UncheckedInput"/> it also stops before a symbol
    /// when fewer than <see cref="InputMargin"/> input bytes are left. Returns true at the end marker.
    /// </summary>
    /// <remarks>
    /// Runs of literals and runs of matches are decoded by two methods that each loop over their kind of symbol and
    /// hand over when the IsMatch bit changes. Each keeps the range coder in registers for its whole run: one method
    /// for both had more live values than the CPU has registers, and the JIT spilled the coder in the literal loop.
    /// </remarks>
    private bool Run<TInput>(RangeDecoder rc, int stop, bool allowEndMarker) where TInput : struct, IInputMode
    {
        var start = _pos;
        _runStop = stop;
        _runTotalBase = (int)_total - start; // pos + this has the low bits of the total, which is all contexts use
        if (_pending > 0)
        {
            var count = Math.Min(_pending, stop - _pos);
            _pending -= count;
            _pos = Copy(_window, _pos, (int)_rep0 + 1, count);
        }
        var next = NextSymbol<TInput>(rc);
        while (next != Stopped)
        {
            next = next == LiteralNext ? LiteralRun<TInput>(rc) : MatchRun<TInput>(rc, allowEndMarker);
            if (next == EndMarker)
                break;
        }
        _total += _pos - start;
        return next == EndMarker;
    }

    // What follows the symbols a run decoded.
    private const int Stopped = 0;     // the end of the block or of the safe input: no IsMatch bit read
    private const int LiteralNext = 1; // an IsMatch bit of 0 was read
    private const int MatchNext = 2;   // an IsMatch bit of 1 was read
    private const int EndMarker = 3;

    /// <summary>Reads the IsMatch bit of the next symbol, unless Run has to stop before it.</summary>
    private int NextSymbol<TInput>(RangeDecoder rc) where TInput : struct, IInputMode
    {
        var pos = _pos;
        rc.Take(out var range, out var code, out var inPos);
        var inEnd = rc.End;
        if (pos >= _runStop || (!TInput.Checked && inPos > inEnd - InputMargin))
            return Stopped;
        ref var input = ref MemoryMarshal.GetArrayDataReference(rc.Buffer);
        var posState = (pos + _runTotalBase) & _pbMask;
        var bit = Bit<TInput>(ref _probabilities[IsMatchAt + (_state << 4) + posState], ref range, ref code, ref input, ref inPos, ref inEnd, rc);
        rc.Put(range, code, inPos);
        return bit == 0 ? LiteralNext : MatchNext;
    }

    /// <summary>
    /// Decodes literals (the first one's IsMatch bit was read) until a match comes (<see cref="MatchNext"/>) or Run
    /// has to stop (<see cref="Stopped"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int LiteralRun<TInput>(RangeDecoder rc) where TInput : struct, IInputMode
    {
        ref var win = ref MemoryMarshal.GetArrayDataReference(_window);
        var pos = _pos;
        var stop = _runStop;
        var totalBase = _runTotalBase;
        ref var probs = ref MemoryMarshal.GetArrayDataReference(_probabilities);
        var state = _state;
        ref var input = ref MemoryMarshal.GetArrayDataReference(rc.Buffer);
        var inEnd = rc.End;
        rc.Take(out var range, out var code, out var inPos);
        int next;
        while (true)
        {
            // A literal, coded with the previous byte (and lp bits of the position) as context.
            uint previous = pos > 0 ? Unsafe.Add(ref win, pos - 1) : _full ? Unsafe.Add(ref win, _window.Length - 1) : 0u;
            var lc = _lc;
            ref var literal = ref Unsafe.Add(ref probs, LiteralAt + LiteralSize * ((((pos + totalBase) & _lpMask) << lc) + (int)(previous >> (8 - lc))));
            // Each step reads the probabilities of both children before the bit is known and keeps the right one,
            // so the next step doesn't wait for a load that depends on this bit.
            uint symbol = 1;
            uint p;
            if (state < LzmaModel.LiteralStates)
            {
                p = Unsafe.Add(ref literal, 1);
                do
                {
                    uint p0 = Unsafe.Add(ref literal, symbol * 2);
                    uint p1 = Unsafe.Add(ref literal, symbol * 2 + 1);
                    var one = BitMask<TInput>(p, ref Unsafe.Add(ref literal, symbol), ref range, ref code, ref input, ref inPos, ref inEnd, rc);
                    p = p0 ^ ((p0 ^ p1) & one);
                    symbol = (symbol << 1) - one;
                }
                while (symbol < 0x100);
            }
            else
            {
                // After a match, while the bits equal those of the byte at the last distance, the probabilities for
                // that bit value are used; after the first difference, the plain ones.
                uint match = _window[ByteAt(pos, _rep0, _window.Length)];
                var offset = 0x100u;
                match <<= 1;
                var index = offset + (match & offset) + symbol;
                p = Unsafe.Add(ref literal, index);
                do
                {
                    // The next index for a 0 (the offset stays where the match bit is 0) and for a 1 (where it is 1).
                    var after = match << 1;
                    var offset0 = offset & ~match;
                    var offset1 = offset & match;
                    var index0 = offset0 + (after & offset0) + symbol * 2;
                    var index1 = offset1 + (after & offset1) + symbol * 2 + 1;
                    uint p0 = Unsafe.Add(ref literal, index0);
                    uint p1 = Unsafe.Add(ref literal, index1);
                    var one = BitMask<TInput>(p, ref Unsafe.Add(ref literal, index), ref range, ref code, ref input, ref inPos, ref inEnd, rc);
                    p = p0 ^ ((p0 ^ p1) & one);
                    index = index0 ^ ((index0 ^ index1) & one);
                    offset = offset0 ^ ((offset0 ^ offset1) & one);
                    symbol = (symbol << 1) - one;
                    match = after;
                }
                while (symbol < 0x100);
            }
            Unsafe.Add(ref win, pos++) = (byte)symbol;
            state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;

            if (pos >= stop || (!TInput.Checked && inPos > inEnd - InputMargin))
            {
                next = Stopped;
                break;
            }
            var posState = (pos + totalBase) & _pbMask;
            if (Bit<TInput>(ref Unsafe.Add(ref probs, IsMatchAt + (state << 4) + posState), ref range, ref code, ref input, ref inPos, ref inEnd, rc) != 0)
            {
                next = MatchNext;
                break;
            }
        }
        _pos = pos;
        _state = state;
        rc.Put(range, code, inPos);
        return next;
    }

    /// <summary>
    /// Decodes matches and repeats (the first one's IsMatch bit was read), each copied to the window up to Run's stop
    /// (the rest stays pending), until a literal comes (<see cref="LiteralNext"/>), Run has to stop
    /// (<see cref="Stopped"/>) or the end marker (<see cref="EndMarker"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int MatchRun<TInput>(RangeDecoder rc, bool allowEndMarker) where TInput : struct, IInputMode
    {
        var window = _window;
        var pos = _pos;
        var stop = _runStop;
        var totalBase = _runTotalBase;
        var pbMask = _pbMask;
        ref var probs = ref MemoryMarshal.GetArrayDataReference(_probabilities);
        var state = _state;
        uint rep0 = _rep0, rep1 = _rep1, rep2 = _rep2, rep3 = _rep3;
        ref var input = ref MemoryMarshal.GetArrayDataReference(rc.Buffer);
        var inEnd = rc.End;
        rc.Take(out var range, out var code, out var inPos);
        int next;
        while (true)
        {
            var posState = (pos + totalBase) & pbMask;
            var shortRep = false;
            uint length = 0;
            int lengthModel;
            if (Bit<TInput>(ref Unsafe.Add(ref probs, IsRepAt + state), ref range, ref code, ref input, ref inPos, ref inEnd, rc) != 0)
            {
                if (pos == 0 && !_full)
                    throw new InvalidDataException("the LZMA data repeats bytes before any were written");
                lengthModel = RepLengthAt;
                if (Bit<TInput>(ref Unsafe.Add(ref probs, IsRepG0At + state), ref range, ref code, ref input, ref inPos, ref inEnd, rc) == 0)
                {
                    // Rep0Long 0: a short repeat, one byte from the last distance.
                    shortRep = Bit<TInput>(ref Unsafe.Add(ref probs, IsRep0LongAt + (state << 4) + posState), ref range, ref code, ref input, ref inPos, ref inEnd, rc) == 0;
                }
                else
                {
                    uint distance;
                    if (Bit<TInput>(ref Unsafe.Add(ref probs, IsRepG1At + state), ref range, ref code, ref input, ref inPos, ref inEnd, rc) == 0)
                    {
                        distance = rep1;
                    }
                    else
                    {
                        if (Bit<TInput>(ref Unsafe.Add(ref probs, IsRepG2At + state), ref range, ref code, ref input, ref inPos, ref inEnd, rc) == 0)
                        {
                            distance = rep2;
                        }
                        else
                        {
                            distance = rep3;
                            rep3 = rep2;
                        }
                        rep2 = rep1;
                    }
                    rep1 = rep0;
                    rep0 = distance;
                }
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                lengthModel = MatchLengthAt;
            }

            if (shortRep)
            {
                state = state < LzmaModel.LiteralStates ? 9 : 11;
                window[pos] = window[ByteAt(pos, rep0, window.Length)];
                pos++;
            }
            else
            {
                // The length, from the match or the repeat length model.
                ref var model = ref Unsafe.Add(ref probs, lengthModel);
                if (Bit<TInput>(ref model, ref range, ref code, ref input, ref inPos, ref inEnd, rc) == 0)
                {
                    length = Tree<TInput>(ref Unsafe.Add(ref model, LowAt + (posState << LengthModel.LowBits)), LengthModel.LowBits, ref range, ref code, ref input, ref inPos, ref inEnd, rc);
                }
                else if (Bit<TInput>(ref Unsafe.Add(ref model, Choice2At), ref range, ref code, ref input, ref inPos, ref inEnd, rc) == 0)
                {
                    length = LengthModel.LowSymbols + Tree<TInput>(ref Unsafe.Add(ref model, MidAt + (posState << LengthModel.MidBits)), LengthModel.MidBits, ref range, ref code, ref input, ref inPos, ref inEnd, rc);
                }
                else
                {
                    length = LengthModel.LowSymbols + LengthModel.MidSymbols + Tree<TInput>(ref Unsafe.Add(ref model, HighAt), LengthModel.HighBits, ref range, ref code, ref input, ref inPos, ref inEnd, rc);
                }

                if (lengthModel == RepLengthAt)
                {
                    state = state < LzmaModel.LiteralStates ? 8 : 11;
                }
                else
                {
                    state = state < LzmaModel.LiteralStates ? 7 : 10;
                    rep0 = DecodeDistance<TInput>(ref probs, length, ref range, ref code, ref input, ref inPos, ref inEnd, rc);
                    if (rep0 == 0xFFFFFFFF)
                    {
                        if (!allowEndMarker)
                            throw new InvalidDataException("the LZMA data has an end marker where none is allowed");
                        next = EndMarker;
                        break;
                    }
                }
                if (rep0 >= (_full ? (uint)window.Length : (uint)pos))
                    throw new InvalidDataException("the LZMA data refers to bytes before the start of the window");
                var matchLength = (int)length + LzmaModel.MatchMinLength;
                var fits = Math.Min(matchLength, stop - pos);
                _pending = matchLength - fits;
                pos = Copy(window, pos, (int)rep0 + 1, fits);
            }

            if (pos >= stop || (!TInput.Checked && inPos > inEnd - InputMargin))
            {
                next = Stopped;
                break;
            }
            posState = (pos + totalBase) & pbMask;
            if (Bit<TInput>(ref Unsafe.Add(ref probs, IsMatchAt + (state << 4) + posState), ref range, ref code, ref input, ref inPos, ref inEnd, rc) == 0)
            {
                next = LiteralNext;
                break;
            }
        }
        _pos = pos;
        _state = state;
        _rep0 = rep0;
        _rep1 = rep1;
        _rep2 = rep2;
        _rep3 = rep3;
        rc.Put(range, code, inPos);
        return next;
    }

    /// <summary>The index of the byte <paramref name="distance"/> + 1 before <paramref name="pos"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ByteAt(int pos, uint distance, int windowLength)
    {
        var index = pos - (long)distance - 1;
        return index < 0 ? index + windowLength : index;
    }

    /// <summary>
    /// Copies <paramref name="count"/> bytes from <paramref name="distance"/> back to <paramref name="pos"/> (which
    /// has room for them) and returns the new position. The distance was checked against the window, so the source
    /// lies inside it; only the source may wrap around its end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Copy(byte[] window, int pos, int distance, int count)
    {
        var windowLength = window.Length;
        var source = pos - distance;
        if (source < 0)
            source += windowLength;
        ref var win = ref MemoryMarshal.GetArrayDataReference(window);
        if (source + count <= windowLength)
        {
            // A forward copy is what LZMA means. It equals a block move when source and target don't overlap or the
            // source lies after the target; 8 bytes at a time are the same while each read lies 8 or more bytes behind
            // the write (or after it).
            var ahead = source > pos;
            if (count >= 32 && (ahead || distance >= count))
            {
                window.AsSpan(source, count).CopyTo(window.AsSpan(pos, count)); // a move: the source may overlap after it
            }
            else if (ahead || distance >= 8)
            {
                var i = 0;
                for (; i + 8 <= count; i += 8)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref win, pos + i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref win, source + i)));
                for (; i < count; i++)
                    Unsafe.Add(ref win, pos + i) = Unsafe.Add(ref win, source + i);
            }
            else if (distance == 1)
            {
                Unsafe.InitBlockUnaligned(ref Unsafe.Add(ref win, pos), Unsafe.Add(ref win, source), (uint)count);
            }
            else
            {
                for (var i = 0; i < count; i++)
                    Unsafe.Add(ref win, pos + i) = Unsafe.Add(ref win, source + i);
            }
        }
        else
        {
            // The source runs over the end of the window (only once it has wrapped): byte by byte.
            for (var i = 0; i < count; i++)
            {
                Unsafe.Add(ref win, pos + i) = Unsafe.Add(ref win, source);
                if (++source == windowLength)
                    source = 0;
            }
        }
        return pos + count;
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

    // ───────── Bits ─────────
    // The helpers below take the coder's locals by ref and are inlined into the run methods, so the locals stay in
    // registers.

    /// <summary>How input bytes are read: <see cref="UncheckedInput"/> while enough are at hand, else checked.</summary>
    private interface IInputMode
    {
        static abstract bool Checked { get; }
    }

    private readonly struct UncheckedInput : IInputMode
    {
        public static bool Checked => false;
    }

    private readonly struct CheckedInput : IInputMode
    {
        public static bool Checked => true;
    }

    /// <summary>More than the most input bytes one symbol reads (48: one per bit, and a match has at most 48 bits).</summary>
    private const int InputMargin = 64;

    /// <summary>The next input byte; checked reads refill an empty buffer (or, for a whole chunk, report damage).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Next<TInput>(ref byte input, ref int inPos, ref int inEnd, RangeDecoder rc) where TInput : struct, IInputMode
    {
        if (TInput.Checked && inPos >= inEnd)
        {
            inEnd = rc.Refill();
            inPos = 0;
        }
        // Unchecked: the symbol started at most End - InputMargin. Checked: inPos < inEnd. Either way inside the buffer.
        return Unsafe.Add(ref input, inPos++);
    }

    /// <summary>One adaptive bit, with a branch on its value: for the decisions that steer the decoder.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Bit<TInput>(ref ushort probability, ref uint range, ref uint code, ref byte input, ref int inPos, ref int inEnd, RangeDecoder rc)
        where TInput : struct, IInputMode
    {
        uint p = probability;
        var bound = (range >> RangeCoder.ProbabilityBits) * p;
        uint bit;
        if (code < bound)
        {
            range = bound;
            probability = (ushort)(p + ((RangeCoder.ProbabilityOne - p) >> RangeCoder.MoveBits));
            bit = 0;
        }
        else
        {
            range -= bound;
            code -= bound;
            probability = (ushort)(p - (p >> RangeCoder.MoveBits));
            bit = 1;
        }
        if (range < Top)
        {
            range <<= 8;
            code = code << 8 | Next<TInput>(ref input, ref inPos, ref inEnd, rc);
        }
        return bit;
    }

    /// <summary>
    /// One adaptive bit without a branch on its value: returns all ones for a 1 and 0 for a 0, so a tree index moves
    /// on with <c>(m &lt;&lt; 1) − mask</c>. The probability update is the same as in <see cref="Bit{TInput}"/>:
    /// p − ((p − 2017) &gt;&gt; 5) with an arithmetic shift is p + ((2048 − p) &gt;&gt; 5) for every p in 0 … 2048.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BitMask<TInput>(ref ushort probability, ref uint range, ref uint code, ref byte input, ref int inPos, ref int inEnd, RangeDecoder rc)
        where TInput : struct, IInputMode =>
        BitMask<TInput>(probability, ref probability, ref range, ref code, ref input, ref inPos, ref inEnd, rc);

    /// <summary><see cref="BitMask{TInput}(ref ushort, ref uint, ref uint, ref byte, ref int, ref int, RangeDecoder)"/> with the probability already read into <paramref name="p"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BitMask<TInput>(uint p, ref ushort probability, ref uint range, ref uint code, ref byte input, ref int inPos, ref int inEnd, RangeDecoder rc)
        where TInput : struct, IInputMode
    {
        var bound = (range >> RangeCoder.ProbabilityBits) * p;
        var one = (uint)(((long)bound - code - 1) >> 63); // code >= bound
        range = bound + ((range - 2 * bound) & one); // bound for a 0, range − bound for a 1
        code -= bound & one;
        probability = (ushort)(p - (uint)((int)(p - (~one & (RangeCoder.ProbabilityOne - (1 << RangeCoder.MoveBits) + 1))) >> RangeCoder.MoveBits));
        if (range < Top)
        {
            range <<= 8;
            code = code << 8 | Next<TInput>(ref input, ref inPos, ref inEnd, rc);
        }
        return one;
    }

    /// <summary>A <paramref name="bits"/>-bit symbol from a binary tree of probabilities (index 1 is the root).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Tree<TInput>(ref ushort probs, int bits, ref uint range, ref uint code, ref byte input, ref int inPos, ref int inEnd, RangeDecoder rc)
        where TInput : struct, IInputMode
    {
        uint m = 1;
        for (var i = 0; i < bits; i++)
            m = (m << 1) - BitMask<TInput>(ref Unsafe.Add(ref probs, m), ref range, ref code, ref input, ref inPos, ref inEnd, rc);
        return m - (1u << bits);
    }

    /// <summary>Like <see cref="Tree{TInput}"/>, least significant bit first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReverseTree<TInput>(ref ushort probs, int bits, ref uint range, ref uint code, ref byte input, ref int inPos, ref int inEnd, RangeDecoder rc)
        where TInput : struct, IInputMode
    {
        uint m = 1;
        uint symbol = 0;
        for (var i = 0; i < bits; i++)
        {
            var bit = 0u - BitMask<TInput>(ref Unsafe.Add(ref probs, m), ref range, ref code, ref input, ref inPos, ref inEnd, rc);
            m = (m << 1) + bit;
            symbol |= bit << i;
        }
        return symbol;
    }

    /// <summary>A match distance minus 1 (0xFFFFFFFF is the end marker): slot, then reverse-coded or direct bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint DecodeDistance<TInput>(ref ushort probs, uint length, ref uint range, ref uint code, ref byte input, ref int inPos, ref int inEnd, RangeDecoder rc)
        where TInput : struct, IInputMode
    {
        var lengthState = (int)Math.Min(length, LzmaModel.LengthStates - 1);
        var slot = Tree<TInput>(ref Unsafe.Add(ref probs, PosSlotAt + (lengthState << LzmaModel.PosSlotBits)), LzmaModel.PosSlotBits, ref range, ref code, ref input, ref inPos, ref inEnd, rc);
        if (slot < LzmaModel.StartPosModelIndex)
            return slot;
        var footerBits = (int)(slot >> 1) - 1;
        var distance = (2 | (slot & 1)) << footerBits;
        if (slot < LzmaModel.EndPosModelIndex)
            return distance + ReverseTree<TInput>(ref Unsafe.Add(ref probs, PosSpecialAt + (int)(distance - slot)), footerBits, ref range, ref code, ref input, ref inPos, ref inEnd, rc);
        // Direct bits of probability one half, then the last four through the align tree.
        uint direct = 0;
        for (var count = footerBits - LzmaModel.AlignBits; count > 0; count--)
        {
            range >>= 1;
            code -= range;
            var t = 0u - (code >> 31); // all ones when the subtraction went below zero (bit 0)
            code += range & t;
            if (code == range)
                throw new InvalidDataException("the LZMA data is damaged");
            if (range < Top)
            {
                range <<= 8;
                code = code << 8 | Next<TInput>(ref input, ref inPos, ref inEnd, rc);
            }
            direct = (direct << 1) + (t + 1);
        }
        distance += direct << LzmaModel.AlignBits;
        return distance + ReverseTree<TInput>(ref Unsafe.Add(ref probs, AlignAt), LzmaModel.AlignBits, ref range, ref code, ref input, ref inPos, ref inEnd, rc);
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
