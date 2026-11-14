// Writes archive formats Filee only reads, in code and without 7-Zip, so the in-process readers are tested with
// 7-Zip hidden: 7z (copy or LZMA coder, optionally an AES coder that marks it encrypted), xz (LZMA2 chunks: LZMA
// from SharpCompress's encoder or stored), RAR 4 and RAR 5 with stored entries, ISO 9660 with Joliet or Rock Ridge,
// cpio (newc, crc, odc, old binary), ar / DEB, RPM and Unix compress (.Z, an LZW encoder that pads its code groups
// as compress does), CAB (stored, MSZIP from .NET's deflate or a fixed-code encoder whose matches reach into the blocks
// before, LZX uncompressed blocks), LHA (header levels 0 to 3, stored or -lh5-/-lh6-/-lh7- from a small static Huffman
// encoder) and ARJ (stored, methods 1-3 with the same encoder, method 4). Each follows the format's own description;
// see the readers in Filee.Engines/Archives.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

/// <summary>One entry of a test archive; <see cref="Data"/> null for a folder.</summary>
internal sealed record TestEntry(string Name, byte[]? Data, DateTime? Modified = null)
{
    public bool IsFolder => Data is null;
    public DateTime Time => Modified ?? KnownTime;
}

/// <summary>How <see cref="ArchiveFormatBuilders.Cab"/> stores a folder's data.</summary>
public enum CabCompression
{
    None,

    /// <summary>MSZIP blocks from .NET's deflate, each on its own.</summary>
    Mszip,

    /// <summary>MSZIP blocks with the fixed codes whose matches reach back into the blocks before.</summary>
    MszipHistory,

    /// <summary>LZX uncompressed blocks.</summary>
    LzxStored,

    /// <summary>Declared Quantum (the data is stored, never decoded).</summary>
    Quantum,
}

internal static class ArchiveFormatBuilders
{
    // ───────────────────────── 7z ─────────────────────────

    /// <summary>
    /// A 7z archive with all files in one folder (solid) compressed with LZMA (or stored with the copy coder).
    /// <paramref name="encrypted"/> declares an AES coder in front (the data is then garbage, as without the key);
    /// <paramref name="declaredSize"/> replaces the sizes the header states (for bomb tests).
    /// </summary>
    public static byte[] SevenZip(IReadOnlyList<TestEntry> entries, bool lzma = true, bool encrypted = false, long? declaredSize = null)
    {
        var files = entries.Where(e => !e.IsFolder && e.Data!.Length > 0).ToList();
        var joined = files.SelectMany(f => f.Data!).ToArray();
        var packed = lzma ? RawLzma(joined, endMarker: false) : joined;

        var header = new MemoryStream();
        void Byte(int b) => header.WriteByte((byte)b);
        void Number(ulong value)
        {
            // 7z numbers: the count of leading 1 bits of the first byte says how many bytes follow.
            var extra = 0;
            while (extra < 8 && value >= 1UL << (7 * (extra + 1)))
                extra++;
            if (extra == 8)
            {
                Byte(0xFF);
                header.Write(BitConverter.GetBytes(value));
                return;
            }
            var first = (byte)((0xFF00 >> extra) & 0xFF);
            first |= (byte)(value >> (8 * extra));
            Byte(first);
            for (var i = 0; i < extra; i++)
                Byte((int)(value >> (8 * i)) & 0xFF);
        }

        Byte(0x01); // Header
        if (files.Count > 0)
        {
            Byte(0x04); // MainStreamsInfo
            Byte(0x06); // PackInfo
            Number(0);
            Number(1);
            Byte(0x09);
            Number((ulong)packed.Length);
            Byte(0x00);

            Byte(0x07); // UnpackInfo
            Byte(0x0B);
            Number(1);
            Byte(0x00);
            Number(encrypted ? 2UL : 1UL);
            if (lzma)
            {
                Byte(0x23); // id size 3, has properties
                header.Write([0x03, 0x01, 0x01]);
                Number(5);
                header.Write([0x5D, 0x00, 0x00, 0x10, 0x00]);
            }
            else
            {
                Byte(0x01);
                Byte(0x00); // copy
            }
            if (encrypted)
            {
                Byte(0x24); // AES-256 + SHA-256, id size 4, has properties
                header.Write([0x06, 0xF1, 0x07, 0x01]);
                Number(2);
                header.Write([0x03, 0x00]);
                // Bind pair: the first coder's input is the AES coder's output.
                Number(0);
                Number(1);
            }
            Byte(0x0C);
            var total = (ulong)(declaredSize ?? joined.Length);
            Number(total);
            if (encrypted)
                Number((ulong)packed.Length);
            Byte(0x00);

            Byte(0x08); // SubStreamsInfo
            Byte(0x0D);
            Number((ulong)files.Count);
            if (files.Count > 1)
            {
                Byte(0x09);
                foreach (var file in files.Take(files.Count - 1))
                    Number((ulong)(declaredSize ?? file.Data!.Length));
            }
            Byte(0x0A);
            Byte(0x01);
            foreach (var file in files)
                header.Write(BitConverter.GetBytes(Crc32.Compute(file.Data!)));
            Byte(0x00);
            Byte(0x00);
        }

        // FilesInfo: streams first in the order above, then folders and empty files.
        var ordered = files.Concat(entries.Where(e => e.IsFolder || e.Data!.Length == 0)).ToList();
        Byte(0x05);
        Number((ulong)ordered.Count);
        var empty = ordered.Select(e => e.IsFolder || e.Data!.Length == 0).ToArray();
        if (empty.Any(x => x))
        {
            var bits = BitVector(empty);
            Byte(0x0E);
            Number((ulong)bits.Length);
            header.Write(bits);
            var emptyFiles = BitVector([.. ordered.Where((_, i) => empty[i]).Select(e => !e.IsFolder)]);
            Byte(0x0F);
            Number((ulong)emptyFiles.Length);
            header.Write(emptyFiles);
        }
        var names = ordered.SelectMany(e => Encoding.Unicode.GetBytes(e.Name.TrimEnd('/') + "\0")).ToArray();
        Byte(0x11);
        Number((ulong)names.Length + 1);
        Byte(0x00);
        header.Write(names);
        Byte(0x14);
        Number((ulong)(ordered.Count * 8 + 2));
        Byte(0x01);
        Byte(0x00);
        foreach (var e in ordered)
            header.Write(BitConverter.GetBytes(e.Time.ToFileTimeUtc()));
        Byte(0x15);
        Number((ulong)(ordered.Count * 4 + 2));
        Byte(0x01);
        Byte(0x00);
        foreach (var e in ordered)
            header.Write(BitConverter.GetBytes(e.IsFolder ? 0x10u : 0x20u));
        Byte(0x00);
        Byte(0x00);

        var headerBytes = header.ToArray();
        var start = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(start, (ulong)packed.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(8), (ulong)headerBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(16), Crc32.Compute(headerBytes));
        using var output = new MemoryStream();
        output.Write([(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4]);
        output.Write(BitConverter.GetBytes(Crc32.Compute(start)));
        output.Write(start);
        output.Write(packed);
        output.Write(headerBytes);
        return output.ToArray();
    }

    private static byte[] BitVector(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i])
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }

    // ───────────────────────── xz ─────────────────────────

    /// <summary>
    /// An xz stream of <paramref name="data"/>: one block, LZMA2 with a CRC-32 check. Each 1 MiB slice is an
    /// independent LZMA chunk, or stored chunks when it does not compress.
    /// </summary>
    public static byte[] Xz(byte[] data)
    {
        using var output = new MemoryStream();
        output.Write([0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00]);
        byte[] flags = [0x00, 0x01];
        output.Write(flags);
        output.Write(BitConverter.GetBytes(Crc32.Compute(flags)));

        // Block header: size, flags (one filter), LZMA2 (id 0x21, 1 property byte: dictionary 1 MiB = 16).
        byte[] blockHeader = [0x02, 0x00, 0x21, 0x01, 16, 0x00, 0x00, 0x00];
        output.Write(blockHeader);
        output.Write(BitConverter.GetBytes(Crc32.Compute(blockHeader)));
        var blockStart = output.Position - 12;

        for (var offset = 0; offset < data.Length; offset += 1 << 20)
        {
            var slice = data.AsSpan(offset, Math.Min(1 << 20, data.Length - offset)).ToArray();
            var lzma = RawLzma(slice, endMarker: false);
            if (lzma.Length <= 60_000)
            {
                // Compressed chunk, dictionary and state reset with new properties.
                var unpacked = slice.Length - 1;
                var packed = lzma.Length - 1;
                output.WriteByte((byte)(0xE0 | (unpacked >> 16)));
                output.WriteByte((byte)(unpacked >> 8));
                output.WriteByte((byte)unpacked);
                output.WriteByte((byte)(packed >> 8));
                output.WriteByte((byte)packed);
                output.WriteByte(0x5D);
                output.Write(lzma);
            }
            else
            {
                for (var part = 0; part < slice.Length; part += 65536)
                {
                    var length = Math.Min(65536, slice.Length - part);
                    output.WriteByte(0x01); // stored, dictionary reset
                    output.WriteByte((byte)((length - 1) >> 8));
                    output.WriteByte((byte)(length - 1));
                    output.Write(slice, part, length);
                }
            }
        }
        output.WriteByte(0x00); // end of LZMA2 data
        var unpaddedSize = output.Position - blockStart + 4;
        while (output.Position % 4 != 0)
            output.WriteByte(0);
        output.Write(BitConverter.GetBytes(Crc32.Compute(data)));

        var index = new MemoryStream();
        index.WriteByte(0x00);
        Vli(index, 1);
        Vli(index, (ulong)unpaddedSize);
        Vli(index, (ulong)data.Length);
        while (index.Position % 4 != 0)
            index.WriteByte(0);
        var indexBytes = index.ToArray();
        output.Write(indexBytes);
        output.Write(BitConverter.GetBytes(Crc32.Compute(indexBytes)));

        var footer = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, (uint)((indexBytes.Length + 4) / 4 - 1));
        footer[4] = 0x00;
        footer[5] = 0x01;
        output.Write(BitConverter.GetBytes(Crc32.Compute(footer)));
        output.Write(footer);
        output.Write("YZ"u8);
        return output.ToArray();
    }

    private static void Vli(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    // ───────────────────────── RAR ─────────────────────────

    /// <summary>A RAR 4 archive with stored entries (ASCII names; folders flagged as directories).</summary>
    public static byte[] Rar4(IReadOnlyList<TestEntry> entries)
    {
        using var output = new MemoryStream();
        output.Write("Rar!\x1A\x07\x00"u8);
        WriteRar4Block(output, 0x73, 0x0000, new byte[6]);
        foreach (var entry in entries)
        {
            var name = Encoding.ASCII.GetBytes(entry.Name.TrimEnd('/').Replace('/', '\\'));
            var data = entry.Data ?? [];
            var body = new MemoryStream();
            var w = new BinaryWriter(body);
            w.Write((uint)data.Length); // packed size
            w.Write((uint)data.Length); // unpacked size
            w.Write((byte)2);           // Windows
            w.Write(Crc32.Compute(data));
            w.Write(DosTime(entry.Time));
            w.Write((byte)29);          // version 2.9
            w.Write((byte)0x30);        // stored
            w.Write((ushort)name.Length);
            w.Write(entry.IsFolder ? 0x10u : 0x20u);
            w.Write(name);
            WriteRar4Block(output, 0x74, (ushort)(0x8000 | (entry.IsFolder ? 0x00E0 : 0)), body.ToArray());
            output.Write(data);
        }
        WriteRar4Block(output, 0x7B, 0x4000, []);
        return output.ToArray();
    }

    private static void WriteRar4Block(Stream output, byte type, ushort flags, byte[] body)
    {
        var block = new byte[5 + body.Length];
        block[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(1), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(3), (ushort)(7 + body.Length));
        body.CopyTo(block, 5);
        output.Write(BitConverter.GetBytes((ushort)(Crc32.Compute(block) & 0xFFFF)));
        output.Write(block);
    }

    /// <summary>A RAR 5 archive with stored entries (UTF-8 names).</summary>
    public static byte[] Rar5(IReadOnlyList<TestEntry> entries)
    {
        using var output = new MemoryStream();
        output.Write("Rar!\x1A\x07\x01\x00"u8);
        WriteRar5Block(output, [1, 0, 0]); // main header: type 1, flags 0, archive flags 0
        foreach (var entry in entries)
        {
            var name = Encoding.UTF8.GetBytes(entry.Name.TrimEnd('/'));
            var data = entry.Data ?? [];
            var body = new MemoryStream();
            Vli(body, 2);                                 // file header
            Vli(body, entry.IsFolder ? 0UL : 0x02);       // header flags: data area present
            if (!entry.IsFolder)
                Vli(body, (ulong)data.Length);
            Vli(body, entry.IsFolder ? 0x03UL : 0x06);    // file flags: directory | mtime, or mtime | CRC32
            Vli(body, (ulong)data.Length);
            Vli(body, entry.IsFolder ? 0x10UL : 0x20);    // attributes
            body.Write(BitConverter.GetBytes((uint)new DateTimeOffset(entry.Time).ToUnixTimeSeconds()));
            if (!entry.IsFolder)
                body.Write(BitConverter.GetBytes(Crc32.Compute(data)));
            Vli(body, 0);                                 // compression: version 0, stored
            Vli(body, 0);                                 // Windows
            Vli(body, (ulong)name.Length);
            body.Write(name);
            WriteRar5Block(output, body.ToArray());
            output.Write(data);
        }
        WriteRar5Block(output, [5, 0, 0]); // end of archive
        return output.ToArray();
    }

    private static void WriteRar5Block(Stream output, byte[] body)
    {
        var block = new MemoryStream();
        Vli(block, (ulong)body.Length);
        block.Write(body);
        var bytes = block.ToArray();
        output.Write(BitConverter.GetBytes(Crc32.Compute(bytes)));
        output.Write(bytes);
    }

    // ───────────────────────── ISO 9660 ─────────────────────────

    /// <summary>What kind of names a test ISO carries besides the plain ISO 9660 ones.</summary>
    public enum IsoNames
    {
        Plain,
        Joliet,
        RockRidge,
    }

    /// <summary>
    /// An ISO 9660 image of <paramref name="entries"/> (folders implied by the file paths are added). With
    /// <paramref name="loop"/> a folder "loop" points back at the root; <paramref name="symlink"/> adds a Rock Ridge
    /// symbolic link named "link" pointing there.
    /// </summary>
    public static byte[] Iso(IReadOnlyList<TestEntry> entries, IsoNames names, bool loop = false, string? symlink = null, int multiExtent = 0)
    {
        // The tree: path → children.
        var folders = new SortedDictionary<string, List<(string Name, TestEntry? File)>>(StringComparer.Ordinal) { [""] = [] };
        void EnsureFolder(string path)
        {
            if (folders.ContainsKey(path))
                return;
            var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            EnsureFolder(parent);
            folders[path] = [];
            folders[parent].Add((path[(path.LastIndexOf('/') + 1)..], null));
        }
        foreach (var entry in entries)
        {
            var path = entry.Name.TrimEnd('/');
            if (entry.IsFolder)
            {
                EnsureFolder(path);
                continue;
            }
            var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            EnsureFolder(parent);
            folders[parent].Add((path[(path.LastIndexOf('/') + 1)..], entry));
        }

        // Layout: 16 system sectors, PVD, (SVD), terminator, then folders (one sector each, two trees), then files.
        var descriptorCount = names == IsoNames.Joliet ? 3 : 2;
        var next = 16 + descriptorCount;
        var folderSector = new Dictionary<string, int>();
        var jolietSector = new Dictionary<string, int>();
        foreach (var path in folders.Keys)
            folderSector[path] = next++;
        if (names == IsoNames.Joliet)
            foreach (var path in folders.Keys)
                jolietSector[path] = next++;
        var fileSector = new Dictionary<TestEntry, int>();
        foreach (var entry in entries.Where(e => !e.IsFolder))
        {
            fileSector[entry] = next;
            next += Math.Max(1, (entry.Data!.Length + 2047) / 2048);
        }
        var image = new byte[next * 2048L];

        byte[] Record(int sector, int size, byte flags, byte[] name, DateTime time, byte[]? systemUse = null)
        {
            systemUse ??= [];
            var length = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0) + systemUse.Length;
            length += length % 2;
            var r = new byte[length];
            r[0] = (byte)length;
            BothEndian32(r, 2, (uint)sector);
            BothEndian32(r, 10, (uint)size);
            var t = time.ToUniversalTime();
            r[18] = (byte)(t.Year - 1900);
            r[19] = (byte)t.Month;
            r[20] = (byte)t.Day;
            r[21] = (byte)t.Hour;
            r[22] = (byte)t.Minute;
            r[23] = (byte)t.Second;
            r[25] = flags;
            r[28] = 1;
            r[31] = 1;
            r[32] = (byte)name.Length;
            name.CopyTo(r, 33);
            systemUse.CopyTo(r, 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0));
            return r;
        }

        byte[] Rr(string name, bool folder, bool isRoot = false, string? linkTarget = null)
        {
            var su = new MemoryStream();
            if (isRoot)
                su.Write([(byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF, 0]);
            if (name.Length > 0)
            {
                var bytes = Encoding.UTF8.GetBytes(name);
                su.Write([(byte)'N', (byte)'M', (byte)(5 + bytes.Length), 1, 0]);
                su.Write(bytes);
            }
            var mode = linkTarget is not null ? 0xA1FFu : folder ? 0x41EDu : 0x81A4u;
            var px = new byte[36];
            px[0] = (byte)'P';
            px[1] = (byte)'X';
            px[2] = 36;
            px[3] = 1;
            BothEndian32(px, 4, mode);
            su.Write(px);
            if (linkTarget is not null)
            {
                var target = Encoding.UTF8.GetBytes(linkTarget);
                su.Write([(byte)'S', (byte)'L', (byte)(5 + 2 + target.Length), 1, 0, 0, (byte)target.Length]);
                su.Write(target);
            }
            return su.ToArray();
        }

        string IsoName(string name, bool folder)
        {
            var upper = new string([.. name.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '_')]);
            return folder ? upper : upper.Contains('.') ? upper + ";1" : upper + ".;1";
        }

        void WriteTree(Dictionary<string, int> sectors, bool joliet)
        {
            foreach (var (path, children) in folders)
            {
                var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                var rr = names == IsoNames.RockRidge && !joliet;
                var records = new List<byte[]>
                {
                    Record(sectors[path], 2048, 2, [0], KnownTime, rr ? Rr("", true, isRoot: path.Length == 0) : null),
                    Record(sectors[path.Length == 0 ? "" : parent], 2048, 2, [1], KnownTime, rr ? Rr("", true) : null),
                };
                foreach (var (name, file) in children.OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    var folder = file is null;
                    var childPath = path.Length == 0 ? name : path + "/" + name;
                    var encoded = joliet ? Encoding.BigEndianUnicode.GetBytes(folder ? name : name + ";1") : Encoding.ASCII.GetBytes(IsoName(name, folder));
                    var su = rr ? Rr(name, folder) : null;
                    if (folder)
                    {
                        records.Add(Record(sectors[childPath], 2048, 2, encoded, KnownTime, su));
                    }
                    else if (multiExtent > 0 && file!.Data!.Length > multiExtent)
                    {
                        // The file in two extents: the first record says more follow.
                        records.Add(Record(fileSector[file], multiExtent, 0x80, encoded, file.Time, su));
                        records.Add(Record(fileSector[file] + multiExtent / 2048, file.Data.Length - multiExtent, 0, encoded, file.Time, su));
                    }
                    else
                    {
                        records.Add(Record(fileSector[file!], file!.Data!.Length, 0, encoded, file.Time, su));
                    }
                }
                if (path.Length == 0 && loop)
                    records.Add(Record(sectors[""], 2048, 2, joliet ? Encoding.BigEndianUnicode.GetBytes("loop") : "LOOP"u8.ToArray(), KnownTime, names == IsoNames.RockRidge && !joliet ? Rr("loop", true) : null));
                if (path.Length == 0 && symlink is not null && rr)
                    records.Add(Record(fileSector.Values.DefaultIfEmpty(0).First(), 0, 0, "LINK.;1"u8.ToArray(), KnownTime, Rr("link", false, linkTarget: symlink)));
                var offset = sectors[path] * 2048;
                foreach (var record in records)
                {
                    Assert.True(offset + record.Length <= (sectors[path] + 1) * 2048, "test ISO folder larger than one sector");
                    record.CopyTo(image, offset);
                    offset += record.Length;
                }
            }
        }

        void Descriptor(int sector, byte type, Dictionary<string, int> sectors, bool joliet)
        {
            var d = image.AsSpan(sector * 2048, 2048);
            d[0] = type;
            "CD001"u8.CopyTo(d[1..]);
            d[6] = 1;
            if (type == 255)
                return;
            Encoding.ASCII.GetBytes("FILEE_TEST".PadRight(32)).CopyTo(d[40..]);
            var total = (uint)(image.Length / 2048);
            BothEndian32(d, 80, total);
            if (joliet)
                "%/E"u8.CopyTo(d[88..]);
            d[120] = 1;
            d[123] = 1;
            d[124] = 1;
            d[127] = 1;
            d[128] = 0x00;
            d[129] = 0x08;
            d[130] = 0x08;
            d[131] = 0x00;
            Record(sectors[""], 2048, 2, [0], KnownTime).AsSpan(0, 34).CopyTo(d[156..]);
            d[156] = 34;
            d[881] = 1;
        }

        WriteTree(folderSector, joliet: false);
        if (names == IsoNames.Joliet)
            WriteTree(jolietSector, joliet: true);
        Descriptor(16, 1, folderSector, joliet: false);
        if (names == IsoNames.Joliet)
            Descriptor(17, 2, jolietSector, joliet: true);
        Descriptor(16 + descriptorCount - 1, 255, folderSector, joliet: false);
        foreach (var (entry, sector) in fileSector)
            entry.Data!.CopyTo(image, sector * 2048L);
        return image;
    }

    private static void BothEndian32(Span<byte> bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[(offset + 4)..], value);
    }

    private static void BothEndian32(byte[] bytes, int offset, uint value) => BothEndian32(bytes.AsSpan(), offset, value);

    // ───────────────────────── cpio, ar, RPM ─────────────────────────

    public enum CpioLayout
    {
        Newc,
        Crc,
        Odc,
        Binary,
    }

    /// <summary>A cpio archive; <paramref name="links"/> adds "./link" as a symbolic link to "../../outside".</summary>
    public static byte[] Cpio(IReadOnlyList<TestEntry> entries, CpioLayout layout = CpioLayout.Newc, bool links = false)
    {
        using var output = new MemoryStream();
        var inode = 1;
        void Write(string name, int mode, byte[] data, DateTime time, int nlink = 1, int? ino = null)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            var mtime = new DateTimeOffset(time).ToUnixTimeSeconds();
            var number = ino ?? inode++;
            switch (layout)
            {
                case CpioLayout.Newc:
                case CpioLayout.Crc:
                    {
                        var check = layout == CpioLayout.Crc ? (uint)data.Sum(b => (long)b) : 0;
                        var header = $"{(layout == CpioLayout.Crc ? "070702" : "070701")}{number:X8}{mode:X8}{0:X8}{0:X8}{nlink:X8}{mtime:X8}{data.Length:X8}{0:X8}{0:X8}{0:X8}{0:X8}{nameBytes.Length:X8}{check:X8}";
                        output.Write(Encoding.ASCII.GetBytes(header));
                        output.Write(nameBytes);
                        Pad(4);
                        output.Write(data);
                        Pad(4);
                        break;
                    }
                case CpioLayout.Odc:
                    {
                        string Oct(long v, int width) => Convert.ToString(v, 8).PadLeft(width, '0');
                        output.Write(Encoding.ASCII.GetBytes("070707" + Oct(0, 6) + Oct(number, 6) + Oct(mode, 6) + Oct(0, 6) + Oct(0, 6)
                                                             + Oct(nlink, 6) + Oct(0, 6) + Oct(mtime, 11) + Oct(nameBytes.Length, 6) + Oct(data.Length, 11)));
                        output.Write(nameBytes);
                        output.Write(data);
                        break;
                    }
                default:
                    {
                        var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
                        w.Write((ushort)0x71C7);
                        w.Write((ushort)0);
                        w.Write((ushort)number);
                        w.Write((ushort)mode);
                        w.Write((ushort)0);
                        w.Write((ushort)0);
                        w.Write((ushort)nlink);
                        w.Write((ushort)0);
                        w.Write((ushort)(mtime >> 16));
                        w.Write((ushort)mtime);
                        w.Write((ushort)nameBytes.Length);
                        w.Write((ushort)(data.Length >> 16));
                        w.Write((ushort)data.Length);
                        w.Flush();
                        output.Write(nameBytes);
                        Pad(2);
                        output.Write(data);
                        Pad(2);
                        break;
                    }
            }
        }
        void Pad(int to)
        {
            while (output.Position % to != 0)
                output.WriteByte(0);
        }

        foreach (var entry in entries)
            Write("./" + entry.Name.TrimEnd('/'), entry.IsFolder ? 0x41ED : 0x81A4, entry.Data ?? [], entry.Time);
        if (links)
            Write("./link", 0xA1FF, "../../outside"u8.ToArray(), KnownTime);
        Write("TRAILER!!!", 0, [], DateTime.UnixEpoch);
        return output.ToArray();
    }

    /// <summary>A newc cpio with a hard link group: "a.txt" and "b.txt" share an inode, the data comes with "b.txt".</summary>
    public static byte[] CpioWithHardLinks(byte[] data)
    {
        using var output = new MemoryStream();
        void Write(string name, int nlink, byte[] content)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            output.Write(Encoding.ASCII.GetBytes($"070701{7:X8}{0x81A4:X8}{0:X8}{0:X8}{nlink:X8}{0:X8}{content.Length:X8}{0:X8}{1:X8}{0:X8}{0:X8}{nameBytes.Length:X8}{0:X8}"));
            output.Write(nameBytes);
            while (output.Position % 4 != 0)
                output.WriteByte(0);
            output.Write(content);
            while (output.Position % 4 != 0)
                output.WriteByte(0);
        }
        Write("a.txt", 2, []);
        Write("b.txt", 2, data);
        output.Write(Encoding.ASCII.GetBytes($"070701{0:X8}{0:X8}{0:X8}{0:X8}{1:X8}{0:X8}{0:X8}{0:X8}{0:X8}{0:X8}{0:X8}{11:X8}{0:X8}"));
        output.Write("TRAILER!!!\0"u8);
        return output.ToArray();
    }

    /// <summary>An ar archive (GNU long names for members over 15 characters).</summary>
    public static byte[] Ar(IReadOnlyList<(string Name, byte[] Data)> members)
    {
        using var output = new MemoryStream();
        output.Write("!<arch>\n"u8);
        var longNames = new StringBuilder();
        var offsets = new Dictionary<string, int>();
        foreach (var (name, _) in members.Where(m => m.Name.Length > 15))
        {
            offsets[name] = Encoding.UTF8.GetByteCount(longNames.ToString());
            longNames.Append(name).Append("/\n");
        }
        void Member(string field, byte[] data)
        {
            var header = $"{field,-16}{new DateTimeOffset(KnownTime).ToUnixTimeSeconds(),-12}{0,-6}{0,-6}{"100644",-8}{data.Length,-10}`\n";
            output.Write(Encoding.ASCII.GetBytes(header));
            output.Write(data);
            if (output.Position % 2 != 0)
                output.WriteByte((byte)'\n');
        }
        if (longNames.Length > 0)
            Member("//", Encoding.UTF8.GetBytes(longNames.ToString()));
        foreach (var (name, data) in members)
            Member(name.Length > 15 ? "/" + offsets[name] : name + "/", data);
        return output.ToArray();
    }

    /// <summary>A Debian package: debian-binary, control.tar.gz and data.tar.xz.</summary>
    public static byte[] Deb(byte[] controlTarGz, byte[] dataTarXz) =>
        Ar([("debian-binary", "2.0\n"u8.ToArray()), ("control.tar.gz", controlTarGz), ("data.tar.xz", dataTarXz)]);

    /// <summary>An RPM package (lead, empty signature and main headers) with <paramref name="payload"/> as it is.</summary>
    public static byte[] Rpm(byte[] payload)
    {
        using var output = new MemoryStream();
        var lead = new byte[96];
        BinaryPrimitives.WriteUInt32BigEndian(lead, 0xEDABEEDB);
        lead[4] = 3;
        Encoding.ASCII.GetBytes("filee-test-1.0-1").CopyTo(lead, 10);
        output.Write(lead);
        byte[] header = [0x8E, 0xAD, 0xE8, 0x01, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 16];
        var entry = new byte[16]; // one index entry (tag 1000, type 6 string, offset 0, count 1)
        BinaryPrimitives.WriteUInt32BigEndian(entry, 1000);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4), 6);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(12), 1);
        var data = Encoding.ASCII.GetBytes("filee-test\0\0\0\0\0\0");
        output.Write(header);
        output.Write(entry);
        output.Write(data);
        while (output.Position % 8 != 0)
            output.WriteByte(0);
        output.Write(header);
        output.Write(entry);
        output.Write(data);
        output.Write(payload);
        return output.ToArray();
    }

    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data);
        return ms.ToArray();
    }

    /// <summary>A TAR (PAX) of <paramref name="entries"/>.</summary>
    public static byte[] Tar(IReadOnlyList<TestEntry> entries)
    {
        using var ms = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(ms, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                var entry = new System.Formats.Tar.PaxTarEntry(e.IsFolder ? System.Formats.Tar.TarEntryType.Directory : System.Formats.Tar.TarEntryType.RegularFile, e.Name)
                {
                    ModificationTime = e.Time,
                };
                if (!e.IsFolder)
                    entry.DataStream = new MemoryStream(e.Data!);
                writer.WriteEntry(entry);
            }
        }
        return ms.ToArray();
    }

    // ───────────────────────── Unix compress (.Z) ─────────────────────────

    /// <summary>
    /// LZW as compress(1) writes it: block mode, codes up to <paramref name="maxBits"/> bits, the table cleared (code
    /// 256) when full, and the current group of eight codes padded out whenever the width changes or the table clears.
    /// </summary>
    public static byte[] UnixCompress(byte[] data, int maxBits = 16)
    {
        using var output = new MemoryStream();
        output.Write([0x1F, 0x9D, (byte)(0x80 | maxBits)]);
        var bits = 9;
        var maxCode = (1 << bits) - 1;
        var maxMaxCode = 1 << maxBits;
        var freeEntry = 257;
        var clearFlag = false;
        var table = new Dictionary<int, int>();
        var group = new byte[16];
        var offset = 0; // bits used in the current group

        void Output(int code)
        {
            for (var i = 0; i < bits; i++)
                if ((code >> i & 1) != 0)
                    group[(offset + i) >> 3] |= (byte)(1 << ((offset + i) & 7));
            offset += bits;
            if (offset == bits * 8)
            {
                output.Write(group, 0, bits);
                Array.Clear(group);
                offset = 0;
            }
            if (freeEntry > maxCode || clearFlag)
            {
                if (offset > 0)
                {
                    output.Write(group, 0, bits); // the rest of the group is padding
                    Array.Clear(group);
                }
                offset = 0;
                if (clearFlag)
                {
                    bits = 9;
                    maxCode = (1 << bits) - 1;
                    clearFlag = false;
                }
                else
                {
                    bits++;
                    maxCode = bits == maxBits ? maxMaxCode : (1 << bits) - 1;
                }
            }
        }

        if (data.Length == 0)
            return output.ToArray();
        int entry = data[0];
        for (var i = 1; i < data.Length; i++)
        {
            int c = data[i];
            var key = c << 16 | entry;
            if (table.TryGetValue(key, out var found))
            {
                entry = found;
                continue;
            }
            Output(entry);
            entry = c;
            if (freeEntry < maxMaxCode)
            {
                table[key] = freeEntry++;
            }
            else
            {
                table.Clear();
                freeEntry = 257;
                clearFlag = true;
                Output(256);
            }
        }
        Output(entry);
        if (offset > 0)
            output.Write(group, 0, (offset + 7) / 8);
        return output.ToArray();
    }

    // ───────────────────────── CAB ─────────────────────────

    /// <summary>
    /// A cabinet ([MS-CAB]) of the files of <paramref name="entries"/> (CAB has no folder entries), split over
    /// <paramref name="folders"/> CFFOLDERs (file i in folder i mod n). <paramref name="reserve"/> adds the optional
    /// reserved areas (header, folder and data, filled with non-zero bytes). <paramref name="checksums"/> false writes 0
    /// (no checksum). <paramref name="window"/> is the LZX window, also written for Quantum.
    /// </summary>
    public static byte[] Cab(IReadOnlyList<TestEntry> entries, CabCompression compression, int folders = 1, bool reserve = false,
        bool checksums = true, int window = 16)
    {
        var files = entries.Where(e => !e.IsFolder).ToList();
        const int headerReserve = 6, folderReserve = 3, dataReserve = 5;
        var headerSize = 36 + (reserve ? 4 + headerReserve : 0);
        var folderSize = 8 + (reserve ? folderReserve : 0);
        var filesOffset = headerSize + folders * folderSize;

        var cfFiles = new MemoryStream();
        var data = new MemoryStream();
        var folderRecords = new List<(long Offset, int Blocks)>();
        var dataStart = filesOffset + files.Sum(f => 16 + Encoding.UTF8.GetByteCount(f.Name) + 1);
        for (var folder = 0; folder < folders; folder++)
        {
            var mine = files.Where((_, i) => i % folders == folder).ToList();
            var joined = mine.SelectMany(f => f.Data!).ToArray();
            long offset = 0;
            foreach (var file in mine)
            {
                var name = Encoding.UTF8.GetBytes(file.Name.Replace('/', '\\'));
                var fields = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(fields, (uint)file.Data!.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(4), (uint)offset);
                BinaryPrimitives.WriteUInt16LittleEndian(fields.AsSpan(8), (ushort)folder);
                var dos = DosTime(file.Time);
                BinaryPrimitives.WriteUInt16LittleEndian(fields.AsSpan(10), (ushort)(dos >> 16));
                BinaryPrimitives.WriteUInt16LittleEndian(fields.AsSpan(12), (ushort)dos);
                BinaryPrimitives.WriteUInt16LittleEndian(fields.AsSpan(14), (ushort)(name.Any(b => b >= 0x80) ? 0xA0 : 0x20));
                cfFiles.Write(fields);
                cfFiles.Write(name);
                cfFiles.WriteByte(0);
                offset += file.Data.Length;
            }

            var blocks = CabBlocks(joined, compression);
            folderRecords.Add((dataStart + data.Length, blocks.Count));
            foreach (var (packed, unpacked) in blocks)
            {
                var header = new byte[8 + (reserve ? dataReserve : 0)];
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)packed.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)unpacked);
                if (reserve)
                    header.AsSpan(8).Fill(0xAB);
                if (checksums)
                    BinaryPrimitives.WriteUInt32LittleEndian(header, CabReader.Checksum(header.AsSpan(4), CabReader.Checksum(packed, 0))); // sizes and reserve
                data.Write(header);
                data.Write(packed);
            }
        }

        var output = new MemoryStream();
        var head = new byte[headerSize];
        "MSCF"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), (uint)(dataStart + data.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(16), (uint)filesOffset);
        head[24] = 3;
        head[25] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(26), (ushort)folders);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(28), (ushort)files.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(30), (ushort)(reserve ? 4 : 0));
        if (reserve)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(36), headerReserve);
            head[38] = folderReserve;
            head[39] = dataReserve;
            head.AsSpan(40).Fill(0xCD);
        }
        output.Write(head);
        var type = compression switch
        {
            CabCompression.None => 0,
            CabCompression.Quantum => 2 | (window << 8),
            CabCompression.LzxStored => 3 | (window << 8),
            _ => 1,
        };
        foreach (var (offset, count) in folderRecords)
        {
            var record = new byte[folderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)offset);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)count);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)type);
            if (reserve)
                record.AsSpan(8).Fill(0xEF);
            output.Write(record);
        }
        output.Write(cfFiles.ToArray());
        output.Write(data.ToArray());
        return output.ToArray();
    }

    /// <summary>The CFDATA payloads (and unpacked sizes) of one folder's data.</summary>
    private static List<(byte[] Packed, int Unpacked)> CabBlocks(byte[] joined, CabCompression compression)
    {
        const int block = 32768;
        var result = new List<(byte[], int)>();
        if (compression == CabCompression.LzxStored)
        {
            // One LZX uncompressed block over the whole folder: E8 bit 0, type 3, 24-bit size, 4 bits of padding up to
            // the 16-bit word (each word little endian), R0-R2, the bytes, a pad byte after an odd count. Cut at the
            // 32 KB frames.
            var stream = new MemoryStream();
            var bits = (3u << 28) | ((uint)joined.Length << 4);
            stream.Write([(byte)(bits >> 16), (byte)(bits >> 24), (byte)bits, (byte)(bits >> 8)]);
            stream.Write([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]);
            stream.Write(joined);
            if (joined.Length % 2 == 1)
                stream.WriteByte(0);
            var lzx = stream.ToArray();
            var at = 0;
            for (var start = 0; start < joined.Length; start += block)
            {
                var last = start + block >= joined.Length;
                var take = last ? lzx.Length - at : (start == 0 ? 16 : 0) + block;
                result.Add((lzx[at..(at + take)], Math.Min(block, joined.Length - start)));
                at += take;
            }
            return result;
        }
        for (var start = 0; start < joined.Length; start += block)
        {
            var end = Math.Min(joined.Length, start + block);
            byte[] packed = compression switch
            {
                CabCompression.None or CabCompression.Quantum => joined[start..end], // Quantum: never decoded
                CabCompression.Mszip => [(byte)'C', (byte)'K', .. RawDeflate(joined[start..end])],
                _ => [(byte)'C', (byte)'K', .. FixedDeflate(joined, start, end)],
            };
            result.Add((packed, end - start));
        }
        return result;
    }

    private static byte[] RawDeflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        return ms.ToArray();
    }

    private static readonly int[] DeflateLengthBase =
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static readonly int[] DeflateDistanceBase =
        [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    /// <summary>
    /// One deflate block with the fixed codes (RFC 1951) for <paramref name="data"/>[start..end), greedy matches that may
    /// reach up to 32 KB back, into the bytes before <paramref name="start"/> (the MSZIP blocks before).
    /// </summary>
    public static byte[] FixedDeflate(byte[] data, int start, int end)
    {
        var writer = new LsbWriter();
        writer.Write(1, 1); // final
        writer.Write(1, 2); // fixed codes
        void Symbol(int symbol)
        {
            var (code, length) = symbol switch
            {
                < 144 => (0x30 + symbol, 8),
                < 256 => (0x190 + symbol - 144, 9),
                < 280 => (symbol - 256, 7),
                _ => (0xC0 + symbol - 280, 8),
            };
            writer.WriteCode(code, length);
        }
        var last = new Dictionary<int, int>();
        int Key(int at) => data[at] | data[at + 1] << 8 | data[at + 2] << 16;
        for (var i = Math.Max(0, start - 32768); i < start && i + 3 <= data.Length; i++)
            last[Key(i)] = i;
        for (var pos = start; pos < end;)
        {
            var length = 0;
            var distance = 0;
            if (pos + 3 <= end && last.TryGetValue(Key(pos), out var candidate) && pos - candidate <= 32768)
            {
                while (length < 258 && pos + length < end && data[candidate + length] == data[pos + length])
                    length++;
                distance = pos - candidate;
            }
            if (length < 3)
            {
                if (pos + 3 <= data.Length)
                    last[Key(pos)] = pos;
                Symbol(data[pos++]);
                continue;
            }
            var l = Array.FindLastIndex(DeflateLengthBase, b => b <= length);
            Symbol(257 + l);
            writer.Write(length - DeflateLengthBase[l], l is >= 8 and < 28 ? (l - 4) / 4 : 0);
            var d = Array.FindLastIndex(DeflateDistanceBase, b => b <= distance);
            writer.WriteCode(d, 5);
            writer.Write(distance - DeflateDistanceBase[d], d >= 4 ? (d - 2) / 2 : 0);
            for (var k = 0; k < length; k++)
                if (pos + k + 3 <= data.Length)
                    last[Key(pos + k)] = pos + k;
            pos += length;
        }
        Symbol(256);
        return writer.ToArray();
    }

    /// <summary>Bits least significant first (deflate).</summary>
    private sealed class LsbWriter
    {
        private readonly MemoryStream _output = new();
        private ulong _bits;
        private int _count;

        public void Write(int value, int count)
        {
            _bits |= (ulong)(uint)value << _count;
            _count += count;
            while (_count >= 8)
            {
                _output.WriteByte((byte)_bits);
                _bits >>= 8;
                _count -= 8;
            }
        }

        /// <summary>A Huffman code, sent most significant bit first.</summary>
        public void WriteCode(int code, int length)
        {
            for (var i = length - 1; i >= 0; i--)
                Write((code >> i) & 1, 1);
        }

        public byte[] ToArray()
        {
            if (_count > 0)
                Write(0, 8 - _count);
            return _output.ToArray();
        }
    }

    // ───────────────────────── LHA / ARJ ─────────────────────────

    /// <summary>
    /// An LHA archive with headers of <paramref name="level"/> (0 to 3) and the data in <paramref name="method"/>
    /// ("-lh0-", "-lh5-", "-lh6-", "-lh7-"); folders are "-lhd-" entries. Level 0 names use "\" between folders, level 1
    /// puts the folder into an extended header, levels 2 and 3 use the 0x01 / 0x02 extended headers.
    /// </summary>
    public static byte[] Lzh(IReadOnlyList<TestEntry> entries, string method = "-lh5-", int level = 2)
    {
        var output = new MemoryStream();
        foreach (var entry in entries)
        {
            var data = entry.Data ?? [];
            var entryMethod = entry.IsFolder ? "-lhd-" : method;
            var packed = entryMethod switch
            {
                "-lh5-" => LzhCompress(data, 14, 4, 8192),
                "-lh6-" => LzhCompress(data, 16, 5, 32768),
                "-lh7-" => LzhCompress(data, 17, 5, 65536),
                _ => data,
            };
            var crc = Crc16(data);
            var path = entry.Name.TrimEnd('/');
            var slash = path.LastIndexOf('/');
            var (folder, leaf) = entry.IsFolder ? (path, "") : (slash < 0 ? "" : path[..slash], path[(slash + 1)..]);
            var folderBytes = Encoding.UTF8.GetBytes(folder).Select(b => b == '/' ? (byte)0xFF : b).Append((byte)0xFF).ToArray();
            var methodBytes = Encoding.ASCII.GetBytes(entryMethod);
            if (level <= 1)
            {
                var name = Encoding.UTF8.GetBytes(level == 0 ? path.Replace('/', '\\') + (entry.IsFolder ? "\\" : "") : leaf);
                var (extended, first) = Extended(level == 1 && folder.Length > 0 ? [(0x02, folderBytes)] : [], wide: false);
                var body = new MemoryStream();
                body.Write(methodBytes);
                body.Write(BitConverter.GetBytes((uint)(packed.Length + extended.Length)));
                body.Write(BitConverter.GetBytes((uint)data.Length));
                body.Write(BitConverter.GetBytes(DosTime(entry.Time)));
                body.WriteByte((byte)(level == 0 && entry.IsFolder ? 0x10 : 0x20));
                body.WriteByte((byte)level);
                body.WriteByte((byte)name.Length);
                body.Write(name);
                body.Write(BitConverter.GetBytes(crc));
                if (level == 1)
                {
                    body.WriteByte((byte)'M');
                    body.Write(BitConverter.GetBytes((ushort)first));
                }
                var bytes = body.ToArray();
                output.WriteByte((byte)bytes.Length);
                output.WriteByte((byte)bytes.Sum(b => b));
                output.Write(bytes);
                output.Write(extended);
            }
            else
            {
                var wide = level == 3;
                List<(byte, byte[])> list = [];
                if (level == 2)
                    list.Add((0x00, [0, 0])); // header CRC, filled in below
                if (leaf.Length > 0)
                    list.Add((0x01, Encoding.UTF8.GetBytes(leaf)));
                if (folder.Length > 0)
                    list.Add((0x02, folderBytes));
                var (extended, first) = Extended(list, wide);
                var fixedSize = wide ? 32 : 26;
                var total = fixedSize + extended.Length;
                var pad = !wide && (total & 0xFF) == 0 ? 1 : 0; // LHA never lets the first byte be 0
                var header = new byte[total + pad];
                BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(wide ? 4 : total + pad));
                methodBytes.CopyTo(header, 2);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(7), (uint)packed.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(11), (uint)data.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(15), (uint)new DateTimeOffset(entry.Time).ToUnixTimeSeconds());
                header[19] = 0x20;
                header[20] = (byte)level;
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(21), crc);
                header[23] = (byte)'U';
                if (wide)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)total);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)first);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), (ushort)first);
                }
                extended.CopyTo(header, fixedSize);
                if (level == 2)
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(fixedSize + 1), Crc16(header));
                output.Write(header);
            }
            output.Write(packed);
        }
        output.WriteByte(0);
        return output.ToArray();
    }

    /// <summary>
    /// A chain of LHA extended headers (type, data, size of the next one; 0 after the last) and the size of the first,
    /// which the base header states.
    /// </summary>
    private static (byte[] Bytes, int First) Extended(List<(byte Type, byte[] Data)> headers, bool wide)
    {
        var sizeField = wide ? 4 : 2;
        var output = new MemoryStream();
        for (var i = 0; i < headers.Count; i++)
        {
            output.WriteByte(headers[i].Type);
            output.Write(headers[i].Data);
            var next = i + 1 < headers.Count ? 1 + headers[i + 1].Data.Length + sizeField : 0;
            output.Write(wide ? BitConverter.GetBytes((uint)next) : BitConverter.GetBytes((ushort)next));
        }
        return (output.ToArray(), headers.Count == 0 ? 0 : 1 + headers[0].Data.Length + sizeField);
    }

    /// <summary>CRC-16 as LHA uses it (ARC: reflected polynomial 0xA001, start 0).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }

    /// <summary>
    /// An ARJ archive: its main header, one local header per entry (method 0 stored, 1 to 3 the LZH codes, 4 "fastest"),
    /// folders as type 3 entries, and the empty end header. <paramref name="flags"/> go on every local header (0x01 marks
    /// a password, 0x04 a file continued in the next volume).
    /// </summary>
    public static byte[] Arj(IReadOnlyList<TestEntry> entries, int method = 1, byte flags = 0)
    {
        var output = new MemoryStream();
        void Header(byte[] basic)
        {
            output.Write([0x60, 0xEA]);
            output.Write(BitConverter.GetBytes((ushort)basic.Length));
            output.Write(basic);
            output.Write(BitConverter.GetBytes(Crc32.Compute(basic)));
            output.Write([0, 0]); // no extended header
        }
        static byte[] Basic(byte type, byte method, byte flags, uint time, uint packed, uint size, uint crc, string name)
        {
            var basic = new byte[30];
            basic[0] = 30;
            basic[1] = 11;
            basic[2] = 1;
            basic[4] = flags;
            basic[5] = method;
            basic[6] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(8), time);
            BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(12), packed);
            BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(16), size);
            BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(20), crc);
            return [.. basic, .. Encoding.UTF8.GetBytes(name), 0, 0];
        }

        Header(Basic(2, 0, 0, DosTime(KnownTime), 0, 0, 0, "test.arj"));
        foreach (var entry in entries)
        {
            var data = entry.Data ?? [];
            var packed = entry.IsFolder ? [] : method switch
            {
                0 => data,
                4 => ArjFastest(data),
                _ => LzhCompress(data, 17, 5, 26624),
            };
            Header(Basic(entry.IsFolder ? (byte)3 : (byte)0, entry.IsFolder ? (byte)0 : (byte)method, (byte)(flags | 0x10), DosTime(entry.Time),
                (uint)packed.Length, (uint)data.Length, Crc32.Compute(data), entry.Name.TrimEnd('/')));
            output.Write(packed);
        }
        output.Write([0x60, 0xEA, 0, 0]);
        return output.ToArray();
    }

    /// <summary>Greedy LZ77: a literal (length 0, the byte) or a match (length, distance).</summary>
    private static List<(int Length, int Value)> Lz77(byte[] data, int maxDistance, int maxLength)
    {
        var items = new List<(int, int)>();
        var last = new Dictionary<int, int>();
        int Key(int at) => data[at] | data[at + 1] << 8 | data[at + 2] << 16;
        for (var pos = 0; pos < data.Length;)
        {
            var length = 0;
            var distance = 0;
            if (pos + 3 <= data.Length && last.TryGetValue(Key(pos), out var candidate) && pos - candidate <= maxDistance)
            {
                while (length < maxLength && pos + length < data.Length && data[candidate + length] == data[pos + length])
                    length++;
                distance = pos - candidate;
            }
            if (length < 3)
            {
                if (pos + 3 <= data.Length)
                    last[Key(pos)] = pos;
                items.Add((0, data[pos++]));
                continue;
            }
            for (var k = 0; k < length; k++)
                if (pos + k + 3 <= data.Length)
                    last[Key(pos + k)] = pos + k;
            items.Add((length, distance));
            pos += length;
        }
        return items;
    }

    /// <summary>
    /// The static Huffman LZSS of LHA -lh5- to -lh7- and ARJ methods 1 to 3 (see LzhDecoder): blocks of up to 16,000
    /// codes, each with its pretree, literal/length and position tables.
    /// </summary>
    public static byte[] LzhCompress(byte[] data, int positionSymbols, int positionBits, int maxDistance)
    {
        var writer = new MsbWriter();
        var items = Lz77(data, maxDistance, 256);
        for (var start = 0; start < items.Count; start += 16_000)
        {
            var block = items.GetRange(start, Math.Min(16_000, items.Count - start));
            var literalFrequency = new int[510];
            var positionFrequency = new int[positionSymbols];
            foreach (var (length, value) in block)
            {
                if (length == 0)
                {
                    literalFrequency[value]++;
                    continue;
                }
                literalFrequency[256 + length - 3]++;
                positionFrequency[BitLength(value - 1)]++;
            }
            var literalLengths = HuffmanLengths(literalFrequency, 16);
            var positionLengths = HuffmanLengths(positionFrequency, 16);

            writer.Write(block.Count, 16);
            if (literalLengths.Count(l => l > 0) == 1)
            {
                // One literal/length symbol: an empty pretree and the symbol itself, sent without bits.
                writer.Write(0, 5);
                writer.Write(0, 5);
                writer.Write(0, 9);
                writer.Write(Array.FindIndex(literalFrequency, f => f > 0), 9);
                literalLengths = new byte[510];
            }
            else
            {
                WriteLiteralLengths(writer, literalLengths);
            }
            WriteShortLengths(writer, positionLengths, positionFrequency, positionBits, special: -1);
            if (positionLengths.Count(l => l > 0) == 1)
                positionLengths = new byte[positionSymbols];

            var literalCodes = CanonicalCodes(literalLengths);
            var positionCodes = CanonicalCodes(positionLengths);
            foreach (var (length, value) in block)
            {
                var symbol = length == 0 ? value : 256 + length - 3;
                writer.Write(literalCodes[symbol], literalLengths[symbol]);
                if (length == 0)
                    continue;
                var p = value - 1;
                var slot = BitLength(p);
                writer.Write(positionCodes[slot], positionLengths[slot]);
                if (slot > 1)
                    writer.Write(p - (1 << (slot - 1)), slot - 1);
            }
        }
        return writer.ToArray();
    }

    /// <summary>The literal/length code lengths: the pretree, then the lengths through it with runs of zeros.</summary>
    private static void WriteLiteralLengths(MsbWriter writer, byte[] lengths)
    {
        var count = Array.FindLastIndex(lengths, l => l > 0) + 1;
        var items = new List<(int Symbol, int Extra, int ExtraBits)>();
        for (var i = 0; i < count;)
        {
            if (lengths[i] != 0)
            {
                items.Add((lengths[i++] + 2, 0, 0));
                continue;
            }
            var run = 0;
            while (i + run < count && lengths[i + run] == 0)
                run++;
            i += run;
            if (run <= 2)
            {
                for (var k = 0; k < run; k++)
                    items.Add((0, 0, 0));
            }
            else if (run <= 18)
            {
                items.Add((1, run - 3, 4));
            }
            else if (run == 19)
            {
                items.Add((0, 0, 0));
                items.Add((1, 15, 4));
            }
            else
            {
                items.Add((2, run - 20, 9));
            }
        }
        var frequency = new int[19];
        foreach (var item in items)
            frequency[item.Symbol]++;
        var pretree = HuffmanLengths(frequency, 16);
        WriteShortLengths(writer, pretree, frequency, 5, special: 3);
        if (pretree.Count(l => l > 0) == 1)
            pretree = new byte[19];
        var codes = CanonicalCodes(pretree);
        writer.Write(count, 9);
        foreach (var (symbol, extra, extraBits) in items)
        {
            writer.Write(codes[symbol], pretree[symbol]);
            writer.Write(extra, extraBits);
        }
    }

    /// <summary>A pretree or position table: one symbol as count 0 + the symbol, else the lengths (3 bits, 7+ unary).</summary>
    private static void WriteShortLengths(MsbWriter writer, byte[] lengths, int[] frequency, int countBits, int special)
    {
        if (lengths.Count(l => l > 0) <= 1)
        {
            writer.Write(0, countBits);
            writer.Write(Math.Max(0, Array.FindIndex(frequency, f => f > 0)), countBits);
            return;
        }
        var count = Array.FindLastIndex(lengths, l => l > 0) + 1;
        writer.Write(count, countBits);
        for (var i = 0; i < count;)
        {
            int length = lengths[i++];
            if (length < 7)
            {
                writer.Write(length, 3);
            }
            else
            {
                writer.Write(7, 3);
                for (var k = 7; k < length; k++)
                    writer.Write(1, 1);
                writer.Write(0, 1);
            }
            if (i == special)
            {
                var zeros = 0;
                while (zeros < 3 && i + zeros < count && lengths[i + zeros] == 0)
                    zeros++;
                writer.Write(zeros, 2);
                i += zeros;
            }
        }
    }

    private static int BitLength(int value)
    {
        var bits = 0;
        for (; value > 0; value >>= 1)
            bits++;
        return bits;
    }

    /// <summary>Huffman code lengths of at most <paramref name="limit"/> bits (frequencies halved until they fit).</summary>
    private static byte[] HuffmanLengths(int[] frequency, int limit)
    {
        var counts = (int[])frequency.Clone();
        while (true)
        {
            var lengths = new byte[frequency.Length];
            var symbols = Enumerable.Range(0, counts.Length).Where(s => counts[s] > 0).ToList();
            if (symbols.Count <= 1)
            {
                foreach (var s in symbols)
                    lengths[s] = 1;
                return lengths;
            }
            var parent = new int[2 * symbols.Count];
            var weights = new long[2 * symbols.Count];
            var queue = new PriorityQueue<int, (long, int)>();
            for (var i = 0; i < symbols.Count; i++)
            {
                weights[i] = counts[symbols[i]];
                queue.Enqueue(i, (weights[i], i));
            }
            var next = symbols.Count;
            while (queue.Count > 1)
            {
                var a = queue.Dequeue();
                var b = queue.Dequeue();
                weights[next] = weights[a] + weights[b];
                parent[a] = parent[b] = next;
                queue.Enqueue(next, (weights[next], next));
                next++;
            }
            var root = next - 1;
            var max = 0;
            for (var i = 0; i < symbols.Count; i++)
            {
                var depth = 0;
                for (var node = i; node != root; node = parent[node])
                    depth++;
                lengths[symbols[i]] = (byte)depth;
                max = Math.Max(max, depth);
            }
            if (max <= limit)
                return lengths;
            for (var s = 0; s < counts.Length; s++)
                if (counts[s] > 0)
                    counts[s] = Math.Max(1, counts[s] / 2);
        }
    }

    /// <summary>Canonical codes: by length, then by symbol, counting up.</summary>
    private static int[] CanonicalCodes(byte[] lengths)
    {
        var codes = new int[lengths.Length];
        var code = 0;
        for (var length = 1; length <= 16; length++)
        {
            for (var symbol = 0; symbol < lengths.Length; symbol++)
                if (lengths[symbol] == length)
                    codes[symbol] = code++;
            code <<= 1;
        }
        return codes;
    }

    /// <summary>ARJ method 4: unary-prefixed lengths and positions, literals as 8 bits (see ArjFastestDecoder).</summary>
    public static byte[] ArjFastest(byte[] data)
    {
        var writer = new MsbWriter();
        void Code(int value, int minBits, int maxRun)
        {
            var run = 0;
            while (run < maxRun && value >= (1 << (minBits + run + 1)) - (1 << minBits))
                run++;
            for (var i = 0; i < run; i++)
                writer.Write(1, 1);
            if (run < maxRun)
                writer.Write(0, 1);
            writer.Write(value - ((1 << (minBits + run)) - (1 << minBits)), minBits + run);
        }
        foreach (var (length, value) in Lz77(data, 15872, 256))
        {
            if (length == 0)
            {
                Code(0, 0, 7);
                writer.Write(value, 8);
                continue;
            }
            Code(length - 2, 0, 7);
            Code(value - 1, 9, 4);
        }
        return writer.ToArray();
    }

    /// <summary>Bits most significant first (LHA, ARJ).</summary>
    private sealed class MsbWriter
    {
        private readonly MemoryStream _output = new();
        private ulong _bits;
        private int _count;

        public void Write(int value, int count)
        {
            if (count == 0)
                return;
            _bits = (_bits << count) | ((uint)value & ((1UL << count) - 1));
            _count += count;
            while (_count >= 8)
            {
                _output.WriteByte((byte)(_bits >> (_count - 8)));
                _count -= 8;
            }
        }

        public byte[] ToArray()
        {
            if (_count > 0)
                Write(0, 8 - _count);
            return _output.ToArray();
        }
    }

    private static uint DosTime(DateTime t) =>
        (uint)((t.Year - 1980) << 25 | t.Month << 21 | t.Day << 16 | t.Hour << 11 | t.Minute << 5 | t.Second / 2);
}
