using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Connect;

public sealed class SharedFolderManager
{
    public const long MaxFileSize = 200L * 1024 * 1024; // 200 MB
    public const long MaxTotalStorage = 2L * 1024 * 1024 * 1024; // 2 GB
    public const int MaxFiles = 5000;

    private readonly ILogger<SharedFolderManager> _log;
    private readonly string _storageDir;
    private readonly string _tempDir;
    private readonly Dictionary<string, SharedFileItem> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private long _totalStorage;

    public string StorageDirectory => _storageDir;
    public long StorageLimitRemaining
    {
        get
        {
            lock (_lock) { return Math.Max(0, MaxTotalStorage - _totalStorage); }
        }
    }

    public SharedFolderManager(ILogger<SharedFolderManager> log, string? storageDir = null, string? tempDir = null)
    {
        _log = log;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _storageDir = storageDir ?? Path.Combine(appData, "Filee", "SharedFolder");
        _tempDir = tempDir ?? Path.Combine(appData, "Filee", "SharedFolderTemp");
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

            _log.LogInformation("SharedFolderManager initialized: {Count} files, {Size} bytes", _files.Count, _totalStorage);
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

    public async Task<SharedFileItem> SaveStreamAsync(Stream source, string fileName, string relativePath, string sender, string? mimeType)
    {
        var safeRelPath = CleanRelativePath(string.IsNullOrWhiteSpace(relativePath) ? fileName : relativePath);
        var baseName = Path.GetFileName(safeRelPath);

        var id = Guid.NewGuid().ToString("N");
        var tempFile = Path.Combine(_tempDir, $"{id}.upload");

        long bytesCopied = 0;
        await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                bytesCopied += read;
                if (bytesCopied > MaxFileSize)
                {
                    fs.Close();
                    File.Delete(tempFile);
                    throw new InvalidOperationException($"File exceeds the {MaxFileSize / (1024 * 1024)} MB limit.");
                }
                await fs.WriteAsync(buffer.AsMemory(0, read));
            }
        }

        if (bytesCopied == 0)
        {
            File.Delete(tempFile);
            throw new InvalidOperationException("Empty files cannot be shared.");
        }

        lock (_lock)
        {
            if (_files.Count >= MaxFiles)
            {
                File.Delete(tempFile);
                throw new InvalidOperationException($"Shared folder limit of {MaxFiles} files reached.");
            }
            if (_totalStorage + bytesCopied > MaxTotalStorage)
            {
                File.Delete(tempFile);
                throw new InvalidOperationException("Shared storage limit (2 GB) reached.");
            }
        }

        var ext = Path.GetExtension(baseName);
        var diskName = $"{id}{ext}";
        var finalDiskPath = Path.Combine(_storageDir, diskName);
        var metaPath = Path.Combine(_storageDir, $"{id}.meta.json");

        File.Move(tempFile, finalDiskPath, overwrite: true);

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
            _files[item.Id] = item;
            _totalStorage += item.Size;
        }

        return item;
    }

    public async Task<SharedFileItem> AddLocalFileAsync(string localPath, string relativePath)
    {
        var info = new FileInfo(localPath);
        if (!info.Exists)
            throw new FileNotFoundException("Local file not found", localPath);

        await using var fs = File.OpenRead(localPath);
        return await SaveStreamAsync(fs, info.Name, relativePath, "pc", GuessMimeType(info.Name));
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
            return _files.Values
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.ToMetadata())
                .ToList();
        }
    }

    public StorageUsage GetStorageUsage()
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

    public static string GuessMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".html" => "text/html",
            ".zip" => "application/zip",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };
    }
}
