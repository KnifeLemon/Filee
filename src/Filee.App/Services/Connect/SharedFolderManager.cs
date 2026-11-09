using System.IO.Compression;
using System.Text.Json;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Connect;

public sealed class SharedFolderManager
{
    public const long MaxFileSize = 500L * 1024 * 1024; // 500 MB per file
    public const long MaxTotalStorage = 2L * 1024 * 1024 * 1024; // 2 GB total quota
    public const int MaxFiles = 5000;

    private readonly ILogger<SharedFolderManager> _log;
    private readonly string _storageDir;
    private readonly string _tempDir;
    private readonly Dictionary<string, SharedFileItem> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private long _totalStorage;
    private long _reservedBytes;

    public string StorageDirectory => _storageDir;
    public long StorageLimitRemaining
    {
        get
        {
            lock (_lock) { return Math.Max(0, MaxTotalStorage - (_totalStorage + _reservedBytes)); }
        }
    }

    public SharedFolderManager(ILogger<SharedFolderManager> log, UserDataStore? store = null, string? storageDir = null, string? tempDir = null)
    {
        _log = log;
        var baseDir = store?.Directory ?? UserDataStore.DefaultDirectory;
        _storageDir = storageDir ?? Path.Combine(baseDir, "ConnectShared");
        _tempDir = tempDir ?? Path.Combine(Path.GetTempPath(), "FileeConnect_" + Guid.NewGuid().ToString("N"));
    }

    public SharedFolderManager(ILogger<SharedFolderManager> log, string? storageDir, string? tempDir)
        : this(log, null, storageDir, tempDir)
    {
    }

    public void Initialize()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_storageDir);
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); } catch { }
            }
            Directory.CreateDirectory(_tempDir);

            _files.Clear();
            _totalStorage = 0;
            _reservedBytes = 0;

            foreach (var metaFile in Directory.EnumerateFiles(_storageDir, "*.meta.json"))
            {
                try
                {
                    var json = File.ReadAllText(metaFile);
                    var meta = JsonSerializer.Deserialize<SharedFileMetadata>(json);
                    if (meta is null || string.IsNullOrWhiteSpace(meta.Id))
                        continue;

                    var diskFiles = Directory.EnumerateFiles(_storageDir, $"{meta.Id}.*")
                        .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (diskFiles.Count == 0)
                        continue;

                    var diskPath = diskFiles[0];
                    var info = new FileInfo(diskPath);
                    if (!info.Exists || info.Length != meta.Size)
                        continue;

                    var item = new SharedFileItem(
                        meta.Id,
                        meta.Name,
                        meta.RelativePath,
                        meta.Size,
                        meta.Sender,
                        meta.MimeType,
                        meta.CreatedAt,
                        Path.GetFileName(diskPath),
                        diskPath);

                    _files[item.Id] = item;
                    _totalStorage += item.Size;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not load shared file metadata: {Path}", metaFile);
                }
            }
        }
    }

    /// <summary>
    /// Clears all files in the shared folder at session end.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _files.Clear();
            _totalStorage = 0;
            _reservedBytes = 0;

            if (Directory.Exists(_storageDir))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(_storageDir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                    foreach (var dir in Directory.EnumerateDirectories(_storageDir))
                    {
                        try { Directory.Delete(dir, recursive: true); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not completely clear shared storage directory {Dir}", _storageDir);
                }
            }

            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); } catch { }
            }
        }
    }

    public static string CleanRelativePath(string? input)
    {
        var raw = (input ?? "").Replace('\\', '/').Trim('/', ' ', '\t');
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains(".."))
            throw new ArgumentException("Invalid path");

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts);
    }

    public async Task<SharedFileItem> SaveStreamAsync(Stream source, string fileName, string relativePath, string sender, string? mimeType, long? expectedLength = null)
    {
        var safeRelPath = CleanRelativePath(string.IsNullOrWhiteSpace(relativePath) ? fileName : relativePath);
        var baseName = Path.GetFileName(safeRelPath);

        // Reserve quota upfront under the lock if size is known or cap with MaxFileSize
        var reserveAmount = expectedLength.HasValue && expectedLength.Value > 0
            ? Math.Min(expectedLength.Value, MaxFileSize)
            : 1024 * 1024; // 1 MB initial reserve

        lock (_lock)
        {
            if (_files.Count >= MaxFiles)
                throw new InvalidOperationException($"Shared folder limit of {MaxFiles} files reached.");

            if (_totalStorage + _reservedBytes + reserveAmount > MaxTotalStorage)
                throw new InvalidOperationException("Shared storage limit (2 GB) reached.");

            _reservedBytes += reserveAmount;
        }

        var id = Guid.NewGuid().ToString("N");
        var tempFile = Path.Combine(_tempDir, $"{id}.upload");
        long bytesCopied = 0;

        try
        {
            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    bytesCopied += read;
                    if (bytesCopied > MaxFileSize)
                    {
                        throw new InvalidOperationException($"File exceeds the {MaxFileSize / (1024 * 1024)} MB limit.");
                    }

                    // Dynamically adjust reservation if exceeding initial reserve
                    lock (_lock)
                    {
                        if (bytesCopied > reserveAmount)
                        {
                            var diff = bytesCopied - reserveAmount;
                            if (_totalStorage + _reservedBytes + diff > MaxTotalStorage)
                                throw new InvalidOperationException("Shared storage limit (2 GB) reached.");
                            _reservedBytes += diff;
                            reserveAmount = bytesCopied;
                        }
                    }

                    await fs.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            if (bytesCopied == 0)
                throw new InvalidOperationException("Empty files cannot be shared.");

            var ext = Path.GetExtension(baseName);
            var diskName = $"{id}{ext}";
            var finalDiskPath = Path.Combine(_storageDir, diskName);
            var metaPath = Path.Combine(_storageDir, $"{id}.meta.json");

            File.Move(tempFile, finalDiskPath, overwrite: true);

            // Mark received file with Internet zone on Windows / quarantine on macOS
            if (sender != "pc")
            {
                FileExecutionSafety.MarkReceivedFile(finalDiskPath, _log);
            }

            var item = new SharedFileItem(
                id,
                baseName,
                safeRelPath,
                bytesCopied,
                sender,
                mimeType ?? GuessMimeType(baseName),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                diskName,
                finalDiskPath);

            var metaJson = JsonSerializer.Serialize(item.ToMetadata(), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metaPath, metaJson);

            lock (_lock)
            {
                _reservedBytes = Math.Max(0, _reservedBytes - reserveAmount);
                _files[item.Id] = item;
                _totalStorage += item.Size;
            }

            return item;
        }
        catch
        {
            lock (_lock)
            {
                _reservedBytes = Math.Max(0, _reservedBytes - reserveAmount);
            }
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            throw;
        }
    }

    public async Task<SharedFileItem> AddLocalFileAsync(string localPath, string relativePath)
    {
        var info = new FileInfo(localPath);
        if (!info.Exists)
            throw new FileNotFoundException("Local file not found", localPath);

        await using var fs = File.OpenRead(localPath);
        return await SaveStreamAsync(fs, info.Name, relativePath, "pc", GuessMimeType(info.Name), info.Length);
    }

    public SharedFileItem? GetFile(string id)
    {
        lock (_lock)
        {
            return _files.TryGetValue(id, out var file) ? file : null;
        }
    }

    public IReadOnlyList<SharedFileMetadata> GetFiles()
    {
        lock (_lock)
        {
            return _files.Values.OrderByDescending(f => f.CreatedAt).Select(f => f.ToMetadata()).ToList();
        }
    }

    public StorageUsage GetUsage()
    {
        lock (_lock)
        {
            return new StorageUsage(_totalStorage, MaxTotalStorage, _files.Count, MaxFiles);
        }
    }

    public bool DeleteFile(string id)
    {
        lock (_lock)
        {
            if (!_files.Remove(id, out var item))
                return false;

            _totalStorage = Math.Max(0, _totalStorage - item.Size);

            try { File.Delete(item.DiskPath); } catch { }
            try { File.Delete(Path.Combine(_storageDir, $"{id}.meta.json")); } catch { }

            return true;
        }
    }

    public int DeleteFolder(string folderPath)
    {
        var clean = CleanRelativePath(folderPath);
        var prefix = clean + "/";
        var toDelete = new List<string>();

        lock (_lock)
        {
            foreach (var kvp in _files)
            {
                if (kvp.Value.RelativePath.Equals(clean, StringComparison.OrdinalIgnoreCase)
                    || kvp.Value.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    toDelete.Add(kvp.Key);
                }
            }
        }

        var count = 0;
        foreach (var id in toDelete)
        {
            if (DeleteFile(id))
                count++;
        }
        return count;
    }

    public void WriteFolderZip(string folderPath, Stream outputStream)
    {
        var clean = CleanRelativePath(folderPath);
        var prefix = clean + "/";
        List<SharedFileItem> matches;

        lock (_lock)
        {
            matches = _files.Values
                .Where(f => f.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in matches)
        {
            var entryName = file.RelativePath[prefix.Length..];
            if (string.IsNullOrWhiteSpace(entryName))
                continue;

            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            entry.LastWriteTime = DateTimeOffset.FromUnixTimeMilliseconds(file.CreatedAt);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file.DiskPath);
            fileStream.CopyTo(entryStream);
        }
    }

    public string GetFullPath(string fileNameOrId)
    {
        lock (_lock)
        {
            if (_files.TryGetValue(fileNameOrId, out var item))
                return item.DiskPath;

            var match = _files.Values.FirstOrDefault(f => string.Equals(f.Name, fileNameOrId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.DiskPath;

            return Path.Combine(_storageDir, fileNameOrId);
        }
    }

    public static string GuessMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }
}
