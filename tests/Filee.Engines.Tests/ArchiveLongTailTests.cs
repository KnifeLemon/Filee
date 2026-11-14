// Filee's own CAB, LHA and ARJ readers. Archives are built in code (ArchiveFormatBuilders: stored and compressed, every
// header level and method the readers decode) and unpacked without 7-Zip; the bundled 7-Zip, where present, reads the
// same archives, which checks the builders against an independent reader. Real archives come from the tools on the PC
// when there are any: makecab (Windows: stored, MSZIP, LZX at the smallest and largest window, Quantum) and Bandizip's
// bz.exe (LZH). Then the hostile and damaged inputs.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;
using static Filee.Engines.Tests.ArchiveFormatBuilders;

namespace Filee.Engines.Tests;

public class ArchiveLongTailTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime OtherTime = new(2019, 12, 24, 18, 30, 0, DateTimeKind.Local);

    /// <summary>
    /// Nested names (Korean unless <paramref name="ascii"/>), text over several 32 KB blocks, noise, a long run of
    /// zeros, an empty file and, unless <paramref name="folders"/> is false (CAB keeps files only), an empty folder.
    /// </summary>
    private static List<TestEntry> Entries(bool ascii = false, bool folders = true)
    {
        List<TestEntry> entries =
        [
            new(ascii ? "docs/report.txt" : "문서/보고서.txt", Words(90_000)),
            new(ascii ? "docs/deep/data.csv" : "문서/깊은 폴더/data.csv", "a,b\n1,2\n"u8.ToArray(), OtherTime),
            new("noise.bin", Noise(45_000, 5)),
            new("zeros.bin", new byte[70_000]),
            new("zero.txt", []),
            new("mixed.bin", [.. Words(20_000), .. Noise(3_000, 9), .. Words(20_000)]),
        ];
        if (folders)
            entries.InsertRange(0, [new(ascii ? "docs/" : "문서/", null), new("empty/", null)]);
        return entries;
    }

    /// <summary>Path → content hash of the files, and "dir/" for the empty folders, as <see cref="Snapshot"/> lists them.</summary>
    private static SortedDictionary<string, string> Expected(IEnumerable<TestEntry> entries)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var list = entries.ToList();
        foreach (var e in list.Where(e => !e.IsFolder))
            result[e.Name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(e.Data!));
        foreach (var e in list.Where(e => e.IsFolder))
        {
            var name = e.Name.TrimEnd('/');
            if (!list.Any(o => o != e && o.Name.TrimEnd('/').StartsWith(name + "/", StringComparison.Ordinal)))
                result[name + "/"] = "";
        }
        return result;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Unpacks without 7-Zip and returns the folder.</summary>
    private async Task<string> UnpackAsync(string archive, string format)
    {
        var scratch = Directory.CreateDirectory(Path.Combine(fx.NewFolder(), "scratch")).FullName;
        return await new ArchiveExtractor(null).ExtractAsync(archive, format, scratch, Ct);
    }

    /// <param name="recorded">The local time the tool that wrote the archive stored for an entry's time.</param>
    private async Task AssertUnpacksAsync(string archive, string format, IReadOnlyList<TestEntry> entries,
        Func<DateTime, DateTime>? recorded = null)
    {
        var folder = await UnpackAsync(archive, format);
        Assert.Equal(Expected(entries), Snapshot(folder));
        foreach (var entry in entries.Where(e => !e.IsFolder))
        {
            var time = File.GetLastWriteTime(Path.Combine(folder, entry.Name));
            var expected = recorded?.Invoke(entry.Time) ?? entry.Time;
            Assert.True(Math.Abs((time - expected).TotalSeconds) <= 2, $"{entry.Name}: time {time} instead of {expected}");
        }
    }

    /// <summary>
    /// The local time makecab stores: Windows' FileTimeToLocalFileTime, which applies today's offset from UTC rather
    /// than the one in force on that date, so in a zone with summer time a May file is an hour off in November.
    /// </summary>
    private static DateTime MakecabTime(DateTime local) =>
        DateTime.SpecifyKind(local.ToUniversalTime() + TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow), DateTimeKind.Local);

    private async Task<string> RefusedAsync(string archive, string format)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => UnpackAsync(archive, format));
        return error.Message;
    }

    // ───────── Formats ─────────

    [Theory]
    [InlineData(CabCompression.None, 1, false)]
    [InlineData(CabCompression.None, 2, true)]
    [InlineData(CabCompression.Mszip, 1, false)]
    [InlineData(CabCompression.Mszip, 3, true)]
    [InlineData(CabCompression.MszipHistory, 1, false)]
    [InlineData(CabCompression.MszipHistory, 2, true)]
    [InlineData(CabCompression.LzxStored, 1, false)]
    [InlineData(CabCompression.LzxStored, 2, true)]
    public async Task Cab_unpacks_in_process(CabCompression compression, int folders, bool reserve)
    {
        var entries = Entries(folders: false);
        await AssertUnpacksAsync(Write("test.cab", Cab(entries, compression, folders, reserve)), "cab", entries);
    }

    [Fact]
    public async Task Cab_without_checksums_unpacks()
    {
        var entries = Entries(folders: false);
        await AssertUnpacksAsync(Write("plain.cab", Cab(entries, CabCompression.MszipHistory, checksums: false)), "cab", entries);
    }

    [Theory]
    [InlineData(0, "-lh0-")]
    [InlineData(0, "-lh5-")]
    [InlineData(1, "-lh5-")]
    [InlineData(1, "-lh6-")]
    [InlineData(2, "-lh0-")]
    [InlineData(2, "-lh5-")]
    [InlineData(2, "-lh6-")]
    [InlineData(2, "-lh7-")]
    [InlineData(3, "-lh7-")]
    public async Task Lha_unpacks_in_process(int level, string method)
    {
        var entries = Entries();
        await AssertUnpacksAsync(Write("test.lzh", Lzh(entries, method, level)), "lha", entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Arj_unpacks_in_process(int method)
    {
        var entries = Entries();
        await AssertUnpacksAsync(Write("test.arj", Arj(entries, method)), "arj", entries);
    }

    /// <summary>The bundled 7-Zip reads what the builders write: they follow the formats, not just Filee's readers.</summary>
    [Fact]
    public async Task The_builders_archives_are_read_by_7zip()
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var entries = Entries(ascii: true);
        var files = Entries(ascii: true, folders: false);
        foreach (var (name, bytes, list) in new[]
                 {
                     ("none.cab", Cab(files, CabCompression.None, 2, reserve: true), files),
                     ("mszip.cab", Cab(files, CabCompression.Mszip), files),
                     ("history.cab", Cab(files, CabCompression.MszipHistory, 2), files),
                     ("lzx.cab", Cab(files, CabCompression.LzxStored), files),
                     ("l0.lzh", Lzh(entries, "-lh5-", 0), entries),
                     ("l1.lzh", Lzh(entries, "-lh6-", 1), entries),
                     ("l2.lzh", Lzh(entries, "-lh7-", 2), entries),
                     ("l2s.lzh", Lzh(entries, "-lh0-", 2), entries),
                     ("m0.arj", Arj(entries, 0), entries),
                     ("m1.arj", Arj(entries, 1), entries),
                     ("m4.arj", Arj(entries, 4), entries),
                 })
        {
            var archive = Write(name, bytes);
            var target = fx.NewFolder();
            await Run7zAsync("x", "-y", "-o" + target, archive);
            Assert.True(Expected(list).SequenceEqual(Snapshot(target)), $"7-Zip unpacks {name} differently");
        }
    }

    // ───────── Archives made by other tools ─────────

    [Fact]
    public async Task Cabinets_made_by_makecab_read_back_byte_for_byte()
    {
        var makecab = Path.Combine(Environment.SystemDirectory, "makecab.exe");
        Assert.SkipUnless(OperatingSystem.IsWindows() && File.Exists(makecab), "makecab is a Windows tool");
        var source = fx.NewFolder();
        var entries = Entries(ascii: true, folders: false).Where(e => e.Data!.Length > 0).ToList();
        entries.Add(new TestEntry("large.txt", [.. Words(400_000), .. Noise(100_000, 3), .. Words(300_000)]));
        foreach (var entry in entries)
        {
            var path = Path.Combine(source, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, entry.Data!);
            File.SetLastWriteTime(path, entry.Time);
        }

        foreach (var compression in new[] { "NONE", "MSZIP", "LZX 15", "LZX 21" })
        {
            var cab = await MakecabAsync(makecab, source, entries, compression);
            await AssertUnpacksAsync(cab, "cab", entries, MakecabTime);
        }

        // Quantum: not decoded in-process; without 7-Zip that is said, with it 7-Zip unpacks it.
        var quantum = await MakecabAsync(makecab, source, entries, "QUANTUM 7 21");
        if (!quantum.EndsWith(".cab", StringComparison.Ordinal))
            return; // this makecab writes no Quantum
        Assert.Contains("does not unpack", await RefusedAsync(quantum, "cab"));
        if (Filee.Engines.Archives.SevenZip.Locate() is { } sevenZip)
        {
            var scratch = Directory.CreateDirectory(Path.Combine(fx.NewFolder(), "scratch")).FullName;
            var folder = await new ArchiveExtractor(sevenZip).ExtractAsync(quantum, "cab", scratch, Ct);
            Assert.Equal(Expected(entries), Snapshot(folder));
        }
    }

    /// <summary>
    /// Runs makecab with a directive file over <paramref name="entries"/> (folders through DestinationDir); returns the
    /// cabinet, or "" when this makecab refuses the compression type.
    /// </summary>
    private async Task<string> MakecabAsync(string makecab, string source, List<TestEntry> entries, string compression)
    {
        var output = fx.NewFolder();
        var parts = compression.Split(' ');
        var directives = new StringBuilder();
        directives.AppendLine(".Set CabinetNameTemplate=out.cab");
        directives.AppendLine(".Set DiskDirectoryTemplate=" + output);
        directives.AppendLine(".Set MaxDiskSize=0");
        if (parts[0] != "NONE")
            directives.AppendLine(".Set CompressionType=" + parts[0]);
        if (parts[0] == "LZX")
            directives.AppendLine(".Set CompressionMemory=" + parts[1]);
        if (parts[0] == "QUANTUM")
        {
            directives.AppendLine(".Set CompressionLevel=" + parts[1]);
            directives.AppendLine(".Set CompressionMemory=" + parts[2]);
        }
        directives.AppendLine(".Set Compress=" + (parts[0] == "NONE" ? "off" : "on"));
        foreach (var group in entries.GroupBy(e => Path.GetDirectoryName(e.Name) ?? ""))
        {
            directives.AppendLine(".Set DestinationDir=" + group.Key);
            foreach (var entry in group)
                directives.AppendLine('"' + Path.Combine(source, entry.Name) + '"');
        }
        var ddf = Path.Combine(output, "files.ddf");
        await File.WriteAllTextAsync(ddf, directives.ToString(), Ct);
        var start = new ProcessStartInfo(makecab, ["/F", ddf]) { WorkingDirectory = output, RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        var cab = Path.Combine(output, "out.cab");
        if (parts[0] == "QUANTUM" && (process.ExitCode != 0 || !File.Exists(cab)))
            return "";
        Assert.True(process.ExitCode == 0 && File.Exists(cab), $"makecab failed for {compression}");
        return cab;
    }

    [Fact]
    public async Task Lzh_made_by_bandizip_reads_back_byte_for_byte()
    {
        var bz = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bandizip", "bz.exe");
        Assert.SkipUnless(OperatingSystem.IsWindows() && File.Exists(bz), "Bandizip is not installed");
        var source = Path.Combine(fx.NewFolder(), "tree");
        var entries = Entries().Where(e => !e.IsFolder || e.Name == "empty/").ToList();
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
        var archive = Path.Combine(fx.NewFolder(), "bandizip.lzh");
        var start = new ProcessStartInfo(bz, ["c", "-y", "-r", "-fmt:lzh", archive, Path.Combine(source, "*")]) { RedirectStandardOutput = true, UseShellExecute = false };
        using (var process = Process.Start(start)!)
        {
            await process.StandardOutput.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            Assert.True(process.ExitCode == 0 && File.Exists(archive), "bz.exe failed");
        }
        await AssertUnpacksAsync(archive, "lha", entries);
    }

    // ───────── Hostile and damaged input ─────────

    [Fact]
    public async Task Paths_leaving_the_folder_are_refused()
    {
        List<TestEntry> evil = [new("good.txt", [1]), new("../../evil.txt", [2])];
        foreach (var (name, format, bytes) in new[]
                 {
                     ("evil.cab", "cab", Cab(evil, CabCompression.MszipHistory)),
                     ("evil.lzh", "lha", Lzh(evil, "-lh5-", 0)),
                     ("evil2.lzh", "lha", Lzh(evil, "-lh7-", 2)),
                     ("evil.arj", "arj", Arj(evil, 4)),
                 })
        {
            var dir = fx.NewFolder();
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, bytes, Ct);
            Assert.Contains("outside the target folder", await RefusedAsync(path, format));
            Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
        }
    }

    [Fact]
    public async Task Password_arj_is_refused_and_unsupported_features_are_named()
    {
        Assert.Contains("password", await RefusedAsync(Write("locked.arj", Arj(Entries(), 1, flags: 0x01)), "arj"));
        // Without 7-Zip these say that the bundled 7-Zip might read them.
        Assert.Contains("does not unpack", await RefusedAsync(Write("part.arj", Arj(Entries(), 1, flags: 0x04)), "arj"));
        Assert.Contains("does not unpack", await RefusedAsync(Write("quantum.cab", Cab(Entries(folders: false), CabCompression.Quantum)), "cab"));
        var lzs = Lzh([new("a.txt", Words(100))], "-lh0-", 2);
        "-lzs-"u8.CopyTo(lzs.AsSpan(2));
        Assert.Contains("does not unpack", await RefusedAsync(Write("lzs.lzh", FixLevel2Crc(lzs)), "lha"));
        var set = Cab(Entries(folders: false), CabCompression.None);
        set[30] |= 2; // "a next cabinet follows"
        Assert.Contains("does not unpack", await RefusedAsync(Write("set.cab", set), "cab"));
    }

    /// <summary>A level-2 LHA header's own CRC (extended header 0x00) after a test changed the header.</summary>
    private static byte[] FixLevel2Crc(byte[] archive)
    {
        var size = BinaryPrimitives.ReadUInt16LittleEndian(archive);
        archive[27] = archive[28] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(27), Crc16(archive.AsSpan(0, size)));
        return archive;
    }

    [Fact]
    public async Task Damaged_archives_are_refused_as_damaged()
    {
        var files = Entries(folders: false);

        // CAB: a data block's checksum, a file past its folder's data, a cut-off file, an LZX window over 2 MB.
        var cab = Cab(files, CabCompression.MszipHistory);
        var (firstData, _) = FirstCabBlock(cab);
        cab[firstData + 20] ^= 0x40;
        Assert.Contains("damaged", await RefusedAsync(Write("checksum.cab", cab), "cab"));

        var past = Cab(files, CabCompression.None);
        BinaryPrimitives.WriteUInt32LittleEndian(past.AsSpan(CabFileRecord(past, 0)), 10_000_000);
        Assert.Contains("damaged", await RefusedAsync(Write("past.cab", past), "cab"));

        var cut = Cab(files, CabCompression.Mszip);
        Assert.Contains("damaged", await RefusedAsync(Write("cut.cab", cut[..(cut.Length - 5000)]), "cab"));

        var window = Cab(files, CabCompression.LzxStored, window: 22);
        Assert.Contains("damaged", await RefusedAsync(Write("window.cab", window), "cab"));

        // A corrupted MSZIP stream (no checksums to catch it first).
        var deflate = Cab(files, CabCompression.Mszip, checksums: false);
        var (blockData, blockLength) = FirstCabBlock(deflate);
        deflate.AsSpan(blockData + 2, Math.Min(64, blockLength - 2)).Fill(0xFF);
        Assert.Contains("damaged", await RefusedAsync(Write("deflate.cab", deflate), "cab"));

        // LHA: the data's CRC-16, a level-0 header's byte sum, a cut-off archive.
        var lzh = Lzh([new("a.txt", Words(5000))], "-lh5-", 2);
        lzh[21] ^= 0x10; // the CRC-16 the header states
        Assert.Contains("damaged", await RefusedAsync(Write("crc.lzh", lzh), "lha"));
        var level0 = Lzh([new("a.txt", Words(5000))], "-lh5-", 0);
        level0[1] ^= 1;
        Assert.Contains("damaged", await RefusedAsync(Write("sum.lzh", level0), "lha"));
        var shortLzh = Lzh(Entries(), "-lh6-", 1);
        Assert.Contains("damaged", await RefusedAsync(Write("short.lzh", shortLzh[..(shortLzh.Length / 2)]), "lha"));

        // ARJ: a header CRC, the data's CRC-32.
        var arj = Arj([new("a.txt", Words(5000))], 1);
        arj[10] ^= 1;
        Assert.Contains("damaged", await RefusedAsync(Write("header.arj", arj), "arj"));
        var data = Arj([new("a.txt", Words(5000))], 4);
        data[^10] ^= 0x22;
        Assert.Contains("damaged", await RefusedAsync(Write("data.arj", data), "arj"));
    }

    [Fact]
    public async Task Bombs_and_entry_floods_are_refused_before_writing()
    {
        ArchiveSafety.TestLimits.Value = (1 << 20, 100, 20);
        try
        {
            // 4 MB of zeros packs to a few KB: over 100 times its size.
            List<TestEntry> zeros = [new("zeros.bin", new byte[4 << 20])];
            foreach (var (name, format, bytes) in new[]
                     {
                         ("bomb.cab", "cab", Cab(zeros, CabCompression.MszipHistory)),
                         ("bomb.lzh", "lha", Lzh(zeros, "-lh7-", 2)),
                         ("bomb.arj", "arj", Arj(zeros, 1)),
                     })
                Assert.Contains("decompression bomb", await RefusedAsync(Write(name, bytes), format));

            var many = Enumerable.Range(0, 30).Select(i => new TestEntry($"f{i}.txt", [(byte)i])).ToList();
            Assert.Contains("more than 20", await RefusedAsync(Write("many.cab", Cab(many, CabCompression.None)), "cab"));
            Assert.Contains("more than 20", await RefusedAsync(Write("many.lzh", Lzh(many, "-lh0-", 2)), "lha"));
            Assert.Contains("more than 20", await RefusedAsync(Write("many.arj", Arj(many, 0)), "arj"));
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    [Fact]
    public async Task Overlapping_cab_files_each_get_their_bytes_and_a_flood_of_them_is_refused()
    {
        var data = Words(50_000);
        // Two files over the same bytes, a third inside them.
        var cab = Cab([new("a.txt", data), new("b.txt", data[..20_000]), new("c.txt", data[..1000])], CabCompression.MszipHistory);
        BinaryPrimitives.WriteUInt32LittleEndian(cab.AsSpan(CabFileRecord(cab, 1) + 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(cab.AsSpan(CabFileRecord(cab, 2) + 4), 500);
        var folder = await UnpackAsync(Write("overlap.cab", cab), "cab");
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(folder, "a.txt")));
        Assert.Equal(data[..20_000], File.ReadAllBytes(Path.Combine(folder, "b.txt")));
        Assert.Equal(data[500..1500], File.ReadAllBytes(Path.Combine(folder, "c.txt")));

        // A hundred files all over the same 40 KB: refused rather than holding a hundred files open.
        var flood = Cab(Enumerable.Range(0, 100).Select(i => new TestEntry($"f{i}.txt", data[..400])).ToList(), CabCompression.None);
        for (var i = 0; i < 100; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(flood.AsSpan(CabFileRecord(flood, i) + 4), 0);
        Assert.Contains("damaged", await RefusedAsync(Write("flood.cab", flood), "cab"));
    }

    /// <summary>Offset of the <paramref name="index"/>th CFFILE in a cabinet without reserved areas.</summary>
    private static int CabFileRecord(byte[] cab, int index)
    {
        var position = (int)BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(16));
        for (var i = 0; i < index; i++)
            position += 16 + cab.AsSpan(position + 16).IndexOf((byte)0) + 1;
        return position;
    }

    /// <summary>Where the first CFDATA's data starts and how long it is (no reserved areas).</summary>
    private static (int Offset, int Length) FirstCabBlock(byte[] cab)
    {
        var folderStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(36));
        return (folderStart + 8, BinaryPrimitives.ReadUInt16LittleEndian(cab.AsSpan(folderStart + 4)));
    }
}
