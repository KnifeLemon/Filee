// Reads cpio archives (also the payload of RPM packages) in their four layouts:
//
//   "070701" newc / "070702" crc   110-byte header of 8-digit hex fields (ino, mode, uid, gid, nlink, mtime,
//                                  filesize, devmajor, devminor, rdevmajor, rdevminor, namesize, check); name and data
//                                  each padded to a multiple of 4; "crc" adds the byte sum of the data as check
//   "070707" odc                   76-byte header of octal fields (dev, ino, mode, uid, gid, nlink, rdev, mtime 11,
//                                  namesize, filesize 11), no padding
//   0x71C7 old binary              26-byte header of 16-bit words in either byte order (mtime and filesize as two
//                                  words, high first), name and data padded to even
//
// The archive ends with an entry named "TRAILER!!!". The file type is in the mode (0040000 folder, 0100000 file,
// 0120000 symbolic link); links, devices and FIFOs are left out. A newc hard link group stores its data once, with
// the last name; the earlier names become copies of it. Written from the cpio(5) format description.

using System.Text;

namespace Filee.Engines.Archives;

/// <summary>Unpacks cpio archives.</summary>
internal static class CpioReader
{
    private const int MaxNameLength = 64 * 1024;

    private enum Layout
    {
        Newc,
        Crc,
        Odc,
        BinaryLittle,
        BinaryBig,
    }

    private sealed record Entry(string Name, int Mode, long Size, long DataOffset, long Inode, long Device, int Links, DateTime? Modified, uint Check, bool HasCheck)
    {
        public int Type => Mode & 0xF000;
    }

    public static void Extract(string archive, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var entries = List(stream, name);
        ArchiveSafety.CheckListing([.. entries.Select(e => new ArchiveItem(e.Name, e.Type == 0x4000, e.Type == 0x8000 ? e.Size : 0, IsLink: e.Type is not (0x4000 or 0x8000)))],
            name, stream.Length, folder);

        var writer = new EntryWriter(folder, new UnpackBudget(name, stream.Length, folder), cancellationToken);
        // Hard links of a newc group without data yet: (device, inode) → names waiting for it.
        var waiting = new Dictionary<(long, long), List<Entry>>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Type == 0x4000)
            {
                writer.AddFolder(entry.Name, entry.Modified);
                continue;
            }
            if (entry.Type != 0x8000)
                continue;
            var key = (entry.Device, entry.Inode);
            if (entry.Links > 1 && entry.Size == 0)
            {
                if (!waiting.TryGetValue(key, out var names))
                    waiting[key] = names = [];
                names.Add(entry);
                continue;
            }
            stream.Position = entry.DataOffset;
            var data = new SumStream(new BoundedStream(stream, entry.Size));
            writer.AddFile(entry.Name, data, entry.Size, null, entry.Modified);
            if (entry.HasCheck && data.Sum != entry.Check)
                throw ArchiveSafety.Damaged(name, $"checksum error in {entry.Name}");
            if (entry.Links > 1 && waiting.Remove(key, out var links))
                foreach (var link in links)
                    writer.AddHardLink(link.Name, entry.Name, link.Modified);
        }
        // Groups whose data never came were empty files.
        foreach (var link in waiting.Values.SelectMany(l => l))
            writer.AddFile(link.Name, Stream.Null, 0, null, link.Modified);
        writer.Finish();
    }

    private static List<Entry> List(FileStream stream, string name)
    {
        var entries = new List<Entry>();
        Span<byte> magic = stackalloc byte[6];
        while (true)
        {
            if (stream.ReadAtLeast(magic, 6, throwOnEndOfStream: false) < 6)
                throw entries.Count == 0 ? ArchiveSafety.NotAnArchive(name) : ArchiveSafety.Damaged(name, "the archive ends without its trailer");
            var layout = LayoutOf(magic) ?? throw (entries.Count == 0 ? ArchiveSafety.NotAnArchive(name) : ArchiveSafety.Damaged(name, "invalid cpio header"));
            var entry = ReadHeader(stream, layout, magic, name);
            if (entry.Name == "TRAILER!!!")
                return entries;
            if (entry.Size > stream.Length - entry.DataOffset)
                throw ArchiveSafety.Damaged(name, "the archive ends in the middle of an entry");
            if (entries.Count >= ArchiveSafety.EntryLimit)
                throw ArchiveSafety.TooManyEntries(name);
            entries.Add(entry);
            var end = entry.DataOffset + entry.Size;
            stream.Position = layout switch
            {
                Layout.Newc or Layout.Crc => Align(end, 4),
                Layout.BinaryLittle or Layout.BinaryBig => Align(end, 2),
                _ => end,
            };
        }
    }

    private static Layout? LayoutOf(ReadOnlySpan<byte> magic)
    {
        if (magic.SequenceEqual("070701"u8))
            return Layout.Newc;
        if (magic.SequenceEqual("070702"u8))
            return Layout.Crc;
        if (magic.SequenceEqual("070707"u8))
            return Layout.Odc;
        if (magic[0] == 0xC7 && magic[1] == 0x71)
            return Layout.BinaryLittle;
        if (magic[0] == 0x71 && magic[1] == 0xC7)
            return Layout.BinaryBig;
        return null;
    }

    private static Entry ReadHeader(FileStream stream, Layout layout, ReadOnlySpan<byte> magic, string name)
    {
        long inode, device, mtime, size;
        int mode, links, nameSize;
        uint check = 0;
        switch (layout)
        {
            case Layout.Newc:
            case Layout.Crc:
                {
                    var fields = new byte[104];
                    Read(stream, fields, name);
                    long Hex(int i) => Parse(fields.AsSpan(i * 8, 8), 16, name);
                    inode = Hex(0);
                    mode = (int)Hex(1);
                    links = (int)Math.Min(Hex(4), int.MaxValue);
                    mtime = Hex(5);
                    size = Hex(6);
                    device = Hex(7) << 32 | Hex(8);
                    nameSize = (int)Math.Min(Hex(11), int.MaxValue);
                    check = (uint)Hex(12);
                    break;
                }
            case Layout.Odc:
                {
                    var fields = new byte[70];
                    Read(stream, fields, name);
                    long Octal(int offset, int length) => Parse(fields.AsSpan(offset, length), 8, name);
                    device = Octal(0, 6);
                    inode = Octal(6, 6);
                    mode = (int)Octal(12, 6);
                    links = (int)Octal(30, 6);
                    mtime = Octal(42, 11);
                    nameSize = (int)Octal(53, 6);
                    size = Octal(59, 11);
                    break;
                }
            default:
                {
                    var fields = new byte[20];
                    Read(stream, fields, name);
                    var big = layout == Layout.BinaryBig;
                    int Word(int i) => big ? fields[i * 2] << 8 | fields[i * 2 + 1] : fields[i * 2 + 1] << 8 | fields[i * 2];
                    // magic[2..6] are the dev and ino words.
                    device = big ? magic[2] << 8 | magic[3] : magic[3] << 8 | magic[2];
                    inode = big ? magic[4] << 8 | magic[5] : magic[5] << 8 | magic[4];
                    mode = Word(0);
                    links = Word(3);
                    mtime = (long)Word(5) << 16 | (uint)Word(6);
                    nameSize = Word(7);
                    size = (long)Word(8) << 16 | (uint)Word(9);
                    break;
                }
        }
        if (nameSize is <= 0 or > MaxNameLength)
            throw ArchiveSafety.Damaged(name, "invalid name length in a cpio header");
        var nameBytes = new byte[nameSize];
        Read(stream, nameBytes, name);
        var entryName = Encoding.UTF8.GetString(nameBytes.AsSpan(0, nameBytes.AsSpan().IndexOf((byte)0) is var nul and >= 0 ? nul : nameBytes.Length));
        var dataOffset = layout switch
        {
            Layout.Newc or Layout.Crc => Align(stream.Position, 4),
            Layout.BinaryLittle or Layout.BinaryBig => Align(stream.Position, 2),
            _ => stream.Position,
        };
        DateTime? modified = mtime is > 0 and < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime : null;
        return new Entry(entryName, mode, size, dataOffset, inode, device, links, modified, check, layout == Layout.Crc);
    }

    private static void Read(FileStream stream, byte[] buffer, string name)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
            throw ArchiveSafety.Damaged(name, "the archive ends in the middle of a header");
    }

    private static long Parse(ReadOnlySpan<byte> digits, int radix, string name)
    {
        long value = 0;
        foreach (var b in digits)
        {
            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => 99,
            };
            if (digit >= radix)
                throw ArchiveSafety.Damaged(name, "invalid number in a cpio header");
            value = value * radix + digit;
        }
        return value;
    }

    private static long Align(long value, int to) => (value + to - 1) / to * to;

    /// <summary>Passes data through and adds up its bytes (the check of the "070702" layout).</summary>
    private sealed class SumStream(Stream inner) : Stream
    {
        public uint Sum { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            var sum = Sum;
            foreach (var b in buffer[..read])
                sum += b;
            Sum = sum;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
