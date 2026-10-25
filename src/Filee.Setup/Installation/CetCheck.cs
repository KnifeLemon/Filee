// The start-up failure the README's note from #486 describes: on Windows 10 that hasn't been updated for a long time,
// on a processor with Hardware-enforced Stack Protection (CET), Windows turns the protection on for .NET programs but
// lacks a part the runtime needs, and Filee stops at start without a window or a message. Setup itself is built with
// CETCompat=false, so it runs there.
//
// After installing on Windows 10, Setup starts the installed command line once ("filee-cli.exe version", no window).
// When it dies with the fail-fast status a shadow stack violation raises (0xC0000409), Setup turns the protection off
// for Filee's two programs only, as the README's workaround does, and notes it in the uninstall entry so the uninstaller
// removes the exception again. Everywhere else nothing changes: Filee keeps the protection.

using Filee.Setup.Platform;

namespace Filee.Setup.Installation;

/// <summary>Finds the CET start-up failure of #486 and applies the per-program exception.</summary>
internal static class CetCheck
{
    /// <summary>STATUS_STACK_BUFFER_OVERRUN: the status of a fail-fast, which a shadow stack violation raises.</summary>
    internal const int FailFastStatus = unchecked((int)0xC0000409);

    /// <summary>Windows 11's first build: the bug is in old Windows 10 builds only.</summary>
    private const int Windows11Build = 22000;

    /// <summary>The installed programs that need the exception.</summary>
    internal static readonly string[] Programs = [SetupNames.AppExe, CliExe];

    /// <summary>The command line program next to Filee.exe.</summary>
    internal const string CliExe = "filee-cli.exe";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(60);

    /// <summary>True on the Windows builds #486 happens on (Windows 10).</summary>
    internal static bool IsAffected(Version windows) => windows.Major == 10 && windows.Build < Windows11Build;

    /// <summary>True when the probe's exit code is the CET failure of #486 on this Windows.</summary>
    internal static bool IsCetFailure(int exitCode, Version windows) => exitCode == FailFastStatus && IsAffected(windows);

    /// <summary>
    /// Starts the installed command line once; on the CET failure turns the protection off for Filee's programs
    /// (needs administrator rights, which a machine-wide install has). Returns true when the exception is in place.
    /// </summary>
    internal static bool Run(SetupContext context, string folder, bool elevated)
    {
        var cli = Path.Combine(folder, CliExe);
        var windows = context.WindowsVersion ?? Environment.OSVersion.Version;
        if (context.Sandbox || !IsAffected(windows) || !File.Exists(cli))
            return false;
        var exitCode = context.System.RunAndWait(cli, "version", ProbeTimeout);
        if (exitCode == 0)
            return false;
        if (!IsCetFailure(exitCode, windows))
        {
            context.Log.Info($"filee-cli.exe version ended with {exitCode:X8} (not the CET start-up failure of #486)");
            return false;
        }
        if (!elevated)
        {
            context.Log.Info("Filee can't start on this Windows 10 (CET, #486): install the Windows updates, or see the README for the " +
                             "Set-ProcessMitigation workaround (it needs administrator rights, which this install for one user doesn't have)");
            return false;
        }
        var result = context.System.RunAndWait(PowerShell, Command("-Disable UserShadowStack"), PowerShellTimeout);
        context.Log.Info(result == 0
            ? "Filee can't start on this Windows 10 with Hardware-enforced Stack Protection (#486): turned it off for Filee.exe and filee-cli.exe"
            : $"Turning Hardware-enforced Stack Protection off for Filee failed (PowerShell exit code {result})");
        return result == 0;
    }

    /// <summary>Removes the exception again (uninstall).</summary>
    internal static void Remove(SetupContext context)
    {
        if (context.Sandbox)
            return;
        var result = context.System.RunAndWait(PowerShell, Command("-Remove"), PowerShellTimeout);
        context.Log.Info(result == 0
            ? "Removed the Hardware-enforced Stack Protection exception for Filee.exe and filee-cli.exe"
            : $"Removing the Hardware-enforced Stack Protection exception failed (PowerShell exit code {result})");
    }

    /// <summary>Windows PowerShell, part of Windows (Set-ProcessMitigation is in its ProcessMitigations module).</summary>
    private static string PowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>The PowerShell arguments that run Set-ProcessMitigation with <paramref name="what"/> for each program.</summary>
    internal static string Command(string what) =>
        "-NoLogo -NoProfile -NonInteractive -Command \"" +
        string.Join("; ", Programs.Select(p => $"Set-ProcessMitigation -Name '{p}' {what}")) + "\"";
}
