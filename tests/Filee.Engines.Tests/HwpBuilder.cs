// HWP 5.0 files built in code for the HWP reader's tests: records (tag, level, bytes) for DocInfo and BodyText, a
// FileHeader with chosen flags, packed as a compound file (CFB, version 3, 512-byte sectors). Hooks let a test break the
// compound file on purpose: edit the allocation table, the directory or a stream's declared size.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Tests;

internal sealed class HwpBuilder
{
    private const int Sector = 512;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FatSector = 0xFFFFFFFD;
    private const uint Free = 0xFFFFFFFF;

    /// <summary>FileHeader properties: bit 0 compressed, 1 password, 2 distribution, 4 DRM.</summary>
    public uint Flags { get; set; }

    public uint Version { get; set; } = 0x05000500;

    public List<byte[]> DocInfo { get; } = [];

    public List<byte[]> Section { get; } = [];

    /// <summary>The bytes of BodyText/Section0 as they are stored, instead of the packed <see cref="Section"/> records.</summary>
    public byte[]? SectionBytes { get; set; }

    /// <summary>More streams by path ("BinData/BIN0001.png", "ViewText/Section0").</summary>
    public Dictionary<string, byte[]> Streams { get; } = [];

    /// <summary>Edits the allocation table once it is laid out (stream path → first sector).</summary>
    public Action<uint[], IReadOnlyDictionary<string, uint>>? EditFat { get; set; }

    /// <summary>Edits the directory entries (128 bytes each; 0 is the root) once they are laid out.</summary>
    public Action<byte[]>? EditDirectory { get; set; }

    /// <summary>Declared sizes that differ from the streams' lengths.</summary>
    public Dictionary<string, long> DeclaredSizes { get; } = [];

    // ───────────────────────── Records ─────────────────────────

    /// <summary>A record: the header (tag, level, size; 0xFFF and a 32-bit size for long ones) and its bytes.</summary>
    public static byte[] Record(int tag, int level, byte[] data)
    {
        using var output = new MemoryStream();
        var size = data.Length >= 0xFFF ? 0xFFFu : (uint)data.Length;
        Write32(output, (uint)tag | ((uint)level << 10) | (size << 20));
        if (size == 0xFFF)
            Write32(output, (uint)data.Length);
        output.Write(data);
        return output.ToArray();
    }

    /// <summary>Little-endian bytes of the values (int → 4 bytes, short/ushort → 2, byte → 1, string → length + UTF-16).</summary>
    public static byte[] Bytes(params object[] values)
    {
        using var output = new MemoryStream();
        foreach (var value in values)
        {
            switch (value)
            {
                case int i:
                    Write32(output, (uint)i);
                    break;
                case uint u:
                    Write32(output, u);
                    break;
                case short s:
                    output.Write(BitConverter.GetBytes(s));
                    break;
                case ushort s:
                    output.Write(BitConverter.GetBytes(s));
                    break;
                case byte b:
                    output.WriteByte(b);
                    break;
                case string text:
                    output.Write(BitConverter.GetBytes((ushort)text.Length));
                    output.Write(Encoding.Unicode.GetBytes(text));
                    break;
                case byte[] raw:
                    output.Write(raw);
                    break;
                default:
                    throw new ArgumentException(value?.GetType().Name);
            }
        }
        return output.ToArray();
    }

    /// <summary>A control id ("secd") as the number HWP stores.</summary>
    public static uint Id(string id) => (uint)(id[0] << 24 | id[1] << 16 | id[2] << 8 | id[3]);

    /// <summary>
    /// The DocInfo of a plain document: one font for every language, a border fill, a 10 pt character shape, a left
    /// aligned paragraph shape with 160 % line spacing, a tab definition and the style 바탕글.
    /// </summary>
    public HwpBuilder PlainDocInfo()
    {
        DocInfo.Add(Record(16, 0, Bytes((ushort)1, (ushort)1, (ushort)1, (ushort)1, (ushort)1, (ushort)1, (ushort)1, 0, 0, 0)));
        DocInfo.Add(Record(17, 0, Bytes(0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0)));
        for (var i = 0; i < 7; i++)
            DocInfo.Add(Record(19, 1, Bytes((byte)0, "함초롬바탕")));
        DocInfo.Add(Record(20, 1, new byte[40]));
        DocInfo.Add(Record(21, 1, CharShape(1000)));
        DocInfo.Add(Record(22, 1, new byte[8]));
        DocInfo.Add(Record(25, 1, Bytes(1 << 2 | 1 << 8, 0, 0, 0, 0, 0, 160, (ushort)0, (ushort)0, (ushort)1, (short)0, (short)0, (short)0, (short)0, 0, 0, 160)));
        DocInfo.Add(Record(26, 1, Bytes("바탕글", "Normal", (byte)0, (byte)0, (short)1042, (ushort)0, (ushort)0)));
        return this;
    }

    public static byte[] CharShape(int height, uint attributes = 0) => Bytes(
        new byte[14], new byte[] { 100, 100, 100, 100, 100, 100, 100 }, new byte[7], new byte[] { 100, 100, 100, 100, 100, 100, 100 }, new byte[7],
        height, attributes, (byte)10, (byte)10, 0, 0, unchecked((int)0xFFFFFFFF), 0x00B2B2B2, (ushort)1, 0);

    /// <summary>
    /// A section definition paragraph: secd (A4, 30 mm side margins) and a single column, as 한글 starts every
    /// section; <paramref name="landscape"/> turns the page.
    /// </summary>
    public HwpBuilder SectionStart(bool landscape = false)
    {
        var text = Bytes(Control(2, "secd"), Control(2, "cold"), (ushort)13);
        Section.Add(Record(66, 0, Bytes(0x80000000u | 17, 4u, (ushort)0, (byte)0, (byte)3, (ushort)1, (ushort)0, (ushort)1, 0, (ushort)0)));
        Section.Add(Record(67, 1, text));
        Section.Add(Record(68, 1, Bytes(0, 0)));
        Section.Add(Record(71, 1, Bytes(Id("secd"), 0, (ushort)1134, (ushort)0, (ushort)0, 8000, (ushort)1, (ushort)0, (ushort)0, (ushort)0, (ushort)0, (ushort)0)));
        Section.Add(Record(73, 2, Bytes(59528, 84188, 8504, 8504, 5668, 4252, 4252, 4252, 0, landscape ? 1 : 0)));
        Section.Add(Record(71, 1, Bytes(Id("cold"), (ushort)(1 << 2 | 1 << 12), (ushort)0, (ushort)0, (byte)0, (byte)0, 0)));
        return this;
    }

    /// <summary>A paragraph of plain text with one character shape and one stored line.</summary>
    public HwpBuilder Paragraph(string text, int level = 0)
    {
        Section.Add(Record(66, level, Bytes(text.Length + 1, 0, (ushort)0, (byte)0, (byte)0, (ushort)1, (ushort)0, (ushort)1, 0, (ushort)0)));
        Section.Add(Record(67, level + 1, Bytes(Encoding.Unicode.GetBytes(text + "\r"))));
        Section.Add(Record(68, level + 1, Bytes(0, 0)));
        Section.Add(Record(69, level + 1, Bytes(0, 0, 1000, 1000, 850, 600, 0, 42520, 0x60000)));
        return this;
    }

    /// <summary>The eight characters of an extended control in paragraph text.</summary>
    public static byte[] Control(ushort code, string id)
    {
        var packed = Id(id);
        return Bytes(code, (ushort)(packed & 0xFFFF), (ushort)(packed >> 16), 0, 0, (ushort)0, code);
    }

    // ───────────────────────── Compound file ─────────────────────────

    /// <summary>The HWP file: FileHeader, DocInfo and BodyText/Section0 (deflated when the flags say so) and the extra streams.</summary>
    public byte[] Build()
    {
        var header = new byte[256];
        Encoding.ASCII.GetBytes("HWP Document File").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), Flags);
        var compressed = (Flags & 1) != 0;
        var streams = new List<(string Path, byte[] Data)>
        {
            ("FileHeader", header),
            ("DocInfo", Pack(DocInfo.SelectMany(r => r).ToArray(), compressed)),
            ("BodyText/Section0", SectionBytes ?? Pack(Section.SelectMany(r => r).ToArray(), compressed)),
        };
        streams.AddRange(Streams.Select(s => (s.Key, s.Value)));
        return Cfb(streams);
    }

    /// <summary>Raw deflate, as HWP compresses its streams.</summary>
    public static byte[] Pack(byte[] bytes, bool compress)
    {
        if (!compress)
            return bytes;
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(bytes);
        return output.ToArray();
    }

    private byte[] Cfb(List<(string Path, byte[] Data)> streams)
    {
        // Directory: the root, every storage, every stream.
        var entries = new List<(string Name, int Type, string Path)> { ("Root Entry", 5, "") };
        foreach (var (path, _) in streams)
        {
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var folder = string.Join('/', parts[..i]);
                if (!entries.Any(e => e.Path == folder))
                    entries.Add((parts[i - 1], 1, folder));
            }
            entries.Add((parts[^1], 2, path));
        }

        // Sectors: FAT, directory, then each stream (no mini stream: the cutoff is 0).
        var directorySectors = (entries.Count * 128 + Sector - 1) / Sector;
        var dataSectors = streams.Sum(s => Math.Max(1, (s.Data.Length + Sector - 1) / Sector));
        var fatSectors = 1;
        while (fatSectors * (Sector / 4) < fatSectors + directorySectors + dataSectors)
            fatSectors++;
        var total = fatSectors + directorySectors + dataSectors;
        var fat = Enumerable.Repeat(Free, fatSectors * (Sector / 4)).ToArray();
        for (var i = 0; i < fatSectors; i++)
            fat[i] = FatSector;
        Chain(fat, fatSectors, directorySectors);
        var starts = new Dictionary<string, uint>();
        var next = fatSectors + directorySectors;
        foreach (var (path, data) in streams)
        {
            var count = Math.Max(1, (data.Length + Sector - 1) / Sector);
            starts[path] = (uint)next;
            Chain(fat, next, count);
            next += count;
        }
        EditFat?.Invoke(fat, starts);

        var directory = new byte[directorySectors * Sector];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = directory.AsSpan(i * 128, 128);
            var (name, type, path) = entries[i];
            Encoding.Unicode.GetBytes(name).CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)((name.Length + 1) * 2));
            entry[66] = (byte)type;
            entry[67] = 1; // black
            // Members of a storage: the first is its child, the others a chain of right siblings.
            var folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            var siblings = entries.Select((e, index) => (e, index)).Where(x => x.index > 0 && Parent(x.e.Path) == folder).Select(x => x.index).ToList();
            var position = siblings.IndexOf(i);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], Free);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], position >= 0 && position + 1 < siblings.Count ? (uint)siblings[position + 1] : Free);
            var children = entries.Select((e, index) => (e, index)).Where(x => x.index > 0 && Parent(x.e.Path) == path && type != 2).Select(x => x.index).ToList();
            BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], children.Count > 0 ? (uint)children[0] : Free);
            if (type == 2)
            {
                var data = streams.First(s => s.Path == path).Data;
                BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], starts[path]);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[120..], (ulong)(DeclaredSizes.TryGetValue(path, out var size) ? size : data.Length));
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], type == 5 ? EndOfChain : 0);
            }
        }
        EditDirectory?.Invoke(directory);

        var file = new byte[(total + 1) * Sector];
        var head = file.AsSpan(0, Sector);
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(head);
        BinaryPrimitives.WriteUInt16LittleEndian(head[24..], 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(head[26..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(head[28..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(head[30..], 9);
        BinaryPrimitives.WriteUInt16LittleEndian(head[32..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(head[44..], (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(head[48..], (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(head[56..], 0); // mini stream cutoff: every stream in regular sectors
        BinaryPrimitives.WriteUInt32LittleEndian(head[60..], EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(head[68..], EndOfChain);
        for (var i = 0; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(head[(76 + i * 4)..], i < fatSectors ? (uint)i : Free);
        for (var i = 0; i < fat.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(Sector + i * 4), fat[i]);
        directory.CopyTo(file, (fatSectors + 1) * Sector);
        foreach (var (path, data) in streams)
            data.CopyTo(file, (starts[path] + 1) * Sector);
        return file;
    }

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static void Chain(uint[] fat, int start, int count)
    {
        for (var i = 0; i < count; i++)
            fat[start + i] = i + 1 < count ? (uint)(start + i + 1) : EndOfChain;
    }

    private static void Write32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }
}
