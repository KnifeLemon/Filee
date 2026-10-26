using Avalonia.Threading;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Watching;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

/// <summary>
/// Monitors file extension changes across the filesystem and automatically converts them
/// according to user settings.
/// </summary>
public sealed class ExtensionChangeService : IAsyncDisposable
{
    private readonly UserDataStore _store;
    private readonly WindowService _windowService;
    private readonly IPlatformServices _platform;
    private readonly ILogger<ExtensionChangeService> _log;
    private readonly ExtensionChangeWatcher _watcher;
    private bool _isWatching;

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
            log);

        _watcher.ExtensionChangeDetected += OnExtensionChangeDetectedAsync;
    }

    public void Start()
    {
        UpdateWatcherState();
        _store.SettingsChanged += (_, _) => UpdateWatcherState();
    }

    private void UpdateWatcherState()
    {
        var enabled = _store.Settings.ConvertOnExtensionChange;
        if (enabled && !_isWatching)
        {
            var roots = GetWatchRoots();
            _watcher.Start(roots);
            _isWatching = true;
            _log.LogInformation("Extension change auto-conversion started on {Count} roots", roots.Count);
        }
        else if (!enabled && _isWatching)
        {
            _watcher.Stop();
            _isWatching = false;
            _log.LogInformation("Extension change auto-conversion stopped");
        }
    }

    private IReadOnlyList<WatchedFolder> GetWatchRoots()
    {
        var result = new List<WatchedFolder>();
        var seen = new HashSet<string>(FileSystemPaths.Comparer);

        void AddFolder(string? path, bool includeSubdirectories = true)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (Directory.Exists(path) && seen.Add(Path.GetFullPath(path)))
                {
                    result.Add(new WatchedFolder(path, includeSubdirectories));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _log.LogWarning(ex, "Could not access folder for extension change watching: {Folder}", path);
            }
        }

        // Standard user locations (Desktop, Documents, Downloads, Pictures, Music, Videos)
        AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddFolder(_platform.DownloadsFolder);
        AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            if (OperatingSystem.IsMacOS())
            {
                var movies = Path.Combine(userProfile, "Movies");
                AddFolder(movies);
            }

            // On Linux and macOS, watch user profile root non-recursively to avoid inotify exhaustion
            // and permission issues on hidden system/auth directories (.ssh, .gnupg, .cache).
            AddFolder(userProfile, includeSubdirectories: OperatingSystem.IsWindows());
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    {
                        AddFolder(drive.RootDirectory.FullName, includeSubdirectories: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not enumerate fixed drives for extension change monitoring");
            }
        }

        // User watch folders
        foreach (var rule in _store.Settings.WatchFolders)
        {
            if (!string.IsNullOrWhiteSpace(rule.Folder))
                AddFolder(rule.Folder, includeSubdirectories: rule.IncludeSubfolders);
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
                _log.LogWarning("Extension change conversion for {File} did not complete (permission decline or error). Original file preserved.", candidate.NewPath);
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
        _store.SettingsChanged -= (_, _) => UpdateWatcherState();
        await _watcher.DisposeAsync();
    }
}
