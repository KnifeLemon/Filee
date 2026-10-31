// Mutation fuzzing input for the HWP reader (DocumentFuzzTests): a seed HWP is taken apart into its records, which are
// changed (bytes, sizes, levels, tags, order) and packed into a new compound file; now and then the bytes of the
// compound file itself are changed. Rich() is a seed with what the reader turns into more than text: a table, a note,
// a hyperlink field and a picture with its BinData.

using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Binary;
using static Filee.Engines.Tests.HwpBuilder;

namespace Filee.Engines.Tests;

internal sealed class HwpFuzzer
{
    private static readonly uint[] Interesting = [0, 1, 0xFF, 0xFFFF, 0x7FFF, 0x8000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFE, 1 << 20, 1 << 30, 0xFFF];

    private readonly Random _random;
    private readonly uint _flags;
    private readonly uint _version;
    private readonly List<(int Tag, int Level, byte[] Data)> _docInfo;
    private readonly List<(int Tag, int Level, byte[] Data)> _section;
    private readonly Dictionary<string, byte[]> _streams = [];

    public HwpFuzzer(byte[] seed, Random random)
    {
        _random = random;
        using var cfb = Filee.Engines.Hwp.Binary.CompoundFile.Open(new MemoryStream(seed));
        var header = cfb.Read("FileHeader")!;
        _version = BitConverter.ToUInt32(header, 32);
        _flags = BitConverter.ToUInt32(header, 36);
        var compressed = (_flags & 1) != 0;
        _docInfo = Records(Unpack(cfb.Read("DocInfo")!, compressed));
        _section = Records(Unpack(cfb.Read("BodyText/Section0")!, compressed));
        foreach (var stream in cfb.Streams.Where(s => s.StartsWith("BinData/", StringComparison.Ordinal)))
            _streams[stream] = cfb.Read(stream)!;
    }

    private static byte[] Unpack(byte[] bytes, bool compressed)
    {
        if (!compressed)
            return bytes;
        using var inflate = new System.IO.Compression.DeflateStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    private static List<(int, int, byte[])> Records(byte[] bytes) =>
        HwpRecord.Parse(bytes, DocumentLimits.Default).Select(r => (r.Tag, r.Level, r.Data)).ToList();

    /// <summary>A mutated copy of the seed.</summary>
    public byte[] Next()
    {
        var docInfo = _docInfo.Select(r => (r.Tag, r.Level, (byte[])r.Data.Clone())).ToList();
        var section = _section.Select(r => (r.Tag, r.Level, (byte[])r.Data.Clone())).ToList();
        for (var rounds = 1 + _random.Next(4); rounds > 0; rounds--)
            Mutate(_random.Next(3) == 0 ? docInfo : section);

        var builder = new HwpBuilder { Flags = _flags, Version = _version };
        builder.DocInfo.AddRange(docInfo.Select(r => Record(r.Tag, r.Level, r.Item3)));
        builder.Section.AddRange(section.Select(r => Record(r.Tag, r.Level, r.Item3)));
        foreach (var (path, bytes) in _streams)
            builder.Streams[path] = bytes;
        var file = builder.Build();
        if (_random.Next(100) < 10)
        {
            // The compound file itself: header, allocation table, directory.
            for (var flips = 1 + _random.Next(6); flips > 0; flips--)
                file[_random.Next(Math.Min(file.Length, 2048))] = (byte)_random.Next(256);
        }
        return file;
    }

    private void Mutate(List<(int Tag, int Level, byte[] Data)> records)
    {
        if (records.Count == 0)
            return;
        var index = _random.Next(records.Count);
        var (tag, level, data) = records[index];
        switch (_random.Next(100))
        {
            case < 40 when data.Length > 0:
                for (var i = 1 + _random.Next(4); i > 0; i--)
                    data[_random.Next(data.Length)] = (byte)_random.Next(256);
                break;
            case < 60 when data.Length >= 4:
                {
                    var value = Interesting[_random.Next(Interesting.Length)];
                    var offset = _random.Next(data.Length - 3);
                    BitConverter.GetBytes(_random.Next(2) == 0 ? value : (ushort)value).AsSpan(0, _random.Next(2) == 0 ? 4 : 2).CopyTo(data.AsSpan(offset));
                    break;
                }
            case < 70:
                records[index] = (tag, level, data[..(data.Length == 0 ? 0 : _random.Next(data.Length))]);
                break;
            case < 78:
                records.Insert(_random.Next(records.Count + 1), (tag, level, (byte[])data.Clone()));
                break;
            case < 84:
                records.RemoveAt(index);
                break;
            case < 94:
                records[index] = (tag, Math.Clamp(level + _random.Next(-3, 4), 0, 1023), data);
                break;
            default:
                records[index] = (_random.Next(2) == 0 ? _random.Next(16, 100) : _random.Next(1024), level, data);
                break;
        }
    }

    // ───────────────────────── Rich seed ─────────────────────────

    private static readonly byte[] Identity = Bytes(1.0, 0.0, 0.0, 0.0, 1.0, 0.0);

    private static byte[] Bytes(params double[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    /// <summary>Object properties every object shares: attributes, position, size, z-order, margins, instance id.</summary>
    private static byte[] Common(string id, uint attributes, int width, int height, int instance) =>
        HwpBuilder.Bytes(Id(id), attributes, 0, 0, width, height, 0, (short)0, (short)0, (short)0, (short)0, instance, 0, "");

    /// <summary>A plain document with a table of two cells, a footnote, a hyperlink and a floating picture.</summary>
    public static HwpBuilder Rich()
    {
        var builder = new HwpBuilder().PlainDocInfo();
        builder.DocInfo.Add(Record(18, 1, HwpBuilder.Bytes((ushort)1, (ushort)1, "png")));
        builder.Streams["BinData/BIN0001.png"] = DocumentFuzzTests.TinyPng(3, 3);
        builder.SectionStart();

        var text = HwpBuilder.Bytes(Encoding.Unicode.GetBytes("표"), Control(11, "tbl "), Control(17, "fn  "), Control(3, "%hlk"),
            Encoding.Unicode.GetBytes("링크"), HwpBuilder.Bytes((ushort)4, 0, 0, 0, (ushort)4), Control(11, "gso "), (ushort)13);
        var section = builder.Section;
        section.Add(Record(66, 0, HwpBuilder.Bytes(text.Length / 2, 0, (ushort)0, (byte)0, (byte)0, (ushort)1, (ushort)0, (ushort)0, 0, (ushort)0)));
        section.Add(Record(67, 1, text));
        section.Add(Record(68, 1, HwpBuilder.Bytes(0, 0)));

        // Table: one row of two cells.
        section.Add(Record(71, 1, Common("tbl ", 1 | 1 << 21, 20000, 2000, 1)));
        section.Add(Record(77, 2, HwpBuilder.Bytes(0, (ushort)1, (ushort)2, (short)0, (short)510, (short)510, (short)141, (short)141, (ushort)2, (ushort)1, (ushort)0)));
        for (var column = 0; column < 2; column++)
        {
            section.Add(Record(72, 2, HwpBuilder.Bytes(1, 0x20, (ushort)column, (ushort)0, (ushort)1, (ushort)1, 10000, 1000, (short)510, (short)510, (short)141, (short)141, (ushort)1)));
            var cell = new HwpBuilder().Paragraph($"칸{column + 1}", 2);
            section.AddRange(cell.Section);
        }

        // Footnote.
        section.Add(Record(71, 1, HwpBuilder.Bytes(Id("fn  "), 1, (ushort)0, (ushort)0, 0, 2)));
        section.Add(Record(72, 2, HwpBuilder.Bytes(1, 0, 0, 0)));
        section.AddRange(new HwpBuilder().Paragraph(" 각주", 2).Section);

        // Hyperlink field.
        section.Add(Record(71, 1, HwpBuilder.Bytes(Id("%hlk"), 0, (byte)0, "https\\://example.invalid/page;1;0;0;", 3)));

        // Floating picture.
        section.Add(Record(71, 1, Common("gso ", 2 << 3 | 2 << 8 | 4 << 15 | 2 << 18 | 1 << 26, 3000, 3000, 4)));
        section.Add(Record(76, 2, HwpBuilder.Bytes(Id("$pic"), Id("$pic"), 0, 0, (ushort)0, (ushort)1, 3000, 3000, 3000, 3000, 0, (short)0, 1500, 1500, (ushort)1,
            Identity, Identity, Identity)));
        section.Add(Record(85, 3, HwpBuilder.Bytes(0, 0, 0, 0, 0, 3000, 0, 3000, 3000, 0, 3000, 0, 0, 3000, 3000, (short)0, (short)0, (short)0, (short)0,
            (byte)0, (byte)0, (byte)0, (ushort)1, (byte)0, 4, 0, 3000, 3000)));
        return builder;
    }
}
