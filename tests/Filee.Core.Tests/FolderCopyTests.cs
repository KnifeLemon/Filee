using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Watching;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public class FolderCopyTests
{
    private static Preset Copy(string folder) => new()
    {
        Kind = PresetKind.CopyToFolder,
        TargetFormat = Preset.CopyTarget,
        Output = new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = folder }
    };

    private static async Task<ConversionJob> Run(IReadOnlyList<string> sources, Preset preset, ISourceFetcher? fetcher = null)
    {
        await using var queue = new JobQueue(new ConverterCatalog([]));
        var job = new ConversionJob(sources, preset, "Copy", fetcher);
        await queue.RunAsync(job);
        return job;
    }

    [Fact]
    public async Task Local_file_is_copied_byte_for_byte_without_touching_its_original()
    {
        using var source = new TempDir(); using var target = new TempDir();
        var file = source.File("unregistered.extension");
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(file, bytes);
        var stamp = File.GetLastWriteTimeUtc(file);
        var job = await Run([file], Copy(target.Path));
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(job.Outputs)));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task Browser_download_bytes_are_preserved_and_the_work_copy_is_cleaned_up()
    {
        using var target = new TempDir(); using var work = new TempDir();
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];
        using var downloader = new WebDownloader(new Response(bytes));
        using var drop = new WebDrop([WebDropItem.FromAddress("picture.png", new Uri("https://example.test/picture.png"))], work.Path);
        var fetcher = drop.CreateFetcher(new WebDropOptions(target.Path, true), downloader);
        var job = await Run(drop.Placeholders, Copy(target.Path), fetcher);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(job.Outputs)));
        Assert.False(Directory.Exists(drop.WorkFolder));
        Assert.Single(Directory.GetFiles(target.Path));
    }

    private sealed class Response(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    [Fact]
    public async Task Clash_is_numbered_even_when_the_preset_or_save_dialog_requests_overwrite()
    {
        using var source = new TempDir(); using var target = new TempDir();
        var file = source.File("a.txt"); File.WriteAllText(file, "new");
        var existing = target.File("a.txt"); File.WriteAllText(existing, "keep");
        var preset = AskedLocation.ForFile(Copy(target.Path), existing);
        preset.Output.Conflict = ConflictPolicy.Overwrite;
        var job = await Run([file], preset);
        Assert.Equal(Path.Combine(target.Path, "a (2).txt"), Assert.Single(job.Outputs));
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.Equal("new", File.ReadAllText(job.Outputs.Single()));
    }

    [Fact]
    public async Task Missing_destination_fails_with_a_message_and_is_never_created()
    {
        using var source = new TempDir();
        var missing = Path.Combine(source.Path, "unplugged", "target");
        var job = await Run([source.File("a.txt")], Copy(missing));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("destination folder", Assert.Single(job.Files).ErrorDetail);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public async Task Same_folder_is_a_no_op()
    {
        using var source = new TempDir(); var file = source.File("a.txt");
        var job = await Run([file], Copy(source.Path));
        Assert.Equal(FileState.Skipped, Assert.Single(job.Files).State);
        Assert.Empty(job.Outputs);
        Assert.Equal([file], Directory.GetFiles(source.Path));
    }

    [Fact]
    public async Task Several_files_and_folders_keep_their_names_structure_and_originals()
    {
        using var source = new TempDir(); using var target = new TempDir();
        var folder = Path.Combine(source.Path, "icons.v1");
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllText(Path.Combine(folder, "a.svg"), "svg bytes");
        var file = source.File("photo.xyz");
        var job = await Run([folder, file], Copy(target.Path));
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, job.Outputs.Count());
        Assert.True(Directory.Exists(Path.Combine(target.Path, "icons.v1", "empty")));
        Assert.Equal("svg bytes", File.ReadAllText(Path.Combine(target.Path, "icons.v1", "a.svg")));
        Assert.True(File.Exists(file)); Assert.True(Directory.Exists(folder));
        var again = await Run([folder], Copy(target.Path));
        Assert.Equal(Path.Combine(target.Path, "icons.v1 (2)"), Assert.Single(again.Outputs));
    }

    [Fact]
    public async Task Destination_inside_the_source_folder_fails_without_recursing()
    {
        using var source = new TempDir();
        var child = Path.Combine(source.Path, "child"); Directory.CreateDirectory(child);
        var job = await Run([source.Path], Copy(child));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("itself", Assert.Single(job.Files).ErrorDetail);
        Assert.Empty(Directory.GetFileSystemEntries(child));
    }

    [Fact]
    public async Task Published_files_in_a_watched_folder_are_eligible_but_staging_files_are_not()
    {
        using var source = new TempDir(); using var target = new TempDir();
        var job = await Run([source.File("a.png")], Copy(target.Path));
        var rule = new WatchRule { Folder = target.Path, IncludeSubfolders = true };
        Assert.False(FolderWatcher.ShouldIgnore(Assert.Single(job.Outputs), rule));
        Assert.True(FolderWatcher.ShouldIgnore(Path.Combine(target.Path, ".filee-copy-test.tmp", "a.png"), rule));
    }

    [Fact]
    public async Task Cancelled_copy_leaves_no_partial_output()
    {
        using var source = new TempDir(); using var target = new TempDir();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FolderCopy.CopyAsync(source.File("a.txt"), Copy(target.Path).Output, cancel.Token));
        Assert.Empty(Directory.GetFileSystemEntries(target.Path));
    }
}
