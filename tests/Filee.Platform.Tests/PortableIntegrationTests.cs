using System.Runtime.Versioning;
using Filee.Platform.Windows;
using Microsoft.Win32;

namespace Filee.Platform.Tests;

[SupportedOSPlatform("windows")]
public sealed class PortableIntegrationTests
{
    [Theory]
    [InlineData("\"C:\\Portable Filee\\Filee.exe\" --background", true)]
    [InlineData("\"C:\\Other\\Filee.exe\" --background", false)]
    [InlineData("\"C:\\Portable Filee\\Filee.exe.old\" --background", false)]
    [InlineData("\"C:\\Other\\launcher.exe\" \"C:\\Portable Filee\\Filee.exe\"", false)]
    [InlineData("Filee.exe --background", false)]
    [InlineData("\"C:\\Portable Filee\\Filee.exe", false)]
    public void Ownership_requires_the_exact_executable_as_the_command_target(string command, bool expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");
        Assert.Equal(expected, PortableIntegration.CommandTargets(command, @"C:\Portable Filee\Filee.exe"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Cleanup_removes_only_owned_entries_in_an_isolated_registry_tree(bool ownedRun, bool ownedVerb)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry.");
        var path = @"Software\Filee.PortableTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            using var run = root.CreateSubKey(PortableIntegration.RunKey);
            using var command = root.CreateSubKey(PortableIntegration.VerbKey + @"\command");
            var executable = @"C:\Portable Filee\Filee.exe";
            var target = ownedRun ? executable : @"C:\Installed\Filee.exe";
            var verbTarget = ownedVerb ? executable : @"C:\Installed\Filee.exe";
            run.SetValue("Filee", $"\"{target}\" --background");
            run.SetValue("OtherApp", "keep");
            command.SetValue(null, $"\"{verbTarget}\" --convert \"%1\"");
            command.Close();
            Assert.Equal(ownedRun || ownedVerb, PortableIntegration.HasRegistryEntries(root, executable));
            PortableIntegration.RemoveRegistryEntries(root, executable);
            Assert.Equal(ownedRun ? null : $"\"{target}\" --background", run.GetValue("Filee"));
            Assert.Equal("keep", run.GetValue("OtherApp"));
            using var remaining = root.OpenSubKey(PortableIntegration.VerbKey);
            Assert.Equal(!ownedVerb, remaining is not null);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }
}
