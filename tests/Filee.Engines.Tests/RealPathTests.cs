// msiexec gets real paths, without the junctions a user made on the way (#527): the Windows Installer service
// refuses those ("untrusted mount point") and fails with error 1620.

using System.Diagnostics;
using System.Runtime.Versioning;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public sealed class RealPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-realpath-" + Guid.NewGuid().ToString("N")[..8]);

    private string Link => Path.Combine(_root, "link");

    public RealPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // A junction goes first, on its own: a recursive delete with one inside fails with access denied.
        try
        {
            if (Directory.Exists(Link))
                Directory.Delete(Link);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_junction_on_the_way_is_followed_for_a_folder_and_a_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows junctions.");
        var real = Directory.CreateDirectory(Path.Combine(_root, "real", "staging")).Parent!.FullName;
        File.WriteAllText(Path.Combine(real, "package.msi"), "");
        var link = Link;
        Junction(link, real); // like AppData\Local moved to another drive

        // The temp folder may itself have short (8.3) names, which the final path spells out.
        var expected = RealPath.Of(real);
        Assert.DoesNotContain("link", expected, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(expected, "staging"), RealPath.Of(Path.Combine(link, "staging")), ignoreCase: true);
        Assert.Equal(Path.Combine(expected, "package.msi"), RealPath.Of(Path.Combine(link, "package.msi")), ignoreCase: true);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_path_without_links_stays_and_one_that_cannot_be_opened_is_kept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows final paths.");
        var plain = Path.Combine(RealPath.Of(_root), "plain");
        Directory.CreateDirectory(plain);
        Assert.Equal(plain, RealPath.Of(plain));

        var missing = Path.Combine(_root, "missing", "package.msi");
        Assert.Equal(missing, RealPath.Of(missing));
    }

    [Fact]
    public void The_long_path_prefix_is_dropped()
    {
        Assert.Equal(@"C:\Users\x", RealPath.WithoutPrefix(@"\\?\C:\Users\x"));
        Assert.Equal(@"\\server\share\x", RealPath.WithoutPrefix(@"\\?\UNC\server\share\x"));
        Assert.Equal(@"D:\x", RealPath.WithoutPrefix(@"D:\x"));
    }

    [Fact]
    public void An_installer_that_cannot_open_its_package_says_so()
    {
        // In the user's language on the Engines card (error.* keys, every language file has them).
        var link = Assert.IsType<ConversionRefusedException>(EngineInstaller.MsiFailure("libreoffice", 1620, @"C:\Temp\filee-msiexec.log"));
        Assert.Equal("error.msi_link", link.ErrorKey);
        Assert.Equal(@"msiexec 1620, log: C:\Temp\filee-msiexec.log", link.Detail);
        Assert.Equal("error.msi_busy", Assert.IsType<ConversionRefusedException>(EngineInstaller.MsiFailure("calibre", 1618, "log")).ErrorKey);
        Assert.Contains("exit code 1603", EngineInstaller.MsiFailure("calibre", 1603, "log").Message);
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }
}
