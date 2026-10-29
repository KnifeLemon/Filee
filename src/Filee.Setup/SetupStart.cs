// What this program is when it starts. With a payload it is Setup. Without one it is the uninstaller only under the
// uninstaller's name (uninstall.exe: what Setup writes next to Filee.exe, and the copy it runs from %TEMP%), under the
// build output's name (Filee.Setup.exe, a development build) or with /UNINSTALL. Any other program without a payload is
// a Setup.exe whose download didn't finish: the payload's trailer is the last part of the file (only the signature
// comes after it), so a cut file has none. Run as the uninstaller, it said Filee isn't installed in the Downloads
// folder (#599).

using Filee.Setup.Installation;
using Filee.Setup.Payload;

namespace Filee.Setup;

internal enum StartMode
{
    Install,
    Uninstall,

    /// <summary>The payload is there but damaged.</summary>
    Damaged,

    /// <summary>No payload and not the uninstaller: a Setup.exe cut off by a download that didn't finish.</summary>
    Incomplete,
}

/// <param name="Problem">What is wrong, for the log (<see cref="StartMode.Damaged"/>, <see cref="StartMode.Incomplete"/>).</param>
internal sealed record SetupStart(StartMode Mode, SetupPayload? Payload = null, string? Problem = null)
{
    /// <summary>The build output's name. Releases rename it (Filee-&lt;version&gt;-win-Setup.exe, uninstall.exe).</summary>
    public const string DevelopmentExe = "Filee.Setup.exe";

    /// <param name="uninstall">/UNINSTALL: uninstall whatever the program carries.</param>
    public static SetupStart Of(string exe, bool uninstall)
    {
        SetupPayload? payload;
        try
        {
            payload = SetupPayload.Open(exe);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new SetupStart(StartMode.Damaged, Problem: ex.Message);
        }
        if (uninstall)
        {
            payload?.Dispose();
            return new SetupStart(StartMode.Uninstall);
        }
        if (payload is not null)
            return new SetupStart(StartMode.Install, payload);
        var name = Path.GetFileName(exe);
        if (name.Equals(SetupNames.UninstallerExe, StringComparison.OrdinalIgnoreCase) || name.Equals(DevelopmentExe, StringComparison.OrdinalIgnoreCase))
            return new SetupStart(StartMode.Uninstall);
        return new SetupStart(StartMode.Incomplete, Problem: Describe(exe));
    }

    private static string Describe(string exe)
    {
        try
        {
            using var file = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return $"{Path.GetFileName(exe)} ({file.Length} bytes) has no payload" +
                   (SetupPayload.SignatureCutOff(file) ? " and its signature is cut off" : "") + "; the download didn't finish";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(exe)} has no payload ({ex.Message})";
        }
    }
}
