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

/// <summary>A picture ready to embed: PNG or JPEG bytes, their size, and the size of the picture as stored.</summary>
internal sealed record CanvasPicture(string MediaType, byte[] Data, double Width, double Height, double NaturalWidth, double NaturalHeight);

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
    private readonly Dictionary<(string Path, int Edge), CanvasPicture?> _pictureCache = [];
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

    /// <summary>Longest vault path looked for; a longer one is taken as not found, without touching the disk.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>
    /// Most leading folders dropped from a vault path (the folders between the vault's root and the canvas's): a
    /// vault is rarely more than a few folders deep, and each try is a look at the disk.
    /// </summary>
    public const int MaxDroppedFolders = 8;

    /// <summary>
    /// The file <paramref name="vaultPath"/> names under <paramref name="folder"/>: the path itself, then the path
    /// without its first folders (a vault path from above the canvas's folder), at most <see cref="MaxDroppedFolders"/>
    /// of them. Only relative paths that stay inside, of at most <see cref="MaxPathLength"/> characters.
    /// </summary>
    public static string? Locate(string vaultPath, string folder)
    {
        var path = vaultPath.Trim();
        if (path.Length == 0 || path.Length > MaxPathLength || path.Contains(':', StringComparison.Ordinal) || path.Contains('\0', StringComparison.Ordinal)
            || path[0] is '/' or '\\' || path.StartsWith('~') || Path.IsPathRooted(path))
            return null;
        var parts = path.Split('/', '\\');
        if (parts.Any(p => p is ".." or "." || p.Length == 0))
            return null;
        for (var skip = 0; skip < Math.Min(parts.Length, MaxDroppedFolders + 1); skip++)
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
    /// can't be read, is too large, or the board has had its share of pictures. Each picture is decoded once for each
    /// size it is shown at: 200 cards showing the same photo decode it once, and the SVG embeds it once.
    /// </summary>
    public CanvasPicture? Picture(string path, double width, double height, CancellationToken ct)
    {
        var edge = (int)Math.Ceiling(Math.Min(MaxEmbeddedEdge, Math.Max(2 * width, 2 * height)));
        if (_pictureCache.TryGetValue((path, edge), out var known))
            return known;
        if (_pictures >= MaxPictures)
            return null;
        _pictures++; // a picture that can't be read counts too: it was looked at
        return _pictureCache[(path, edge)] = Decode(path, edge, ct);
    }

    private static CanvasPicture? Decode(string path, int edge, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length is 0 or > MaxFileBytes)
                return null;
            var (decoded, naturalWidth, naturalHeight) = IsSvg(path) ? Svg(path, edge) : Raster(path);
            using var image = decoded;
            if (image is null)
                return null;
            ct.ThrowIfCancellationRequested();
            var scale = Math.Min(1.0, (double)edge / Math.Max(image.Width, image.Height));
            if (scale < 1)
                image.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(image.Width * scale)), (uint)Math.Max(1, Math.Round(image.Height * scale))));
            var png = image.HasAlpha || PictureFormat(path) is MagickFormat.Png or MagickFormat.Gif or MagickFormat.Bmp || IsSvg(path);
            image.Quality = png ? 90u : 85u;
            image.Strip();
            var data = image.ToByteArray(png ? MagickFormat.Png : MagickFormat.Jpeg);
            return new CanvasPicture(png ? "image/png" : "image/jpeg", data, image.Width, image.Height, naturalWidth, naturalHeight);
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null; // shown as a file card, by name
        }
    }

    /// <summary>A raster picture, turned upright as its EXIF orientation says; its natural size is its pixels.</summary>
    private static (MagickImage? Image, double NaturalWidth, double NaturalHeight) Raster(string path)
    {
        var format = PictureFormat(path)!.Value;
        var info = new MagickImageInfo();
        info.Read(path, new MagickReadSettings { Format = format });
        if ((long)info.Width * info.Height is 0 or > MaxPixels)
            return (null, 0, 0);
        // One frame: the first of an animated GIF or WebP, as a printed board shows it.
        var image = new MagickImage(path, new MagickReadSettings { Format = format, FrameIndex = 0, FrameCount = 1 });
        image.AutoOrient();
        return (image, image.Width, image.Height);
    }

    /// <summary>
    /// An SVG picture drawn by the vector engine, with its own same-folder rule for what it refers to. Its natural size
    /// is its viewport, in CSS pixels.
    /// </summary>
    private static (MagickImage? Image, double NaturalWidth, double NaturalHeight) Svg(string path, int edge)
    {
        using var drawing = SvgRenderer.Load(path);
        var scale = (float)(edge / Math.Max(drawing.Bounds.Width, drawing.Bounds.Height));
        return (SvgRenderer.Rasterize(drawing, scale), drawing.Bounds.Width, drawing.Bounds.Height);
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
