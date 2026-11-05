// An e-book in memory: the HWPX document model (content) plus the metadata e-book formats carry (title, authors,
// language, cover). Every e-book reader produces a Book and every e-book writer consumes one.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;

namespace Filee.Engines.Ebooks;

/// <summary>Title, authors, language and cover of a book.</summary>
internal sealed class BookMetadata
{
    public string? Title { get; set; }
    public List<string> Authors { get; } = [];

    /// <summary>BCP 47 language tag ("ko", "en-US"), if known.</summary>
    public string? Language { get; set; }
    public string? Description { get; set; }
    public string? Publisher { get; set; }

    /// <summary>Local picture file of the cover, if the source names one.</summary>
    public string? CoverImage { get; set; }
}

/// <summary>An e-book: content and metadata.</summary>
internal sealed record Book(HDocument Document, BookMetadata Metadata)
{
    /// <summary>The title to show: metadata, the document's own title, the first heading, else the file name.</summary>
    public string DisplayTitle(string sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(Metadata.Title))
            return Metadata.Title.Trim();
        if (!string.IsNullOrWhiteSpace(Document.Title))
            return Document.Title.Trim();
        var heading = Document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().FirstOrDefault(p => p.HeadingLevel > 0);
        if (heading is not null && HDocumentWalker.Text(heading.Inlines) is { Length: > 0 } text)
            return text.Length > 200 ? text[..200] : text;
        return Filee.Core.Formats.FormatRegistry.NameWithoutExtension(sourcePath);
    }

    /// <summary>The language from the metadata, else guessed from the text: Korean, Chinese, Japanese or English.</summary>
    public string DisplayLanguage()
    {
        if (!string.IsNullOrWhiteSpace(Metadata.Language))
            return Metadata.Language.Trim();
        int hangul = 0, han = 0, kana = 0, latin = 0;
        foreach (var text in Document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).OfType<HText>().Take(2000))
        {
            foreach (var ch in text.Text)
            {
                if (ch is >= '가' and <= '힣')
                    hangul++;
                else if (ch is >= '぀' and <= 'ヿ')
                    kana++;
                else if (ch is >= '一' and <= '鿿')
                    han++;
                else if (char.IsAsciiLetter(ch))
                    latin++;
            }
        }
        return hangul > 0 && hangul * 3 >= latin ? "ko"
            : kana > 0 && kana * 3 >= latin ? "ja"
            : han > 0 && han * 3 >= latin ? "zh"
            : "en";
    }
}

/// <summary>Helpers shared by the e-book readers and writers.</summary>
internal static class EbookFiles
{
    /// <summary>Message for books with DRM: Filee never removes copy protection.</summary>
    public const string DrmMessage =
        "This book is protected with DRM (copy protection), so it cannot be converted. Filee does not remove DRM; " +
        "open it in the reading app it was bought for.";

    /// <summary>More entries than this are refused: a book has a few per chapter and picture.</summary>
    private const int MaxEntries = 100_000;

    /// <summary>Largest entry unpacked: far more than a picture or chapter, enough for a video clip of an EPUB 3 book.</summary>
    private const long MaxEntryBytes = 1024L * 1024 * 1024;

    /// <summary>Everything unpacked from one book: comics and fixed-layout books of high resolution scans reach about 1 GB.</summary>
    private const long MaxTotalBytes = 2048L * 1024 * 1024;

    /// <summary>
    /// Unpacks a ZIP into <paramref name="folder"/>. Entries that would land outside it ("../", absolute paths)
    /// are skipped, so a crafted archive cannot write anywhere else. Sizes are checked while unpacking (a lying
    /// header does not help a zip bomb): past the limits above the book is refused.
    /// </summary>
    public static void Extract(ZipArchive zip, string folder) => Extract(zip, folder, MaxEntryBytes, MaxTotalBytes);

    /// <summary><see cref="Extract(ZipArchive, string)"/> with smaller limits (for tests).</summary>
    internal static void Extract(ZipArchive zip, string folder, long maxEntryBytes, long maxTotalBytes)
    {
        if (zip.Entries.Count > MaxEntries)
            throw new ConversionRefusedException("error.document_too_large", $"over {MaxEntries} entries");
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var buffer = new byte[81920];
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;
            if (SafePath(root, entry.FullName) is not { } target)
                continue;
            if (entry.Length > maxEntryBytes)
                throw TooLarge(entry, "over " + BoundedXml.Size(maxEntryBytes));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = File.Create(target);
            long written = 0;
            int count;
            while ((count = source.Read(buffer)) > 0)
            {
                written += count;
                total += count;
                if (written > maxEntryBytes)
                    throw TooLarge(entry, "over " + BoundedXml.Size(maxEntryBytes));
                if (total > maxTotalBytes)
                    throw TooLarge(entry, "the book unpacks to over " + BoundedXml.Size(maxTotalBytes));
                output.Write(buffer, 0, count);
            }
        }
    }

    private static ConversionRefusedException TooLarge(ZipArchiveEntry entry, string reason) =>
        new("error.document_too_large", $"{entry.FullName}: {reason}");

    /// <summary>A path inside <paramref name="root"/> for an archive entry name, or null if it would escape.</summary>
    public static string? SafePath(string root, string entryName)
    {
        var relative = entryName.Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0 || relative.Contains(':'))
            return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>A fresh folder inside the job's work directory.</summary>
    public static string NewFolder(string workDirectory, string prefix)
    {
        var folder = Path.Combine(workDirectory, $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// A picture in a format every reading system shows: JPEG and PNG (plus GIF and SVG when
    /// <paramref name="gifAndSvg"/>) are kept, others are converted into <paramref name="folder"/> — to JPEG for
    /// opaque photos (WEBP, AVIF, ... would grow a lot as PNG), else PNG. Null when the file cannot be read.
    /// </summary>
    public static (string File, string MediaType)? CommonImage(string path, string folder, bool gifAndSvg)
    {
        var mediaType = ImageMediaType(path);
        if (mediaType is "image/jpeg" or "image/png" || gifAndSvg && mediaType is "image/gif" or "image/svg+xml")
            return (path, mediaType);
        try
        {
            using var image = new ImageMagick.MagickImage(path);
            DdsChannelOrder.Correct(path, image);
            var jpeg = !image.HasAlpha && mediaType is "image/webp" or "image/avif" or "image/jxl" or "image/tiff";
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"{Guid.NewGuid():N}.{(jpeg ? "jpg" : "png")}");
            image.Quality = 90;
            image.Write(file, jpeg ? ImageMagick.MagickFormat.Jpeg : ImageMagick.MagickFormat.Png);
            return (file, jpeg ? "image/jpeg" : "image/png");
        }
        catch (ImageMagick.MagickException)
        {
            return null;
        }
    }

    /// <summary>The media type of a picture file by extension, or null for files that are not pictures.</summary>
    public static string? ImageMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".avif" => "image/avif",
        ".jxl" => "image/jxl",
        _ => null,
    };
}
