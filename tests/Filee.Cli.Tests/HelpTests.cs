// CLI help must describe the watcher's platform default without loading settings or engines.

using Filee.Core.Watching;

namespace Filee.Cli.Tests;

public sealed class HelpTests
{
    [Theory]
    [InlineData("help", "watch")]
    [InlineData("watch", "--help")]
    [InlineData("watch", "-h")]
    public async Task Watch_help_reports_the_platform_default_without_loading_engines(string command, string option)
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        var code = await Cli.RunAsync([command, option], output, errors, TestContext.Current.CancellationToken,
            () => throw new InvalidOperationException("Help must not initialize engines."));

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors.ToString());
        Assert.Contains(FormattableString.Invariant(
            $"--settle <seconds> How long a file must stay unchanged (default {FolderWatcher.DefaultSettleTime.TotalSeconds})"), output.ToString());
    }

    /// <summary>Paths in help follow the platform (#1709): D:\Scans and \ on Windows, ~/Scans and / elsewhere.</summary>
    [Fact]
    public void Help_paths_follow_the_platform()
    {
        var general = Help.Text(null);
        var watch = Help.Text("watch");

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(@"filee convert D:\Scans", general);
            Assert.Contains(@"filee watch D:\Inbox", general);
            Assert.Contains(@"<folder>\converted", watch);
            Assert.Contains(@"<folder>\originals", watch);
            Assert.DoesNotContain("~/", general);
        }
        else
        {
            Assert.Contains("filee convert ~/Scans", general);
            Assert.Contains("filee watch ~/Inbox", general);
            Assert.Contains("<folder>/converted", watch);
            Assert.Contains("<folder>/originals", watch);
            Assert.DoesNotContain(@"D:\", general);
            Assert.DoesNotContain(@"\converted", watch);
            Assert.DoesNotContain(@"\originals", watch);
        }
    }
}
