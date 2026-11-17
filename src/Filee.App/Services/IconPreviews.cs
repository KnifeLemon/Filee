using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Filee.Core.Presets;

namespace Filee.App.Services;

/// <summary>
/// The little pictures the folder-icon ring shows above its entries' names: the picture itself, seen before it is
/// applied. Decoded once per session per file and kept (a ring's pictures are a handful of small files); a file that
/// would not decode, or is too big to bother with, gets no picture and its name alone.
/// </summary>
internal static class IconPreviews
{
    /// <summary>A bigger candidate would stall the ring's build for seconds: its name alone tells it.</summary>
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, IImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture of <paramref name="path"/> (a folder-icon ring entry's source file), or null without one.</summary>
    public static IImage? Of(string? path)
    {
        if (path is null)
            return null;
        return Cache.GetOrAdd(path, Decode);
    }

    private static IImage? Decode(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes)
                return null;
            return new Bitmap(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            return null; // a broken or unsupported picture keeps its name only
        }
    }
}
