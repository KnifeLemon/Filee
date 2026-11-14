// Hostile archives against the limits of the in-process readers, each built in code and refused (or unpacked) in well
// under a second: hard links that would each write a full copy, entry names too long or too many, folders nested too
// deep, TAR headers that state gigabytes, clashing names by the thousand, Windows device names, and ALZ bombs.

using System.Buffers.Binary;
using System.Formats.Tar;
using System.Text;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;
using static Filee.Engines.Tests.ArchiveFormatBuilders;

namespace Filee.Engines.Tests;

public class ArchiveLimitsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string NewScratch() => Directory.CreateDirectory(Path.Combine(fx.NewFolder(), "scratch")).FullName;

    /// <summary>Unpacks <paramref name="archive"/> in-process and returns the refusal's message; nothing may be written.</summary>
    private string Refused(string archive, string format)
    {
        var folder = NewScratch();
        var error = Assert.Throws<InvalidDataException>(() => ArchiveExtractor.ExtractInProcess(archive, format, folder, Ct));
        Assert.True(ArchiveSafety.IsRefusal(error), error.Message);
        Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
        return error.Message;
    }

    private static T WithLimits<T>(long floor, long ratio, Func<T> run)
    {
        ArchiveSafety.TestLimits.Value = (floor, ratio, ArchiveSafety.MaxEntries);
        try
        {
            return run();
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    // ───────── Hard links ─────────

    /// <summary>A TAR of one file and <paramref name="links"/> hard links to it.</summary>
    private static byte[] TarWithHardLinks(byte[] data, int links, TarEntryFormat format = TarEntryFormat.Pax)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, format, leaveOpen: true))
        {
            writer.WriteEntry(Entry(format, TarEntryType.RegularFile, "big.bin", data));
            for (var i = 0; i < links; i++)
            {
                var link = Entry(format, TarEntryType.HardLink, $"link{i}.bin", null);
                link.LinkName = "big.bin";
                writer.WriteEntry(link);
            }
        }
        return output.ToArray();
    }

    private static TarEntry Entry(TarEntryFormat format, TarEntryType type, string name, byte[]? data)
    {
        TarEntry entry = format == TarEntryFormat.Gnu ? new GnuTarEntry(type, name) : new PaxTarEntry(type, name);
        entry.ModificationTime = KnownTime;
        if (data is not null)
            entry.DataStream = new MemoryStream(data);
        return entry;
    }

    /// <summary>A newc cpio whose hard link group of <paramref name="links"/> names stores <paramref name="data"/> once, with the last.</summary>
    private static byte[] CpioLinkGroup(byte[] data, int links)
    {
        using var output = new MemoryStream();
        void Pad()
        {
            while (output.Position % 4 != 0)
                output.WriteByte(0);
        }
        void Header(string name, int ino, int mode, int nlink, int size)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            output.Write(Encoding.ASCII.GetBytes($"070701{ino:X8}{mode:X8}{0:X8}{0:X8}{nlink:X8}{0:X8}{size:X8}{0:X8}{1:X8}{0:X8}{0:X8}{nameBytes.Length:X8}{0:X8}"));
            output.Write(nameBytes);
            Pad();
        }
        for (var i = 0; i < links; i++)
            Header($"link{i}.bin", 7, 0x81A4, links + 1, 0);
        Header("big.bin", 7, 0x81A4, links + 1, data.Length);
        output.Write(data);
        Pad();
        Header("TRAILER!!!", 0, 0, 1, 0);
        return output.ToArray();
    }

    [Theory]
    [InlineData("tar")]
    [InlineData("cpio")]
    public void Hard_links_count_at_the_size_of_their_file_before_anything_is_written(string format)
    {
        // One megabyte and 300 links to it: 301 MB written from an archive of about 1.2 MB, past the lowered ratio of 100.
        var data = Noise(1 << 20, 3);
        var bytes = format == "tar" ? TarWithHardLinks(data, 300) : CpioLinkGroup(data, 300);
        var message = WithLimits(1 << 20, 100, () => Refused(Write("links." + format, bytes), format));
        Assert.Contains("decompression bomb", message);
    }

    [Theory]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    public void A_few_hard_links_still_unpack_as_copies(TarEntryFormat format)
    {
        var data = Words(5_000);
        var folder = NewScratch();
        ArchiveExtractor.ExtractInProcess(Write("few.tar", TarWithHardLinks(data, 3, format)), "tar", folder, Ct);
        foreach (var name in new[] { "big.bin", "link0.bin", "link1.bin", "link2.bin" })
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(folder, name)));
    }

    // ───────── Names ─────────

    [Theory]
    [InlineData("tar")]
    [InlineData("cpio")]
    public void An_entry_name_longer_than_windows_allows_is_refused(string format)
    {
        var name = string.Concat(Enumerable.Repeat("abcdefghi/", 4_000)) + "f.txt"; // 40,005 characters
        var entries = new List<TestEntry> { new(name, [1, 2, 3]) };
        var message = Refused(Write("long." + format, format == "tar" ? Tar(entries) : Cpio(entries)), format);
        Assert.Contains($"longer than {ArchiveSafety.MaxPathLength:N0} characters", message);
    }

    [Theory]
    [InlineData("tar")]
    [InlineData("cpio")]
    [InlineData("zip")]
    public void Names_that_together_pass_the_limit_are_refused(string format)
    {
        var entries = Enumerable.Range(0, 100).Select(i => new TestEntry($"{i:D3}-{new string('n', 200)}.txt", [(byte)i])).ToList();
        var bytes = format switch
        {
            "tar" => Tar(entries),
            "cpio" => Cpio(entries),
            _ => ZipOf(entries),
        };
        var archive = Write("names." + format, bytes);
        ArchiveSafety.TestNameChars.Value = 10_000; // 100 names of some 210 characters are 21,000
        try
        {
            Assert.Contains("entry names together", Refused(archive, format));
        }
        finally
        {
            ArchiveSafety.TestNameChars.Value = null;
        }
        ArchiveExtractor.ExtractInProcess(archive, format, NewScratch(), Ct); // the real limit lets them pass
    }

    private static byte[] ZipOf(IEnumerable<TestEntry> entries)
    {
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                using var stream = zip.CreateEntry(entry.Name).Open();
                stream.Write(entry.Data);
            }
        return output.ToArray();
    }

    // ───────── Depth ─────────

    [Theory]
    [InlineData("tar")]
    [InlineData("cpio")]
    [InlineData("zip")]
    public void Entries_nested_deeper_than_the_limit_are_refused(string format)
    {
        var deep = string.Concat(Enumerable.Repeat("a/", 600)) + "f.txt";
        var entries = new List<TestEntry> { new(deep, [1]) };
        var bytes = format switch
        {
            "tar" => Tar(entries),
            "cpio" => Cpio(entries),
            _ => ZipOf(entries),
        };
        Assert.Contains("folders deep", Refused(Write("deep." + format, bytes), format));
    }

    [Fact]
    public void Path_depth_counts_names_not_separators_and_caps_resolved_paths()
    {
        Assert.Equal(2, ArchiveSafety.Depth("./a//b/"));
        Assert.Equal(4, ArchiveSafety.Depth("a\\b/.../c")); // "..." is a name
        Assert.Equal(0, ArchiveSafety.Depth("./"));

        var root = fx.NewFolder();
        var limit = string.Join('/', Enumerable.Repeat("d", ArchiveSafety.MaxPathDepth));
        Assert.StartsWith(root, ArchiveSafety.ResolveEntryPath(root, limit, "x.tar"), StringComparison.OrdinalIgnoreCase);
        var error = Assert.Throws<InvalidDataException>(() => ArchiveSafety.ResolveEntryPath(root, limit + "/f", "x.tar"));
        Assert.True(ArchiveSafety.IsRefusal(error));
    }

    // ───────── TAR headers ─────────

    /// <summary>One 512-byte TAR header of type <paramref name="type"/> stating <paramref name="size"/> bytes, and no data.</summary>
    private static byte[] TarHeader(char type, long size)
    {
        var header = new byte[1024];
        Encoding.ASCII.GetBytes("././@LongLink").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
        Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);
        header[156] = (byte)type;
        Encoding.ASCII.GetBytes("ustar  \0").CopyTo(header, 257);
        "        "u8.CopyTo(header.AsSpan(148));
        var sum = header.AsSpan(0, 512).ToArray().Sum(b => b);
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        return header;
    }

    [Theory]
    [InlineData('x', 0x7FFF_0000L, "extended header")]
    [InlineData('g', 0x7FFF_0000L, "extended header")]
    [InlineData('L', 1L << 20, "entry name is longer")]
    [InlineData('K', 0x7FFF_0000L, "entry name is longer")]
    public void Tar_headers_that_state_gigabytes_are_refused_before_they_are_read(char type, long size, string expected)
    {
        var message = Refused(Write("header.tar", TarHeader(type, size)), "tar");
        Assert.Contains(expected, message);
    }

    // ───────── Clashing names ─────────

    [Fact]
    public void Thousands_of_entries_of_one_name_are_numbered_in_one_pass()
    {
        const int Count = 3_000;
        var entries = Enumerable.Range(0, Count).Select(i => new TestEntry("same.txt", Encoding.ASCII.GetBytes(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToList();
        var folder = NewScratch();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ArchiveExtractor.ExtractInProcess(Write("same.tar", Tar(entries)), "tar", folder, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"{Count} clashing names in {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(Count, Directory.GetFiles(folder).Length);
        Assert.Equal("0", File.ReadAllText(Path.Combine(folder, "same.txt")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(folder, "same_1.txt")));
        Assert.Equal($"{Count - 1}", File.ReadAllText(Path.Combine(folder, $"same_{Count - 1}.txt")));
    }

    [Fact]
    public void Numbering_skips_names_that_are_taken_already()
    {
        var folder = NewScratch();
        var unique = new UniquePaths();
        var path = Path.Combine(folder, "a.txt");
        File.WriteAllText(path, "");
        File.WriteAllText(Path.Combine(folder, "a_2.txt"), "");
        var first = unique.Get(path);
        Assert.Equal(Path.Combine(folder, "a_1.txt"), first);
        File.WriteAllText(first, "");
        Assert.Equal(Path.Combine(folder, "a_3.txt"), unique.Get(path));
    }

    // ───────── Device names ─────────

    [Theory]
    [InlineData("CON .txt", "_CON .txt")]
    [InlineData("conin$", "_conin$")]
    [InlineData("CONOUT$.log", "_CONOUT$.log")]
    [InlineData("COM¹.txt", "_COM¹.txt")]
    [InlineData("lpt³", "_lpt³")]
    [InlineData("COM0", "_COM0")]
    [InlineData("LPT0.bin", "_LPT0.bin")]
    [InlineData("NUL  .tar.gz", "_NUL  .tar.gz")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("COM10", "COM10")]
    public void Windows_device_names_get_a_prefix(string entry, string expected)
    {
        var root = fx.NewFolder();
        Assert.Equal(Path.Combine(root, "d", expected), ArchiveSafety.ResolveEntryPath(root, "d/" + entry, "x.zip"));
    }

    // ───────── ALZ ─────────

    [Fact]
    public void An_alz_that_grows_past_the_ratio_is_refused_before_it_is_written()
    {
        var alz = Path.Combine(fx.NewFolder(), "bomb.alz");
        WriteAlz(alz, [new("zeros.bin", new byte[8 << 20], Method: 2)]);
        var message = WithLimits(1 << 20, 100, () => Refused(alz, "alz"));
        Assert.Contains("decompression bomb", message);
    }

    [Fact]
    public void Alz_entries_of_one_name_are_numbered()
    {
        var alz = Path.Combine(fx.NewFolder(), "same.alz");
        WriteAlz(alz, [new("a.txt", "1"u8.ToArray()), new("a.txt", "2"u8.ToArray()), new("a.txt", "3"u8.ToArray())]);
        var folder = NewScratch();
        ArchiveExtractor.ExtractInProcess(alz, "alz", folder, Ct);
        Assert.Equal(["1", "2", "3"], new[] { "a.txt", "a_1.txt", "a_2.txt" }.Select(n => File.ReadAllText(Path.Combine(folder, n))));
    }
}
