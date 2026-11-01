// A test-only LZX compressor, so CHM test inputs can be built in code. It writes what the decoder must read: verbatim,
// aligned offset and uncompressed blocks (in turn, so each kind is used), Huffman trees sent as pretree deltas with
// all three run codes, repeat offsets, blocks that cross frame boundaries, the 16-bit realignment after each 32 KB
// frame, state resets at a chosen interval and, optionally, Intel E8 translation. Matching is a simple greedy search.

namespace Filee.Engines.Tests;

internal static class LzxEncoder
{
    public const int FrameSize = 32768;

    public enum BlockKind
    {
        Verbatim = 1,
        Aligned = 2,
        Uncompressed = 3,
    }

    /// <param name="ResetFrames">Frames between resets (CHM uses 2 or 4); 0 for one stream without resets.</param>
    /// <param name="BlockSize">Bytes of output per block (blocks end at reset points too).</param>
    /// <param name="Kinds">Block kinds used in turn.</param>
    /// <param name="E8FileSize">Intel E8 translation with this file size; null for none.</param>
    public sealed record Options(int WindowBits = 16, int ResetFrames = 2, int BlockSize = 20_000, BlockKind[]? Kinds = null, int? E8FileSize = null);

    private static readonly int[] ExtraBits = new int[52];
    private static readonly int[] PositionBase = new int[51];

    static LzxEncoder()
    {
        for (int i = 0, j = 0; i < 51; i += 2)
        {
            ExtraBits[i] = ExtraBits[i + 1] = j;
            if (i != 0 && j < 17)
                j++;
        }
        for (int i = 0, j = 0; i < 51; i++)
        {
            PositionBase[i] = j;
            j += 1 << ExtraBits[i];
        }
    }

    private readonly record struct Token(int Length, int Offset); // Offset 0 = a literal

    /// <summary>The compressed stream and the compressed offset of every frame (the CHM reset table).</summary>
    public static (byte[] Data, List<long> FrameOffsets) Compress(byte[] data, Options options)
    {
        var kinds = options.Kinds ?? [BlockKind.Verbatim, BlockKind.Aligned, BlockKind.Uncompressed];
        var slots = options.WindowBits switch { 20 => 42, 21 => 50, _ => options.WindowBits * 2 };
        var mainSymbols = 256 + slots * 8;
        var input = options.E8FileSize is { } fileSize ? TranslateE8(data, fileSize) : data;
        var writer = new BitWriter();
        var frameOffsets = new List<long> { 0 };
        var segmentSize = options.ResetFrames > 0 ? options.ResetFrames * FrameSize : Math.Max(input.Length, 1);
        var kindIndex = 0;
        long frameEnd = FrameSize;

        for (var segmentStart = 0; segmentStart < input.Length || segmentStart == 0 && input.Length == 0; segmentStart += segmentSize)
        {
            var segmentEnd = Math.Min(input.Length, segmentStart + segmentSize);
            // Reset: the header again, empty trees, repeat offsets 1.
            var mainLengths = new byte[mainSymbols];
            var lengthLengths = new byte[249];
            var (r0, r1, r2) = (1, 1, 1);
            var previousKind = (BlockKind)0;
            var previousLength = 0;
            if (options.E8FileSize is { } e8)
            {
                writer.Write(1, 1);
                writer.Write((uint)e8 >> 16, 16);
                writer.Write((uint)e8 & 0xFFFF, 16);
            }
            else
            {
                writer.Write(0, 1);
            }

            var tokens = Tokenize(input, segmentStart, segmentEnd, 1 << options.WindowBits);
            var position = (long)segmentStart;
            for (var t = 0; t < tokens.Count;)
            {
                // Tokens of the next block.
                var first = t;
                var size = 0;
                while (t < tokens.Count && (size < options.BlockSize || t == first))
                    size += tokens[t++].Length;
                var kind = kinds[kindIndex++ % kinds.Length];
                if (options.E8FileSize is not null && kind == BlockKind.Uncompressed && first == 0 && segmentStart == 0)
                    kind = BlockKind.Verbatim; // the first block must start the E8 translation through the main tree

                if (previousKind == BlockKind.Uncompressed && (previousLength & 1) == 1)
                    writer.RawByte(0);
                writer.Write((uint)kind, 3);
                writer.Write((uint)size >> 8, 16);
                writer.Write((uint)size & 0xFF, 8);
                previousKind = kind;
                previousLength = size;

                if (kind == BlockKind.Uncompressed)
                {
                    if (writer.Aligned)
                        writer.Write(0, 16);
                    else
                        writer.PadToWord();
                    foreach (var r in new[] { r0, r1, r2 })
                        for (var b = 0; b < 4; b++)
                            writer.RawByte((byte)(r >> (8 * b)));
                    for (var i = 0; i < size; i++)
                    {
                        writer.RawByte(input[position]);
                        position++;
                        if (position == frameEnd)
                        {
                            frameEnd += FrameSize;
                            frameOffsets.Add(writer.Length);
                        }
                    }
                    continue;
                }

                // Symbols of the block (the repeat offsets change as the decoder will change them).
                var symbols = new List<(int Main, int Length, int Extra, int ExtraValue, int Aligned)>();
                var at = position;
                for (var i = first; i < t; i++)
                {
                    var token = tokens[i];
                    at += token.Length;
                    if (token.Offset == 0)
                    {
                        symbols.Add((input[at - 1], -1, 0, 0, -1));
                        continue;
                    }
                    int slot, extra = 0, extraValue = 0;
                    if (token.Offset == r0)
                    {
                        slot = 0;
                    }
                    else if (token.Offset == r1)
                    {
                        slot = 1;
                        (r0, r1) = (r1, r0);
                    }
                    else if (token.Offset == r2)
                    {
                        slot = 2;
                        (r0, r2) = (r2, r0);
                    }
                    else
                    {
                        var formatted = token.Offset + 2;
                        slot = 3;
                        while (slot + 1 < slots && PositionBase[slot + 1] <= formatted)
                            slot++;
                        extra = ExtraBits[slot];
                        extraValue = formatted - PositionBase[slot];
                        (r2, r1, r0) = (r1, r0, token.Offset);
                    }
                    var header = Math.Min(token.Length - 2, 7);
                    var aligned = kind == BlockKind.Aligned && extra >= 3 ? extraValue & 7 : -1;
                    symbols.Add((256 + slot * 8 + header, header == 7 ? token.Length - 9 : -1, extra, extraValue, aligned));
                }

                var mainFrequencies = new int[mainSymbols];
                var lengthFrequencies = new int[249];
                var alignedFrequencies = new int[8];
                foreach (var symbol in symbols)
                {
                    mainFrequencies[symbol.Main]++;
                    if (symbol.Length >= 0)
                        lengthFrequencies[symbol.Length]++;
                    if (symbol.Aligned >= 0)
                        alignedFrequencies[symbol.Aligned]++;
                }
                if (options.E8FileSize is not null)
                    mainFrequencies[0xE8]++;
                var newMain = HuffmanLengths(mainFrequencies, 16);
                var newLength = lengthFrequencies.Any(f => f > 0) ? HuffmanLengths(lengthFrequencies, 16) : new byte[249];
                byte[]? newAligned = null;
                if (kind == BlockKind.Aligned)
                {
                    newAligned = alignedFrequencies.Count(f => f > 0) >= 2 ? HuffmanLengths(alignedFrequencies, 7) : [3, 3, 3, 3, 3, 3, 3, 3];
                    foreach (var length in newAligned)
                        writer.Write(length, 3);
                }
                WriteLengths(writer, mainLengths, newMain, 0, 256);
                WriteLengths(writer, mainLengths, newMain, 256, mainSymbols);
                WriteLengths(writer, lengthLengths, newLength, 0, 249);
                var mainCodes = Codes(newMain);
                var lengthCodes = Codes(newLength);
                var alignedCodes = newAligned is null ? null : Codes(newAligned);

                for (var i = 0; i < symbols.Count; i++)
                {
                    var symbol = symbols[i];
                    writer.Write(mainCodes[symbol.Main].Code, mainCodes[symbol.Main].Length);
                    if (symbol.Length >= 0)
                        writer.Write(lengthCodes[symbol.Length].Code, lengthCodes[symbol.Length].Length);
                    if (symbol.Aligned >= 0)
                    {
                        if (symbol.Extra > 3)
                            writer.Write((uint)(symbol.ExtraValue >> 3), symbol.Extra - 3);
                        writer.Write(alignedCodes![symbol.Aligned].Code, alignedCodes[symbol.Aligned].Length);
                    }
                    else if (symbol.Extra > 0)
                    {
                        writer.Write((uint)symbol.ExtraValue, symbol.Extra);
                    }
                    position += tokens[first + i].Length;
                    if (position >= frameEnd)
                    {
                        writer.PadToWord(); // the decoder realigns after the symbol that ends a frame
                        frameEnd += FrameSize;
                        frameOffsets.Add(writer.Length);
                    }
                }
            }
            // The segment ends at a frame end (or the end of the data): the decoder realigns there.
            writer.PadToWord();
            if (input.Length == 0)
                break;
        }
        var bytes = writer.ToArray();
        return (bytes, frameOffsets.Take((input.Length + FrameSize - 1) / FrameSize).ToList());
    }

    /// <summary>Greedy matches of 2..257 bytes within the segment (and the window), preferring the last offset.</summary>
    private static List<Token> Tokenize(byte[] data, int start, int end, int window)
    {
        var tokens = new List<Token>();
        var heads = new Dictionary<int, int>();
        var previous = new int[end - start];
        var lastOffset = 0;
        for (var i = start; i < end;)
        {
            var best = (Length: 0, Offset: 0);
            if (lastOffset > 0 && i - lastOffset >= start)
            {
                var length = MatchLength(data, i - lastOffset, i, end);
                if (length >= 3)
                    best = (length, lastOffset);
            }
            if (i + 2 < end)
            {
                var key = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
                var candidate = heads.GetValueOrDefault(key, -1);
                for (var steps = 0; candidate >= start && i - candidate < window - 3 && steps < 32; steps++)
                {
                    var length = MatchLength(data, candidate, i, end);
                    if (length > best.Length)
                        best = (length, i - candidate);
                    candidate = previous[candidate - start];
                }
            }
            var take = best.Length >= 3 ? best.Length : 1;
            for (var k = 0; k < take; k++, i++)
            {
                if (i + 2 < end)
                {
                    var key = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
                    previous[i - start] = heads.GetValueOrDefault(key, -1);
                    heads[key] = i;
                }
            }
            tokens.Add(best.Length >= 3 ? new Token(best.Length, best.Offset) : new Token(1, 0));
            if (best.Length >= 3)
                lastOffset = best.Offset;
        }
        return tokens;
    }

    private static int MatchLength(byte[] data, int from, int at, int end)
    {
        var length = 0;
        while (length < 257 && at + length < end && data[from + length] == data[at + length])
            length++;
        return length;
    }

    /// <summary>Tree lengths as pretree deltas from <paramref name="previous"/>, which is then updated.</summary>
    private static void WriteLengths(BitWriter writer, byte[] previous, byte[] lengths, int first, int last)
    {
        var codes = new List<(int Symbol, int ExtraBits, int Extra)>();
        for (var x = first; x < last;)
        {
            var zeros = 0;
            while (x + zeros < last && lengths[x + zeros] == 0)
                zeros++;
            if (zeros >= 20)
            {
                var n = Math.Min(zeros, 51);
                codes.Add((18, 5, n - 20));
                x += n;
                continue;
            }
            if (zeros >= 4)
            {
                var n = Math.Min(zeros, 19);
                codes.Add((17, 4, n - 4));
                x += n;
                continue;
            }
            var same = 1;
            while (x + same < last && lengths[x + same] == lengths[x])
                same++;
            if (same >= 4)
            {
                var n = Math.Min(same, 5);
                codes.Add((19, 1, n - 4));
                codes.Add(((previous[x] - lengths[x] + 17) % 17, 0, 0));
                x += n;
                continue;
            }
            codes.Add(((previous[x] - lengths[x] + 17) % 17, 0, 0));
            x++;
        }
        var frequencies = new int[20];
        foreach (var code in codes)
            frequencies[code.Symbol]++;
        var pretree = HuffmanLengths(frequencies, 15);
        foreach (var length in pretree)
            writer.Write(length, 4);
        var pretreeCodes = Codes(pretree);
        foreach (var (symbol, extraBits, extra) in codes)
        {
            writer.Write(pretreeCodes[symbol].Code, pretreeCodes[symbol].Length);
            if (extraBits > 0)
                writer.Write((uint)extra, extraBits);
        }
        Array.Copy(lengths, first, previous, first, last - first);
    }

    /// <summary>Huffman code lengths of at most <paramref name="maxLength"/> bits (at least two codes, so the tree is complete).</summary>
    internal static byte[] HuffmanLengths(int[] frequencies, int maxLength)
    {
        var counts = frequencies.Select(f => (long)f).ToArray();
        if (counts.Count(c => c > 0) < 2)
        {
            var used = Array.FindIndex(counts, c => c > 0);
            counts[used == 0 ? 1 : 0] = 1;
            if (used < 0)
                counts[1] = 1;
        }
        while (true)
        {
            var lengths = Build(counts);
            if (lengths.Max() <= maxLength)
                return lengths;
            for (var i = 0; i < counts.Length; i++)
                if (counts[i] > 0)
                    counts[i] = (counts[i] + 1) / 2;
        }
    }

    private static byte[] Build(long[] counts)
    {
        var queue = new PriorityQueue<int, (long, int)>();
        var parent = new List<int>();
        var order = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            parent.Add(-1);
            if (counts[i] > 0)
                queue.Enqueue(i, (counts[i], order++));
        }
        var weights = counts.ToList();
        while (queue.Count > 1)
        {
            queue.TryDequeue(out var a, out var pa);
            queue.TryDequeue(out var b, out var pb);
            var node = parent.Count;
            parent.Add(-1);
            weights.Add(pa.Item1 + pb.Item1);
            parent[a] = node;
            parent[b] = node;
            queue.Enqueue(node, (pa.Item1 + pb.Item1, order++));
        }
        var lengths = new byte[counts.Length];
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
                continue;
            var depth = 0;
            for (var n = i; parent[n] >= 0; n = parent[n])
                depth++;
            lengths[i] = (byte)Math.Min(depth, 255);
        }
        return lengths;
    }

    /// <summary>Canonical codes: shorter codes first, symbols in order within a length.</summary>
    private static (uint Code, int Length)[] Codes(byte[] lengths)
    {
        var codes = new (uint, int)[lengths.Length];
        uint code = 0;
        for (var length = 1; length <= 16; length++)
        {
            for (var symbol = 0; symbol < lengths.Length; symbol++)
                if (lengths[symbol] == length)
                    codes[symbol] = (code++, length);
            code <<= 1;
        }
        return codes;
    }

    /// <summary>Intel E8 translation as the compressor applies it: relative call targets become absolute.</summary>
    private static byte[] TranslateE8(byte[] data, int fileSize)
    {
        var output = (byte[])data.Clone();
        for (var frame = 0; frame * FrameSize < output.Length && frame < 32768; frame++)
        {
            var start = frame * FrameSize;
            var size = Math.Min(FrameSize, output.Length - start);
            if (size <= 10)
                continue;
            var position = start;
            for (var i = 0; i < size - 10;)
            {
                if (output[start + i++] != 0xE8)
                {
                    position++;
                    continue;
                }
                var relative = BitConverter.ToInt32(output, start + i);
                if (relative >= -position && relative < fileSize)
                {
                    var absolute = relative < fileSize - position ? relative + position : relative - fileSize;
                    BitConverter.GetBytes(absolute).CopyTo(output, start + i);
                }
                i += 4;
                position += 5;
            }
        }
        return output;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private uint _word;
        private int _bits;

        public bool Aligned => _bits == 0;

        public int Length => _bytes.Count;

        public void Write(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                _word = (_word << 1) | ((value >> i) & 1);
                if (++_bits == 16)
                {
                    _bytes.Add((byte)_word);
                    _bytes.Add((byte)(_word >> 8));
                    _word = 0;
                    _bits = 0;
                }
            }
        }

        public void PadToWord()
        {
            if (_bits > 0)
                Write(0, 16 - _bits);
        }

        public void RawByte(byte value)
        {
            if (_bits != 0)
                throw new InvalidOperationException("Raw bytes need a word boundary.");
            _bytes.Add(value);
        }

        public byte[] ToArray()
        {
            PadToWord();
            return [.. _bytes];
        }
    }
}
