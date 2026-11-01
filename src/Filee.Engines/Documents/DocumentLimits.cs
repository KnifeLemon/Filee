// How much of an untrusted document Filee reads and lays out, shared by the document readers (HWPX, DOCX, OFD and
// HWP 5.0) and the layout engine, so a hostile file can't hang, exhaust the memory or crash Filee. Every limit is far
// above what real documents need: a document that passes one is refused with error.document_too_complex rather than
// read in part.
//
// Where they apply:
//  * DocumentPackage   ZIP packages: entries, bytes per part and in total (counted while unpacking, so a header that
//                      understates a part does not help), the inflate ratio, XML depth and elements (SafeXml).
//  * DocumentBudget    readers: nesting of tables, text boxes, notes, groups and fields; paragraphs and objects.
//  * PictureSize       pictures whose header claims more than MaxPicturePixels are left out.
//  * LayoutGuard       the layout loop: pages, empty pages in a row, the time budget and cancellation.
//  * CompoundFile      compound files (HWP 5.0, DOC): sector chains and the directory are walked once per sector or
//                      entry (a loop is damage), streams count against MaxPartBytes / MaxPackageBytes and, deflated,
//                      the inflate ratio; HWP records against MaxXmlElements.
//
// Embedded fonts are not read by any reader (DOCX odttf parts, HWPX and OFD font files keep only their names). A
// reader that ever uses them must pass them through Filee's own SFNT parser (Fonts/SfntFont) before Skia or HarfBuzz.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Documents;

/// <summary>Limits for reading and laying out one document.</summary>
internal sealed record DocumentLimits
{
    /// <summary>The limits Filee uses.</summary>
    public static DocumentLimits Default { get; } = new();

    /// <summary>The error key of a document refused for passing a limit.</summary>
    public const string ErrorKey = "error.document_too_complex";

    /// <summary>Bytes one part of a package (an XML part, a picture) may unpack to.</summary>
    public long MaxPartBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Bytes all parts read from one package may unpack to together.</summary>
    public long MaxPackageBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Entries in a package (real documents have a few per page or picture).</summary>
    public int MaxPackageEntries { get; init; } = 100_000;

    /// <summary>
    /// How many times its compressed size a part may unpack to, once it is larger than <see cref="InflateRatioFloor"/>.
    /// Deflate reaches about 1,000 on runs of one byte; XML from word processors stays far below 100.
    /// </summary>
    public int MaxInflateRatio { get; init; } = 200;

    /// <summary>What a part may always unpack to, whatever its ratio (small parts of repeated markup compress well).</summary>
    public long InflateRatioFloor { get; init; } = 4L * 1024 * 1024;

    /// <summary>Levels of XML elements in a part. Tables nested <see cref="MaxNestingDepth"/> deep stay well within.</summary>
    public int MaxXmlDepth { get; init; } = 512;

    /// <summary>XML elements in all parts of a package together (each costs some 100 bytes of memory in the tree).</summary>
    public int MaxXmlElements { get; init; } = 5_000_000;

    /// <summary>Levels of tables in tables, text boxes, notes, groups and fields.</summary>
    public int MaxNestingDepth { get; init; } = 32;

    /// <summary>Paragraphs in a document (cells, notes, headers and text boxes included).</summary>
    public int MaxParagraphs { get; init; } = 1_000_000;

    /// <summary>Tables, pictures, shapes, text boxes and equations in a document.</summary>
    public int MaxObjects { get; init; } = 200_000;

    /// <summary>
    /// Columns of a table (as many as a spreadsheet has). Column numbers and spans beyond are cut there, so a damaged
    /// cell address can't make a table of two billion columns.
    /// </summary>
    public int MaxTableColumns { get; init; } = 16_384;

    /// <summary>Pages the layout engine makes of one document.</summary>
    public int MaxPages { get; init; } = 20_000;

    /// <summary>Pixels of one picture (16,384 × 16,384, beyond every camera); bigger pictures are left out.</summary>
    public long MaxPicturePixels { get; init; } = 16_384L * 16_384;

    /// <summary>Pages in a row with nothing placed on them before the layout counts as stuck.</summary>
    public int MaxConsecutiveEmptyPages { get; init; } = 64;

    /// <summary>How long laying out one document may take.</summary>
    public TimeSpan LayoutTimeBudget { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The XML limits of one part, given how many elements the package's other parts held.</summary>
    internal XmlLimits Xml(int elementsBefore, bool preserveWhitespace) =>
        new(MaxXmlDepth, MaxXmlElements, MaxPartBytes, preserveWhitespace, ElementsBefore: elementsBefore);

    /// <summary>The refusal for a document that passes a limit; <paramref name="detail"/> says which (in English).</summary>
    public static ConversionRefusedException Exceeded(string detail) => new(ErrorKey, detail);

    /// <summary>"64 MB" for messages.</summary>
    internal static string Megabytes(long bytes) => (bytes / (1024 * 1024)).ToString("N0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>"20,000" for messages.</summary>
    internal static string Count(long count) => count.ToString("N0", CultureInfo.InvariantCulture);
}
