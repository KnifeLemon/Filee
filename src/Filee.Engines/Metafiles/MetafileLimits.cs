// How much of an untrusted EMF or WMF file the metafile renderer reads and draws, so a hostile file can't hang,
// exhaust the memory or crash Filee. Every limit is far above what real metafiles need (clip art has hundreds of
// records, a chart a few thousand): a file that passes one is refused with error.document_too_complex rather than
// drawn in part. Damaged files (a record longer than the file, a count its record can't hold, an object index outside
// the table) stop with an InvalidDataException that says which record is wrong.
//
// Where they apply:
//  * MetafileReader   the file size and the number of records.
//  * GdiPlayer        the object table, saved device contexts, path points and region rectangles, and the drawing
//                     operations a few bytes can multiply: the cells a gradient triangle is drawn as, recoloured
//                     pattern brushes, and the region operations clip records combine regions with.
//  * Dib              the pixels of one bitmap and of all bitmaps of the file together (recoloured patterns too).

using Filee.Engines.Documents;

namespace Filee.Engines.Metafiles;

/// <summary>Limits for reading and drawing one metafile.</summary>
internal sealed record MetafileLimits
{
    /// <summary>The limits Filee uses.</summary>
    public static MetafileLimits Default { get; } = new();

    /// <summary>Bytes of one metafile (a scanned page stored as a bitmap in an EMF is some tens of MB).</summary>
    public long MaxFileBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Records in one file.</summary>
    public int MaxRecords { get; init; } = 5_000_000;

    /// <summary>Entries of the object table (pens, brushes, fonts, palettes, regions); EMF and WMF count them in 16 bits.</summary>
    public int MaxObjects { get; init; } = 65_535;

    /// <summary>Device contexts saved (SaveDC) and not restored.</summary>
    public int MaxSavedStates { get; init; } = 1_024;

    /// <summary>Points of the path a BeginPath … EndPath bracket builds.</summary>
    public int MaxPathPoints { get; init; } = 4_000_000;

    /// <summary>Rectangles of one region (a clip or fill region).</summary>
    public int MaxRegionRectangles { get; init; } = 100_000;

    /// <summary>Pixels of one bitmap (8,192 × 8,192).</summary>
    public long MaxBitmapPixels { get; init; } = 8_192L * 8_192;

    /// <summary>Pixels of all bitmaps of one file together.</summary>
    public long MaxTotalBitmapPixels { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Drawing operations one record can multiply, for the whole file: a cell of a gradient triangle or a gradient
    /// rectangle, a recoloured pattern brush, the square of the points of a region operation done with PathOps, and
    /// one for every grid row the edges of a larger region operation cross. Records that draw one shape each are
    /// bounded by <see cref="MaxRecords"/> instead.
    /// </summary>
    public long MaxOperations { get; init; } = 1_000_000;

    /// <summary>
    /// Points of the two regions of a region operation (clip regions combined with AND, OR, XOR or DIFF) that PathOps
    /// combines exactly. PathOps slows down much faster than the square of the points when edges cross (a 400-point
    /// star takes most of a second), so larger regions are combined on a grid over the picture, as GDI does.
    /// </summary>
    public int MaxRegionOperationPoints { get; init; } = 32;

    /// <summary>The refusal for a file that passes a limit; <paramref name="detail"/> says which (in English).</summary>
    public static Core.Conversion.ConversionRefusedException Exceeded(string detail) => DocumentLimits.Exceeded(detail);
}

/// <summary>What one metafile has used of its <see cref="MetafileLimits"/> so far.</summary>
internal sealed class MetafileBudget(MetafileLimits limits)
{
    private long _pixels;
    private long _operations;

    public MetafileLimits Limits { get; } = limits;

    /// <summary>Counts the pixels of a bitmap about to be decoded.</summary>
    public void Bitmap(long width, long height)
    {
        var pixels = width * height;
        if (pixels > Limits.MaxBitmapPixels)
            throw MetafileLimits.Exceeded($"A bitmap in the metafile has {DocumentLimits.Count(width)} × {DocumentLimits.Count(height)} pixels, more than {DocumentLimits.Count(Limits.MaxBitmapPixels)}.");
        _pixels += pixels;
        if (_pixels > Limits.MaxTotalBitmapPixels)
            throw MetafileLimits.Exceeded($"The bitmaps in the metafile have more than {DocumentLimits.Count(Limits.MaxTotalBitmapPixels)} pixels together.");
    }

    /// <summary>Counts <paramref name="count"/> drawing operations of <paramref name="what"/> (in English, for the refusal).</summary>
    public void Operations(long count, string what)
    {
        _operations += count;
        if (_operations > Limits.MaxOperations)
            throw MetafileLimits.Exceeded($"The metafile needs more than {DocumentLimits.Count(Limits.MaxOperations)} drawing operations ({what}).");
    }
}
