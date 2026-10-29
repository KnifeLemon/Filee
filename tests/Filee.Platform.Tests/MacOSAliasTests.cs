// Finder aliases dropped on Filee convert the file they point to, like Windows shortcuts (#159).

using Filee.Platform.MacOS;

// Each test checks for macOS before it touches the native methods.
#pragma warning disable CA1416

namespace Filee.Platform.Tests;

public sealed class MacOSAliasTests : IDisposable
{
    // The temporary folder is under /var, a symbolic link to /private/var, and a resolved alias gives the real path.
    private readonly string _folder = RealPath(Directory.CreateTempSubdirectory("filee-alias-").FullName);

    private static string RealPath(string path) =>
        path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string Write(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private string Alias(string name, string target)
    {
        var path = Path.Combine(_folder, name);
        NativeMethods.CreateAlias(path, target);
        return path;
    }

    [Fact]
    public void An_alias_resolves_to_its_file_or_folder_and_a_broken_one_to_nothing()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Finder aliases need macOS.");
            return;
        }
        var report = Write("보고서 report.docx");
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "photos")).FullName;
        var gone = Write("gone.pdf");
        var toReport = Alias("report.docx alias", report);
        var toFolder = Alias("photos alias", folder);
        var broken = Alias("gone alias", gone);
        File.Delete(gone);
        var platform = new MacOSPlatformServices();

        Assert.Equal(report, platform.ShortcutTarget(toReport));
        Assert.Equal(folder, platform.ShortcutTarget(toFolder));
        Assert.Null(platform.ShortcutTarget(broken));
        Assert.Null(platform.ShortcutTarget(report));
        Assert.Null(platform.ShortcutTarget(folder));
        Assert.Null(platform.ShortcutTarget(Path.Combine(_folder, "missing alias")));
    }

    [Fact]
    public void An_alias_to_a_file_in_a_trash_is_broken()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Finder aliases need macOS.");
            return;
        }
        // The layout of a volume's Trash; the user's ~/.Trash is matched the same way.
        var trash = Directory.CreateDirectory(Path.Combine(_folder, ".Trashes", "501")).FullName;
        var trashed = Path.Combine(trash, "report.docx");
        File.WriteAllText(trashed, "x");
        var toTrashed = Alias("report alias", trashed);

        Assert.Null(new MacOSPlatformServices().ShortcutTarget(toTrashed));
    }

    [Fact]
    public void Writing_an_alias_to_a_missing_file_throws()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Finder aliases need macOS.");
            return;
        }
        Assert.Throws<IOException>(() => Alias("missing alias", Path.Combine(_folder, "missing.pdf")));
    }

    [Fact]
    public void An_alias_dropped_with_its_target_gives_the_file_once_and_a_broken_one_stays()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Finder aliases need macOS.");
            return;
        }
        var report = Write("report.docx");
        var photo = Write("photo.jpg");
        var toReport = Alias("report alias", report);
        var broken = Write("broken alias");
        Core.Platform.IPlatformServices platform = new MacOSPlatformServices();

        var files = platform.ResolveShortcuts([toReport, photo, report, broken]);

        Assert.Equal([report, photo, broken], files);
    }
}
