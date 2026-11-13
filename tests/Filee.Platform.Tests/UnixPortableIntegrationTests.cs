// Exercises generated Linux/macOS entries in isolated directories without changing the real user profile.

using System.Xml.Linq;
using Filee.Platform.Linux;
using Filee.Platform.MacOS;

namespace Filee.Platform.Tests;

public sealed class UnixPortableIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-portable-integration-").FullName;
    private const string Executable = "/opt/Filee's portable % $ copy/Filee";
    private const string Other = "/opt/Installed Filee/Filee";

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Linux_cleanup_preserves_other_copies_and_unrelated_thunar_actions(bool ownStartup, bool ownMenu)
    {
        var config = Path.Combine(_root, "config");
        var data = Path.Combine(_root, "data");
        var normal = new LinuxDesktopIntegration(config, data);
        normal.SetAutostart(true, ownStartup ? Executable : Other);
        normal.SetContextMenu(true, ownMenu ? Executable : Other, "Convert");
        normal.SetThunarShortcut("<Control>space");
        var accelerators = Path.Combine(config, "Thunar", "accels.scm");
        File.AppendAllText(accelerators, "(gtk_accel_path \"<Actions>/other\" \"<Alt>x\")\n");
        var thunar = Path.Combine(config, "Thunar", "uca.xml");
        var document = XDocument.Load(thunar);
        document.Root!.Add(new XElement("action", new XElement("unique-id", "unrelated"), new XElement("command", "echo keep")));
        document.Save(thunar);
        var cleanup = new LinuxPortableIntegration(config, data);
        Assert.Equal(ownStartup || ownMenu, cleanup.HasOwnedEntries(Executable));
        cleanup.RemoveOwnedEntries(Executable);
        Assert.False(cleanup.HasOwnedEntries(Executable));
        Assert.Equal(!ownStartup, File.Exists(Path.Combine(config, "autostart", "filee.desktop")));
        foreach (var path in new[] { "Thunar/sendto/filee.desktop", "nautilus/scripts/Filee", "nemo/actions/filee.nemo_action", "kio/servicemenus/filee.desktop" })
            Assert.Equal(!ownMenu, File.Exists(Path.Combine(data, path)));
        document = XDocument.Load(thunar);
        Assert.Contains(document.Root!.Elements("action"), a => (string?)a.Element("command") == "echo keep");
        Assert.Equal(!ownMenu, document.Root.Elements("action").Any(a => (string?)a.Element("unique-id") == LinuxDesktopIntegration.ThunarActionId));
        Assert.Equal(!ownMenu, File.ReadAllText(Path.Combine(config, "Thunar", "accels.scm")).Contains(LinuxDesktopIntegration.ThunarAcceleratorPath));
        Assert.Contains("<Actions>/other", File.ReadAllText(accelerators));
        cleanup.RemoveOwnedEntries(Executable); // Repeated cleanup is harmless.
    }

    [Fact]
    public void Linux_malformed_actions_and_launchers_with_multiple_commands_are_preserved()
    {
        var config = Path.Combine(_root, "config");
        var data = Path.Combine(_root, "data");
        var normal = new LinuxDesktopIntegration(config, data);
        normal.SetAutostart(true, Executable);
        var startup = Path.Combine(config, "autostart", "filee.desktop");
        File.AppendAllText(startup, "Exec=\"/opt/Other/Filee\" --background\n");
        var thunar = Path.Combine(config, "Thunar", "uca.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(thunar)!);
        File.WriteAllText(thunar, "<actions><broken");
        var cleanup = new LinuxPortableIntegration(config, data);
        Assert.False(cleanup.HasOwnedEntries(Executable));
        cleanup.RemoveOwnedEntries(Executable);
        Assert.True(File.Exists(startup));
        Assert.Equal("<actions><broken", File.ReadAllText(thunar));
    }

    [Fact]
    public void Linux_portable_paths_that_cannot_be_registered_do_not_break_first_start()
    {
        var cleanup = new LinuxPortableIntegration(Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        Assert.False(cleanup.HasOwnedEntries("/opt/portable=copy/Filee"));
        cleanup.RemoveOwnedEntries("/opt/portable=copy/Filee");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Mac_cleanup_preserves_entries_for_other_executables(bool ownAgent, bool ownWorkflow)
    {
        var agent = Write("Library/LaunchAgents/com.filee.app.plist", MacOSIntegrationFiles.LaunchAgent(ownAgent ? Executable : Other));
        var workflow = WriteWorkflow(ownWorkflow ? Executable : Other);
        var sharedPreferences = Write("Library/Preferences/pbs.plist", "shared Finder service preferences");
        var cleanup = new MacOSPortableIntegration(Path.Combine(_root, "Library"));
        Assert.Equal(ownAgent || ownWorkflow, cleanup.HasOwnedEntries(Executable));
        cleanup.RemoveOwnedEntries(Executable);
        Assert.False(cleanup.HasOwnedEntries(Executable));
        Assert.Equal(!ownAgent, File.Exists(agent));
        Assert.Equal(!ownWorkflow, File.Exists(workflow));
        Assert.Equal("shared Finder service preferences", File.ReadAllText(sharedPreferences));
        cleanup.RemoveOwnedEntries(Executable);
    }

    [Fact]
    public void Mac_bundle_workflow_is_matched_using_its_exact_quoted_open_command()
    {
        const string executable = "/Users/a & b/Portable 'copy'/Filee.app/Contents/MacOS/Filee";
        var workflow = WriteWorkflow(executable);
        var cleanup = new MacOSPortableIntegration(Path.Combine(_root, "Library"));
        Assert.False(cleanup.HasOwnedEntries(executable + ".old"));
        Assert.True(cleanup.HasOwnedEntries(executable));
        cleanup.RemoveOwnedEntries(executable);
        Assert.False(File.Exists(workflow));
    }

    [Fact]
    public void Mac_does_not_match_an_executable_passed_as_an_argument_to_another_program()
    {
        Write("Library/LaunchAgents/com.filee.app.plist", MacOSIntegrationFiles.LaunchAgent(Other).Replace("--background", Executable));
        Write("Library/Services/Filee.workflow/Contents/document.wflow", "not a property list");
        var cleanup = new MacOSPortableIntegration(Path.Combine(_root, "Library"));
        Assert.False(cleanup.HasOwnedEntries(Executable));
        cleanup.RemoveOwnedEntries(Executable);
        Assert.True(File.Exists(Path.Combine(_root, "Library/LaunchAgents/com.filee.app.plist")));
    }

    [Fact]
    public void Mac_workflows_with_actions_for_more_than_one_copy_are_preserved()
    {
        var path = WriteWorkflow(Executable);
        var document = XDocument.Load(path);
        document.Root!.Element("dict")!.Add(new XElement("dict", new XElement("key", "COMMAND_STRING"),
            new XElement("string", MacOSPortableIntegration.ConversionCommand(Other))));
        document.Save(path);
        var cleanup = new MacOSPortableIntegration(Path.Combine(_root, "Library"));
        Assert.False(cleanup.HasOwnedEntries(Executable));
        cleanup.RemoveOwnedEntries(Executable);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Unix_cleanup_does_not_follow_linked_entries_or_delete_linked_workflow_contents()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix symbolic links.");
        var original = Write("original.plist", MacOSIntegrationFiles.LaunchAgent(Executable));
        var agent = Path.Combine(_root, "Library/LaunchAgents/com.filee.app.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(agent)!);
        File.CreateSymbolicLink(agent, original);
        var cleanup = new MacOSPortableIntegration(Path.Combine(_root, "Library"));
        Assert.False(cleanup.HasOwnedEntries(Executable));
        cleanup.RemoveOwnedEntries(Executable);
        Assert.True(File.Exists(original));
        var workflow = WriteWorkflow(Executable);
        File.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName(workflow)!, "linked"), original);
        Assert.Throws<IOException>(() => cleanup.RemoveOwnedEntries(Executable));
        Assert.True(File.Exists(workflow));
        Assert.True(File.Exists(original));
        var config = Path.Combine(_root, "config");
        var data = Path.Combine(_root, "data");
        var desktop = Write("original.desktop", $"[Desktop Entry]\nExec={LinuxDesktopIntegration.DesktopArgument(Executable)} --background\n");
        var startup = Path.Combine(config, "autostart", "filee.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(startup)!);
        File.CreateSymbolicLink(startup, desktop);
        new LinuxPortableIntegration(config, data).RemoveOwnedEntries(Executable);
        Assert.True(File.Exists(startup));
        Assert.True(File.Exists(desktop));
    }

    private string WriteWorkflow(string executable) => Write("Library/Services/Filee.workflow/Contents/document.wflow",
        new XDocument(new XElement("plist", new XElement("dict", new XElement("key", "COMMAND_STRING"),
            new XElement("string", MacOSPortableIntegration.ConversionCommand(executable))))).ToString());

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
