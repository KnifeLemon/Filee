using System.Text;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class FolderIconsTests
{
    [Fact]
    public void A_second_icon_replaces_a_hidden_system_desktop_ini()
    {
        if (!OperatingSystem.IsWindows())
            return; // the record and its attributes are a Windows thing (the tool is greyed out elsewhere)
        using var dir = new TempDir();
        var ini = Path.Combine(dir.Path, "desktop.ini");
        File.WriteAllText(ini, "[ShellClassInfo]\r\nIconResource=old.ico,0\r\n", Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

        FolderIcons.RecordIcon(dir.Path, "new.ico");

        Assert.Contains("new.ico", File.ReadAllText(ini, Encoding.Unicode));
        var after = File.GetAttributes(ini);
        Assert.True(after.HasFlag(FileAttributes.Hidden));
        Assert.True(after.HasFlag(FileAttributes.System));
    }

    [Fact]
    public void An_icon_living_outside_the_folder_is_recorded_by_its_absolute_path()
    {
        if (!OperatingSystem.IsWindows())
            return; // the record and its attributes are a Windows thing (the tool is greyed out elsewhere)
        using var dir = new TempDir();
        FolderIcons.RecordIcon(dir.Path, @"D:\Art\pick.ico");
        var text = File.ReadAllText(Path.Combine(dir.Path, "desktop.ini"), Encoding.Unicode);
        // The reference goes into the record as it is: nothing is copied, Explorer resolves the absolute path.
        Assert.StartsWith("[.ShellClassInfo]", text);
        Assert.Contains("IconResource=D:\\Art\\pick.ico,0", text);
    }

    [Fact]
    public void A_fresh_folder_gets_a_hidden_system_record_and_a_system_folder()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var dir = new TempDir();
        FolderIcons.RecordIcon(dir.Path, "new.ico");
        var ini = Path.Combine(dir.Path, "desktop.ini");
        Assert.True(File.Exists(ini));
        var text = File.ReadAllText(ini, Encoding.Unicode);
        Assert.Contains("new.ico", text);
        // Explorer honours only the dotted header: "[ShellClassInfo]" without the dot is ignored and the folder
        // keeps its plain icon (lab-verified, 2026-11-11).
        Assert.StartsWith("[.ShellClassInfo]", text);
        var after = File.GetAttributes(ini);
        Assert.True(after.HasFlag(FileAttributes.Hidden));
        Assert.True(after.HasFlag(FileAttributes.System));
        Assert.True(File.GetAttributes(dir.Path).HasFlag(FileAttributes.System));
    }
}
