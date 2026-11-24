// A file that came in through a shortcut is saved next to the shortcut, not next to the file it points to (#1706):
// from the shortcut being resolved to the converted file landing beside it.

using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class ShortcutFoldersTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    private static Preset Jpg(OutputLocation location = OutputLocation.SameFolder) =>
        new() { TargetFormat = "jpg", Output = { Location = location } };

    [Fact]
    public void A_resolved_shortcut_remembers_its_own_folder()
    {
        using var desktop = new TempDir();
        using var program = new TempDir();
        var target = Path.Combine(program.Path, "app.exe");
        var shortcut = Path.Combine(desktop.Path, "app.lnk");
        var folders = new ShortcutFolders();

        var files = IPlatformServices.ResolveShortcuts([shortcut], p => p == shortcut ? target : null, folders);

        Assert.Equal([target], files);
        Assert.Equal(desktop.Path, folders.FolderOf(target));
    }

    [Fact]
    public void A_file_brought_in_as_itself_is_saved_next_to_itself_again()
    {
        using var desktop = new TempDir();
        using var program = new TempDir();
        var target = Path.Combine(program.Path, "app.exe");
        var shortcut = Path.Combine(desktop.Path, "app.lnk");
        string? Target(string p) => p == shortcut ? target : null;
        var folders = new ShortcutFolders();

        IPlatformServices.ResolveShortcuts([shortcut], Target, folders);
        IPlatformServices.ResolveShortcuts([target], Target, folders);
        Assert.Null(folders.FolderOf(target));

        // Dropped together with a shortcut to it, the file itself wins.
        IPlatformServices.ResolveShortcuts([shortcut, target], Target, folders);
        Assert.Null(folders.FolderOf(target));
    }

    [Fact]
    public void Only_sources_that_came_through_a_shortcut_change_the_preset()
    {
        using var desktop = new TempDir();
        using var program = new TempDir();
        var target = program.File("app.exe");
        var other = program.File("other.png");
        var folders = new ShortcutFolders();
        var preset = Jpg();

        Assert.Same(preset, folders.Apply(preset, [target, other]));

        folders.Note(Path.Combine(desktop.Path, "app.lnk"), target);
        var applied = folders.Apply(preset, [target, other]);

        Assert.NotSame(preset, applied);
        Assert.Null(preset.Output.SourceFolders);
        Assert.Equal(desktop.Path, Assert.Single(applied.Output.SourceFolders!).Value);
        // Per job only: never written into a saved preset.
        var json = System.Text.Json.JsonSerializer.Serialize(applied.Output);
        Assert.DoesNotContain("SourceFolders", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(OutputLocation.SameFolder, "")]
    [InlineData(OutputLocation.Subfolder, "converted")]
    public void Next_to_the_source_means_next_to_the_shortcut(OutputLocation location, string subfolder)
    {
        using var desktop = new TempDir();
        using var program = new TempDir();
        var target = program.File("app.png");
        var other = program.File("other.png");
        var folders = new ShortcutFolders();
        folders.Note(Path.Combine(desktop.Path, "app.lnk"), target);
        var rule = folders.Apply(Jpg(location), [target, other]).Output;

        string? Resolve(string source) => OutputPathResolver.Resolve(rule,
            new OutputPathResolver.Tokens(source, "JPG", 1, DateTime.Now), "jpg", File.Exists);

        Assert.Equal(Path.Combine(desktop.Path, subfolder, "app.jpg"), Resolve(target));
        Assert.Equal(Path.Combine(program.Path, subfolder, "other.jpg"), Resolve(other));
    }

    [Fact]
    public async Task The_converted_file_lands_next_to_the_shortcut()
    {
        using var desktop = new TempDir();
        using var program = new TempDir();
        var target = Path.Combine(program.Path, "logo.png");
        File.WriteAllBytes(target, Png);
        var folders = new ShortcutFolders();
        var files = IPlatformServices.ResolveShortcuts([Path.Combine(desktop.Path, "logo.lnk")], _ => target, folders);
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("png", "jpg"))]));

        var job = new ConversionJob(files, folders.Apply(Jpg(), files), "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([Path.Combine(desktop.Path, "logo.jpg")], job.Outputs);
        Assert.Equal(["logo.png"], Directory.GetFiles(program.Path).Select(Path.GetFileName));
    }
}
