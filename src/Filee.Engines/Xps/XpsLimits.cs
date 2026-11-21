// What one XPS document may make Filee build, on top of DocumentLimits (package sizes, XML depth and elements):
// pages, nesting of canvases and visual brushes, drawn elements, points of paths and glyphs, and the length of one
// path's data. Every limit is far beyond what printers and Office write; a document past one is refused, naming the
// limit: with error.document_too_large for pages and sizes, with error.document_too_complex for nesting, elements and points.

using Filee.Engines.Documents;

namespace Filee.Engines.Xps;

/// <summary>Limits of the XPS reader.</summary>
internal sealed record XpsLimits
{
    public static XpsLimits Default { get; } = new();

    /// <summary>The package and picture limits.</summary>
    public DocumentLimits Document { get; init; } = DocumentLimits.Default with
    {
        // Canvases in canvases and visual brushes in visual brushes; printer drivers nest a handful.
        MaxNestingDepth = 64,
        // Paths, glyph runs and canvases: a CAD plot may draw a few hundred thousand.
        MaxObjects = 2_000_000,
    };

    /// <summary>Pages of one document.</summary>
    public int MaxPages { get; init; } = DocumentLimits.Default.MaxPages;

    /// <summary>Points of all paths and glyphs of the pages read together (each costs some 10 bytes).</summary>
    public long MaxPoints { get; init; } = 40_000_000;

    /// <summary>Characters of one path's data (the abbreviated syntax).</summary>
    public int MaxPathData { get; init; } = 32 * 1024 * 1024;

    /// <summary>Entries of a dash pattern used; longer patterns are drawn solid.</summary>
    public int MaxDashes { get; init; } = 1024;

    /// <summary>Largest page side in points (PDF's own limit, 200 inches); larger pages are cut there.</summary>
    public float MaxPageSide { get; init; } = 14_400;
}

/// <summary>Counts what the reader builds against <see cref="XpsLimits"/>.</summary>
internal sealed class XpsBudget(XpsLimits limits)
{
    private long _points;

    public XpsLimits Limits { get; } = limits;

    /// <summary>Nesting and object counts.</summary>
    public DocumentBudget Document { get; } = new(limits.Document);

    /// <summary>Counts <paramref name="count"/> points of a path or glyph run.</summary>
    public void Points(long count)
    {
        _points += count;
        if (_points > Limits.MaxPoints)
            throw DocumentLimits.Exceeded($"The document's paths and glyphs have more than {DocumentLimits.Count(Limits.MaxPoints)} points.");
    }
}
