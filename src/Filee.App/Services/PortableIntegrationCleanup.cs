// Dispatches cleanup of integration owned by this executable without touching another installation.

using Filee.Platform.Linux;
using Filee.Platform.MacOS;
using Filee.Platform.Windows;

namespace Filee.App.Services;

internal static class PortableIntegrationCleanup
{
    internal static bool HasOwnedEntries(string executable) => OperatingSystem.IsWindows()
        ? PortableIntegration.HasOwnedEntries(executable)
        : OperatingSystem.IsLinux() ? LinuxPortableIntegration.Current.HasOwnedEntries(executable)
        : OperatingSystem.IsMacOS() && MacOSPortableIntegration.Current.HasOwnedEntries(executable);

    internal static Task RemoveOwnedEntriesAsync(string executable)
    {
        if (OperatingSystem.IsWindows()) return PortableIntegration.RemoveOwnedEntriesAsync(executable);
        if (OperatingSystem.IsLinux()) LinuxPortableIntegration.Current.RemoveOwnedEntries(executable);
        if (OperatingSystem.IsMacOS()) MacOSPortableIntegration.Current.RemoveOwnedEntries(executable);
        return Task.CompletedTask;
    }
}
