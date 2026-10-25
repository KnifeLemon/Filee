// #486: Setup turns Hardware-enforced Stack Protection off for Filee's programs only when the installed command line
// dies with the CET failure on Windows 10, and the uninstaller turns it back on. Nothing runs PowerShell for real.

using Filee.Setup.Installation;
using Filee.Setup.Platform;

namespace Filee.Setup.Tests;

public class CetCheckTests
{
    private static readonly Version OldWindows10 = new(10, 0, 17763);
    private static readonly Version Windows11 = new(10, 0, 26100);

    [Theory]
    [InlineData(unchecked((int)0xC0000409), 17763, true)]
    [InlineData(unchecked((int)0xC0000409), 19045, true)]
    [InlineData(unchecked((int)0xC0000409), 22000, false)] // Windows 11 has the fix
    [InlineData(1, 17763, false)]                          // an ordinary error
    [InlineData(-1, 17763, false)]                         // didn't start or timed out
    public void Only_the_fail_fast_status_on_windows_10_is_the_cet_failure(int exitCode, int build, bool expected) =>
        Assert.Equal(expected, CetCheck.IsCetFailure(exitCode, new Version(10, 0, build)));

    [Fact]
    public void The_exception_is_made_and_noted_when_filee_cannot_start()
    {
        using var bench = Bench(OldWindows10, cliExitCode: CetCheck.FailFastStatus);

        Assert.True(bench.Install().Succeeded);

        Assert.Contains("run filee-cli.exe version", bench.System.Calls);
        Assert.Contains(bench.System.Calls, c => c.StartsWith("run powershell.exe", StringComparison.Ordinal)
                                                 && c.Contains("Set-ProcessMitigation -Name 'Filee.exe' -Disable UserShadowStack")
                                                 && c.Contains("Set-ProcessMitigation -Name 'filee-cli.exe' -Disable UserShadowStack"));
        Assert.NotNull(bench.Registry.Get(Hive.LocalMachine, SetupNames.UninstallKey, SetupNames.ShadowStackOffValue));
    }

    [Theory]
    [InlineData(0)]     // Filee starts: nothing to do
    [InlineData(1)]     // fails for another reason: not this workaround's business
    public void Nothing_changes_when_filee_starts_or_fails_otherwise(int exitCode)
    {
        using var bench = Bench(OldWindows10, exitCode);

        Assert.True(bench.Install().Succeeded);

        Assert.DoesNotContain(bench.System.Calls, c => c.StartsWith("run powershell.exe", StringComparison.Ordinal));
        Assert.Null(bench.Registry.Get(Hive.LocalMachine, SetupNames.UninstallKey, SetupNames.ShadowStackOffValue));
    }

    [Fact]
    public void Windows_11_is_not_even_probed()
    {
        using var bench = Bench(Windows11, CetCheck.FailFastStatus);

        Assert.True(bench.Install().Succeeded);

        Assert.DoesNotContain("run filee-cli.exe version", bench.System.Calls);
        Assert.DoesNotContain(bench.System.Calls, c => c.StartsWith("run powershell.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void An_install_for_one_user_cannot_make_the_exception()
    {
        // Set-ProcessMitigation writes to HKLM: without administrator rights the log points to the README instead.
        using var bench = Bench(OldWindows10, CetCheck.FailFastStatus);
        var folder = bench.Paths.DefaultInstallFolder(InstallScope.User);

        Assert.True(bench.Install(bench.Choices() with { Folder = folder, Scope = InstallScope.User }).Succeeded);

        Assert.DoesNotContain(bench.System.Calls, c => c.StartsWith("run powershell.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void An_update_keeps_the_note_although_filee_starts_now()
    {
        // The first install made the exception; with it, the command line starts, but the note must stay for uninstalling.
        using var bench = Bench(OldWindows10, CetCheck.FailFastStatus);
        Assert.True(bench.Install().Succeeded);
        bench.System.Run = (_, _) => 0;

        Assert.True(bench.Install(previous: PreviousInstall.Find(bench.Registry, InstallScope.Machine)).Succeeded);

        Assert.NotNull(bench.Registry.Get(Hive.LocalMachine, SetupNames.UninstallKey, SetupNames.ShadowStackOffValue));
    }

    [Fact]
    public void Uninstalling_removes_the_exception_it_made()
    {
        using var bench = Bench(OldWindows10, CetCheck.FailFastStatus);
        Assert.True(bench.Install().Succeeded);
        bench.System.Calls.Clear();

        new Uninstaller(bench.Context).Run(bench.InstallFolder, InstallRecord.Read(bench.InstallFolder)!, deleteSettings: false,
            bench.Temp.Sub("trash"));

        Assert.Contains(bench.System.Calls, c => c.StartsWith("run powershell.exe", StringComparison.Ordinal)
                                                 && c.Contains("Set-ProcessMitigation -Name 'Filee.exe' -Remove"));
    }

    [Fact]
    public void Uninstalling_leaves_mitigations_alone_when_setup_made_none()
    {
        using var bench = Bench(OldWindows10, 0);
        Assert.True(bench.Install().Succeeded);
        bench.System.Calls.Clear();

        new Uninstaller(bench.Context).Run(bench.InstallFolder, InstallRecord.Read(bench.InstallFolder)!, deleteSettings: false,
            bench.Temp.Sub("trash"));

        Assert.DoesNotContain(bench.System.Calls, c => c.StartsWith("run powershell.exe", StringComparison.Ordinal));
    }

    /// <summary>A bench on <paramref name="windows"/> whose installed "filee-cli.exe version" ends with <paramref name="cliExitCode"/>.</summary>
    private static SetupBench Bench(Version windows, int cliExitCode)
    {
        var bench = new SetupBench();
        bench.Context = bench.Context with { WindowsVersion = windows };
        bench.System.Run = (executable, arguments) =>
            Path.GetFileName(executable) == CetCheck.CliExe && arguments == "version" ? cliExitCode : 0;
        return bench;
    }
}
