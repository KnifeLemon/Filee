// Setup runs elevated and deletes inside folders the user can change. A junction there (which any user can make) must
// never take a delete outside the folder. The junctions here are real ones, made with mklink /J.

using System.Diagnostics;
using Filee.Setup.Installation;
using Filee.Setup.Platform;

namespace Filee.Setup.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class LinkSafeDeleteTests
{
    private static void Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), $"mklink /J failed: {process.StandardError.ReadToEnd()}");
    }

    [Fact]
    public void A_junction_inside_the_tree_goes_but_what_it_points_to_stays()
    {
        using var temp = new TempFolder();
        var outside = temp.Write(@"outside\canary.txt", "keep");
        temp.Write(@"tree\a.txt");
        temp.Write(@"tree\sub\b.txt");
        Junction(temp.Sub(@"tree\sub\link"), temp.Sub("outside"));
        var system = new FakeSystem();

        FileSwap.DeleteTree(temp.Sub("tree"), system, new SetupLog(null));

        Assert.False(Directory.Exists(temp.Sub("tree")));
        Assert.Equal("keep", File.ReadAllText(outside));
        Assert.Empty(system.Calls);
    }

    [Fact]
    public void A_folder_that_is_a_junction_loses_only_the_junction()
    {
        using var temp = new TempFolder();
        var outside = temp.Write(@"outside\canary.txt", "keep");
        Junction(temp.Sub("tree"), temp.Sub("outside"));

        FileSwap.DeleteTree(temp.Sub("tree"), new FakeSystem(), new SetupLog(null));

        Assert.False(Path.Exists(temp.Sub("tree")));
        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public void Nothing_is_deleted_in_a_folder_reached_through_a_junction()
    {
        using var temp = new TempFolder();
        var outside = temp.Write(@"outside\current\canary.txt", "keep");
        Junction(temp.Sub("root"), temp.Sub("outside"));

        FileSwap.DeleteTree(temp.Sub(@"root\current"), new FakeSystem(), new SetupLog(null), userFolder: true);

        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public void A_file_whose_folder_was_swapped_for_a_junction_is_not_deleted()
    {
        using var temp = new TempFolder();
        var root = LinkSafeDelete.RealFolder(Directory.CreateDirectory(temp.Sub("tree")).FullName)!;
        var outside = temp.Write(@"outside\canary.txt", "keep");
        // As if a folder inside the tree was swapped for a junction after the tree was checked.
        Junction(temp.Sub(@"tree\sub"), temp.Sub("outside"));

        Assert.Equal(LinkSafeDelete.Outcome.Outside, LinkSafeDelete.Entry(temp.Sub(@"tree\sub\canary.txt"), root));
        Assert.Equal("keep", File.ReadAllText(outside));
        Assert.Equal(LinkSafeDelete.Outcome.Deleted, LinkSafeDelete.Entry(temp.Sub(@"tree\sub"), root));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Read_only_files_go_and_a_file_in_use_waits_for_the_restart_only_outside_the_users_folders()
    {
        using var temp = new TempFolder();
        File.SetAttributes(temp.Write(@"tree\read-only.txt"), FileAttributes.ReadOnly);
        var busy = temp.Write(@"tree\busy.txt");
        var system = new FakeSystem();
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            FileSwap.DeleteTree(temp.Sub("tree"), system, new SetupLog(null));
            Assert.False(File.Exists(temp.Sub(@"tree\read-only.txt")));
            Assert.Contains($"delete-at-restart {busy}", system.Calls);

            var users = new FakeSystem();
            FileSwap.DeleteTree(temp.Sub("tree"), users, new SetupLog(null), userFolder: true);
            Assert.DoesNotContain(users.Calls, call => call.StartsWith("delete-at-restart", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Filee_1_1_removal_never_follows_a_junction_planted_in_its_folder()
    {
        using var bench = new SetupBench();
        var root = VelopackInstall.Root(bench.Paths);
        bench.Temp.Write(Path.Combine(Path.GetRelativePath(bench.Temp.Path, root), "Update.exe"));
        bench.Temp.Write(Path.Combine(Path.GetRelativePath(bench.Temp.Path, root), @"current\Filee.exe"));
        var canary = bench.Temp.Write(@"admin-only\canary.txt", "keep");
        Junction(Path.Combine(root, @"current\j"), bench.Temp.Sub("admin-only"));

        Assert.True(bench.Install().Succeeded);

        Assert.False(Directory.Exists(Path.Combine(root, "current")));
        Assert.Equal("keep", File.ReadAllText(canary));
    }
}
