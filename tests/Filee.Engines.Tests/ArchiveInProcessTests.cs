// The in-process archive readers without 7-Zip: every format Filee reads itself is built in code (ArchiveFormatBuilders),
// unpacked with ArchiveExtractor(null) and compared file by file; the conversions run through the engine as well. Then
// the hostile inputs: paths leaving the folder, links, passwords, decompression bombs (with lowered limits where the
// real ones would need gigabytes), entry floods, cyclic ISO folders, damaged data. Run the whole archive suite as
// without 7-Zip with FILEE_HIDE_7ZIP=1 (see docs/ENGINES.md, Archives).

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;
using static Filee.Engines.Tests.ArchiveFormatBuilders;

namespace Filee.Engines.Tests;

public class ArchiveInProcessTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime OtherTime = new(2019, 12, 24, 18, 30, 0, DateTimeKind.Local);

    /// <summary>Korean names, a nested folder, an empty folder, text, noise and an empty file.</summary>
    private static List<TestEntry> Entries(bool ascii = false) =>
    [
        new(ascii ? "docs/" : "문서/", null),
        new(ascii ? "docs/report.txt" : "문서/보고서.txt", Encoding.UTF8.GetBytes("Filee 압축 테스트 archive\n")),
        new(ascii ? "docs/deep/data.csv" : "문서/깊은 폴더/data.csv", "a,b\n1,2\n"u8.ToArray(), OtherTime),
        new("empty/", null),
        new("noise.bin", Noise(150_000, 5)),
        new("words.txt", Words(300_000)),
        new("zero.txt", []),
    ];

    /// <summary>Path → content of the files, and "dir/" for the empty folders, as <see cref="Snapshot"/> lists them.</summary>
    private static SortedDictionary<string, string> Expected(IEnumerable<TestEntry> entries, Func<string, string>? rename = null)
    {
        rename ??= n => n;
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var list = entries.ToList();
        foreach (var e in list.Where(e => !e.IsFolder))
            result[rename(e.Name)] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(e.Data!));
        foreach (var e in list.Where(e => e.IsFolder))
        {
            var name = e.Name.TrimEnd('/');
            if (!list.Any(o => o != e && o.Name.TrimEnd('/').StartsWith(name + "/", StringComparison.Ordinal)))
                result[rename(name) + "/"] = "";
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

    private async Task AssertUnpacksAsync(string archive, string format, IReadOnlyList<TestEntry> entries, Func<string, string>? rename = null)
    {
        var folder = await UnpackAsync(archive, format);
        Assert.Equal(Expected(entries, rename), Snapshot(folder));
        var known = entries.First(e => e.Data is { Length: > 0 } && e.Modified is null);
        var time = File.GetLastWriteTime(Path.Combine(folder, (rename ?? (n => n))(known.Name)));
        Assert.True(Math.Abs((time - KnownTime).TotalSeconds) <= 2, $"{known.Name}: time {time} instead of {KnownTime}");
    }

    private async Task<string> RefusedAsync(string archive, string format)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => UnpackAsync(archive, format));
        return error.Message;
    }

    // ───────── Formats ─────────

    [Fact]
    public async Task Zip_with_utf8_and_cp949_names_unpacks_in_process()
    {
        var entries = Entries();
        var zip = Write("utf8.zip", ZipOf(entries));
        await AssertUnpacksAsync(zip, "zip", entries);

        // A ZIP from Korean Windows: CP949 names without the UTF-8 flag, read with the OEM code page when that is 949.
        if (System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage == 949)
        {
            var legacy = Write("cp949.zip", ZipOf(entries, Encoding.GetEncoding(949)));
            await AssertUnpacksAsync(legacy, "zip", entries);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SevenZip_with_lzma_or_copy_coder_unpacks_in_process(bool lzma)
    {
        var entries = Entries();
        await AssertUnpacksAsync(Write("test.7z", SevenZip(entries, lzma)), "7z", entries);
    }

    [Fact]
    public async Task Rar4_and_rar5_unpack_in_process()
    {
        var ascii = Entries(ascii: true);
        await AssertUnpacksAsync(Write("old.rar", Rar4(ascii)), "rar", ascii);
        var entries = Entries();
        await AssertUnpacksAsync(Write("new.rar", Rar5(entries)), "rar", entries);
    }

    [Fact]
    public async Task Tar_and_compressed_tars_unpack_in_process()
    {
        var entries = Entries();
        var tar = Tar(entries);
        await AssertUnpacksAsync(Write("plain.tar", tar), "tar", entries);
        await AssertUnpacksAsync(Write("backup.tar.gz", Gzip(tar)), "tgz", entries);
        await AssertUnpacksAsync(Write("backup.tar.bz2", Bzip2(tar)), "tbz2", entries);
        await AssertUnpacksAsync(Write("backup.tar.xz", Xz(tar)), "txz", entries);
        await AssertUnpacksAsync(Write("backup.tar.Z", UnixCompress(tar)), "tz", entries);
        await AssertUnpacksAsync(Write("backup.tar.lz", Lzip(tar)), "tlz", entries);
        await AssertUnpacksAsync(Write("backup.tar.7z", SevenZip([new("backup.tar", tar)])), "tar7z", entries);
    }

    [Theory]
    [InlineData("gz")]
    [InlineData("bz2")]
    [InlineData("xz")]
    [InlineData("lzma")]
    [InlineData("z")]
    [InlineData("lz")]
    public async Task Compressed_single_files_unpack_in_process(string format)
    {
        var data = Words(1_500_000);
        byte[] packed = format switch
        {
            "gz" => Gzip(data),
            "bz2" => Bzip2(data),
            "xz" => Xz(data),
            "lzma" => [0x5D, 0x00, 0x00, 0x10, 0x00, .. BitConverter.GetBytes((long)data.Length), .. RawLzma(data, endMarker: false)],
            "z" => UnixCompress(data),
            _ => Lzip(data),
        };
        var folder = await UnpackAsync(Write("log.txt." + format, packed), format);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(folder, "log.txt"), Ct));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(16)]
    public void Unix_compress_decoder_follows_code_widths_and_clears(int maxBits)
    {
        // Small tables fill up quickly and are cleared many times; noise makes the codes grow to the top width.
        byte[] data = [.. Words(200_000), .. Noise(100_000, 3), .. new byte[50_000], .. Words(100_000)];
        using var decoder = new UnixCompressStream(new MemoryStream(UnixCompress(data, maxBits)));
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public async Task Unix_compress_files_written_by_the_test_encoder_are_what_7zip_reads()
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var data = Words(400_000);
        var z = Write("check.txt.Z", UnixCompress(data));
        var output = Path.Combine(Path.GetDirectoryName(z)!, "out");
        await Run7zAsync("x", "-o" + output, z);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(output, "check.txt"), Ct));
    }

    [Theory]
    [InlineData("Newc")]
    [InlineData("Crc")]
    [InlineData("Odc")]
    [InlineData("Binary")]
    public async Task Cpio_layouts_unpack_in_process(string layout)
    {
        var entries = Entries();
        await AssertUnpacksAsync(Write("files.cpio", Cpio(entries, Enum.Parse<CpioLayout>(layout))), "cpio", entries);
    }

    [Fact]
    public async Task Cpio_hard_link_groups_become_copies()
    {
        var data = Words(5_000);
        var folder = await UnpackAsync(Write("links.cpio", CpioWithHardLinks(data)), "cpio");
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(folder, "a.txt"), Ct));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(folder, "b.txt"), Ct));
    }

    [Fact]
    public async Task Rpm_payloads_in_gzip_and_xz_unpack_to_their_files()
    {
        var entries = Entries();
        var cpio = Cpio(entries);
        foreach (var payload in new[] { Gzip(cpio), Xz(cpio), cpio })
            await AssertUnpacksAsync(Write("filee-test.rpm", Rpm(payload)), "rpm", entries);
    }

    [Fact]
    public async Task Deb_unpacks_to_its_three_members()
    {
        var control = Gzip(Tar([new("control", "Package: filee-test\n"u8.ToArray())]));
        var data = Xz(Tar(Entries()));
        var folder = await UnpackAsync(Write("filee-test_1.0_amd64.deb", Deb(control, data)), "deb");
        Assert.Equal(["control.tar.gz", "data.tar.xz", "debian-binary"], Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(folder, "data.tar.xz"), Ct));

        // Long member names use the GNU name table.
        var longName = "a-member-with-a-long-name.txt";
        var ar = await UnpackAsync(Write("lib.deb", Ar([(longName, [1, 2, 3]), ("short.o", [4])])), "deb");
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(ar, longName), Ct));
    }

    [Fact]
    public async Task Iso_with_joliet_or_rock_ridge_names_unpacks_in_process()
    {
        var entries = Entries();
        await AssertUnpacksAsync(Write("joliet.iso", Iso(entries, IsoNames.Joliet)), "iso", entries);
        await AssertUnpacksAsync(Write("rockridge.iso", Iso(entries, IsoNames.RockRidge)), "iso", entries);

        // Plain ISO 9660: upper-case names, ";1" and a trailing dot dropped.
        var plain = Entries(ascii: true);
        await AssertUnpacksAsync(Write("plain.iso", Iso(plain, IsoNames.Plain)), "iso", plain, n => n.ToUpperInvariant());
    }

    [Fact]
    public async Task Iso_files_in_several_extents_are_joined()
    {
        var big = Noise(5000, 9);
        List<TestEntry> entries = [new("big.bin", big)];
        var folder = await UnpackAsync(Write("multi.iso", Iso(entries, IsoNames.Joliet, multiExtent: 4096)), "iso");
        Assert.Equal(big, await File.ReadAllBytesAsync(Path.Combine(folder, "big.bin"), Ct));
    }

    [Fact]
    public async Task Split_7z_zip_and_tar_volumes_unpack_in_process()
    {
        var entries = Entries();
        foreach (var (extension, bytes) in new[] { ("7z", SevenZip(entries)), ("zip", ZipOf(entries)), ("tar", Tar(entries)) })
        {
            var dir = fx.NewFolder();
            var size = bytes.Length / 3 + 1;
            for (var i = 0; i * size < bytes.Length; i++)
                await File.WriteAllBytesAsync(Path.Combine(dir, $"parts.{extension}.{i + 1:000}"), bytes.Skip(i * size).Take(size).ToArray(), Ct);
            await AssertUnpacksAsync(Path.Combine(dir, $"parts.{extension}.001"), "split", entries);
        }
    }

    [Fact]
    public async Task Egg_lzma_and_lzip_no_longer_need_7zip()
    {
        var text = Words(30_000);
        var egg = Write("lzma.egg", []);
        WriteEgg(egg, [new("lzma.txt", false, new EggBlock(text, 4, EggLzma(RawLzma(text, endMarker: false))))]);
        Assert.Equal(text, await File.ReadAllBytesAsync(Path.Combine(await UnpackAsync(egg, "egg"), "lzma.txt"), Ct));
    }

    [Fact]
    public async Task Engine_converts_in_process_formats_to_zip_tar_and_folders()
    {
        var entries = Entries();
        var source = Write("source.7z", SevenZip(entries));
        var tgz = (await fx.ConvertAsync([source], new Preset { TargetFormat = "tgz" })).Outputs.Single();
        var zip = (await fx.ConvertAsync([tgz], new Preset { TargetFormat = "zip" })).Outputs.Single();
        using (var archive = ZipFile.OpenRead(zip))
            Assert.Contains(archive.Entries, e => e.FullName == "문서/보고서.txt");
        var extract = BuiltInData.CreatePresets().Single(p => p.Id == "extract");
        var job = await fx.ConvertAsync([Write("disc.iso", Iso(entries, IsoNames.Joliet))], extract);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(Expected(entries), Snapshot(job.Outputs.Single()));
        var gz = (await fx.ConvertAsync([Write("log.txt.xz", Xz(Words(10_000)))], new Preset { TargetFormat = "gz" })).Outputs.Single();
        Assert.EndsWith("log.txt.gz", gz);
    }

    [Fact]
    public void Without_7zip_the_engine_keeps_the_in_process_edges_only()
    {
        var engine = new ArchiveConverter();
        Assert.True(engine.GetStatus().IsAvailable);
        var edges = new HashSet<(string, string)>(engine.Edges.Select(e => (e.From, e.To)));
        foreach (var format in ArchiveExtractor.BuiltInFormats)
            Assert.Contains((format, "zip"), edges);
        if (SevenZipExe is null)
        {
            Assert.DoesNotContain(("dmg", "zip"), edges);
            Assert.DoesNotContain(("zip", "7z"), edges);
            Assert.Contains(("xz", "gz"), edges);
        }
    }

    // ───────── Hostile input ─────────

    [Fact]
    public async Task Paths_leaving_the_folder_are_refused_in_every_reader()
    {
        List<TestEntry> evil = [new("good.txt", [1]), new("../../evil.txt", [2])];
        foreach (var (name, format, bytes) in new[]
                 {
                     ("evil.7z", "7z", SevenZip(evil)),
                     ("evil.rar", "rar", Rar5(evil)),
                     ("evil.tar", "tar", TarWithRawNames(evil)),
                     ("evil.cpio", "cpio", Cpio([new("../../evil.txt", [2])])),
                     ("evil.iso", "iso", Iso([new("../evil.txt", [2])], IsoNames.RockRidge)),
                     ("evil.deb", "deb", Ar([("../evil", [1])])),
                 })
        {
            var dir = fx.NewFolder();
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, bytes, Ct);
            var message = await RefusedAsync(path, format);
            Assert.Contains("outside the target folder", message);
            Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
        }
    }

    [Fact]
    public async Task Absolute_paths_and_hard_links_out_of_the_folder_are_refused()
    {
        var abs = Write("abs.cpio", []);
        await File.WriteAllBytesAsync(abs, CpioRaw([("/etc/filee-test", 0x81A4, "x"u8.ToArray())]), Ct);
        Assert.Contains("outside the target folder", await RefusedAsync(abs, "cpio"));

        var tar = Write("hard.tar", TarWith(w =>
        {
            w.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.HardLink, "link") { LinkName = "../../etc/passwd" });
        }));
        Assert.Contains("outside the target folder", await RefusedAsync(tar, "tar"));
    }

    [Fact]
    public async Task Links_are_left_out_and_hard_links_inside_become_copies()
    {
        var tar = Write("links.tar", TarWith(w =>
        {
            w.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "real.txt") { DataStream = new MemoryStream("real"u8.ToArray()) });
            w.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.SymbolicLink, "escape") { LinkName = "../../../../Windows/win.ini" });
            w.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.HardLink, "copy.txt") { LinkName = "real.txt" });
            w.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.Fifo, "pipe"));
        }));
        var folder = await UnpackAsync(tar, "tar");
        Assert.Equal(["copy.txt", "real.txt"], Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("real", await File.ReadAllTextAsync(Path.Combine(folder, "copy.txt"), Ct));

        var cpio = await UnpackAsync(Write("links.cpio", Cpio([new("a.txt", [1])], links: true)), "cpio");
        Assert.Equal(["a.txt"], Directory.GetFileSystemEntries(cpio).Select(Path.GetFileName));

        var iso = await UnpackAsync(Write("links.iso", Iso([new("a.txt", [1])], IsoNames.RockRidge, symlink: "../../../etc/passwd")), "iso");
        Assert.Equal(["a.txt"], Directory.GetFileSystemEntries(iso).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Encrypted_zip_and_7z_are_refused_in_process()
    {
        var zip = ZipOf([new("secret.txt", Words(1000))]);
        // General purpose flag bit 0 (encrypted) in the local and the central header.
        zip[6] |= 1;
        var central = zip.AsSpan().LastIndexOf("PK\x01\x02"u8);
        zip[central + 8] |= 1;
        Assert.Contains("password-protected", await RefusedAsync(Write("locked.zip", zip), "zip"));
        Assert.Contains("password-protected", await RefusedAsync(Write("locked.7z", SevenZip([new("secret.txt", Words(1000))], encrypted: true)), "7z"));
    }

    [Fact]
    public async Task Overlapping_zip_entries_and_lying_7z_sizes_are_refused_as_bombs()
    {
        // A 7z that says it holds 4 GiB, in a few hundred bytes: refused from the listing with the real limits.
        var lying = Write("bomb.7z", SevenZip([new("a.bin", new byte[100]), new("b.bin", new byte[100])], declaredSize: 4L << 30));
        Assert.Contains("decompression bomb", await RefusedAsync(lying, "7z"));

        // Twenty central directory entries sharing one deflated megabyte of zeros, with lowered limits.
        ArchiveSafety.TestLimits.Value = (1 << 20, 100, ArchiveSafety.MaxEntries);
        try
        {
            Assert.Contains("decompression bomb", await RefusedAsync(Write("overlap.zip", OverlappingZip(new byte[1 << 20], 20)), "zip"));
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    [Theory]
    [InlineData("gz")]
    [InlineData("bz2")]
    [InlineData("xz")]
    [InlineData("tgz")]
    public async Task Streams_that_grow_too_much_are_stopped_while_unpacking(string format)
    {
        var zeros = new byte[24 << 20];
        byte[] packed = format switch
        {
            "gz" => Gzip(zeros),
            "bz2" => Bzip2(zeros),
            "xz" => Xz(zeros),
            _ => Gzip(Tar([new("zeros.bin", zeros)])),
        };
        ArchiveSafety.TestLimits.Value = (1 << 20, 100, ArchiveSafety.MaxEntries);
        try
        {
            var archive = Write("bomb." + format, packed);
            var scratch = Directory.CreateDirectory(Path.Combine(fx.NewFolder(), "scratch")).FullName;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ArchiveExtractor(null).ExtractAsync(archive, format, scratch, Ct));
            Assert.Contains("decompression bomb", error.Message);
            Assert.Empty(Directory.GetFiles(scratch, "*", SearchOption.AllDirectories)); // the partial output is gone
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    [Theory]
    [InlineData("tar")]
    [InlineData("cpio")]
    [InlineData("zip")]
    [InlineData("iso")]
    public async Task Entry_floods_are_refused(string format)
    {
        var many = Enumerable.Range(0, 60).Select(i => new TestEntry($"f{i}.txt", [(byte)i])).ToList();
        byte[] bytes = format switch
        {
            "tar" => Tar(many),
            "cpio" => Cpio(many),
            "zip" => ZipOf(many),
            _ => Iso([.. many.Take(35)], IsoNames.Joliet),
        };
        ArchiveSafety.TestLimits.Value = (ArchiveSafety.RatioFloor, ArchiveSafety.MaxRatio, 30);
        try
        {
            Assert.Contains("entries", await RefusedAsync(Write("many." + format, bytes), format));
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    [Fact]
    public async Task Iso_folders_that_loop_and_extents_past_the_end_are_refused()
    {
        var loop = Write("loop.iso", Iso([new("a.txt", [1])], IsoNames.Joliet, loop: true));
        Assert.Contains("loop", await RefusedAsync(loop, "iso"));

        var image = Iso([new("a.txt", Words(10_000))], IsoNames.Plain);
        var cut = Write("cut.iso", image[..(image.Length - 4096)]);
        Assert.Contains("damaged", await RefusedAsync(cut, "iso"));
    }

    [Fact]
    public async Task Damaged_data_fails_with_a_clear_message_in_every_reader()
    {
        // A stored ZIP entry with one byte changed: the CRC-32 does not match.
        var zip = ZipOf([new("data.bin", Noise(5000, 2))], store: true);
        zip[100] ^= 0xFF;
        Assert.Contains("damaged", await RefusedAsync(Write("crc.zip", zip), "zip"));

        var sevenZip = SevenZip([new("data.bin", Noise(5000, 2))], lzma: false);
        sevenZip[40] ^= 0xFF;
        Assert.Contains("damaged", await RefusedAsync(Write("crc.7z", sevenZip), "7z"));

        var tar = Tar(Entries());
        Assert.Contains("damaged", await RefusedAsync(Write("cut.tar", tar[..(tar.Length / 2 + 100)]), "tar"));

        var xz = Xz(Words(100_000));
        xz[xz.Length / 2] ^= 0x55;
        Assert.Contains("damaged", await RefusedAsync(Write("bad.xz", xz), "xz"));

        var gz = Gzip(Words(100_000));
        gz[gz.Length - 6] ^= 0x01; // the CRC-32 in the trailer
        Assert.Contains("damaged", await RefusedAsync(Write("bad.gz", gz), "gz"));

        Assert.Contains("not a valid archive", await RefusedAsync(Write("fake.7z", "not an archive"u8.ToArray()), "7z"));
        Assert.Contains("not a valid archive", await RefusedAsync(Write("fake.cpio", "not an archive"u8.ToArray()), "cpio"));
        Assert.Contains("not a valid archive", await RefusedAsync(Write("fake.deb", "not an archive"u8.ToArray()), "deb"));
        await RefusedAsync(Write("fake.iso", new byte[40_000]), "iso");
        await RefusedAsync(Write("fake.Z", [0x1F, 0x9D, 0x90, 0xFF, 0xFF, 0xFF]), "z");
    }

    // ───────── Helpers ─────────

    internal static byte[] ZipOf(IReadOnlyList<TestEntry> entries, Encoding? names = null, bool store = false)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, names ?? Encoding.UTF8))
        {
            foreach (var e in entries)
            {
                var entry = zip.CreateEntry(e.Name, store ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                entry.LastWriteTime = e.Time;
                if (!e.IsFolder)
                    using (var stream = entry.Open())
                        stream.Write(e.Data);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A ZIP whose central directory lists <paramref name="count"/> entries that all point at one local file.</summary>
    private static byte[] OverlappingZip(byte[] data, int count)
    {
        var single = ZipOf([new("0.bin", data)]);
        var central = single.AsSpan().LastIndexOf("PK\x01\x02"u8);
        var end = single.AsSpan().LastIndexOf("PK\x05\x06"u8);
        var record = single[central..end];
        using var ms = new MemoryStream();
        ms.Write(single, 0, central);
        var directoryStart = ms.Position;
        for (var i = 0; i < count; i++)
        {
            var copy = (byte[])record.Clone();
            copy[46] = (byte)('a' + i); // a different one-letter name each time
            ms.Write(copy);
        }
        var directorySize = ms.Position - directoryStart;
        var eocd = single[end..];
        BitConverter.GetBytes((ushort)count).CopyTo(eocd, 8);
        BitConverter.GetBytes((ushort)count).CopyTo(eocd, 10);
        BitConverter.GetBytes((uint)directorySize).CopyTo(eocd, 12);
        BitConverter.GetBytes((uint)directoryStart).CopyTo(eocd, 16);
        ms.Write(eocd);
        return ms.ToArray();
    }

    private static byte[] TarWith(Action<System.Formats.Tar.TarWriter> write)
    {
        using var ms = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(ms, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
            write(writer);
        return ms.ToArray();
    }

    private static byte[] TarWithRawNames(IReadOnlyList<TestEntry> entries) => TarWith(w =>
    {
        foreach (var e in entries)
            w.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, e.Name) { DataStream = new MemoryStream(e.Data!) });
    });

    /// <summary>A newc cpio with names exactly as given (no "./" in front).</summary>
    private static byte[] CpioRaw(IReadOnlyList<(string Name, int Mode, byte[] Data)> entries)
    {
        using var output = new MemoryStream();
        void Write(string name, int mode, byte[] data)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            output.Write(Encoding.ASCII.GetBytes($"070701{1:X8}{mode:X8}{0:X8}{0:X8}{1:X8}{0:X8}{data.Length:X8}{0:X8}{0:X8}{0:X8}{0:X8}{nameBytes.Length:X8}{0:X8}"));
            output.Write(nameBytes);
            while (output.Position % 4 != 0)
                output.WriteByte(0);
            output.Write(data);
            while (output.Position % 4 != 0)
                output.WriteByte(0);
        }
        foreach (var (name, mode, data) in entries)
            Write(name, mode, data);
        Write("TRAILER!!!", 0, []);
        return output.ToArray();
    }
}
