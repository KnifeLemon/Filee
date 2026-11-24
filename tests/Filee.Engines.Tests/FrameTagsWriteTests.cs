// A marked video frame's copy (#1205) through the real job queue and image engine: the Annotate tool's conversion with
// the frame's facts writes them as one plain text that reads back as it was, notes of any length in any script
// included, also when the tool drops metadata (and then nothing else is written) and when nothing is drawn on the
// frame, leaving the picture's pixels alone; and a picture made from a marked frame's copy isn't taken for one.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class FrameTagsWriteTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly FrameFacts Facts = new("Site walk.mp4", 312.4, 48_211_932, 124.7, 3741, "配線が緩い · كابل مفكوك 🔌");

    /// <summary>
    /// A small picture, with a title, an author and EXIF of its own when asked, and earlier frame text.
    /// </summary>
    private string Source(bool ownText = false, FrameFacts? earlier = null)
    {
        var path = Path.Combine(fx.NewFolder(), "Site walk 2m04.700s.png");
        using var source = new MagickImage(MagickColors.White, 64, 36);
        new ImageMagick.Drawing.Drawables().FillColor(new MagickColor("#3E63DD")).Rectangle(10, 5, 30, 20).Draw(source);
        if (ownText)
        {
            source.SetAttribute("Title", "Site visit");
            source.SetAttribute("Author", "Someone");
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Someone");
            source.SetProfile(exif);
        }
        if (earlier is not null)
            source.SetAttribute(FrameTags.Key, FrameTags.ToText(earlier));
        source.Write(path, MagickFormat.Png);
        return path;
    }

    private async Task<string> Save(string path, FrameFacts? facts, bool keepMetadata = true)
    {
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.AnnotateId);
        var conversion = AnnotateTool.ConversionOf(tool, BuiltInData.SameAsSource, []);
        conversion.Image.KeepMetadata = keepMetadata;
        conversion.Image.Frame = facts;
        var job = await fx.ConvertAsync([path], conversion);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    /// <summary>The keywords of a PNG's tEXt, zTXt and iTXt chunks.</summary>
    private static List<string> TextKeywords(string png)
    {
        var bytes = File.ReadAllBytes(png);
        var keywords = new List<string>();
        for (var i = 8; i + 8 <= bytes.Length;)
        {
            var length = (bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3];
            var type = System.Text.Encoding.ASCII.GetString(bytes, i + 4, 4);
            if (type is "tEXt" or "zTXt" or "iTXt")
                keywords.Add(System.Text.Encoding.Latin1.GetString(bytes, i + 8, Array.IndexOf(bytes, (byte)0, i + 8) - (i + 8)));
            i += 12 + length;
        }
        return keywords;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // the tool set to drop metadata
    public async Task A_frame_saved_with_no_marks_keeps_its_facts_and_its_pixels(bool keepMetadata)
    {
        var path = Source();
        var output = await Save(path, Facts, keepMetadata);
        using var result = new MagickImage(output);
        Assert.Equal(Facts, FrameTags.FromText(result.GetAttribute(FrameTags.Key)));
        // As Cut a clip's tray reads it: the text chunks alone, the picture left undecoded.
        Assert.Equal(Facts, FrameTags.ReadFile(output));
        using var original = new MagickImage(path);
        Assert.Equal(0, result.Compare(original, ErrorMetric.Absolute));
    }

    /// <summary>
    /// Notes as long as a note may be, in scripts Magick would write as compressed UTF-8 (which Magick.NET reads back
    /// as nothing): read back whole, both ways.
    /// </summary>
    [Theory]
    [InlineData("配線")]
    [InlineData("كابل ")]
    [InlineData("Größe – ")]
    [InlineData("🔌")]
    public async Task A_note_of_any_length_in_any_script_reads_back_whole(string piece)
    {
        var note = string.Concat(Enumerable.Repeat(piece, 300 / piece.Length));
        var facts = Facts with { Note = note, VideoName = string.Concat(Enumerable.Repeat("現場", 60)) + ".mp4" };
        var output = await Save(Source(), facts);
        using var result = new MagickImage(output);
        Assert.Equal(facts, FrameTags.FromText(result.GetAttribute(FrameTags.Key)));
        Assert.Equal(facts, FrameTags.ReadFile(output));
    }

    [Fact]
    public async Task Dropping_metadata_drops_the_pictures_own_text_and_keeps_the_frames()
    {
        var output = await Save(Source(ownText: true), Facts, keepMetadata: false);
        Assert.Equal([FrameTags.Key], TextKeywords(output));
        using var result = new MagickImage(output);
        Assert.Null(result.GetExifProfile());
    }

    [Fact]
    public async Task An_earlier_copys_text_is_replaced_as_a_whole()
    {
        var output = await Save(Source(earlier: Facts with { Note = "old note", FrameNumber = 99 }), Facts with { Note = "", FrameNumber = null });
        Assert.Equal(Facts with { Note = "", FrameNumber = null }, FrameTags.ReadFile(output));
    }

    [Fact]
    public async Task A_picture_made_from_a_marked_frames_copy_isnt_taken_for_one()
    {
        var output = await Save(Source(earlier: Facts), facts: null);
        Assert.Null(FrameTags.ReadFile(output));
        Assert.DoesNotContain(FrameTags.Key, TextKeywords(output));
    }

    [Fact]
    public void A_frame_alone_makes_a_conversion_rework_its_pixels()
    {
        Assert.True(new ImageOptions { Frame = Facts }.ReworksPixels);
        Assert.False(new ImageOptions().ReworksPixels);
    }
}
