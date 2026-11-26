// Gestures set to "File managers and desktop" also start over file managers other than Explorer (issue #124).

using System.Runtime.Versioning;
using Filee.Platform.Linux;
using Filee.Platform.Windows;

namespace Filee.App.Tests;

// Plain string checks: they run on every OS, the class is only marked for the Windows-only type.
[SupportedOSPlatform("windows")]
public class FileManagerScopeTests
{
    [Theory]
    [InlineData("CabinetWClass", "explorer")]
    [InlineData("Progman", "explorer")]
    [InlineData("WinUIDesktopWin32WindowClass", "Files")]
    [InlineData("dopus.lister", "dopus")]
    [InlineData("TTOTAL_CMD", "TOTALCMD64")]
    [InlineData("ThunderRT6FormDC", "XYplorer")]
    [InlineData("Unknown", "FPilot")] // File Pilot: found by its process name alone
    [InlineData("ATL:ExplorerFrame", "xplorer2_64")] // xplorer² (#1424)
    [InlineData("Zed::Window", "rustyfiles")] // rustyfiles (#1714)
    public void Explorer_the_desktop_and_other_file_managers_count(string windowClass, string process) =>
        Assert.True(WindowsPlatformServices.IsFileManager(windowClass, process));

    [Theory]
    [InlineData("Chrome_WidgetWin_1", "chrome")]
    [InlineData("Notepad", "notepad")]
    [InlineData("WinUIDesktopWin32WindowClass", "FilesBackup")]
    [InlineData(null, null)]
    public void Other_programs_dont(string? windowClass, string? process) =>
        Assert.False(WindowsPlatformServices.IsFileManager(windowClass, process));
}

// Plain string checks: they run on every OS, the class is only marked for the Linux-only type.
[SupportedOSPlatform("linux")]
public class LinuxFileManagerScopeTests
{
    [Theory]
    [InlineData("thunar")]
    [InlineData("nautilus")]
    [InlineData("dolphin")]
    [InlineData("xfdesktop")]
    [InlineData("flea")]
    [InlineData("ROX-Filer")]
    [InlineData("rox-filer")]
    [InlineData("rox")]
    [InlineData("ROX-Pinboard")]
    public void Recognized_Linux_file_managers_and_desktops_count(string process) =>
        Assert.True(LinuxPlatformServices.IsFileManager(process));

    [Theory]
    [InlineData("firefox")]
    [InlineData("vivaldi-bin")]
    [InlineData("ROX-FilerBackup")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_Linux_programs_dont(string? process) =>
        Assert.False(LinuxPlatformServices.IsFileManager(process));
}
