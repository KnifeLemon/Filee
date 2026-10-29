// #450: the selection shortcut reads the desktop's selection when the desktop is in front, and "start at sign-in"
// says so when the entry it wrote didn't stay.

using System.Runtime.Versioning;
using Filee.Core.Platform;
using Filee.Platform.Windows;

namespace Filee.Platform.Tests;

// Plain checks: they run on every OS, the class is only marked for the Windows-only type.
[SupportedOSPlatform("windows")]
public sealed class WindowsTriggerSupportTests
{
    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("CabinetWClass", false)] // a folder window: Shell.Application lists it
    [InlineData("Shell_TrayWnd", false)] // the taskbar
    [InlineData(null, false)]
    public void The_desktop_is_told_from_folder_windows(string? windowClass, bool desktop) =>
        Assert.Equal(desktop, WindowsPlatformServices.IsDesktop(windowClass));

    [Theory]
    [InlineData(10, 20, "Progman", true)]        // a desktop icon layer, owned by the desktop
    [InlineData(10, 20, "WorkerW", true)]
    [InlineData(10, 20, "CabinetWClass", false)] // a dialog owned by a folder window
    [InlineData(20, 20, "Progman", false)]       // the desktop itself is its own root owner: nothing to map
    [InlineData(10, 0, null, false)]             // no owner
    public void A_window_the_desktop_owns_stands_for_the_desktop(long window, long rootOwner, string? rootOwnerClass, bool desktop) =>
        Assert.Equal(desktop, WindowsPlatformServices.StandsForDesktop((nint)window, (nint)rootOwner, rootOwnerClass));

    [Fact]
    public void The_startup_entry_read_back_must_be_the_one_written()
    {
        const string written = "\"C:\\Program Files\\Filee\\Filee.exe\" --background";
        WindowsPlatformServices.CheckStartupEntry(written, written);

        Assert.Throws<StartupEntryNotKeptException>(() => WindowsPlatformServices.CheckStartupEntry(written, null));
        Assert.Throws<StartupEntryNotKeptException>(() =>
            WindowsPlatformServices.CheckStartupEntry(written, "\"C:\\Program Files\\Filee\\Filee.exe\""));
        Assert.Throws<StartupEntryNotKeptException>(() => WindowsPlatformServices.CheckStartupEntry(written, new byte[] { 1 }));
    }
}
