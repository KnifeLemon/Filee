// BIN/CUE → ISO conversion, chained routes (cue → iso → folder / zip), CUE sheet parsing and refusals.
// Inputs are real ISOs built in-process with FolderIsoConverter and wrapped into each CD/DVD raw sector layout.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class BinCueTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly byte[] SyncPattern = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Preset Extract => BuiltInData.CreatePresets().Single(p => p.Id == "extract");

    private void SkipWithout7Zip()
    {
        var engine = fx.Converters.Single(c => c.Id == "archive");
        Assert.SkipUnless(fx.Catalog.StatusOf(engine).IsAvailable, "7-Zip not found (pwsh build/fetch-engines.ps1 -Only 7zip)");
    }

    // ───────── Routes ─────────

    [Fact]
    public void Cue_routes_to_iso_directly_and_chains_through_iso_for_zip_and_extract()
    {
        Assert.DoesNotContain("cue", ArchiveExtractor.ReadableFormats);

        var planner = fx.Catalog.CreatePlanner(["archive"]);

        var toIso = planner.Plan("cue", "iso");
        Assert.NotNull(toIso);
        var isoStep = Assert.Single(toIso.Steps);
        Assert.Equal(("cue", "iso"), (isoStep.From, isoStep.To));

        foreach (var target in new[] { "zip", FormatRegistry.Folder })
        {
            var route = planner.Plan("cue", target);
            Assert.NotNull(route);
            Assert.Equal(2, route.Steps.Count);
            Assert.Equal(("cue", "iso"), (route.Steps[0].From, route.Steps[0].To));
            Assert.Equal(("iso", target), (route.Steps[1].From, route.Steps[1].To));
        }
    }

    // ───────── Sector modes ─────────

    [Theory]
    [InlineData("MODE1/2048")]
    [InlineData("MODE1/2352")]
    [InlineData("MODE2/2352")]
    [InlineData("MODE2/2336")]
    public async Task Single_data_track_converts_byte_identically_to_iso(string mode)
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var (_, originalIso) = await MakeReferenceIsoAsync(dir);
        var binPath = Path.Combine(dir, "disc.bin");
        await File.WriteAllBytesAsync(binPath, WrapSectors(originalIso, mode), Ct);

        var cuePath = Path.Combine(dir, "disc.cue");
        await File.WriteAllTextAsync(cuePath, $"""
            FILE "disc.bin" BINARY
              TRACK 01 {mode}
                INDEX 01 00:00:00
            """, Ct);

        var job = await fx.ConvertAsync([cuePath], new Preset { TargetFormat = "iso" });

        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        var isoPath = Assert.Single(job.Outputs);
        Assert.Equal(originalIso, await File.ReadAllBytesAsync(isoPath, Ct));
    }

    // ───────── Chained routes ─────────

    [Fact]
    public async Task Cue_extracts_to_folder_and_repacks_to_zip_through_iso()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var (tree, originalIso) = await MakeReferenceIsoAsync(dir);
        var expected = Snapshot(tree);
        var binPath = Path.Combine(dir, "game.bin");
        await File.WriteAllBytesAsync(binPath, WrapSectors(originalIso, "MODE2/2352"), Ct);

        var cuePath = Path.Combine(dir, "game.cue");
        await File.WriteAllTextAsync(cuePath, """
            FILE "game.bin" BINARY
              TRACK 01 MODE2/2352
                INDEX 01 00:00:00
            """, Ct);

        var extractJob = await fx.ConvertAsync([cuePath], Extract);
        Assert.True(extractJob.State == JobState.Completed, extractJob.Files[0].ErrorDetail);
        var extracted = Assert.Single(extractJob.Outputs);
        Assert.Equal(expected, Snapshot(extracted));

        var zipJob = await fx.ConvertAsync([cuePath], new Preset { TargetFormat = "zip" });
        Assert.True(zipJob.State == JobState.Completed, zipJob.Files[0].ErrorDetail);
        var zipPath = Assert.Single(zipJob.Outputs);
        var check = Path.Combine(dir, "zip-check");
        ZipFile.ExtractToDirectory(zipPath, check);
        Assert.Equal(expected, Snapshot(check));
    }

    // ───────── CUE syntax and BIN resolution ─────────

    [Fact]
    public async Task Nonzero_index_01_skips_pregap_sectors_and_ignores_index_00()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var (_, originalIso) = await MakeReferenceIsoAsync(dir);
        // 00:02:05 = 2 * 75 + 5 = 155 leading sectors before INDEX 01.
        var leadingSectors = 155;
        var binBytes = WrapSectors(originalIso, "MODE1/2352", pregapSectors: leadingSectors);
        await File.WriteAllBytesAsync(Path.Combine(dir, "offset.bin"), binBytes, Ct);

        var cuePath = Path.Combine(dir, "offset.cue");
        await File.WriteAllTextAsync(cuePath, """
            REM COMMENT LINE
            CATALOG 0123456789012
            TITLE "Sample Disc"
            PERFORMER "Filee"
            FILE "offset.bin" BINARY
              TRACK 01 MODE1/2352
                FLAGS DCP
                PREGAP 00:02:00
                INDEX 00 00:00:00
                INDEX 01 00:02:05
                POSTGAP 00:00:00
            """, Ct);

        var job = await fx.ConvertAsync([cuePath], new Preset { TargetFormat = "iso" });

        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        Assert.Equal(originalIso, await File.ReadAllBytesAsync(Assert.Single(job.Outputs), Ct));
    }

    [Fact]
    public async Task Unquoted_name_lowercase_keywords_utf8_bom_and_stale_absolute_paths_all_work()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var (_, originalIso) = await MakeReferenceIsoAsync(dir);
        var wrapped = WrapSectors(originalIso, "MODE1/2352");

        // Unquoted file name, lowercase keywords and UTF-8 BOM.
        await File.WriteAllBytesAsync(Path.Combine(dir, "plain.bin"), wrapped, Ct);
        var unquotedCue = Path.Combine(dir, "plain.cue");
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(unquotedCue, "rem lowercase with bom\nfile plain.bin binary\n  track 01 mode1/2352\n    index 01 00:00:00\n", utf8WithBom, Ct);

        var job1 = await fx.ConvertAsync([unquotedCue], new Preset { TargetFormat = "iso" });
        Assert.True(job1.State == JobState.Completed, job1.Files[0].ErrorDetail);
        Assert.Equal(originalIso, await File.ReadAllBytesAsync(Assert.Single(job1.Outputs), Ct));

        // Quoted name with spaces and a stale Windows absolute path whose file sits next to the cue.
        await File.WriteAllBytesAsync(Path.Combine(dir, "my game disc.bin"), wrapped, Ct);
        var staleCue = Path.Combine(dir, "stale.cue");
        await File.WriteAllTextAsync(staleCue, """
            FILE "Z:\DoesNotExist\Old Folder\my game disc.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            """, Ct);

        var job2 = await fx.ConvertAsync([staleCue], new Preset { TargetFormat = "iso" });
        Assert.True(job2.State == JobState.Completed, job2.Files[0].ErrorDetail);
        Assert.Equal(originalIso, await File.ReadAllBytesAsync(Assert.Single(job2.Outputs), Ct));
    }

    // ───────── Where the BIN is looked for ─────────

    [Fact]
    public void A_relative_path_inside_the_cues_folder_is_followed()
    {
        var root = fx.NewFolder();
        Directory.CreateDirectory(Path.Combine(root, "disc"));
        var bin = Path.Combine(root, "disc", "game.bin");
        File.WriteAllBytes(bin, [0]);

        Assert.Equal(Path.GetFullPath(bin), CueSheet.ResolveBinPath(root, "disc/game.bin"));
        Assert.Equal(Path.GetFullPath(bin), CueSheet.ResolveBinPath(root, "disc" + Path.DirectorySeparatorChar + "game.bin"));
    }

    [Fact]
    public void An_absolute_path_outside_the_cues_folder_is_never_opened()
    {
        var root = fx.NewFolder();
        var cueFolder = Path.Combine(root, "cue");
        Directory.CreateDirectory(cueFolder);
        var outside = Path.Combine(root, "secret.bin");
        File.WriteAllBytes(outside, [1]);

        // Only the file name counts: missing in the cue's folder, the file elsewhere is not used.
        var error = Assert.Throws<ConversionRefusedException>(() => CueSheet.ResolveBinPath(cueFolder, Path.GetFullPath(outside)));
        Assert.Contains("missing", error.Detail, StringComparison.Ordinal);

        // With a file of that name in the cue's folder, that one is read.
        var local = Path.Combine(cueFolder, "secret.bin");
        File.WriteAllBytes(local, [2]);
        Assert.Equal(Path.GetFullPath(local), CueSheet.ResolveBinPath(cueFolder, Path.GetFullPath(outside)));
    }

    [Theory]
    [InlineData("../secret.bin")]
    [InlineData(@"..\secret.bin")]
    [InlineData("../../secret.bin")]
    [InlineData("sub/../../secret.bin")]
    public void A_path_that_climbs_out_of_the_cues_folder_is_refused(string fileName)
    {
        var root = fx.NewFolder();
        var cueFolder = Path.Combine(root, "a", "cue");
        Directory.CreateDirectory(cueFolder);
        File.WriteAllBytes(Path.Combine(root, "a", "secret.bin"), [1]);
        File.WriteAllBytes(Path.Combine(root, "secret.bin"), [1]);

        var error = Assert.Throws<ConversionRefusedException>(() => CueSheet.ResolveBinPath(cueFolder, fileName));
        Assert.Contains("missing", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\filee-test-host.invalid\share\disc.bin")]
    [InlineData("//filee-test-host.invalid/share/disc.bin")]
    [InlineData(@"\disc.bin")]
    [InlineData(@"C:disc.bin")]
    public void A_rooted_or_network_path_is_looked_for_by_its_name_in_the_cues_folder(string fileName)
    {
        var cueFolder = fx.NewFolder();
        var local = Path.Combine(cueFolder, "disc.bin");
        File.WriteAllBytes(local, [0]);

        Assert.Equal(Path.GetFullPath(local), CueSheet.ResolveBinPath(cueFolder, fileName));
    }

    [Fact]
    public void A_cue_that_isnt_utf8_is_read_in_the_system_code_page()
    {
        var encoding = CueSheet.LegacyEncoding();
        const string name = "Déjà vu.bin";
        var bytes = encoding.GetBytes($"FILE \"{name}\" BINARY\r\n");
        Assert.SkipUnless(encoding.GetString(bytes) == $"FILE \"{name}\" BINARY\r\n", "the system code page has no é or à");
        Assert.SkipWhen(Utf8IsValid(bytes), "the name's bytes are valid UTF-8 in this code page");

        Assert.Contains(name, CueSheet.DecodeCue(bytes), StringComparison.Ordinal);
        Assert.Equal("FILE \"Déjà vu.bin\" BINARY", CueSheet.DecodeCue(Encoding.UTF8.GetBytes("FILE \"Déjà vu.bin\" BINARY")));
    }

    private static bool Utf8IsValid(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // ───────── Refusals ─────────

    [Fact]
    public async Task Multi_track_disc_with_audio_is_refused_and_writes_nothing()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var (_, originalIso) = await MakeReferenceIsoAsync(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "ps1.bin"), WrapSectors(originalIso, "MODE2/2352"), Ct);

        var cuePath = Path.Combine(dir, "ps1.cue");
        await File.WriteAllTextAsync(cuePath, """
            FILE "ps1.bin" BINARY
              TRACK 01 MODE2/2352
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 01 01:00:00
            """, Ct);

        var job = await fx.ConvertAsync([cuePath], new Preset { TargetFormat = "iso" });

        AssertRefusedWithoutOutput(job, Path.Combine(dir, "ps1.iso"), "2 tracks");
    }

    [Fact]
    public async Task Missing_bin_is_refused_and_writes_nothing()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var cuePath = Path.Combine(dir, "missing.cue");
        await File.WriteAllTextAsync(cuePath, """
            FILE "not-here.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            """, Ct);

        var job = await fx.ConvertAsync([cuePath], new Preset { TargetFormat = "iso" });

        AssertRefusedWithoutOutput(job, Path.Combine(dir, "missing.iso"), "missing");
    }

    [Theory]
    [InlineData("MODE2/2352", 16)]
    [InlineData("MODE2/2336", 0)]
    public async Task Mode2_form2_sector_is_refused_and_deletes_output(string mode, int subheaderOffset)
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var (_, originalIso) = await MakeReferenceIsoAsync(dir);
        var bin = WrapSectors(originalIso, mode);
        var sectorSize = mode == "MODE2/2352" ? 2352 : 2336;
        // Mark sector 5 as Form 2 (submode byte bit 0x20).
        bin[5 * sectorSize + subheaderOffset + 2] = 0x20;
        bin[5 * sectorSize + subheaderOffset + 6] = 0x20;

        await File.WriteAllBytesAsync(Path.Combine(dir, "xa.bin"), bin, Ct);
        var cuePath = Path.Combine(dir, "xa.cue");
        await File.WriteAllTextAsync(cuePath, $"""
            FILE "xa.bin" BINARY
              TRACK 01 {mode}
                INDEX 01 00:00:00
            """, Ct);

        var job = await fx.ConvertAsync([cuePath], new Preset { TargetFormat = "iso" });

        AssertRefusedWithoutOutput(job, Path.Combine(dir, "xa.iso"), "Form 2");
    }

    [Fact]
    public async Task Non_iso_payload_is_refused_and_deletes_output()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var junk = new byte[20 * 2048];
        new Random(123).NextBytes(junk);
        await File.WriteAllBytesAsync(Path.Combine(dir, "junk.bin"), WrapSectors(junk, "MODE1/2352"), Ct);

        var cuePath = Path.Combine(dir, "junk.cue");
        await File.WriteAllTextAsync(cuePath, """
            FILE "junk.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            """, Ct);

        var job = await fx.ConvertAsync([cuePath], new Preset { TargetFormat = "iso" });

        AssertRefusedWithoutOutput(job, Path.Combine(dir, "junk.iso"), "ISO 9660");
    }

    [Fact]
    public async Task A_refused_conversion_leaves_an_image_it_would_overwrite_as_it_was()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var junk = new byte[20 * 2048];
        new Random(7).NextBytes(junk);
        await File.WriteAllBytesAsync(Path.Combine(dir, "disc.bin"), WrapSectors(junk, "MODE1/2048"), Ct);
        var cuePath = Path.Combine(dir, "disc.cue");
        await File.WriteAllTextAsync(cuePath, "FILE \"disc.bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n", Ct);
        var existing = Path.Combine(dir, "disc.iso");
        await File.WriteAllTextAsync(existing, "an image made earlier", Ct);

        var job = await fx.ConvertAsync([cuePath],
            new Preset { TargetFormat = "iso", Output = { Location = OutputLocation.SameFolder, Conflict = ConflictPolicy.Overwrite } });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("an image made earlier", await File.ReadAllTextAsync(existing, Ct));
        Assert.Empty(Directory.GetFiles(dir, "*.filee-tmp", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public async Task Truncated_bin_is_refused_and_writes_nothing()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var (_, originalIso) = await MakeReferenceIsoAsync(dir);
        var wrapped = WrapSectors(originalIso, "MODE1/2352");
        await File.WriteAllBytesAsync(Path.Combine(dir, "cut.bin"), wrapped[..^100], Ct);

        var cuePath = Path.Combine(dir, "cut.cue");
        await File.WriteAllTextAsync(cuePath, """
            FILE "cut.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            """, Ct);

        var job = await fx.ConvertAsync([cuePath], new Preset { TargetFormat = "iso" });

        AssertRefusedWithoutOutput(job, Path.Combine(dir, "cut.iso"), "whole number of sectors");
    }

    // ───────── Helpers ─────────

    private static void AssertRefusedWithoutOutput(ConversionJob job, string expectedOutputPath, string expectedReasonPart)
    {
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.unsupported_source", job.Files[0].ErrorKey);
        Assert.Contains(expectedReasonPart, job.Files[0].ErrorDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(job.Outputs);
        Assert.False(File.Exists(expectedOutputPath));
    }

    private static async Task<(string Tree, byte[] IsoBytes)> MakeReferenceIsoAsync(string dir)
    {
        var tree = MakeTree(dir, "source-tree");
        var isoPath = Path.Combine(dir, "reference.iso");
        var step = new ConversionStep(tree, FormatRegistry.Folder, "iso", new Preset { TargetFormat = "iso" },
            new FixedOutput(isoPath), dir);
        await new FolderIsoConverter().ConvertAsync(step, null, Ct);
        var bytes = await File.ReadAllBytesAsync(isoPath, Ct);
        File.Delete(isoPath);
        Assert.Equal(0, bytes.Length % 2048);
        return (tree, bytes);
    }

    private static byte[] WrapSectors(byte[] isoBytes, string mode, int pregapSectors = 0)
    {
        var (sectorSize, dataOffset, isMode2) = mode switch
        {
            "MODE1/2048" => (2048, 0, false),
            "MODE1/2352" => (2352, 16, false),
            "MODE2/2352" => (2352, 24, true),
            "MODE2/2336" => (2336, 8, true),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        var dataSectors = isoBytes.Length / 2048;
        var totalSectors = pregapSectors + dataSectors;
        var bin = new byte[totalSectors * sectorSize];

        for (var s = 0; s < totalSectors; s++)
        {
            var start = s * sectorSize;
            if (sectorSize == 2352)
            {
                SyncPattern.CopyTo(bin, start);
                bin[start + 15] = (byte)(isMode2 ? 2 : 1);
            }
            if (isMode2)
            {
                var subOffset = sectorSize == 2352 ? 16 : 0;
                // Submode byte 0x08 = Form 1 data sector (bit 0x20 clear), repeated in both 4-byte copies.
                bin[start + subOffset + 2] = 0x08;
                bin[start + subOffset + 6] = 0x08;
            }
            if (s >= pregapSectors)
                isoBytes.AsSpan((s - pregapSectors) * 2048, 2048).CopyTo(bin.AsSpan(start + dataOffset, 2048));
        }

        return bin;
    }

    private sealed class FixedOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
