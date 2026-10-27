using Avalonia.Threading;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Watching;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

/// <summary>
/// Monitors file extension changes across user-configured watch folders and automatically converts them
/// according to user settings.
/// </summary>
public sealed class ExtensionChangeService : IAsyncDisposable
{
    private readonly UserDataStore _store;
    private readonly WindowService _windowService;
    private readonly IPlatformServices _platform;
    private readonly ILogger<ExtensionChangeService> _log;
    private readonly ExtensionChangeWatcher _watcher;
    private readonly EventHandler _settingsChangedHandler;
    private bool _started;
    private bool _isWatching;
    private List<WatchedFolder> _currentRoots = [];

    public ExtensionChangeService(
        UserDataStore store,
        ConverterCatalog catalog,
        ConversionService conversions,
        WindowService windowService,
        IPlatformServices platform,
        ILogger<ExtensionChangeService> log)
    {
        _store = store;
        _windowService = windowService;
        _platform = platform;
        _log = log;

        _watcher = new ExtensionChangeWatcher(
            () => catalog.CreatePlanner(),
            async (sourcePath, targetFormat, outputPath, ct) =>
            {
                var preset = new Preset { TargetFormat = targetFormat };
                preset.Output.Location = OutputLocation.CustomFolder;
                preset.Output.CustomFolder = Path.GetDirectoryName(outputPath) ?? "";
                preset.Output.Conflict = ConflictPolicy.Overwrite;
                var job = await conversions.RunAsync([sourcePath], preset).ConfigureAwait(false);
                return job is not null && job.Files.Any(f => f.State == FileState.Done);
            },
            platform.MoveToTrash,
            log);

        _watcher.ExtensionChangeDetected += OnExtensionChangeDetectedAsync;
        _settingsChangedHandler = (_, _) => UpdateWatcherState();
    }

    public void Start()
    {
        if (_started)
            return;

        _started = true;
        _store.SettingsChanged += _settingsChangedHandler;
        UpdateWatcherState();
    }

    private void UpdateWatcherState()
    {
        var enabled = _store.Settings.ConvertOnExtensionChange;
        if (!enabled)
        {
            if (_isWatching)
            {
                _watcher.Stop();
                _isWatching = false;
                _currentRoots.Clear();
                _log.LogInformation("Extension change auto-conversion stopped");
            }
            return;
        }

        var newRoots = GetWatchRoots();
        if (_isWatching && RootsEqual(_currentRoots, newRoots))
            return;

        _watcher.Stop();
        if (newRoots.Count > 0)
        {
            _watcher.Start(newRoots);
            _isWatching = true;
            _currentRoots = [.. newRoots];
            _log.LogInformation("Extension change auto-conversion started on {Count} roots", newRoots.Count);
        }
        else
        {
            _isWatching = false;
            _currentRoots.Clear();
            _log.LogInformation("Extension change auto-conversion has no active watch roots");
        }
    }

    private static bool RootsEqual(IReadOnlyList<WatchedFolder> a, IReadOnlyList<WatchedFolder> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Directory, b[i].Directory, FileSystemPaths.Comparison) ||
                a[i].IncludeSubdirectories != b[i].IncludeSubdirectories)
            {
                return false;
            }
        }
        return true;
    }

    private IReadOnlyList<WatchedFolder> GetWatchRoots()
    {
        var result = new List<WatchedFolder>();
        var seen = new HashSet<string>(FileSystemPaths.Comparer);

        // Only watch folders the user explicitly picks in Settings → Watch folders
        foreach (var rule in _store.Settings.WatchFolders)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Folder))
                continue;

            try
            {
                if (Directory.Exists(rule.Folder) && seen.Add(Path.GetFullPath(rule.Folder)))
                {
                    result.Add(new WatchedFolder(rule.Folder, rule.IncludeSubfolders));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _log.LogWarning(ex, "Could not access folder for extension change watching: {Folder}", rule.Folder);
            }
        }

        return result;
    }

    private async Task OnExtensionChangeDetectedAsync(ExtensionChangeCandidate candidate)
    {
        if (!_store.Settings.ConvertOnExtensionChange)
            return;

        if (_store.Settings.ConfirmExtensionChange)
        {
            var confirmed = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                return await _windowService.ConfirmExtensionChangeAsync(
                    candidate.FileName,
                    candidate.SourceFormat.DisplayName,
                    candidate.TargetFormat.DisplayName);
            });

            if (!confirmed)
            {
                _log.LogInformation("User cancelled extension conversion for {File}", candidate.NewPath);
                return;
            }
        }

        _log.LogInformation("Executing extension conversion for {File} from {Source} to {Target}",
            candidate.NewPath, candidate.SourceFormat.Id, candidate.TargetFormat.Id);

        IDisposable? progress = null;
        try
        {
            progress = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                return _windowService.ShowExtensionChangeProgress(
                    candidate.FileName,
                    candidate.TargetFormat.DisplayName);
            });

            var success = await candidate.PerformConversionAsync(CancellationToken.None).ConfigureAwait(false);
            if (!success)
            {
                _log.LogWarning("Extension change conversion for {File} did not complete. Original file preserved.", candidate.NewPath);
            }
        }
        finally
        {
            if (progress is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => progress.Dispose());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _store.SettingsChanged -= _settingsChangedHandler;
        _watcher.Stop();
        _isWatching = false;
        _currentRoots.Clear();
        await _watcher.DisposeAsync();
    }
}
