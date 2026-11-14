// One bzip2 block, as the bzip2 format describes it (Archives/Bzip2Writer feeds it the block after the first
// run-length step): the Burrows-Wheeler transform (Bzip2Sort), move-to-front with the zero runs written as RUNA / RUNB
// digits, and two to six Huffman tables, each group of 50 symbols coded with the one its selector names. The tables
// start from slices of the symbol range of about equal weight and are refined a few times: every group picks the
// table that codes it shortest, and each table is rebuilt from the groups that picked it. Code lengths stay at 17
// bits or less, which every bzip2 decoder takes. The result is a bit string (Bzip2Bits) that starts with the block
// magic; blocks are not byte aligned, so the writer joins them bit by bit.

namespace Filee.Engines.Archives;

/// <summary>Encodes bzip2 blocks; reusable, one block at a time.</summary>
internal sealed class Bzip2BlockEncoder
{
    /// <summary>The largest block bzip2 has ("BZh9").</summary>
    public const int MaxBlock = 900_000;

    private const int GroupSize = 50;
    private const int MaxCodeLength = 17;
    private const int MaxTables = 6;
    private const int MaxAlphabet = 258;
    private const int RunA = 0;
    private const int RunB = 1;

    private byte[] _bwt = [];
    private ushort[] _symbols = [];
    private byte[] _selectors = [];
    private readonly byte[][] _lengths = CreateTables<byte>();
    private readonly uint[][] _codes = CreateTables<uint>();
    private readonly int[][] _tableFrequencies = CreateTables<int>();

    /// <summary>The last block's bits, from its magic to its last Huffman code.</summary>
    public Bzip2Bits Output { get; } = new();

    private static T[][] CreateTables<T>()
    {
        var tables = new T[MaxTables][];
        for (var i = 0; i < MaxTables; i++)
            tables[i] = new T[MaxAlphabet];
        return tables;
    }

    /// <param name="block">The block after the first run-length step (1 … 900,000 bytes).</param>
    /// <param name="crc">The block CRC of the bytes before that step.</param>
    /// <param name="passes">How often the Huffman tables are refined (1 or more).</param>
    public void Encode(ReadOnlySpan<byte> block, uint crc, int passes, CancellationToken cancellationToken)
    {
        var n = block.Length;
        // The origin pointer has 24 bits and the selector count 15: both fit for 900,000 bytes, not for much more.
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1, nameof(block));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, MaxBlock, nameof(block));
        if (_bwt.Length < n)
        {
            _bwt = new byte[n];
            _symbols = new ushort[n + 1];
            _selectors = new byte[n / GroupSize + 1];
        }
        var origin = Bzip2Sort.Transform(block, _bwt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // The bytes the block uses, numbered in order: the move-to-front list and the symbols count only those.
        Span<bool> used = stackalloc bool[256];
        used.Clear();
        foreach (var b in block)
            used[b] = true;
        Span<byte> number = stackalloc byte[256];
        var inUse = 0;
        for (var i = 0; i < 256; i++)
        {
            if (used[i])
                number[i] = (byte)inUse++;
        }
        var alphabet = inUse + 2;
        var endOfBlock = inUse + 1;

        Span<int> frequencies = stackalloc int[MaxAlphabet];
        frequencies.Clear();
        var count = MoveToFront(_bwt.AsSpan(0, n), number, inUse, endOfBlock, frequencies);

        var tables = count < 200 ? 2 : count < 600 ? 3 : count < 1200 ? 4 : count < 2400 ? 5 : 6;
        var selectorCount = (count + GroupSize - 1) / GroupSize;
        ChooseTables(count, alphabet, tables, selectorCount, frequencies, passes);
        cancellationToken.ThrowIfCancellationRequested();

        var bits = Output;
        bits.Clear();
        bits.Write(24, 0x314159);
        bits.Write(24, 0x265359);
        bits.Write(32, crc);
        bits.Write(1, 0); // not randomised
        bits.Write(24, (uint)origin);

        // The used bytes as a 16 × 16 map: which rows of 16 have any, then each such row.
        uint rows = 0;
        for (var row = 0; row < 16; row++)
        {
            if (used.Slice(row * 16, 16).Contains(true))
                rows |= 0x8000u >> row;
        }
        bits.Write(16, rows);
        for (var row = 0; row < 16; row++)
        {
            if ((rows & (0x8000u >> row)) == 0)
                continue;
            uint mask = 0;
            for (var j = 0; j < 16; j++)
            {
                if (used[row * 16 + j])
                    mask |= 0x8000u >> j;
            }
            bits.Write(16, mask);
        }

        bits.Write(3, (uint)tables);
        bits.Write(15, (uint)selectorCount);
        // Selectors: move-to-front over the table numbers, each written in unary (that many 1s, then a 0).
        Span<byte> order = stackalloc byte[MaxTables];
        for (var t = 0; t < tables; t++)
            order[t] = (byte)t;
        for (var i = 0; i < selectorCount; i++)
        {
            var selector = _selectors[i];
            var j = order.IndexOf(selector);
            order[..j].CopyTo(order[1..]);
            order[0] = selector;
            bits.Write(j + 1, ((1u << j) - 1) << 1);
        }

        // Code lengths: a 5-bit start, then per symbol "10" (one longer) or "11" (one shorter) until it fits, then "0".
        for (var t = 0; t < tables; t++)
        {
            var lengths = _lengths[t];
            int current = lengths[0];
            bits.Write(5, (uint)current);
            for (var v = 0; v < alphabet; v++)
            {
                for (; current < lengths[v]; current++)
                    bits.Write(2, 2);
                for (; current > lengths[v]; current--)
                    bits.Write(2, 3);
                bits.Write(1, 0);
            }
            AssignCodes(lengths, _codes[t], alphabet);
        }

        var symbols = _symbols;
        for (var g = 0; g < selectorCount; g++)
        {
            var lengths = _lengths[_selectors[g]];
            var codes = _codes[_selectors[g]];
            var end = Math.Min(count, (g + 1) * GroupSize);
            for (var i = g * GroupSize; i < end; i++)
            {
                var symbol = symbols[i];
                bits.Write(lengths[symbol], codes[symbol]);
            }
        }
        bits.Finish();
    }

    /// <summary>
    /// Move-to-front over the transformed block into <see cref="_symbols"/>: position 0 repeated is a run, written in
    /// bijective base 2 with RUNA (1) and RUNB (2), lowest digit first; position j is symbol j + 1. Ends with the
    /// end-of-block symbol. Returns the number of symbols.
    /// </summary>
    private int MoveToFront(ReadOnlySpan<byte> bwt, ReadOnlySpan<byte> number, int inUse, int endOfBlock, Span<int> frequencies)
    {
        Span<byte> list = stackalloc byte[256];
        for (var i = 0; i < inUse; i++)
            list[i] = (byte)i;
        var symbols = _symbols;
        var count = 0;
        var run = 0;
        foreach (var b in bwt)
        {
            var value = number[b];
            if (list[0] == value)
            {
                run++;
                continue;
            }
            if (run > 0)
            {
                count = WriteRun(run, count, frequencies);
                run = 0;
            }
            var j = list[1..inUse].IndexOf(value) + 1;
            list[..j].CopyTo(list[1..]);
            list[0] = value;
            symbols[count++] = (ushort)(j + 1);
            frequencies[j + 1]++;
        }
        if (run > 0)
            count = WriteRun(run, count, frequencies);
        symbols[count++] = (ushort)endOfBlock;
        frequencies[endOfBlock]++;
        return count;
    }

    private int WriteRun(int run, int count, Span<int> frequencies)
    {
        var symbols = _symbols;
        run--;
        while (true)
        {
            var digit = (run & 1) == 0 ? RunA : RunB;
            symbols[count++] = (ushort)digit;
            frequencies[digit]++;
            if (run < 2)
                return count;
            run = (run - 2) >> 1;
        }
    }

    /// <summary>Picks the selectors and the tables' code lengths.</summary>
    private void ChooseTables(int count, int alphabet, int tables, int selectorCount, ReadOnlySpan<int> frequencies, int passes)
    {
        // Start: table t is cheap for one slice of the symbols (by number), dear for the rest.
        var remaining = count;
        var low = 0;
        for (var t = 0; t < tables; t++)
        {
            var target = remaining / (tables - t);
            var high = low - 1;
            var sum = 0;
            while (sum < target && high < alphabet - 1)
                sum += frequencies[++high];
            var lengths = _lengths[t];
            for (var v = 0; v < alphabet; v++)
                lengths[v] = (byte)(v >= low && v <= high ? 0 : 15);
            low = high + 1;
            remaining -= sum;
        }

        Span<int> cost = stackalloc int[MaxTables];
        var symbols = _symbols;
        for (var pass = 0; pass < passes; pass++)
        {
            for (var t = 0; t < tables; t++)
                Array.Clear(_tableFrequencies[t], 0, alphabet);
            for (var g = 0; g < selectorCount; g++)
            {
                var start = g * GroupSize;
                var end = Math.Min(count, start + GroupSize);
                cost.Clear();
                for (var i = start; i < end; i++)
                {
                    var symbol = symbols[i];
                    for (var t = 0; t < tables; t++)
                        cost[t] += _lengths[t][symbol];
                }
                var best = 0;
                for (var t = 1; t < tables; t++)
                {
                    if (cost[t] < cost[best])
                        best = t;
                }
                _selectors[g] = (byte)best;
                var chosen = _tableFrequencies[best];
                for (var i = start; i < end; i++)
                    chosen[symbols[i]]++;
            }
            for (var t = 0; t < tables; t++)
                Bzip2Huffman.Lengths(_tableFrequencies[t].AsSpan(0, alphabet), _lengths[t].AsSpan(0, alphabet), MaxCodeLength);
        }
    }

    /// <summary>Canonical codes: shorter first, and in symbol order within a length (as decoders rebuild them).</summary>
    private static void AssignCodes(byte[] lengths, uint[] codes, int alphabet)
    {
        uint code = 0;
        for (var length = 1; length <= MaxCodeLength; length++)
        {
            for (var v = 0; v < alphabet; v++)
            {
                if (lengths[v] == length)
                    codes[v] = code++;
            }
            code <<= 1;
        }
    }
}

/// <summary>Huffman code lengths with a length limit.</summary>
internal static class Bzip2Huffman
{
    /// <summary>
    /// Code lengths (1 … <paramref name="maxLength"/>) for <paramref name="frequencies"/>; every symbol gets one, unused
    /// ones as if seen once. A tree deeper than the limit is built again from flattened frequencies.
    /// </summary>
    public static void Lengths(ReadOnlySpan<int> frequencies, Span<byte> lengths, int maxLength)
    {
        var n = frequencies.Length;
        Span<int> weights = stackalloc int[n];
        for (var i = 0; i < n; i++)
            weights[i] = Math.Max(frequencies[i], 1);
        Span<int> keys = stackalloc int[n];
        Span<int> leaves = stackalloc int[n];
        Span<int> nodeWeight = stackalloc int[2 * n];
        Span<int> parent = stackalloc int[2 * n];
        Span<byte> depth = stackalloc byte[2 * n];
        while (true)
        {
            weights.CopyTo(keys);
            for (var i = 0; i < n; i++)
                leaves[i] = i;
            keys.Sort(leaves);

            // Two queues: the sorted leaves, and the merged nodes, which come out in order of weight by themselves.
            for (var i = 0; i < n; i++)
                nodeWeight[i] = keys[i];
            int leaf = 0, inner = n, next = n;
            for (var step = 0; step < n - 1; step++)
            {
                var a = leaf < n && (inner >= next || nodeWeight[leaf] <= nodeWeight[inner]) ? leaf++ : inner++;
                var b = leaf < n && (inner >= next || nodeWeight[leaf] <= nodeWeight[inner]) ? leaf++ : inner++;
                nodeWeight[next] = nodeWeight[a] + nodeWeight[b];
                parent[a] = next;
                parent[b] = next;
                next++;
            }
            var root = next - 1;
            depth[root] = 0;
            var deepest = 0;
            for (var node = root - 1; node >= 0; node--)
            {
                depth[node] = (byte)Math.Min(depth[parent[node]] + 1, 255);
                if (node < n)
                    deepest = Math.Max(deepest, depth[node]);
            }
            if (deepest <= maxLength)
            {
                for (var i = 0; i < n; i++)
                    lengths[leaves[i]] = depth[i];
                return;
            }
            for (var i = 0; i < n; i++)
                weights[i] = 1 + weights[i] / 2;
        }
    }
}
