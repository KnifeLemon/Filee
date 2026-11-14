// Filee's own NSIS reader (Archives/NsisReader): installers built in code (NsisBuilder) for every compression, solid or
// not, NSIS 2 / 3 strings, hostile headers and data; Tesseract's pinned Windows installer against 7-Zip when the file
// is on this PC (build/.cache after build/fetch-engines.ps1 -Only tesseract, or FILEE_TESSERACT_INSTALLER); the engine
// install of an NSIS package; and a fuzz target.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Filee.Engines.Archives;
using Filee.Engines.Infrastructure;
using static Filee.Engines.Tests.ArchiveBuilders;
using static Filee.Engines.Tests.NsisBuilder;

namespace Filee.Engines.Tests;

public sealed class NsisReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-nsis-" + Guid.NewGuid().ToString("N")[..8]);

    public NsisReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { EngineInstaller.DeleteFolder(_root); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly DateTime Stamp = new(2024, 6, 6, 23, 46, 56, DateTimeKind.Utc);

    /// <summary>
    /// A small installer like Tesseract's: a plug-in extracted twice, files in $INSTDIR and two folders below it, a
    /// file in $TEMP, CreateDirectory (not SetOutPath) of a shell folder, and the uninstaller.
    /// </summary>
    private static (List<Op> Script, List<byte[]> Data) Typical() =>
    ([
        new ExtractFile("$PLUGINSDIR\\System.dll", 0),
        new SetOutPath("$INSTDIR"),
        new ExtractFile("tool.exe", 1, Stamp),
        new ExtractFile("$PLUGINSDIR\\System.dll", 0),
        new SetOutPath("$INSTDIR\\data"),
        new ExtractFile("words.txt", 2, Stamp),
        new SetOutPath("$OUTDIR\\more"),
        new ExtractFile("noise.bin", 3),
        new SetOutPath("$SMPROGRAMS\\Tool", Create: true),
        new SetOutPath("$INSTDIR"),
        new ExtractFile("tool.exe", 1, Stamp),
        new ExtractFile("$TEMP\\temp.txt", 4),
        new WriteUninstaller("$INSTDIR\\uninstall.exe", 5),
    ],
    [Words(3_000), Noise(5_000, 1), Words(20_000), Noise(2_000, 2), "temp"u8.ToArray(), [.. "MZ"u8, .. Noise(700, 3)]]);

    private string Write(byte[] installer, string name = "setup.exe")
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6] + "-" + name);
        File.WriteAllBytes(path, installer);
        return path;
    }

    private string NewFolder() => Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N")[..6])).FullName;

    /// <summary>Relative path ('/' separators) → content of every file below <paramref name="folder"/>.</summary>
    private static SortedDictionary<string, byte[]> Contents(string folder) =>
        new(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllBytes), StringComparer.Ordinal);

    [Theory]
    [InlineData("Lzma", true, "Unicode")]
    [InlineData("Lzma", false, "Unicode")]
    [InlineData("Deflate", true, "Ansi3")]
    [InlineData("Deflate", false, "Unicode")]
    [InlineData("Bzip2", true, "Unicode")]
    [InlineData("Bzip2", false, "Ansi3")]
    [InlineData("Stored", false, "Ansi3")]
    public void Every_compression_unpacks_what_the_script_installs(string method, bool solid, string kind)
    {
        var (compression, strings) = (Enum.Parse<NsisCompression>(method), Enum.Parse<StringKind>(kind));
        var (script, data) = Typical();
        var installer = Write(Build(script, data, new Options { Compression = compression, Solid = solid, Strings = strings }));

        var everything = NewFolder();
        var listing = NsisReader.Extract(installer, everything, NsisScope.Everything, TestContext.Current.CancellationToken);

        Assert.Equal((compression, solid, strings == StringKind.Unicode), (listing.Compression, listing.Solid, listing.Unicode));
        var files = Contents(everything);
        Assert.Equal(["$PLUGINSDIR/System.dll", "$TEMP/temp.txt", "data/more/noise.bin", "data/words.txt", "tool.exe", "uninstall.exe"], files.Keys);
        Assert.Equal(data[0], files["$PLUGINSDIR/System.dll"]);
        Assert.Equal(data[1], files["tool.exe"]);
        Assert.Equal(data[2], files["data/words.txt"]);
        Assert.Equal(data[3], files["data/more/noise.bin"]);
        Assert.Equal(data[5], files["uninstall.exe"]);
        Assert.Equal(Stamp, File.GetLastWriteTimeUtc(Path.Combine(everything, "tool.exe")));
        Assert.True(listing.Files.Single(f => f.Path == "uninstall.exe").IsUninstaller);

        var installDir = NewFolder();
        NsisReader.Extract(installer, installDir, NsisScope.InstallDir, TestContext.Current.CancellationToken);
        Assert.Equal(["data/more/noise.bin", "data/words.txt", "tool.exe", "uninstall.exe"], Contents(installDir).Keys);
    }

    [Fact]
    public void Nsis_2_strings_name_the_same_files_but_have_no_uninstaller_opcode()
    {
        var (script, data) = Typical();
        var installer = Write(Build(script, data, new Options { Strings = StringKind.Ansi2, Compression = NsisCompression.Deflate }));

        var listing = NsisReader.List(installer, TestContext.Current.CancellationToken);

        Assert.False(listing.Unicode);
        // Opcode 62 is WriteUninstaller only in NSIS 3: an NSIS 2 script's 62 is something else and writes nothing.
        Assert.Equal(["$PLUGINSDIR\\System.dll", "tool.exe", "data\\words.txt", "data\\more\\noise.bin", "$TEMP\\temp.txt"], listing.Files.Select(f => f.Path));
    }

    [Fact]
    public void Ansi_names_keep_latin_1_letters_and_escaped_codes()
    {
        var name = "caf\u00e9 \u00fc\u00fe.txt"; // ü and þ are NSIS 2 codes 252 and 254 unless escaped
        var installer = Write(Build([new SetOutPath("$INSTDIR"), new ExtractFile(name, 0)], [Words(100)],
            new Options { Strings = StringKind.Ansi2, Compression = NsisCompression.Deflate }));

        var folder = NewFolder();
        NsisReader.Extract(installer, folder, NsisScope.InstallDir, TestContext.Current.CancellationToken);

        Assert.Equal([name], Contents(folder).Keys);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void Lzma_after_a_filter_byte_unpacks(byte filter)
    {
        // Words have no E8/E9 bytes, so the x86 converter (filter 1) leaves them as they are.
        var installer = Write(Build([new SetOutPath("$INSTDIR"), new ExtractFile("a.txt", 0)], [Words(10_000)],
            new Options { Filter = filter }));

        var folder = NewFolder();
        NsisReader.Extract(installer, folder, NsisScope.InstallDir, TestContext.Current.CancellationToken);

        Assert.Equal(Words(10_000), File.ReadAllBytes(Path.Combine(folder, "a.txt")));
    }

    [Fact]
    public void Non_solid_installers_may_store_blocks_compression_could_not_shrink()
    {
        var (script, data) = Typical();
        var installer = Write(Build(script, data, new Options { Solid = false, Stored = new HashSet<int> { 1, 3 } }));

        var folder = NewFolder();
        NsisReader.Extract(installer, folder, NsisScope.InstallDir, TestContext.Current.CancellationToken);

        Assert.Equal(data[3], File.ReadAllBytes(Path.Combine(folder, "data", "more", "noise.bin")));
        Assert.Equal(data[2], File.ReadAllBytes(Path.Combine(folder, "data", "words.txt")));
    }

    [Fact]
    public void An_installer_without_a_stub_or_crc_reads()
    {
        var (script, data) = Typical();
        var installer = Write(Build(script, data, new Options { Stub = 0, Crc = false }));

        Assert.Equal(6, NsisReader.List(installer, TestContext.Current.CancellationToken).Files.Count);
    }

    [Fact]
    public void Same_names_with_other_data_are_numbered_as_7zip_numbers_them()
    {
        var installer = Write(Build([new SetOutPath("$INSTDIR"), new ExtractFile("a.txt", 0), new ExtractFile("a.txt", 1)],
            ["one"u8.ToArray(), "two"u8.ToArray()]));

        var folder = NewFolder();
        NsisReader.Extract(installer, folder, NsisScope.InstallDir, TestContext.Current.CancellationToken);

        Assert.Equal("one", File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(folder, "a_1.txt")));
    }

    // ───────────────────────── Hostile input ─────────────────────────

    /// <summary>The reader's error for <paramref name="installer"/>, which must come quickly.</summary>
    private InvalidDataException Refused(byte[] installer, NsisScope scope = NsisScope.Everything)
    {
        var path = Write(installer);
        var started = DateTime.UtcNow;
        var error = Assert.Throws<InvalidDataException>(() => NsisReader.Extract(path, NewFolder(), scope, TestContext.Current.CancellationToken));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), $"took {DateTime.UtcNow - started}");
        return error;
    }

    [Fact]
    public void Something_else_is_not_an_installer()
    {
        Assert.Contains("not a valid archive", Refused(Noise(100_000, 7)).Message);
        Assert.Contains("not a valid archive", Refused([]).Message);
    }

    [Fact]
    public void A_header_block_past_the_limit_is_refused_before_anything_is_read()
    {
        var installer = Build([new SetOutPath("$INSTDIR")], [], new Options { Crc = false });
        BitConverter.GetBytes(0x7FFFFFF0u).CopyTo(installer, 512 + 20);

        var error = Refused(installer);

        Assert.True(ArchiveSafety.IsRefusal(error));
        Assert.Contains("NSIS header", error.Message);
    }

    [Theory]
    [InlineData("Lzma", true)]
    [InlineData("Deflate", false)]
    public void Header_block_sizes_that_lie_cost_no_memory(string method, bool solid)
    {
        var compression = Enum.Parse<NsisCompression>(method);
        // The first header claims 60 MB of header; the data holds a few hundred bytes.
        var installer = Build([new SetOutPath("$INSTDIR")], [], new Options { Compression = compression, Solid = solid, Crc = false });
        BitConverter.GetBytes(60u << 20).CopyTo(installer, 512 + 20);
        var before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Contains("damaged", Refused(installer).Message);

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 << 20);
    }

    [Fact]
    public void Entry_counts_and_string_offsets_past_the_header_are_damage()
    {
        var (script, data) = Typical();
        Assert.Contains("script", Refused(Build(script, data, new Options
        {
            Header = h => BitConverter.GetBytes(uint.MaxValue).CopyTo(h, EntriesBlock + 4),
        })).Message);
        Assert.Contains("string table", Refused(Build(script, data, new Options
        {
            Header = h => BitConverter.GetBytes(0x7FFFFFFFu).CopyTo(h, StringsBlock),
        })).Message);
        Assert.Contains("outside the string table", Refused(Build([new Raw(20, 0, 1_000_000, 0, -1, -1)], [Words(10)])).Message);
        Assert.Contains("negative", Refused(Build([new Raw(20, 0, 0, -5, -1, -1)], [Words(10)])).Message);
    }

    [Fact]
    public void A_string_without_its_end_is_damage()
    {
        var installer = Build([new ExtractFile("name", 0)], [Words(10)], new Options
        {
            Header = h => h.AsSpan(h.Length - 2).Fill((byte)'x'), // the last string's terminator
        });

        Assert.Contains("no end", Refused(installer).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Data_offsets_past_the_end_are_damage(bool solid)
    {
        var installer = Build([new SetOutPath("$INSTDIR"), new ExtractFile("a", 0), new ExtractFile("b", 0, Offset: 1_000_000_000)],
            [Words(100)], new Options { Solid = solid, Compression = NsisCompression.Deflate });

        Assert.Contains("past the end", Refused(installer).Message);
    }

    [Fact]
    public void Solid_data_that_overlaps_or_lies_about_its_size_is_damage()
    {
        // The second file starts inside the first one's data.
        Assert.Contains("overlaps", Refused(Build([new SetOutPath("$INSTDIR"), new ExtractFile("a", 0), new ExtractFile("b", 0, Offset: 10)],
            [Words(1_000)])).Message);
        // A size of 4 GB for 1 KB of data.
        Assert.Contains("impossible size", Refused(Build([new SetOutPath("$INSTDIR"), new ExtractFile("a", 0)], [Words(1_000)],
            new Options { BlockSize = (_, _) => 0xFFFFFFF0 })).Message);
        Assert.Contains("ends early", Refused(Build([new SetOutPath("$INSTDIR"), new ExtractFile("a", 0)], [Words(1_000)],
            new Options { BlockSize = (_, _) => 0x7FFFFFF0 })).Message);
    }

    [Fact]
    public void Non_solid_blocks_past_the_end_are_damage()
    {
        Assert.Contains("past the end", Refused(Build([new SetOutPath("$INSTDIR"), new ExtractFile("a", 0)], [Words(1_000)],
            new Options { Solid = false, BlockSize = (_, size) => size + 1_000_000 })).Message);
    }

    [Fact]
    public void A_dictionary_past_the_limit_is_refused()
    {
        var installer = Build([new SetOutPath("$INSTDIR"), new ExtractFile("a", 0)], [Words(1_000)], new Options { Crc = false });
        BitConverter.GetBytes(0x7FFFFF00u).CopyTo(installer, 512 + 28 + 1); // after the properties byte

        var error = Refused(installer);

        Assert.True(ArchiveSafety.IsRefusal(error));
        Assert.Contains("dictionary", error.Message);
    }

    [Fact]
    public void Every_cut_of_an_installer_is_refused()
    {
        foreach (var options in new[]
                 {
                     new Options { Crc = false }, new Options { Crc = false, Solid = false, Compression = NsisCompression.Deflate },
                     new Options { Crc = false, Compression = NsisCompression.Bzip2 }, new Options(),
                 })
        {
            var (script, data) = Typical();
            var installer = Build(script, data, options);
            for (var length = 0; length < installer.Length - 4; length += 97)
                Refused(installer[..length]);
        }
    }

    [Fact]
    public void A_crc_error_is_damage()
    {
        var (script, data) = Typical();
        var installer = Build(script, data, new Options { Compression = NsisCompression.Deflate, Solid = false });
        installer[^20] ^= 1;

        Assert.Contains("CRC", Refused(installer).Message);
    }

    [Fact]
    public void Paths_out_of_the_target_are_refused_and_outside_install_dir_are_left_out()
    {
        var climbing = Build([new SetOutPath("$INSTDIR"), new ExtractFile("..\\..\\evil.txt", 0)], [Words(10)]);
        Assert.True(ArchiveSafety.IsRefusal(Refused(climbing, NsisScope.InstallDir)));

        var absolute = Build([new SetOutPath("$INSTDIR"), new ExtractFile("C:\\evil.txt", 0), new ExtractFile("ok.txt", 0)], [Words(10)]);
        Assert.True(ArchiveSafety.IsRefusal(Refused(absolute, NsisScope.Everything)));
        var folder = NewFolder();
        NsisReader.Extract(Write(absolute), folder, NsisScope.InstallDir, TestContext.Current.CancellationToken);
        Assert.Equal(["ok.txt"], Contents(folder).Keys);
    }

    [Fact]
    public void A_name_that_is_a_file_and_a_folder_is_damage()
    {
        var installer = Build([new SetOutPath("$INSTDIR"), new ExtractFile("sub", 0), new ExtractFile("SUB/x.txt", 1)], [Words(10), Words(20)]);

        Assert.Contains("is a file and a folder", Refused(installer).Message);
    }

    [Fact]
    public void An_out_path_that_doubles_itself_is_refused_with_bounded_memory()
    {
        List<Op> script = [new SetOutPath("$INSTDIR\\aaaaaaaaaaaaaaaa")];
        script.AddRange(Enumerable.Repeat<Op>(new SetOutPath("$OUTDIR\\$OUTDIR"), 40));
        script.Add(new ExtractFile("a", 0));
        var before = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(ArchiveSafety.IsRefusal(Refused(Build(script, [Words(10)]))));

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 20);
    }

    [Fact]
    public void Bombs_and_entry_floods_are_refused()
    {
        ArchiveSafety.TestLimits.Value = (1 << 20, 20, 50);
        try
        {
            // 8 MB of zeros, solid: the decoder's output counts, also for data no file is written from.
            var zeros = Build([new SetOutPath("$INSTDIR"), new ExtractFile("a", 0)], [new byte[8 << 20]]);
            Assert.True(ArchiveSafety.IsRefusal(Refused(zeros)));
            var skipped = Build([new SetOutPath("$INSTDIR"), new ExtractFile("b", 1)], [new byte[8 << 20], Words(10)]);
            Assert.True(ArchiveSafety.IsRefusal(Refused(skipped)));

            // One 200 KB block written under 40 names, non-solid: what is written counts.
            List<Op> copies = [new SetOutPath("$INSTDIR")];
            copies.AddRange(Enumerable.Range(0, 40).Select(i => new ExtractFile($"copy{i}.bin", 0)));
            Assert.True(ArchiveSafety.IsRefusal(Refused(Build(copies, [new byte[200_000]],
                new Options { Solid = false, Compression = NsisCompression.Deflate }))));

            List<Op> many = [new SetOutPath("$INSTDIR")];
            many.AddRange(Enumerable.Range(0, 60).Select(i => new ExtractFile($"f{i}", 0)));
            Assert.Contains("entries", Refused(Build(many, [Words(10)])).Message);
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    [Theory]
    [InlineData("Lzma")]
    [InlineData("Bzip2")]
    public void Cancelling_stops_unpacking(string method)
    {
        var compression = Enum.Parse<NsisCompression>(method);
        var installer = Write(Build([new SetOutPath("$INSTDIR"), new ExtractFile("a", 0)], [Words(3_000_000)],
            new Options { Compression = compression }));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => NsisReader.Extract(installer, NewFolder(), NsisScope.Everything, cancel.Token));
    }

    // ───────────────────────── Engine install ─────────────────────────

    private sealed class OneFile(string url, byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.ToString() == url
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task The_tesseract_package_installs_from_its_nsis_installer_without_7zip()
    {
        var installer = Build(
        [
            new ExtractFile("$PLUGINSDIR\\System.dll", 0),
            new SetOutPath("$INSTDIR"),
            new ExtractFile("tesseract.exe", 1),
            new ExtractFile("libtesseract-5.dll", 2),
            new ExtractFile("lstmtraining.exe", 3),
            new ExtractFile("tesseract.1.html", 3),
            new SetOutPath("$INSTDIR\\tessdata"),
            new ExtractFile("eng.traineddata", 3),
            new SetOutPath("$INSTDIR\\doc"),
            new ExtractFile("LICENSE", 4),
            new WriteUninstaller("$INSTDIR\\tesseract-uninstall.exe", 5),
        ], [Noise(100, 1), Noise(200, 2), Noise(300, 3), Words(50), "Apache"u8.ToArray(), Noise(100, 4)]);
        var component = new EngineComponent("tesseract", "1.0", "https://example.test/tesseract-setup.exe",
            Convert.ToHexString(SHA256.HashData(installer)), installer.Length, "nsis");
        var engines = Path.Combine(_root, "engines");
        var engineInstaller = new EngineInstaller(new HttpClient(new OneFile(component.Url, installer)), engines,
            new Dictionary<string, EngineComponent> { ["tesseract"] = component });
        var package = new EnginePackage("tesseract", ["tesseract"], 1, ["tesseract"]);
        var previous = Environment.GetEnvironmentVariable(SevenZip.HideVariable);
        Environment.SetEnvironmentVariable(SevenZip.HideVariable, "1");
        try
        {
            await engineInstaller.InstallAsync(package, null, TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SevenZip.HideVariable, previous);
        }

        var folder = Path.Combine(engines, "tesseract");
        // Trimmed as before: no training programs, manuals, uninstaller, language data or installer plug-ins.
        Assert.Equal(["doc/LICENSE", "libtesseract-5.dll", "tesseract.exe"],
            Contents(folder).Keys.Where(k => !k.StartsWith('.')));
        Assert.Equal(Noise(300, 3), File.ReadAllBytes(Path.Combine(folder, "libtesseract-5.dll")));
        Assert.True(engineInstaller.IsInstalled(package));
    }

    // ───────────────────────── Tesseract's real installer ─────────────────────────

    /// <summary>
    /// What 7-Zip 26.05 unpacks from tesseract-ocr-w64-setup-5.4.0.20240606.exe ("7z x -aou"): 139 files, 249,571,062
    /// bytes, and the SHA-256 of their sorted "path␉SHA-256␊" lines ('/' separators), so the comparison also runs
    /// without 7-Zip.
    /// </summary>
    private const int SevenZipFileCount = 139;
    private const long SevenZipBytes = 249_571_062;
    private const string SevenZipManifest = "D0F1B92B3DE260D367B8989FBDF7BA3E2F0D08C62CB8D62E7496FBCEAF38EEE3";

    private static string? TesseractInstaller()
    {
        var component = EngineDownloads.ComponentsForRuntime("win-x64")["tesseract"];
        var name = Path.GetFileName(new Uri(component.Url).LocalPath);
        if (Environment.GetEnvironmentVariable("FILEE_TESSERACT_INSTALLER") is { Length: > 0 } given)
            return File.Exists(given) ? given : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                continue;
            var cached = Path.Combine(dir.FullName, "build", ".cache", name);
            return File.Exists(cached) ? cached : null;
        }
        return null;
    }

    private static string Manifest(SortedDictionary<string, string> hashes) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(hashes.Select(h => $"{h.Key}\t{h.Value}\n")))));

    private static SortedDictionary<string, string> Hashes(string folder) =>
        new(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(folder, f).Replace('\\', '/'),
            f =>
            {
                using var stream = File.OpenRead(f);
                return Convert.ToHexString(SHA256.HashData(stream));
            }), StringComparer.Ordinal);

    [Fact]
    public async Task Tesseract_installer_unpacks_exactly_as_7zip_unpacks_it()
    {
        var installer = TesseractInstaller();
        Assert.SkipWhen(installer is null, "Tesseract's installer is not on this PC (pwsh build/fetch-engines.ps1 -Only tesseract, or FILEE_TESSERACT_INSTALLER)");
        await using (var stream = File.OpenRead(installer))
            Assert.Equal(EngineDownloads.ComponentsForRuntime("win-x64")["tesseract"].Sha256, Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken)));

        var ours = NewFolder();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var listing = NsisReader.Extract(installer, ours, NsisScope.Everything, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"Filee: {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal((NsisCompression.Lzma, true, true), (listing.Compression, listing.Solid, listing.Unicode));
        var hashes = Hashes(ours);
        Assert.Equal(SevenZipFileCount, hashes.Count);
        Assert.Equal(SevenZipBytes, Directory.EnumerateFiles(ours, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length));
        Assert.Equal(SevenZipManifest, Manifest(hashes));

        if (SevenZip.Locate() is { } sevenZip)
        {
            var theirs = NewFolder();
            clock.Restart();
            await sevenZip.ExtractAsync(installer, theirs, [], _root, TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine($"7-Zip: {clock.Elapsed.TotalSeconds:0.0} s");
            Assert.Equal(Hashes(theirs), hashes);
            // Both set the times the script names (plug-ins and the uninstaller have none).
            foreach (var file in listing.Files.Where(f => f.Modified is not null))
            {
                var path = file.Path.Replace('\\', '/');
                Assert.Equal(file.Modified, File.GetLastWriteTimeUtc(Path.Combine(theirs, path)));
                Assert.Equal(file.Modified, File.GetLastWriteTimeUtc(Path.Combine(ours, path)));
            }
        }

        // What the engine install unpacks: the same minus the installer's plug-ins.
        var installDir = NewFolder();
        NsisReader.Extract(installer, installDir, NsisScope.InstallDir, TestContext.Current.CancellationToken);
        Assert.Equal(hashes.Where(h => !h.Key.StartsWith("$PLUGINSDIR/", StringComparison.Ordinal)), Hashes(installDir));
    }
}

[Trait("Category", "Fuzz")]
public class NsisFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>
    /// Mutated installers of every compression, solid or not, NSIS 2 and 3 strings, without the stub and the CRC so the
    /// changes reach the header and the decoders: every one unpacks or is refused with InvalidDataException, in time.
    /// </summary>
    [Fact]
    public void Nsis_reader_unpacks_or_refuses_every_mutated_installer()
    {
        List<Op> script =
        [
            new ExtractFile("$PLUGINSDIR\\a.dll", 0),
            new SetOutPath("$INSTDIR"),
            new ExtractFile("a.txt", 1, DateTime.UnixEpoch),
            new SetOutPath("$OUTDIR\\sub"),
            new ExtractFile("b.bin", 2),
            new ExtractFile("a.txt", 1),
            new WriteUninstaller("$INSTDIR\\u.exe", 3),
        ];
        List<byte[]> data = [Words(500), Words(2_000), Noise(800, 5), Noise(100, 6)];
        List<byte[]> seeds =
        [
            Build(script, data, new Options { Stub = 0, Crc = false }),
            Build(script, data, new Options { Stub = 0, Crc = false, Solid = false, Compression = NsisCompression.Deflate, Strings = StringKind.Ansi2 }),
            Build(script, data, new Options { Stub = 0, Crc = false, Compression = NsisCompression.Bzip2, Strings = StringKind.Ansi3 }),
            Build(script, data, new Options { Stub = 0, Crc = false, Solid = false, Filter = 1, Stored = new HashSet<int> { 2 } }),
            Build(script, data, new Options { Stub = 0, Crc = false, Solid = false, Compression = NsisCompression.Stored }),
        ];
        new FuzzHarness(fx, 80, 20261114).Files("nsis", "exe", seeds, FuzzHarness.Bytes,
            (input, media) => NsisReader.Extract(input, media, NsisScope.Everything, CancellationToken.None));
    }
}
