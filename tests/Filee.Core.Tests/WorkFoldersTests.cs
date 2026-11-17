// Where the work files go: the user's own temp folder on Windows; on Linux and macOS, where /tmp is shared, the
// user's cache folder with mode 700.

using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public class WorkFoldersTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void Jobs_and_browser_files_share_one_root()
    {
        Assert.Equal(WorkFolders.Root, JobQueue.DefaultWorkRoot);
        Assert.Equal(Path.Combine(WorkFolders.Root, "web"), WebDrop.DefaultWorkRoot);
        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.Combine(Path.GetTempPath(), "Filee"), WorkFolders.Root); // as before: %TEMP% is the user's
    }

    [Fact]
    public void The_root_on_linux_and_macos_is_in_the_users_cache_folder()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\someone" : "/home/someone";
        var cache = OperatingSystem.IsWindows() ? @"D:\cache" : "/srv/cache/someone";

        Assert.Equal(Path.Combine(home, ".cache", "filee"), WorkFolders.UnixRoot(null, home, macOS: false));
        Assert.Equal(Path.Combine(cache, "filee"), WorkFolders.UnixRoot(cache, home, macOS: false));
        Assert.Equal(Path.Combine(home, ".cache", "filee"), WorkFolders.UnixRoot("relative/cache", home, macOS: false)); // ignored, as XDG says
        Assert.Equal(Path.Combine(home, "Library", "Caches", "Filee"), WorkFolders.UnixRoot(cache, home, macOS: true));
        Assert.Null(WorkFolders.UnixRoot(null, "", macOS: false));
        Assert.Null(WorkFolders.UnixRoot("", null, macOS: true));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Only_the_user_can_open_the_root_on_linux_and_macos()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps the work files in the user's own temp folder.");

        var root = WorkFolders.Root;
        Assert.True(Directory.Exists(root));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(root));
        // Not a fixed name in the shared /tmp that someone else could have made first.
        Assert.NotEqual(Path.Combine(Path.GetTempPath(), "Filee"), root);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_folder_that_others_could_open_is_made_private()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "File modes are a Unix matter.");

        using var dir = new TempDir();
        var made = Path.Combine(dir.Path, "new", "filee");
        WorkFolders.MakePrivate(made);
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(made));

        var open = Path.Combine(dir.Path, "open");
        Directory.CreateDirectory(open);
        File.SetUnixFileMode(open, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
                                   | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        WorkFolders.MakePrivate(open);
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(open));
    }
}
