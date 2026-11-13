// The Engines page's package cards (#867): every engine that works is "Ready", with a line saying where it comes from
// (Filee's download, a copy on this computer, the user's own copy); a copy on the computer that isn't used because
// Filee's download is installed is offered instead; Filee's download is only offered over an older copy; and warnings
// show in the warning colour.

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

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        EngineEnvironment.ForgetSystemCopies();
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
    /// A download service whose engines folder is a folder of the test's, with Filee's download of Pandoc "installed"
    /// there (its marker) and a copy of Pandoc on the PATH.
    /// </summary>
    private (EngineDownloadService Downloads, EnginePackageState Pandoc, string SystemCopy, string Download) PandocInBothPlaces()
    {
        var system = Folder("filee-path-");
        File.WriteAllBytes(Path.Combine(system, EngineEnvironment.ProgramName("pandoc.exe")), [0x4D, 0x5A]);
        Environment.SetEnvironmentVariable("PATH", system + Path.PathSeparator + _path);
        EngineEnvironment.ForgetSystemCopies();

        var root = Folder("filee-engines-");
        var component = new EngineComponent("pandoc", "3.13", "https://example.test/pandoc.zip", new string('a', 64), 1, "zip");
        Directory.CreateDirectory(Path.Combine(root, "pandoc"));
        File.WriteAllText(Path.Combine(root, "pandoc", ".filee-component"), component.Sha256);
        var installer = new EngineInstaller(new HttpClient(), root, new Dictionary<string, EngineComponent> { ["pandoc"] = component });
        var downloads = new EngineDownloadService(AppHost.Get<ConverterCatalog>(), Loc, NullLogger<EngineDownloadService>.Instance, installer);
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
    public void Filees_download_is_only_offered_over_an_older_copy_on_the_computer()
    {
        TestServices.EnsureInitialized("en");
        var state = Downloadable();
        state.OwnCopyFolder = "/usr/local/bin";
        state.FromSystem = true;
        state.Status = EnginePackageStatus.OwnCopy;

        Assert.False(state.OffersInstall);
        Assert.False(state.InstallsOverCopy);
        // The first-run setup and the installer's choices still install it when asked to.
        Assert.True(state.CanInstall);

        state.IsOlderCopy = true;
        Assert.True(state.OffersInstall);
        Assert.True(state.InstallsOverCopy);

        state.Status = EnginePackageStatus.NotInstalled;
        Assert.True(state.OffersInstall);
        Assert.False(state.InstallsOverCopy);
    }

    [AvaloniaFact]
    public void An_unused_copy_on_the_computer_is_described_while_filees_download_is_used()
    {
        TestServices.EnsureInitialized("en");
        var state = Package("calibre");
        state.OtherCopyFolder = "/Applications/calibre.app/Contents/MacOS";

        state.Status = EnginePackageStatus.NotInstalled;
        Assert.False(state.HasOtherCopy);
        Assert.Null(state.OtherCopyText);

        state.Status = EnginePackageStatus.Installed;
        Assert.True(state.HasOtherCopy);
        Assert.Equal("/Applications/calibre.app (Filee's download would be removed)", state.OtherCopyText);
        state.OtherCopyVersion = "9.15";
        Assert.Equal("/Applications/calibre.app · version 9.15 (Filee's download would be removed)", state.OtherCopyText);
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
    public void Installing_filees_copy_over_an_older_one_reads_the_card_again_from_disk()
    {
        TestServices.EnsureInitialized("en");
        var (downloads, pandoc, system, download) = PandocInBothPlaces();
        Assert.Null(downloads.Uninstall(pandoc));
        Assert.Equal(EnginePackageStatus.OwnCopy, pandoc.Status);
        pandoc.IsOlderCopy = true;
        pandoc.CopyVersion = "3.1";
        pandoc.UpdateNote = "Update recommended: this copy is 3.1, Filee is tested with 3.13.";

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
        Assert.True(pandoc.HasOtherCopy);
        Assert.Equal(system, pandoc.OtherCopyFolder);
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
            Pump();
            Assert.Equal([expected, expected], asked);
            Assert.False(Directory.Exists(download));
            Assert.Equal(EnginePackageStatus.OwnCopy, pandoc.Status);
            Assert.False(button.IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == $"On this computer · {system}" && t.IsEffectivelyVisible);
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
        pandoc.UpdateNote = "Update recommended: this copy is 3.1, Filee is tested with 3.13.";
        using var vm = new EnginesPageViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<ConverterCatalog>(), Loc, downloads);
        var window = new Window { Content = new EnginesPage { DataContext = vm }, Width = 1080, Height = 1400 };
        window.Show();
        Pump();
        try
        {
            var note = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == pandoc.UpdateNote);
            Assert.True(window.TryFindResource("WarningBrush", window.ActualThemeVariant, out var warning));
            Assert.Equal(((ISolidColorBrush)warning!).Color, ((ISolidColorBrush)note.Foreground!).Color);

            // Over an up-to-date copy nothing is offered and no download size shows; over an older one both do.
            bool Shown(string? text) => window.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => ReferenceEquals(t.DataContext, pandoc) && t.Text == text && t.IsEffectivelyVisible);
            Assert.False(Shown(pandoc.Sizes));
            bool Offered() => window.GetVisualDescendants().OfType<Button>()
                .Any(b => ReferenceEquals(b.DataContext, pandoc) && Equals(b.Content, "Install Filee's copy") && b.IsEffectivelyVisible);
            Assert.False(Offered());
            pandoc.IsOlderCopy = true;
            Pump();
            Assert.True(Shown(pandoc.Sizes));
            Assert.True(Offered());
        }
        finally
        {
            window.Close();
        }
    }
}
