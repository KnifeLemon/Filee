// A former portable copy may have registered integration before the marker existed.

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Filee.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class PortableIntegration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string VerbKey = @"Software\Classes\*\shell\Filee";
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Filee.lnk");

    public static bool HasOwnedEntries(string executable) =>
        HasRegistryEntries(Registry.CurrentUser, executable)
        || SameExecutable(ShellLinks.TargetOf(Shortcut), executable) || OwnsModernPackage(executable);

    public static async Task RemoveOwnedEntriesAsync(string executable)
    {
        RemoveRegistryEntries(Registry.CurrentUser, executable);
        if (SameExecutable(ShellLinks.TargetOf(Shortcut), executable)) File.Delete(Shortcut);
        if (OwnsModernPackage(executable))
        {
            var result = await ExplorerMenuRegistration.RemoveOwnedAsync(Path.GetDirectoryName(executable)!);
            if (!result.Succeeded) throw new IOException(result.Error ?? "Could not remove the Explorer menu.");
        }
    }

    private static bool OwnsModernPackage(string executable) => ExplorerMenuRegistration.IsSupportedOs
        && ExplorerMenuRegistration.Find()?.ExternalLocation is { } folder
        && SameExecutable(Path.Combine(folder, "Filee.exe"), executable);

    internal static bool HasRegistryEntries(RegistryKey root, string executable)
    {
        using var run = root.OpenSubKey(RunKey);
        using var command = root.OpenSubKey(VerbKey + @"\command");
        return CommandTargets(run?.GetValue("Filee") as string, executable)
            || CommandTargets(command?.GetValue(null) as string, executable);
    }

    internal static void RemoveRegistryEntries(RegistryKey root, string executable)
    {
        using var run = root.OpenSubKey(RunKey, writable: true);
        if (CommandTargets(run?.GetValue("Filee") as string, executable)) run!.DeleteValue("Filee", false);
        using var command = root.OpenSubKey(VerbKey + @"\command");
        var ownsVerb = CommandTargets(command?.GetValue(null) as string, executable);
        command?.Dispose();
        if (ownsVerb) root.DeleteSubKeyTree(VerbKey, false);
    }

    internal static bool CommandTargets(string? command, string executable)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        command = command.Trim();
        string target;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) return false;
            target = command[1..end];
        }
        else target = command.Split([' ', '\t'], 2)[0];
        return SameExecutable(target, executable);
    }

    private static bool SameExecutable(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || !Path.IsPathFullyQualified(a)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
}
