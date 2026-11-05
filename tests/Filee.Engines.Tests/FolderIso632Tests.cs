// Generated folders exercise the real ISO writer, queue routing, Joliet limits and safe cancellation.

using DiscUtils.Iso9660;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Archives;

namespace Filee.Engines.Tests;

public sealed class FolderIso632Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-iso632-").FullName;
    public void Dispose() => Directory.Delete(_root, true);
    private string Folder(string name = "photos.jpg") => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private async Task<ConversionJob> Run(string source, OutputRule? rule = null, IsoStandard standard = IsoStandard.Iso9660Joliet)
    {
        await using var queue = new JobQueue(new ConverterCatalog([new FolderIsoConverter()]));
        var preset = new Preset { TargetFormat = "iso", Output = rule ?? new OutputRule { Location = OutputLocation.SameFolder } };
        preset.Iso.Standard = standard;
        var job = new ConversionJob([source], preset, "ISO", folderSources: new HashSet<string> { source });
        await queue.RunAsync(job);
        return job;
    }

    [Theory]
    [InlineData(IsoStandard.Iso9660, false)]
    [InlineData(IsoStandard.Iso9660Joliet, true)]
    public async Task Selected_standard_controls_joliet_records_and_filename_behavior(IsoStandard standard, bool joliet)
    {
        var folder = Folder();
        Directory.CreateDirectory(Path.Combine(folder, "EMPTY"));
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "standard test");
        var job = await Run(folder, standard: standard);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        var bytes = File.ReadAllBytes(Assert.Single(job.Outputs));
        // Joliet supplementary volume descriptors use %/@, %/C or %/E escape sequences.
        var descriptors = Enumerable.Range(16, bytes.Length / 2048 - 16).Select(i => i * 2048).TakeWhile(i => bytes[i] != 255);
        Assert.Equal(joliet, descriptors.Any(i => bytes[i] == 2 && bytes[i + 88] == '%' && bytes[i + 89] == '/'));
        using var stream = File.OpenRead(job.Outputs.First());
        using var reader = new CDReader(stream, joliet);
        Assert.True(reader.DirectoryExists("EMPTY"));
        var file = Assert.Single(reader.GetFiles("\\"));
        Assert.Contains(joliet ? "readme.txt" : "README.TXT", file, StringComparison.Ordinal);
        using var text = new StreamReader(reader.OpenFile(file, FileMode.Open));
        Assert.Equal("standard test", text.ReadToEnd());
    }

    [Fact]
    public async Task Plain_iso_rejects_names_that_collide_after_ascii_mapping_and_preserves_destination()
    {
        var folder = Folder();
        File.WriteAllText(Path.Combine(folder, "é.txt"), "one");
        File.WriteAllText(Path.Combine(folder, "è.txt"), "two");
        var output = folder + ".iso";
        File.WriteAllText(output, "existing destination");
        var job = await Run(folder, new OutputRule { Location = OutputLocation.SameFolder, Conflict = ConflictPolicy.Overwrite }, IsoStandard.Iso9660);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("maps multiple entries", job.Files[0].ErrorDetail);
        Assert.Equal("existing destination", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    [InlineData("tar")]
    [InlineData("tgz")]
    [InlineData("tbz2")]
    [InlineData("txz")]
    public async Task Every_folder_archive_action_round_trips_nested_unicode_content_and_empty_directories(string format)
    {
        var archive = new ArchiveConverter();
        Assert.SkipUnless(archive.CanCombineInto(format), "Bundled 7-Zip is required for this format.");
        var folder = Folder();
        Directory.CreateDirectory(Path.Combine(folder, "docs"));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllText(Path.Combine(folder, "docs", "한글.txt"), "folder archive contents");
        var preset = BuiltInData.FolderArchivePresets().Single(p => p.TargetFormat == format);
        preset.Output.Location = OutputLocation.SameFolder;
        var catalog = new ConverterCatalog([archive, new FolderIsoConverter()]);
        await using var queue = new JobQueue(catalog, combiner: archive);
        var job = new ConversionJob([folder], preset, preset.Name, folderSources: new HashSet<string> { folder });
        await queue.RunAsync(job);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        var output = Assert.Single(job.Outputs);
        Assert.Equal(folder + "." + FormatRegistry.Get(format).PrimaryExtension, output);
        var extract = new ConversionJob([output], new Preset
        {
            TargetFormat = FormatRegistry.Folder,
            Output = { Location = OutputLocation.CustomFolder, CustomFolder = Path.Combine(_root, "extracted") }
        }, "Extract");
        await queue.RunAsync(extract);
        Assert.True(extract.State == JobState.Completed, extract.Files[0].ErrorDetail);
        var extracted = Assert.Single(extract.Outputs);
        Assert.Equal("folder archive contents", File.ReadAllText(Assert.Single(Directory.GetFiles(extracted, "*.txt", SearchOption.AllDirectories))));
        Assert.Contains(Directory.GetDirectories(extracted, "*", SearchOption.AllDirectories), p => Path.GetFileName(p) == "empty");
        Assert.Equal("folder archive contents", File.ReadAllText(Path.Combine(folder, "docs", "한글.txt")));
    }

    [Fact]
    public async Task Queue_writes_contents_at_root_preserving_unicode_empty_directories_and_dotted_folder_name()
    {
        var folder = Folder();
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllText(Path.Combine(folder, "한글.txt"), "Unicode contents");
        var job = await Run(folder);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        Assert.Equal(folder + ".iso", Assert.Single(job.Outputs));
        using var stream = File.OpenRead(job.Outputs.First());
        using var iso = new CDReader(stream, true);
        Assert.True(iso.DirectoryExists("empty"));
        using var reader = new StreamReader(iso.OpenFile("한글.txt", FileMode.Open));
        Assert.Equal("Unicode contents", reader.ReadToEnd());
        Assert.Equal("Unicode contents", File.ReadAllText(Path.Combine(folder, "한글.txt")));
    }

    [Fact]
    public async Task A_maximum_length_unicode_name_and_timestamp_round_trip()
    {
        var folder = Folder();
        var name = new string('한', 60) + ".txt";
        var source = Path.Combine(folder, name);
        File.WriteAllText(source, "name boundary");
        var modified = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);
        var job = await Run(folder);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        using var stream = File.OpenRead(Assert.Single(job.Outputs));
        using var iso = new CDReader(stream, true);
        Assert.True(iso.FileExists(name));
        Assert.InRange(Math.Abs((iso.GetLastWriteTimeUtc(name) - modified).TotalSeconds), 0, 2);
    }

    [Fact]
    public async Task Generated_image_is_readable_by_the_independent_7zip_engine()
    {
        var reader = SevenZip.Locate();
        Assert.SkipUnless(reader is not null, "Bundled 7-Zip is required for the independent reader check.");
        var folder = Folder();
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllText(Path.Combine(folder, "한글.txt"), "independent contents");
        var job = await Run(folder);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        var extracted = Path.Combine(_root, "extracted");
        await reader.ExtractAsync(Assert.Single(job.Outputs), extracted, [], _root, TestContext.Current.CancellationToken);
        Assert.Equal("independent contents", File.ReadAllText(Path.Combine(extracted, "한글.txt")));
        Assert.True(Directory.Exists(Path.Combine(extracted, "empty")));
    }

    [Fact]
    public async Task Long_disc_paths_fail_clearly()
    {
        var folder = Folder();
        var nested = folder;
        for (var i = 0; i < 5; i++) nested = Directory.CreateDirectory(Path.Combine(nested, new string('a', 49))).FullName;
        var job = await Run(folder);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("240", job.Files[0].ErrorDetail);
        Assert.False(File.Exists(folder + ".iso"));
    }

    [Fact]
    public async Task Linked_entries_are_rejected_without_following_them()
    {
        var folder = Folder();
        var outside = Folder("outside");
        File.WriteAllText(Path.Combine(outside, "private.txt"), "keep");
        try { Directory.CreateSymbolicLink(Path.Combine(folder, "link"), outside); }
        catch (UnauthorizedAccessException) { Assert.Skip("Symbolic link creation is not permitted."); }
        var job = await Run(folder);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("symbolic links", job.Files[0].ErrorDetail);
        Assert.False(File.Exists(folder + ".iso"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "private.txt")));
    }

    [Fact]
    public async Task Empty_source_is_a_valid_image()
    {
        var job = await Run(Folder());
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        using var stream = File.OpenRead(Assert.Single(job.Outputs));
        using var iso = new CDReader(stream, true);
        Assert.Empty(iso.GetFiles("\\", "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("semicolon;1.txt")]
    [InlineData("emoji-😀.txt")]
    public async Task Unrepresentable_names_preserve_an_existing_destination(string name)
    {
        var folder = Folder();
        File.WriteAllText(Path.Combine(folder, name == "long" ? new string('a', 65) : name), "source");
        File.WriteAllText(folder + ".iso", "previous image");
        var job = await Run(folder, new OutputRule { Location = OutputLocation.SameFolder, Conflict = ConflictPolicy.Overwrite });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Joliet", job.Files[0].ErrorDetail);
        Assert.Equal("previous image", File.ReadAllText(folder + ".iso"));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*"));
    }

    [Fact]
    public async Task A_file_of_four_GiB_is_rejected_before_an_image_is_written()
    {
        var folder = Folder();
        using (var file = File.Create(Path.Combine(folder, "large.bin"))) file.SetLength(1L << 32);
        var job = await Run(folder);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("4 GiB", job.Files[0].ErrorDetail);
        Assert.False(File.Exists(folder + ".iso"));
    }

    [Fact]
    public async Task Cancellation_during_streaming_keeps_the_original_destination()
    {
        var folder = Folder();
        File.WriteAllBytes(Path.Combine(folder, "file.bin"), new byte[200000]);
        var output = folder + ".iso";
        File.WriteAllText(output, "keep");
        using var cancel = new CancellationTokenSource();
        var step = new ConversionStep(folder, FormatRegistry.Folder, "iso", new Preset { TargetFormat = "iso" }, new FixedOutput(output), _root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FolderIsoConverter().ConvertAsync(step,
            new CancelProgress(cancel), cancel.Token));
        Assert.Equal("keep", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*"));
    }

    [Fact]
    public async Task A_destination_inside_the_source_is_excluded()
    {
        var folder = Folder();
        File.WriteAllText(Path.Combine(folder, "input.txt"), "contents");
        File.WriteAllText(Path.Combine(folder, "photos.jpg.iso"), "old image");
        var job = await Run(folder, new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = folder, Conflict = ConflictPolicy.Overwrite });
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        using var stream = File.OpenRead(Assert.Single(job.Outputs));
        using var iso = new CDReader(stream, true);
        Assert.True(iso.FileExists("input.txt"));
        Assert.False(iso.FileExists("photos.jpg.iso"));
    }

    [Fact]
    public void Archive_to_iso_uses_a_direct_archive_route_without_enabling_unrelated_inputs()
    {
        var planner = new ConverterCatalog([new ArchiveConverter(), new FolderIsoConverter()]).CreatePlanner();
        Assert.Equal("archive", Assert.Single(planner.Plan("zip", "iso")!.Steps).Converter.Id);
        Assert.Null(planner.Plan("png", "iso"));
        Assert.NotNull(planner.Plan(FormatRegistry.Folder, "iso"));
    }

    private sealed class FixedOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
    private sealed class CancelProgress(CancellationTokenSource cancel) : IProgress<double>
    {
        public void Report(double value) { if (value > 0) cancel.Cancel(); }
    }
}
