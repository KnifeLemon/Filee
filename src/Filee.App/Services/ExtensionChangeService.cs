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

    private IReadOnlyList<string> GetWatchRoots()
    {
        var roots = new HashSet<string>(FileSystemPaths.Comparer);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                        roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not enumerate fixed drives for extension change monitoring");
            }
        }

        if (roots.Count == 0)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
                roots.Add(userProfile);
        }

        // Standard user locations
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrWhiteSpace(desktop))
            roots.Add(desktop);

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(docs))
            roots.Add(docs);

        if (!string.IsNullOrWhiteSpace(_platform.DownloadsFolder))
            roots.Add(_platform.DownloadsFolder);

        // User watch folders
        foreach (var rule in _store.Settings.WatchFolders)
        {
            if (!string.IsNullOrWhiteSpace(rule.Folder) && Directory.Exists(rule.Folder))
                roots.Add(rule.Folder);
        }

        return roots.Where(Directory.Exists).ToList();
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

            await candidate.PerformConversionAsync(CancellationToken.None).ConfigureAwait(false);
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
