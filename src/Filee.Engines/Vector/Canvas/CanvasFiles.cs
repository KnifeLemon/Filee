// The files a canvas shows: a file card's note or picture and a group's background, named by their path in the Obsidian
// vault. Filee's rule for every document applies (DocumentFiles): a file is read only from the canvas's own folder and
// its subfolders. A path that is absolute, goes up (..), names another computer or has a scheme (http:, file:) is never
// looked at, and nothing is fetched. Vault paths start at the vault's root, which may be above the canvas's folder; so
// "Boards/img/a.png" in "Vault/Boards/plan.canvas" is also looked for as "img/a.png", without ever leaving the folder.
//
// Pictures are checked before they are decoded (file size, pixel count), decoded by the reader of their extension only
// (a ".png" that is something else isn't read as that), shrunk to what the card shows, and embedded in the SVG.

using Filee.Engines.Infrastructure;
using ImageMagick;

namespace Filee.Engines.Vector.Canvas;

/// <summary>Finds and reads the files a canvas refers to.</summary>
internal sealed class CanvasFiles(string canvasFolder)
{
    /// <summary>Largest picture or note file read.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>Most pixels a picture may have before it is decoded.</summary>
    public const long MaxPixels = 100_000_000;

    /// <summary>Longest edge of a picture embedded in the board.</summary>
    public const int MaxEmbeddedEdge = 2048;

    /// <summary>Most pictures embedded in one board; the others are shown by name.</summary>
    public const int MaxPictures = 200;

    /// <summary>Most of a note shown in a file card (the card shows its beginning).</summary>
    public const int MaxNoteCharacters = 64 * 1024;

    private readonly Dictionary<string, string?> _found = new(StringComparer.Ordinal);
    private int _pictures;

    /// <summary>The picture formats Obsidian shows in a card, by extension.</summary>
    public static MagickFormat? PictureFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => MagickFormat.Png,
        ".jpg" or ".jpeg" => MagickFormat.Jpeg,
        ".gif" => MagickFormat.Gif,
        ".bmp" => MagickFormat.Bmp,
        ".webp" => MagickFormat.WebP,
        ".avif" => MagickFormat.Avif,
        _ => null,
    };

    public static bool IsPicture(string path) => PictureFormat(path) is not null || IsSvg(path);

    public static bool IsSvg(string path) => Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    public static bool IsNote(string path) => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>The local file a vault path names, inside the canvas's folder, or null.</summary>
    public string? Find(string? vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            return null;
        if (_found.TryGetValue(vaultPath, out var known))
            return known;
        return _found[vaultPath] = Locate(vaultPath, canvasFolder);
    }

    /// <summary>
    /// The file <paramref name="vaultPath"/> names under <paramref name="folder"/>: the path itself, then the path
    /// without its first folders (a vault path from above the canvas's folder). Only relative paths that stay inside.
    /// </summary>
    public static string? Locate(string vaultPath, string folder)
    {
        var path = vaultPath.Trim();
        if (path.Length == 0 || path.Contains(':', StringComparison.Ordinal) || path.Contains('\0', StringComparison.Ordinal)
            || path[0] is '/' or '\\' || path.StartsWith('~') || Path.IsPathRooted(path))
            return null;
        var parts = path.Split('/', '\\');
        if (parts.Any(p => p is ".." or "." || p.Length == 0))
            return null;
        for (var skip = 0; skip < parts.Length; skip++)
        {
            var candidate = string.Join('/', parts[skip..]);
            // Escaped (%20) names are read as they are written: Obsidian stores paths unescaped.
            if (DocumentFiles.Find(candidate.Replace("%", "%25", StringComparison.Ordinal), folder) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// The picture at <paramref name="path"/> shrunk to fit <paramref name="width"/> × <paramref name="height"/> canvas
    /// pixels (twice that, for sharp prints, at most <see cref="MaxEmbeddedEdge"/>), as PNG or JPEG bytes; null when it
    /// can't be read, is too large, or the board has had its share of pictures.
    /// </summary>
    public (string MediaType, byte[] Data, double Width, double Height)? Picture(string path, double width, double height, CancellationToken ct)
    {
        if (_pictures >= MaxPictures)
            return null;
        try
        {
            if (new FileInfo(path).Length is 0 or > MaxFileBytes)
                return null;
            using var image = IsSvg(path) ? Svg(path, width, height) : Raster(path);
            if (image is null)
                return null;
            ct.ThrowIfCancellationRequested();
            image.AutoOrient();
            var scale = Math.Min(1.0, Math.Min(MaxEmbeddedEdge, Math.Max(2 * width, 2 * height)) / Math.Max(image.Width, image.Height));
            if (scale < 1)
                image.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(image.Width * scale)), (uint)Math.Max(1, Math.Round(image.Height * scale))));
            var png = image.HasAlpha || PictureFormat(path) is MagickFormat.Png or MagickFormat.Gif or MagickFormat.Bmp || IsSvg(path);
            image.Quality = png ? 90u : 85u;
            image.Strip();
            var data = image.ToByteArray(png ? MagickFormat.Png : MagickFormat.Jpeg);
            _pictures++;
            return (png ? "image/png" : "image/jpeg", data, image.Width, image.Height);
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null; // shown as a file card, by name
        }
    }

    private static MagickImage? Raster(string path)
    {
        var format = PictureFormat(path)!.Value;
        var info = new MagickImageInfo();
        info.Read(path, new MagickReadSettings { Format = format });
        if ((long)info.Width * info.Height is 0 or > MaxPixels)
            return null;
        // One frame: the first of an animated GIF or WebP, as a printed board shows it.
        return new MagickImage(path, new MagickReadSettings { Format = format, FrameIndex = 0, FrameCount = 1 });
    }

    /// <summary>An SVG picture drawn by the vector engine, with its own same-folder rule for what it refers to.</summary>
    private static MagickImage? Svg(string path, double width, double height)
    {
        using var drawing = SvgRenderer.Load(path);
        var edge = Math.Min(MaxEmbeddedEdge, Math.Max(2 * width, 2 * height));
        var scale = (float)(edge / Math.Max(drawing.Bounds.Width, drawing.Bounds.Height));
        return SvgRenderer.Rasterize(drawing, scale);
    }

    /// <summary>The beginning of the note at <paramref name="path"/>, or null when it can't be read.</summary>
    public static string? Note(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                return null;
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxNoteCharacters];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
