// "Add background" through the real image engine: the canvas takes the chosen shape round the picture and its padding,
// the corners are cut round (the background shows there), the shadow falls below the picture, a gradient runs from
// one colour to the other, a picture of one's own covers the canvas, and the tool runs as an ordinary conversion.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;
using ImageMagick.Drawing;

namespace Filee.Engines.Tests;

public class BackdropTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static MagickImage Picture(uint width = 400, uint height = 200) => new(new MagickColor("#FF0000"), width, height);

    private static (byte R, byte G, byte B) At(IMagickImage<byte> image, int x, int y)
    {
        var pixel = image.GetPixels().GetPixel(x, y).ToColor()!;
        return (pixel.R, pixel.G, pixel.B);
    }

    [Theory]
    [InlineData(BackdropCanvas.Auto, 10, 480, 280)]       // 400 × 200 and 40 px round it
    [InlineData(BackdropCanvas.Square, 10, 480, 480)]
    [InlineData(BackdropCanvas.Landscape, 10, 498, 280)]  // 280 high → 16:9 is 497.8 wide
    [InlineData(BackdropCanvas.Portrait, 10, 480, 600)]   // 480 wide → 4:5 is 600 high
    [InlineData(BackdropCanvas.Auto, 0, 400, 200)]
    public void The_canvas_takes_its_shape_round_the_picture_and_its_padding(BackdropCanvas canvas, int padding, int width, int height)
    {
        var layout = BackdropComposer.LayoutOf(400, 200, new BackdropOptions { Canvas = canvas, Padding = padding });

        Assert.Equal((width, height), (layout.Width, layout.Height));
        Assert.Equal(((width - 400) / 2, (height - 200) / 2), (layout.X, layout.Y));
    }

    [Fact]
    public void The_corners_are_cut_round_the_shadow_falls_below_and_the_canvas_is_opaque()
    {
        using var image = Picture();
        var look = new BackdropOptions { Kind = BackdropKind.Color, Color = "#FFFFFF", Padding = 10, Corners = 10, Shadow = 100 };
        var layout = BackdropComposer.LayoutOf(400, 200, look);

        BackdropComposer.Apply(image, look);

        Assert.Equal(((uint)layout.Width, (uint)layout.Height), (image.Width, image.Height));
        Assert.False(image.HasAlpha);
        // The middle is the picture; its very corner is cut away, so the background (or the shadow) shows there.
        Assert.Equal((255, 0, 0), At(image, layout.X + 200, layout.Y + 100));
        Assert.NotEqual((255, 0, 0), At(image, layout.X, layout.Y));
        // Just below the picture is darker than just above it: the shadow falls down.
        var below = At(image, layout.X + 200, layout.Y + 200 + 6);
        var above = At(image, layout.X + 200, layout.Y - 6);
        Assert.True(below.R < above.R, $"below {below}, above {above}");
        Assert.True(below.R < 250);
    }

    [Fact]
    public void Without_a_shadow_or_corners_the_background_is_untouched_round_the_picture()
    {
        using var image = Picture();
        var look = new BackdropOptions { Kind = BackdropKind.Color, Color = "#2040C0", Padding = 10, Corners = 0, Shadow = 0 };
        var layout = BackdropComposer.LayoutOf(400, 200, look);

        BackdropComposer.Apply(image, look);

        Assert.Equal((0x20, 0x40, 0xC0), At(image, layout.X + 200, layout.Y + 200 + 3));
        Assert.Equal((255, 0, 0), At(image, layout.X, layout.Y));
    }

    [Fact]
    public void A_gradient_runs_from_the_top_left_colour_to_the_bottom_right_one()
    {
        using var image = Picture(100, 100);
        var look = new BackdropOptions { From = "#000000", To = "#FFFFFF", Padding = 40, Shadow = 0, Corners = 0 };

        BackdropComposer.Apply(image, look);

        Assert.True(At(image, 1, 1).R < 20);
        Assert.True(At(image, (int)image.Width - 2, (int)image.Height - 2).R > 235);
    }

    [Fact]
    public void A_picture_of_ones_own_covers_the_canvas_and_a_missing_one_is_refused()
    {
        var own = Path.Combine(fx.NewFolder(), "behind.png");
        using (var behind = new MagickImage(new MagickColor("#00FF00"), 50, 300))
            behind.Write(own, MagickFormat.Png);
        using var image = Picture();
        BackdropComposer.Apply(image, new BackdropOptions { Kind = BackdropKind.Picture, Picture = own, Padding = 10, Shadow = 0 });
        Assert.Equal((0, 255, 0), At(image, 2, 2));
        Assert.Equal((0, 255, 0), At(image, (int)image.Width - 3, (int)image.Height - 3));

        using var other = Picture();
        var refused = Assert.Throws<ConversionRefusedException>(() =>
            BackdropComposer.Apply(other, new BackdropOptions { Kind = BackdropKind.Picture, Picture = own + ".gone.png", Padding = 10, Shadow = 0 }));
        Assert.Equal("error.background_picture", refused.ErrorKey);
    }

    [Fact]
    public async Task The_tool_runs_as_a_conversion_into_a_png_named_with_background()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "release notes.jpg");
        using (var source = Picture())
            source.Write(path, MagickFormat.Jpeg);
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.BackgroundId);
        var look = new BackdropOptions { Kind = BackdropKind.Color, Color = "#FFFFFF", Padding = 10, Canvas = BackdropCanvas.Square };

        var job = await fx.ConvertAsync([path], BackgroundTool.ConversionOf(tool, look));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = Assert.Single(job.Outputs);
        Assert.Equal(Path.Combine(folder, "release notes With Background.png"), output);
        using var result = new MagickImage(output);
        Assert.Equal((480u, 480u), (result.Width, result.Height));
    }

    [Fact]
    public void A_canvas_over_the_pixel_limit_is_made_from_a_smaller_copy_of_the_picture()
    {
        var limit = BackdropComposer.MaxPixels;
        BackdropComposer.MaxPixels = 1_000_000;
        try
        {
            // 4000 × 50 on a square with 40 % padding would be 7200 × 7200 = 52 megapixels.
            using var image = Picture(4000, 50);
            BackdropComposer.Apply(image, new BackdropOptions { Canvas = BackdropCanvas.Square, Padding = 40, Shadow = 0 });

            Assert.True((long)image.Width * image.Height <= 1_000_000, $"{image.Width} × {image.Height}");
            Assert.Equal(image.Width, image.Height);
        }
        finally
        {
            BackdropComposer.MaxPixels = limit;
        }
    }

    [Theory]
    [InlineData("/Users/me/Pictures/behind.jpg", true)]
    [InlineData(@"C:\Users\me\behind.png", true)]
    [InlineData(@"\\server\share\behind.png", true)]   // a network share, picked from the picker
    [InlineData(@"\\?\C:\behind.png", false)]           // device paths
    [InlineData(@"\\.\C:\behind.png", false)]
    [InlineData("//server/share/behind.png", true)]
    [InlineData("behind.png", false)]
    [InlineData("/Users/me/drawing.svg", false)]
    [InlineData("/Users/me/script.msl", false)]
    [InlineData("", false)]
    public void Only_a_full_path_to_a_raster_picture_is_put_behind(string path, bool accepted)
    {
        // A Windows drive or share path is only a full path on Windows, and a Unix one ("/Users/…", no drive) only
        // elsewhere.
        if ((path.StartsWith("C:", StringComparison.Ordinal) || path.StartsWith(@"\\server", StringComparison.Ordinal)) && !OperatingSystem.IsWindows())
            accepted = false;
        if (path.StartsWith("/Users", StringComparison.Ordinal) && OperatingSystem.IsWindows())
            accepted = false;
        Assert.Equal(accepted, BackdropComposer.IsPicturePath(path, out _));
    }

    /// <summary>The picture's colour, as seen, and the background's: what each file must show in any viewer.</summary>
    private const string Seen = "#DDE6F5", Behind = "#2D3A8C";

    private static (byte R, byte G, byte B) Rgb(string hex)
    {
        var color = new MagickColor(hex);
        return (color.R, color.G, color.B);
    }

    private static void Near((byte R, byte G, byte B) want, (byte R, byte G, byte B) got, int tolerance, string what) =>
        Assert.True(Math.Abs(want.R - got.R) <= tolerance && Math.Abs(want.G - got.G) <= tolerance && Math.Abs(want.B - got.B) <= tolerance,
            $"{what}: {got} instead of {want}");

    /// <summary>The file as a colour-managed viewer shows it: through its own profile when it has one.</summary>
    private static MagickImage AsSeen(string path)
    {
        var image = new MagickImage(path);
        if (image.GetColorProfile() is { } profile && profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) != true)
        {
            image.RenderingIntent = RenderingIntent.Relative;
            image.TransformColorSpace(profile, ColorProfiles.SRGB);
        }
        return image;
    }

    public static TheoryData<string, string> Spaces => new()
    {
        { "png", "gray" }, { "jpg", "gray" }, { "png", "gray-linear" }, { "jpg", "cmyk" }, { "tif", "cmyk" },
        { "jpg", "USWebCoatedSWOP" }, { "jpg", "AdobeRGB1998" }, { "png", "AdobeRGB1998" }, { "png", "srgb" },
    };

    [Theory]
    [MemberData(nameof(Spaces))]
    public async Task The_picture_keeps_how_it_looks_and_the_background_its_colour_whatever_the_pictures_colours(string extension, string space)
    {
        // The PNG is sRGB: a picture with a wide-gamut or print profile, a CMYK one or a greyscale one is turned into
        // it as it is seen, and the background is the colour picked, in any viewer.
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "photo." + extension);
        var seen = space.StartsWith("gray", StringComparison.Ordinal) ? "#C8C8C8" : Seen;
        using (var source = new MagickImage(new MagickColor(seen), 400, 200))
        {
            switch (space)
            {
                case "gray":
                    source.ColorSpace = ColorSpace.Gray;
                    source.ColorType = ColorType.Grayscale;
                    break;
                case "gray-linear":
                    // A linear grey profile: its numbers are seen brighter (128 is sRGB 187), so copied over as they
                    // are, the picture would darken.
                    source.ColorSpace = ColorSpace.Gray;
                    source.ColorType = ColorType.Grayscale;
                    source.Evaluate(Channels.All, EvaluateOperator.Set, 128 * 257);
                    source.SetProfile(new ColorProfile(GreyProfile(1.0)));
                    seen = "#BBBBBB";
                    break;
                case "cmyk":
                    source.ColorSpace = ColorSpace.CMYK;
                    break;
                case "srgb":
                    break;
                default:
                    var profile = (IColorProfile)typeof(ColorProfiles).GetProperty(space)!.GetValue(null)!;
                    source.SetProfile(ColorProfiles.SRGB);
                    source.RenderingIntent = RenderingIntent.Relative;
                    source.TransformColorSpace(ColorProfiles.SRGB, profile);
                    break;
            }
            source.Quality = 95;
            source.Write(path);
        }
        // What the picture looks like before: the truth the file is held to (a print profile can't show every colour).
        using (var before = AsSeen(path))
        {
            before.ColorSpace = ColorSpace.sRGB;
            var at = At(before, 200, 100);
            seen = $"#{at.R:X2}{at.G:X2}{at.B:X2}";
        }
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.BackgroundId);
        var look = new BackdropOptions { Kind = BackdropKind.Color, Color = Behind, Padding = 10, Corners = 0, Shadow = 0 };

        var job = await fx.ConvertAsync([path], BackgroundTool.ConversionOf(tool, look));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var result = AsSeen(Assert.Single(job.Outputs));
        Assert.Equal(ColorSpace.sRGB, result.ColorSpace);
        Near(Rgb(Behind), At(result, 2, 2), 2, "the background");
        Near(Rgb(seen), At(result, 240, 140), 6, "the picture");
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

    [Theory]
    [InlineData("jpg", "USWebCoatedSWOP")]
    [InlineData("png", "AdobeRGB1998")]
    [InlineData("png", "gray")]
    public void A_picture_behind_shows_as_it_is_seen_whatever_its_colours(string extension, string space)
    {
        var own = Path.Combine(fx.NewFolder(), "behind." + extension);
        using (var behind = new MagickImage(new MagickColor(space == "gray" ? "#7A7A7A" : "#3E8E5E"), 300, 300))
        {
            if (space == "gray")
            {
                behind.ColorSpace = ColorSpace.Gray;
                behind.ColorType = ColorType.Grayscale;
            }
            else
            {
                var profile = (IColorProfile)typeof(ColorProfiles).GetProperty(space)!.GetValue(null)!;
                behind.SetProfile(ColorProfiles.SRGB);
                behind.RenderingIntent = RenderingIntent.Relative;
                behind.TransformColorSpace(ColorProfiles.SRGB, profile);
            }
            behind.Quality = 95;
            behind.Write(own);
        }
        (byte R, byte G, byte B) want;
        using (var before = AsSeen(own))
        {
            before.ColorSpace = ColorSpace.sRGB;
            want = At(before, 150, 150);
        }
        using var image = Picture();

        BackdropComposer.Apply(image, new BackdropOptions { Kind = BackdropKind.Picture, Picture = own, Padding = 10, Shadow = 0 });

        Near(want, At(image, 2, 2), 4, "the picture behind");
    }

    [Fact]
    public void A_picture_behind_with_see_through_parts_is_set_on_white()
    {
        // Where it is see-through, a viewer shows its own page (white, a checkerboard, dark): the window and the file
        // both show white there.
        var own = Path.Combine(fx.NewFolder(), "behind.png");
        using (var behind = new MagickImage(MagickColors.Transparent, 300, 300))
        {
            new ImageMagick.Drawing.Drawables().FillColor(new MagickColor("#3E8E5E")).Rectangle(150, 0, 299, 299).Draw(behind);
            behind.Write(own, MagickFormat.Png);
        }
        using var image = Picture();

        BackdropComposer.Apply(image, new BackdropOptions { Kind = BackdropKind.Picture, Picture = own, Padding = 10, Shadow = 0 });

        Assert.Equal((255, 255, 255), At(image, 2, 2));
        Near((0x3E, 0x8E, 0x5E), At(image, (int)image.Width - 3, 2), 1, "the opaque half");
    }

    [Fact]
    public void The_small_previews_of_the_picture_on_its_own_are_dropped()
    {
        // Viewers that show a file's small previews would show the picture without its background.
        using var image = Picture();
        image.SetProfile(new ImageProfile("8bim", [0x38, 0x42, 0x49, 0x4D, 0x04, 0x0C, 0, 0, 0, 0, 0, 0]));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Phone");
        image.SetProfile(exif);

        BackdropComposer.Apply(image, new BackdropOptions());

        Assert.False(image.HasProfile("8bim"));
        Assert.Equal("Phone", image.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }

    [Theory]
    [InlineData(400u, 200u, BackdropCanvas.Auto, 12)]
    [InlineData(4000u, 50u, BackdropCanvas.Square, 40)]    // over the limit: made from a smaller copy
    [InlineData(50u, 4000u, BackdropCanvas.Landscape, 40)]
    [InlineData(1u, 1u, BackdropCanvas.Portrait, 0)]
    public void The_size_the_window_tells_is_the_files(uint width, uint height, BackdropCanvas canvas, int padding)
    {
        var limit = BackdropComposer.MaxPixels;
        BackdropComposer.MaxPixels = 1_000_000;
        try
        {
            var look = new BackdropOptions { Canvas = canvas, Padding = padding, Corners = 20, Shadow = 100 };
            using var image = Picture(width, height);

            var told = BackdropComposer.SizeOf((int)width, (int)height, look);
            BackdropComposer.Apply(image, look);

            Assert.Equal(((int)image.Width, (int)image.Height), (told.Width, told.Height));
        }
        finally
        {
            BackdropComposer.MaxPixels = limit;
        }
    }

    [Fact]
    public void The_canvas_never_crops_the_picture_and_has_its_shape_for_any_size()
    {
        var random = new Random(876);
        for (var i = 0; i < 2000; i++)
        {
            int width = random.Next(1, 9000), height = random.Next(1, 9000);
            var look = new BackdropOptions
            {
                Canvas = (BackdropCanvas)random.Next(4),
                Padding = random.Next(BackdropOptions.MaxPadding + 1),
                Corners = random.Next(BackdropOptions.MaxCorners + 1),
            };
            var layout = BackdropComposer.LayoutOf(width, height, look);
            var what = $"{width} × {height}, {look.Canvas}, {look.Padding} %";
            Assert.True(layout.X >= 0 && layout.Y >= 0 && layout.X + width <= layout.Width && layout.Y + height <= layout.Height, what);
            // Centred, to the pixel.
            Assert.True(Math.Abs(layout.Width - width - 2 * layout.X) <= 1 && Math.Abs(layout.Height - height - 2 * layout.Y) <= 1, what);
            // The shape, to a pixel of rounding.
            var (ratioWidth, ratioHeight) = look.Canvas switch
            {
                BackdropCanvas.Square => (1, 1),
                BackdropCanvas.Landscape => (16, 9),
                BackdropCanvas.Portrait => (4, 5),
                _ => (0, 0),
            };
            if (ratioWidth > 0)
                Assert.True(Math.Abs((double)layout.Width * ratioHeight / ratioWidth - layout.Height) <= Math.Max(1, (double)ratioHeight / ratioWidth), what);
            Assert.True(layout.Radius * 2 <= Math.Min(width, height), what);
        }
    }

    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(1u, 3000u)]
    [InlineData(3000u, 2u)]
    public void A_picture_a_pixel_wide_or_high_gets_its_background(uint width, uint height)
    {
        using var image = Picture(width, height);
        var look = new BackdropOptions { Canvas = BackdropCanvas.Square, Padding = 40, Corners = 20, Shadow = 100 };

        BackdropComposer.Apply(image, look);

        Assert.Equal(image.Width, image.Height);
        Assert.False(image.HasAlpha);
    }

    [Theory]
    [InlineData(3000u, 40u, 45)]
    [InlineData(1600u, 100u, 100)]
    [InlineData(40u, 2400u, 100)]
    [InlineData(400u, 200u, 45)]
    [InlineData(2400u, 1600u, 100)]
    public void The_shadow_is_the_one_the_window_draws_for_a_picture_of_any_shape(uint width, uint height, int strength)
    {
        // The window's shadow is Avalonia's drop shadow: Skia's, at the picture's own size. Drawn small by ImageMagick
        // and scaled up, a strip's shadow was a few pixels high and came out as a halo round it.
        var look = new BackdropOptions { Kind = BackdropKind.Color, Color = "#FFFFFF", Padding = 20, Corners = 10, Shadow = strength };
        using var image = Picture(width, height);
        BackdropComposer.Apply(image, look);

        // The reference: the picture (its corners cut round) over its drop shadow, by Skia, on white.
        var layout = BackdropComposer.LayoutOf((int)width, (int)height, look);
        var shape = BackdropComposer.ShadowOf((int)width, (int)height, strength);
        using var reference = new SkiaSharp.SKBitmap(layout.Width, layout.Height);
        using (var canvas = new SkiaSharp.SKCanvas(reference))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            var shadowColor = new SkiaSharp.SKColor(0, 0, 0, (byte)Math.Round(shape.Opacity * 255));
            using var shadow = new SkiaSharp.SKPaint
            {
                IsAntialias = true,
                ImageFilter = SkiaSharp.SKImageFilter.CreateDropShadowOnly(0, (float)shape.Drop, (float)shape.Sigma, (float)shape.Sigma, shadowColor),
            };
            using var red = new SkiaSharp.SKPaint { IsAntialias = true, Color = SkiaSharp.SKColors.Red };
            var rect = new SkiaSharp.SKRoundRect(new SkiaSharp.SKRect(layout.X, layout.Y, layout.X + width, layout.Y + height), layout.Radius);
            canvas.DrawRoundRect(rect, shadow);
            canvas.DrawRoundRect(rect, red);
        }

        // Down the middle and across it, through the shadow on every side of the picture.
        var worst = (Difference: 0, At: "");
        foreach (var (x0, y0, dx, dy, n) in new[] { ((int)image.Width / 2, 0, 0, 1, (int)image.Height), (0, (int)image.Height / 2, 1, 0, (int)image.Width) })
        {
            for (var i = 0; i < n; i++)
            {
                int x = x0 + dx * i, y = y0 + dy * i;
                var (a, b) = (At(image, x, y), reference.GetPixel(x, y));
                var difference = Math.Max(Math.Abs(a.R - b.Red), Math.Max(Math.Abs(a.G - b.Green), Math.Abs(a.B - b.Blue)));
                if (difference > worst.Difference)
                    worst = (difference, $"({x}, {y}): {a} instead of ({b.Red}, {b.Green}, {b.Blue})");
            }
        }
        Assert.True(worst.Difference <= 3, $"{worst.Difference} of 255 from the window's shadow at {worst.At}");
    }

    [Fact]
    public void A_picture_behind_named_as_another_kind_is_read_as_what_it_is()
    {
        // A JPEG saved as ".png" (as many downloads are) was read as a PNG, failed, and was left out.
        var own = Path.Combine(fx.NewFolder(), "download.png");
        using (var behind = new MagickImage(new MagickColor("#3E8E5E"), 300, 300))
            behind.Write(own, MagickFormat.Jpeg);
        using var image = Picture();

        BackdropComposer.Apply(image, new BackdropOptions { Kind = BackdropKind.Picture, Picture = own, Padding = 10, Shadow = 0 });

        Near((0x3E, 0x8E, 0x5E), At(image, 2, 2), 4, "the picture behind");
        // Only the kinds it is allowed to be: a file that is none of them is still refused.
        var script = Path.Combine(fx.NewFolder(), "script.png");
        File.WriteAllText(script, "<?xml version=\"1.0\"?><image><read filename=\"x.png\"/></image>");
        Assert.Null(BackdropComposer.ReadPictureBehind(script));
    }

    [Fact]
    public async Task A_picture_behind_gone_by_the_time_the_file_is_written_fails_it_rather_than_writing_another_look()
    {
        // The file came out on the gradient, reported as done.
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "shot.png");
        using (var source = Picture())
            source.Write(path, MagickFormat.Png);
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.BackgroundId);
        var look = new BackdropOptions { Kind = BackdropKind.Picture, Picture = Path.Combine(folder, "moved away.jpg") };

        var job = await fx.ConvertAsync([path], BackgroundTool.ConversionOf(tool, look));

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.background_picture", file.ErrorKey);
        Assert.Empty(job.Outputs);
    }

    [Fact]
    public void The_picker_offers_each_kind_in_capitals_too()
    {
        // Linux's file picker matches patterns as written: "*.jpg" hid a camera's IMG_0001.JPG.
        Assert.Contains("*.jpg", BackdropComposer.PictureBehindPatterns);
        Assert.Contains("*.JPG", BackdropComposer.PictureBehindPatterns);
        Assert.Contains("*.HEIC", BackdropComposer.PictureBehindPatterns);
    }

    [Fact]
    public void A_wide_pictures_corners_are_as_smooth_as_a_small_ones()
    {
        // Skia's clip gave up smoothing once the canvas was some 8,000 px wide: a phone photo's corners were steps.
        static int Smoothed(uint width, uint height)
        {
            using var image = new MagickImage(MagickColors.Red, width, height);
            var look = new BackdropOptions { Kind = BackdropKind.Color, Color = "#FFFFFF", Padding = 10, Corners = 20, Shadow = 0 };
            var layout = BackdropComposer.LayoutOf((int)width, (int)height, look);
            BackdropComposer.Apply(image, look);
            var smoothed = 0;
            for (var y = layout.Y; y < layout.Y + layout.Radius; y++)
            {
                for (var x = layout.X; x < layout.X + layout.Radius; x++)
                {
                    var g = At(image, x, y).G;
                    if (g is > 8 and < 247)
                        smoothed++;
                }
            }
            return smoothed;
        }

        var small = Smoothed(900, 70);    // radius 14
        var wide = Smoothed(9000, 700);   // radius 140, ten times the arc
        Assert.True(small >= 10, $"{small} smoothed pixels on the small picture's corner");
        Assert.True(wide >= small * 7, $"{wide} smoothed pixels on the wide picture's corner, {small} on the small one's");
    }

    [Fact]
    public void The_file_is_the_same_however_it_is_split_into_bands_and_tiles()
    {
        // Each band and tile is drawn on its own: the corners' smoothing and the shadow must not see where they meet.
        var look = new BackdropOptions { Kind = BackdropKind.Color, Color = "#FFFFFF", Padding = 15, Corners = 20, Shadow = 100 };
        using var whole = Picture(600, 300);
        BackdropComposer.Apply(whole, look);
        var (side, rows) = (BackdropComposer.TileSide, BackdropComposer.BandRows);
        (BackdropComposer.TileSide, BackdropComposer.BandRows) = (37, 23);
        try
        {
            using var split = Picture(600, 300);
            BackdropComposer.Apply(split, look);
            var a = whole.GetPixels().ToByteArray(PixelMapping.RGB)!;
            var b = split.GetPixels().ToByteArray(PixelMapping.RGB)!;
            Assert.True(a.Zip(b, (x, y) => Math.Abs(x - y)).Max() <= 1, "the split file differs");
        }
        finally
        {
            (BackdropComposer.TileSide, BackdropComposer.BandRows) = (side, rows);
        }
    }

    [Fact]
    public void A_strip_wider_than_skia_draws_at_once_has_its_shadow_all_along()
    {
        // Past some 16,000 px along a 33,000 px strip, the shadow stopped.
        using var image = Picture(33000, 300);
        var look = new BackdropOptions { Kind = BackdropKind.Color, Color = "#FFFFFF", Padding = 1, Corners = 0, Shadow = 100 };
        var layout = BackdropComposer.LayoutOf(33000, 300, look);
        var shape = BackdropComposer.ShadowOf(33000, 300, 100);
        BackdropComposer.Apply(image, look);

        var below = layout.Y + 300 + (int)Math.Round(shape.Drop);
        var shades = new[] { 1000, 16000, 20000, 32000 }.Select(x => At(image, layout.X + x, below).G).ToList();
        Assert.All(shades, g => Assert.True(g < 200, $"{string.Join(", ", shades)} below the strip"));
        Assert.True(shades.Max() - shades.Min() <= 2, $"{string.Join(", ", shades)} below the strip");
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(200.0)]
    [InlineData(400.0)]
    [InlineData(800.0)]
    public void A_shadow_of_any_width_is_a_gaussian_blur(double sigma)
    {
        // Skia's blur isn't Gaussian past a sigma of about 100, and far from it past 500: a big picture's strong shadow
        // came out wider, then tighter, than the window's.
        var shape = new BackdropComposer.ShadowShape(sigma, 0, 1);
        int side = (int)(8 * sigma), edge = side / 2;
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(side, 1, SkiaSharp.SKColorType.Alpha8, SkiaSharp.SKAlphaType.Premul));
        // A picture filling the left half of a canvas twice as tall as the blur reaches: its shadow along the middle.
        var mask = new byte[edge * side];
        Array.Fill(mask, (byte)255);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Translate(0, -side / 2);
            BackdropComposer.PaintShadow(canvas, mask, edge, side, 0, 0, shape, new SkiaSharp.SKRectI(0, side / 2, side, side / 2 + 1));
        }
        foreach (var at in new[] { -1.0, 0.0, 1.0, 2.0 })
        {
            var x = edge + (int)Math.Round(at * sigma);
            var want = 255 * 0.5 * (1 - Erf((x + 0.5 - edge) / (sigma * Math.Sqrt(2))));
            var got = bitmap.Bytes[x];
            Assert.True(Math.Abs(got - want) <= 4, $"at {at:+0;-0;0} sigma: {got} instead of {want:0}");
        }
    }

    /// <summary>The error function, to 1.5e-7 (Abramowitz and Stegun 7.1.26).</summary>
    private static double Erf(double x)
    {
        var t = 1 / (1 + 0.3275911 * Math.Abs(x));
        var y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return x < 0 ? -y : y;
    }

    [Fact]
    public void A_picture_behind_is_read_as_large_as_covering_its_box_takes()
    {
        // Shrunk to fit inside the box, a panorama behind a square was read 2000 × 250 and blown up eight times.
        var folder = fx.NewFolder();
        string Write(string name, uint width, uint height)
        {
            var path = Path.Combine(folder, name);
            using var behind = new MagickImage(new MagickColor("#3E8E5E"), width, height);
            behind.Write(path, MagickFormat.Png);
            return path;
        }

        // Too small to cover the box as it is: kept whole, never shrunk (nor enlarged: the canvas scales it).
        using (var panorama = BackdropComposer.ReadPictureBehind(Write("panorama.png", 8000, 1000), 2000, 2000)!)
            Assert.Equal((8000u, 1000u), (panorama.Width, panorama.Height));
        // Larger than covering takes: shrunk to just cover it.
        using (var large = BackdropComposer.ReadPictureBehind(Write("large.png", 8000, 4000), 2000, 2000)!)
            Assert.Equal((4000u, 2000u), (large.Width, large.Height));
    }

    [Fact]
    public void A_picture_behind_over_the_pixel_limit_is_refused_before_it_is_decoded()
    {
        var own = Path.Combine(fx.NewFolder(), "huge.png");
        using (var behind = new MagickImage(new MagickColor("#3E8E5E"), 2000, 1000))
            behind.Write(own, MagickFormat.Png);
        var limit = BackdropComposer.MaxPixels;
        BackdropComposer.MaxPixels = 1_000_000;
        try
        {
            Assert.Null(BackdropComposer.ReadPictureBehind(own, 500, 500));
        }
        finally
        {
            BackdropComposer.MaxPixels = limit;
        }
        using var fine = BackdropComposer.ReadPictureBehind(own, 500, 500);
        Assert.NotNull(fine);
    }

    [Theory]
    [InlineData(4000u, 1000u, 2000, 2000, 4000u, 1000u)]    // can't cover without enlarging: kept as it is, never enlarged
    [InlineData(12000u, 8000u, 300, 300, 450u, 300u)]       // decoded much smaller, then shrunk to just cover
    [InlineData(8000u, 6000u, 1000, 3000, 4000u, 3000u)]    // a tall box: shrunk to just cover it
    public void A_jpeg_behind_is_decoded_no_larger_than_it_is_and_covers_its_box(uint width, uint height, int boxWidth, int boxHeight, uint wantWidth, uint wantHeight)
    {
        // Its size hint, the box's longer side on both, made the decoder enlarge a picture whose shorter side was less:
        // 4000 × 1000 came out 8000 × 2000, past the limit its header was checked against.
        var own = Path.Combine(fx.NewFolder(), "photo.jpg");
        using (var behind = new MagickImage(new MagickColor("#3E8E5E"), width, height))
            behind.Write(own, MagickFormat.Jpeg);

        using var read = BackdropComposer.ReadPictureBehind(own, boxWidth, boxHeight)!;

        Assert.Equal((wantWidth, wantHeight), (read.Width, read.Height));
    }

    [Fact]
    public void A_panorama_behind_keeps_only_the_part_that_covers_the_canvas()
    {
        // Kept whole, a 20000 × 2000 panorama behind a square canvas held ten times the pixels the file shows.
        var own = Path.Combine(fx.NewFolder(), "panorama.png");
        using (var behind = new MagickImage(new MagickColor("#3E8E5E"), 20000, 2000))
            behind.Write(own, MagickFormat.Png);

        using var cover = BackdropComposer.CoverOf(own, new BackdropComposer.Layout(1500, 1500, 0, 0, 0))!;

        // Its middle 1500 × 1500 and two pixels round it, for the smoothing to read.
        Assert.Equal((1504, 1500), (cover.Image.Image.Width, cover.Image.Image.Height));
    }

    [Theory]
    [InlineData(101u, 50u, BackdropCanvas.Square)]       // enlarged five times: half a pixel was two and a half
    [InlineData(3001u, 2u, BackdropCanvas.Portrait)]
    [InlineData(33u, 17u, BackdropCanvas.Square)]
    [InlineData(100u, 50u, BackdropCanvas.Landscape)]
    [InlineData(5000u, 4999u, BackdropCanvas.Square)]    // shrunk first
    public void The_part_of_a_picture_behind_kept_is_drawn_where_the_whole_one_would_be(uint width, uint height, BackdropCanvas canvas)
    {
        // Cut to whole pixels, the part kept moved by up to half a pixel times how much it was enlarged. Now within a
        // level; a picture enlarged hundreds of times can show a one-pixel seam from Skia's smoothing, kept whole or
        // not.
        var own = Path.Combine(fx.NewFolder(), "behind.png");
        // A ramp across (red) and down (green), so a shift either way shows.
        var ramp = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * (int)width + x) * 3;
                ramp[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                ramp[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                ramp[i + 2] = 128;
            }
        }
        using (var behind = new MagickImage())
        {
            behind.ReadPixels(ramp, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGB));
            behind.Write(own, MagickFormat.Png);
        }
        var look = new BackdropOptions { Kind = BackdropKind.Picture, Picture = own, Canvas = canvas, Padding = 10, Corners = 0, Shadow = 0 };
        using var cropped = Picture(120, 90);
        BackdropComposer.Apply(cropped, look);
        using var whole = Picture(120, 90);
        BackdropComposer.KeepWholePictureBehind = true;
        try
        {
            BackdropComposer.Apply(whole, look);
        }
        finally
        {
            BackdropComposer.KeepWholePictureBehind = false;
        }

        var a = cropped.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var b = whole.GetPixels().ToByteArray(PixelMapping.RGB)!;
        Assert.True(a.Zip(b, (x, y) => Math.Abs(x - y)).Max() <= 1, $"{a.Zip(b, (x, y) => Math.Abs(x - y)).Max()} of 255 from the whole picture behind");
    }

    [Theory]
    [InlineData(@"\??\C:\behind.png")]
    [InlineData(@"\/?\C:\behind.png")]
    [InlineData(@"/\.\pipe\behind.png")]
    [InlineData(@"//?/C:/behind.png")]
    public void A_windows_device_path_however_it_is_written_is_never_put_behind(string path) =>
        Assert.False(BackdropComposer.IsPicturePath(path, out _));

    [Fact]
    public void A_picture_behind_whose_file_changed_since_the_window_showed_it_refuses_the_file()
    {
        // A file the window couldn't see change (a download that paused while the window was in front) was written
        // with the finished picture behind, though the window showed it cut off.
        var own = Path.Combine(fx.NewFolder(), "behind.png");
        using (var behind = new MagickImage(new MagickColor("#3E8E5E"), 300, 200))
            behind.Write(own, MagickFormat.Png);
        var shown = new FileInfo(own);
        var look = new BackdropOptions
        {
            Kind = BackdropKind.Picture,
            Picture = own,
            PictureWritten = shown.LastWriteTimeUtc,
            PictureLength = shown.Length,
        };
        using (var image = Picture())
            BackdropComposer.Apply(image, look);

        using (var behind = new MagickImage(new MagickColor("#E3B341"), 600, 400))
            behind.Write(own, MagickFormat.Png);
        using var later = Picture();
        var refused = Assert.Throws<ConversionRefusedException>(() => BackdropComposer.Apply(later, look));
        Assert.Equal("error.background_picture_changed", refused.ErrorKey);
    }

    [Fact]
    public void A_picture_behind_saved_over_while_it_is_read_for_the_file_refuses_it()
    {
        // Its stamp was looked at before it was read, not after: a save in between gave a picture the window hadn't
        // shown.
        var own = Path.Combine(fx.NewFolder(), "behind.png");
        using (var behind = new MagickImage(new MagickColor("#3E8E5E"), 300, 200))
            behind.Write(own, MagickFormat.Png);
        var shown = (new FileInfo(own).LastWriteTimeUtc, new FileInfo(own).Length);
        var look = new BackdropOptions { Kind = BackdropKind.Picture, Picture = own, PictureWritten = shown.Item1, PictureLength = shown.Item2 };
        BackdropComposer.AfterPictureBehindRead = () =>
        {
            using var behind = new MagickImage(new MagickColor("#E3B341"), 600, 400);
            behind.Write(own, MagickFormat.Png);
        };
        try
        {
            using var image = Picture();
            var refused = Assert.Throws<ConversionRefusedException>(() => BackdropComposer.Apply(image, look));
            Assert.Equal("error.background_picture_changed", refused.ErrorKey);
        }
        finally
        {
            BackdropComposer.AfterPictureBehindRead = null;
        }
    }

    [Theory]
    [InlineData(BackdropCanvas.Auto)]
    [InlineData(BackdropCanvas.Square)]
    public async Task A_gif_with_see_through_parts_comes_out_opaque_on_its_background(BackdropCanvas canvas)
    {
        // Its background colour is its see-through one: the canvas grown round it came out see-through, the file a
        // picture with holes where the window showed the background and the shadow.
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "sticker.gif");
        using (var source = new MagickImage(MagickColors.Transparent, 200, 100))
        {
            new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Red).Rectangle(20, 10, 180, 90).Draw(source);
            source.BackgroundColor = MagickColors.Transparent;
            source.Write(path, MagickFormat.Gif);
        }
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.BackgroundId);
        var look = new BackdropOptions { Kind = BackdropKind.Color, Color = Behind, Padding = 10, Corners = 0, Shadow = 45, Canvas = canvas };

        var job = await fx.ConvertAsync([path], BackgroundTool.ConversionOf(tool, look));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var result = new MagickImage(Assert.Single(job.Outputs));
        Assert.False(result.HasAlpha, "the file has see-through parts");
        Near(Rgb(Behind), At(result, (int)result.Width - 2, (int)result.Height - 2), 2, "the far corner");
    }
}
