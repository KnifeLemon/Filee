// Pixel-level check that slice labels sit on their slice's centre line.
// Regression: labels were centred on the measured text width while being laid out centred in a wider box,
// so short labels drifted right (reported with a screenshot of the donut editor).

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class DonutLabelPlacementTests
{
    private const double OuterRadius = 150;
    private const double HoleRatio = 0.42;

    [AvaloniaFact]
    public void Labels_are_centred_on_their_slices()
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu
        {
            OuterRadius = OuterRadius,
            HoleRatio = HoleRatio,
            Items =
            [
                new DonutItem { Id = "a", Label = "PNG" },
                new DonutItem { Id = "b", Label = "JPG" },
                new DonutItem { Id = "c", Label = "PDF" },
                new DonutItem { Id = "d", Label = "ICO" },
            ],
            SliceBrush = Brushes.White,
            CenterBrush = Brushes.White,
            Foreground = Brushes.Black,
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight, Background = Brushes.White };
        window.Show();
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        using var frame = window.CaptureRenderedFrame()!;
        var (pixels, width, height) = Read(frame);
        var origin = donut.TranslatePoint(new Point(0, 0), window)!.Value;
        var center = new Point(origin.X + donut.Bounds.Width / 2, origin.Y + donut.Bounds.Height / 2);
        var labelRadius = (OuterRadius + OuterRadius * HoleRatio) / 2;

        for (var i = 0; i < 4; i++)
        {
            var anchor = DonutGeometry.PointAt(center, labelRadius, DonutGeometry.MidAngle(i, 4));
            var (cx, cy) = DarkCentroid(pixels, width, height, anchor, 34);
            Assert.True(Math.Abs(cx - anchor.X) < 3, $"slice {i}: text centre x {cx:0.0} vs slice centre {anchor.X:0.0}");
            Assert.True(Math.Abs(cy - anchor.Y) < 5, $"slice {i}: text centre y {cy:0.0} vs slice centre {anchor.Y:0.0}");
        }
        window.Close();
    }

    /// <summary>Average position of dark (text) pixels in a square around <paramref name="at"/>.</summary>
    private static (double X, double Y) DarkCentroid(byte[] bgra, int width, int height, Point at, int half)
    {
        double sx = 0, sy = 0;
        var n = 0;
        for (var y = Math.Max(0, (int)at.Y - half); y < Math.Min(height, (int)at.Y + half); y++)
        {
            for (var x = Math.Max(0, (int)at.X - half); x < Math.Min(width, (int)at.X + half); x++)
            {
                var o = (y * width + x) * 4;
                var luminance = 0.0722 * bgra[o] + 0.7152 * bgra[o + 1] + 0.2126 * bgra[o + 2];
                if (luminance < 110)
                {
                    sx += x;
                    sy += y;
                    n++;
                }
            }
        }
        Assert.True(n > 20, $"no label found near {at}");
        return (sx / n, sy / n);
    }

    /// <summary>A Korean word stays on one line: a word joiner goes between its syllables.</summary>
    [Fact]
    public void A_korean_word_is_kept_together()
    {
        Assert.Equal("ZIP 하\u2060나\u2060로", DonutMenu.KeepKoreanWords("ZIP 하나로"));
        Assert.Equal("Merge PDF", DonutMenu.KeepKoreanWords("Merge PDF"));
    }

    /// <summary>
    /// The Portuguese names of image actions were shortened to fit a full donut's slices; they still fit whole, without
    /// a word split, on every slice of a full donut of the default size at the smallest Label scale (80 %). At larger
    /// scales the ring falls back to icons (Auto) or shortens the name (Text only).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("pt-PT", "preset.half_size")]
    [InlineData("pt-BR", "preset.half_size")]
    [InlineData("pt-PT", "preset.trace_line_art")]
    [InlineData("pt-BR", "preset.trace_line_art")]
    [InlineData("pt-PT", "preset.trace_svg")]
    [InlineData("pt-BR", "preset.trace_svg")]
    [InlineData("pt-PT", "preset.extract_motion")]
    [InlineData("pt-BR", "preset.extract_motion")]
    public void Portuguese_image_actions_fit_a_full_donuts_slices_whole(string language, string key)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<LocalizationService>();
        var preset = BuiltInData.CreatePresets().Single(p => p.NameKey == key);
        var label = loc.DonutName(preset);
        Assert.Equal(loc[key], loc.DisplayName(preset));
        preset.Name = "My custom action";
        Assert.Equal(preset.Name, loc.DonutName(preset));
        var family = SoftPalette.FontFamilyFor(language);
        var layout = new SliceLabelLayout(new LabelFonts(new Typeface(family, FontStyle.Normal, FontWeight.SemiBold),
            new Typeface(family), FlowDirection.LeftToRight));
        var center = new Point(200, 200);
        var whole = Enumerable.Range(0, 12).Count(i => layout.FitText(
            SafeZone.Of(new SliceShape(center, OuterRadius * HoleRatio, OuterRadius, DonutGeometry.MidAngle(i, 12), DonutGeometry.Span(12), 3), 1),
            label, null, SliceLabelLayout.NameSizeFor(0.8, 1), 10.5, alongToo: true) is { Whole: true });
        Assert.True(whole == 12, $"{language}: {label} fits whole on {whole} of 12 slices");
    }

    private static (byte[] Pixels, int Width, int Height) Read(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var width = buffer.Size.Width;
        var height = buffer.Size.Height;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Marshal.Copy(buffer.Address + row * buffer.RowBytes, pixels, row * width * 4, width * 4);
        return (pixels, width, height);
    }
}
