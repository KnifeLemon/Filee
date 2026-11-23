// The export of a video's marked frames (#1205 step 3) through the real job queue and engines: one PDF with a page
// per frame, written and dated whatever the regional calendar, or a folder of PNGs each with its strip; a name already
// taken gets a number; a folder that can't be written fails the job and leaves the marked copies as they are. What a
// page shows is FramePdfTests'.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

/// <summary>
/// Tests that set the process's default regional format (<see cref="CultureInfo.DefaultThreadCurrentCulture"/>), so the
/// job queue's threads see it. Nothing else runs beside them: a calendar test running alongside would read its dates in
/// the Persian or Umm al-Qura calendar.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SetsTheRegionalFormat
{
    public const string Name = "Sets the regional format";
}

[Collection(SetsTheRegionalFormat.Name)]
public class FrameExportJobTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string FrameWord = "frame {0}";

    /// <summary>A video file and three marked copies of its frames, one with a note in three scripts.</summary>
    private (string Video, List<string> Copies) Marked()
    {
        var folder = fx.NewFolder();
        var video = Path.Combine(folder, "Site walk.mp4");
        File.WriteAllBytes(video, new byte[1000]);
        var copies = new List<string>();
        foreach (var (seconds, note) in new[] { (41.633, "Crack above the door frame"), (124.7, "配線が緩い · كابل مفكوك · loose 🔌"), (247.267, "") })
        {
            var path = Path.Combine(folder, MarkedName(seconds));
            using var image = new MagickImage(new MagickColor("#3E63DD"), 640, 360);
            image.SetAttribute(FrameTags.Key, FrameTags.ToText(new FrameFacts("Site walk.mp4", 312.4, 1000, seconds, (long)(seconds * 30), note)));
            image.Write(path, MagickFormat.Png);
            copies.Add(path);
        }
        return (video, copies);
    }

    private static string MarkedName(double seconds) => $"Site walk {seconds:0.000}s Annotated.png";

    private static int Pages(string pdf) =>
        System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)), @"/Type\s*/Page[^s]").Count;

    [Fact]
    public async Task One_pdf_a_page_per_frame_next_to_the_video()
    {
        var (video, copies) = Marked();
        var job = await fx.ConvertAsync(copies, FrameExport.Pdf(video, letter: false, FrameWord));
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var pdf = Assert.Single(job.Outputs.Distinct());
        Assert.Equal(Path.Combine(Path.GetDirectoryName(video)!, "Site walk frames.pdf"), pdf);
        Assert.Equal(3, Pages(pdf));
    }

    /// <summary>
    /// Where the regional format's calendar isn't the Gregorian one (Umm al-Qura, Thai Buddhist, Persian), the PDF is
    /// written and dated this year: PDF dates are Gregorian, whatever the user's calendar.
    /// </summary>
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("fa-IR")]
    public async Task The_pdf_is_written_whatever_the_regional_calendar(string region)
    {
        var (video, copies) = Marked();
        var (current, ui, threads) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, CultureInfo.DefaultThreadCurrentCulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(region);
            var job = await fx.ConvertAsync(copies, FrameExport.Pdf(video, letter: false, FrameWord));
            Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
            var pdf = Assert.Single(job.Outputs.Distinct());
            Assert.Equal(3, Pages(pdf));
            // Dated this year, not the regional calendar's year read as a Gregorian one (2569 under Thailand's).
            var year = DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture);
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
            Assert.Contains($"/CreationDate(D:{year}", text.Replace(" ", ""));
            Assert.Contains($"<xmp:CreateDate>{year}", text);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, CultureInfo.DefaultThreadCurrentCulture) = (current, ui, threads);
        }
    }

    [Fact]
    public async Task A_folder_of_pngs_each_with_its_strip_and_the_copies_left_alone()
    {
        var (video, copies) = Marked();
        var before = copies.Select(File.ReadAllBytes).ToList();
        var job = await fx.ConvertAsync(copies, FrameExport.Pngs(video, FrameWord));
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var folder = Path.Combine(Path.GetDirectoryName(video)!, "Site walk frames");
        Assert.Equal(copies.Select(Path.GetFileName).Order(), job.Outputs.Select(o => Path.GetFileName(o)).Order());
        foreach (var output in job.Outputs)
        {
            Assert.Equal(folder, Path.GetDirectoryName(output));
            using var image = new MagickImage(output);
            Assert.Equal(640u, image.Width);
            Assert.True(image.Height > 360, $"{output}: no strip");
            Assert.Null(FrameTags.ReadFile(output)); // an export isn't a marked copy: the tray doesn't list it
        }
        Assert.Equal(before, copies.Select(File.ReadAllBytes).ToList());
    }

    [Fact]
    public async Task A_taken_name_gets_a_number()
    {
        var (video, copies) = Marked();
        var taken = Path.Combine(Path.GetDirectoryName(video)!, "Site walk frames.pdf");
        File.WriteAllText(taken, "someone's own");
        var job = await fx.ConvertAsync(copies, FrameExport.Pdf(video, letter: true, FrameWord));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(video)!, "Site walk frames (2).pdf"), Assert.Single(job.Outputs.Distinct()));
        Assert.Equal("someone's own", File.ReadAllText(taken));
    }

    [Fact]
    public async Task A_folder_that_cant_be_written_fails_the_job_and_keeps_the_copies()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("A read-only folder still takes new files on Windows; the attribute only protects the folder itself.");
        var (video, copies) = Marked();
        var folder = Path.GetDirectoryName(video)!;
        var before = copies.Select(File.ReadAllBytes).ToList();
        if (OperatingSystem.IsWindows())
            return; // skipped above; this tells the platform analyzer so
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (Environment.UserName == "root")
                Assert.Skip("root writes into a read-only folder all the same");
            var job = await fx.ConvertAsync(copies, FrameExport.Pdf(video, letter: false, FrameWord));
            Assert.NotEqual(JobState.Completed, job.State);
            Assert.Empty(job.Outputs);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.Equal(before, copies.Select(File.ReadAllBytes).ToList());
    }
}
