// The bit reader of Filee's JPEG XR decoder (ITU-T T.832): bits most significant first, fixed-length fields (u(n)) and
// the variable-length codes of the spec's code tables (e(v), and ae(v) whose table is chosen adaptively). A codestream
// that ends before a syntax element does is damaged: reading past its end throws.

namespace Filee.Engines.JpegXr;

/// <summary>Reads a JPEG XR codestream bit by bit.</summary>
internal sealed class JpegXrBits(byte[] data, int start, int end)
{
    private readonly byte[] _data = data;
    private readonly int _end = Math.Min(end, data.Length);
    private long _bit = (long)start * 8;

    /// <summary>The byte the next bit is in, from the start of <see cref="_data"/>.</summary>
    public int BytePosition => (int)(_bit >> 3);

    public bool IsByteAligned => (_bit & 7) == 0;

    /// <summary>Moves to byte <paramref name="position"/> of the data.</summary>
    public void Seek(int position)
    {
        if (position < 0 || position > _end)
            throw Damaged();
        _bit = (long)position * 8;
    }

    public void AlignToByte() => _bit = (_bit + 7) & ~7L;

    public int Bit()
    {
        var at = _bit >> 3;
        if (at >= _end)
            throw Damaged();
        var bit = (_data[at] >> (7 - (int)(_bit & 7))) & 1;
        _bit++;
        return bit;
    }

    /// <summary>u(n): <paramref name="count"/> bits (up to 32) as an unsigned number.</summary>
    public uint Bits(int count)
    {
        if (count == 0)
            return 0;
        if ((ulong)count > 32 || _bit + count > (long)_end * 8)
            throw Damaged();
        uint value = 0;
        for (var i = 0; i < count; i++)
        {
            value = (value << 1) | (uint)((_data[_bit >> 3] >> (7 - (int)(_bit & 7))) & 1);
            _bit++;
        }
        return value;
    }

    public int Int(int count) => (int)Bits(count);

    public bool Flag() => Bit() == 1;

    /// <summary>A value of the code table <paramref name="table"/>.</summary>
    public int Code(JpegXrVlc table)
    {
        int code = 0, length = 0;
        while (true)
        {
            code = (code << 1) | Bit();
            length++;
            if (table.Find(code, length) is var value and >= 0)
                return value;
            if (length >= table.MaxLength)
                throw Damaged();
        }
    }

    public static InvalidDataException Damaged() => new("The JPEG XR picture is damaged: its data ends early or holds a code that doesn't exist.");
}

/// <summary>A variable-length code table: each value's code as a string of 0s and 1s.</summary>
internal sealed class JpegXrVlc
{
    /// <summary>The value of each code, at (1 &lt;&lt; length) | bits; −1 where there is no code.</summary>
    private readonly int[] _values;

    /// <param name="codes">The code of value i at index i (spaces are ignored).</param>
    public JpegXrVlc(params string[] codes)
        : this(codes.Select((code, value) => (code, value)).ToArray())
    {
    }

    private JpegXrVlc((string Code, int Value)[] codes)
    {
        Codes = [.. codes.Select(c => c.Code.Replace(" ", "", StringComparison.Ordinal))];
        MaxLength = Codes.Max(c => c.Length);
        _values = new int[2 << MaxLength];
        Array.Fill(_values, -1);
        for (var i = 0; i < codes.Length; i++)
        {
            var bits = 0;
            foreach (var c in Codes[i])
                bits = (bits << 1) | (c == '1' ? 1 : 0);
            _values[(1 << Codes[i].Length) | bits] = codes[i].Value;
        }
    }

    /// <summary>A table whose values are not 0…n−1: pairs of code and value.</summary>
    public static JpegXrVlc Mapped(params (string Code, int Value)[] codes) => new(codes);

    public int MaxLength { get; }

    /// <summary>The codes, for the table's own checks.</summary>
    public IReadOnlyList<string> Codes { get; }

    public int Find(int bits, int length) => _values[(1 << length) | bits];
}
