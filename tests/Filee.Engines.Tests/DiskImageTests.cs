// Disk images unpacked in-process (DiskImageReader): FAT12/16/32 volumes with long names, MBR / GPT / Apple partition
// maps with a folder per volume, UDF discs (plain and behind an ISO 9660 bridge) and DMG files with every run type
// DiscUtils.Dmg decodes. Images are built in code (DiskImageBuilders, and DiscUtils.Fat as an independent FAT writer);
// the bundled 7-Zip, where present, reads the same images, and the tools of the PC write real ones where there are
// any: Windows' IMAPI2 (UDF 1.02 / 2.01 / 2.50 with an ISO 9660 bridge) and macOS' hdiutil (HFS+ DMGs). Then the
// hostile and damaged inputs, and what is left to 7-Zip.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;
using static Filee.Engines.Tests.DiskImageBuilders;

namespace Filee.Engines.Tests;

public class DiskImageTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime OtherTime = new(2019, 12, 24, 18, 30, 0, DateTimeKind.Local);

    /// <summary>
    /// Long and Korean names, a lowercase 8.3 name, nested and empty folders, an empty file, files over several
    /// clusters and one exactly a cluster long.
    /// </summary>
    private static List<TestEntry> Entries(bool ascii = false) =>
    [
        new(ascii ? "Documents/" : "문서/", null),
        new(ascii ? "Documents/Quarterly report.txt" : "문서/분기 보고서.txt", Words(90_000)),
        new(ascii ? "Documents/deep folder/data.csv" : "문서/깊은 폴더/data.csv", "a,b\n1,2\n"u8.ToArray(), OtherTime),
        new("readme.txt", "lowercase 8.3 name"u8.ToArray()),
        new("NOISE.BIN", Noise(45_000, 5)),
        new("zero.txt", []),
        new("cluster.bin", Noise(2048, 3)),
        new("empty/", null),
    ];

    /// <summary>Path → content hash of the files, and "dir/" for the empty folders, as <see cref="Snapshot"/> lists them.</summary>
    private static SortedDictionary<string, string> Expected(IEnumerable<TestEntry> entries, string prefix = "")
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var list = entries.ToList();
        foreach (var e in list.Where(e => !e.IsFolder))
            result[prefix + e.Name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(e.Data!));
        foreach (var e in list.Where(e => e.IsFolder))
        {
            var name = e.Name.TrimEnd('/');
            if (!list.Any(o => o != e && o.Name.TrimEnd('/').StartsWith(name + "/", StringComparison.Ordinal)))
                result[prefix + name + "/"] = "";
        }
        return result;
    }

    private static SortedDictionary<string, string> Merge(params SortedDictionary<string, string>[] parts) =>
        new(parts.SelectMany(p => p).ToDictionary(), StringComparer.Ordinal);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Unpacks without 7-Zip and returns the folder.</summary>
    private async Task<string> UnpackAsync(string image, string format)
    {
        var scratch = Directory.CreateDirectory(Path.Combine(fx.NewFolder(), "scratch")).FullName;
        return await new ArchiveExtractor(null).ExtractAsync(image, format, scratch, Ct);
    }

    private async Task AssertUnpacksAsync(string image, string format, IReadOnlyList<TestEntry> entries, string prefix = "")
    {
        var folder = await UnpackAsync(image, format);
        Assert.Equal(Expected(entries, prefix), Snapshot(folder));
        foreach (var entry in entries.Where(e => !e.IsFolder))
        {
            var time = File.GetLastWriteTime(Path.Combine(folder, prefix + entry.Name));
            Assert.True(Math.Abs((time - entry.Time).TotalSeconds) <= 2, $"{entry.Name}: time {time} instead of {entry.Time}");
        }
    }

    private async Task<string> RefusedAsync(string image, string format)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => UnpackAsync(image, format));
        return error.Message;
    }

    // ───────── FAT ─────────

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(32)]
    public async Task Fat_volumes_unpack_with_their_long_names(int bits)
    {
        var entries = Entries();
        await AssertUnpacksAsync(Write("card.img", Fat(entries, bits)), "img", entries);
    }

    [Fact]
    public async Task Fat_written_by_discutils_unpacks()
    {
        List<TestEntry> entries =
        [
            new("DOCS/", null),
            new("DOCS/REPORT.TXT", Words(30_000)),
            new("DOCS/SUB/DATA.CSV", "a,b\n"u8.ToArray(), OtherTime),
            new("NOISE.BIN", Noise(10_000, 2)),
            new("EMPTY/", null),
        ];
        await AssertUnpacksAsync(Write("floppy.img", DiscUtilsFat(entries)), "img", entries);
    }

    // ───────── Partitions ─────────

    [Fact]
    public async Task Each_volume_of_a_partitioned_disk_gets_its_own_folder()
    {
        var first = Entries(ascii: true);
        List<TestEntry> second = [new("other.txt", Words(5_000)), new("sub/inner.bin", Noise(3_000, 1))];
        var a = Fat(first, 16, "FIRST");
        var b = Fat(second, 12, "SECOND");

        var mbr = await UnpackAsync(Write("mbr.img", Mbr((0x06, a), (0x01, b))), "img");
        Assert.Equal(Merge(Expected(first, "1 FIRST/"), Expected(second, "2 SECOND/")), Snapshot(mbr));

        var gpt = await UnpackAsync(Write("gpt.img", Gpt(("one", a), ("two", b))), "img");
        Assert.Equal(Merge(Expected(first, "1 FIRST/"), Expected(second, "2 SECOND/")), Snapshot(gpt));

        var apm = await UnpackAsync(Write("apm.img", Apm(("one", a), ("two", b))), "img");
        Assert.Equal(Merge(Expected(first, "2 FIRST/"), Expected(second, "3 SECOND/")), Snapshot(apm));

        var logical = await UnpackAsync(Write("logical.img", Extended([a, b])), "img");
        Assert.Equal(Merge(Expected(first, "5 FIRST/"), Expected(second, "6 SECOND/")), Snapshot(logical));
    }

    [Fact]
    public async Task A_disk_with_one_volume_unpacks_its_files_at_the_top()
    {
        var entries = Entries();
        await AssertUnpacksAsync(Write("one.img", Gpt(("data", Fat(entries, 32)))), "img", entries);
        // Unknown partitions (here: zeros) are skipped.
        await AssertUnpacksAsync(Write("two.img", Mbr((0x06, Fat(entries, 16)), (0x82, new byte[1 << 20]))), "img", entries);
    }

    // ───────── UDF ─────────

    [Theory]
    [InlineData(0x0102, false)]
    [InlineData(0x0102, true)]
    [InlineData(0x0201, false)]
    [InlineData(0x0201, true)]
    public async Task Udf_discs_unpack_in_process(int revision, bool bridge)
    {
        var entries = Entries();
        var image = Udf(entries, revision, bridge);
        await AssertUnpacksAsync(Write("disc.iso", image), "iso", entries);
        // The same disc named .img.
        await AssertUnpacksAsync(Write("disc.img", image), "img", entries);
    }

    // ───────── DMG ─────────

    [Theory]
    [InlineData(new[] { DmgRun.Raw })]
    [InlineData(new[] { DmgRun.Zlib })]
    [InlineData(new[] { DmgRun.Bzip2 })]
    [InlineData(new[] { DmgRun.Adc })]
    [InlineData(new[] { DmgRun.Zlib, DmgRun.Raw, DmgRun.Bzip2, DmgRun.Adc })]
    public async Task Dmg_files_unpack_in_process(DmgRun[] runs)
    {
        var entries = Entries();
        // A GPT disk (as hdiutil makes by default) and a bare volume (hdiutil -layout NONE).
        await AssertUnpacksAsync(Write("disk.dmg", Dmg(Gpt(("disk image", Fat(entries, 16))), runs)), "dmg", entries);
        await AssertUnpacksAsync(Write("volume.dmg", Dmg(Fat(entries, 12), runs)), "dmg", entries);
    }

    [Fact]
    public async Task A_dmg_without_a_trailer_is_read_as_a_disk()
    {
        var entries = Entries();
        await AssertUnpacksAsync(Write("raw.dmg", Fat(entries, 12)), "dmg", entries);
    }

    // ───────── 7-Zip and other tools ─────────

    /// <summary>The bundled 7-Zip reads what the builders write: they follow the formats, not just Filee's readers.</summary>
    [Fact]
    public async Task The_builders_images_are_read_by_7zip()
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var entries = Entries(ascii: true);
        foreach (var (name, bytes) in new[]
                 {
                     ("fat12.img", Fat(entries, 12)),
                     ("fat16.img", Fat(entries, 16)),
                     ("fat32.img", Fat(entries, 32)),
                     ("udf102.iso", Udf(entries, 0x0102)),
                     ("udf201.iso", Udf(entries, 0x0201, bridge: true)),
                 })
        {
            var image = Write(name, bytes);
            var target = fx.NewFolder();
            await Run7zAsync("x", "-y", "-o" + target, image);
            Assert.True(Expected(entries).SequenceEqual(Snapshot(target)), $"7-Zip unpacks {name} differently: {string.Join(", ", Snapshot(target).Keys)}");
            Assert.Equal(Expected(entries), Snapshot(await UnpackAsync(image, Path.GetExtension(name)[1..])));
        }
    }

    /// <summary>UDF discs written by Windows (IMAPI2, as Windows Explorer burns them), with and without ISO 9660.</summary>
    [Theory]
    [InlineData(0x102, false)]
    [InlineData(0x201, true)]
    [InlineData(0x250, false)]
    [InlineData(0x250, true)]
    public async Task Udf_discs_made_by_windows_read_back_byte_for_byte(int revision, bool iso9660)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage") is not null,
            "IMAPI2 is a Windows component");
        var source = fx.NewFolder();
        var entries = Entries();
        entries.Add(new TestEntry("large.bin", [.. Words(700_000), .. Noise(300_000, 8)]));
        foreach (var entry in entries)
        {
            var path = Path.Combine(source, entry.Name);
            if (entry.IsFolder)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, entry.Data!);
            File.SetLastWriteTime(path, entry.Time);
        }
        var image = Path.Combine(fx.NewFolder(), "windows.iso");
        if (OperatingSystem.IsWindows())
            ImapiImage(source, image, revision, iso9660);
        await AssertUnpacksAsync(image, "iso", entries);
        if (SevenZipExe is not null)
        {
            var target = fx.NewFolder();
            await Run7zAsync("x", "-y", "-o" + target, image);
            Assert.Equal(Expected(entries), Snapshot(target));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ImapiImage(string source, string image, int revision, bool iso9660)
    {
        dynamic fsi = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage")!)!;
        fsi.FileSystemsToCreate = 4 | (iso9660 ? 1 | 2 : 0); // UDF, and ISO 9660 + Joliet
        fsi.UDFRevision = revision;
        fsi.FreeMediaBlocks = 0; // no size limit
        fsi.VolumeName = "FILEE";
        fsi.Root.AddTree(source, false);
        dynamic result = fsi.CreateResultImage();
        var stream = (System.Runtime.InteropServices.ComTypes.IStream)result.ImageStream;
        using var output = File.Create(image);
        var buffer = new byte[1 << 16];
        var read = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, read);
                var n = Marshal.ReadInt32(read);
                if (n == 0)
                    break;
                output.Write(buffer, 0, n);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(read);
            Marshal.ReleaseComObject(stream);
        }
    }

    /// <summary>HFS+ DMGs made by macOS' hdiutil, zlib, bzip2 and uncompressed, with a symbolic link left out.</summary>
    [Theory]
    [InlineData("UDZO")]
    [InlineData("UDBZ")]
    [InlineData("UDRO")]
    public async Task Hfs_plus_dmgs_made_by_hdiutil_read_back_byte_for_byte(string format)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && File.Exists("/usr/bin/hdiutil"), "hdiutil is a macOS tool");
        var source = fx.NewFolder();
        var entries = Entries();
        foreach (var entry in entries)
        {
            var path = Path.Combine(source, entry.Name);
            if (entry.IsFolder)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, entry.Data!);
            File.SetLastWriteTime(path, entry.Time);
        }
        File.CreateSymbolicLink(Path.Combine(source, "link"), "/etc/hosts");
        var image = Path.Combine(fx.NewFolder(), "mac.dmg");
        await RunAsync("/usr/bin/hdiutil", "create", "-quiet", "-srcfolder", source, "-fs", "HFS+", "-format", format, "-volname", "Filee", image);
        // hdiutil adds its own folders (.fseventsd and the like) at the top: compare what the test wrote.
        var folder = await UnpackAsync(image, "dmg");
        var snapshot = Snapshot(folder);
        foreach (var (path, hash) in Expected(entries))
            Assert.True(snapshot.TryGetValue(path, out var got) && got == hash, $"{path} differs");
        Assert.False(File.Exists(Path.Combine(folder, "link")));
    }

    [Fact]
    public async Task Apfs_dmgs_made_by_hdiutil_are_left_to_7zip()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && File.Exists("/usr/bin/hdiutil"), "hdiutil is a macOS tool");
        var source = fx.NewFolder();
        File.WriteAllText(Path.Combine(source, "a.txt"), "apfs");
        var image = Path.Combine(fx.NewFolder(), "apfs.dmg");
        await RunAsync("/usr/bin/hdiutil", "create", "-quiet", "-srcfolder", source, "-fs", "APFS", "-format", "UDZO", image);
        Assert.Contains("APFS", await RefusedAsync(image, "dmg"));
    }

    private static async Task RunAsync(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"{tool} failed: {await error}");
    }

    // ───────── Left to 7-Zip ─────────

    [Fact]
    public async Task File_systems_filee_does_not_read_are_named_for_7zip()
    {
        var volume = new byte[1 << 20];
        foreach (var (kind, patch) in new (string, Action<byte[]>)[]
                 {
                     ("NTFS", v => { v[0] = 0xEB; "NTFS    "u8.CopyTo(v.AsSpan(3)); v[510] = 0x55; v[511] = 0xAA; }),
                     ("exFAT", v => { v[0] = 0xEB; "EXFAT   "u8.CopyTo(v.AsSpan(3)); v[510] = 0x55; v[511] = 0xAA; }),
                     ("APFS", v => "NXSB"u8.CopyTo(v.AsSpan(32))),
                     ("HFSX", v => "HX"u8.CopyTo(v.AsSpan(1024))),
                     ("ext2/3/4", v => { v[1080] = 0x53; v[1081] = 0xEF; }),
                 })
        {
            var copy = (byte[])volume.Clone();
            patch(copy);
            var message = await RefusedAsync(Write(kind.Replace('/', '-') + ".img", Gpt(("data", Fat(Entries(), 12)), ("other", copy))), "img");
            Assert.Contains(kind, message);
            Assert.Contains("does not unpack", message);
        }
        Assert.Contains("does not unpack", await RefusedAsync(Write("blank.img", new byte[1 << 20]), "img"));
        var lzfse = Dmg(Fat(Entries(), 12), [DmgRun.Lzfse]);
        Assert.Contains("LZFSE", await RefusedAsync(Write("lzfse.dmg", lzfse), "dmg"));
    }

    [Fact]
    public async Task With_7zip_what_filee_leaves_out_goes_to_7zip()
    {
        var sevenZip = Filee.Engines.Archives.SevenZip.Locate();
        Assert.SkipUnless(sevenZip is not null, "7-Zip not found");
        // A disk with an NTFS volume: unpacked by 7-Zip as a whole (it shows the volumes as files) instead of in
        // part by Filee.
        var ntfs = new byte[1 << 20];
        ntfs[0] = 0xEB;
        "NTFS    "u8.CopyTo(ntfs.AsSpan(3));
        var path = Write("ntfs.img", Gpt(("data", Fat(Entries(ascii: true), 12)), ("windows", ntfs)));
        Assert.Contains("NTFS", await RefusedAsync(path, "img"));
        var scratch = Directory.CreateDirectory(Path.Combine(fx.NewFolder(), "scratch")).FullName;
        var folder = await new ArchiveExtractor(sevenZip).ExtractAsync(path, "img", scratch, Ct);
        Assert.NotEmpty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Without_7zip_a_bridge_disc_falls_back_to_its_iso9660_part()
    {
        var image = Udf(Entries(), bridge: true);
        image.AsSpan(256 * 2048, 2048).Clear();
        var folder = await UnpackAsync(Write("bridge.iso", image), "iso");
        Assert.Equal(["README.TXT"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    // ───────── Hostile and damaged images ─────────

    [Fact]
    public async Task Encrypted_images_are_refused()
    {
        var dmg = Dmg(Fat(Entries(), 12), [DmgRun.Zlib]);
        "encrcdsa"u8.CopyTo(dmg);
        Assert.Contains("password", await RefusedAsync(Write("locked.dmg", dmg), "dmg"));
        var bitlocker = new byte[1 << 20];
        bitlocker[0] = 0xEB;
        "-FVE-FS-"u8.CopyTo(bitlocker.AsSpan(3));
        Assert.Contains("password", await RefusedAsync(Write("bitlocker.img", Gpt(("bitlocker", bitlocker))), "img"));
        var luks = new byte[1 << 20];
        ((ReadOnlySpan<byte>)[0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE]).CopyTo(luks);
        Assert.Contains("password", await RefusedAsync(Write("luks.img", Mbr((0x83, luks))), "img"));
    }

    [Fact]
    public async Task Loops_in_folders_and_chains_are_damage()
    {
        // FAT: a folder chain that comes back to its first cluster; a folder whose entry names the root's cluster.
        Assert.Contains("damaged", await RefusedAsync(Write("chain.img", Fat(Entries(), 16, loop: true)), "img"));
        var fat32 = Fat([new("a/", null), new("a/b.txt", [1])], 32);
        PointFolderAt(fat32, "A          ", RootCluster(fat32));
        Assert.Contains("damaged", await RefusedAsync(Write("folders.img", fat32), "img"));
        // UDF: a folder that is the root again.
        Assert.Contains("damaged", await RefusedAsync(Write("loop.iso", Udf(Entries(), loop: true)), "iso"));
        // MBR: extended partitions that come back to the first one.
        Assert.Contains("loop", await RefusedAsync(Write("ebr.img", Extended([Fat(Entries(), 12), Fat(Entries(), 12)], loop: true)), "img"));
    }

    private static uint RootCluster(byte[] fat32) => BinaryPrimitives.ReadUInt32LittleEndian(fat32.AsSpan(44));

    /// <summary>Points the folder entry <paramref name="shortName"/> (in the root) at <paramref name="cluster"/>.</summary>
    private static void PointFolderAt(byte[] image, string shortName, uint cluster)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(shortName);
        var at = image.AsSpan().IndexOf(name);
        Assert.True(at > 0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at + 26), (ushort)cluster);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at + 20), (ushort)(cluster >> 16));
    }

    [Fact]
    public async Task Bombs_and_entry_floods_are_refused()
    {
        // A file whose size says 4 GB on a 1.44 MB floppy: the listing is a bomb.
        var floppy = Fat([new("big.bin", Noise(4096, 1))], 12);
        var entry = floppy.AsSpan().IndexOf("BIG     BIN"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(floppy.AsSpan(entry + 28), uint.MaxValue);
        ArchiveSafety.TestLimits.Value = (1 << 20, 100, ArchiveSafety.MaxEntries);
        try
        {
            Assert.Contains("decompression bomb", await RefusedAsync(Write("bomb.img", floppy), "img"));
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }

        ArchiveSafety.TestLimits.Value = (ArchiveSafety.RatioFloor, ArchiveSafety.MaxRatio, 20);
        try
        {
            List<TestEntry> many = [.. Enumerable.Range(0, 40).Select(i => new TestEntry($"f{i}.txt", [(byte)i]))];
            Assert.Contains("entries", await RefusedAsync(Write("many.img", Fat(many, 16)), "img"));
            Assert.Contains("entries", await RefusedAsync(Write("many.iso", Udf(many)), "iso"));
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    [Fact]
    public async Task Damaged_tables_and_block_lists_are_refused()
    {
        var entries = Entries();
        // GPT: a partition beyond the end of the disk.
        var gpt = Gpt(("data", Fat(entries, 12)));
        BinaryPrimitives.WriteUInt64LittleEndian(gpt.AsSpan(1024 + 32), 1UL << 40);
        BinaryPrimitives.WriteUInt64LittleEndian(gpt.AsSpan(1024 + 40), (1UL << 40) + 100);
        Assert.Contains("beyond the end", await RefusedAsync(Write("gpt.img", gpt), "img"));

        var dmg = Dmg(Fat(entries, 12), [DmgRun.Zlib]);
        var koly = dmg.Length - 512;
        // The block list beyond the end of the file.
        var list = (byte[])dmg.Clone();
        BinaryPrimitives.WriteUInt64BigEndian(list.AsSpan(koly + 224), (ulong)dmg.Length);
        Assert.Contains("damaged", await RefusedAsync(Write("list.dmg", list), "dmg"));
        // A run beyond the end of the file, and a buffer larger than any real DMG asks for.
        var mish = Mish(dmg);
        var run = (byte[])mish.Clone();
        BinaryPrimitives.WriteInt64BigEndian(run.AsSpan(204 + 24), dmg.Length * 4L);
        Assert.Contains("damaged", await RefusedAsync(Write("run.dmg", WithMish(dmg, run)), "dmg"));
        var buffer = (byte[])mish.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(32), uint.MaxValue);
        Assert.Contains("buffer", await RefusedAsync(Write("buffer.dmg", WithMish(dmg, buffer)), "dmg"));
        // XML nested deeper than any property list.
        var deep = WithXml(dmg, "<?xml version=\"1.0\"?><plist>" + string.Concat(Enumerable.Repeat("<array>", 5000)) + "</plist>");
        Assert.Contains("damaged", await RefusedAsync(Write("deep.dmg", deep), "dmg"));
    }

    /// <summary>The block table of a DMG made by <see cref="Dmg"/>.</summary>
    private static byte[] Mish(byte[] dmg)
    {
        var xml = Xml(dmg);
        var start = xml.IndexOf("<data>", StringComparison.Ordinal) + 6;
        return Convert.FromBase64String(xml[start..xml.IndexOf("</data>", StringComparison.Ordinal)]);
    }

    private static string Xml(byte[] dmg)
    {
        var koly = dmg.AsSpan(dmg.Length - 512);
        var offset = (int)BinaryPrimitives.ReadUInt64BigEndian(koly[216..]);
        var length = (int)BinaryPrimitives.ReadUInt64BigEndian(koly[224..]);
        return System.Text.Encoding.UTF8.GetString(dmg, offset, length);
    }

    private static byte[] WithMish(byte[] dmg, byte[] mish)
    {
        var xml = Xml(dmg);
        var start = xml.IndexOf("<data>", StringComparison.Ordinal) + 6;
        return WithXml(dmg, xml[..start] + Convert.ToBase64String(mish) + xml[xml.IndexOf("</data>", StringComparison.Ordinal)..]);
    }

    /// <summary>The DMG with another XML block list (appended before the trailer, which then points at it).</summary>
    private static byte[] WithXml(byte[] dmg, string xml)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        var body = dmg.AsSpan(0, dmg.Length - 512);
        var koly = dmg.AsSpan(dmg.Length - 512).ToArray();
        BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(216), (ulong)body.Length);
        BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(224), (ulong)bytes.Length);
        return [.. body, .. bytes, .. koly];
    }

    [Fact]
    public async Task Disk_images_keep_their_mark_of_the_web()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Mark of the Web is a Windows (NTFS) stream.");
        var image = Write("download.img", Fat(Entries(), 12));
        var mark = Filee.Core.Platform.MarkOfTheWeb.Internet(new Uri("https://example.com/download.img"));
        Assert.SkipUnless(Filee.Core.Platform.MarkOfTheWeb.TryWrite(image, mark), "The temp drive holds no NTFS streams.");
        var folder = await UnpackAsync(image, "img");
        Assert.All(Directory.GetFiles(folder, "*", SearchOption.AllDirectories), f => Assert.Equal(mark, Filee.Core.Platform.MarkOfTheWeb.Read(f)));
    }
}
