// The export of a video's marked frames (#1205 step 3), as two ordinary conversions over its marked copies: one PDF,
// a page per frame (the copies merged, each with its time and note under the picture, the pages numbered), or a folder
// of PNGs with the time and note in a strip below each picture. Both go next to the video and are named after it (cut
// to fit a file name, without the characters a file name can't have); a name already taken gets a number, the folder's
// too, never overwritten or mixed with an earlier export.

using System.Globalization;

namespace Filee.Core.Presets;

public static class FrameExport
{
    /// <summary>The page margin, in millimetres.</summary>
    private const double MarginMm = 12;

    /// <summary>
    /// One PDF of the copies given to it, in that order: A4, or US Letter when <paramref name="letter"/>, each page
    /// turned to its picture; named "&lt;video&gt; frames" next to <paramref name="video"/>.
    /// </summary>
    /// <param name="frameWord">The translated word for a frame and its number ("frame {0}").</param>
    public static Preset Pdf(string video, bool letter, string frameWord)
    {
        var preset = Base(video, "pdf", frameWord);
        preset.Pdf.MergeIntoSingle = true;
        preset.Pdf.PageSize = letter ? PdfPageSize.Letter : PdfPageSize.A4;
        preset.Pdf.MarginMm = MarginMm;
        preset.Pdf.PageNumbers = true;
        preset.Pdf.JpegAboveWidth = JpegAboveWidth;
        preset.Output.CustomFolder = Path.GetDirectoryName(Path.GetFullPath(video)) ?? "";
        // A name is a pattern there; NameFor has no "{…}" left in it, so it is written as it is.
        preset.Output.FileNamePattern = NameFor(video);
        return preset;
    }

    /// <summary>
    /// The copies given to it as PNGs, each with its strip, in a new folder "&lt;video&gt; frames" next to
    /// <paramref name="video"/> (" (2)" when that name is taken), named as the copies are; in
    /// <paramref name="folder"/> when the caller has chosen it (<see cref="Target"/>).
    /// </summary>
    public static Preset Pngs(string video, string frameWord, string? folder = null)
    {
        var preset = Base(video, "png", frameWord);
        preset.Output.CustomFolder = folder ?? Target(video, pdf: false);
        preset.Output.FileNamePattern = "{name}";
        return preset;
    }

    /// <summary>
    /// The longest a video's name may be in an export's name, in UTF-8 bytes: with " frames", ".pdf" and a " (99)" for
    /// a name already taken it stays within the 255 bytes a file name may have (as a marked copy's name does).
    /// </summary>
    public const int MaxNameBytes = 200;

    /// <summary>
    /// What an export of <paramref name="video"/>'s frames is named, without ".pdf": "Site walk frames". The video's
    /// name is cut to <see cref="MaxNameBytes"/>, characters a file name can't have become "_", and "{…}" becomes
    /// "(…)", as a name pattern would otherwise fill it in.
    /// </summary>
    public static string NameFor(string video) =>
        OutputPathResolver.Literal(OutputPathResolver.Sanitize(OutputPathResolver.Shortened(Path.GetFileNameWithoutExtension(video), MaxNameBytes), "video")
                                   + " frames");

    /// <summary>
    /// Where an export of <paramref name="video"/>'s frames will go: the PDF, or the PNGs' folder, next to it, with
    /// " (2)", " (3)"… when the name is taken by a file or a folder, as the PDF's own conflict rule numbers it, or by
    /// an export queued already (<paramref name="taken"/>: its folder isn't made until its job runs).
    /// </summary>
    public static string Target(string video, bool pdf, Func<string, bool>? taken = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(video)) ?? "";
        var name = NameFor(video);
        var extension = pdf ? ".pdf" : "";
        var path = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(path) || Directory.Exists(path) || taken?.Invoke(path) == true; n++)
            path = Path.Combine(folder, $"{name} ({n}){extension}");
        return path;
    }

    /// <summary>
    /// Frames wider than this many pixels go into the PDF as high-quality JPEG (the owner's choice of 19 Nov 2026): two
    /// grainy 4K test frames took 16 MB a page lossless, 3 to 4 MB as JPEG.
    /// </summary>
    public const int JpegAboveWidth = 1920;

    /// <summary>US Letter paper where the regional format is the United States; A4 everywhere else.</summary>
    public static bool UsesLetter(RegionInfo region) => region.TwoLetterISORegionName == "US";

    private static Preset Base(string video, string target, string frameWord)
    {
        var preset = new Preset { Name = Path.GetFileNameWithoutExtension(video), TargetFormat = target };
        preset.Image.FrameCaption = frameWord;
        preset.Output.Location = OutputLocation.CustomFolder;
        preset.Output.KeepFoldersFrom = "";
        preset.Output.Conflict = ConflictPolicy.Rename;
        preset.Output.ReplaceOriginal = false;
        preset.Output.TrashOriginalAfterConvert = false;
        preset.Output.OnlyIfSmaller = false;
        return preset;
    }
}
