// Application start-up: builds services, applies settings, wires global gestures to the donut toolbar.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Filee.Core.Web;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filee.App;

public partial class App : Application
{
    private readonly List<string> _pendingConvertFiles = [];
    private IDisposable? _convertDebounce;
    private SelectionLaunches? _launches;
    private bool _selectionPending;

    /// <summary>Forwarded launches and empty selections can come in bursts: a few lines a minute are enough.</summary>
    private static readonly LogThrottle Throttle = new(TimeSpan.FromSeconds(30));

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        SingleSelectionGuard.Register();
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try { await PortableFirstStart.RunAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Program.Log.LogError(ex, "Portable first-start preparation failed");
                desktop.Shutdown(1);
                return;
            }
            // macOS preloads settings to configure its Dock; reload after a possible import.
            if (PortableMode.IsEnabled) Program.PreloadedStore?.Load();
            AppHost.Build(preloadedStore: Program.PreloadedStore);
            Start(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Ends Filee as the tray's Quit does.</summary>
    public static void Quit() => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var log = AppHost.Get<ILogger<App>>();
        // macOS: "Convert with Filee" in Finder's right-click menu. Registered first, so a service picked while
        // Filee wasn't running is delivered as soon as it starts. A request without files Filee can find (another
        // file manager put its selection on the pasteboard in a form Filee doesn't read) is logged with what it held.
        if (OperatingSystem.IsMacOS())
            Filee.Platform.MacOS.MacOSServicesProvider.Register(
                files => Dispatcher.UIThread.Post(() => HandleCommandLine(CommandLine.Parse(["--convert", .. files]))),
                types => log.LogWarning("Convert with Filee was picked, but the request named no existing file; it held: {Types}",
                    types.Count == 0 ? "nothing" : string.Join(", ", types)));
        // Exceptions in UI event handlers end Filee; write them down first (they are not marked handled).
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            log.LogCritical(e.Exception, "Unhandled exception on the UI thread");
        var watchdog = new UiWatchdog(action => Dispatcher.UIThread.Post(action), AppHost.Get<ILogger<UiWatchdog>>());
        watchdog.Start();
        var store = AppHost.Get<UserDataStore>();
        if (Program.PreloadedStore is null)
            store.Load();
        // Before anything downloads: the proxy from Settings, read for each request so a change there applies at once.
        ProxySetting.Use(() => store.Settings.Proxy);
        if (!string.IsNullOrWhiteSpace(store.Settings.Proxy))
        {
            if (ProxySetting.TryParse(store.Settings.Proxy, out var proxy) && proxy is not null)
                log.LogInformation("Downloads go through the proxy {Proxy}", ProxySetting.Describe(proxy));
            else
                log.LogWarning("The proxy in the settings isn't a proxy address; downloads use the system's");
        }

        var loc = AppHost.Get<LocalizationService>();
        loc.Attach(this);
        ApplySettings(store.Settings);
        // Window zoom: set once here, not with the other settings, since the zoom saves itself (UiZoom) without
        // raising SettingsChanged.
        UiZoom.Configure(store.Settings.UiScale, scale =>
        {
            try
            {
                store.SaveUiScale(scale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not remember the window zoom");
            }
        });
        // Every engine's availability, checked once off the UI thread: the first donut or drop otherwise starts
        // every engine's check (FFmpeg -version, folder searches) on the UI thread and freezes it meanwhile.
        var catalog = AppHost.Get<ConverterCatalog>();
        _ = catalog.CheckInBackgroundAsync();
        Filee.Engines.Translation.TranslatorConverter.StatusChanged += () =>
            Dispatcher.UIThread.Post(() =>
            {
                catalog.Refresh();
                _ = catalog.CheckInBackgroundAsync();
            });

        // Global gestures → donut toolbar
        var triggers = AppHost.Get<TriggerService>();
        var radial = AppHost.Get<RadialController>();
        var platform = AppHost.Get<IPlatformServices>();
        triggers.DragStarted += (_, e) => radial.ShowForDrag(e.X, e.Y, OpensBack(e));
        triggers.DragEnded += (_, _) => radial.OnDragGestureEnded();
        triggers.SelectionGesture += async (_, e) =>
        {
            if (_selectionPending || store.Settings.Paused)
                return;
            _selectionPending = true;
            try
            {
                // A clipboard shortcut converts the clipboard only: with nothing convertible on it, it does nothing.
                // Falling back to the file manager's selection would convert whatever is selected in a window in
                // the background, from any app, for a shortcut set to Everywhere.
                if (e.Gesture is { Clipboard: true })
                {
                    if (platform.SupportsClipboard)
                        ShowForClipboard(radial, platform, e.X, e.Y, OpensBack(e), log);
                    return;
                }
                var files = OperatingSystem.IsMacOS()
                    ? await Task.Run(platform.GetFileManagerSelection) : platform.GetFileManagerSelection();
                if (files.Count > 0 && !store.Settings.Paused)
                    radial.ShowForFiles(e.X, e.Y, files, backFace: OpensBack(e));
                else if (files.Count == 0 && Throttle.TryEnter("empty-selection", out var held))
                    log.LogInformation("The selection shortcut found no selected files in {Window}{Elevation}{Held}",
                        platform.DescribeForegroundWindow() is { Length: > 0 } window ? window : "the foreground window",
                        Program.Elevation.ShellCannotReachFilee ? " (Filee runs as administrator and can't read Explorer)" : "",
                        LogThrottle.HeldText(held));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(ex, "Could not read the file manager selection");
                AppHost.Get<WindowService>().ShowMain("triggers");
                if (AppHost.Get<WindowService>().Main is { } window)
                    _ = AppHost.Get<WindowService>().MessageAsync(window, ex.Message);
            }
            finally
            {
                _selectionPending = false;
            }
        };
        triggers.ClickTriggered += async (_, e) =>
        {
            if (_selectionPending || store.Settings.Paused)
                return;
            try
            {
                var files = platform.IsFileManagerAt(e.X, e.Y)
                    ? (OperatingSystem.IsMacOS() ? await Task.Run(platform.GetFileManagerSelection) : platform.GetFileManagerSelection())
                    : (IReadOnlyList<string>)[];
                if (files.Count > 0)
                {
                    radial.ShowForFiles(e.X, e.Y, files);
                }
                else
                {
                    radial.ShowForConnect(e.X, e.Y);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(ex, "Could not check file manager selection on click");
                radial.ShowForConnect(e.X, e.Y);
            }
        };
        triggers.Start();
        log.LogInformation("Filee {Version} started", UpdateService.CurrentVersion);
        // Which way the transparent windows (donut, toast) are drawn, for reports of one that isn't see-through (#269).
        if (OperatingSystem.IsWindows())
        {
            log.LogInformation("{Rendering}", WindowsRendering.Describe(Environment.GetEnvironmentVariable(WindowsRendering.Variable)));
            TransparentWindows.Watch(log);
        }
        else if (Program.LinuxDrawsInSoftware)
            log.LogInformation("Rendering: software ({Variable})", WindowsRendering.Variable);
        // macOS: ask for Accessibility with the system's own alert when the user opened Filee (not at sign-in) and
        // a drag gesture is on, once ever (see ShouldAskForPermission). The home page keeps a reminder until it is
        // given. Asked once the main window is up: asked earlier, the alert ends up behind it.
        if (OperatingSystem.IsMacOS() && !Program.Options.Background
            && platform is Filee.Platform.MacOS.MacOSPlatformServices mac
            && TriggerService.ShouldAskForPermission(store.Settings, triggers.NeedsPermission))
            DispatcherTimer.RunOnce(() =>
            {
                if (!OperatingSystem.IsMacOS() || !TriggerService.ShouldAskForPermission(store.Settings, triggers.NeedsPermission))
                    return;
                store.Settings.AccessibilityPromptShown = true;
                store.SaveSettings();
                mac.RequestAccessibilityPermission();
            }, TimeSpan.FromSeconds(1.5));

        store.SettingsChanged += (_, _) => ApplySettings(store.Settings);

        AppHost.Get<TrayService>().Create(this);
        AppHost.Get<WindowService>().EnsureToast();
        AppHost.Get<ConversionService>().FeedbackDue += (_, _) => AppHost.Get<WindowService>().ShowFeedbackNotice();
        AppHost.Get<WatchFolderService>().Start();
        // Originals of files from a browser that a crash kept from being deleted ("Save only the converted file").
        _ = Task.Run(() => Filee.Core.Web.WebDrop.DeleteStale(Filee.Core.Web.WebDrop.DefaultWorkRoot, TimeSpan.FromDays(1)));
        // Likewise the work folders of conversions that never got to clean up after themselves.
        _ = Task.Run(() => JobQueue.DeleteStaleWorkFolders(JobQueue.DefaultWorkRoot, TimeSpan.FromDays(1)));
        // Leftover update workspaces from cancelled or failed in-app updates (Linux only).
        if (OperatingSystem.IsLinux())
            _ = Task.Run(() => UpdateService.DeleteStaleWorkspaces());
        // Java and H2Orestart from an earlier LibreOffice download are unused now (HWP is read by Filee itself).
        _ = AppHost.Get<EngineDownloadService>().RemoveRetiredAsync();
        // Once LibreOffice is installed: prepare its profiles while the user isn't converting yet.
        AppHost.Get<EngineDownloadService>().WarmUpInBackground(TimeSpan.FromSeconds(10));

        // Later launches (context menu, Send To, double-clicking the exe) forward their arguments here.
        if (Program.Instance is { } instance)
            ListenForLaterLaunches(instance, log);
        Program.InstanceAcquired += again => ListenForLaterLaunches(again, log);

        desktop.Exit += (_, _) =>
        {
            UiZoom.Flush();
            watchdog.Dispose();
            AppHost.Get<MemoryTrimmer>().Dispose();
            AppHost.Get<TriggerService>().Dispose();
            AppHost.Get<TrayService>().Dispose();
            AppHost.Get<WatchFolderService>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            Filee.App.ViewModels.OllamaModelManagerViewModel.StopFileeStartedServer();
        };

        // Before HandleCommandLine marks the first run complete: a fresh install gets no slides (an update by the
        // installer does, even when its settings are gone). Without the main window (sign-in, the Explorer menu) they
        // wait until the user opens Filee, unless the installer asked for them.
        AppHost.Get<WhatsNewService>().CheckAtStartup(
            background: Program.Options.Background && !Program.Options.ShowSettings && !Program.Options.WhatsNew
                        || Program.Options.ConvertFiles.Count > 0,
            updated: Program.Options.WhatsNew, updatedFrom: Program.Options.UpdatedFrom);
        HandleCommandLine(Program.Options, firstLaunch: true);
        if (OperatingSystem.IsMacOS())
            this.TryGetFeature<IActivatableLifetime>()?.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Clicking the Dock icon or opening Filee again in Finder brings the window back, as Mac apps do
                        // (#232), also while it only runs in the background.
                        AppHost.Get<WindowService>().ShowMain(store.Settings.FirstRunCompleted ? null : "home");
                        if (!store.Settings.ShowInDock && !platform.TrySetShowInDock(false))
                            log.LogWarning("Could not restore the macOS Dock visibility policy after reopening Filee");
                    });
            };
        WatchForUpdates(store);
        // Started to the tray (at sign-in): no job or window would ask for a trim, however long Filee waits there (#957).
        if (AppHost.Get<WindowService>().Main is null)
            AppHost.Get<MemoryTrimmer>().Request();
        // Filee runs as administrator while Explorer doesn't (an installer started "as administrator" started it):
        // say so once, next to the tray, and on the Home page for as long as it lasts.
        if (AppHost.Get<ElevationService>().ShellCannotReachFilee)
            DispatcherTimer.RunOnce(() => AppHost.Get<WindowService>().ShowElevationNotice(), TimeSpan.FromSeconds(2));
    }

    /// <summary>The gesture is set to open the donut on its back face.</summary>
    private static bool OpensBack(GestureEventArgs e) => e.Gesture is { Face: DonutFace.Back };

    /// <summary>
    /// Opens the donut for the files on the clipboard, else for its picture (a screenshot), which is saved when it
    /// converts, like a picture from a browser, without keeping the original. The results go back on the clipboard.
    /// With neither on the clipboard, nothing opens.
    /// </summary>
    private static void ShowForClipboard(RadialController radial, IPlatformServices platform, int x, int y, bool backFace, ILogger log)
    {
        ClipboardContent? content;
        try
        {
            content = platform.ReadClipboard();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Could not read the clipboard");
            return;
        }
        if (content is { Files.Count: > 0 })
        {
            radial.ShowForFiles(x, y, content.Files, toClipboard: true, backFace: backFace);
            return;
        }
        if (content?.Picture is not { Length: > 0 } picture)
        {
            if (Throttle.TryEnter("empty-clipboard", out var held))
                log.LogInformation("Nothing to convert on the clipboard{Held}", LogThrottle.HeldText(held));
            return;
        }
        var name = $"Clipboard {DateTime.Now:yyyy-MM-dd HH.mm.ss}.{content.PictureExtension ?? "png"}";
        var web = new Filee.Core.Web.WebDrop([Filee.Core.Web.WebDropItem.FromBytes(name, picture)], Filee.Core.Web.WebDrop.DefaultWorkRoot)
        {
            SaveOnlyConverted = true,
        };
        log.LogInformation("The clipboard shortcut opens the donut for a {Extension} picture of {Bytes} bytes",
            content.PictureExtension, picture.Length);
        radial.ShowForFiles(x, y, web.Placeholders, web: web, toClipboard: true, backFace: backFace);
    }

    private void ListenForLaterLaunches(SingleInstance instance, ILogger log)
    {
        instance.ArgumentsReceived += args =>
        {
            if (Throttle.TryEnter("forwarded", out var held))
                log.LogInformation("A later launch handed over {Count} argument(s){Held}", args.Length, LogThrottle.HeldText(held));
            Dispatcher.UIThread.Post(() => HandleCommandLine(CommandLine.Parse(args), forwarded: true));
        };
        instance.ListeningFailed += ex =>
        {
            if (Throttle.TryEnter("listening", out var held))
                log.LogWarning(ex, "A later launch couldn't be taken, trying the next{Held}", LogThrottle.HeldText(held));
        };
        instance.StartListening();
    }

    /// <summary>
    /// Looks for new releases in the background. A new version shows up at the bottom of the navigation and in the
    /// tray menu; the bottom-right notice appears once per version.
    /// </summary>
    private static void WatchForUpdates(UserDataStore store)
    {
        var updates = AppHost.Get<UpdateService>();
        updates.RequestApplicationShutdown = Quit;
        // Required: the card every time Filee starts (UpdateFound fires once per run). Recommended: once per version.
        // Optional: no card, the sidebar button only. Frequent releases no longer ask every day.
        updates.UpdateFound += (_, version) =>
        {
            if (updates.Level == UpdateLevel.Optional
                || updates.Level == UpdateLevel.Recommended && store.Settings.NotifiedUpdateVersion == version)
                return;
            store.Settings.NotifiedUpdateVersion = version;
            store.SaveSettings();
            AppHost.Get<WindowService>().ShowUpdateNotice(version);
        };
        updates.UpdateFailed += async (_, _) => await AppHost.Get<WindowService>().ShowUpdateFailedAsync();
        // The last run's update failed after Filee closed (Linux): the installer rolled back and started this copy.
        if (updates.CheckPendingFailure(store.Directory))
            Dispatcher.UIThread.Post(async () => await AppHost.Get<WindowService>().ShowUpdateRolledBackAsync(updates.LastFailureLogPath));
        updates.StartPeriodicChecks(() => store.Settings.CheckForUpdates, TimeSpan.FromSeconds(20));
        DispatcherTimer.RunOnce(() => NotifyEngineUpdates(store), TimeSpan.FromSeconds(25));
    }

    /// <summary>
    /// Engines the user downloaded that are older than the versions this Filee pins (it was updated since): one
    /// notice per new version, only to people who installed that engine, and only while update checks are on.
    /// </summary>
    private static void NotifyEngineUpdates(UserDataStore store)
    {
        if (!store.Settings.CheckForUpdates)
            return;
        var downloads = AppHost.Get<EngineDownloadService>();
        downloads.RefreshStatus();
        var outdated = downloads.Outdated;
        if (!outdated.Any(p => store.Settings.NotifiedEngineVersions.GetValueOrDefault(p.Package.Id) != p.Version))
            return;
        foreach (var package in outdated)
            store.Settings.NotifiedEngineVersions[package.Package.Id] = package.Version ?? "";
        store.SaveSettings();
        AppHost.Get<WindowService>().ShowEngineUpdateNotice(outdated);
    }

    /// <summary>Applies everything derived from settings. Called at start-up and whenever settings are saved.</summary>
    private static void ApplySettings(AppSettings settings)
    {
        // Before the catalog is created (the first time) or re-checked: engines look for the user's own copies first.
        var ownCopiesChanged = !EngineEnvironment.OwnCopies.OrderBy(p => p.Key).SequenceEqual(settings.EngineOwnCopies.OrderBy(p => p.Key));
        if (ownCopiesChanged)
            EngineEnvironment.OwnCopies = new Dictionary<string, string>(settings.EngineOwnCopies);
        var routesChanged = EngineEnvironment.LibreOfficeForSlides != settings.LibreOfficeForSlides
                            || EngineEnvironment.LibreOfficeForDocuments != settings.LibreOfficeForDocuments
                            || EngineEnvironment.LibreOfficeForSheets != settings.LibreOfficeForSheets
                            || EngineEnvironment.FileeRenderer != settings.FileeRenderer;
        EngineEnvironment.LibreOfficeForSlides = settings.LibreOfficeForSlides;
        EngineEnvironment.LibreOfficeForDocuments = settings.LibreOfficeForDocuments;
        EngineEnvironment.LibreOfficeForSheets = settings.LibreOfficeForSheets;
        EngineEnvironment.FileeRenderer = settings.FileeRenderer;

        var loc = AppHost.Get<LocalizationService>();
        if (loc.NeedsUpdate(settings.Language))
            loc.SetLanguage(settings.Language);

        var custom = CustomTheme.Load(AppHost.Get<UserDataStore>().Directory, AppHost.Get<ILogger<App>>());
        AppHost.Get<ThemeService>().Apply(settings.Theme, loc.Language, custom);
        AppHost.Get<TriggerService>().Apply(settings);
        AppHost.Get<ConverterCatalog>().Priority = settings.EnginePriority;
        if (ownCopiesChanged || routesChanged)
        {
            AppHost.Get<ConverterCatalog>().Refresh();
            AppHost.Get<EngineDownloadService>().RefreshStatus();
        }
        AppHost.Get<SystemIntegrationService>().Apply(settings);
    }

    private void HandleCommandLine(CommandLine options, bool firstLaunch = false, bool forwarded = false)
    {
        // Pairs with "A later launch handed over …": the request reached the UI thread too.
        if (forwarded && Throttle.TryEnter("forwarded-handled", out var held))
            AppHost.Get<ILogger<App>>().LogInformation("Handling a later launch: {Request}{Held}",
                options.Quit ? "quit" : options.ConvertFiles.Count > 0 ? $"convert {options.ConvertFiles.Count} file(s)" : "show Filee",
                LogThrottle.HeldText(held));
        if (options.Quit)
        {
            // The installer is about to replace or remove Filee's files (Program.RunInstallerCommand waits for this).
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return;
        }
        // The Windows 11 Explorer menu hands over huge selections in a temp file; its paths are in ConvertFiles now.
        options.DeleteListFile();
        if (options.ConvertFiles.Count > 0)
        {
            // Explorer may start one process per selected file: the first one brings the selection, or (when it can't
            // be read) they are collected for a moment and later ones join the donut (see OpenPendingFiles).
            _launches ??= new SelectionLaunches(ReadSelectionForLaunch);
            var added = _launches.Arrived(options.ConvertFiles);
            if (added.Count == 0)
                return;
            _pendingConvertFiles.AddRange(added);
            _convertDebounce?.Dispose();
            _convertDebounce = DispatcherTimer.RunOnce(OpenPendingFiles, TimeSpan.FromMilliseconds(350));
            return;
        }

        var store = AppHost.Get<UserDataStore>();
        ApplyInstallerChoices(options, store);
        if (!options.Background || options.ShowSettings || !firstLaunch)
            AppHost.Get<WindowService>().ShowMain(store.Settings.FirstRunCompleted ? null : "home");

        if (options.InstallEngines is { } engines)
        {
            // Picked on the installer's engine page: download them now. Picking none answers the first-run question.
            if (engines.Count > 0)
                AppHost.Get<WindowService>().ShowEngineSetup(engines);
        }
        else if (!store.Settings.FirstRunCompleted)
        {
            // The installer only contains the small engines: offer the large ones on first run (portable zip, or an
            // installation that skipped the engine page).
            if (AppHost.Get<EngineDownloadService>().Packages.Any(p => !p.IsInstalled))
                AppHost.Get<WindowService>().ShowEngineSetup();
        }
        if (!store.Settings.FirstRunCompleted)
        {
            store.Settings.FirstRunCompleted = true;
            store.SaveSettings();
        }
    }

    /// <summary>
    /// Options ticked in Setup, and the language picked there. Saving applies them (SystemIntegrationService, the
    /// language) as if they had been switched on the General page.
    /// </summary>
    private static void ApplyInstallerChoices(CommandLine options, UserDataStore store)
    {
        // Only Filee's languages; anything else keeps the setting (normally "auto", Windows' language).
        var language = LocalizationService.Languages.FirstOrDefault(l => l.Code.Equals(options.Language, StringComparison.OrdinalIgnoreCase)).Code;
        if (options.StartWithWindows is null && options.ContextMenu is null && language is null)
            return;
        if (options.StartWithWindows is { } start)
            store.Settings.StartWithSystem = start;
        if (options.ContextMenu is { } menu)
            store.Settings.ContextMenuEnabled = menu;
        if (language is not null)
            store.Settings.Language = language;
        store.SaveSettings();
    }

    private void OpenPendingFiles()
    {
        var files = _pendingConvertFiles.Distinct(FileSystemPaths.Comparer).ToList();
        _pendingConvertFiles.Clear();
        var radial = AppHost.Get<RadialController>();
        // A launch that came after a longer gap belongs with the ones the open donut has: replacing them lost those files.
        if (radial.TryAddFiles(files))
            return;
        var (x, y) = AppHost.Get<TriggerService>().LastCursor;
        if (x == 0 && y == 0 && AppHost.Get<WindowService>().Main is { } main)
            (x, y) = (main.Position.X + 200, main.Position.Y + 200);
        radial.ShowForFiles(x, y, files, forAnotherProcess: true);
    }

    /// <summary>The selection a launch for one file may be part of. Only Windows' menu entry starts one process per file.</summary>
    private static IReadOnlyList<string> ReadSelectionForLaunch()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var log = AppHost.Get<ILogger<App>>();
        try
        {
            var selection = AppHost.Get<IPlatformServices>().GetFileManagerSelection();
            log.LogInformation("A launch for one file found {Count} selected file(s) in {Window}", selection.Count,
                AppHost.Get<IPlatformServices>().DescribeForegroundWindow());
            return selection;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Could not read the selection for a launch");
            return [];
        }
    }

}
