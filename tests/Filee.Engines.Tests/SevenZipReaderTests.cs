// Filee's own 7z reader (Archives/SevenZipReader, SevenZipCoders): archives from Filee's 7z writer and from the bundled
// 7-Zip (levels, solid on and off, LZMA, Copy, BCJ, BCJ2, Delta, Deflate, BZip2, plain headers; skipped when 7-Zip is
// missing) unpack byte for byte without 7-Zip; empty files and folders, an archive of only empty files, Unicode names
// and times; PPMd is handed on; encrypted archives are refused; damaged input and hostile headers (huge counts, sizes,
// dictionaries) are refused quickly; cancellation stops it. The mutation fuzz target is in ArchiveFuzzTests.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;
using static Filee.Engines.Tests.ArchiveFormatBuilders;

namespace Filee.Engines.Tests;

public class SevenZipReaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Older = new(2019, 12, 31, 23, 59, 58, 123, DateTimeKind.Utc);

    /// <summary>
    /// Nested folders with Korean, Chinese and emoji names, an empty file, an empty folder, text, a CSV, noise, x86-like
    /// code, and a known time on a file and a folder.
    /// </summary>
    private static string MakeSourceTree(string parent, int proseBytes = 150_000)
    {
        var root = MakeTree(parent, "자료 🚀");
        var deep = Directory.CreateDirectory(Path.Combine(root, "a", "b 深い", "c")).FullName;
        File.WriteAllBytes(Path.Combine(deep, "긴 글.txt"), LzmaTests.Prose(proseBytes));
        File.WriteAllBytes(Path.Combine(root, "a", "빈 파일.txt"), []);
        File.WriteAllBytes(Path.Combine(root, "a", "数据.csv"), Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 4000).Select(i => $"{i},{i * i},값{i}"))));
        File.WriteAllBytes(Path.Combine(root, "a", "code.bin"), X86Like(120_000, 5));
        File.WriteAllBytes(Path.Combine(root, "a", "b 深い", "again.txt"), LzmaTests.Prose(proseBytes));
        Directory.CreateDirectory(Path.Combine(root, "a", "비어 있음"));
        File.SetLastWriteTimeUtc(Path.Combine(deep, "긴 글.txt"), Older);
        Directory.SetLastWriteTimeUtc(deep, Older);
        return root;
    }

    /// <summary>
    /// Bytes that look like x86 code to the branch converters: CALL / JMP / Jcc opcodes with near targets (top byte 0x00
    /// or 0xFF), some with far ones, between other instruction-like bytes.
    /// </summary>
    internal static byte[] X86Like(int length, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[length];
        var i = 0;
        while (i < length - 6)
        {
            switch (random.Next(8))
            {
                case 0:
                case 1:
                    bytes[i++] = 0xE8;
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i), random.Next(-200_000, 200_000) - i);
                    i += 4;
                    break;
                case 2:
                    bytes[i++] = 0xE9;
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i), random.Next(-5000, 5000));
                    i += 4;
                    break;
                case 3:
                    bytes[i++] = 0x0F;
                    bytes[i++] = (byte)(0x80 + random.Next(16));
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i), random.Next(-3000, 3000));
                    i += 4;
                    break;
                case 4:
                    bytes[i++] = 0xE8;
                    bytes[i++] = (byte)random.Next(256); // far: not converted
                    bytes[i++] = (byte)random.Next(256);
                    break;
                default:
                    bytes[i++] = (byte)"\x48\x89\x5C\x24\x8B\x45\x33\xC0\x90\xC3"[random.Next(10)];
                    break;
            }
        }
        return bytes;
    }

    /// <summary>Unpacks with the engine's in-process readers (no 7-Zip) and returns the unpacked folder.</summary>
    private async Task<string> UnpackAsync(string archive, string dir, string format = "7z")
    {
        var target = Directory.CreateDirectory(Path.Combine(dir, "u-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        return await new ArchiveExtractor(null).ExtractAsync(archive, format, target, Ct);
    }

    /// <summary>Unpacks with Filee's reader alone (no SharpCompress fallback, no 7-Zip).</summary>
    private static string ReadOwn(string archive, string dir)
    {
        var target = Directory.CreateDirectory(Path.Combine(dir, "own-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        using var file = File.OpenRead(archive);
        SevenZipReader.Extract(file, Path.GetFileName(archive), target, Ct);
        return target;
    }

    private static string Methods(string archive)
    {
        using var file = File.OpenRead(archive);
        var listing = SevenZipReader.Read(file, "x.7z", Ct);
        return string.Join("; ", listing.Folders.Select(f => string.Join(" ", f.Coders.Select(c => SevenZipCoders.NameOf(c.Method)))).Distinct());
    }

    // ───────── Filee's own writer ─────────

    [Theory]
    [InlineData(ArchiveLevel.Store)]
    [InlineData(ArchiveLevel.Fast)]
    [InlineData(ArchiveLevel.Normal)]
    [InlineData(ArchiveLevel.Maximum)]
    public async Task Archives_from_our_writer_unpack_byte_for_byte(ArchiveLevel level)
    {
        var dir = fx.NewFolder();
        var tree = MakeSourceTree(dir);
        var output = Path.Combine(dir, "out.7z");
        await new ArchivePacker(null).PackAsync([new PackItem(tree, "자료 🚀")], output, "7z", level, dir, null, Ct);
        Assert.False(SevenZipReader.NeedsOtherDecoder(output));

        foreach (var unpacked in new[] { ReadOwn(output, dir), await UnpackAsync(output, dir) })
        {
            var root = Path.Combine(unpacked, "자료 🚀");
            Assert.Equal(Snapshot(tree), Snapshot(root));
            Assert.Equal(KnownTime, File.GetLastWriteTime(Path.Combine(root, "readme.txt")));
            Assert.Equal(Older, File.GetLastWriteTimeUtc(Path.Combine(root, "a", "b 深い", "c", "긴 글.txt")));
            Assert.Equal(Older, Directory.GetLastWriteTimeUtc(Path.Combine(root, "a", "b 深い", "c")));
            Assert.Equal(0, new FileInfo(Path.Combine(root, "a", "빈 파일.txt")).Length);
            Assert.True(Directory.Exists(Path.Combine(root, "a", "비어 있음")));
        }
    }

    [Fact]
    public async Task Several_solid_blocks_unpack_in_order()
    {
        var dir = fx.NewFolder();
        var tree = Directory.CreateDirectory(Path.Combine(dir, "many")).FullName;
        for (var i = 0; i < 40; i++)
            File.WriteAllBytes(Path.Combine(tree, $"f{i:00}.{(i % 3 == 0 ? "bin" : "txt")}"), i % 3 == 0 ? Noise(20_000 + i, i) : LzmaTests.Prose(60_000 + i * 100));
        var output = Path.Combine(dir, "blocks.7z");
        await using (var file = File.Create(output))
        {
            var writer = new SevenZipWriter(file, ArchiveLevel.Fast, cancellationToken: Ct, blockLimit: 300_000);
            foreach (var path in Directory.GetFiles(tree).Order(StringComparer.Ordinal))
            {
                await using var data = File.OpenRead(path);
                writer.AddFile("many/" + Path.GetFileName(path), data, data.Length, File.GetLastWriteTimeUtc(path), 0x20);
            }
            writer.Finish();
            Assert.True(writer.BlockCount >= 5);
        }
        await using (var file = File.OpenRead(output))
            Assert.True(SevenZipReader.Read(file, "blocks.7z", Ct).Folders.Length >= 5);
        Assert.Equal(Snapshot(tree), Snapshot(Path.Combine(ReadOwn(output, dir), "many")));
    }

    [Fact]
    public async Task An_archive_of_only_empty_files_and_folders_is_not_taken_for_an_encrypted_one()
    {
        var dir = fx.NewFolder();
        var tree = Directory.CreateDirectory(Path.Combine(dir, "top", "inner")).FullName;
        File.WriteAllBytes(Path.Combine(tree, "zero.txt"), []);
        File.WriteAllBytes(Path.Combine(dir, "top", "also-zero.bin"), []);
        Directory.CreateDirectory(Path.Combine(dir, "top", "empty"));

        var ours = Path.Combine(dir, "ours.7z");
        await new ArchivePacker(null).PackAsync([new PackItem(Path.Combine(dir, "top"), "top")], ours, "7z", ArchiveLevel.Normal, dir, null, Ct);
        List<string> archives = [ours, Write(dir, "built.7z", SevenZip([new("top/", null), new("top/inner/zero.txt", []), new("top/also-zero.bin", [])]))];
        if (SevenZipExe is not null)
        {
            var theirs = Path.Combine(dir, "theirs.7z");
            await Run7zAsync("a", "-t7z", theirs, Path.Combine(dir, "top"));
            archives.Add(theirs);
        }
        foreach (var archive in archives)
        {
            var root = Path.Combine(await UnpackAsync(archive, dir), "top");
            Assert.Equal(0, new FileInfo(Path.Combine(root, "inner", "zero.txt")).Length);
            Assert.Equal(0, new FileInfo(Path.Combine(root, "also-zero.bin")).Length);
            if (archive != archives[1])
                Assert.True(Directory.Exists(Path.Combine(root, "empty")), archive);
        }
        // And with 7-Zip there: still Filee's reader (no hand-off), still no "password" refusal.
        if (SevenZipExe is not null)
        {
            Assert.False(SevenZipReader.NeedsOtherDecoder(archives[^1]));
            var target = Directory.CreateDirectory(Path.Combine(dir, "with7z")).FullName;
            var unpacked = await new ArchiveExtractor(Archives.SevenZip.Locate()).ExtractAsync(archives[^1], "7z", target, Ct);
            Assert.True(File.Exists(Path.Combine(unpacked, "top", "inner", "zero.txt")));
        }
    }

    [Fact]
    public async Task A_comic_7z_with_an_empty_file_is_not_taken_for_an_encrypted_one()
    {
        // CB7 still goes through SharpCompress (Ebooks/ComicBook), which marks empty 7z files as encrypted.
        var dir = fx.NewFolder();
        var comic = Path.Combine(dir, "comic.cb7");
        await using (var file = File.Create(comic))
        {
            var writer = new SevenZipWriter(file, ArchiveLevel.Normal, cancellationToken: Ct);
            writer.AddFile("page1.png", new MemoryStream(Noise(3000, 1)), 3000, null, 0x20);
            writer.AddFile("page2.png", new MemoryStream(Noise(3000, 2)), 3000, null, 0x20);
            writer.AddFile("notes.txt", new MemoryStream(), 0, null, 0x20);
            writer.Finish();
        }
        var pages = Ebooks.ComicBook.ExtractPages(comic, "cb7", Directory.CreateDirectory(Path.Combine(dir, "pages")).FullName, Ct);
        Assert.Equal(2, pages.Count);
    }

    [Fact]
    public async Task An_empty_archive_unpacks_to_nothing()
    {
        var dir = fx.NewFolder();
        var output = Path.Combine(dir, "nothing.7z");
        await using (var file = File.Create(output))
            new SevenZipWriter(file, ArchiveLevel.Normal, cancellationToken: Ct).Finish();
        Assert.Empty(Directory.EnumerateFileSystemEntries(ReadOwn(output, dir)));
    }

    // ───────── 7-Zip's archives ─────────

    [Theory]
    [InlineData("-mx1")]
    [InlineData("-mx5")]
    [InlineData("-mx9")]
    [InlineData("-mx5 -ms=off")]
    [InlineData("-mx9 -ms=off")]
    [InlineData("-mx0")]
    [InlineData("-m0=LZMA -mx5")]
    [InlineData("-mf=BCJ")]
    [InlineData("-mf=BCJ2")]
    [InlineData("-mf=Delta:4")]
    [InlineData("-m0=Deflate")]
    [InlineData("-m0=BZip2")]
    [InlineData("-mhc=off")]
    [InlineData("-mx1 -mmt=4 -md=64k")]
    public async Task Archives_from_7zip_unpack_byte_for_byte_without_it(string switches)
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var dir = fx.NewFolder();
        var tree = MakeSourceTree(dir, switches.Contains("-mmt=4") ? 3_000_000 : 150_000);
        // A real program (7-Zip's own: 7z.dll on Windows, the 7zz binary elsewhere) for the exe filters 7-Zip picks by
        // extension.
        var program = Path.Combine(Path.GetDirectoryName(SevenZipExe)!, "7z.dll");
        File.Copy(File.Exists(program) ? program : SevenZipExe!, Path.Combine(tree, "a", "tool.dll"));
        var output = Path.Combine(dir, "theirs.7z");
        await Run7zAsync(["a", "-t7z", .. switches.Split(' '), output, tree]);
        TestContext.Current.TestOutputHelper?.WriteLine($"{switches}: {Methods(output)}");
        Assert.False(SevenZipReader.NeedsOtherDecoder(output));

        var root = Path.Combine(ReadOwn(output, dir), "자료 🚀");
        Assert.Equal(Snapshot(tree), Snapshot(root));
        Assert.Equal(KnownTime, File.GetLastWriteTime(Path.Combine(root, "readme.txt")));
        Assert.Equal(Older, File.GetLastWriteTimeUtc(Path.Combine(root, "a", "b 深い", "c", "긴 글.txt")));
        Assert.Equal(Snapshot(tree), Snapshot(Path.Combine(await UnpackAsync(output, dir), "자료 🚀")));
    }

    [Fact]
    public async Task The_x86_filters_change_the_data_and_are_undone()
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var dir = fx.NewFolder();
        var source = Directory.CreateDirectory(Path.Combine(dir, "code")).FullName;
        // Odd sizes and offsets, so opcodes fall on the converters' buffer edges.
        for (var i = 0; i < 6; i++)
            File.WriteAllBytes(Path.Combine(source, $"part{i}.bin"), X86Like(70_000 + i * 12_345, i));
        foreach (var filter in new[] { "BCJ", "BCJ2" })
        {
            var output = Path.Combine(dir, filter + ".7z");
            await Run7zAsync("a", "-t7z", "-mf=" + filter, "-mx1", output, source);
            Assert.Contains(filter, Methods(output));
            Assert.Equal(Snapshot(source), Snapshot(Path.Combine(ReadOwn(output, dir), "code")));
        }
    }

    [Fact]
    public async Task Ppmd_goes_to_7zip_when_it_is_there_and_to_SharpCompress_when_not()
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var dir = fx.NewFolder();
        var tree = MakeSourceTree(dir);
        var output = Path.Combine(dir, "ppmd.7z");
        await Run7zAsync("a", "-t7z", "-m0=PPMd", output, tree);
        Assert.True(SevenZipReader.NeedsOtherDecoder(output));
        await using (var file = File.OpenRead(output))
            Assert.Equal("the 7z method PPMd", Assert.Throws<SevenZipUnsupportedException>(() => SevenZipReader.Extract(file, "ppmd.7z", dir, Ct)).Message);

        Assert.Equal(Snapshot(tree), Snapshot(Path.Combine(await UnpackAsync(output, dir), "자료 🚀")));
        var target = Directory.CreateDirectory(Path.Combine(dir, "with7z")).FullName;
        Assert.Equal(Snapshot(tree), Snapshot(Path.Combine(await new ArchiveExtractor(Archives.SevenZip.Locate()).ExtractAsync(output, "7z", target, Ct), "자료 🚀")));
    }

    [Fact]
    public async Task Password_protected_7z_is_refused_with_or_without_encrypted_names()
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var dir = fx.NewFolder();
        var secret = Path.Combine(dir, "secret.txt");
        await File.WriteAllBytesAsync(secret, Words(5_000), Ct);
        foreach (var switches in new[] { "-mhe=off", "-mhe=on" })
        {
            var output = Path.Combine(dir, $"locked{switches}.7z");
            await Run7zAsync("a", "-t7z", "-pfilee", switches, output, secret);
            Assert.False(SevenZipReader.NeedsOtherDecoder(output));
            await using (var file = File.OpenRead(output))
                Assert.Contains("password-protected", Assert.Throws<InvalidDataException>(() => SevenZipReader.Extract(file, "locked.7z", dir, Ct)).Message);
            foreach (var extractor in new[] { new ArchiveExtractor(null), new ArchiveExtractor(Archives.SevenZip.Locate()) })
            {
                var target = Directory.CreateDirectory(Path.Combine(dir, "t-" + Guid.NewGuid().ToString("N")[..6])).FullName;
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(output, "7z", target, Ct));
                Assert.Contains("password-protected", error.Message);
            }
        }
    }

    [Fact]
    public async Task The_builders_encrypted_7z_is_refused()
    {
        var dir = fx.NewFolder();
        var locked = Write(dir, "locked.7z", SevenZip([new("secret.txt", Words(1000)), new("empty.txt", [])], encrypted: true));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => UnpackAsync(locked, dir));
        Assert.Contains("password-protected", error.Message);
    }

    // ───────── Damage and hostile headers ─────────

    [Fact]
    public async Task Damaged_archives_are_refused()
    {
        var dir = fx.NewFolder();
        var tree = MakeSourceTree(dir);
        var output = Path.Combine(dir, "good.7z");
        await new ArchivePacker(null).PackAsync([new PackItem(tree, "t")], output, "7z", ArchiveLevel.Normal, dir, null, Ct);
        var good = await File.ReadAllBytesAsync(output, Ct);

        var packed = (byte[])good.Clone();
        packed[200] ^= 0x10; // in the LZMA2 data
        var cut = good[..(good.Length - 10)];
        var header = (byte[])good.Clone();
        header[^5] ^= 0x01; // in the (packed) header: its CRC no longer matches
        var start = (byte[])good.Clone();
        start[14] ^= 0x01; // the start header
        foreach (var (label, bytes) in new[] { ("packed", packed), ("cut", cut), ("header", header), ("start", start) })
        {
            var path = Write(dir, $"bad-{label}.7z", bytes);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => UnpackAsync(path, dir));
            Assert.True(error.Message.Contains("damaged", StringComparison.Ordinal) || error.Message.Contains("not a valid archive", StringComparison.Ordinal), $"{label}: {error.Message}");
        }

        // A stored file with a wrong CRC.
        var stored = SevenZip([new("data.bin", Noise(5000, 2))], lzma: false);
        stored[40] ^= 0xFF;
        var crc = await Assert.ThrowsAsync<InvalidDataException>(() => UnpackAsync(Write(dir, "crc.7z", stored), dir));
        Assert.Contains("checksum error in data.bin", crc.Message);
    }

    /// <summary>A 7z with a plain header <paramref name="header"/> after <paramref name="packed"/>, every CRC right.</summary>
    private static byte[] Raw7z(byte[] header, byte[]? packed = null, ulong? headerSize = null)
    {
        packed ??= [];
        var start = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(start, (ulong)packed.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(8), headerSize ?? (ulong)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(16), Crc32.Compute(header));
        using var output = new MemoryStream();
        output.Write([(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4]);
        output.Write(BitConverter.GetBytes(Crc32.Compute(start)));
        output.Write(start);
        output.Write(packed);
        output.Write(header);
        return output.ToArray();
    }

    /// <summary>A 7z number (the encoding the header uses).</summary>
    private static byte[] Num(ulong value)
    {
        var extra = 0;
        while (extra < 8 && value >= 1UL << (7 * (extra + 1)))
            extra++;
        if (extra == 8)
            return [0xFF, .. BitConverter.GetBytes(value)];
        var bytes = new byte[extra + 1];
        bytes[0] = (byte)(((0xFF00 >> extra) & 0xFF) | (int)(value >> (8 * extra)));
        for (var i = 0; i < extra; i++)
            bytes[i + 1] = (byte)(value >> (8 * i));
        return bytes;
    }

    /// <summary>Main streams info: one packed stream of <paramref name="packedSize"/> bytes, one folder of one coder.</summary>
    private static byte[] OneFolder(byte[] coder, ulong packedSize, ulong unpackSize, byte[]? subStreams = null) =>
    [
        0x04,
        0x06, 0x00, 0x01, 0x09, .. Num(packedSize), 0x00,
        0x07, 0x0B, 0x01, 0x00, 0x01, .. coder, 0x0C, .. Num(unpackSize), 0x00,
        .. subStreams ?? [],
        0x00,
    ];

    private static byte[] Names(params string[] names) =>
        [0x11, .. Num((ulong)names.Sum(n => (n.Length + 1) * 2) + 1), 0x00, .. names.SelectMany(n => Encoding.Unicode.GetBytes(n + "\0"))];

    [Fact]
    public async Task Hostile_headers_are_refused_quickly()
    {
        var dir = fx.NewFolder();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        byte[] copy = [0x01, 0x00];
        byte[] lzma = [0x23, 0x03, 0x01, 0x01, 0x05, 0x5D, 0x00, 0x00, 0x10, 0x00];

        async Task<string> Refused(string label, byte[] bytes)
        {
            var path = Write(dir, label + ".7z", bytes);
            await using (var file = File.OpenRead(path))
            {
                var target = Directory.CreateDirectory(Path.Combine(dir, "t-" + label)).FullName;
                var own = Assert.Throws<InvalidDataException>(() => SevenZipReader.Extract(file, label + ".7z", target, Ct));
                Assert.Empty(Directory.EnumerateFileSystemEntries(target));
                return own.Message;
            }
        }

        // Ten million files in a header of a few bytes.
        Assert.Contains("more than 1,000,000 entries", await Refused("files", Raw7z([0x01, 0x05, .. Num(10_000_000), 0x00, 0x00])));
        // Counts far past what the header's bytes can hold.
        Assert.Contains("count in the header is out of range", await Refused("folders", Raw7z([0x01, 0x04, 0x07, 0x0B, .. Num(1UL << 40), 0x00])));
        Assert.Contains("count in the header is out of range", await Refused("packs", Raw7z([0x01, 0x04, 0x06, 0x00, .. Num(1UL << 31), 0x00])));
        Assert.Contains("count in the header is out of range", await Refused("coders", Raw7z([0x01, 0x04, 0x07, 0x0B, 0x01, 0x00, .. Num(1000)])));
        Assert.Contains("count in the header is out of range", await Refused("substreams", Raw7z(
            [0x01, .. OneFolder(copy, 4, 4, [0x08, 0x0D, .. Num(1UL << 32), 0x00]), 0x00], [1, 2, 3, 4])));
        // Files bigger than their folder.
        Assert.Contains("larger than their folder", await Refused("sizes", Raw7z(
            [0x01, .. OneFolder(copy, 4, 4, [0x08, 0x0D, 0x02, 0x09, .. Num(1000), 0x00]), 0x05, 0x02, .. Names("a", "b"), 0x00, 0x00], [1, 2, 3, 4])));
        // A folder that says it unpacks to a petabyte.
        Assert.Contains("decompression bomb", await Refused("bomb", Raw7z([0x01, .. OneFolder(lzma, 4, 1UL << 50), 0x05, 0x01, .. Names("a"), 0x00, 0x00], [0, 0, 0, 0])));
        // A header said to be a terabyte; a packed header said to unpack to 600 MiB.
        Assert.Contains("ends before its header", await Refused("header", Raw7z([0x01, 0x00], headerSize: 1UL << 40)));
        Assert.Contains("header is larger than", await Refused("encoded", Raw7z([0x17, .. OneFolder(lzma, 4, 600UL << 20)[1..]], [0, 0, 0, 0])));
        // A 4 GiB dictionary.
        byte[] hugeDictionary = [0x23, 0x03, 0x01, 0x01, 0x05, 0x5D, 0xFF, 0xFF, 0xFF, 0xFF];
        Assert.Contains("dictionary", await Refused("dictionary", Raw7z([0x01, .. OneFolder(hugeDictionary, 4, 100), 0x05, 0x01, .. Names("a"), 0x00, 0x00], [0, 0, 0, 0])));
        // Packed data past the end of the file.
        Assert.Contains("ends before its packed data", await Refused("pack", Raw7z([0x01, .. OneFolder(copy, 1UL << 40, 4), 0x05, 0x01, .. Names("a"), 0x00, 0x00])));
        // More files with data than the folders hold, and the other way round.
        Assert.Contains("more files with data", await Refused("more", Raw7z([0x01, .. OneFolder(copy, 4, 4), 0x05, 0x02, .. Names("a", "b"), 0x00, 0x00], [1, 2, 3, 4])));
        Assert.Contains("more data streams than files", await Refused("fewer", Raw7z([0x01, .. OneFolder(copy, 4, 4), 0x00], [1, 2, 3, 4])));
        // Names that stop short, an entry leaving the folder.
        Assert.Contains("names are cut short", await Refused("names", Raw7z([0x01, 0x05, 0x02, 0x0E, 0x01, 0xC0, 0x11, 0x05, 0x00, (byte)'a', 0, 0, 0, 0x00, 0x00])));
        Assert.Contains("outside the target folder", await Refused("slip", Raw7z([0x01, .. OneFolder(copy, 4, 4), 0x05, 0x01, .. Names("../evil.txt"), 0x00, 0x00], [1, 2, 3, 4])));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");

        // The empty-file bits before the empty-stream ones (sized for none): read as folders, not out of range.
        var early = Write(dir, "early.7z", Raw7z([0x01, 0x05, 0x02, 0x0F, 0x01, 0x80, 0x0E, 0x01, 0xC0, .. Names("a", "b"), 0x00, 0x00]));
        var unpacked = ReadOwn(early, dir);
        Assert.True(Directory.Exists(Path.Combine(unpacked, "a")) && Directory.Exists(Path.Combine(unpacked, "b")));
    }

    [Fact]
    public async Task Bombs_are_refused_from_the_folder_sizes()
    {
        var dir = fx.NewFolder();
        var tree = Directory.CreateDirectory(Path.Combine(dir, "zeros")).FullName;
        File.WriteAllBytes(Path.Combine(tree, "z.bin"), new byte[4 << 20]);
        var output = Path.Combine(dir, "zeros.7z");
        await new ArchivePacker(null).PackAsync([new PackItem(tree, "zeros")], output, "7z", ArchiveLevel.Normal, dir, null, Ct);
        ArchiveSafety.TestLimits.Value = (1 << 20, 10, ArchiveSafety.MaxEntries);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => UnpackAsync(output, dir));
            Assert.Contains("decompression bomb", error.Message);
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    [Fact]
    public async Task Cancelling_stops_in_the_middle_of_a_solid_block()
    {
        var dir = fx.NewFolder();
        var tree = Directory.CreateDirectory(Path.Combine(dir, "big")).FullName;
        for (var i = 0; i < 8; i++)
            File.WriteAllBytes(Path.Combine(tree, $"f{i}.txt"), LzmaTests.Prose(1_000_000 + i));
        var output = Path.Combine(dir, "big.7z");
        await new ArchivePacker(null).PackAsync([new PackItem(tree, "big")], output, "7z", ArchiveLevel.Fast, dir, null, Ct);

        using var cancel = new CancellationTokenSource();
        await using var file = new CancelAfterStream(File.OpenRead(output), 64 << 10, cancel);
        var target = Directory.CreateDirectory(Path.Combine(dir, "t")).FullName;
        Assert.ThrowsAny<OperationCanceledException>(() => SevenZipReader.Extract(file, "big.7z", target, cancel.Token));
        Assert.True(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length < 8);
    }

    /// <summary>Cancels <paramref name="cancel"/> once <paramref name="after"/> bytes past the header have been read.</summary>
    private sealed class CancelAfterStream(Stream inner, long after, CancellationTokenSource cancel) : Stream
    {
        private long _read;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            if (Position < inner.Length - 4096 && (_read += n) > after)
                cancel.Cancel();
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static string Write(string dir, string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
