// Janome JEF embroidery designs (issue #1716): the header's thread table and the stitch records decoded, drawn in the
// threads' chart colours as SVG and rendered to PNG through the vector engine. The test designs are written here byte
// by byte, following the layout described in JefEmbroidery.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Vector;
using ImageMagick;

namespace Filee.Engines.Tests;

public class JefEmbroideryTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A command: 0x80, the code, then its two bytes.</summary>
    private static (int, int)[] Command(int code, int dx = 0, int dy = 0) => [(0x80, code), (dx, dy)];

    /// <summary>
    /// A JEF file: the header (stitch offset, thread count at 24, thread numbers from 116), then (dx, dy) pairs and
    /// commands, then the end command. Byte pairs are written as given; stitches are signed bytes.
    /// </summary>
    private static byte[] Jef(int[] threads, params (int A, int B)[] pairs)
    {
        var start = 116 + threads.Length * 8; // thread numbers, then their thread types
        var header = new byte[start];
        BitConverter.GetBytes(start).CopyTo(header, 0);
        BitConverter.GetBytes(0x14).CopyTo(header, 4);
        "20261121120000"u8.CopyTo(header.AsSpan(8));
        BitConverter.GetBytes(threads.Length).CopyTo(header, 24);
        for (var i = 0; i < threads.Length; i++)
        {
            BitConverter.GetBytes(threads[i]).CopyTo(header, 116 + i * 4);
            BitConverter.GetBytes(0x0D).CopyTo(header, 116 + (threads.Length + i) * 4);
        }
        List<byte> result = [.. header];
        foreach (var (a, b) in pairs)
            result.AddRange([(byte)(sbyte)a, (byte)(sbyte)b]);
        result.AddRange([0x80, 0x10]);
        return [.. result];
    }

    private static (int, int)[] Square(int size) => [(size, 0), (0, size), (-size, 0), (0, -size)];

    /// <summary>Two 10 mm squares: Red (225, number 10) then, after a move and a colour change, Blue (number 12).</summary>
    private static byte[] TwoSquares() => Jef([10, 12],
        [.. Square(100), .. Command(0x02, 127, 0), .. Command(0x02, 23, 0), .. Command(0x01), .. Square(100)]);

    [Fact]
    public void Stitches_moves_and_colour_changes_are_decoded_with_their_thread_numbers()
    {
        var (threads, numbers) = JefEmbroidery.Read(TwoSquares());

        Assert.Equal(2, threads.Count);
        Assert.Equal([(0, 0), (100, 0), (100, 100), (0, 100), (0, 0)], Assert.Single(threads[0]));
        Assert.Equal([(150, 0), (250, 0), (250, 100), (150, 100), (150, 0)], Assert.Single(threads[1]));
        Assert.Equal([10, 12], numbers);
        Assert.Equal("#FF0000", JefEmbroidery.ColorOf(10, 0));
        Assert.Equal("#0B2F84", JefEmbroidery.ColorOf(12, 1));
    }

    [Fact]
    public void A_change_to_thread_0_is_a_stop_that_keeps_the_thread()
    {
        // Yellow, a stop, then Green: two threads, the second one green.
        var (threads, numbers) = JefEmbroidery.Read(Jef([3, 0, 6],
            [(10, 0), .. Command(0x01), (0, 10), .. Command(0x01), (10, 0)]));

        Assert.Equal(2, threads.Count);
        Assert.Equal([[(0, 0), (10, 0)], [(10, 0), (10, 10)]], threads[0]);
        Assert.Equal([3, 6], numbers);
    }

    [Fact]
    public void A_number_outside_the_chart_gets_a_colour_of_its_own()
    {
        Assert.Equal(EmbroiderySvg.Fallback(0), JefEmbroidery.ColorOf(0, 0));
        Assert.Equal(EmbroiderySvg.Fallback(1), JefEmbroidery.ColorOf(200, 1));
    }

    [Fact]
    public void Negative_steps_are_signed_bytes()
    {
        var run = Assert.Single(Assert.Single(JefEmbroidery.Read(Jef([1], [(-127, 127), (-1, -1)])).Threads));

        Assert.Equal([(0, 0), (-127, 127), (-128, 126)], run);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void A_thread_number_far_outside_the_chart_is_not_a_jef(int number)
    {
        var error = Assert.Throws<InvalidDataException>(() => JefEmbroidery.Read(Jef([number], Square(10))));

        Assert.Contains("isn't a Janome JEF", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negated_thread_number_is_read_as_its_number()
    {
        Assert.Equal([12], JefEmbroidery.Read(Jef([-12], Square(10))).Numbers);
    }

    [Fact]
    public async Task A_design_becomes_an_svg_and_a_png_in_its_thread_colours()
    {
        var path = Path.Combine(fx.NewFolder(), "badge.jef");
        await File.WriteAllBytesAsync(path, TwoSquares(), TestContext.Current.CancellationToken);
        Assert.Equal("jef", FormatRegistry.Detect(path)?.Id);

        var svgJob = await fx.ConvertAsync([path], new Preset { TargetFormat = "svg" });
        Assert.True(svgJob.State == JobState.Completed, string.Join("; ", svgJob.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var svg = await File.ReadAllTextAsync(svgJob.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("width=\"29mm\"", svg); // 25 mm of design and 2 mm on each side
        Assert.Contains("stroke=\"#FF0000\"", svg);
        Assert.Contains("stroke=\"#0B2F84\"", svg);

        var pngJob = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });
        Assert.True(pngJob.State == JobState.Completed, string.Join("; ", pngJob.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var png = new MagickImage(pngJob.Outputs.Single());
        Assert.True(png.Width > png.Height); // two squares side by side
        Assert.True(png.Histogram().Count > 2, "the threads are drawn in colour");
    }

    [Fact]
    public async Task A_file_that_isnt_a_jef_design_fails_with_a_reason()
    {
        var path = Path.Combine(fx.NewFolder(), "notes.jef");
        await File.WriteAllTextAsync(path, new string('x', 400), TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("JEF", job.Files[0].ErrorDetail);
    }
}
