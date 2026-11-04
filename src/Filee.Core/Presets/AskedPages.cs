// "Ask every time" for the pages (#981): which drops ask, what the window offers, and the preset as it runs once the
// user has answered.

using Filee.Core.Formats;

namespace Filee.Core.Presets;

public static class AskedPages
{
    /// <summary>
    /// The preset asks for the pages of the PDFs dropped on it (<see cref="PdfOptions.AskPageRange"/>). Not a merge:
    /// one range would go for every file, which the window can't show; a merge keeps <see cref="PdfOptions.PageRange"/>.
    /// </summary>
    public static bool Asks(Preset preset) =>
        preset.Pdf.AskPageRange && !preset.IsGroup && !preset.IsTool && !Merges(preset);

    /// <summary>True for a PDF among the dropped files (also a browser file, by its name), whose pages are asked for.</summary>
    public static bool IsPdf(string path) => FormatRegistry.Detect(path)?.Id == "pdf";

    /// <summary>
    /// The window offers one PDF or one file per page: the result is a PDF made from the pages themselves (not text
    /// recognized into a new one, not several pages to a sheet).
    /// </summary>
    public static bool OffersSplit(Preset preset) =>
        preset.TargetFormat == "pdf" && !preset.RecognizesTextOf("pdf") && !preset.Pdf.MakesSheets;

    /// <summary>
    /// The preset as it runs for one PDF: the <paramref name="range"/> the user typed (checked by
    /// <see cref="Conversion.PageRange.Check"/>), one file per page or one PDF where <see cref="OffersSplit"/>, and no
    /// asking again (a retry from history converts the same pages). A copy; the saved preset is left alone.
    /// </summary>
    public static Preset Apply(Preset preset, string range, bool splitPages)
    {
        var copy = preset.Clone();
        copy.Pdf.PageRange = range.Trim();
        copy.Pdf.AskPageRange = false;
        if (OffersSplit(preset))
        {
            copy.Pdf.SplitPages = splitPages;
            // One PDF needs no folder of its own: the subfolder (Split PDF's "{name}") is for the pages.
            if (!splitPages && copy.Output.Location == OutputLocation.Subfolder)
                copy.Output.SubfolderOnlyForSeveralFiles = true;
        }
        return copy;
    }

    private static bool Merges(Preset preset) => preset.TargetFormat == "pdf" && preset.Pdf.MergeIntoSingle;
}
