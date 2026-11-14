// The slide decks Filee's slide renderer reads (PptxSlideReader; OpenDocument presentations through OdfDrawReader): the
// Filee renderer takes them to PDF and to a picture per slide. A reader for another deck format adds its format here.

using Filee.Engines.Odf.Draw;

namespace Filee.Engines.Slides;

internal static class SlideFormats
{
    /// <summary>
    /// PowerPoint 2007+ decks: presentations, macro-enabled ones, templates and slide shows; and OpenDocument
    /// presentations and templates, packaged or flat (Odf/Draw).
    /// </summary>
    public static readonly string[] Decks = ["pptx", "pptm", "potx", "potm", "ppsx", .. OdfDrawReader.Presentations];

    /// <summary>What a deck becomes through the slide renderer.</summary>
    public static readonly string[] Targets = ["pdf", "png", "jpg"];

    /// <summary>Reads a deck of format <paramref name="format"/> with its reader.</summary>
    public static SlideDeck Read(string path, string format, CancellationToken cancellationToken) =>
        OdfDrawReader.Presentations.Contains(format, StringComparer.OrdinalIgnoreCase)
            ? OdfDrawReader.Read(path, cancellationToken: cancellationToken)
            : PptxSlideReader.Read(path, cancellationToken: cancellationToken);

    /// <summary>The first thing in the deck its reader doesn't draw (the capability check of the format); null when it draws everything.</summary>
    public static string? FirstUnsupported(string path, string format, CancellationToken cancellationToken = default) =>
        OdfDrawReader.Presentations.Contains(format, StringComparer.OrdinalIgnoreCase)
            ? OdfDrawReader.FirstUnsupported(path, cancellationToken)
            : SlideCapabilities.FirstUnsupported(path, cancellationToken);
}
