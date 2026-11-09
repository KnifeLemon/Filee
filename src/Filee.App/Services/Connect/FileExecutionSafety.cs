using System.Runtime.InteropServices;
using System.Text;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Connect;

public static class FileExecutionSafety
{
    private static readonly HashSet<string> SafeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Pictures
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".ico", ".tif", ".tiff", ".avif", ".heic", ".heif", ".jxl",
        // PDF
        ".pdf",
        // Plain text
        ".txt", ".csv", ".tsv", ".log", ".md", ".json", ".xml", ".yaml", ".yml",
        // Audio
        ".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac", ".wma", ".opus", ".mid",
        // Video
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv", ".m4v"
    };

    public static bool IsSafeToOpenDirectly(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && SafeExtensions.Contains(ext);
    }

    public static string GetSafeIconKind(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".ico" or ".tif" or ".tiff" or ".avif" => "image",
            ".pdf" => "pdf",
            ".txt" or ".csv" or ".tsv" or ".log" or ".md" or ".json" or ".xml" or ".yaml" or ".yml" => "text",
            ".mp3" or ".wav" or ".ogg" or ".m4a" or ".flac" or ".aac" or ".wma" or ".opus" => "audio",
            ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".wmv" or ".flv" or ".m4v" => "video",
            _ => "file"
        };
    }

    public static void MarkReceivedFile(string path, ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows())
        {
            MarkOfTheWeb.TryWrite(path, MarkOfTheWeb.Internet(), logger);
        }
        else if (OperatingSystem.IsMacOS())
        {
            ApplyMacOsQuarantine(path, logger);
        }
    }

    private static void ApplyMacOsQuarantine(string path, ILogger? logger)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        try
        {
            // Standard macOS Gatekeeper quarantine string format: 0083;date;Filee;UUID
            var value = $"0083;00000000;Filee;{Guid.NewGuid()}";
            var bytes = Encoding.UTF8.GetBytes(value);
            _ = SetXAttr(path, "com.apple.quarantine", bytes, (nuint)bytes.Length, 0, 0);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not set com.apple.quarantine on {Path}", path);
        }
    }

    [DllImport("libc", EntryPoint = "setxattr", CharSet = CharSet.Ansi)]
    private static extern int SetXAttr(string path, string name, byte[] value, nuint size, uint position, int options);
}
