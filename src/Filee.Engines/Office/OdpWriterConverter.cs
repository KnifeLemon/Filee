// Anything → ODP slides without LibreOffice: OdpWriter (Odf/Draw) writes OpenDocument presentations from the slide
// model.
//
// - PowerPoint decks (PPTX, PPTM, POTX, POTM, PPSX, and PowerPoint 97–2003 as the PPTX package it translates to) are
//   read by the slide renderer's reader (PptxSlideReader) with their speaker notes (PptxNotes): charts become ODF chart
//   objects, outer shadows, glow and soft edges LibreOffice's shadow, glow and soft edge attributes, reflections a
//   faded mirror copy, a shape's turn in space and extrusion ODF extrusion attributes. Their edges are checked with the
//   renderer's capability check for the ODP writer: a deck with charts nothing reads and no picture of, inner shadows,
//   blur, WordArt or other things the model leaves out takes LibreOffice's route where LibreOffice is installed.
// - Every other format the document readers read (Markdown, the markup languages, Word, ODT, HWP / HWPX, HTML,
//   spreadsheets, e-books, ...) is laid out on slides by the PPTX writer first (a slide per heading, Pandoc's rules),
//   then written as the same deck in ODP, so both writers give the same slides.
//
// The edges cost 8, as the PPTX writer's, below LibreOffice's fallback (25); they are DirectOnly, so a deck written
// here is never a step towards another format (a document → PDF stays off slides). That keeps them from leading on
// to the template and the flat file too, so the writer makes those itself: the ODP it writes, turned into OTP or FODP
// (Odf/OdfVariants). OpenDocument presentations themselves (ODP, OTP, FODP) are not sources: they become each other
// through OdfVariantConverter, which keeps the whole file as it is, and keep LibreOffice's route to PPTX (there is no
// PresentationML writer of the slide model yet).

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Odf;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Office.Ppt;
using Filee.Engines.Slides;

namespace Filee.Engines.Office;

/// <summary>Writes ODP presentations with the built-in OpenDocument presentation writer.</summary>
public sealed class OdpWriterConverter : IConverter, ICapabilityCheck
{
    /// <summary>As the PPTX writer's: below LibreOffice's fallback (25) whatever the engine order adds (at most 9).</summary>
    internal const int Cost = 8;

    /// <summary>What the writer writes: the presentation, its template and its flat file.</summary>
    private static readonly string[] Targets = ["odp", "otp", "fodp"];

    /// <summary>The PowerPoint decks the slide reader reads.</summary>
    internal static readonly string[] Decks = [.. SlideFormats.Decks.Where(f => !OdfDrawReader.Presentations.Contains(f))];

    /// <summary>Formats that don't become slides through the PPTX writer: decks (their own slides), PDF (its own slides) and ODF presentations.</summary>
    private static readonly HashSet<string> NotDocuments = new([.. SlideFormats.Decks, "pdf"], StringComparer.OrdinalIgnoreCase);

    private bool? _pandoc;

    public string Id => "odp-writer";
    public string DisplayName => "ODP writer (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges
    {
        get
        {
            var documents = Documents(_pandoc ??= PandocConverter.Locate() is not null);
            return
            [
                .. from source in Decks from to in Targets select new ConversionEdge(source, to, Cost, DirectOnly: true, Checked: true),
                .. from source in documents
                   from to in Targets
                   select new ConversionEdge(source, to, Cost, DirectOnly: true, Checked: DocumentReaders.Checked(source)),
            ];
        }
    }

    private static IEnumerable<string> Documents(bool pandoc) => DocumentReaders.Formats(pandoc).Where(f => !NotDocuments.Contains(f));

    /// <summary>
    /// For a deck, the first thing the slide model leaves out (charts nothing reads, inner shadows, WordArt, ...; a
    /// PowerPoint 95 file); for a document, a Word 6.0 / 95 or Works file the DOC reader doesn't read.
    /// </summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat) =>
        Decks.Contains(sourceFormat, StringComparer.OrdinalIgnoreCase)
            ? SlideFormats.FirstUnsupported(sourcePath, sourceFormat, forOdp: true)
            : DocumentReaders.Unsupported(sourcePath, sourceFormat);

    public EngineStatus GetStatus()
    {
        _pandoc = PandocConverter.Locate() is not null;
        return EngineStatus.Available(string.Join(", ", Decks.Concat(Documents(_pandoc.Value)).Select(f => f.ToUpperInvariant())) + " → ODP, OTP, FODP",
            EngineVersions.BuiltIn);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        string deckPath;
        if (Decks.Contains(step.From, StringComparer.OrdinalIgnoreCase))
        {
            deckPath = step.InputPath;
            if (step.From == PptReader.Format)
            {
                // Translated to PPTX once, for the slides and their notes.
                deckPath = Path.Combine(step.WorkDirectory, "slides.pptx");
                await Task.Run(() => PptReader.ToPptx(step.InputPath, deckPath, cancellationToken: cancellationToken), cancellationToken);
            }
        }
        else
        {
            var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
            progress?.Report(0.4);
            deckPath = Path.Combine(step.WorkDirectory, "slides.pptx");
            await Task.Run(() => PptxWriter.Write(document, deckPath, cancellationToken), cancellationToken);
        }
        progress?.Report(0.6);

        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];
        await Task.Run(() =>
        {
            var deck = PptxSlideReader.Read(deckPath, cancellationToken: cancellationToken);
            var notes = PptxNotes.Read(deckPath, cancellationToken: cancellationToken);
            if (step.To == "odp")
            {
                OdpWriter.Write(deck, output, notes, cancellationToken: cancellationToken);
                return;
            }
            // The template or the flat file: the presentation written first, then turned into it.
            var odp = Path.Combine(step.WorkDirectory, $"deck-{Guid.NewGuid():N}.odp");
            try
            {
                OdpWriter.Write(deck, odp, notes, cancellationToken: cancellationToken);
                OdfVariants.Convert(odp, "odp", output, step.To, cancellationToken: cancellationToken);
            }
            finally
            {
                File.Delete(odp);
            }
        }, cancellationToken);
        progress?.Report(1);
        return [output];
    }
}
