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
    /// <param name="log">Logger instance.</param>
    /// <param name="settleTimeout">Maximum time to wait for the renamed file to unlock and settle.</param>
    public ExtensionChangeWatcher(
        Func<RoutePlanner> createPlanner,
        Func<string, string, string, CancellationToken, Task<bool>> convertFile,
        ILogger? log = null,
        TimeSpan? settleTimeout = null)
    {
        _createPlanner = createPlanner;
        _convertFile = convertFile;
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

    /// <summary>Starts watching the given folders (and their subdirectories).</summary>
    public void Start(IEnumerable<string> directories)
    {
        lock (_lock)
        {
            Stop();
            foreach (var dir in directories.Distinct(FileSystemPaths.Comparer))
            {
                if (!Directory.Exists(dir))
                    continue;

                try
                {
                    var watcher = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Renamed += OnRenamed;
                    watcher.Error += (_, e) =>
                    {
                        _log.LogWarning(e.GetException(), "Filesystem watcher error on {Directory}", dir);
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                    _log.LogInformation("Watching extension changes in {Directory}", dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _log.LogWarning(ex, "Could not start extension watcher on {Directory}", dir);
                }
            }
        }
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
        _ = HandleRenameAsync(e.OldFullPath, e.FullPath);
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
    /// </summary>
    public async Task<bool> ConvertSafelyAsync(
        string filePath,
        FileFormat sourceFormat,
        FileFormat targetFormat,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return false;

        var workDir = Path.Combine(Path.GetTempPath(), "Filee-ExtensionChange", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var tempSource = Path.Combine(workDir, $"{FormatRegistry.NameWithoutExtension(filePath)}.{sourceFormat.Id}");
        var tempOutput = Path.Combine(workDir, $"{FormatRegistry.NameWithoutExtension(filePath)}.{targetFormat.Id}");

        MarkInProgress(filePath);
        MarkInProgress(tempSource);
        MarkInProgress(tempOutput);

        try
        {
            // Copy source to temporary working location with correct source extension
            File.Copy(filePath, tempSource, overwrite: true);

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

            // Atomically replace destination file with converted output
            File.Move(tempOutput, filePath, overwrite: true);
            _log.LogInformation("Successfully converted {File} from {Source} to {Target}", filePath, sourceFormat.Id, targetFormat.Id);
            return true;
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

    /// <summary>Checks whether a path belongs to system, temp, or ignored folders.</summary>
    public static bool ShouldIgnorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
            return true;

        // Temporary or system files
        if (name.StartsWith('.') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
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
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
                || (!isTemp && part.Equals("Library", StringComparison.OrdinalIgnoreCase)))
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
            }
            catch (UnauthorizedAccessException)
            {
                stableCount = 0;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return File.Exists(path) && new FileInfo(path).Length > 0;
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

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        Stop();
        return ValueTask.CompletedTask;
    }
}
