// Every .cur Filee writes is a real cursor (type 2) with a hotspot: ImageMagick writes "CUR" as an icon, whose directory
// holds colour planes and bits per pixel where Windows reads the hotspot. From a cursor, the hotspot follows the picture.

using System.Buffers.Binary;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class CursorFileTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private async Task<byte[]> ToCurAsync(string source, Preset? preset = null)
    {
        preset ??= new Preset();
        preset.TargetFormat = "cur";
        var job = await fx.ConvertAsync([source], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));
        return File.ReadAllBytes(Assert.Single(job.Outputs));
    }

    private static ushort Type(byte[] cur) => BinaryPrimitives.ReadUInt16LittleEndian(cur.AsSpan(2));

    private static (int X, int Y) Hotspot(byte[] cur, int entry = 0) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(cur.AsSpan(6 + entry * 16 + 4)),
         BinaryPrimitives.ReadUInt16LittleEndian(cur.AsSpan(6 + entry * 16 + 6)));

    private string SaveCursor()
    {
        var path = Path.Combine(fx.NewFolder(), "pointer.cur");
        File.WriteAllBytes(path, AniBuilder.Cursor((MagickColors.Red, 32, 10, 20)));
        return path;
    }

    [Fact]
    public async Task A_picture_becomes_a_real_cursor_clicking_at_its_top_left_corner()
    {
        var source = EngineFixture.MakeImage(fx.NewFolder(), "a.png", MagickFormat.Png, 64, 64);
        var cur = await ToCurAsync(source);
        Assert.Equal(2, Type(cur)); // not an icon (1) named .cur
        var count = BinaryPrimitives.ReadUInt16LittleEndian(cur.AsSpan(4));
        Assert.All(Enumerable.Range(0, count), i => Assert.Equal((0, 0), Hotspot(cur, i)));
        using var image = new MagickImage(cur, new MagickReadSettings { Format = MagickFormat.Cur });
        Assert.Equal((64u, 64u), (image.Width, image.Height));
    }

    [Fact]
    public async Task A_cursor_keeps_its_hotspot_when_it_is_resized_or_mirrored()
    {
        var source = SaveCursor();
        var half = await ToCurAsync(source, new Preset { Image = { Resize = ResizeMode.Percent, ResizePercent = 50 } });
        Assert.Equal(2, Type(half));
        Assert.Equal((5, 10), Hotspot(half));

        var mirrored = await ToCurAsync(source, new Preset { Image = { Flip = ImageFlip.Horizontal } });
        Assert.Equal((21, 20), Hotspot(mirrored));
    }

    [Fact]
    public async Task A_trimmed_cursor_clicks_at_its_top_left_corner()
    {
        // Where the hotspot ends up once the transparent border is cut isn't known.
        var cur = await ToCurAsync(SaveCursor(), new Preset { Image = { TrimTransparent = true } });
        Assert.Equal(2, Type(cur));
        Assert.Equal((0, 0), Hotspot(cur));
    }
}
