// Mark of the Web: the "Zone.Identifier" stream Windows browsers attach to the files they download. SmartScreen,
// Office's block on macros from the internet and Protected View go by it, so files Filee saves from the web get it,
// and files made from a marked file (unpacked from an archive, converted) keep it. Windows only. A drive without
// streams (FAT32, exFAT, many network shares) can't hold it: nothing is written there, and nothing fails.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Filee.Core.Platform;

/// <summary>Reads and writes the Mark of the Web of files.</summary>
public static class MarkOfTheWeb
{
    private const string StreamSuffix = ":Zone.Identifier";

    /// <summary>A mark longer than this isn't read; the file still counts as from the internet.</summary>
    private const int MaxLength = 64 * 1024;

    /// <summary>
    /// The mark of a file from the internet zone (zone 3), with the addresses of the file and of the page it came
    /// from when they are known (http and https only, without user name, password or fragment).
    /// </summary>
    public static string Internet(Uri? hostUrl = null, Uri? referrerUrl = null)
    {
        var mark = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
        if (Address(referrerUrl) is { } referrer)
            mark.Append("ReferrerUrl=").Append(referrer).Append("\r\n");
        if (Address(hostUrl) is { } host)
            mark.Append("HostUrl=").Append(host).Append("\r\n");
        return mark.ToString();
    }

    /// <summary>The mark <paramref name="path"/> carries, or null: none, not Windows, or no file.</summary>
    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var stream = new FileStream(Path.GetFullPath(path) + StreamSuffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxLength)
                return Internet();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // FileNotFoundException: not marked
        }
    }

    /// <summary>
    /// Gives <paramref name="path"/> the mark <paramref name="mark"/>. False when the file or its drive can't take it;
    /// the file itself stays as it is.
    /// </summary>
    public static bool TryWrite(string path, string mark, ILogger? log = null)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            File.WriteAllText(Path.GetFullPath(path) + StreamSuffix, mark, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log?.LogInformation("Could not mark {File} as from the internet: {Reason}", path, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Gives every file among <paramref name="outputs"/> the mark of <paramref name="source"/>, if it has one. Folders
    /// are left alone: what is in them may not all come from the source.
    /// </summary>
    public static void Copy(string source, IEnumerable<string> outputs, ILogger? log = null)
    {
        if (Read(source) is not { } mark)
            return;
        foreach (var output in outputs)
            if (File.Exists(output))
                TryWrite(output, mark, log);
    }

    private static string? Address(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            : null;
}
