// The strip a marked frame gets in an export (#1205): below the picture, never over it, with the frame's time and
// number (bold) and its note (regular, none when blank), each wrapped to the picture's width, right-aligned when it
// reads right to left, and as large on a 4K frame as on a phone's; a picture that isn't a marked frame is left as it
// is.

using Filee.Core.Presets;
using Filee.Engines.Annotations;
using ImageMagick;

namespace Filee.Engines.Tests;

public class FrameCaptionTests
{
    private const string FrameWord = "frame {0}";

    private static MagickImage Frame(uint width, uint height, string note, double seconds = 124.7, long number = 3741)
    {
        var image = new MagickImage(new MagickColor("#3E63DD"), width, height);
        image.SetAttribute(FrameTags.Key, FrameTags.ToText(new FrameFacts("Site walk.mp4", 4000, 48_211_932, seconds, number, note)));
        return image;
    }

    private static void Caption(MagickImage image, string frameWord = FrameWord) =>
        FrameCaption.Apply(image, new ImageOptions { FrameCaption = frameWord }, FrameTags.FromText(image.GetAttribute(FrameTags.Key)));

    /// <summary>Left, right, top and bottom of the dark ink in the rows from <paramref name="top"/> down.</summary>
    private static (int Left, int Right, int Top, int Bottom) Ink(IMagickImage<byte> image, int top)
    {
        var (left, right, inkTop, bottom) = (int.MaxValue, -1, int.MaxValue, -1);
        using var pixels = image.GetPixels();
        for (var y = top; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (pixels.GetPixel(x, y).ToColor() is { } c && c.R < 128 && c.G < 128 && c.B < 128)
                    (left, right, inkTop, bottom) = (Math.Min(left, x), Math.Max(right, x), Math.Min(inkTop, y), Math.Max(bottom, y));
        return (left, right, inkTop, bottom);
    }

    [Fact]
    public void The_strip_goes_below_and_leaves_the_picture_alone()
    {
        using var image = Frame(1280, 720, "Loose cable, left of the panel");
        using var before = (MagickImage)image.Clone();
        Caption(image);
        Assert.Equal(1280u, image.Width);
        Assert.True(image.Height > 720, $"height {image.Height}");
        using var top = (MagickImage)image.Clone();
        top.Crop(new MagickGeometry(0, 0, 1280, 720));
        top.ResetPage();
        Assert.Equal(0, top.Compare(before, ErrorMetric.Absolute));
        // The strip is white, with dark text in it.
        using var pixels = image.GetPixels();
        var corner = pixels.GetPixel(2, (int)image.Height - 2).ToColor()!;
        Assert.Equal((255, 255, 255), (corner.R, corner.G, corner.B));
        Assert.True(Ink(image, 720).Right >= 0, "no text in the strip");
    }

    [Fact]
    public void A_long_note_wraps_within_the_margins_and_a_short_one_doesnt()
    {
        using var shortNote = Frame(640, 360, "Crack");
        using var longNote = Frame(640, 360, string.Join(' ', Enumerable.Repeat("Loose cable left of the panel", 10)));
        using var cjk = Frame(640, 360, string.Concat(Enumerable.Repeat("配線が緩い", 30))); // no spaces to break at
        Caption(shortNote);
        Caption(longNote);
        Caption(cjk);
        Assert.True(longNote.Height > shortNote.Height + 20, $"{longNote.Height} against {shortNote.Height}");
        Assert.True(cjk.Height > shortNote.Height + 20, $"{cjk.Height} against {shortNote.Height}");
        foreach (var image in new[] { longNote, cjk })
        {
            var ink = Ink(image, 360);
            Assert.True(ink.Left >= 8 && ink.Right <= 640 - 8, $"text from {ink.Left} to {ink.Right}");
        }
    }

    [Fact]
    public void A_right_to_left_note_is_right_aligned()
    {
        using var image = Frame(1280, 720, "كابل مفكوك على يسار اللوحة");
        Caption(image);
        // The note's own line: below the label's line, its ink against the right margin and clear of the left.
        var labelBottom = Ink(image, 720).Top + (int)(1280 / 64f * 1.6);
        var note = Ink(image, labelBottom);
        Assert.True(note.Right > 1280 - 60, $"note ends at {note.Right}");
        Assert.True(note.Left > 640, $"note starts at {note.Left}");
    }

    [Fact]
    public void The_text_scales_with_the_picture()
    {
        // Above the smallest text size (14 px, which a picture narrower than 896 px gets): three times as wide, three
        // times the strip.
        using var small = Frame(1280, 720, "Crack");
        using var large = Frame(3840, 2160, "Crack");
        using var phone = Frame(360, 640, "Crack");
        Caption(small);
        Caption(large);
        Caption(phone);
        var (smallStrip, largeStrip) = ((double)small.Height - 720, (double)large.Height - 2160);
        Assert.InRange(largeStrip / smallStrip, 2.7, 3.3);
        Assert.True(phone.Height - 640 >= 14 * 3, $"a portrait phone frame's strip is {phone.Height - 640} px");
    }

    [Fact]
    public void A_picture_without_frame_tags_or_without_the_option_is_left_as_it_is()
    {
        using var plain = new MagickImage(new MagickColor("#3E63DD"), 320, 180);
        Caption(plain);
        Assert.Equal((320u, 180u), (plain.Width, plain.Height));
        using var tagged = Frame(320, 180, "Crack");
        FrameCaption.Apply(tagged, new ImageOptions(), FrameTags.FromText(tagged.GetAttribute(FrameTags.Key)));
        Assert.Equal((320u, 180u), (tagged.Width, tagged.Height));
    }

    /// <summary>
    /// On a frame too narrow for the time and number on one line (a 176 px QCIF one, an hour in), they wrap within the
    /// margins rather than run off the edge: the number is never cut short.
    /// </summary>
    [Theory]
    [InlineData(176u, 144u)]
    [InlineData(128u, 96u)]
    public void On_a_narrow_frame_the_time_and_number_wrap_within_it(uint width, uint height)
    {
        using var image = Frame(width, height, "", seconds: 3725.3, number: 223_518);
        Caption(image);
        var ink = Ink(image, (int)height);
        Assert.True(ink.Right >= 0 && ink.Right < width - 4, $"text reaches {ink.Right} of {width}");
        Assert.True(ink.Bottom - ink.Top > 14 * 1.6, "the time and number on one line, cut off");
    }

    [Fact]
    public void A_right_to_left_label_is_right_aligned()
    {
        using var image = Frame(1280, 720, "");
        Caption(image, "الإطار {0}");
        var ink = Ink(image, 720);
        Assert.True(ink.Right > 1280 - 60, $"label ends at {ink.Right}");
        Assert.True(ink.Left > 640, $"label starts at {ink.Left}");
    }

    [Fact]
    public void The_note_is_regular_and_the_time_bold_and_a_blank_note_is_none()
    {
        // The same words are narrower in the regular face.
        Assert.True(FrameCaption.Width("Loose cable left of the panel", 20, bold: false) < FrameCaption.Width("Loose cable left of the panel", 20, bold: true));
        var facts = new FrameFacts("Site walk.mp4", 312.4, 1000, 124.7, 3741, "Crack");
        var lines = FrameCaption.Lines(facts, FrameWord, 14, 600);
        Assert.Equal([true, false], lines.Select(line => line.Bold));
        Assert.Single(FrameCaption.Lines(facts with { Note = "   " }, FrameWord, 14, 600));
    }
}
