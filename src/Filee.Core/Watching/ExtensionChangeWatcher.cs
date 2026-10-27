// Detects when a user renames a file to a different extension (e.g. xyz.png -> xyz.pdf),
// validates the file signature, checks that a conversion route exists, waits for OS locks to settle,
// and safely converts the file while avoiding infinite loops.

using System.Collections.Concurrent;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Filee.Core.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Watching;

/// <summary>Folder to monitor for file extension changes.</summary>
public sealed record WatchedFolder(string Directory, bool IncludeSubdirectories = true);

/// <summary>Details of a detected file extension change candidate ready for conversion.</summary>
public sealed class ExtensionChangeCandidate
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public required FileFormat SourceFormat { get; init; }
    public required FileFormat TargetFormat { get; init; }
    public required string FileName { get; init; }
    public required Func<CancellationToken, Task<bool>> PerformConversionAsync { get; init; }
}

/// <summary>
/// Cross-platform watcher that monitors directory roots for file extension changes.
/// </summary>
public sealed class ExtensionChangeWatcher : IAsyncDisposable
{
    private static readonly TimeSpan DefaultSettleTimeout = TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 3 : 5);
    private static readonly TimeSpan InProgressTtl = TimeSpan.FromSeconds(20);

    private readonly Func<RoutePlanner> _createPlanner;
    private readonly Func<string, string, string, CancellationToken, Task<bool>> _convertFile;
    private readonly Func<string, TrashRefusal?>? _moveToTrash;
    private readonly ILogger _log;
    private readonly TimeSpan _settleTimeout;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _inProgressOperations = new(FileSystemPaths.Comparer);
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <summary>
    /// Raised when a valid extension change is detected and settled.
    /// Handlers can inspect the candidate and either trigger conversion directly or prompt the user.
    /// </summary>
    public event Func<ExtensionChangeCandidate, Task>? ExtensionChangeDetected;

    /// <param name="createPlanner">Factory for route planner to test whether conversion is possible.</param>
    /// <param name="convertFile">Delegate: (sourceFile, targetFormat, outputFile, ct) -> bool success.</param>
    /// <param name="moveToTrash">Optional delegate to move the original file to Recycle Bin / Trash when replacing.</param>
    /// <param name="log">Logger instance.</param>
    /// <param name="settleTimeout">Maximum time to wait for the renamed file to unlock and settle.</param>
    public ExtensionChangeWatcher(
        Func<RoutePlanner> createPlanner,
        Func<string, string, string, CancellationToken, Task<bool>> convertFile,
        Func<string, TrashRefusal?>? moveToTrash = null,
        ILogger? log = null,
        TimeSpan? settleTimeout = null)
    {
        _createPlanner = createPlanner;
        _convertFile = convertFile;
        _moveToTrash = moveToTrash;
        _log = log ?? NullLogger.Instance;
        _settleTimeout = settleTimeout ?? DefaultSettleTimeout;
    }

    /// <summary>Marks a path as actively modified by Filee so filesystem events on it are ignored.</summary>
    public void MarkInProgress(string path)
    {
        _inProgressOperations[path] = DateTimeOffset.UtcNow.Add(InProgressTtl);
    }

    /// <summary>Unmarks a path from the in-progress list.</summary>
    public void UnmarkInProgress(string path)
    {
        _inProgressOperations.TryRemove(path, out _);
    }

    /// <summary>True if the path is currently being modified by Filee or recently was.</summary>
    public bool IsInProgress(string path)
    {
        PruneExpiredInProgress();
        return _inProgressOperations.ContainsKey(path);
    }

    private void PruneExpiredInProgress()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (p, expiry) in _inProgressOperations)
        {
            if (expiry < now)
                _inProgressOperations.TryRemove(p, out _);
        }
    }

    /// <summary>Starts watching the given folder targets.</summary>
    public void Start(IEnumerable<WatchedFolder> targets)
    {
        lock (_lock)
        {
            Stop();
            var sorted = targets
                .GroupBy(t => t.Directory, FileSystemPaths.Comparer)
                .Select(g => g.First())
                .OrderBy(t => t.Directory.Length)
                .ToList();

            foreach (var target in sorted)
            {
                if (!Directory.Exists(target.Directory))
                    continue;

                // If an active watcher already covers this folder recursively, skip redundant child watcher
                if (target.IncludeSubdirectories && _watchers.Any(w => w.IncludeSubdirectories && IsSubpath(target.Directory, w.Path)))
                    continue;

                try
                {
                    var watcher = new FileSystemWatcher(target.Directory)
                    {
                        IncludeSubdirectories = target.IncludeSubdirectories,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Renamed += OnRenamed;
                    watcher.Error += (_, e) =>
                    {
                        _log.LogWarning(e.GetException(), "Filesystem watcher error on {Directory}", target.Directory);
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                    _log.LogInformation("Watching extension changes in {Directory} (recursive: {Recursive})",
                        target.Directory, target.IncludeSubdirectories);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
                {
                    _log.LogWarning(ex, "Could not start extension watcher on {Directory}", target.Directory);
                }
            }
        }
    }

    /// <summary>Starts watching the given directory paths recursively.</summary>
    public void Start(IEnumerable<string> directories) =>
        Start(directories.Select(d => new WatchedFolder(d, true)));

    private static bool IsSubpath(string path, string parent)
    {
        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(parent);
        if (!fullParent.EndsWith(Path.DirectorySeparatorChar) && !fullParent.EndsWith(Path.AltDirectorySeparatorChar))
            fullParent += Path.DirectorySeparatorChar;

        return fullPath.StartsWith(fullParent, FileSystemPaths.Comparison);
    }

    /// <summary>Stops all active directory watchers.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            foreach (var w in _watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch
                {
                    // Ignore disposal errors on teardown
                }
            }
            _watchers.Clear();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleRenameAsync(e.OldFullPath, e.FullPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error handling extension rename from {Old} to {New}", e.OldFullPath, e.FullPath);
            }
        });
    }

    /// <summary>
    /// Processes a rename event. Can also be called directly in tests.
    /// Returns the candidate if valid, or null if ignored.
    /// </summary>
    public async Task<ExtensionChangeCandidate?> HandleRenameAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return null;

        if (ShouldIgnorePath(oldPath) || ShouldIgnorePath(newPath))
            return null;

        if (IsInProgress(oldPath) || IsInProgress(newPath))
            return null;

        var oldExt = FormatRegistry.ExtensionOf(oldPath);
        var newExt = FormatRegistry.ExtensionOf(newPath);

        if (string.IsNullOrWhiteSpace(oldExt) || string.IsNullOrWhiteSpace(newExt))
            return null;

        if (string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase))
            return null;

        var sourceFormat = FormatRegistry.FindByExtension(oldExt);
        var targetFormat = FormatRegistry.FindByExtension(newExt);

        if (sourceFormat is null || targetFormat is null)
            return null;

        if (string.Equals(sourceFormat.Id, targetFormat.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!targetFormat.Writable)
            return null;

        // Check if a conversion route exists between source and target format
        var planner = _createPlanner();
        if (planner.Plan(sourceFormat.Id, targetFormat.Id) is null)
            return null;

        // Wait until the file is accessible and unlocked
        if (!await WaitForFileReadyAsync(newPath, _settleTimeout, cancellationToken).ConfigureAwait(false))
            return null;

        // Read file header to inspect actual file signature
        var head = ReadHeader(newPath, WebFormats.SniffLength);
        if (head.Length == 0)
            return null;

        var sniffed = WebFormats.Sniff(head);
        if (sniffed is not null)
        {
            // If the file already matches the target format internally (e.g. already a PDF), don't convert!
            if (string.Equals(sniffed, targetFormat.Id, StringComparison.OrdinalIgnoreCase))
                return null;

            // If sniffed format is known and different from source, check if route from sniffed format to target exists
            if (!string.Equals(sniffed, sourceFormat.Id, StringComparison.OrdinalIgnoreCase))
            {
                var sniffedFormat = FormatRegistry.FindByExtension(sniffed);
                if (sniffedFormat is not null)
                {
                    if (planner.Plan(sniffedFormat.Id, targetFormat.Id) is null)
                        return null;

                    sourceFormat = sniffedFormat;
                }
            }
        }

        var fileName = Path.GetFileName(newPath);
        var candidate = new ExtensionChangeCandidate
        {
            OldPath = oldPath,
            NewPath = newPath,
            SourceFormat = sourceFormat,
            TargetFormat = targetFormat,
            FileName = fileName,
            PerformConversionAsync = ct => ConvertSafelyAsync(newPath, sourceFormat, targetFormat, ct),
        };

        if (ExtensionChangeDetected is { } handler)
        {
            await handler.Invoke(candidate).ConfigureAwait(false);
        }

        return candidate;
    }

    /// <summary>
    /// Executes a safe conversion: writes to a temporary output file, validates it,
    /// and replaces the destination file atomically while preserving the original on failure.
    /// Handles read-only files, cross-device filesystem moves, and temporary file locks.
    /// </summary>
    public async Task<bool> ConvertSafelyAsync(
        string filePath,
        FileFormat sourceFormat,
        FileFormat targetFormat,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return false;

        string workDir;
        try
        {
            workDir = Path.Combine(Path.GetTempPath(), "Filee-ExtensionChange", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Fallback to user app data if system temp directory is restricted
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            workDir = Path.Combine(localAppData, "Filee", "Temp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
        }

        var tempSource = Path.Combine(workDir, $"{FormatRegistry.NameWithoutExtension(filePath)}.{sourceFormat.Id}");
        var tempOutput = Path.Combine(workDir, $"{FormatRegistry.NameWithoutExtension(filePath)}.{targetFormat.Id}");

        MarkInProgress(filePath);
        MarkInProgress(tempSource);
        MarkInProgress(tempOutput);

        try
        {
            // Copy source to temporary working location with retries for locks
            var sourceCopied = false;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    File.Copy(filePath, tempSource, overwrite: true);
                    sourceCopied = true;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 3) throw;
                    await Task.Delay(attempt * 100, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!sourceCopied || !File.Exists(tempSource))
            {
                _log.LogWarning("Could not copy source file {File} due to lock or permission restrictions", filePath);
                return false;
            }

            var success = await _convertFile(tempSource, targetFormat.Id, tempOutput, cancellationToken).ConfigureAwait(false);
            if (!success || !File.Exists(tempOutput))
            {
                _log.LogWarning("Conversion of {File} to {Target} failed", filePath, targetFormat.Id);
                return false;
            }

            var outputInfo = new FileInfo(tempOutput);
            if (outputInfo.Length == 0)
            {
                _log.LogWarning("Converted output for {File} to {Target} is empty", filePath, targetFormat.Id);
                return false;
            }

            // Verify output signature if sniffable
            var outputHead = ReadHeader(tempOutput, WebFormats.SniffLength);
            var outputSniffed = WebFormats.Sniff(outputHead);
            if (outputSniffed is not null && !string.Equals(outputSniffed, targetFormat.Id, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("Converted output signature for {File} does not match {Target}", filePath, targetFormat.Id);
                return false;
            }

            // If destination file is read-only, clear attribute so move/copy can overwrite
            FileAttributes? originalAttrs = null;
            try
            {
                if (File.Exists(filePath))
                {
                    originalAttrs = File.GetAttributes(filePath);
                    if ((originalAttrs.Value & FileAttributes.ReadOnly) != 0)
                    {
                        var newAttrs = originalAttrs.Value & ~FileAttributes.ReadOnly;
                        File.SetAttributes(filePath, newAttrs == 0 ? FileAttributes.Normal : newAttrs);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _log.LogDebug(ex, "Could not check or clear ReadOnly attribute on {File}", filePath);
            }

            // Move the original file to Recycle Bin / Trash, or preserve it as a backup file if trash is refused
            string? backupPath = null;
            var movedToTrash = false;

            if (_moveToTrash is not null)
            {
                try
                {
                    var refusal = _moveToTrash(filePath);
                    movedToTrash = refusal is null && !File.Exists(filePath);
                    if (refusal is not null)
                    {
                        _log.LogInformation("Trash refused original {File} ({Reason}); preserving as backup file", filePath, refusal.ReasonKey);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not move original {File} to trash; preserving as backup file", filePath);
                }
            }

            // If not moved to trash, rename original to backup path to ensure it is never overwritten or lost
            if (!movedToTrash && File.Exists(filePath))
            {
                backupPath = GetBackupPath(filePath, sourceFormat.Id);
                try
                {
                    File.Move(filePath, backupPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(ex, "Could not preserve original file {File} before replacing; aborting conversion to prevent data loss", filePath);
                    return false;
                }
            }

            // Safely place the converted output at filePath.
            // Retries handle momentary locks by file managers/antivirus/indexers.
            // Fallback handles cross-device links (EXDEV on Linux/macOS when temp is tmpfs).
            var placed = false;
            Exception? lastError = null;

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    File.Move(tempOutput, filePath, overwrite: true);
                    placed = true;
                    break;
                }
                catch (IOException ioEx)
                {
                    lastError = ioEx;
                    try
                    {
                        File.Copy(tempOutput, filePath, overwrite: true);
                        try { File.Delete(tempOutput); } catch { }
                        placed = true;
                        break;
                    }
                    catch (Exception copyEx)
                    {
                        lastError = copyEx;
                    }
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    lastError = uaEx;
                    try
                    {
                        File.Copy(tempOutput, filePath, overwrite: true);
                        try { File.Delete(tempOutput); } catch { }
                        placed = true;
                        break;
                    }
                    catch (Exception copyEx)
                    {
                        lastError = copyEx;
                    }
                }

                if (attempt < 5)
                {
                    await Task.Delay(attempt * 100, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!placed)
            {
                _log.LogWarning(lastError, "Could not place converted output at {File}", filePath);
                // Restore original file so it is not lost
                if (backupPath is not null && File.Exists(backupPath) && !File.Exists(filePath))
                {
                    try { File.Move(backupPath, filePath); } catch { }
                }
                else if (movedToTrash && !File.Exists(filePath) && File.Exists(tempSource))
                {
                    try { File.Copy(tempSource, filePath); } catch { }
                }
                return false;
            }

            _log.LogInformation("Successfully converted {File} from {Source} to {Target} (original preserved)", filePath, sourceFormat.Id, targetFormat.Id);
            return true;
        }
        catch (UnauthorizedAccessException uaEx)
        {
            _log.LogWarning(uaEx, "Permission declined during extension change conversion on {File}", filePath);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to safely convert extension change on {File}", filePath);
            return false;
        }
        finally
        {
            UnmarkInProgress(tempSource);
            UnmarkInProgress(tempOutput);
            // Give a brief window before unmarking destination to ensure OS file system events finish settling
            _ = Task.Delay(1000).ContinueWith(_ =>
            {
                UnmarkInProgress(filePath);
                try
                {
                    if (Directory.Exists(workDir))
                        Directory.Delete(workDir, recursive: true);
                }
                catch
                {
                    // Ignore temp dir cleanup errors
                }
            });
        }
    }

    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>Checks whether a path belongs to system, temp, or ignored folders.</summary>
    public static bool ShouldIgnorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        var lastSep = path.LastIndexOfAny(PathSeparators);
        var name = lastSep >= 0 ? path[(lastSep + 1)..] : path;
        if (string.IsNullOrWhiteSpace(name))
            return true;

        // Temporary or system files
        if (name.StartsWith('.') || name.StartsWith("._")
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".filee-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "thumbs.db", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var isTemp = path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

        // Ignored system and build directories
        var parts = path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.StartsWith('.') && part.Length > 1)
                return true;
            if ((!isTemp && part.Equals("AppData", StringComparison.OrdinalIgnoreCase))
                || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || part.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || (!isTemp && part.Equals("Library", StringComparison.OrdinalIgnoreCase))
                || part.Equals("proc", StringComparison.OrdinalIgnoreCase)
                || part.Equals("sys", StringComparison.OrdinalIgnoreCase)
                || part.Equals("dev", StringComparison.OrdinalIgnoreCase)
                || part.Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Waits for a file to become accessible, non-empty, and stable in size.</summary>
    public static async Task<bool> WaitForFileReadyAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        long lastLength = -1;
        var stableCount = 0;
        var consecutiveDenied = 0;

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(path))
                    return false;

                var info = new FileInfo(path);
                var length = info.Length;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    consecutiveDenied = 0;
                    if (length == lastLength && length > 0)
                    {
                        stableCount++;
                        if (stableCount >= 2)
                            return true;
                    }
                    else if (length > 0)
                    {
                        lastLength = length;
                        stableCount = 1;
                    }
                }
            }
            catch (IOException)
            {
                stableCount = 0;
                consecutiveDenied = 0;
            }
            catch (UnauthorizedAccessException)
            {
                stableCount = 0;
                consecutiveDenied++;
                // Stop waiting if access is repeatedly declined for over a second
                if (consecutiveDenied >= 10)
                    return false;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return stableCount >= 1 && File.Exists(path) && new FileInfo(path).Length > 0;
    }

    private static byte[] ReadHeader(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(maxBytes, (int)Math.Min(stream.Length, maxBytes))];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);
            return buffer;
        }
        catch
        {
            return [];
        }
    }

    private static string GetBackupPath(string filePath, string sourceFormatId)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "";
        var nameWithoutExt = FormatRegistry.NameWithoutExtension(filePath);
        var candidate = Path.Combine(dir, $"{nameWithoutExt}.original.{sourceFormatId}");
        if (!File.Exists(candidate))
            return candidate;

        for (var i = 2; i <= 100; i++)
        {
            candidate = Path.Combine(dir, $"{nameWithoutExt}.original ({i}).{sourceFormatId}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{nameWithoutExt}.original.{Guid.NewGuid().ToString("N")[..6]}.{sourceFormatId}");
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        Stop();
        return ValueTask.CompletedTask;
    }
}
