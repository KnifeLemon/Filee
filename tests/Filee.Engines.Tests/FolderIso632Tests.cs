// Generated folders exercise the real ISO writer, queue routing, Joliet limits and safe cancellation.

using DiscUtils.Iso9660;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Engines.Archives;

namespace Filee.Engines.Tests;

public sealed class FolderIso632Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-iso632-").FullName;
    public void Dispose() => Directory.Delete(_root, true);
    private string Folder(string name = "photos.jpg") => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private async Task<ConversionJob> Run(string source, OutputRule? rule = null)
    {
        await using var queue = new JobQueue(new ConverterCatalog([new FolderIsoConverter()]));
        var preset = new Preset { TargetFormat = "iso", Output = rule ?? new OutputRule { Location = OutputLocation.SameFolder } };
        var job = new ConversionJob([source], preset, "ISO", folderSources: new HashSet<string> { source });
        await queue.RunAsync(job);
        return job;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_drive_root_is_refused_before_output_allocation_or_enumeration(bool relativeSegment)
    {
        var root = Path.GetPathRoot(_root)!;
        var input = relativeSegment ? Path.Combine(root, ".") : root;
        var output = new UnexpectedAllocation();
        var step = new ConversionStep(input, FormatRegistry.Folder, "iso", new Preset { TargetFormat = "iso" }, output, _root);

        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() => new FolderIsoConverter().ConvertAsync(step, null, TestContext.Current.CancellationToken));

        Assert.Equal("error.iso_root", error.ErrorKey);
        Assert.Equal(root, error.Detail);
        Assert.False(output.Called);
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*"));
    }

    [Theory]
    [InlineData(OutputLocation.SameFolder, false)]
    [InlineData(OutputLocation.CustomFolder, false)]
    [InlineData(OutputLocation.SameFolder, true)]
    [InlineData(OutputLocation.CustomFolder, true)]
    public async Task A_drive_root_fails_clearly_through_the_queue_without_writing_output(OutputLocation location, bool zipResult)
    {
        var root = Path.GetPathRoot(_root)!;
        var destination = Path.Combine(_root, "output");
        var job = await Run(root, new OutputRule { Location = location, CustomFolder = destination, ZipResult = zipResult });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(FileState.Failed, Assert.Single(job.Files).State);
        Assert.Equal("error.iso_root", job.Files[0].ErrorKey);
        Assert.Equal(root, job.Files[0].ErrorDetail);
        Assert.Empty(job.Outputs);
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*"));
    }

    [Fact]
    public async Task Images_always_include_joliet_records_and_preserve_filenames()
    {
        var folder = Folder();
        Directory.CreateDirectory(Path.Combine(folder, "EMPTY"));
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "standard test");
        var job = await Run(folder);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        var bytes = File.ReadAllBytes(Assert.Single(job.Outputs));
        // Joliet supplementary volume descriptors use %/@, %/C or %/E escape sequences.
        var descriptors = Enumerable.Range(16, bytes.Length / 2048 - 16).Select(i => i * 2048).TakeWhile(i => bytes[i] != 255);
        Assert.Contains(descriptors, i => bytes[i] == 2 && bytes[i + 88] == '%' && bytes[i + 89] == '/');
        using var stream = File.OpenRead(job.Outputs.First());
        using var reader = new CDReader(stream, true);
        Assert.True(reader.DirectoryExists("EMPTY"));
        var file = Assert.Single(reader.GetFiles("\\"));
        Assert.Contains("readme.txt", file, StringComparison.Ordinal);
        using var text = new StreamReader(reader.OpenFile(file, FileMode.Open));
        Assert.Equal("standard test", text.ReadToEnd());
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
        var name = new string('한', 58) + ".txt";
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

    [Theory]
    [InlineData("root")]
    [InlineData("nested")]
    [InlineData("none")]
    [InlineData("excluded-output")]
    public async Task Iso_is_marked_only_when_an_included_file_has_the_mark_of_the_web(string markedEntry)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Mark of the Web is a Windows (NTFS) stream.");
        var probe = Path.Combine(_root, "probe.txt");
        File.WriteAllText(probe, "probe");
        Assert.SkipUnless(MarkOfTheWeb.TryWrite(probe, MarkOfTheWeb.Internet()), "The temp drive holds no NTFS streams.");
        var folder = Folder();
        var rootFile = Path.Combine(folder, "plain.txt");
        var nested = Directory.CreateDirectory(Path.Combine(folder, "nested", "downloads")).FullName;
        var nestedFile = Path.Combine(nested, "setup.exe");
        File.WriteAllText(rootFile, "local contents");
        File.WriteAllText(nestedFile, "downloaded contents");
        var output = Path.Combine(folder, Path.GetFileName(folder) + ".iso");
        File.WriteAllText(output, "previous image");
        var source = markedEntry switch { "root" => rootFile, "nested" => nestedFile, "excluded-output" => output, _ => null };
        if (source is not null) Assert.True(MarkOfTheWeb.TryWrite(source, MarkOfTheWeb.Internet()));

        var job = await Run(folder, new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = folder, Conflict = ConflictPolicy.Overwrite });

        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        Assert.Equal(output, Assert.Single(job.Outputs));
        Assert.Equal(markedEntry is "root" or "nested" ? MarkOfTheWeb.Internet() : null, MarkOfTheWeb.Read(output));
        using var stream = File.OpenRead(output);
        using var iso = new CDReader(stream, true);
        Assert.True(iso.FileExists("nested\\downloads\\setup.exe"));
        Assert.False(iso.FileExists(Path.GetFileName(output)));
        if (source is not null && source != output) Assert.Equal(MarkOfTheWeb.Internet(), MarkOfTheWeb.Read(source));
    }

    [Theory]
    [InlineData("1800-06-15T12:34:56Z")]
    [InlineData("1900-01-01T00:00:00Z")]
    [InlineData("2020-06-15T12:34:56Z")]
    [InlineData("2155-12-31T23:59:59Z")]
    [InlineData("2156-01-01T00:00:00Z")]
    [InlineData("2200-06-15T12:34:56Z")]
    public async Task File_and_directory_dates_are_clamped_to_the_iso_range(string sourceDate)
    {
        var modified = DateTime.Parse(sourceDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var folder = Folder();
        var source = Path.Combine(folder, "file.txt");
        var directory = Path.Combine(folder, "empty");
        File.WriteAllText(source, "date boundary");
        Directory.CreateDirectory(directory);
        File.SetLastWriteTimeUtc(source, modified);
        Directory.SetLastWriteTimeUtc(directory, modified);
        // Some filesystems clamp source dates themselves (e.g. to the 32-bit Unix-time minimum).
        var actualFile = File.GetLastWriteTimeUtc(source);
        var actualDirectory = Directory.GetLastWriteTimeUtc(directory);
        static DateTime Expected(DateTime actual) => new(Math.Clamp(actual.Ticks,
            new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            new DateTime(2155, 12, 31, 23, 59, 59, DateTimeKind.Utc).Ticks), DateTimeKind.Utc);

        var job = await Run(folder);

        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        using var stream = File.OpenRead(Assert.Single(job.Outputs));
        using var iso = new CDReader(stream, true);
        Assert.Equal(Expected(actualFile), iso.GetLastWriteTimeUtc("file.txt"));
        Assert.Equal(Expected(actualDirectory), iso.GetLastWriteTimeUtc("empty"));
        using var reader = new StreamReader(iso.OpenFile("file.txt", FileMode.Open));
        Assert.Equal("date boundary", reader.ReadToEnd());
        Assert.Equal(actualFile, File.GetLastWriteTimeUtc(source));
        Assert.Equal(actualDirectory, Directory.GetLastWriteTimeUtc(directory));
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
        Assert.Equal("error.iso_name", job.Files[0].ErrorKey);
        Assert.Equal(Path.GetRelativePath(folder, nested).Replace(Path.DirectorySeparatorChar, '\\'), job.Files[0].ErrorDetail);
        Assert.False(File.Exists(folder + ".iso"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linked_entries_are_rejected_without_following_them(bool linkedSource)
    {
        var folder = Folder();
        var outside = Folder("outside");
        File.WriteAllText(Path.Combine(outside, "private.txt"), "keep");
        var link = Path.Combine(folder, "link");
        LinkFolder(link, outside);
        try
        {
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            var input = linkedSource ? link : folder;
            var job = await Run(input);
            Assert.Equal(JobState.Failed, job.State);
            Assert.Equal("error.iso_link", job.Files[0].ErrorKey);
            Assert.Contains("link", job.Files[0].ErrorDetail);
            Assert.False(File.Exists(input + ".iso"));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "private.txt")));
        }
        finally { Directory.Delete(link); }
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
    [InlineData("63")]
    [InlineData("64")]
    [InlineData("long")]
    [InlineData("semicolon;1.txt")]
    [InlineData("emoji-😀.txt")]
    public async Task Unrepresentable_names_preserve_an_existing_destination(string name)
    {
        var folder = Folder();
        var actualName = name == "long" ? new string('a', 65) : int.TryParse(name, out var length) ? new string('a', length) : name;
        File.WriteAllText(Path.Combine(folder, actualName), "source");
        File.WriteAllText(folder + ".iso", "previous image");
        var job = await Run(folder, new OutputRule { Location = OutputLocation.SameFolder, Conflict = ConflictPolicy.Overwrite });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.iso_name", job.Files[0].ErrorKey);
        Assert.Equal(actualName, job.Files[0].ErrorDetail);
        Assert.Equal("previous image", File.ReadAllText(folder + ".iso"));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Names_at_the_62_character_boundary_are_preserved(bool withExtension)
    {
        var folder = Folder();
        var name = new string('a', withExtension ? 58 : 62) + (withExtension ? ".txt" : "");
        var directory = new string('d', 62);
        File.WriteAllText(Path.Combine(folder, name), "boundary");
        Directory.CreateDirectory(Path.Combine(folder, directory));
        var job = await Run(folder);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        using var stream = File.OpenRead(Assert.Single(job.Outputs));
        using var iso = new CDReader(stream, true);
        Assert.True(iso.DirectoryExists(directory));
        using var reader = new StreamReader(iso.OpenFile(name, FileMode.Open));
        Assert.Equal("boundary", reader.ReadToEnd());
    }

    [Theory]
    [InlineData("bad*name")]
    [InlineData("bad:name")]
    [InlineData("bad?name")]
    public async Task Unix_only_forbidden_names_do_not_replace_an_existing_image(string name)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows already refuses these source filenames.");
        var folder = Folder();
        File.WriteAllText(Path.Combine(folder, name), "source");
        File.WriteAllText(folder + ".iso", "previous image");
        var job = await Run(folder, new OutputRule { Location = OutputLocation.SameFolder, Conflict = ConflictPolicy.Overwrite });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.iso_name", job.Files[0].ErrorKey);
        Assert.Equal(name, job.Files[0].ErrorDetail);
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
        Assert.Equal("error.iso_file_size", job.Files[0].ErrorKey);
        Assert.Equal("large.bin", job.Files[0].ErrorDetail);
        Assert.False(File.Exists(folder + ".iso"));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 1024 * 1024)]
    [InlineData(false, 1024 * 1024)]
    public async Task A_source_shrinking_during_writing_fails_without_publishing_an_incomplete_image(bool existingDestination, int remainingBytes)
    {
        var folder = Folder();
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[2 * 1024 * 1024]);
        var changing = Path.Combine(folder, "z.bin");
        File.WriteAllBytes(changing, new byte[2 * 1024 * 1024]);
        var output = folder + ".iso";
        if (existingDestination) File.WriteAllText(output, "previous image");
        var step = new ConversionStep(folder, FormatRegistry.Folder, "iso", new Preset { TargetFormat = "iso" }, new FixedOutput(output), _root);
        var progress = new ShrinkProgress(changing, remainingBytes);

        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() => new FolderIsoConverter().ConvertAsync(step, progress, TestContext.Current.CancellationToken));

        Assert.Equal("error.iso_changed", error.ErrorKey);
        Assert.True(progress.Shrank);
        Assert.Equal(remainingBytes, new FileInfo(changing).Length);
        Assert.DoesNotContain(1d, progress.Values);
        if (existingDestination) Assert.Equal("previous image", File.ReadAllText(output));
        else Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*"));
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

    private static void LinkFolder(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("This filesystem cannot create symbolic links.");
            }
            return;
        }
        using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        junction.WaitForExit();
        Assert.Equal(0, junction.ExitCode);
    }

    private sealed class FixedOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
    private sealed class UnexpectedAllocation : IOutputAllocator
    {
        public bool Called { get; private set; }
        public string? Allocate(string extension, string? suffix = null)
        {
            Called = true;
            throw new InvalidOperationException("A drive root must be refused before allocating output.");
        }
    }
    private sealed class CancelProgress(CancellationTokenSource cancel) : IProgress<double>
    {
        public void Report(double value) { if (value > 0) cancel.Cancel(); }
    }
    private sealed class ShrinkProgress(string path, int remainingBytes) : IProgress<double>
    {
        public bool Shrank { get; private set; }
        public List<double> Values { get; } = [];
        public void Report(double value)
        {
            Values.Add(value);
            if (value <= 0 || Shrank) return;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            file.SetLength(remainingBytes);
            Shrank = true;
        }
    }
}
