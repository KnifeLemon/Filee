// LZFSE, Apple's compressor (libcompression's COMPRESSION_LZFSE, the chunks of a ULFO disk image), decoded in-process.
// Written from the format as Apple's open-source reference (BSD-licensed) describes it.
//
// A stream is a run of blocks, each starting with a little-endian magic, ended by "bvx$":
//   "bvx-": raw, n_raw u32, then the bytes.
//   "bvxn": LZVN, n_raw u32, n_payload u32, then LZVN opcodes (below) ending with the 8-byte end opcode.
//   "bvx1" / "bvx2": FSE-coded literals and (L, M, D) triples. bvx1 stores its header in full (n_raw, n_payload,
//     n_literals, n_matches, n_literal_payload, n_lmd_payload u32, literal_bits i32, literal states 4 × u16, lmd_bits
//     i32, L, M, D states u16, then the L, M, D and literal frequencies as u16: 772 bytes with the C layout's two
//     bytes of padding). bvx2 packs the counts and states into three u64 (see ReadV2) and the frequencies into a
//     prefix code (see Frequency), header size given in the third u64.
// FSE here spreads no states: a symbol of frequency f owns f consecutive states of the table (literals 1024 states
// over 256 symbols; L and M 64 over 20; D 256 over 64), and leaving a state reads k or k-1 bits, k being the shift
// that brings f to between half and all of the table. Literals are four interleaved decoders on one bit stream; L,
// M and D are each a symbol (an FSE state) plus that symbol's extra bits, read together. Bit streams are read
// backwards: the last byte holds the first bits, most significant first, and literal_bits / lmd_bits (−8 to 0) say
// how many top bits of the last byte are padding. Literals and match distances may reach back into earlier blocks of
// the same stream; a distance of 0 repeats the previous one. A block holds at most 40,000 literals and 10,000 matches.
//
// Every length, distance and count is checked against the bytes actually there and the room left in the output; a
// damaged stream ends in InvalidDataException, never in a read or write outside the buffers.

using System.Buffers.Binary;
using System.Numerics;

namespace Filee.Engines.Archives;

/// <summary>Decodes LZFSE streams (raw, LZVN and FSE blocks) into a buffer whose size the caller knows.</summary>
internal sealed class LzfseDecoder
{
    private const uint EndOfStream = 0x24787662, RawBlock = 0x2d787662, V1Block = 0x31787662, V2Block = 0x32787662,
        LzvnBlock = 0x6e787662;

    private const int V1HeaderSize = 772, V2FixedSize = 32;
    private const int LiteralStates = 1024, LStates = 64, MStates = 64, DStates = 256;
    private const int LiteralSymbols = 256, LSymbols = 20, MSymbols = 20, DSymbols = 64;
    private const int MaxLiterals = 40000, MaxMatches = 10000;

    private static readonly byte[] LExtra = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 5, 8];
    private static readonly byte[] MExtra = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 8, 11];
    private static readonly byte[] DExtra = Enumerable.Range(0, DSymbols).Select(i => (byte)(i >> 2)).ToArray();
    private static readonly int[] LBase = Bases(LExtra), MBase = Bases(MExtra), DBase = Bases(DExtra);

    // The block being decoded; kept between calls so a DMG's chunks reuse them.
    private readonly LiteralEntry[] _literalTable = new LiteralEntry[LiteralStates];
    private readonly ValueEntry[] _lTable = new ValueEntry[LStates];
    private readonly ValueEntry[] _mTable = new ValueEntry[MStates];
    private readonly ValueEntry[] _dTable = new ValueEntry[DStates];
    private readonly ushort[] _frequencies = new ushort[LSymbols + MSymbols + DSymbols + LiteralSymbols];
    private readonly byte[] _literals = new byte[MaxLiterals + 4];

    private struct LiteralEntry
    {
        public byte Bits;
        public byte Symbol;
        public short Next;
    }

    private struct ValueEntry
    {
        public byte TotalBits;
        public byte ExtraBits;
        public short Next;
        public int Base;
    }

    /// <summary>What a bvx1 / bvx2 header holds.</summary>
    private struct Header
    {
        public int Literals, Matches, LiteralPayload, LmdPayload, LiteralBits, LmdBits;
        public int Literal0, Literal1, Literal2, Literal3, L, M, D;
    }

    /// <summary>Each extra-bits table's base values: every symbol starts where the one before it ends.</summary>
    private static int[] Bases(byte[] extra)
    {
        var bases = new int[extra.Length];
        for (var i = 1; i < extra.Length; i++)
            bases[i] = bases[i - 1] + (1 << extra[i - 1]);
        return bases;
    }

    /// <summary>
    /// Decodes the LZFSE stream <paramref name="source"/> (up to and including its end block) into
    /// <paramref name="destination"/>; returns the bytes written. Throws <see cref="InvalidDataException"/> when the
    /// stream is damaged or would write more than <paramref name="destination"/> holds.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var at = 0;
        var written = 0;
        while (true)
        {
            if (source.Length - at < 4)
                throw Damaged("the stream ends without its end block");
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(source[at..]);
            switch (magic)
            {
                case EndOfStream:
                    return written;
                case RawBlock:
                    {
                        var raw = Field(source, at + 4);
                        if (raw > source.Length - at - 8)
                            throw Damaged("a raw block is cut short");
                        Room(destination, written, raw);
                        source.Slice(at + 8, raw).CopyTo(destination[written..]);
                        written += raw;
                        at += 8 + raw;
                        break;
                    }
                case LzvnBlock:
                    {
                        var raw = Field(source, at + 4);
                        var payload = Field(source, at + 8);
                        if (payload > source.Length - at - 12)
                            throw Damaged("an LZVN block is cut short");
                        Room(destination, written, raw);
                        Lzvn(source.Slice(at + 12, payload), destination, written, written + raw);
                        written += raw;
                        at += 12 + payload;
                        break;
                    }
                case V1Block or V2Block:
                    {
                        var (header, size) = magic == V1Block ? ReadV1(source[at..]) : ReadV2(source[at..]);
                        var literalsAt = at + size;
                        if (header.LiteralPayload > source.Length - literalsAt
                            || header.LmdPayload > source.Length - literalsAt - header.LiteralPayload)
                            throw Damaged("a block is cut short");
                        Tables();
                        DecodeLiterals(source[..(literalsAt + header.LiteralPayload)], literalsAt, header);
                        var lmdAt = literalsAt + header.LiteralPayload;
                        written = DecodeMatches(source.Slice(lmdAt, header.LmdPayload), header, destination, written);
                        at = lmdAt + header.LmdPayload;
                        break;
                    }
                default:
                    throw Damaged("unknown block");
            }
        }
    }

    private static InvalidDataException Damaged(string detail) => new("damaged LZFSE data: " + detail);

    private static int Field(ReadOnlySpan<byte> source, int at)
    {
        if (source.Length - at < 4)
            throw Damaged("a block header is cut short");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(source[at..]);
        return value <= int.MaxValue ? (int)value : throw Damaged("a block is larger than the stream");
    }

    private static void Room(Span<byte> destination, int written, int bytes)
    {
        if (bytes > destination.Length - written)
            throw Damaged("it unpacks to more than its chunk holds");
    }

    // ───────────────────────── Headers ─────────────────────────

    private (Header, int Size) ReadV1(ReadOnlySpan<byte> block)
    {
        if (block.Length < V1HeaderSize)
            throw Damaged("a block header is cut short");
        var header = new Header
        {
            Literals = Count(block, 12),
            Matches = Count(block, 16),
            LiteralPayload = Count(block, 20),
            LmdPayload = Count(block, 24),
            LiteralBits = BinaryPrimitives.ReadInt32LittleEndian(block[28..]),
            Literal0 = U16(block, 32),
            Literal1 = U16(block, 34),
            Literal2 = U16(block, 36),
            Literal3 = U16(block, 38),
            LmdBits = BinaryPrimitives.ReadInt32LittleEndian(block[40..]),
            L = U16(block, 44),
            M = U16(block, 46),
            D = U16(block, 48),
        };
        for (var i = 0; i < _frequencies.Length; i++)
            _frequencies[i] = BinaryPrimitives.ReadUInt16LittleEndian(block[(50 + 2 * i)..]);
        Check(header);
        return (header, V1HeaderSize);
    }

    private static int U16(ReadOnlySpan<byte> block, int at) => BinaryPrimitives.ReadUInt16LittleEndian(block[at..]);

    private static int Count(ReadOnlySpan<byte> block, int at)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(block[at..]);
        return value <= 1 << 24 ? (int)value : throw Damaged("a block header is out of range");
    }

    /// <summary>
    /// bvx2: after the magic and n_raw, u64 0 = n_literals (20 bits), n_literal_payload (20), n_matches (20),
    /// literal_bits + 7 (3); u64 1 = the four literal states (10 bits each), n_lmd_payload (20), lmd_bits + 7 (3);
    /// u64 2 = header size (32), L, M, D states (10 each); then the frequencies up to the header size.
    /// </summary>
    private (Header, int Size) ReadV2(ReadOnlySpan<byte> block)
    {
        if (block.Length < V2FixedSize)
            throw Damaged("a block header is cut short");
        var v0 = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
        var v1 = BinaryPrimitives.ReadUInt64LittleEndian(block[16..]);
        var v2 = BinaryPrimitives.ReadUInt64LittleEndian(block[24..]);
        static int Bits(ulong v, int at, int count) => (int)((v >> at) & ((1UL << count) - 1));
        var header = new Header
        {
            Literals = Bits(v0, 0, 20),
            LiteralPayload = Bits(v0, 20, 20),
            Matches = Bits(v0, 40, 20),
            LiteralBits = Bits(v0, 60, 3) - 7,
            Literal0 = Bits(v1, 0, 10),
            Literal1 = Bits(v1, 10, 10),
            Literal2 = Bits(v1, 20, 10),
            Literal3 = Bits(v1, 30, 10),
            LmdPayload = Bits(v1, 40, 20),
            LmdBits = Bits(v1, 60, 3) - 7,
            L = Bits(v2, 32, 10),
            M = Bits(v2, 42, 10),
            D = Bits(v2, 52, 10),
        };
        var size = (long)(uint)v2;
        if (size < V2FixedSize || size > block.Length)
            throw Damaged("a block header is cut short");
        ReadFrequencies(block[V2FixedSize..(int)size]);
        Check(header);
        return (header, (int)size);
    }

    /// <summary>
    /// The 360 frequencies of a bvx2 header (L, M, D, literals), each a prefix code read from the low bits up: ...0 is
    /// 0 or 1 (2 bits), ..01 is 2 or 3 (3 bits), .011 is 4 to 7 (5 bits), 0111 is 8 to 23 (8 bits), 1111 is 24 to
    /// 1047 (14 bits). No frequencies at all means every one is zero; otherwise the codes fill the bytes to the last.
    /// </summary>
    private void ReadFrequencies(ReadOnlySpan<byte> codes)
    {
        if (codes.IsEmpty)
        {
            Array.Clear(_frequencies);
            return;
        }
        uint bits = 0;
        var count = 0;
        var at = 0;
        for (var i = 0; i < _frequencies.Length; i++)
        {
            while (at < codes.Length && count <= 24)
            {
                bits |= (uint)codes[at++] << count;
                count += 8;
            }
            var (length, value) = (bits & 1) == 0 ? (2, (int)(bits >> 1) & 1)
                : (bits & 3) == 1 ? (3, 2 + ((int)(bits >> 2) & 1))
                : (bits & 7) == 3 ? (5, 4 + ((int)(bits >> 3) & 3))
                : (bits & 15) == 7 ? (8, 8 + ((int)(bits >> 4) & 15))
                : (14, 24 + ((int)(bits >> 4) & 1023));
            if (length > count)
                throw Damaged("its frequency tables are cut short");
            _frequencies[i] = (ushort)value;
            bits >>= length;
            count -= length;
        }
        if (count >= 8 || at != codes.Length)
            throw Damaged("its frequency tables don't fill their header");
    }

    private static void Check(Header header)
    {
        if (header.Literals > MaxLiterals || header.Matches > MaxMatches)
            throw Damaged("a block holds more literals or matches than the format allows");
        if (header.LiteralBits is < -8 or > 0 || header.LmdBits is < -8 or > 0)
            throw Damaged("a block's bit counts are out of range");
        if (header.Literal0 >= LiteralStates || header.Literal1 >= LiteralStates || header.Literal2 >= LiteralStates
            || header.Literal3 >= LiteralStates || header.L >= LStates || header.M >= MStates || header.D >= DStates)
            throw Damaged("a block starts in a state its tables don't have");
    }

    // ───────────────────────── FSE tables ─────────────────────────

    private void Tables()
    {
        var f = _frequencies.AsSpan();
        Values(f[..LSymbols], LExtra, LBase, _lTable);
        Values(f.Slice(LSymbols, MSymbols), MExtra, MBase, _mTable);
        Values(f.Slice(LSymbols + MSymbols, DSymbols), DExtra, DBase, _dTable);
        Literals(f[(LSymbols + MSymbols + DSymbols)..]);
    }

    /// <summary>
    /// Where each state of a symbol with frequency <paramref name="frequency"/> goes: its <paramref name="index"/>-th
    /// state reads <c>bits</c> bits and adds them to <c>next</c>. The first states read one bit more than the rest so
    /// that, together, the symbol's states reach every state of the table exactly once.
    /// </summary>
    private static (int Bits, int Next) Step(int states, int frequency, int index)
    {
        var shift = BitOperations.LeadingZeroCount((uint)frequency) - BitOperations.LeadingZeroCount((uint)states);
        var wide = (2 * states >> shift) - frequency;
        return index < wide
            ? (shift, ((frequency + index) << shift) - states)
            : (shift - 1, (index - wide) << (shift - 1));
    }

    /// <summary>Frequencies that sum to more states than the table has can't be decoded; fewer leave states unused.</summary>
    private static void CheckSum(ReadOnlySpan<ushort> frequencies, int states)
    {
        var sum = 0;
        foreach (var f in frequencies)
            sum += f;
        if (sum > states)
            throw Damaged("its frequencies sum to more than its table");
    }

    private void Literals(ReadOnlySpan<ushort> frequencies)
    {
        CheckSum(frequencies, LiteralStates);
        Array.Clear(_literalTable);
        var state = 0;
        for (var symbol = 0; symbol < frequencies.Length; symbol++)
        {
            for (var i = 0; i < frequencies[symbol]; i++)
            {
                var (bits, next) = Step(LiteralStates, frequencies[symbol], i);
                _literalTable[state++] = new LiteralEntry { Bits = (byte)bits, Symbol = (byte)symbol, Next = (short)next };
            }
        }
    }

    private static void Values(ReadOnlySpan<ushort> frequencies, byte[] extra, int[] bases, ValueEntry[] table)
    {
        CheckSum(frequencies, table.Length);
        Array.Clear(table);
        var state = 0;
        for (var symbol = 0; symbol < frequencies.Length; symbol++)
        {
            for (var i = 0; i < frequencies[symbol]; i++)
            {
                var (bits, next) = Step(table.Length, frequencies[symbol], i);
                table[state++] = new ValueEntry
                {
                    TotalBits = (byte)(bits + extra[symbol]),
                    ExtraBits = extra[symbol],
                    Next = (short)next,
                    Base = bases[symbol],
                };
            }
        }
    }

    // ───────────────────────── Bit streams ─────────────────────────

    /// <summary>A bit stream read from its end towards <c>start</c>, most significant bits first.</summary>
    private ref struct Backwards
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _start;
        private int _at;
        private ulong _bits;
        private int _count;

        /// <param name="padding">literal_bits / lmd_bits: minus the number of padding bits atop the last byte.</param>
        public Backwards(ReadOnlySpan<byte> data, int start, int padding)
        {
            _data = data;
            _start = start;
            _at = data.Length;
            Fill();
            if (padding == 0)
                return;
            if (-padding > _count || (_bits >> (_count + padding)) != 0)
                throw Damaged("a bit stream's padding isn't zero");
            _count += padding;
        }

        private void Fill()
        {
            while (_count <= 56 && _at > _start)
            {
                _bits = (_bits << 8) | _data[--_at];
                _count += 8;
            }
        }

        public int Read(int count)
        {
            if (count == 0)
                return 0;
            if (count > _count)
            {
                Fill();
                if (count > _count)
                    throw Damaged("a bit stream is cut short");
            }
            _count -= count;
            var value = (int)(_bits >> _count);
            _bits &= (1UL << _count) - 1;
            return value;
        }
    }

    private void DecodeLiterals(ReadOnlySpan<byte> payload, int start, Header header)
    {
        var input = new Backwards(payload, start, header.LiteralBits);
        Span<int> states = [header.Literal0, header.Literal1, header.Literal2, header.Literal3];
        var table = _literalTable;
        for (var i = 0; i < header.Literals; i += 4)
        {
            for (var s = 0; s < 4; s++)
            {
                var entry = table[states[s]];
                _literals[i + s] = entry.Symbol;
                states[s] = entry.Next + input.Read(entry.Bits);
            }
        }
    }

    private static int Value(ref int state, ValueEntry[] table, ref Backwards input)
    {
        var entry = table[state];
        var bits = input.Read(entry.TotalBits);
        state = entry.Next + (bits >> entry.ExtraBits);
        return entry.Base + (bits & ((1 << entry.ExtraBits) - 1));
    }

    private int DecodeMatches(ReadOnlySpan<byte> payload, Header header, Span<byte> destination, int written)
    {
        var input = new Backwards(payload, 0, header.LmdBits);
        var (l, m, d) = (header.L, header.M, header.D);
        var literal = 0;
        var distance = -1; // no previous distance yet
        for (var i = 0; i < header.Matches; i++)
        {
            var length = Value(ref l, _lTable, ref input);
            var match = Value(ref m, _mTable, ref input);
            var next = Value(ref d, _dTable, ref input);
            if (next != 0)
                distance = next;
            if (length > header.Literals - literal)
                throw Damaged("a block uses more literals than it holds");
            if (distance < 0 || distance > written + length)
                throw Damaged("a match reaches back before the start");
            Room(destination, written, length + match);
            _literals.AsSpan(literal, length).CopyTo(destination[written..]);
            literal += length;
            written += length;
            Copy(destination, written, distance, match);
            written += match;
        }
        return written;
    }

    /// <summary>Copies <paramref name="length"/> bytes from <paramref name="distance"/> back, byte by byte where they overlap.</summary>
    private static void Copy(Span<byte> destination, int at, int distance, int length)
    {
        if (length == 0)
            return;
        if (distance >= length)
        {
            destination.Slice(at - distance, length).CopyTo(destination[at..]);
            return;
        }
        for (var i = 0; i < length; i++)
            destination[at + i] = destination[at + i - distance];
    }

    // ───────────────────────── LZVN ─────────────────────────

    /// <summary>
    /// One LZVN block, written from <paramref name="at"/> to exactly <paramref name="end"/>. Each opcode copies L
    /// literals that follow it, then M bytes from D back (L, M, D as below; a missing D repeats the previous one):
    /// <list type="bullet">
    /// <item>LLMMMDDD dddddddd (D &lt; 0x600): small distance; L 0–3, M 3–10. Low bits 110 keep the previous D
    /// (1 byte); low bits 111 take D from the next two bytes (little endian, 3 bytes).</item>
    /// <item>101LLMMM then 16 bits MM + 14 bits D (little endian): medium distance, M 3–34.</item>
    /// <item>1110LLLL: L 1–15 literals; 11100000 llllllll: 16–271. 1111MMMM: a match of M 1–15 at the previous D;
    /// 11110000 mmmmmmmm: 16–271.</item>
    /// <item>00000110 and 7 more bytes: end of the block. 00001110, 00010110: nothing. 0111xxxx, 1101xxxx and
    /// 00xxx110 above 00010110: invalid.</item>
    /// </list>
    /// The opcode layout leaves L 1 with M up to 8, L 2 with M up to 6 and L 3 with M up to 4.
    /// </summary>
    private static void Lzvn(ReadOnlySpan<byte> source, Span<byte> destination, int at, int end)
    {
        var p = 0;
        var distance = 0;
        while (true)
        {
            if (p >= source.Length)
                throw Damaged("an LZVN block ends without its end opcode");
            var op = source[p];
            int literals, match;
            var size = 1;
            if (op == 0x06)
            {
                if (source.Length - p != 8 || at != end)
                    throw Damaged("an LZVN block's size doesn't match its contents");
                return;
            }
            if (op is 0x0E or 0x16)
            {
                p++;
                continue;
            }
            if (op >= 0xF0)
            {
                literals = 0;
                match = op & 15;
                if (op == 0xF0)
                    (match, size) = (Byte(source, p + 1) + 16, 2);
            }
            else if (op >= 0xE0)
            {
                match = 0;
                literals = op & 15;
                if (op == 0xE0)
                    (literals, size) = (Byte(source, p + 1) + 16, 2);
            }
            else if ((op & 0xF0) is 0x70 or 0xD0 || (op & 0xC7) == 0x06)
            {
                throw Damaged("an LZVN block has an invalid opcode");
            }
            else if ((op & 0xE0) == 0xA0)
            {
                literals = (op >> 3) & 3;
                var word = Byte(source, p + 1) | Byte(source, p + 2) << 8;
                match = ((op & 7) << 2 | (word & 3)) + 3;
                distance = word >> 2;
                size = 3;
            }
            else
            {
                literals = op >> 6;
                match = ((op >> 3) & 7) + 3;
                switch (op & 7)
                {
                    case 7:
                        distance = Byte(source, p + 1) | Byte(source, p + 2) << 8;
                        size = 3;
                        break;
                    case 6:
                        break;
                    default:
                        distance = (op & 7) << 8 | Byte(source, p + 1);
                        size = 2;
                        break;
                }
            }
            p += size;
            if (literals > source.Length - p || literals > end - at)
                throw Damaged("an LZVN block's literals run past its end");
            source.Slice(p, literals).CopyTo(destination[at..]);
            p += literals;
            at += literals;
            if (match == 0)
                continue;
            if (distance == 0 || distance > at)
                throw Damaged("a match reaches back before the start");
            if (match > end - at)
                throw Damaged("an LZVN block's matches run past its end");
            Copy(destination, at, distance, match);
            at += match;
        }
    }

    private static int Byte(ReadOnlySpan<byte> source, int at) =>
        at < source.Length ? source[at] : throw Damaged("an LZVN block is cut short");
}
