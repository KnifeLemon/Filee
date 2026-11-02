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
}
