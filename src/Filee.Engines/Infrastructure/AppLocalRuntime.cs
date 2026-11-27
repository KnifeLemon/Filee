// The Microsoft C++ runtime (MSVCP140.dll, VCRUNTIME140.dll, ...) that engines built with Visual C++ need.
//
// Windows has no copy of its own: a PC gets one from the Visual C++ Redistributable, which many programs install but a
// fresh Windows lacks. Filee therefore puts the runtime next to the programs that need it (app-local deployment,
// Windows looks in a program's folder first): the "vcruntime" download (conda-forge) next to rhwp.exe, Filee.exe
// (for unhwp.dll) and gswin64c.exe, LibreOffice's own copy next to soffice.exe (see CopyLibreOfficeRuntime).
// When a DLL is still missing, Windows does not start the program at all and reports STATUS_DLL_NOT_FOUND as its
// exit code; ThrowIfMissing turns that into a message the user can act on.

using System.ComponentModel;
using System.Security;
using Filee.Core.Conversion;
using Microsoft.Win32;

namespace Filee.Engines.Infrastructure;

public static class AppLocalRuntime
{
    /// <summary>Exit code of a program Windows could not start because a DLL it imports is missing (0xC0000135).</summary>
    public const int DllNotFound = unchecked((int)0xC0000135);

    /// <summary>Exit code of a program whose DLL is too old to have a function it imports (0xC0000139).</summary>
    public const int EntryPointNotFound = unchecked((int)0xC0000139);

    /// <summary>Localization key of the message for <see cref="DllNotFound"/> and <see cref="EntryPointNotFound"/>.</summary>
    public const string ErrorKey = "error.runtime_missing";

    /// <summary>True when Windows refused to start a program because of a missing or outdated DLL.</summary>
    public static bool IsMissingDll(int exitCode) => exitCode is DllNotFound or EntryPointNotFound;

    /// <summary>
    /// Exit code of a program whose DLL Windows' code integrity refused to load (0xC0E90002): Smart App Control or
    /// App Control for Business blocking a file it cannot verify. Windows shows it as "Bad Image" (#1847).
    /// </summary>
    public const int BlockedByCodeIntegrity = unchecked((int)0xC0E90002);

    /// <summary>Exit code of a program whose file failed the image hash / signature check (0xC0000428).</summary>
    public const int InvalidImageHash = unchecked((int)0xC0000428);

    /// <summary>Localization key of the message for a program Windows blocked (see <see cref="IsBlockedByWindows(int)"/>).</summary>
    public const string BlockedErrorKey = "error.blocked_by_windows";

    /// <summary>Localization key of the engine status for a program Windows blocked.</summary>
    public const string BlockedReasonKey = "engine.reason.blocked_by_windows";

    /// <summary>True when Windows (Smart App Control, App Control, SmartScreen) refused to load a file of the program.</summary>
    public static bool IsBlockedByWindows(int exitCode) => exitCode is BlockedByCodeIntegrity or InvalidImageHash;

    /// <summary>
    /// True when Windows refused to start the program itself: ERROR_ACCESS_DISABLED_BY_POLICY (1260),
    /// ERROR_CODE_INTEGRITY... (4551, 4552), ERROR_VIRUS_INFECTED (225) and ERROR_BAD_EXE_FORMAT-less policy blocks.
    /// </summary>
    public static bool IsBlockedByWindows(Win32Exception ex) => ex.NativeErrorCode is 1260 or 4551 or 4552 or 225;

    /// <summary>
    /// True when Smart App Control is on (or still evaluating) on this PC. It blocks unsigned programs and DLLs that
    /// Microsoft's cloud doesn't know, whatever the user clicks, and the only ways around are a signed copy of the
    /// program or turning it off (which Windows then never turns on again without a reset).
    /// </summary>
    public static bool SmartAppControlActive()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
            // 0 off, 1 on (enforced), 2 evaluation mode.
            return key?.GetValue("VerifiedAndReputablePolicyState") is int state && state != 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The exception for a program Windows blocked, with the program, the exit code and whether Smart App Control is
    /// on as detail.
    /// </summary>
    public static ConversionRefusedException Blocked(string executable, string code) =>
        new(BlockedErrorKey, $"{Path.GetFileName(executable)}, {code}{(SmartAppControlActive() ? ", Smart App Control" : "")}");

    /// <summary>
    /// Throws a <see cref="ConversionRefusedException"/> saying that a runtime file is missing (with the program and
    /// the exit code as detail) when <paramref name="exitCode"/> is <see cref="DllNotFound"/> or
    /// <see cref="EntryPointNotFound"/>, instead of the raw exit code each engine would report.
    /// </summary>
    public static void ThrowIfMissing(string executable, int exitCode)
    {
        if (IsMissingDll(exitCode))
            throw new ConversionRefusedException(ErrorKey, $"{Path.GetFileName(executable)}, 0x{exitCode:X8}");
        if (IsBlockedByWindows(exitCode))
            throw Blocked(executable, $"0x{exitCode:X8}");
    }

    /// <summary>
    /// LibreOffice's MSI installs the C++ runtime into the system folder; unpacked with an administrative install
    /// (msiexec /a) it lands in System64 next to program/ instead, where soffice.exe never looks. Copies those DLLs
    /// into program/ (never replacing one that is there). Returns the number of files copied; 0 for any other layout.
    /// </summary>
    public static int CopyLibreOfficeRuntime(string root)
    {
        var system64 = Path.Combine(root, "System64");
        var program = Path.Combine(root, "program");
        if (!Directory.Exists(system64) || !Directory.Exists(program))
            return 0;
        var copied = 0;
        foreach (var dll in Directory.EnumerateFiles(system64, "*.dll"))
        {
            var target = Path.Combine(program, Path.GetFileName(dll));
            if (File.Exists(target))
                continue;
            File.Copy(dll, target);
            copied++;
        }
        return copied;
    }
}
