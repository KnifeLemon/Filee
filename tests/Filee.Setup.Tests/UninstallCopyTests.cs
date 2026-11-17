using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Filee.Setup.Platform;

namespace Filee.Setup.Tests;

/// <summary>Where the uninstaller copies itself before it deletes the install folder, and what the copy is told.</summary>
[SupportedOSPlatform("windows")]
public class UninstallCopyTests
{
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    [Fact]
    public void Without_administrator_rights_the_copy_goes_to_a_new_folder_in_the_users_temp()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The uninstaller is for Windows.");
        using var temp = new TempFolder();
        var paths = SetupPaths.Sandbox(temp.Sub("pc"));
        var exe = temp.Write(@"Filee\uninstall.exe", "uninstaller");

        var copy = Program.CopyUninstaller(exe, paths, elevated: false);
        var folder = Path.GetDirectoryName(copy)!;
        Assert.Equal(paths.Temp, Path.GetDirectoryName(folder));
        // Phase 2 knows it runs from a copy by this name.
        Assert.StartsWith(Program.CopyFolderPrefix, Path.GetFileName(folder));
        Assert.Equal("uninstall.exe", Path.GetFileName(copy));
        Assert.Equal("uninstaller", File.ReadAllText(copy));
        Assert.NotEqual(folder, Path.GetDirectoryName(Program.CopyUninstaller(exe, paths, elevated: false)));
    }

    [Fact]
    public void An_elevated_copy_goes_where_only_administrators_can_change_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && NativeMethods.IsElevated(), "Needs administrator rights, like the CI runner.");
        using var temp = new TempFolder();
        var paths = SetupPaths.Sandbox(temp.Sub("pc"));
        var exe = temp.Write(@"Filee\uninstall.exe", "uninstaller");

        var copy = Program.CopyUninstaller(exe, paths, elevated: true);
        var folder = Path.GetDirectoryName(copy)!;
        Assert.Equal(paths.SystemTemp, Path.GetDirectoryName(folder));
        Assert.StartsWith(Program.CopyFolderPrefix, Path.GetFileName(folder));
        Assert.Equal("uninstaller", File.ReadAllText(copy));
        AssertAdminOnly(new DirectoryInfo(folder).GetAccessControl());
        // The copy and the trash phase 2 makes next to it take the folder's rights.
        AssertAdminOnly(new FileInfo(copy).GetAccessControl(), inherited: true);
        Directory.CreateDirectory(Path.Combine(folder, "trash"));
        AssertAdminOnly(new DirectoryInfo(Path.Combine(folder, "trash")).GetAccessControl(), inherited: true);
    }

    [Fact]
    public void The_admin_only_folder_takes_nothing_from_its_parent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The uninstaller is for Windows.");
        using var temp = new TempFolder();
        // The scratch folder gives this user full control, and the new folder must not inherit it. Without
        // administrator rights the test can still read the rights (the owner's), and delete the empty folder through
        // the parent, which is what the uninstaller's own folder must never allow.
        var folder = temp.Sub(Program.CopyFolderPrefix + "test");
        NativeMethods.CreateNewFolder(folder, Program.AdminOnlyFolder);
        try
        {
            AssertAdminOnly(new DirectoryInfo(folder).GetAccessControl());
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void A_folder_that_is_there_already_is_never_taken_over()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The uninstaller is for Windows.");
        using var temp = new TempFolder();
        var folder = temp.Sub("taken");
        Directory.CreateDirectory(folder);
        const int ErrorAlreadyExists = 183;
        Assert.Equal(ErrorAlreadyExists, Assert.Throws<Win32Exception>(() => NativeMethods.CreateNewFolder(folder, Program.AdminOnlyFolder)).NativeErrorCode);
        Assert.Equal(ErrorAlreadyExists, Assert.Throws<Win32Exception>(() => NativeMethods.CreateNewFolder(folder, null)).NativeErrorCode);
    }

    [Fact]
    public void The_copy_waits_for_every_uninstaller_in_the_install_folder()
    {
        string[] args = ["/SILENT", "/DIR=old", "/LOG=old.log", "/FIRSTPHASE=9", "/UNINSTALL", "/LANG=ko"];
        var child = Program.SecondPhaseArgs(args, @"C:\Program Files\Filee", @"C:\Users\a b\Temp\u.log", [12, 34]).ToArray();
        Assert.Equal(["/SILENT", "/LANG=ko", "/UNINSTALL", @"/DIR=C:\Program Files\Filee", @"/LOG=C:\Users\a b\Temp\u.log", "/FIRSTPHASE=12,34"], child);
        var options = SetupOptions.Parse(child);
        Assert.Equal([12, 34], options.FirstPhase);
        Assert.Equal(@"C:\Program Files\Filee", options.Directory);
    }

    /// <summary>
    /// SYSTEM and administrators may do anything, the owner only read, nobody else anything. The folder takes nothing
    /// from its parent; what is in it (<paramref name="inherited"/>) takes the folder's rights.
    /// </summary>
    private static void AssertAdminOnly(FileSystemSecurity security, bool inherited = false)
    {
        Assert.Equal(!inherited, security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            var who = (SecurityIdentifier)rule.IdentityReference;
            if (who.IsWellKnown(WellKnownSidType.LocalSystemSid) || who.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
            else if (who == OwnerRights)
                Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rule.FileSystemRights);
            else
                Assert.Fail($"Unexpected entry for {who}: {rule.FileSystemRights}");
        }
    }
}
