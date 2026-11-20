// What one DjVu document may make Filee read and build: pages, chunk sizes, decompressed (BZZ) bytes, page pixels,
// JB2 shapes, blits and their pixels, IW44 slices and coefficient blocks, text zones, annotation parts. Every limit is
// far beyond what scanners and DjVu encoders write (a 600 dpi A3 scan is 70 million pixels); a document past one is
// refused with error.document_too_complex, naming the limit.

using Filee.Engines.Documents;

namespace Filee.Engines.Djvu;

/// <summary>Limits of the DjVu reader.</summary>
internal sealed record DjvuLimits
{
    public static DjvuLimits Default { get; } = new();

    /// <summary>Pages of one document.</summary>
    public int MaxPages { get; init; } = DocumentLimits.Default.MaxPages;

    /// <summary>Components (pages, shared dictionaries, thumbnails) of a bundled document.</summary>
    public int MaxComponents { get; init; } = 2 * DocumentLimits.Default.MaxPages;

    /// <summary>Bytes of one chunk read into memory (a page's mask, background or text).</summary>
    public long MaxChunkBytes { get; init; } = DocumentLimits.Default.MaxPartBytes;

    /// <summary>Bytes one BZZ stream (text, palette indices, the directory) may unpack to.</summary>
    public long MaxUnpackedBytes { get; init; } = DocumentLimits.Default.MaxPartBytes;

    /// <summary>Pixels of one page (its INFO size), and of one IW44 picture.</summary>
    public long MaxPagePixels { get; init; } = 150_000_000;

    /// <summary>
    /// Pixels of one IW44 picture (a background or foreground layer; encoders store them at a third of the page's
    /// resolution or less, photographs at full size). Three maps of two bytes per pixel are decoded.
    /// </summary>
    public long MaxIw44Pixels { get; init; } = 64_000_000;

    /// <summary>Shapes of one JB2 image or dictionary.</summary>
    public int MaxShapes { get; init; } = 2_000_000;

    /// <summary>Blits (placed shapes) of one JB2 image.</summary>
    public int MaxBlits { get; init; } = 4_000_000;

    /// <summary>Pixels of all shapes of one JB2 image or dictionary together (each costs a byte while decoded).</summary>
    public long MaxShapePixels { get; init; } = 400_000_000;

    /// <summary>Cells of the JB2 number coder between two resets (each costs 9 bytes).</summary>
    public int MaxNumberCells { get; init; } = 20_000_000;

    /// <summary>Coding slices of one IW44 picture (all its chunks together; real files have 100 or fewer).</summary>
    public int MaxSlices { get; init; } = 10_000;

    /// <summary>Text zones of one page.</summary>
    public int MaxTextZones { get; init; } = 2_000_000;

    /// <summary>Colours of a foreground palette.</summary>
    public int MaxPaletteColors { get; init; } = 65_535;

    /// <summary>Parts (brackets, words, strings) of one page's annotations (its hyperlinks and display settings).</summary>
    public int MaxAnnotationNodes { get; init; } = 1_000_000;
}
