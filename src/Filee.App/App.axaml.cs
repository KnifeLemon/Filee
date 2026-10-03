// Application start-up: builds services, applies settings, wires global gestures to the donut toolbar.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filee.App;

public partial class App : Application
{
    private readonly List<string> _pendingConvertFiles = [];
    private IDisposable? _convertDebounce;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppHost.Build();
            Start(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var log = AppHost.Get<ILogger<App>>();
        var store = AppHost.Get<UserDataStore>();
        store.Load();

        var loc = AppHost.Get<LocalizationService>();
        loc.Attach(this);
        ApplySettings(store.Settings);

        // Global gestures → donut toolbar
        var triggers = AppHost.Get<TriggerService>();
        var radial = AppHost.Get<RadialController>();
        var platform = AppHost.Get<IPlatformServices>();
        triggers.DragStarted += (_, e) => radial.ShowForDrag(e.X, e.Y);
        triggers.DragEnded += (_, _) => radial.OnDragGestureEnded();
        triggers.SelectionGesture += (_, e) =>
        {
            var files = platform.GetFileManagerSelection();
            if (files.Count > 0)
                radial.ShowForFiles(e.X, e.Y, files);
        };
        triggers.Start();
        log.LogInformation("Filee {Version} started", UpdateService.CurrentVersion);

        store.SettingsChanged += (_, _) => ApplySettings(store.Settings);

        AppHost.Get<TrayService>().Create(this);
        AppHost.Get<WindowService>().EnsureToast();
        AppHost.Get<ConversionService>().FeedbackDue += (_, _) => AppHost.Get<WindowService>().ShowFeedbackNotice();
        AppHost.Get<WatchFolderService>().Start();
        // Once LibreOffice is installed: prepare its profiles while the user isn't converting yet.
        AppHost.Get<EngineDownloadService>().WarmUpInBackground(TimeSpan.FromSeconds(10));

        // Later launches (context menu, Send To, double-clicking the exe) forward their arguments here.
        if (Program.Instance is { } instance)
        {
            instance.ArgumentsReceived += args => Dispatcher.UIThread.Post(() => HandleCommandLine(CommandLine.Parse(args)));
            instance.StartListening();
        }

        desktop.Exit += (_, _) =>
        {
            AppHost.Get<TriggerService>().Dispose();
            AppHost.Get<TrayService>().Dispose();
            AppHost.Get<WatchFolderService>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        };

        HandleCommandLine(Program.Options, firstLaunch: true);
        WatchForUpdates(store);
    }

    /// <summary>
    /// Looks for new releases in the background. A new version shows up at the bottom of the navigation and in the
    /// tray menu; the bottom-right notice appears once per version.
    /// </summary>
    private static void WatchForUpdates(UserDataStore store)
    {
        var updates = AppHost.Get<UpdateService>();
        updates.UpdateFound += (_, version) =>
        {
            if (store.Settings.NotifiedUpdateVersion == version)
                return;
            store.Settings.NotifiedUpdateVersion = version;
            store.SaveSettings();
            AppHost.Get<WindowService>().ShowUpdateNotice(version);
        };
        updates.UpdateFailed += async (_, _) => await AppHost.Get<WindowService>().ShowUpdateFailedAsync();
        updates.StartPeriodicChecks(() => store.Settings.CheckForUpdates, TimeSpan.FromSeconds(20));
    }

    /// <summary>Applies everything derived from settings. Called at start-up and whenever settings are saved.</summary>
    private static void ApplySettings(AppSettings settings)
    {
        // Before the catalog is created (the first time) or re-checked: engines look for the user's own copies first.
        var ownCopiesChanged = !EngineEnvironment.OwnCopies.OrderBy(p => p.Key).SequenceEqual(settings.EngineOwnCopies.OrderBy(p => p.Key));
        if (ownCopiesChanged)
            EngineEnvironment.OwnCopies = new Dictionary<string, string>(settings.EngineOwnCopies);

        var loc = AppHost.Get<LocalizationService>();
        if (loc.NeedsUpdate(settings.Language))
            loc.SetLanguage(settings.Language);

        AppHost.Get<ThemeService>().Apply(settings.Theme, loc.Language);
        AppHost.Get<TriggerService>().Apply(settings);
        AppHost.Get<ConverterCatalog>().Priority = settings.EnginePriority;
        if (ownCopiesChanged)
        {
            AppHost.Get<ConverterCatalog>().Refresh();
            AppHost.Get<EngineDownloadService>().RefreshStatus();
        }
        ApplySystemIntegration(settings, loc);
    }

    /// <summary>Keeps registry entries in sync with settings (idempotent; paths follow updates).</summary>
    private static void ApplySystemIntegration(AppSettings settings, ILocalizer loc)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !OperatingSystem.IsWindows())
            return;
        // When running from the IDE (bin\Debug) don't touch the user's Explorer / startup settings.
        if (!UpdateService.IsReleaseBuild)
            return;
        try
        {
            var platform = AppHost.Get<IPlatformServices>();
            platform.SetStartWithSystem(settings.StartWithSystem, exe);
            platform.SetContextMenu(settings.ContextMenuEnabled, exe, loc["general.context_menu_label"]);
        }
        catch (Exception ex)
        {
            AppHost.Get<ILogger<App>>().LogWarning(ex, "Could not update system integration");
        }
    }

    private void HandleCommandLine(CommandLine options, bool firstLaunch = false)
    {
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
            // Explorer may start one process per selected file: collect them for a moment, then open once.
            _pendingConvertFiles.AddRange(options.ConvertFiles);
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
    /// Options ticked on the installer's task page. Saving applies them (registry entries, see ApplySystemIntegration)
    /// as if they had been switched on the General page.
    /// </summary>
    private static void ApplyInstallerChoices(CommandLine options, UserDataStore store)
    {
        if (options.StartWithWindows is null && options.ContextMenu is null)
            return;
        if (options.StartWithWindows is { } start)
            store.Settings.StartWithSystem = start;
        if (options.ContextMenu is { } menu)
            store.Settings.ContextMenuEnabled = menu;
        store.SaveSettings();
    }

    private void OpenPendingFiles()
    {
        var files = _pendingConvertFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _pendingConvertFiles.Clear();
        var (x, y) = AppHost.Get<TriggerService>().LastCursor;
        if (x == 0 && y == 0 && AppHost.Get<WindowService>().Main is { } main)
            (x, y) = (main.Position.X + 200, main.Position.Y + 200);
        AppHost.Get<RadialController>().ShowForFiles(x, y, files);
    }

}
