using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Engines.Archives;

namespace Filee.Engines.Tests;

public sealed class FolderArchives1345Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-folder-archives-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Folder(string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllText(Path.Combine(folder, "日本語.txt"), name);
        return folder;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zip_creates_one_archive_per_folder_or_one_for_the_whole_selection(bool combine)
    {
        Assert.SkipUnless(SevenZip.Locate() is not null, "The archive converter uses bundled 7-Zip.");
        string[] folders = [Folder("first"), Folder("second")];
        var converter = new ArchiveConverter();
        await using var queue = new JobQueue(new ConverterCatalog([converter]), combiner: converter);
        var job = new ConversionJob(folders, new Preset
        {
            TargetFormat = "zip",
            Archive = { CombineIntoOne = combine },
            Output = { Location = OutputLocation.SameFolder }
        }, "ZIP", folderSources: folders.ToHashSet());
        await queue.RunAsync(job);
        Assert.True(job.State == JobState.Completed, string.Join("\n", job.Files.Select(file => file.ErrorDetail)));
        Assert.Equal(combine ? 1 : 2, job.Outputs.Count());
    }

    [Fact]
    public async Task Folder_zip_keeps_the_mark_of_an_included_downloaded_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Mark of the Web is Windows-only.");
        Assert.SkipUnless(SevenZip.Locate() is not null, "The archive converter uses bundled 7-Zip.");
        var folder = Folder("downloaded");
        var source = Path.Combine(folder, "日本語.txt");
        MarkOfTheWeb.TryWrite(source, MarkOfTheWeb.Internet());
        Assert.SkipUnless(MarkOfTheWeb.Read(source) is not null, "Filesystem must support alternate data streams.");
        var converter = new ArchiveConverter();
        await using var queue = new JobQueue(new ConverterCatalog([converter]), combiner: converter);
        var job = new ConversionJob([folder], new Preset { TargetFormat = "zip", Output = { Location = OutputLocation.SameFolder } },
            "ZIP", folderSources: new HashSet<string> { folder });
        await queue.RunAsync(job);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(MarkOfTheWeb.Internet(), MarkOfTheWeb.Read(Assert.Single(job.Outputs)));
    }

    [Theory]
    [InlineData("zip", false)]
    [InlineData("7z", false)]
    [InlineData("tar", false)]
    [InlineData("tgz", false)]
    [InlineData("tbz2", false)]
    [InlineData("txz", false)]
    [InlineData("zip", true)]
    public async Task Folder_archives_round_trip_names_contents_and_empty_folders(string format, bool combine)
    {
        var sevenZip = SevenZip.Locate();
        Assert.SkipUnless(sevenZip is not null, "Bundled 7-Zip is required for the independent archive check.");
        var one = Folder("photos.jpg");
        var two = Folder("second");
        string[] sources = combine ? [one, two] : [one];
        var converter = new ArchiveConverter();
        var catalog = new ConverterCatalog([converter]);
        Assert.True(converter.GetStatus().IsAvailable);
        await using var queue = new JobQueue(catalog, combiner: converter);
        var preset = new Preset
        {
            TargetFormat = format,
            Archive = { CombineIntoOne = combine },
            Output = { Location = OutputLocation.SameFolder }
        };
        var job = new ConversionJob(sources, preset, "archive", folderSources: sources.ToHashSet());
        await queue.RunAsync(job);
        Assert.True(job.State == JobState.Completed, string.Join("\n", job.Files.Select(f => f.ErrorDetail)));
        var output = Assert.Single(job.Outputs);
        if (!combine) Assert.EndsWith("photos.jpg." + FormatRegistry.Get(format).PrimaryExtension, output);
        var extracted = await new ArchiveExtractor(sevenZip).ExtractAsync(output, format, _root, TestContext.Current.CancellationToken);
        foreach (var folder in sources)
        {
            var name = Path.GetFileName(folder);
            Assert.Equal(name, File.ReadAllText(Path.Combine(extracted, name, "日本語.txt")));
            Assert.True(Directory.Exists(Path.Combine(extracted, name, "empty")));
            Assert.Equal(name, File.ReadAllText(Path.Combine(folder, "日本語.txt")));
        }
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    [InlineData("tar")]
    [InlineData("tgz")]
    [InlineData("tbz2")]
    [InlineData("txz")]
    public async Task Nested_links_never_pack_files_outside_the_selected_folder(string format)
    {
        var sevenZip = SevenZip.Locate();
        Assert.SkipUnless(sevenZip is not null, "Bundled 7-Zip is required to inspect all archive formats.");
        var folder = Folder("selected");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "private.txt"), "do not archive");
        var link = Path.Combine(folder, "link");
        if (OperatingSystem.IsWindows())
        {
            using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, outside])
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            junction.WaitForExit();
            Assert.Equal(0, junction.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            var archive = Path.Combine(_root, "output." + format);
            var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
            await new ArchivePacker(sevenZip).PackAsync([new PackItem(folder, "selected")], archive, format,
                ArchiveLevel.Fast, work, null, TestContext.Current.CancellationToken);
            var extracted = await new ArchiveExtractor(sevenZip).ExtractAsync(archive, format, work, TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(Path.Combine(extracted, "selected", "link")));
            Assert.DoesNotContain(Directory.GetFiles(extracted, "*", SearchOption.AllDirectories), file => Path.GetFileName(file) == "private.txt");
            Assert.True(File.Exists(Path.Combine(extracted, "selected", "日本語.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Cancellation_preserves_an_existing_destination()
    {
        var folder = Folder("selected");
        var output = Path.Combine(_root, "output.zip");
        File.WriteAllText(output, "previous archive");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArchivePacker(null).PackAsync(
            [new PackItem(folder, "selected")], output, "zip", ArchiveLevel.Normal, _root, null, cancellation.Token));
        Assert.Equal("previous archive", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
