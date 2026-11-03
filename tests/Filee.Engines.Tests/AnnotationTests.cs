// "Annotate" through the real image engine: each mark lands where it was placed, in its colour and at a thickness that
// follows the picture's size; a highlight lets the picture show through; text and numbers draw (Korean too); the rest
// of the picture keeps its pixels; and the tool runs as an ordinary conversion in the picture's own format.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Annotations;
using ImageMagick;

namespace Filee.Engines.Tests;

public class AnnotationTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static Annotation Mark(AnnotationKind kind, string color, params (double X, double Y)[] points) => new()
    {
        Kind = kind,
        Color = color,
        Points = [.. points.Select(p => new AnnotationPoint { X = p.X, Y = p.Y })],
    };

    private static MagickImage White(uint width = 800, uint height = 400) => new(MagickColors.White, width, height);

    private static void Draw(MagickImage image, params Annotation[] marks) =>
        Annotator.Apply(image, new ImageOptions { Annotations = [.. marks] });

    private static (byte R, byte G, byte B) At(IMagickImage<byte> image, int x, int y)
    {
        var pixel = image.GetPixels().GetPixel(x, y).ToColor()!;
        return (pixel.R, pixel.G, pixel.B);
    }

    [Fact]
    public void A_box_is_drawn_on_its_edges_in_its_colour_and_leaves_the_inside_and_outside_alone()
    {
        using var image = White();
        Draw(image, Mark(AnnotationKind.Box, "#3E63DD", (0.25, 0.25), (0.75, 0.75)));    // 200,100 → 600,300

        Assert.Equal((0x3E, 0x63, 0xDD), At(image, 400, 100));
        Assert.Equal((0x3E, 0x63, 0xDD), At(image, 200, 200));
        Assert.Equal((255, 255, 255), At(image, 400, 200));
        Assert.Equal((255, 255, 255), At(image, 100, 50));
        // Medium is 0.7 % of the shorter side: about 3 px either side of the edge at 400 px.
        Assert.Equal((255, 255, 255), At(image, 400, 100 + 6));
    }

    [Fact]
    public void An_arrow_runs_from_its_tail_to_a_filled_head_at_its_tip()
    {
        using var image = White();
        Draw(image, Mark(AnnotationKind.Arrow, "#E5484D", (0.1, 0.5), (0.9, 0.5)));      // 80,200 → 720,200

        Assert.Equal((0xE5, 0x48, 0x4D), At(image, 300, 200));
        Assert.Equal((0xE5, 0x48, 0x4D), At(image, 712, 200));
        // Near the tip the head is wider than the line (3 px off the middle is head, not line); nothing beyond the tip.
        var head = AnnotationPainter.StrokeOf(AnnotationWidth.Medium, 800, 400) * 4.5f;
        Assert.Equal((0xE5, 0x48, 0x4D), At(image, (int)(720 - head * 0.6f), 203));
        Assert.Equal((255, 255, 255), At(image, 300, 205));
        Assert.Equal((255, 255, 255), At(image, 730, 200));
    }

    [Fact]
    public void A_highlight_lets_the_picture_show_through()
    {
        using var image = new MagickImage(new MagickColor("#202020"), 400, 200);
        Draw(image, Mark(AnnotationKind.Highlight, "#F9D84A", (0.1, 0.1), (0.9, 0.9)));

        var (r, g, b) = At(image, 200, 100);
        Assert.True(r > 0x20 && r < 0xF9 && b < 0x4A + 10, $"{r},{g},{b}");
        Assert.Equal((0x20, 0x20, 0x20), At(image, 10, 10));
    }

    [Fact]
    public void A_pen_line_text_and_numbers_draw_and_their_ink_reads_on_their_colour()
    {
        using var image = White();
        var text = Mark(AnnotationKind.Text, "#E5484D", (0.1, 0.1));
        text.Text = "저장하기 Save";
        var number = Mark(AnnotationKind.Number, "#F9D84A", (0.5, 0.75));
        number.Number = 7;
        Draw(image, Mark(AnnotationKind.Pen, "#30A46C", (0.1, 0.9), (0.3, 0.8), (0.5, 0.9)), text, number);

        // The pen passes through its middle point.
        Assert.Equal((0x30, 0xA4, 0x6C), At(image, 240, 320));
        // The tag starts at its point, filled; the text on it is lighter (white ink on red).
        var tag = AnnotationPainter.TagOf(text, 800, 400);
        Assert.Equal((0xE5, 0x48, 0x4D), At(image, (int)tag.Left + 2, (int)tag.MidY));
        // The Korean letters are drawn in a font that has them: they take room, not nothing.
        var latin = AnnotationPainter.TagOf(new Annotation { Kind = AnnotationKind.Text, Text = "Save", Points = text.Points }, 800, 400);
        Assert.True(tag.Width > latin.Width * 1.8, $"{tag.Width} px against {latin.Width} px for Save alone");
        var row = Enumerable.Range((int)tag.Left, (int)tag.Width).Select(x => At(image, x, (int)tag.MidY)).ToList();
        Assert.Contains(row, p => p.R > 240 && p.G > 200);
        // A yellow number has dark ink, looked for across the whole circle: a font's thin strokes can miss any one row.
        var circle = AnnotationPainter.CircleOf(number, 800, 400);
        var inside = Enumerable.Range((int)circle.Top, (int)circle.Height)
            .SelectMany(y => Enumerable.Range((int)circle.Left, (int)circle.Width).Select(x => At(image, x, y)));
        Assert.Contains(inside, p => p.R < 80);

        // On a dark picture its white ring shows: the brightest pixel across the circle's edge is near white.
        using var dark = new MagickImage(new MagickColor("#101010"), 800, 400);
        Draw(dark, number);
        var edge = Enumerable.Range((int)circle.Left - 1, 4).Select(x => At(dark, x, (int)circle.MidY)).Max(p => p.B);
        Assert.True(edge > 200, $"brightest edge pixel {edge}");
    }

    [Fact]
    public void Arabic_text_is_shaped_its_letters_joined()
    {
        // Drawn letter by letter, three seens were three isolated ones, each with its tail; joined they are narrower.
        static float TextWidth(string text)
        {
            var mark = new Annotation { Kind = AnnotationKind.Text, Text = text, Points = [new AnnotationPoint { X = 0, Y = 0 }] };
            return AnnotationPainter.TagOf(mark, 800, 400).Width - AnnotationPainter.FontSizeOf(AnnotationWidth.Medium, 800, 400) * 0.9f;
        }

        Assert.True(TextWidth("سسس") < TextWidth("س") * 2.7f, $"{TextWidth("سسس")} against {TextWidth("س")} for one");
    }

    [Theory]
    // Left to right with Arabic in it: the Arabic words read right to left after "Save", the first on the right.
    [InlineData("Save مرحبا بالعالم", false, new[] { "Save", "بالعالم", "مرحبا" })]
    // Right to left with a name in it: the name is on the left, read left to right.
    [InlineData("مرحبا Filee", true, new[] { "Filee", "مرحبا" })]
    // A number with its separator in Arabic text stays one number, read left to right, in the right place.
    [InlineData("المجموع 1,200 ريال", true, new[] { "ريال", "1,200", "المجموع" })]
    [InlineData("Total: 1,200 ريال (approx.)", false, new[] { "Total: 1,200", "ريال", "(approx.)" })]
    public void Mixed_text_is_put_in_reading_order(string text, bool rightToLeft, string[] leftToRight)
    {
        Assert.Equal(rightToLeft, LabelText.IsRightToLeft(text));
        var runs = LabelText.Runs(text);
        // As seen from the left: a right-to-left run (and a right-to-left piece) reversed. Whether two Arabic words are
        // one run or three (with the space between them in another font) depends on the fonts installed.
        static string Seen(string part, bool rightToLeft) =>
            rightToLeft ? string.Concat(part.EnumerateRunes().Reverse().Select(r => r.ToString())) : part;
        var shown = string.Concat(runs.Select(r => Seen(r.Text, r.RightToLeft)));
        // Each piece once, unbroken, in the order it is seen from the left.
        var at = leftToRight.Select(piece => shown.IndexOf(Seen(piece, piece.Any(c => c is >= '\u0600' and <= '\u06FF')), StringComparison.Ordinal)).ToList();
        Assert.All(at, i => Assert.True(i >= 0, $"a piece is missing or broken up in \"{shown}\""));
        Assert.Equal(at.Order(), at);
        // Arabic is shaped right to left, Latin and numbers left to right (a run of spaces may go either way).
        Assert.All(runs.Where(r => r.Text.Any(char.IsLetterOrDigit)),
            r => Assert.Equal(r.Text.Any(c => c is >= '\u0600' and <= '\u06FF'), r.RightToLeft));
    }

    [Fact]
    public void Brackets_in_right_to_left_text_are_shaped_right_to_left_so_they_mirror()
    {
        var runs = LabelText.Runs("(مرحبا)");
        Assert.All(runs, r => Assert.True(r.RightToLeft, $"\"{r.Text}\" left to right"));
    }

    [Fact]
    public void Lines_get_thicker_with_the_width_and_with_the_picture()
    {
        Assert.True(AnnotationPainter.StrokeOf(AnnotationWidth.Thick, 800, 400) > AnnotationPainter.StrokeOf(AnnotationWidth.Medium, 800, 400));
        Assert.True(AnnotationPainter.StrokeOf(AnnotationWidth.Medium, 800, 400) > AnnotationPainter.StrokeOf(AnnotationWidth.Thin, 800, 400));
        Assert.Equal(2 * AnnotationPainter.StrokeOf(AnnotationWidth.Thick, 1600, 800), 4 * AnnotationPainter.StrokeOf(AnnotationWidth.Thick, 800, 400), 2);
    }

    [Fact]
    public async Task The_tool_runs_as_a_conversion_in_the_pictures_own_format()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "screen.jpg");
        using (var source = White())
            source.Write(path, MagickFormat.Jpeg);
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.AnnotateId);
        var conversion = AnnotateTool.ConversionOf(tool, BuiltInData.SameAsSource,
            [Mark(AnnotationKind.Box, "#1C1C1C", (0.25, 0.25), (0.75, 0.75)), new Annotation { Kind = AnnotationKind.Text, Text = "" }]);

        Assert.Single(conversion.Image.Annotations!); // the empty text is no mark
        var job = await fx.ConvertAsync([path], conversion);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = Assert.Single(job.Outputs);
        Assert.Equal(Path.Combine(folder, "screen Annotated.jpg"), output);
        using var result = new MagickImage(output);
        Assert.True(At(result, 400, 100).R < 60);
        Assert.True(At(result, 400, 200).R > 240);
    }

    [Theory]
    [InlineData("png", ColorSpace.Gray)]
    [InlineData("jpg", ColorSpace.Gray)]
    [InlineData("jpg", ColorSpace.CMYK)]
    [InlineData("tif", ColorSpace.CMYK)]
    public async Task Marks_keep_their_colours_on_a_greyscale_or_cmyk_picture(string extension, ColorSpace space)
    {
        // Laid on as they are (sRGB), red came out as grey 229 on a greyscale picture and as teal on a CMYK one.
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "scan." + extension);
        using (var source = White(1600, 800))
        {
            source.ColorSpace = space;
            if (space == ColorSpace.Gray)
                source.ColorType = ColorType.Grayscale;
            source.Quality = 95;
            source.Write(path);
        }
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.AnnotateId);
        Annotation Thick(string color, double y)
        {
            var box = Mark(AnnotationKind.Box, color, (0.2, y), (0.8, y + 0.2));
            box.Width = AnnotationWidth.Thick;
            return box;
        }
        var conversion = AnnotateTool.ConversionOf(tool, BuiltInData.SameAsSource,
            [Thick("#E5484D", 0.1), Thick("#F9D84A", 0.4), Thick("#3E63DD", 0.7)]);

        var job = await fx.ConvertAsync([path], conversion);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var result = new MagickImage(Assert.Single(job.Outputs));
        Assert.Equal(space == ColorSpace.CMYK ? ColorSpace.CMYK : ColorSpace.sRGB, result.ColorSpace);
        result.ColorSpace = ColorSpace.sRGB;
        // The middle of each box's top edge (Thick is about 9 px at 800), as the window draws it; JPEG shifts it a bit.
        foreach (var (color, y) in new[] { ((0xE5, 0x48, 0x4D), 80), ((0xF9, 0xD8, 0x4A), 320), ((0x3E, 0x63, 0xDD), 560) })
        {
            var (r, g, b) = At(result, 800, y);
            Assert.True(Math.Abs(r - color.Item1) <= 12 && Math.Abs(g - color.Item2) <= 12 && Math.Abs(b - color.Item3) <= 12,
                $"at y {y}: {r},{g},{b} instead of {color}");
        }
    }

    [Fact]
    public void A_large_picture_is_painted_in_bands_that_meet_without_a_seam_and_untouched_outside_the_marks()
    {
        // 6000 px across, the box about 4900: a band is about 860 rows, so the box's sides cross band edges.
        using var image = White(6000, 3000);
        Draw(image, Mark(AnnotationKind.Box, "#3E63DD", (0.1, 0.05), (0.9, 0.95)));

        var side = Enumerable.Range(150, 2700).Select(y => At(image, 600, y)).Distinct().ToList();
        Assert.Equal([(0x3E, 0x63, 0xDD)], side);
        Assert.Equal((255, 255, 255), At(image, 3000, 1500));
        Assert.Equal((255, 255, 255), At(image, 100, 100));
    }

    [Fact]
    public void The_exif_thumbnail_of_the_unmarked_picture_is_dropped()
    {
        // Viewers that show a file's small previews would show the picture without its marks.
        using var image = White();
        image.SetProfile(new ImageProfile("8bim", [0x38, 0x42, 0x49, 0x4D, 0x04, 0x0C, 0, 0, 0, 0, 0, 0]));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Phone");
        image.SetProfile(exif);

        Draw(image, Mark(AnnotationKind.Box, "#3E63DD", (0.25, 0.25), (0.75, 0.75)));

        Assert.False(image.HasProfile("8bim"));
        Assert.Equal("Phone", image.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }

    public static TheoryData<string, string> Profiles => new() { { "jpg", "AdobeRGB1998" }, { "png", "AdobeRGB1998" }, { "jpg", "USWebCoatedSWOP" } };

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task Marks_look_as_drawn_where_a_viewer_applies_the_pictures_colour_profile(string extension, string name)
    {
        // A wide-gamut photo (Display P3, Adobe RGB) or a print CMYK keeps its profile. Written as sRGB numbers, red
        // was too vivid there, and 220, 105, 79 through a CMYK profile.
        var profile = (IColorProfile)typeof(ColorProfiles).GetProperty(name)!.GetValue(null)!;
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "photo." + extension);
        using (var source = new MagickImage(new MagickColor("#DDE6F5"), 1600, 800))
        {
            source.SetProfile(ColorProfiles.SRGB);
            source.TransformColorSpace(ColorProfiles.SRGB, profile);
            source.Quality = 95;
            source.Write(path);
        }
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.AnnotateId);
        var box = Mark(AnnotationKind.Box, "#E5484D", (0.2, 0.1), (0.8, 0.3));
        box.Width = AnnotationWidth.Thick;
        var highlight = Mark(AnnotationKind.Highlight, "#3E63DD", (0.2, 0.5), (0.8, 0.7));

        var job = await fx.ConvertAsync([path], AnnotateTool.ConversionOf(tool, BuiltInData.SameAsSource, [box, highlight]));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var result = new MagickImage(Assert.Single(job.Outputs));
        var embedded = result.GetColorProfile();
        Assert.Equal(profile.Description, embedded?.Description);
        // As a colour-managed viewer shows it.
        result.RenderingIntent = RenderingIntent.Relative;
        result.TransformColorSpace(embedded!, ColorProfiles.SRGB);
        var (r, g, b) = At(result, 800, 80);
        Assert.True(Math.Abs(r - 0xE5) <= 10 && Math.Abs(g - 0x48) <= 10 && Math.Abs(b - 0x4D) <= 10, $"box {r},{g},{b} instead of 229,72,77");
        // The highlight still lets the picture show through: 40 % of its blue over the picture, as the window blends
        // them, each as the profile can show it (a print CMYK can't show the pale blue or the blue as they are).
        var (hr, hg, hb) = At(result, 800, 480);
        var under = At(result, 800, 760);
        using var blue = new MagickImage(new MagickColor("#3E63DD"), 1, 1);
        blue.SetProfile(ColorProfiles.SRGB);
        blue.RenderingIntent = RenderingIntent.Relative;
        blue.TransformColorSpace(ColorProfiles.SRGB, profile);
        blue.TransformColorSpace(profile, ColorProfiles.SRGB);
        var shown = At(blue, 0, 0);
        var want = (R: under.R * 0.6 + shown.R * 0.4, G: under.G * 0.6 + shown.G * 0.4, B: under.B * 0.6 + shown.B * 0.4);
        Assert.True(Math.Abs(hr - want.R) <= 10 && Math.Abs(hg - want.G) <= 10 && Math.Abs(hb - want.B) <= 10,
            $"highlight {hr},{hg},{hb} instead of {want.R:0},{want.G:0},{want.B:0}");
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    public async Task A_greyscale_picture_with_its_own_profile_becomes_colour_through_it(string extension)
    {
        // Converted through its grey profile to sRGB, so its greys stay as they were seen and the marks keep their
        // colours. A linear profile (gamma 1) tells it apart from greys copied over as they are: its middle grey is
        // seen as sRGB 187, not 128.
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "scan." + extension);
        using (var source = new MagickImage(new MagickColor("#808080"), 1600, 800))
        {
            source.ColorSpace = ColorSpace.Gray;
            source.ColorType = ColorType.Grayscale;
            source.SetProfile(new ColorProfile(GreyProfile(1.0)));
            source.Quality = 95;
            source.Write(path);
        }
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.AnnotateId);
        var box = Mark(AnnotationKind.Box, "#E5484D", (0.2, 0.1), (0.8, 0.3));
        box.Width = AnnotationWidth.Thick;

        var job = await fx.ConvertAsync([path], AnnotateTool.ConversionOf(tool, BuiltInData.SameAsSource, [box]));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var result = new MagickImage(Assert.Single(job.Outputs));
        Assert.Equal(ColorSpace.sRGB, result.ColorSpace);
        if (result.GetColorProfile() is { } embedded)
            result.TransformColorSpace(embedded, ColorProfiles.SRGB);
        var (r, g, b) = At(result, 800, 80);
        Assert.True(Math.Abs(r - 0xE5) <= 10 && Math.Abs(g - 0x48) <= 10 && Math.Abs(b - 0x4D) <= 10, $"box {r},{g},{b} instead of 229,72,77");
        var grey = At(result, 800, 600);
        Assert.True(Math.Abs(grey.R - 187) <= 6 && grey.R == grey.G && grey.G == grey.B, $"grey {grey} instead of 187");
    }

    /// <summary>
    /// A minimal ICC v2 grey display profile with a gamma curve: what scanners and phones embed in greyscale.
    /// </summary>
    private static byte[] GreyProfile(double gamma)
    {
        static byte[] Be(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        static byte[] Sig(string s) => System.Text.Encoding.ASCII.GetBytes(s);
        static byte[] Xyz(double x, double y, double z) =>
            [.. Sig("XYZ "), 0, 0, 0, 0, .. Be((uint)Math.Round(x * 65536)), .. Be((uint)Math.Round(y * 65536)), .. Be((uint)Math.Round(z * 65536))];
        static byte[] Pad(byte[] data) => [.. data, .. new byte[(4 - data.Length % 4) % 4]];
        var name = Sig("Grey gamma\0");
        byte[] desc = Pad([.. Sig("desc"), 0, 0, 0, 0, .. Be((uint)name.Length), .. name, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. new byte[67]]);
        var gamma88 = (ushort)Math.Round(gamma * 256);
        byte[] curve = Pad([.. Sig("curv"), 0, 0, 0, 0, .. Be(1), (byte)(gamma88 >> 8), (byte)gamma88]);
        byte[] copyright = Pad([.. Sig("text"), 0, 0, 0, 0, .. Sig("none\0")]);
        var tags = new (string Sig, byte[] Data)[] { ("desc", desc), ("wtpt", Xyz(0.9642, 1, 0.8249)), ("kTRC", curve), ("cprt", copyright) };
        var offset = 128 + 4 + tags.Length * 12;
        var table = new List<byte>(Be((uint)tags.Length));
        var data = new List<byte>();
        foreach (var (sig, bytes) in tags)
        {
            table.AddRange([.. Sig(sig), .. Be((uint)(offset + data.Count)), .. Be((uint)bytes.Length)]);
            data.AddRange(bytes);
        }
        var size = (uint)(offset + data.Count);
        byte[] header = [.. Be(size), 0, 0, 0, 0, 0x02, 0x10, 0, 0, .. Sig("mntr"), .. Sig("GRAY"), .. Sig("XYZ "), .. new byte[12],
            .. Sig("acsp"), .. new byte[24], 0, 0, 0, 0, .. Xyz(0.9642, 1, 0.8249)[8..], .. new byte[48]];
        return [.. header, .. table, .. data];
    }

    [Fact]
    public void An_emoji_in_a_label_is_drawn()
    {
        // Colour emoji have no outline: drawn as outlines, it left a blank gap in the tag.
        static int Ink(string text, out bool coloured)
        {
            var mark = new Annotation { Kind = AnnotationKind.Text, Text = text, Color = "#3E63DD", Points = [new AnnotationPoint { X = 0.1, Y = 0.1 }] };
            using var image = White();
            Draw(image, mark);
            var tag = AnnotationPainter.TagOf(mark, 800, 400);
            var pixels = Enumerable.Range((int)tag.Left + 2, (int)tag.Width - 4)
                .SelectMany(x => Enumerable.Range((int)tag.Top + 2, (int)tag.Height - 4).Select(y => At(image, x, y))).ToList();
            coloured = pixels.Any(p => p.R > 200 && p.G > 140 && p.B < 90);   // a thumbs-up's yellow
            return pixels.Count(p => Math.Abs(p.R - 0x3E) + Math.Abs(p.G - 0x63) + Math.Abs(p.B - 0xDD) > 60);
        }

        var fonts = SkiaSharp.SKFontManager.Default;
        Assert.SkipUnless(fonts.FontFamilies.Any(f => fonts.MatchFamily(f)?.ContainsGlyph(0x1F44D) == true),
            "No installed font has the emoji: neither the text box nor the label can draw it.");
        var plain = Ink("OK", out _);
        var withEmoji = Ink("OK 👍", out var coloured);
        Assert.True(withEmoji > plain * 1.5, $"{withEmoji} ink pixels with the emoji against {plain} without");
        // In colour where its font is a colour one.
        if (fonts.FontFamilies.Any(f => f.Contains("Color Emoji", StringComparison.OrdinalIgnoreCase)))
            Assert.True(coloured, "the emoji in colour");
    }

    [Fact]
    public void Accents_stacked_above_a_label_are_not_cut_off_in_the_file()
    {
        // The file is painted only over the part the marks cover: that part ended 2 px above the tag, and accents
        // stacked higher were cut off there, though the window showed them. The file is the marks painted whole (how
        // high the accents reach depends on the font).
        var text = Mark(AnnotationKind.Text, "#F9D84A", (0.3, 0.5));     // yellow: dark ink, which shows on white
        text.Text = "A\u0308\u0308\u0308\u0308\u0308";
        using var image = White();
        Draw(image, text);
        using var whole = new SkiaSharp.SKBitmap(800, 400);
        using (var canvas = new SkiaSharp.SKCanvas(whole))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            AnnotationPainter.Paint(canvas, text, 800, 400);
        }

        var apart = 0;
        for (var y = 0; y < 400; y++)
        {
            for (var x = 0; x < 800; x++)
            {
                var (r, g, b) = At(image, x, y);
                var p = whole.GetPixel(x, y);
                if (Math.Max(Math.Abs(r - p.Red), Math.Max(Math.Abs(g - p.Green), Math.Abs(b - p.Blue))) > 40)
                    apart++;
            }
        }
        Assert.True(apart == 0, $"{apart} pixels of the label differ from it painted whole: cut off");
    }

    [Fact]
    public void A_number_of_three_digits_or_more_stays_inside_its_coloured_disc()
    {
        foreach (var value in new[] { 7, 42, 100, 1234 })
        {
            // Yellow has dark ink, which shows on the white ring around the disc where white ink would not.
            var number = Mark(AnnotationKind.Number, "#F9D84A", (0.5, 0.5));
            number.Number = value;
            number.Width = AnnotationWidth.Thick;
            using var image = White(1600, 800);
            Draw(image, number);
            var circle = AnnotationPainter.CircleOf(number, 1600, 800);
            var radius = circle.Width / 2;
            var ring = Enumerable.Range(0, 360).Select(degrees => degrees * Math.PI / 180)
                .Select(a => At(image, (int)(circle.MidX + Math.Cos(a) * radius * 0.94), (int)(circle.MidY + Math.Sin(a) * radius * 0.94)));
            Assert.True(ring.All(p => p.R > 200), $"{value} spills out of its disc onto the ring");
        }
    }
}
