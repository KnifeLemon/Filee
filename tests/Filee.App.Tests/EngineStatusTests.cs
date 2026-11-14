// The Engines page's package cards (#867): every engine that works is "Ready", with a line saying where it comes from
// (Filee's download, a copy on this computer, the user's own copy); a copy on the computer that isn't used because
// Filee's download is installed is offered instead, once it runs, and checked again before the download goes; Filee's
// download is only offered over a copy that is older or whose version can't be read, once checked; versions are read in
// the background; and warnings show in the warning colour.

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

/// <summary>Runs alone: changes PATH and the search for copies on the computer, which other tests read.</summary>
[CollectionDefinition("Engine copies", DisableParallelization = true)]
public sealed class EngineCopiesCollection;

[Collection("Engine copies")]
public sealed class EngineStatusTests : IDisposable
{
    private readonly string _path = Environment.GetEnvironmentVariable("PATH") ?? "";
    private readonly List<string> _folders = [];

    /// <summary>What the copy of Pandoc on the PATH says its version is; null when it doesn't tell or start.</summary>
    private volatile Version? _systemVersion;

    /// <summary>
    /// The copy on the PATH starts even when it tells no version (as an FFmpeg nightly or Upscayl does).
    /// </summary>
    private volatile bool _systemRuns;

    /// <summary>
    /// Holds the next version check of the copy until set, to land it late; <see cref="_held"/> is set once it waits.
    /// </summary>
    private ManualResetEventSlim? _hold;

    private readonly ManualResetEventSlim _held = new();

    /// <summary>Another folder whose Pandoc starts too (a copy the user didn't confirm).</summary>
    private volatile string? _alsoRuns;

    /// <summary>Holds the next start of the copy (<see cref="EngineDownloadService.CopyRuns"/>) until set.</summary>
    private ManualResetEventSlim? _holdStart;

    /// <summary>Starting or asking the copy on the PATH throws (something unforeseen).</summary>
    private volatile bool _throws;

    /// <summary>How often the copy on the PATH was asked its version.</summary>
    private int _reads;

    private bool _usedCatalog;

    /// <summary>The version of Pandoc Filee is tested with.</summary>
    private static Version Tested => CopyVersions.Expected("pandoc")!;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        EngineEnvironment.ForgetSystemCopies();
        // Removing a download refreshed the shared converter list while the fake Pandoc was on the PATH.
        if (_usedCatalog)
            AppHost.Get<ConverterCatalog>().Refresh();
        foreach (var folder in _folders)
            try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static ILocalizer Loc => AppHost.Get<ILocalizer>();

    private static EnginePackageState Package(string id) => new(EngineDownloads.Packages.Single(p => p.Id == id), Loc);

    private static EnginePackageState Downloadable()
    {
        var state = EngineDownloads.Packages.Where(EngineDownloads.CanDownload).Select(p => new EnginePackageState(p, Loc)).FirstOrDefault();
        Assert.SkipWhen(state is null, "Filee downloads no engine on this platform");
        return state!;
    }

    private string Folder(string prefix)
    {
        var folder = Directory.CreateTempSubdirectory(prefix).FullName;
        _folders.Add(folder);
        return folder;
    }

    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    /// Runs the UI thread until <paramref name="task"/> is done (it continues there), then gives its result.
    /// </summary>
    private static T Finish<T>(Task<T> task)
    {
        WaitFor(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Lets the background version checks land.</summary>
    private static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Thread.Sleep(10);
            Pump();
        }
        Assert.True(condition());
    }

    /// <summary>
    /// A download service whose engines folder is a folder of the test's, with Filee's download of Pandoc "installed"
    /// there (its marker) and a copy of Pandoc on the PATH, whose version is <see cref="_systemVersion"/> (the tested
    /// one to begin with).
    /// </summary>
    private (EngineDownloadService Downloads, EnginePackageState Pandoc, string SystemCopy, string Download) PandocInBothPlaces()
    {
        _systemVersion = Tested;
        _usedCatalog = true;
        var system = Folder("filee-path-");
        File.WriteAllBytes(Path.Combine(system, EngineEnvironment.ProgramName("pandoc.exe")), [0x4D, 0x5A]);
        Environment.SetEnvironmentVariable("PATH", system + Path.PathSeparator + _path);
        EngineEnvironment.ForgetSystemCopies();

        var root = Folder("filee-engines-");
        var component = new EngineComponent("pandoc", "3.13", "https://example.test/pandoc.zip", new string('a', 64), 1, "zip");
        Directory.CreateDirectory(Path.Combine(root, "pandoc"));
        File.WriteAllText(Path.Combine(root, "pandoc", ".filee-component"), component.Sha256);
        var installer = new EngineInstaller(new HttpClient(), root, new Dictionary<string, EngineComponent> { ["pandoc"] = component });
        var downloads = new EngineDownloadService(AppHost.Get<ConverterCatalog>(), Loc, NullLogger<EngineDownloadService>.Instance, installer,
            (_, folder) =>
            {
                if (folder != system && folder != _alsoRuns)
                    return null;
                if (_throws)
                    throw new IOException("the folder can't be read");
                Interlocked.Increment(ref _reads);
                var version = _systemVersion;
                if (Interlocked.Exchange(ref _hold, null) is { } hold)
                {
                    _held.Set();
                    hold.Wait(TimeSpan.FromSeconds(10));
                }
                return version;
            },
            (_, folder) =>
            {
                Interlocked.Exchange(ref _holdStart, null)?.Wait(TimeSpan.FromSeconds(10));
                if (_throws)
                    throw new UnauthorizedAccessException("the folder can't be read");
                return folder == _alsoRuns || (folder == system && (_systemVersion is not null || _systemRuns));
            });
        return (downloads, downloads.Packages.Single(p => p.Package.Id == "pandoc"), system, Path.Combine(root, "pandoc"));
    }

    [AvaloniaFact]
    public void Every_working_engine_is_ready_and_says_where_it_comes_from()
    {
        TestServices.EnsureInitialized("en");
        var state = Downloadable();

        state.Status = EnginePackageStatus.Installed;
        Assert.Equal("Ready", state.StatusText);
        Assert.Equal($"Filee's download · version {state.Version} · {EnginePackageState.FormatBytes(state.Package.InstalledSize)} on disk", state.SourceText);

        state.OwnCopyFolder = "/opt/homebrew/bin";
        state.FromSystem = true;
        state.Status = EnginePackageStatus.OwnCopy;
        Assert.Equal("Ready", state.StatusText);
        Assert.Equal("On this computer · /opt/homebrew/bin", state.SourceText);
        state.CopyVersion = "9.0";
        Assert.Equal("On this computer · /opt/homebrew/bin · version 9.0", state.SourceText);

        state.FromSystem = false;
        state.OwnCopyFolder = "/Applications/calibre.app/Contents/MacOS";
        Assert.Equal("Your copy · /Applications/calibre.app · version 9.0", state.SourceText);

        state.Status = EnginePackageStatus.NotInstalled;
        Assert.Equal("Not installed", state.StatusText);
        Assert.Null(state.SourceText);
    }

    [AvaloniaFact]
    public void An_outdated_download_says_so_in_its_own_line_not_in_the_status()
    {
        TestServices.EnsureInitialized("en");
        var state = Downloadable();
        state.Status = EnginePackageStatus.Installed;
        Assert.Null(state.UpdateText);

        state.IsOutdated = true;
        Assert.Equal("Ready", state.StatusText);
        Assert.Equal($"New version {state.Version}", state.UpdateText);
        // The version the card would name is the new one, not the one on disk: it is left out of the source line.
        Assert.Equal($"Filee's download · {EnginePackageState.FormatBytes(state.Package.InstalledSize)} on disk", state.SourceText);
    }

    [AvaloniaFact]
    public void Every_ready_engine_has_the_same_green_tag_and_amber_when_its_card_recommends_an_update()
    {
        TestServices.EnsureInitialized("en");
        var state = Downloadable();
        (bool Ok, bool Warn, bool Danger) Tone() => (state.StatusIsOk, state.StatusIsWarn, state.StatusIsDanger);

        state.Status = EnginePackageStatus.Installed;
        Assert.Equal((true, false, false), Tone());
        state.IsOutdated = true; // Filee's download has a new version
        Assert.Equal((false, true, false), Tone());
        state.IsOutdated = false;

        // A copy on the computer and the user's own copy are the same Ready, in the same colour.
        state.OwnCopyFolder = "/opt/homebrew/bin";
        state.FromSystem = true;
        state.Status = EnginePackageStatus.OwnCopy;
        Assert.Equal((true, false, false), Tone());
        state.IsOlderCopy = true; // older than the version Filee is tested with
        Assert.Equal((false, true, false), Tone());
        state.FromSystem = false;
        Assert.Equal((false, true, false), Tone());
        state.IsOlderCopy = false;
        Assert.Equal((true, false, false), Tone());

        state.Status = EnginePackageStatus.Failed;
        Assert.Equal((false, false, true), Tone());
        foreach (var busy in new[] { EnginePackageStatus.NotInstalled, EnginePackageStatus.Queued, EnginePackageStatus.Downloading, EnginePackageStatus.Unpacking })
        {
            state.Status = busy;
            Assert.Equal((false, false, false), Tone());
        }
    }

    [AvaloniaFact]
    public void Filees_download_is_offered_over_a_copy_on_the_computer_only_when_older_or_its_version_unknown()
    {
        TestServices.EnsureInitialized("en");
        var state = Downloadable();
        state.OwnCopyFolder = "/usr/local/bin";
        state.FromSystem = true;
        state.Status = EnginePackageStatus.OwnCopy;

        // Until its version check comes back, nothing is offered (no Install shown for a moment).
        Assert.False(state.OffersInstall);
        Assert.False(state.InstallsOverCopy);
        // The first-run setup and the installer's choices still install it when asked to.
        Assert.True(state.CanInstall);

        // Checked, and its version can't be told: it may not start at all, so Filee's copy is offered.
        state.CopyVersionChecked = true;
        Assert.True(state.OffersInstall);
        Assert.True(state.InstallsOverCopy);

        // Checked and up to date: simply used.
        state.CopyVersion = state.Version ?? "99.0";
        Assert.False(state.OffersInstall);
        Assert.False(state.InstallsOverCopy);

        state.IsOlderCopy = true;
        Assert.True(state.OffersInstall);
        Assert.True(state.InstallsOverCopy);

        state.Status = EnginePackageStatus.NotInstalled;
        Assert.True(state.OffersInstall);
        Assert.False(state.InstallsOverCopy);
    }

    [AvaloniaFact]
    public void An_unused_copy_on_the_computer_is_offered_once_it_runs_while_filees_download_is_used()
    {
        TestServices.EnsureInitialized("en");
        var state = Package("calibre");
        state.OtherCopyFolder = "/Applications/calibre.app/Contents/MacOS";
        state.OtherCopyRuns = true;
        state.OtherCopyVersion = "9.15";

        state.Status = EnginePackageStatus.NotInstalled;
        Assert.False(state.HasOtherCopy);
        Assert.Null(state.OtherCopyText);

        state.Status = EnginePackageStatus.Installed;
        Assert.True(state.HasOtherCopy);
        Assert.Equal("/Applications/calibre.app · version 9.15 (Filee's download would be removed)", state.OtherCopyText);

        // One that starts without telling its version (an FFmpeg nightly, Upscayl) is offered too.
        state.OtherCopyVersion = null;
        Assert.True(state.HasOtherCopy);
        Assert.Equal("/Applications/calibre.app (Filee's download would be removed)", state.OtherCopyText);

        // One that didn't start (or hasn't been tried yet) isn't: it may not work.
        state.OtherCopyRuns = false;
        Assert.False(state.HasOtherCopy);
        Assert.Null(state.OtherCopyText);
    }

    [AvaloniaFact]
    public void No_copy_is_offered_in_place_of_a_download_with_more_parts()
    {
        TestServices.EnsureInitialized("en");
        // As on Windows: LibreOffice with H2Orestart and Java, which LibreOffice on the PC doesn't have.
        var state = new EnginePackageState(new EnginePackage("libreoffice", ["libreoffice", "h2orestart", "jre"], 1, ["libreoffice"]), Loc)
        {
            OtherCopyFolder = @"C:\Program Files\LibreOffice\program",
            OtherCopyVersion = "26.2",
            OtherCopyRuns = true,
            Status = EnginePackageStatus.Installed,
        };
        Assert.False(state.HasOtherCopy);
        Assert.Null(state.OtherCopyText);
    }

    [Theory]
    [InlineData("/Applications/calibre.app/Contents/MacOS", "/Applications/calibre.app")]
    [InlineData("/Applications/LibreOffice.app/Contents/MacOS/", "/Applications/LibreOffice.app")]
    [InlineData("/opt/homebrew/bin", "/opt/homebrew/bin")]
    [InlineData(@"C:\Program Files\gs\gs10.08.0\bin", @"C:\Program Files\gs\gs10.08.0\bin")]
    public void A_macos_apps_program_folder_is_shown_as_the_app(string folder, string shown) =>
        Assert.Equal(shown, EnginePackageState.DisplayFolder(folder));

    [AvaloniaFact]
    public void Install_guidance_only_shows_while_the_engine_is_missing()
    {
        TestServices.EnsureInitialized("en");
        var state = EngineDownloads.Packages.Where(p => !EngineDownloads.CanDownload(p)).Select(p => new EnginePackageState(p, Loc)).FirstOrDefault();
        Assert.SkipWhen(state is null, "Filee downloads every engine on this platform");

        state!.Status = EnginePackageStatus.NotInstalled;
        Assert.False(string.IsNullOrEmpty(state.InstallGuidance));
        state.OwnCopyFolder = "/opt/homebrew/bin";
        state.FromSystem = true;
        state.Status = EnginePackageStatus.OwnCopy;
        Assert.Null(state.InstallGuidance);
    }

    [AvaloniaFact]
    public void Removing_filees_download_hands_over_to_the_copy_on_the_computer()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, system, download) = PandocInBothPlaces();

        Assert.Equal(EnginePackageStatus.Installed, pandoc.Status);
        Assert.Equal(system, pandoc.OtherCopyFolder);
        WaitFor(() => pandoc.HasOtherCopy);
        Assert.Equal(Tested.ToString(2), pandoc.OtherCopyVersion);

        // A copy that starts but tells no version Filee can read (an FFmpeg nightly, Upscayl) is offered.
        _systemVersion = null;
        _systemRuns = true;
        downloads.RefreshStatus();
        WaitFor(() => pandoc.OtherCopyVersion is null);
        Assert.True(pandoc.HasOtherCopy);

        Assert.Null(downloads.Uninstall(pandoc));
        Assert.False(Directory.Exists(download));
        Assert.Equal(EnginePackageStatus.OwnCopy, pandoc.Status);
        Assert.True(pandoc.FromSystem);
        Assert.Equal(system, pandoc.OwnCopyFolder);
        Assert.Equal("Ready", pandoc.StatusText);
        Assert.False(pandoc.HasOtherCopy);
    }

    [AvaloniaFact]
    public void Removing_filees_download_that_the_path_links_to_doesnt_hand_over_to_the_link()
    {
        TestServices.EnsureInitialized("en");
        _usedCatalog = true;
        var root = Folder("filee-engines-");
        var component = new EngineComponent("pandoc", "3.13", "https://example.test/pandoc.zip", new string('a', 64), 1, "zip");
        var download = Directory.CreateDirectory(Path.Combine(root, "pandoc")).FullName;
        File.WriteAllText(Path.Combine(download, ".filee-component"), component.Sha256);
        File.WriteAllBytes(Path.Combine(download, EngineEnvironment.ProgramName("pandoc.exe")), [0x4D, 0x5A]);
        // The user put Filee's Pandoc on the PATH through a link.
        var link = Path.Combine(Folder("filee-path-"), "pandoc-link");
        try
        {
            Directory.CreateSymbolicLink(link, download);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Making a link needs Developer Mode or an administrator here.");
        }
        Environment.SetEnvironmentVariable("PATH", link + Path.PathSeparator + _path);
        EngineEnvironment.ForgetSystemCopies();
        var installer = new EngineInstaller(new HttpClient(), root, new Dictionary<string, EngineComponent> { ["pandoc"] = component });
        var downloads = new EngineDownloadService(AppHost.Get<ConverterCatalog>(), Loc, NullLogger<EngineDownloadService>.Instance, installer,
            (_, _) => null, (_, _) => false);
        var pandoc = downloads.Packages.Single(p => p.Package.Id == "pandoc");
        Assert.Equal(EnginePackageStatus.Installed, pandoc.Status);
        Assert.Null(pandoc.OtherCopyFolder);

        // The link leads nowhere now: it isn't the copy that takes over (one of this computer's own may be).
        Assert.Null(downloads.Uninstall(pandoc));
        Assert.NotEqual(link, pandoc.OwnCopyFolder);
    }

    [AvaloniaFact]
    public void Installing_filees_copy_over_an_older_one_reads_the_card_again_from_disk()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, system, download) = PandocInBothPlaces();
        _systemVersion = new Version(1, 0);
        Assert.Null(downloads.Uninstall(pandoc));
        Assert.Equal(EnginePackageStatus.OwnCopy, pandoc.Status);
        WaitFor(() => pandoc.IsOlderCopy);
        Assert.NotNull(pandoc.UpdateNote);

        // The download puts Filee's copy (its marker) in place, as the installer would.
        downloads.InstallPackage = (_, _, _) =>
        {
            Directory.CreateDirectory(download);
            File.WriteAllText(Path.Combine(download, ".filee-component"), new string('a', 64));
            return Task.CompletedTask;
        };
        var install = downloads.InstallAsync(pandoc);
        for (var i = 0; i < 500 && !install.IsCompleted; i++)
        {
            Thread.Sleep(10);
            Pump();
        }
        Assert.True(install.IsCompletedSuccessfully);

        Assert.Equal(EnginePackageStatus.Installed, pandoc.Status);
        Assert.Null(pandoc.UpdateNote);
        Assert.Null(pandoc.CopyVersion);
        Assert.Null(pandoc.OwnCopyFolder);
        Assert.False(pandoc.IsOutdated);
        Assert.StartsWith("Filee's download", pandoc.SourceText);
        Assert.Equal(system, pandoc.OtherCopyFolder);
        WaitFor(() => pandoc.HasOtherCopy);
    }

    [AvaloniaFact]
    public void A_version_check_that_fails_leaves_the_version_unknown_and_the_card_checked()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, _, _) = PandocInBothPlaces();
        _throws = true;

        // The copy on the computer is used now, and asking its version throws: the version is unknown, so Filee's
        // download is offered over it.
        Assert.Null(downloads.Uninstall(pandoc));
        WaitFor(() => pandoc.CopyVersionChecked);
        Assert.Equal(EnginePackageStatus.OwnCopy, pandoc.Status);
        Assert.Null(pandoc.CopyVersion);
        Assert.False(pandoc.IsOlderCopy);
        Assert.True(pandoc.OffersInstall);
    }

    [AvaloniaFact]
    public void Versions_are_read_in_the_background_and_kept_while_the_card_is_read_again()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, _, _) = PandocInBothPlaces();
        _systemVersion = new Version(1, 0);
        Assert.Null(downloads.Uninstall(pandoc)); // the copy on the PATH is used now

        WaitFor(() => pandoc.CopyVersion == "1.0");
        Assert.True(pandoc.IsOlderCopy);
        Assert.Equal(Loc.Format("engines.update_recommended", "1.0", Tested.ToString(2)), pandoc.UpdateNote);
        Assert.True(pandoc.OffersInstall);

        // Reading the card again (Check again, a settings change) doesn't blank what is known about the same copy.
        downloads.RefreshStatus();
        Assert.Equal("1.0", pandoc.CopyVersion);
        Assert.True(pandoc.IsOlderCopy);
        Assert.NotNull(pandoc.UpdateNote);

        // The copy was updated in place: the note and the offer go once its new version is read.
        _systemVersion = Tested;
        downloads.RefreshStatus();
        WaitFor(() => pandoc.CopyVersion == Tested.ToString(2));
        Assert.False(pandoc.IsOlderCopy);
        Assert.Null(pandoc.UpdateNote);
        Assert.False(pandoc.OffersInstall);

        // A check that was slow to come back doesn't overwrite what the card read after it.
        _systemVersion = new Version(1, 0);
        // The checks of the reads before have all asked their version (the count stays put), so the hold is taken by
        // the next read's check.
        int asked;
        do
        {
            asked = Volatile.Read(ref _reads);
            for (var i = 0; i < 10; i++)
            {
                Thread.Sleep(10);
                Pump();
            }
        }
        while (Volatile.Read(ref _reads) != asked);
        using var hold = new ManualResetEventSlim();
        Volatile.Write(ref _hold, hold);
        downloads.RefreshStatus();
        Assert.True(_held.Wait(TimeSpan.FromSeconds(5))); // that check read 1.0 and waits
        _systemVersion = Tested;
        downloads.RefreshStatus();
        WaitFor(() => pandoc.CopyVersion == Tested.ToString(2));
        hold.Set();
        for (var i = 0; i < 50; i++)
        {
            Thread.Sleep(10);
            Pump();
        }
        Assert.Equal(Tested.ToString(2), pandoc.CopyVersion);
        Assert.False(pandoc.IsOlderCopy);

        // It no longer starts: no version, nothing said about it being older, and Filee's copy is offered.
        _systemVersion = null;
        downloads.RefreshStatus();
        WaitFor(() => pandoc.CopyVersion is null);
        Assert.Equal("On this computer · " + pandoc.OwnCopyFolder, pandoc.SourceText);
        Assert.False(pandoc.IsOlderCopy);
        Assert.True(pandoc.OffersInstall);
    }

    [AvaloniaFact]
    public void Nothing_is_offered_over_a_new_copy_until_its_version_check_comes_back()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, _, _) = PandocInBothPlaces();
        WaitFor(() => pandoc.HasOtherCopy);
        // The copy on the PATH tells no version: once checked, Filee's copy is offered over it.
        _systemVersion = null;
        _systemRuns = true;
        using var hold = new ManualResetEventSlim();
        Volatile.Write(ref _hold, hold);
        Assert.Null(downloads.Uninstall(pandoc)); // the copy on the PATH is used now; its check is held

        Assert.True(_held.Wait(TimeSpan.FromSeconds(5)));
        Pump();
        Assert.Equal(EnginePackageStatus.OwnCopy, pandoc.Status);
        Assert.False(pandoc.CopyVersionChecked);
        Assert.False(pandoc.OffersInstall);

        hold.Set();
        WaitFor(() => pandoc.CopyVersionChecked);
        Assert.Null(pandoc.CopyVersion);
        Assert.True(pandoc.OffersInstall);
        Assert.True(pandoc.InstallsOverCopy);

        // Another copy comes first now (installed meanwhile, found by Check again): what was known about the first one
        // doesn't carry over, so nothing is offered until its own check comes back.
        var another = Folder("filee-path-");
        File.WriteAllBytes(Path.Combine(another, EngineEnvironment.ProgramName("pandoc.exe")), [0x4D, 0x5A]);
        _alsoRuns = another;
        Environment.SetEnvironmentVariable("PATH", another + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        EngineEnvironment.ForgetSystemCopies();
        using var second = new ManualResetEventSlim();
        _held.Reset();
        Volatile.Write(ref _hold, second);
        downloads.RefreshStatus();
        Assert.True(_held.Wait(TimeSpan.FromSeconds(5)));
        Pump();
        Assert.Equal(another, pandoc.OwnCopyFolder);
        Assert.False(pandoc.OffersInstall);
        second.Set();
        WaitFor(() => pandoc.CopyVersionChecked);
        Assert.True(pandoc.OffersInstall);
    }

    [AvaloniaFact]
    public void Filees_download_is_kept_when_the_copy_on_the_computer_no_longer_runs_or_is_gone()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, system, download) = PandocInBothPlaces();
        WaitFor(() => pandoc.HasOtherCopy);

        // The card shows another copy than the one confirmed (it was read again while the question was open).
        var confirmed = Folder("filee-path-");
        File.WriteAllBytes(Path.Combine(confirmed, EngineEnvironment.ProgramName("pandoc.exe")), [0x4D, 0x5A]);
        _alsoRuns = confirmed;
        Assert.Equal("engines.other_copy_gone", Finish(downloads.UseOtherCopyAsync(pandoc, confirmed)));
        Assert.True(Directory.Exists(download));
        WaitFor(() => pandoc.HasOtherCopy);

        // It stopped starting since it was found.
        _systemVersion = null;
        Assert.Equal("engines.other_copy_gone", Finish(downloads.UseOtherCopyAsync(pandoc, system)));
        Assert.True(Directory.Exists(download));
        Assert.Equal(EnginePackageStatus.Installed, pandoc.Status);
        WaitFor(() => !pandoc.HasOtherCopy);

        // One that doesn't start isn't asked its version as well (one that hangs would cost a second time-out).
        var reads = Volatile.Read(ref _reads);
        downloads.RefreshStatus();
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(10);
            Pump();
        }
        Assert.Equal(reads, Volatile.Read(ref _reads));

        // It was uninstalled since it was found (the search is cached until Check again).
        _systemVersion = Tested;
        downloads.RefreshStatus();
        WaitFor(() => pandoc.HasOtherCopy);
        File.Delete(Path.Combine(system, EngineEnvironment.ProgramName("pandoc.exe")));
        Assert.Equal("engines.other_copy_gone", Finish(downloads.UseOtherCopyAsync(pandoc, system)));
        Assert.True(Directory.Exists(download));
        Assert.Equal(EnginePackageStatus.Installed, pandoc.Status);
        // Searched again: not offered any more (a Pandoc of this computer's own may be found instead).
        Assert.NotEqual(system, pandoc.OtherCopyFolder);
        Assert.False(pandoc.HasOtherCopy);
        Assert.NotEqual("engines.other_copy_gone", Loc["engines.other_copy_gone"]);
    }

    [AvaloniaFact]
    public void Use_this_one_instead_asks_first_then_removes_filees_download()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, system, download) = PandocInBothPlaces();
        using var vm = new EnginesPageViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<ConverterCatalog>(), Loc, downloads);
        var page = new EnginesPage { DataContext = vm };
        var asked = new List<string>();
        var answer = false;
        page.ConfirmAsync = (_, message) =>
        {
            asked.Add(message);
            return Task.FromResult(answer);
        };
        var window = new Window { Content = page, Width = 1080, Height = 1400 };
        window.Show();
        WaitFor(() => pandoc.HasOtherCopy);
        Pump();
        try
        {
            var button = window.GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.DataContext, pandoc) && Equals(b.Content, "Use this one instead"));
            Assert.True(button.IsEffectivelyVisible);
            var expected = $"Use the copy in {system} and remove Filee's download of {pandoc.Name}? You can download it again later.";

            // "Cancel" leaves everything as it was.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.Equal([expected], asked);
            Assert.True(Directory.Exists(download));
            Assert.Equal(EnginePackageStatus.Installed, pandoc.Status);

            answer = true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => !Directory.Exists(download));
            Pump();
            Assert.Equal([expected, expected], asked);
            Assert.False(Directory.Exists(download));
            Assert.Equal(EnginePackageStatus.OwnCopy, pandoc.Status);
            Assert.False(button.IsEffectivelyVisible);
            WaitFor(() => pandoc.CopyVersion is not null);
            Pump();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == $"On this computer · {system} · version {Tested.ToString(2)}" && t.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Warnings_are_in_the_warning_colour_and_filees_copy_shows_its_size_where_offered()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, _, _) = PandocInBothPlaces();
        Assert.Null(downloads.Uninstall(pandoc)); // the copy on the PATH is used now
        using var vm = new EnginesPageViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<ConverterCatalog>(), Loc, downloads);
        var window = new Window { Content = new EnginesPage { DataContext = vm }, Width = 1080, Height = 1400 };
        window.Show();
        WaitFor(() => pandoc.CopyVersion == Tested.ToString(2));
        Pump();
        try
        {
            // Over an up-to-date copy nothing is offered and no download size shows; over an older one both do.
            bool Shown(string? text) => window.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => ReferenceEquals(t.DataContext, pandoc) && t.Text == text && t.IsEffectivelyVisible);
            Assert.False(Shown(pandoc.Sizes));
            bool Offered() => window.GetVisualDescendants().OfType<Button>()
                .Any(b => ReferenceEquals(b.DataContext, pandoc) && Equals(b.Content, "Install Filee's copy") && b.IsEffectivelyVisible);
            Assert.False(Offered());

            _systemVersion = new Version(1, 0);
            downloads.RefreshStatus();
            WaitFor(() => pandoc.IsOlderCopy);
            Pump();
            Assert.True(Shown(pandoc.Sizes));
            Assert.True(Offered());
            var note = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == pandoc.UpdateNote && t.IsEffectivelyVisible);
            Assert.True(window.TryFindResource("WarningBrush", window.ActualThemeVariant, out var warning));
            Assert.Equal(((ISolidColorBrush)warning!).Color, ((ISolidColorBrush)note.Foreground!).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_ready_engine_has_the_dot_and_offers_another_copy_and_its_update_with_its_size()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, _, download) = PandocInBothPlaces();
        using var vm = new EnginesPageViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<ConverterCatalog>(), Loc, downloads);
        var window = new Window { Content = new EnginesPage { DataContext = vm }, Width = 1080, Height = 1400 };
        window.Show();
        Pump();
        try
        {
            bool Dot(string brush = "SuccessBrush") => window.GetVisualDescendants().OfType<Border>()
                .Any(b => ReferenceEquals(b.DataContext, pandoc) && b.Width == 10 && b.IsEffectivelyVisible
                          && window.TryFindResource(brush, window.ActualThemeVariant, out var colour) && Equals(b.Background, colour));
            bool Tag(string tone) => window.GetVisualDescendants().OfType<Border>()
                .Any(b => ReferenceEquals(b.DataContext, pandoc) && b.Classes.Contains("status") && b.Classes.Contains(tone));
            bool Offers(string text) => window.GetVisualDescendants().OfType<Button>()
                .Any(b => ReferenceEquals(b.DataContext, pandoc) && Equals(b.Content, text) && b.IsEffectivelyVisible);
            bool Shown(string? text) => window.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => ReferenceEquals(t.DataContext, pandoc) && t.Text == text && t.IsEffectivelyVisible);

            // Filee's download, up to date: Ready in green, no size, another copy can be picked.
            Assert.True(Dot());
            Assert.True(Tag("ok"));
            Assert.False(Dot("WarningBrush"));
            Assert.True(Offers("Use another copy…"));
            Assert.False(Offers("Use my copy…"));
            Assert.False(Shown(pandoc.Sizes));

            // An older download: the update is offered with its size.
            File.WriteAllText(Path.Combine(download, ".filee-component"), new string('b', 64));
            downloads.RefreshStatus();
            Pump();
            Assert.True(pandoc.CanUpdate);
            // Still Ready, in amber: its card recommends the update.
            Assert.Equal("Ready", pandoc.StatusText);
            Assert.True(Dot("WarningBrush"));
            Assert.False(Dot());
            Assert.True(Tag("warn"));
            Assert.False(Tag("ok"));
            Assert.True(Shown(pandoc.UpdateText));
            Assert.True(Shown(pandoc.Sizes));

            // Missing (this computer may have a Pandoc of its own, so the card is told): no dot, the size, and "Use my
            // copy…".
            pandoc.Status = EnginePackageStatus.NotInstalled;
            Pump();
            Assert.False(Dot());
            Assert.False(Dot("WarningBrush"));
            Assert.False(Tag("ok") || Tag("warn"));
            Assert.True(Offers("Use my copy…"));
            Assert.False(Offers("Use another copy…"));
            Assert.True(Shown(pandoc.Sizes));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_second_click_or_remove_while_the_copy_is_started_again_says_nothing_wrong()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, system, download) = PandocInBothPlaces();
        WaitFor(() => pandoc.HasOtherCopy);

        // Clicked twice: the second waits for nothing and says nothing; the first switches.
        var first = downloads.UseOtherCopyAsync(pandoc, system);
        var second = downloads.UseOtherCopyAsync(pandoc, system);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Null(second.Result);
        Assert.Null(Finish(first));
        Assert.False(Directory.Exists(download));
        Assert.Equal(EnginePackageStatus.OwnCopy, pandoc.Status);

        // Removed on the same card while the copy was being started: nothing to switch from, and no message.
        Directory.CreateDirectory(download);
        File.WriteAllText(Path.Combine(download, ".filee-component"), new string('a', 64));
        downloads.RefreshStatus();
        WaitFor(() => pandoc.HasOtherCopy);
        using var hold = new ManualResetEventSlim();
        Volatile.Write(ref _holdStart, hold);
        var switching = downloads.UseOtherCopyAsync(pandoc, system);
        Assert.Null(downloads.Uninstall(pandoc));
        hold.Set();
        Assert.Null(Finish(switching));
        Assert.Equal(EnginePackageStatus.OwnCopy, pandoc.Status);
    }
}
