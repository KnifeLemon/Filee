// CCITT Group 4 (ITU-T T.6, "MMR") coding of 1-bit pictures: every row coded two-dimensionally against the row above
// it (an all-white row above the first) with the pass, horizontal and vertical modes of T.4's 2-D coding, the
// horizontal runs in T.4's modified Huffman white and black codes (make-up codes for runs of 64 and more, the extended
// make-up codes from 1792 to 2560 shared by both colours). The data ends with EOFB (two EOLs). No uncompressed mode.
//
// DjvuPdfWriter codes a page mask with it (PDF's /CCITTFaxDecode with /K -1) when that is smaller than JBIG2 and
// Flate. DjvuDocument reads Smmr masks (DjVu's MMR-coded alternative to JB2) with the decoder.

namespace Filee.Engines.Djvu;

internal static class CcittG4
{
    /// <summary>White run codes: (run, code bits, code length) — terminating 0–63, then make-up 64–1728.</summary>
    private static readonly (int Run, int Code, int Length)[] WhiteCodes =
    [
        (0, 0b00110101, 8), (1, 0b000111, 6), (2, 0b0111, 4), (3, 0b1000, 4), (4, 0b1011, 4), (5, 0b1100, 4),
        (6, 0b1110, 4), (7, 0b1111, 4), (8, 0b10011, 5), (9, 0b10100, 5), (10, 0b00111, 5), (11, 0b01000, 5),
        (12, 0b001000, 6), (13, 0b000011, 6), (14, 0b110100, 6), (15, 0b110101, 6), (16, 0b101010, 6),
        (17, 0b101011, 6), (18, 0b0100111, 7), (19, 0b0001100, 7), (20, 0b0001000, 7), (21, 0b0010111, 7),
        (22, 0b0000011, 7), (23, 0b0000100, 7), (24, 0b0101000, 7), (25, 0b0101011, 7), (26, 0b0010011, 7),
        (27, 0b0100100, 7), (28, 0b0011000, 7), (29, 0b00000010, 8), (30, 0b00000011, 8), (31, 0b00011010, 8),
        (32, 0b00011011, 8), (33, 0b00010010, 8), (34, 0b00010011, 8), (35, 0b00010100, 8), (36, 0b00010101, 8),
        (37, 0b00010110, 8), (38, 0b00010111, 8), (39, 0b00101000, 8), (40, 0b00101001, 8), (41, 0b00101010, 8),
        (42, 0b00101011, 8), (43, 0b00101100, 8), (44, 0b00101101, 8), (45, 0b00000100, 8), (46, 0b00000101, 8),
        (47, 0b00001010, 8), (48, 0b00001011, 8), (49, 0b01010010, 8), (50, 0b01010011, 8), (51, 0b01010100, 8),
        (52, 0b01010101, 8), (53, 0b00100100, 8), (54, 0b00100101, 8), (55, 0b01011000, 8), (56, 0b01011001, 8),
        (57, 0b01011010, 8), (58, 0b01011011, 8), (59, 0b01001010, 8), (60, 0b01001011, 8), (61, 0b00110010, 8),
        (62, 0b00110011, 8), (63, 0b00110100, 8),
        (64, 0b11011, 5), (128, 0b10010, 5), (192, 0b010111, 6), (256, 0b0110111, 7), (320, 0b00110110, 8),
        (384, 0b00110111, 8), (448, 0b01100100, 8), (512, 0b01100101, 8), (576, 0b01101000, 8), (640, 0b01100111, 8),
        (704, 0b011001100, 9), (768, 0b011001101, 9), (832, 0b011010010, 9), (896, 0b011010011, 9),
        (960, 0b011010100, 9), (1024, 0b011010101, 9), (1088, 0b011010110, 9), (1152, 0b011010111, 9),
        (1216, 0b011011000, 9), (1280, 0b011011001, 9), (1344, 0b011011010, 9), (1408, 0b011011011, 9),
        (1472, 0b010011000, 9), (1536, 0b010011001, 9), (1600, 0b010011010, 9), (1664, 0b011000, 6),
        (1728, 0b010011011, 9),
    ];

    /// <summary>Black run codes: (run, code bits, code length) — terminating 0–63, then make-up 64–1728.</summary>
    private static readonly (int Run, int Code, int Length)[] BlackCodes =
    [
        (0, 0b0000110111, 10), (1, 0b010, 3), (2, 0b11, 2), (3, 0b10, 2), (4, 0b011, 3), (5, 0b0011, 4),
        (6, 0b0010, 4), (7, 0b00011, 5), (8, 0b000101, 6), (9, 0b000100, 6), (10, 0b0000100, 7),
        (11, 0b0000101, 7), (12, 0b0000111, 7), (13, 0b00000100, 8), (14, 0b00000111, 8), (15, 0b000011000, 9),
        (16, 0b0000010111, 10), (17, 0b0000011000, 10), (18, 0b0000001000, 10), (19, 0b00001100111, 11),
        (20, 0b00001101000, 11), (21, 0b00001101100, 11), (22, 0b00000110111, 11), (23, 0b00000101000, 11),
        (24, 0b00000010111, 11), (25, 0b00000011000, 11), (26, 0b000011001010, 12), (27, 0b000011001011, 12),
        (28, 0b000011001100, 12), (29, 0b000011001101, 12), (30, 0b000001101000, 12), (31, 0b000001101001, 12),
        (32, 0b000001101010, 12), (33, 0b000001101011, 12), (34, 0b000011010010, 12), (35, 0b000011010011, 12),
        (36, 0b000011010100, 12), (37, 0b000011010101, 12), (38, 0b000011010110, 12), (39, 0b000011010111, 12),
        (40, 0b000001101100, 12), (41, 0b000001101101, 12), (42, 0b000011011010, 12), (43, 0b000011011011, 12),
        (44, 0b000001010100, 12), (45, 0b000001010101, 12), (46, 0b000001010110, 12), (47, 0b000001010111, 12),
        (48, 0b000001100100, 12), (49, 0b000001100101, 12), (50, 0b000001010010, 12), (51, 0b000001010011, 12),
        (52, 0b000000100100, 12), (53, 0b000000110111, 12), (54, 0b000000111000, 12), (55, 0b000000100111, 12),
        (56, 0b000000101000, 12), (57, 0b000001011000, 12), (58, 0b000001011001, 12), (59, 0b000000101011, 12),
        (60, 0b000000101100, 12), (61, 0b000001011010, 12), (62, 0b000001100110, 12), (63, 0b000001100111, 12),
        (64, 0b0000001111, 10), (128, 0b000011001000, 12), (192, 0b000011001001, 12), (256, 0b000001011011, 12),
        (320, 0b000000110011, 12), (384, 0b000000110100, 12), (448, 0b000000110101, 12),
        (512, 0b0000001101100, 13), (576, 0b0000001101101, 13), (640, 0b0000001001010, 13),
        (704, 0b0000001001011, 13), (768, 0b0000001001100, 13), (832, 0b0000001001101, 13),
        (896, 0b0000001110010, 13), (960, 0b0000001110011, 13), (1024, 0b0000001110100, 13),
        (1088, 0b0000001110101, 13), (1152, 0b0000001110110, 13), (1216, 0b0000001110111, 13),
        (1280, 0b0000001010010, 13), (1344, 0b0000001010011, 13), (1408, 0b0000001010100, 13),
        (1472, 0b0000001010101, 13), (1536, 0b0000001011010, 13), (1600, 0b0000001011011, 13),
        (1664, 0b0000001100100, 13), (1728, 0b0000001100101, 13),
    ];

    /// <summary>Make-up codes from 1792 to 2560, the same for both colours.</summary>
    private static readonly (int Run, int Code, int Length)[] ExtendedCodes =
    [
        (1792, 0b00000001000, 11), (1856, 0b00000001100, 11), (1920, 0b00000001101, 11),
        (1984, 0b000000010010, 12), (2048, 0b000000010011, 12), (2112, 0b000000010100, 12),
        (2176, 0b000000010101, 12), (2240, 0b000000010110, 12), (2304, 0b000000010111, 12),
        (2368, 0b000000011100, 12), (2432, 0b000000011101, 12), (2496, 0b000000011110, 12),
        (2560, 0b000000011111, 12),
    ];

    /// <summary>All codes of one colour, for tests: terminating, make-up and extended make-up.</summary>
    internal static IEnumerable<(int Run, int Code, int Length)> Codes(bool black) =>
        (black ? BlackCodes : WhiteCodes).Concat(ExtendedCodes);

    private const int Pass = -100;
    private const int Horizontal = -101;
    private const int EndOfLine = -102;

    /// <summary>Mode codes: (mode, code bits, length); a vertical mode is the offset of a1 from b1.</summary>
    private static readonly (int Mode, int Code, int Length)[] ModeCodes =
    [
        (0, 0b1, 1), (1, 0b011, 3), (-1, 0b010, 3), (Horizontal, 0b001, 3), (Pass, 0b0001, 4),
        (2, 0b000011, 6), (-2, 0b000010, 6), (3, 0b0000011, 7), (-3, 0b0000010, 7),
    ];

    /// <summary>The vertical mode codes by offset + 3.</summary>
    private static readonly (int Mode, int Code, int Length)[] Vertical = [.. ModeCodes.Where(m => m.Mode is >= -3 and <= 3).OrderBy(m => m.Mode)];

    private const int TableBits = 13;

    /// <summary>For each 13-bit window: (run, code length) of the run code it starts with; length 0 for none.</summary>
    private static readonly (short Run, byte Length)[] WhiteTable = Table(WhiteCodes), BlackTable = Table(BlackCodes);

    /// <summary>For each 7-bit window: (mode, code length); length 0 for the start of an EOL or an extension.</summary>
    private static readonly (int Mode, byte Length)[] ModeTable = Modes();

    private static (short, byte)[] Table((int Run, int Code, int Length)[] codes)
    {
        var table = new (short, byte)[1 << TableBits];
        foreach (var (run, code, length) in codes.Concat(ExtendedCodes))
        {
            var shift = TableBits - length;
            for (var rest = 0; rest < 1 << shift; rest++)
                table[(code << shift) | rest] = ((short)run, (byte)length);
        }
        return table;
    }

    private static (int, byte)[] Modes()
    {
        var table = new (int, byte)[1 << 7];
        foreach (var (mode, code, length) in ModeCodes)
        {
            var shift = 7 - length;
            for (var rest = 0; rest < 1 << shift; rest++)
                table[(code << shift) | rest] = (mode, (byte)length);
        }
        return table;
    }

    // ---- encoding ----

    /// <summary>
    /// <paramref name="height"/> rows of <paramref name="width"/> pixels, each row (width + 7) / 8 bytes, a set bit
    /// for a black pixel, coded as G4 data ending with EOFB.
    /// </summary>
    public static byte[] Encode(byte[] bits, int width, int height, CancellationToken cancellationToken = default)
    {
        var stride = (width + 7) / 8;
        var writer = new BitWriter(Math.Max(64, bits.Length / 16));
        var reference = new int[width + 4];
        var coding = new int[width + 4];
        Changes(bits, 0, width, reference, white: true);
        for (var y = 0; y < height; y++)
        {
            if ((y & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            Changes(bits, (long)y * stride, width, coding, white: false);
            EncodeRow(writer, reference, coding, width);
            (reference, coding) = (coding, reference);
        }
        writer.Write(1, 12); // EOFB: two EOLs
        writer.Write(1, 12);
        return writer.ToArray();
    }

    /// <summary>
    /// The changing elements of a row (positions whose colour differs from the pixel before; white before the first),
    /// followed by three copies of <paramref name="width"/>. With <paramref name="white"/> the row is all white.
    /// </summary>
    private static void Changes(byte[] bits, long row, int width, int[] changes, bool white)
    {
        var n = 0;
        if (!white)
        {
            var black = false;
            var x = 0;
            while (true)
            {
                x = NextChange(bits, row, x, width, black);
                if (x >= width)
                    break;
                changes[n++] = x;
                black = !black;
            }
        }
        changes[n] = changes[n + 1] = changes[n + 2] = width;
    }

    /// <summary>The first position from <paramref name="x"/> on whose pixel isn't <paramref name="black"/>; width when none.</summary>
    private static int NextChange(byte[] bits, long row, int x, int width, bool black)
    {
        var skip = black ? 0xFF : 0x00;
        while (x < width)
        {
            var value = bits[row + (x >> 3)];
            if ((x & 7) == 0 && value == skip)
            {
                x += 8;
                continue;
            }
            // Bits of this byte from x on that differ from the run's colour.
            var differ = (value ^ skip) & (0xFF >> (x & 7));
            if (differ != 0)
                return Math.Min(width, (x & ~7) + System.Numerics.BitOperations.LeadingZeroCount((uint)differ) - 24);
            x = (x & ~7) + 8;
        }
        return width;
    }

    private static void EncodeRow(BitWriter writer, int[] reference, int[] coding, int width)
    {
        var a0 = -1;
        var black = false;
        int r = 0, c = 0; // first changes on the reference and coding rows right of a0
        while (a0 < width)
        {
            while (reference[r] <= a0 && reference[r] < width)
                r++;
            // b1: a change to the colour opposite a0's (even changes go to black, odd ones to white).
            var b = r;
            if (((b & 1) == 0) == black && reference[b] < width)
                b++;
            var b1 = reference[b];
            var b2 = b1 >= width ? width : reference[b + 1];
            while (coding[c] <= a0 && coding[c] < width)
                c++;
            var a1 = coding[c];
            if (b2 < a1)
            {
                writer.Write(0b0001, 4);
                a0 = b2;
                continue;
            }
            var d = a1 - b1;
            if (d is >= -3 and <= 3)
            {
                var (_, code, length) = Vertical[d + 3];
                writer.Write(code, length);
                a0 = a1;
                black = !black;
                continue;
            }
            var a2 = coding[c + 1];
            writer.Write(0b001, 3);
            WriteRun(writer, a1 - Math.Max(a0, 0), black);
            WriteRun(writer, a2 - a1, !black);
            a0 = a2;
        }
    }

    private static void WriteRun(BitWriter writer, int run, bool black)
    {
        var codes = black ? BlackCodes : WhiteCodes;
        while (run >= 2560)
        {
            writer.Write(ExtendedCodes[^1].Code, ExtendedCodes[^1].Length);
            run -= 2560;
        }
        if (run >= 64)
        {
            var makeUp = run / 64; // 1 … 39
            var (_, code, length) = makeUp <= 27 ? codes[63 + makeUp] : ExtendedCodes[makeUp - 28];
            writer.Write(code, length);
            run %= 64;
        }
        writer.Write(codes[run].Code, codes[run].Length);
    }

    private sealed class BitWriter(int capacity)
    {
        private byte[] _data = new byte[capacity];
        private int _count;
        private ulong _bits;
        private int _held;

        public void Write(int code, int length)
        {
            _bits = (_bits << length) | (uint)code;
            _held += length;
            while (_held >= 8)
            {
                _held -= 8;
                if (_count == _data.Length)
                    Array.Resize(ref _data, _data.Length * 2);
                _data[_count++] = (byte)(_bits >> _held);
            }
        }

        public byte[] ToArray()
        {
            if (_held > 0)
                Write(0, 8 - _held);
            return _data[.._count];
        }
    }

    // ---- decoding ----

    /// <summary>
    /// Decodes G4 data into rows <paramref name="firstRow"/> … of <paramref name="bits"/> (rows of (width + 7) / 8
    /// bytes, a set bit for black), at most <paramref name="rows"/> rows, the row above the first taken as white.
    /// Data that ends early (or with EOFB) leaves the remaining rows white; a code that isn't G4 is damage.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> data, int width, int rows, byte[] bits, int firstRow, CancellationToken cancellationToken = default)
    {
        var stride = (width + 7) / 8;
        var reader = new BitReader(data);
        var reference = new int[width + 4];
        var coding = new int[width + 4];
        Changes(bits, 0, width, reference, white: true);
        for (var y = 0; y < rows; y++)
        {
            if ((y & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (!DecodeRow(ref reader, reference, coding, width))
                return;
            Fill(bits, (long)(firstRow + y) * stride, coding, width);
            (reference, coding) = (coding, reference);
        }
    }

    /// <summary>Decodes one row's changing elements into <paramref name="coding"/>; false at the end of the data.</summary>
    private static bool DecodeRow(ref BitReader reader, int[] reference, int[] coding, int width)
    {
        var a0 = -1;
        var black = false;
        var n = 0;
        var r = 0;
        while (a0 < width)
        {
            if (reader.AtEnd)
                return false;
            var (mode, length) = ModeTable[reader.Peek(7)];
            if (length == 0)
            {
                // EOFB (two EOLs: eleven zeros and a one) at a row's start, or data that ends here, ends the picture;
                // anything else (an EOL inside a row, the uncompressed mode) is damage.
                if ((reader.Peek(12) == 1 && n == 0 && a0 < 0) || reader.RestIsZero)
                    return false;
                throw new InvalidDataException("G4 data holds a code Filee doesn't read.");
            }
            reader.Skip(length);
            while (reference[r] <= a0 && reference[r] < width)
                r++;
            var b = r;
            if (((b & 1) == 0) == black && reference[b] < width)
                b++;
            var b1 = reference[b];
            var b2 = b1 >= width ? width : reference[b + 1];
            switch (mode)
            {
                case Pass:
                    a0 = b2;
                    break;
                case Horizontal:
                    {
                        var run1 = ReadRun(ref reader, black, width);
                        var run2 = run1 < 0 ? -1 : ReadRun(ref reader, !black, width);
                        if (run2 < 0)
                            return false; // the data ends inside the row
                        var a1 = Math.Max(a0, 0) + run1;
                        var a2 = a1 + run2;
                        // Each change lies right of the one before (only the first run of a row may be empty), so a row
                        // has fewer changes than pixels.
                        if (a2 > width || (a0 >= 0 && a1 <= a0) || (a2 <= a1 && a1 < width))
                            throw new InvalidDataException("A G4 row's runs don't fit the row.");
                        // A change at the row's end is only where the row stops.
                        if (a1 < width)
                            coding[n++] = a1;
                        if (a2 < width)
                            coding[n++] = a2;
                        a0 = a2;
                        break;
                    }
                default:
                    {
                        var a1 = b1 + mode;
                        if (a1 < 0 || a1 > width || a1 <= a0)
                            throw new InvalidDataException("A G4 row's changes are out of order.");
                        if (a1 < width)
                            coding[n++] = a1;
                        a0 = a1;
                        black = !black;
                        break;
                    }
            }
        }
        coding[n] = coding[n + 1] = coding[n + 2] = width;
        return true;
    }

    /// <summary>A run's length (its make-up codes and terminating code); -1 when the data ends first.</summary>
    private static int ReadRun(ref BitReader reader, bool black, int width)
    {
        var table = black ? BlackTable : WhiteTable;
        var total = 0;
        while (true)
        {
            var (run, length) = table[reader.Peek(TableBits)];
            if (length == 0)
                return reader.RestIsZero ? -1 : throw new InvalidDataException("G4 data holds a run code Filee doesn't read.");
            reader.Skip(length);
            total += run;
            if (total > width)
                throw new InvalidDataException("A G4 run is longer than the row.");
            if (run < 64)
                return total;
        }
    }

    /// <summary>Sets the black pixels of a row from its changing elements.</summary>
    private static void Fill(byte[] bits, long row, int[] changes, int width)
    {
        for (var i = 0; changes[i] < width; i += 2)
        {
            int from = changes[i], to = Math.Min(changes[i + 1], width);
            for (var x = from; x < to; x++)
                bits[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _position;

        public readonly bool AtEnd => _position >= 8L * _data.Length;

        /// <summary>Whether only zero bits are left (the padding after the last code, or nothing).</summary>
        public readonly bool RestIsZero
        {
            get
            {
                var at = (int)(_position >> 3);
                if (at >= _data.Length)
                    return true;
                if ((_data[at] & (0xFF >> (int)(_position & 7))) != 0)
                    return false;
                return !_data[(at + 1)..].ContainsAnyExcept((byte)0);
            }
        }

        /// <summary>The next <paramref name="count"/> bits (≤ 24), zeros past the end.</summary>
        public readonly int Peek(int count)
        {
            var at = (int)(_position >> 3);
            var value = 0u;
            for (var i = 0; i < 4; i++)
                value = (value << 8) | (at + i < _data.Length ? _data[at + i] : 0u);
            return (int)((value << (int)(_position & 7)) >> (32 - count));
        }

        public void Skip(int count) => _position += count;
    }
}
