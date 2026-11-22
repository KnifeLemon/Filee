// Marked video frames in a PDF (#1205), through the export's own preset and the real job queue: the picture goes in as
// it is, its time, number and note drawn under it as outlines (no font embedded, no strip in the picture); a frame
// wider than 1920 px goes in as a high-quality JPEG (the owner's decision of 19 Nov 2026) and one at or below it
// lossless; and any other picture put in a PDF is encoded as before.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class FramePdfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly MagickColor Blue = new("#3E63DD");

    private string Picture(uint width, uint height, string? note)
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, $"frame {width}.png");
        using var image = new MagickImage(Blue, width, height);
        new ImageMagick.Drawing.Drawables().FillColor(new MagickColor("#F5A524")).Circle(width / 3.0, height / 3.0, width / 3.0 + height / 8.0, height / 3.0).Draw(image);
        if (note is not null)
            image.SetAttribute(FrameTags.Key, FrameTags.ToText(new FrameFacts("Site walk.mp4", 312.4, 48_211_932, 124.7, 3741, note)));
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>The PDF the export writes of <paramref name="picture"/>, or a plain image-to-PDF one.</summary>
    private async Task<string> PdfOf(string picture, bool export)
    {
        var preset = export
            ? FrameExport.Pdf(Path.Combine(Path.GetDirectoryName(picture)!, "Site walk.mp4"), letter: false, "frame {0}")
            : new Preset { Name = "pictures", TargetFormat = "pdf", Pdf = { PageSize = PdfPageSize.A4, MarginMm = 12 } };
        var job = await fx.ConvertAsync([picture], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs.Distinct());
    }

    private static string Text(string pdf) => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf));

    /// <summary>The PDF's first page as a picture at 100 dpi.</summary>
    private async Task<MagickImage> Page(string pdf)
    {
        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 100, PageRange = "1" } });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return new MagickImage(Assert.Single(job.Outputs));
    }

    /// <summary>
    /// The rows of <paramref name="page"/> holding the picture (its blue), and how many dark pixels lie below it.
    /// </summary>
    private static (int Top, int Bottom, int InkBelow) Measure(MagickImage page)
    {
        using var pixels = page.GetPixels();
        int top = -1, bottom = -1, ink = 0;
        var channels = (int)page.ChannelCount;
        for (var y = 0; y < page.Height; y++)
        {
            var row = pixels.GetArea(0, y, page.Width, 1)!;
            var blue = 0;
            for (var x = 0; x < page.Width; x++)
            {
                var (r, g, b) = (row[x * channels], row[x * channels + 1], row[x * channels + 2]);
                if (Math.Abs(r - 0x3E) < 24 && Math.Abs(g - 0x63) < 24 && Math.Abs(b - 0xDD) < 24)
                    blue++;
            }
            if (blue > page.Width / 8)
            {
                top = top < 0 ? y : top;
                bottom = y;
            }
        }
        // Below the picture, above the footer (the page number's line, 10 mm from the bottom edge).
        var footer = (int)(page.Height - 14 / 25.4 * 100);
        for (var y = bottom + 1; y < footer; y++)
        {
            var row = pixels.GetArea(0, y, page.Width, 1)!;
            for (var x = 0; x < page.Width; x++)
            {
                if (row[x * channels] + row[x * channels + 1] + row[x * channels + 2] < 3 * 110)
                    ink++;
            }
        }
        return (top, bottom, ink);
    }

    [Fact]
    public async Task A_page_has_the_picture_as_it_is_and_its_time_number_and_note_under_it_as_outlines()
    {
        var pdf = await PdfOf(Picture(640, 360, "Crack above the door frame · 配線が緩い · كابل مفكوك"), export: true);
        var text = Text(pdf);
        // The picture is the frame's own pixels, no strip added to it; the text is drawn, no font embedded.
        Assert.Contains("/Width 640", text, StringComparison.Ordinal);
        Assert.Contains("/Height 360", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Font", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type/Font", text.Replace(" ", ""), StringComparison.Ordinal);

        using var withNote = await Page(pdf);
        using var withoutNote = await Page(await PdfOf(Picture(640, 360, ""), export: true));
        using var plain = await Page(await PdfOf(Picture(640, 360, note: null), export: false));
        var (top, bottom, ink) = Measure(withNote);
        Assert.True(top >= 0 && bottom > top, "no picture found on the page");
        // The time and number under every frame; the note's lines add to them.
        var labelOnly = Measure(withoutNote).InkBelow;
        Assert.True(labelOnly > 100, $"no time and number under the picture ({labelOnly} dark pixels)");
        Assert.True(ink > labelOnly * 3 / 2, $"no note under the picture ({ink} dark pixels against {labelOnly} without one)");
        Assert.Equal(0, Measure(plain).InkBelow);
    }

    [Fact]
    public async Task A_wide_frame_goes_in_as_a_quality_92_jpeg_and_a_full_hd_one_lossless()
    {
        var wide = await PdfOf(Picture(3840, 2160, "Crack"), export: true);
        var text = Text(wide);
        Assert.Contains("/DCTDecode", text, StringComparison.Ordinal);
        // The JPEG's own quality, as Magick estimates it from its tables.
        var start = text.IndexOf("stream", text.IndexOf("/DCTDecode", StringComparison.Ordinal), StringComparison.Ordinal) + "stream".Length;
        start += text[start] == '\r' ? 2 : 1;
        var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
        using var jpeg = new MagickImage(File.ReadAllBytes(wide).AsSpan(start, end - start).ToArray());
        Assert.Equal(92u, jpeg.Quality);

        var fullHd = Text(await PdfOf(Picture(1920, 1080, "Crack"), export: true));
        Assert.DoesNotContain("/DCTDecode", fullHd, StringComparison.Ordinal);
        Assert.Contains("/FlateDecode", fullHd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Any_other_picture_in_a_pdf_is_encoded_as_before()
    {
        Assert.DoesNotContain("/DCTDecode", Text(await PdfOf(Picture(3840, 2160, note: null), export: false)), StringComparison.Ordinal);
    }

    /// <summary>
    /// However a picture's shape and a note's length meet (the caption wrapping again once the picture is scaled for
    /// it), the picture and its caption fit above the footer.
    /// </summary>
    [Fact]
    public void The_picture_and_its_caption_always_fit_above_the_footer()
    {
        var box = new PdfSharp.Drawing.XRect(34, 34, 527, 774 - Filee.Engines.Pdf.FramePage.FooterRoom); // A4 portrait, 12 mm margins
        var words = string.Join(' ', Enumerable.Repeat("Loose cable left of the panel", 40)).Split(' ');
        foreach (var (width, height) in new[] { (640.0, 480.0), (1080.0, 1920.0), (1920.0, 1080.0), (480.0, 640.0), (1000.0, 1000.0) })
            for (var count = 0; count <= words.Length; count += 3)
            {
                var facts = new FrameFacts("Site walk.mp4", 312.4, 1000, 124.7, 3741, string.Join(' ', words.Take(count)));
                var layout = Filee.Engines.Pdf.FramePage.Place(box, width, height, facts, "frame {0}");
                var bottom = layout.CaptionTop + Filee.Engines.Annotations.FrameCaption.Height(layout.Lines);
                Assert.True(bottom <= box.Bottom + 0.01, $"{width}x{height}, {count} words: caption ends at {bottom}, room to {box.Bottom}");
                Assert.True(layout.Picture.Bottom <= layout.CaptionTop);
            }
    }
}
