// Writes archive formats Filee only reads, in code and without 7-Zip, so the in-process readers are tested with
// 7-Zip hidden: 7z (copy or LZMA coder, optionally an AES coder that marks it encrypted), xz (LZMA2 chunks: LZMA
// from SharpCompress's encoder or stored), RAR 4 and RAR 5 with stored entries, ISO 9660 with Joliet or Rock Ridge,
// cpio (newc, crc, odc, old binary), ar / DEB, RPM and Unix compress (.Z, an LZW encoder that pads its code groups
// as compress does). Each follows the format's own description; see the readers in Filee.Engines/Archives.

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

    private static uint DosTime(DateTime t) =>
        (uint)((t.Year - 1980) << 25 | t.Month << 21 | t.Day << 16 | t.Hour << 11 | t.Minute << 5 | t.Second / 2);
}
