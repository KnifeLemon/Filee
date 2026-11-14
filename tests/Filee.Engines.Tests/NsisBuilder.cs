// Builds NSIS-like installers in code for NsisReaderTests: a stub, the first header, the header block (flags, the
// block table, the script's entries, the string table with variable codes) and the data blocks, solid or not, stored,
// deflated, in NSIS's bzip2 framing or LZMA, and the CRC-32. Only what Filee's reader looks at is filled in.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

internal static class NsisBuilder
{
    public enum StringKind
    {
        /// <summary>UTF-16 with codes 1-4 (NSIS 3 Unicode).</summary>
        Unicode,

        /// <summary>Bytes with codes 1-4 (NSIS 3 ANSI).</summary>
        Ansi3,

        /// <summary>Bytes with codes 252-255 (NSIS 2).</summary>
        Ansi2,
    }

    /// <summary>One entry of the script.</summary>
    public abstract record Op;

    /// <summary>SetOutPath (EW_CREATEDIR with parameter 1 set); <see cref="Create"/> is a plain CreateDirectory.</summary>
    public sealed record SetOutPath(string Path, bool Create = false) : Op;

    /// <summary>File: <paramref name="Data"/> is the index of a data block, or <paramref name="Offset"/> the offset.</summary>
    public sealed record ExtractFile(string Name, int Data, DateTime? Modified = null, int? Offset = null) : Op;

    public sealed record WriteUninstaller(string Name, int Data) : Op;

    /// <summary>Any entry as it is (strings are offsets, not text).</summary>
    public sealed record Raw(int Opcode, params int[] Parameters) : Op;

    public sealed record Options
    {
        public NsisCompression Compression { get; init; } = NsisCompression.Lzma;
        public bool Solid { get; init; } = true;
        public StringKind Strings { get; init; } = StringKind.Unicode;
        public bool Crc { get; init; } = true;

        /// <summary>Bytes before the first header (a multiple of 512; 0: none).</summary>
        public int Stub { get; init; } = 512;

        /// <summary>LZMA: the filter byte before the properties (null: none).</summary>
        public byte? Filter { get; init; }

        /// <summary>Non-solid: store the blocks with these indexes instead of compressing them.</summary>
        public IReadOnlySet<int> Stored { get; init; } = new HashSet<int>();

        /// <summary>Changes the size written before data block i.</summary>
        public Func<int, uint, uint>? BlockSize { get; init; }

        /// <summary>Changes the header block before it is packed.</summary>
        public Action<byte[]>? Header { get; init; }
    }

    /// <summary>Where the header block's parts lie, for tests that change them.</summary>
    public const int EntriesBlock = 4 + 8 * 2;
    public const int StringsBlock = 4 + 8 * 3;
    public const int FirstEntry = 4 + 8 * 8;

    private static readonly Dictionary<string, int> Variables = new()
    {
        ["$INSTDIR"] = 21,
        ["$OUTDIR"] = 22,
        ["$TEMP"] = 25,
        ["$PLUGINSDIR"] = 26,
        ["$0"] = 0,
    };

    public static byte[] Build(IReadOnlyList<Op> script, IReadOnlyList<byte[]> data, Options? options = null)
    {
        options ??= new Options();

        // The data blocks: their offsets count from the end of the header block.
        using var blocks = new MemoryStream();
        var offsets = new List<int>();
        for (var i = 0; i < data.Count; i++)
        {
            offsets.Add((int)blocks.Length);
            var stored = options.Solid || options.Compression == NsisCompression.Stored || options.Stored.Contains(i);
            var body = stored ? data[i] : Pack(data[i], options);
            var size = (uint)body.Length | (stored ? 0 : 0x80000000);
            blocks.Write(U32(options.BlockSize?.Invoke(i, size) ?? size));
            blocks.Write(body);
        }
        var header = Header(script, options.Strings, offsets);
        options.Header?.Invoke(header);

        byte[] payload;
        if (options.Solid)
        {
            payload = Pack([.. U32((uint)header.Length), .. header, .. blocks.ToArray()], options);
        }
        else if (options.Compression == NsisCompression.Stored)
        {
            payload = [.. U32((uint)header.Length), .. header, .. blocks.ToArray()];
        }
        else
        {
            var packed = Pack(header, options);
            payload = [.. U32((uint)packed.Length | 0x80000000), .. packed, .. blocks.ToArray()];
        }

        var stub = new byte[options.Stub];
        if (stub.Length >= 2)
            "MZ"u8.CopyTo(stub);
        var first = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(first, options.Crc ? 0u : 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(4), 0xDEADBEEF);
        "NullsoftInst"u8.CopyTo(first.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(20), (uint)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(24), (uint)(first.Length + payload.Length + 4));
        byte[] file = [.. stub, .. first, .. payload, 0, 0, 0, 0];
        if (options.Crc)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(file.Length - 4), Crc32.Compute(file.AsSpan(512, file.Length - 4 - 512)));
        return file;
    }

    /// <summary>Recomputes the CRC-32 after a test changed bytes.</summary>
    public static void FixCrc(byte[] file) =>
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(file.Length - 4), Crc32.Compute(file.AsSpan(512, file.Length - 4 - 512)));

    /// <summary>The header block: flags, the block table, the entries and the strings.</summary>
    private static byte[] Header(IReadOnlyList<Op> script, StringKind kind, List<int> offsets)
    {
        var strings = new StringTable(kind);
        var entries = new List<int[]>();
        foreach (var op in script)
        {
            entries.Add(op switch
            {
                SetOutPath s => [11, strings.Add(s.Path), s.Create ? 0 : 1, 0, 0, 0, 0],
                ExtractFile f => [20, 2, strings.Add(f.Name), f.Offset ?? offsets[f.Data], .. Time(f.Modified), -37],
                WriteUninstaller u => [62, strings.Add(u.Name), offsets[u.Data], 0, 0, 0, 0],
                Raw r => [r.Opcode, .. r.Parameters, .. new int[6 - r.Parameters.Length]],
                _ => throw new ArgumentException(op.ToString()),
            });
        }
        var table = strings.ToArray();
        var entriesAt = FirstEntry;
        var stringsAt = entriesAt + entries.Count * 28;
        var length = stringsAt + table.Length;
        var header = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(EntriesBlock), (uint)entriesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(EntriesBlock + 4), (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(StringsBlock), (uint)stringsAt);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(StringsBlock + 8), (uint)length); // language tables: none
        for (var i = 0; i < entries.Count; i++)
            for (var k = 0; k < 7; k++)
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(entriesAt + i * 28 + k * 4), entries[i][k]);
        table.CopyTo(header, stringsAt);
        return header;
    }

    private static int[] Time(DateTime? time)
    {
        if (time is not { } t)
            return [-1, -1];
        var value = t.ToFileTimeUtc();
        return [(int)(uint)value, (int)(value >> 32)];
    }

    private static byte[] Pack(byte[] data, Options options) => options.Compression switch
    {
        NsisCompression.Deflate => Deflate(data),
        NsisCompression.Bzip2 => ToNsisBzip2(Bzip2(data)),
        NsisCompression.Lzma => [.. options.Filter is { } f ? new[] { f } : [], 0x5D, 0x00, 0x00, 0x10, 0x00, .. RawLzma(data, endMarker: true)],
        _ => data,
    };

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    /// <summary>
    /// A standard bzip2 stream in NSIS's framing: no "BZh9", each block's 48-bit magic, CRC and randomised bit become
    /// the byte 0x31, the end magic and stream CRC the byte 0x17.
    /// </summary>
    public static byte[] ToNsisBzip2(byte[] standard)
    {
        Assert.Equal("BZh9"u8.ToArray(), standard[..4]);
        var bits = new bool[standard.Length * 8];
        for (var i = 0; i < bits.Length; i++)
            bits[i] = (standard[i >> 3] & (0x80 >> (i & 7))) != 0;
        const ulong blockMagic = 0x314159265359, endMagic = 0x177245385090;
        ulong Read48(int at)
        {
            ulong v = 0;
            for (var i = 0; i < 48 && at + i < bits.Length; i++)
                v = (v << 1) | (bits[at + i] ? 1UL : 0);
            return v;
        }

        var output = new List<bool>();
        void Write(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                output.Add(((value >> i) & 1) != 0);
        }

        var pos = 32;
        while (true)
        {
            var magic = Read48(pos);
            if (magic == endMagic)
            {
                Write(0x17, 8);
                break;
            }
            Assert.Equal(blockMagic, magic);
            Write(0x31, 8);
            pos += 48 + 32 + 1;
            var next = pos;
            while (Read48(next) is not (blockMagic or endMagic))
                next++;
            output.AddRange(bits[pos..next]);
            pos = next;
        }
        var bytes = new byte[(output.Count + 7) / 8];
        for (var i = 0; i < output.Count; i++)
            if (output[i])
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// NSIS strings: "$INSTDIR" and the other names in <see cref="Variables"/> become variable codes, "$SMPROGRAMS" a
    /// shell folder code; the number after a code is two bytes with bit 7 set (one UTF-16 unit in Unicode).
    /// </summary>
    private sealed class StringTable
    {
        private readonly StringKind _kind;
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<string, int> _offsets = [];

        public StringTable(StringKind kind)
        {
            _kind = kind;
            Add(""); // offset 0 is the empty string
        }

        private bool Unicode => _kind == StringKind.Unicode;

        private (int Language, int Shell, int Variable, int Skip) Codes =>
            _kind == StringKind.Ansi2 ? (255, 254, 253, 252) : (1, 2, 3, 4);

        public int Add(string text)
        {
            if (_offsets.TryGetValue(text, out var known))
                return known;
            var offset = _bytes.Count / (Unicode ? 2 : 1);
            _offsets[text] = offset;
            for (var i = 0; i < text.Length;)
            {
                var variable = Variables.Keys.Where(v => string.CompareOrdinal(text, i, v, 0, v.Length) == 0).MaxBy(v => v.Length);
                if (variable is not null)
                {
                    Code(Codes.Variable, (Variables[variable] & 0x7F) | 0x80, (Variables[variable] >> 7) | 0x80);
                    i += variable.Length;
                }
                else if (string.CompareOrdinal(text, i, "$SMPROGRAMS", 0, 11) == 0)
                {
                    Code(Codes.Shell, 0x02, 0x17);
                    i += 11;
                }
                else
                {
                    if (text[i] is >= '\u0001' and <= '\u0004' || !Unicode && text[i] >= 252)
                        Unit(Codes.Skip);
                    Unit(text[i]);
                    i++;
                }
            }
            Unit(0);
            return offset;
        }

        private void Code(int code, int low, int high)
        {
            Unit(code);
            if (Unicode)
            {
                Unit(low | high << 8);
            }
            else
            {
                Unit(low);
                Unit(high);
            }
        }

        private void Unit(int value)
        {
            if (Unicode)
            {
                _bytes.Add((byte)value);
                _bytes.Add((byte)(value >> 8));
            }
            else
            {
                _bytes.Add(Encoding.Latin1.GetBytes([(char)value])[0]);
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
