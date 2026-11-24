// The export of a video's marked frames (#1205 step 3): one PDF next to the video, a page per frame on A4 (Letter in
// the United States) with the frames' time and note under each picture and the pages numbered; or a folder of PNGs
// next to the video, each with the strip below it; a name already taken (the PDF's, or the folder's by a file or a
// folder) gets a number; a video's name is cut to fit a file name and kept as written ("{date}" as "(date)", which the
// card shows as the file gets it).

using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class FrameExportTests
{
    private static readonly string Video = Path.Combine(Path.GetTempPath(), "walks", "Site walk.mp4");

    [Theory]
    [InlineData(false, PdfPageSize.A4)]
    [InlineData(true, PdfPageSize.Letter)]
    public void One_pdf_next_to_the_video_a_page_per_frame(bool letter, PdfPageSize paper)
    {
        var preset = FrameExport.Pdf(Video, letter, "frame {0}");
        Assert.Equal("pdf", preset.TargetFormat);
        Assert.True(preset.Pdf.MergeIntoSingle);
        Assert.Equal(paper, preset.Pdf.PageSize);
        Assert.Equal(12, preset.Pdf.MarginMm);
        Assert.True(preset.Pdf.PageNumbers);
        Assert.Equal("frame {0}", preset.Image.FrameCaption);
        Assert.Null(preset.Image.Frame);
        Assert.Equal(OutputLocation.CustomFolder, preset.Output.Location);
        Assert.Equal(Path.GetDirectoryName(Video), preset.Output.CustomFolder);
        Assert.Equal("Site walk frames", preset.Output.FileNamePattern);
        Assert.Equal(ConflictPolicy.Rename, preset.Output.Conflict);
        Assert.False(preset.Output.ReplaceOriginal);
        Assert.False(preset.Output.TrashOriginalAfterConvert);
    }

    [Fact]
    public void A_folder_of_pngs_next_to_the_video_named_as_the_copies()
    {
        var preset = FrameExport.Pngs(Video, "frame {0}");
        Assert.Equal("png", preset.TargetFormat);
        Assert.Equal("frame {0}", preset.Image.FrameCaption);
        Assert.Equal(OutputLocation.CustomFolder, preset.Output.Location);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Video)!, "Site walk frames"), preset.Output.CustomFolder);
        Assert.Equal("{name}", preset.Output.FileNamePattern);
        Assert.Equal(ConflictPolicy.Rename, preset.Output.Conflict);
        Assert.False(preset.Output.ReplaceOriginal);
        Assert.False(preset.Output.TrashOriginalAfterConvert);
    }

    [Fact]
    public void A_videos_name_with_a_token_in_it_is_kept_as_written()
    {
        var odd = Path.Combine(Path.GetTempPath(), "Minutes {date} {Name}.mov");
        Assert.Equal("Minutes (date) (Name) frames", FrameExport.Pdf(odd, false, "frame {0}").Output.FileNamePattern);
        Assert.EndsWith("Minutes (date) (Name) frames", FrameExport.Pngs(odd, "frame {0}").Output.CustomFolder, StringComparison.Ordinal);
        // Characters a Mac or Linux name can have and a Windows one can't (":" is left out: Windows reads it as part
        // of the path, so no name there has it).
        Assert.Equal("a_b_c frames", FrameExport.NameFor(Path.Combine(Path.GetTempPath(), "a|b?c.mp4")));
    }

    /// <summary>
    /// A video's name too long for a file name with " frames" and ".pdf" after it is cut to whole characters, as a
    /// marked copy's own name is (step 2): within 255 bytes with a " (99)", in any script.
    /// </summary>
    [Theory]
    [InlineData("a", 246)]
    [InlineData("現場", 120)]
    [InlineData("👷", 80)]
    public void A_long_videos_name_is_cut_to_fit_a_file_name(string piece, int times)
    {
        var video = Path.Combine(Path.GetTempPath(), string.Concat(Enumerable.Repeat(piece, times)) + ".mp4");
        var name = FrameExport.NameFor(video);
        Assert.EndsWith(" frames", name, StringComparison.Ordinal);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(name + " (99).pdf"), 1, 255);
        Assert.True(name[..^" frames".Length].Length % piece.Length == 0, "a character cut in half");
    }

    /// <summary>
    /// Where the export goes: the PDF, or a new folder for the PNGs, never into an earlier export's folder or onto a
    /// file of that name.
    /// </summary>
    [Fact]
    public void A_name_taken_by_a_file_or_a_folder_gets_a_number()
    {
        var folder = Directory.CreateTempSubdirectory("filee-export-").FullName;
        try
        {
            var video = Path.Combine(folder, "Site walk.mp4");
            Assert.Equal(Path.Combine(folder, "Site walk frames.pdf"), FrameExport.Target(video, pdf: true));
            Assert.Equal(Path.Combine(folder, "Site walk frames"), FrameExport.Target(video, pdf: false));
            Directory.CreateDirectory(Path.Combine(folder, "Site walk frames"));
            File.WriteAllText(Path.Combine(folder, "Site walk frames (2)"), "a file");
            File.WriteAllText(Path.Combine(folder, "Site walk frames.pdf"), "an export");
            Assert.Equal(Path.Combine(folder, "Site walk frames (3)"), FrameExport.Target(video, pdf: false));
            Assert.Equal(Path.Combine(folder, "Site walk frames (3)"), FrameExport.Pngs(video, "frame {0}").Output.CustomFolder);
            Assert.Equal(Path.Combine(folder, "Site walk frames (2).pdf"), FrameExport.Target(video, pdf: true));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("en-US", true)]
    [InlineData("es-US", true)]
    [InlineData("en-GB", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-CA", false)]
    public void Letter_only_in_the_United_States(string culture, bool letter)
    {
        Assert.Equal(letter, FrameExport.UsesLetter(new System.Globalization.RegionInfo(culture)));
    }
}
