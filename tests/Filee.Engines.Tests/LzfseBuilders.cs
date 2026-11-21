// Writes LZFSE streams in code for the LZFSE and DMG tests: raw ("bvx-"), LZVN ("bvxn") and FSE blocks with
// compressed ("bvx2") or full ("bvx1") headers. A greedy match finder and frequencies normalized the simple way, so
// the output is larger than Apple's encoder makes, but it uses every kind of opcode and symbol the decoder reads.
// Written from the format's description (Apple's open-source reference), independently of Filee.Engines' LzfseDecoder.

using System.Buffers.Binary;
using System.Numerics;

namespace Filee.Engines.Tests;

/// <summary>How <see cref="LzfseBuilders.Encode"/> writes each block.</summary>
public enum LzfseBlock
{
    Raw,
    Lzvn,
    FseV2,
    FseV1,
}

internal static class LzfseBuilders
{
    private static readonly byte[] LExtra = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 5, 8];
    private static readonly byte[] MExtra = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 8, 11];
    private static readonly byte[] DExtra = Enumerable.Range(0, 64).Select(i => (byte)(i / 4)).ToArray();

    private static readonly int[] LBase = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 28, 60];
    private static readonly int[] MBase = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 24, 56, 312];
    private static readonly int[] DBase =
    [
        0, 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 28, 36, 44, 52, 60, 76, 92, 108, 124, 156, 188, 220, 252, 316, 380, 444,
        508, 636, 764, 892, 1020, 1276, 1532, 1788, 2044, 2556, 3068, 3580, 4092, 5116, 6140, 7164, 8188, 10236, 12284,
        14332, 16380, 20476, 24572, 28668, 32764, 40956, 49148, 57340, 65532, 81916, 98300, 114684, 131068, 163836,
        196604, 229372,
    ];

    /// <summary>
    /// <paramref name="data"/> as an LZFSE stream of blocks of <paramref name="blockSize"/> bytes, written in turn
    /// as <paramref name="kinds"/> says, then the end block. Matches may reach back into earlier blocks.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> data, LzfseBlock[] kinds, int blockSize = 32768)
    {
        var output = new MemoryStream();
        var finder = new MatchFinder(data.ToArray());
        var n = 0;
        for (var start = 0; start < data.Length; start += blockSize)
        {
            var end = Math.Min(data.Length, start + blockSize);
            var kind = kinds[n++ % kinds.Length];
            switch (kind)
            {
                case LzfseBlock.Raw:
                    output.Write("bvx-"u8);
                    WriteU32(output, end - start);
                    output.Write(data[start..end]);
                    break;
                case LzfseBlock.Lzvn:
                    var payload = Lzvn(finder, start, end);
                    output.Write("bvxn"u8);
                    WriteU32(output, end - start);
                    WriteU32(output, payload.Length);
                    output.Write(payload);
                    break;
                default:
                    output.Write(Fse(finder, start, end, kind == LzfseBlock.FseV1));
                    break;
            }
        }
        output.Write("bvx$"u8);
        return output.ToArray();
    }

    private static void WriteU32(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    // ───────────────────────── Matches ─────────────────────────

    /// <summary>The latest earlier position with the same four bytes, followed as far as it matches.</summary>
    private sealed class MatchFinder(byte[] data)
    {
        private readonly Dictionary<int, int> _last = [];

        public byte[] Data => data;

        /// <summary>Records position <paramref name="at"/> and returns the match it starts, if any.</summary>
        public (int Length, int Distance) Find(int at, int end, int maxDistance, int maxLength)
        {
            if (end - at < 4)
                return (0, 0);
            var key = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
            _last.TryGetValue(key, out var earlier);
            var found = _last.ContainsKey(key);
            _last[key] = at;
            if (!found || at - earlier > maxDistance)
                return (0, 0);
            var length = 0;
            while (at + length < end && length < maxLength && data[earlier + length] == data[at + length])
                length++;
            return (length, at - earlier);
        }

        public void Skip(int at, int end)
        {
            if (end - at >= 4)
                _last[BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at))] = at;
        }
    }

    /// <summary>The block as (literal count, match length, distance) triples; the last one may have no match.</summary>
    private static List<(int L, int M, int D)> Parse(MatchFinder finder, int start, int end, int maxDistance, int minMatch)
    {
        var triples = new List<(int, int, int)>();
        var literals = 0;
        var at = start;
        while (at < end)
        {
            var (length, distance) = finder.Find(at, end, maxDistance, int.MaxValue);
            if (length < minMatch)
            {
                literals++;
                at++;
                continue;
            }
            triples.Add((literals, length, distance));
            literals = 0;
            for (var i = 1; i < length; i++)
                finder.Skip(at + i, end);
            at += length;
        }
        if (literals > 0)
            triples.Add((literals, 0, 0));
        return triples;
    }

    // ───────────────────────── LZVN ─────────────────────────

    private static int MaxMatch(int literals) => literals switch { 0 => 10, 1 => 8, 2 => 6, _ => 4 };

    private static byte[] Lzvn(MatchFinder finder, int start, int end)
    {
        var data = finder.Data;
        var output = new MemoryStream();
        output.WriteByte(0x0E); // a no-op, which the decoder skips
        var at = start;
        var previous = 0;
        void Literals(int count)
        {
            while (count > 0)
            {
                var n = Math.Min(count, 271);
                if (n < 16)
                {
                    output.WriteByte((byte)(0xE0 | n));
                }
                else
                {
                    output.WriteByte(0xE0);
                    output.WriteByte((byte)(n - 16));
                }
                output.Write(data, at, n);
                at += n;
                count -= n;
            }
        }
        foreach (var (l, m, d) in Parse(finder, start, end, 0xFFFF, 3))
        {
            var folded = l <= 3 ? l : 0;
            Literals(l - folded);
            if (m == 0)
            {
                Literals(folded);
                break;
            }
            int first;
            if (d == previous && folded == 0)
            {
                first = 0; // 00MMM110 would be the end or a no-op: a match alone at the same distance is 1111MMMM
            }
            else if (d == previous)
            {
                first = Math.Min(m, MaxMatch(folded));
                output.WriteByte((byte)(folded << 6 | (first - 3) << 3 | 6));
            }
            else if (d < 0x600)
            {
                first = Math.Min(m, MaxMatch(folded));
                output.WriteByte((byte)(folded << 6 | (first - 3) << 3 | d >> 8));
                output.WriteByte((byte)d);
            }
            else if (d < 0x4000)
            {
                first = Math.Min(m, 34);
                output.WriteByte((byte)(0xA0 | folded << 3 | (first - 3) >> 2));
                var word = (first - 3) & 3 | d << 2;
                output.WriteByte((byte)word);
                output.WriteByte((byte)(word >> 8));
            }
            else
            {
                first = Math.Min(m, MaxMatch(folded));
                output.WriteByte((byte)(folded << 6 | (first - 3) << 3 | 7));
                output.WriteByte((byte)d);
                output.WriteByte((byte)(d >> 8));
            }
            output.Write(data, at, folded);
            at += folded + first;
            previous = d;
            for (var rest = m - first; rest > 0;)
            {
                var n = Math.Min(rest, 271);
                if (n < 16)
                {
                    output.WriteByte((byte)(0xF0 | n));
                }
                else
                {
                    output.WriteByte(0xF0);
                    output.WriteByte((byte)(n - 16));
                }
                at += n;
                rest -= n;
            }
        }
        output.Write([0x06, 0, 0, 0, 0, 0, 0, 0]);
        return output.ToArray();
    }

    // ───────────────────────── FSE ─────────────────────────

    /// <summary>Bits written forwards, low first; the decoder reads them from the end.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private ulong _bits;
        private int _count;

        public long Total { get; private set; }

        public void Write(int count, long value)
        {
            Total += count;
            for (var i = 0; i < count; i++)
            {
                _bits |= (ulong)((value >> i) & 1) << _count++;
                if (_count == 8)
                {
                    _bytes.Add((byte)_bits);
                    (_bits, _count) = (0, 0);
                }
            }
        }

        /// <summary>The bytes, and minus the number of unused bits atop the last one.</summary>
        public (byte[] Bytes, int Padding) Finish()
        {
            var padding = 0;
            if (_count > 0)
            {
                padding = _count - 8;
                _bytes.Add((byte)_bits);
            }
            return ([.. _bytes], padding);
        }
    }

    private static int Symbol(int value, int[] bases)
    {
        var symbol = bases.Length - 1;
        while (bases[symbol] > value)
            symbol--;
        return symbol;
    }

    /// <summary>
    /// Scales <paramref name="counts"/> to frequencies that sum to <paramref name="states"/>; every used symbol keeps
    /// one. With no symbols at all, every state goes to the first (as Apple's encoder does; 7-Zip wants full tables).
    /// </summary>
    private static int[] Normalize(int[] counts, int states)
    {
        var total = counts.Sum();
        var frequencies = counts.Select(c => c == 0 ? 0 : Math.Max(1, (int)((long)c * states / total))).ToArray();
        if (total == 0)
        {
            frequencies[0] = states;
            return frequencies;
        }
        var sum = frequencies.Sum();
        while (sum > states)
        {
            var i = Array.IndexOf(frequencies, frequencies.Max());
            frequencies[i]--;
            sum--;
        }
        frequencies[Array.IndexOf(frequencies, frequencies.Max())] += states - sum;
        return frequencies;
    }

    /// <summary>One FSE coding: the frequencies and, per symbol, its states' bits and next-state bases.</summary>
    private sealed class Coding
    {
        private readonly int[] _offsets;
        private readonly int[] _frequencies;
        private readonly int _states;

        public Coding(int[] frequencies, int states)
        {
            _frequencies = frequencies;
            _states = states;
            _offsets = new int[frequencies.Length];
            for (var i = 1; i < frequencies.Length; i++)
                _offsets[i] = _offsets[i - 1] + frequencies[i - 1];
        }

        public int[] Frequencies => _frequencies;

        /// <summary>
        /// The state that decodes <paramref name="symbol"/> and then moves to <paramref name="next"/>, and the bits it
        /// reads to get there. State j of a symbol with frequency f reads k bits (k − 1 past the first 2N/2^k − f) and
        /// adds them to a base; the bases of a symbol's states tile 0 to N.
        /// </summary>
        public (int State, int Bits, int Value) Before(int symbol, int next)
        {
            var f = _frequencies[symbol];
            var k = BitOperations.LeadingZeroCount((uint)f) - BitOperations.LeadingZeroCount((uint)_states);
            var wide = (2 * _states >> k) - f;
            for (var j = 0; j < f; j++)
            {
                var (bits, start) = j < wide ? (k, ((f + j) << k) - _states) : (k - 1, (j - wide) << (k - 1));
                if (next >= start && next < start + (1 << bits))
                    return (_offsets[symbol] + j, bits, next - start);
            }
            throw new InvalidOperationException("no state reaches " + next);
        }
    }

    private static byte[] Fse(MatchFinder finder, int start, int end, bool fullHeader)
    {
        var data = finder.Data;
        // Split what no symbol can hold: literal runs past 315, matches past 2359.
        var triples = new List<(int L, int M, int D)>();
        foreach (var (l, m, d) in Parse(finder, start, end, 262139, 4))
        {
            var (literals, match) = (l, m);
            while (literals > 315)
            {
                triples.Add((315, 0, 1));
                literals -= 315;
            }
            triples.Add((literals, Math.Min(match, 2359), match == 0 ? 1 : d));
            for (match -= Math.Min(match, 2359); match > 0; match -= Math.Min(match, 2359))
                triples.Add((0, Math.Min(match, 2359), d));
        }
        var literalBytes = new List<byte>();
        var at = start;
        foreach (var (l, m, _) in triples)
        {
            literalBytes.AddRange(data.AsSpan(at, l));
            at += l + m;
        }
        while (literalBytes.Count % 4 != 0)
            literalBytes.Add(0);

        // Symbols: a distance equal to the one before is written as 0, "the same again".
        var values = new List<(int L, int M, int D)>();
        var previous = -1;
        foreach (var (l, m, d) in triples)
        {
            values.Add((l, m, d == previous ? 0 : d));
            previous = d;
        }
        int[] Count(IEnumerable<int> symbols, int size)
        {
            var counts = new int[size];
            foreach (var s in symbols)
                counts[s]++;
            return counts;
        }
        var lCoding = new Coding(Normalize(Count(values.Select(v => Symbol(v.L, LBase)), 20), 64), 64);
        var mCoding = new Coding(Normalize(Count(values.Select(v => Symbol(v.M, MBase)), 20), 64), 64);
        var dCoding = new Coding(Normalize(Count(values.Select(v => Symbol(v.D, DBase)), 64), 256), 256);
        var literalCoding = new Coding(Normalize(Count(literalBytes.Select(b => (int)b), 256), 1024), 1024);

        // Literals: four decoders taking turns, coded last to first so the decoder reads them first to last; the
        // decoder ends exactly at the first bit. The L, M, D stream starts with eight zero bytes, below everything
        // the decoder reads. Both as Apple's encoder writes them (7-Zip checks that).
        var literalBits = new BitWriter();
        var literalStates = new int[4];
        for (var i = literalBytes.Count - 1; i >= 0; i--)
        {
            var (state, bits, value) = literalCoding.Before(literalBytes[i], literalStates[i % 4]);
            literalBits.Write(bits, value);
            literalStates[i % 4] = state;
        }
        var (literalPayload, literalPadding) = literalBits.Finish();

        var lmdBits = new BitWriter();
        lmdBits.Write(64, 0);
        int lState = 0, mState = 0, dState = 0;
        void Value(Coding coding, int[] bases, byte[] extra, int v, ref int state)
        {
            var symbol = Symbol(v, bases);
            var (before, bits, next) = coding.Before(symbol, state);
            lmdBits.Write(bits + extra[symbol], (long)next << extra[symbol] | (long)(v - bases[symbol]));
            state = before;
        }
        for (var i = values.Count - 1; i >= 0; i--)
        {
            Value(dCoding, DBase, DExtra, values[i].D, ref dState);
            Value(mCoding, MBase, MExtra, values[i].M, ref mState);
            Value(lCoding, LBase, LExtra, values[i].L, ref lState);
        }
        var (lmdPayload, lmdPadding) = lmdBits.Finish();

        var frequencies = lCoding.Frequencies.Concat(mCoding.Frequencies).Concat(dCoding.Frequencies).Concat(literalCoding.Frequencies).ToArray();
        var output = new MemoryStream();
        var header = fullHeader
            ? V1Header(end - start, literalBytes.Count, values.Count, literalPayload.Length, lmdPayload.Length,
                literalPadding, literalStates, lmdPadding, lState, mState, dState, frequencies)
            : V2Header(end - start, literalBytes.Count, values.Count, literalPayload.Length, lmdPayload.Length,
                literalPadding, literalStates, lmdPadding, lState, mState, dState, frequencies);
        output.Write(header);
        output.Write(literalPayload);
        output.Write(lmdPayload);
        return output.ToArray();
    }

    private static byte[] V1Header(int raw, int literals, int matches, int literalPayload, int lmdPayload,
        int literalPadding, int[] literalStates, int lmdPadding, int l, int m, int d, int[] frequencies)
    {
        var header = new byte[772];
        "bvx1"u8.CopyTo(header);
        var fields = new[] { raw, literalPayload + lmdPayload, literals, matches, literalPayload, lmdPayload, literalPadding };
        for (var i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4 + 4 * i), fields[i]);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32 + 2 * i), (ushort)literalStates[i]);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), lmdPadding);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(44), (ushort)l);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(46), (ushort)m);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(48), (ushort)d);
        for (var i = 0; i < frequencies.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(50 + 2 * i), (ushort)frequencies[i]);
        return header;
    }

    private static byte[] V2Header(int raw, int literals, int matches, int literalPayload, int lmdPayload,
        int literalPadding, int[] literalStates, int lmdPadding, int l, int m, int d, int[] frequencies)
    {
        // The frequencies as prefix codes, low bits first (see LzfseDecoder.ReadFrequencies for the code).
        var codes = new BitWriter();
        foreach (var f in frequencies)
        {
            var (length, code) = f switch
            {
                < 2 => (2, f << 1),
                < 4 => (3, 1 | (f - 2) << 2),
                < 8 => (5, 3 | (f - 4) << 3),
                < 24 => (8, 7 | (f - 8) << 4),
                _ => (14, 15 | (f - 24) << 4),
            };
            codes.Write(length, code);
        }
        var (table, _) = codes.Finish();
        var header = new byte[32 + table.Length];
        "bvx2"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), raw);
        static ulong U(int value) => (uint)value;
        var v0 = U(literals) | U(literalPayload) << 20 | U(matches) << 40 | U(literalPadding + 7) << 60;
        var v1 = U(literalStates[0]) | U(literalStates[1]) << 10 | U(literalStates[2]) << 20 | U(literalStates[3]) << 30
            | U(lmdPayload) << 40 | U(lmdPadding + 7) << 60;
        var v2 = U(header.Length) | U(l) << 32 | U(m) << 42 | U(d) << 52;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), v0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), v1);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), v2);
        table.CopyTo(header, 32);
        return header;
    }
}
