using System.Runtime.Versioning;
using Filee.Setup.Platform;

namespace Filee.Setup.Tests;

/// <summary>What earlier runs left of Setup's own copies of Skia and HarfBuzz, and what of it a later run deletes.</summary>
[SupportedOSPlatform("windows")]
public class NativeLibrariesTests
{
    [Fact]
    public void A_build_carries_no_libraries_of_its_own()
    {
        // Only the publish embeds them (Filee.Setup.csproj); a build finds them next to it, like the tests do.
        Assert.False(NativeLibraries.AreCarried);
    }

    [Fact]
    public void Old_folders_go_and_new_ones_stay()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Setup is for Windows.");
        using var temp = new TempFolder();
        var old = Folder(temp, "old", TimeSpan.FromHours(2));
        var recent = Folder(temp, "recent", TimeSpan.Zero);
        var log = new SetupLog(null);

        NativeLibraries.RemoveOld(temp.Path, elevated: false, log);

        Assert.False(Directory.Exists(old));
        // Possibly another Setup's, still writing its libraries.
        Assert.True(File.Exists(Path.Combine(recent, "libSkiaSharp.dll")));
    }

    [Fact]
    public void Only_the_files_Setup_writes_are_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Setup is for Windows.");
        using var temp = new TempFolder();
        var old = Folder(temp, "old", TimeSpan.FromHours(2), extra: "notes.txt");
        var unrelated = temp.Write(@"Other\libSkiaSharp.dll");

        NativeLibraries.RemoveOld(temp.Path, elevated: false, new SetupLog(null));

        Assert.False(File.Exists(Path.Combine(old, "libSkiaSharp.dll")));
        Assert.True(File.Exists(Path.Combine(old, "notes.txt")));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void With_administrator_rights_a_folder_a_user_made_stays()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && !NativeMethods.IsElevated(), "Needs a folder owned by a standard user.");
        using var temp = new TempFolder();
        var old = Folder(temp, "old", TimeSpan.FromHours(2));

        NativeLibraries.RemoveOld(temp.Path, elevated: true, new SetupLog(null));

        Assert.True(File.Exists(Path.Combine(old, "libSkiaSharp.dll")));
    }

    private static string Folder(TempFolder temp, string name, TimeSpan age, string? extra = null)
    {
        var folder = temp.Sub(NativeLibraries.FolderPrefix + name);
        foreach (var library in NativeLibraries.Carried)
            temp.Write(Path.Combine(folder, library + ".dll"));
        if (extra is not null)
            temp.Write(Path.Combine(folder, extra));
        Directory.SetCreationTimeUtc(folder, DateTime.UtcNow - age);
        return folder;
    }
}
