// Windows animated cursors (.ani, issue #1365): every step with its time to the animated targets, the first picture to
// the others, and to CUR the first frame with its hotspot. The files are built here (AniBuilder).

using System.Buffers.Binary;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class AniTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string Save(byte[] data, string name = "busy.ani")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static AniBuilder ThreeColours() =>
        new AniBuilder().Frame(MagickColors.Red).Frame(MagickColors.Lime).Frame(MagickColors.Blue);

    private async Task<string> ConvertAsync(string ani, string target, Preset? preset = null)
    {
        preset ??= new Preset();
        preset.TargetFormat = target;
        var job = await fx.ConvertAsync([ani], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    private static IMagickColor<byte> Centre(IMagickImage<byte> image)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel((int)image.Width / 2, (int)image.Height / 2)!.ToColor()!;
    }

    private static (int X, int Y) CursorHotspot(byte[] cur, int entry = 0) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(cur.AsSpan(6 + entry * 16 + 4)),
         BinaryPrimitives.ReadUInt16LittleEndian(cur.AsSpan(6 + entry * 16 + 6)));

    [Theory]
    [InlineData("busy.ani")]
    [InlineData("WAIT.ANI")]
    public void An_ani_is_detected_by_its_extension_and_read_only(string name)
    {
        var format = FormatRegistry.Detect(name);
        Assert.Equal("ani", format!.Id);
        Assert.Equal(FormatCategory.Image, format.Category);
        Assert.False(format.Writable);
        var magick = new MagickImageConverter();
        Assert.True(magick.CanRead("ani"));
        Assert.False(magick.CanWrite("ani"));
        Assert.Contains(magick.Edges, e => e.From == "ani" && e.To == "gif");
    }

    [Fact]
    public async Task Without_seq_or_rate_every_frame_plays_in_order_at_the_default_time_forever()
    {
        // 6 jiffies = 1/10 s = 10 hundredths.
        var gif = await ConvertAsync(Save(ThreeColours().Build()), "gif");
        using var frames = new MagickImageCollection(gif);
        Assert.Equal(3, frames.Count);
        Assert.Equal([MagickColors.Red, MagickColors.Lime, MagickColors.Blue], frames.Select(f => Centre(f)));
        Assert.All(frames, f => Assert.Equal(10u, f.AnimationDelay));
        Assert.Equal(0u, frames[0].AnimationIterations); // loops forever
        // The cursor's transparent corner stays transparent.
        using var pixels = frames[1].GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0)!.ToColor()!.A);
    }

    [Fact]
    public async Task Seq_repeats_frames_and_rate_gives_each_step_its_time()
    {
        var ani = new AniBuilder().Frame(MagickColors.Red).Frame(MagickColors.Blue);
        ani.Sequence = [0, 1, 0, 0, 1];
        ani.Rates = [6, 12, 30, 3, 60];
        var gif = await ConvertAsync(Save(ani.Build()), "gif");
        using var frames = new MagickImageCollection(gif);
        Assert.Equal(5, frames.Count);
        Assert.Equal([MagickColors.Red, MagickColors.Blue, MagickColors.Red, MagickColors.Red, MagickColors.Blue], frames.Select(f => Centre(f)));
        Assert.Equal([10u, 20u, 50u, 5u, 100u], frames.Select(f => f.AnimationDelay));
    }

    [Fact]
    public async Task A_short_rate_chunk_leaves_the_last_steps_at_the_default_time()
    {
        var ani = ThreeColours();
        ani.Rates = [30];
        ani.DefaultJiffies = 12;
        var gif = await ConvertAsync(Save(ani.Build()), "gif");
        using var frames = new MagickImageCollection(gif);
        Assert.Equal([50u, 20u, 20u], frames.Select(f => f.AnimationDelay));
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("apng")]
    public async Task Other_animated_targets_keep_the_animation(string target)
    {
        var output = await ConvertAsync(Save(ThreeColours().Build()), target);
        using var frames = target == "apng" ? Apng.Read(output) : new MagickImageCollection(output);
        Assert.Equal(3, frames.Count);
        Assert.Equal(MagickColors.Blue, Centre(frames[2]));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("ico")]
    public async Task Still_targets_get_the_first_step(string target)
    {
        var ani = ThreeColours();
        ani.Sequence = [1, 0, 2]; // the first step shows the second frame
        var output = await ConvertAsync(Save(ani.Build()), target);
        using var image = new MagickImageCollection(output);
        var centre = Centre(image.OrderByDescending(i => i.Width).First());
        Assert.True(centre.R < 8 && centre.G > 247 && centre.B < 8, centre.ToString()); // lime, give or take JPEG
    }

    [Fact]
    public async Task A_frame_with_several_sizes_gives_its_largest()
    {
        var ani = new AniBuilder();
        ani.Frames.Add(AniBuilder.Cursor((MagickColors.Red, 16, 0, 0), (MagickColors.Blue, 48, 0, 0), (MagickColors.Lime, 32, 0, 0)));
        ani.Frames.Add(AniBuilder.Cursor((MagickColors.Yellow, 16, 0, 0), (MagickColors.Purple, 48, 0, 0)));
        var gif = await ConvertAsync(Save(ani.Build()), "gif");
        using var frames = new MagickImageCollection(gif);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal((48u, 48u), (f.Width, f.Height)));
        Assert.Equal([MagickColors.Blue, MagickColors.Purple], frames.Select(f => Centre(f)));
    }

    [Fact]
    public async Task Frames_of_different_sizes_play_on_one_canvas()
    {
        var gif = await ConvertAsync(Save(new AniBuilder().Frame(MagickColors.Red, 32).Frame(MagickColors.Blue, 48).Build()), "gif");
        using var frames = new MagickImageCollection(gif);
        Assert.All(frames, f => Assert.Equal((48u, 48u), (f.Width, f.Height)));
        using var pixels = frames[0].GetPixels();
        Assert.Equal(MagickColors.Red, pixels.GetPixel(16, 16)!.ToColor());
        Assert.Equal(0, pixels.GetPixel(40, 40)!.ToColor()!.A); // outside the smaller frame: transparent
    }

    [Fact]
    public async Task To_cur_the_first_frame_is_kept_as_it_is_with_its_hotspot()
    {
        var first = AniBuilder.Cursor((MagickColors.Red, 32, 7, 21), (MagickColors.Red, 16, 3, 10));
        var ani = new AniBuilder();
        ani.Frames.Add(first);
        ani.Frame(MagickColors.Blue);
        var cur = await ConvertAsync(Save(ani.Build()), "cur");
        Assert.Equal(first, File.ReadAllBytes(cur)); // every size, the hotspots with them
    }

    [Fact]
    public async Task To_cur_a_resized_or_turned_picture_keeps_its_hotspot_where_it_was()
    {
        var ani = new AniBuilder();
        ani.Frames.Add(AniBuilder.Cursor((MagickColors.Red, 32, 10, 20)));
        ani.Frame(MagickColors.Blue);
        var path = Save(ani.Build());

        var half = await ConvertAsync(path, "cur", new Preset { Image = { Resize = ResizeMode.Percent, ResizePercent = 50 } });
        var data = File.ReadAllBytes(half);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2))); // a cursor, not an icon named .cur
        using (var image = new MagickImage(half))
            Assert.Equal((16u, 16u), (image.Width, image.Height));
        Assert.Equal((5, 10), CursorHotspot(data));

        // Turned right, the point 10 px from the left and 20 px from the top of a 32 px cursor is 11 px from the left
        // and 10 px from the top.
        var turned = await ConvertAsync(path, "cur", new Preset { Image = { Rotation = ImageRotation.Right90 } });
        Assert.Equal((11, 10), CursorHotspot(File.ReadAllBytes(turned)));
    }

    [Theory]
    [InlineData(ImageRotation.None, ImageFlip.None, 64, 64, 21, 41)]
    [InlineData(ImageRotation.Left90, ImageFlip.None, 32, 32, 20, 21)]
    [InlineData(ImageRotation.Rotate180, ImageFlip.None, 32, 32, 21, 11)]
    [InlineData(ImageRotation.None, ImageFlip.Horizontal, 32, 32, 21, 20)]
    [InlineData(ImageRotation.None, ImageFlip.Vertical, 32, 32, 10, 11)]
    public void The_hotspot_follows_the_picture(ImageRotation rotation, ImageFlip flip, int width, int height, int x, int y) =>
        Assert.Equal((x, y), CursorFile.Moved((10, 20, 32, 32), new ImageOptions { Rotation = rotation, Flip = flip }, (uint)width, (uint)height));

    [Fact]
    public void Jiffies_become_hundredths_of_a_second()
    {
        Assert.Equal(0u, Ani.Delay(0));
        Assert.Equal(2u, Ani.Delay(1)); // 1.67
        Assert.Equal(100u, Ani.Delay(60));
        Assert.Equal(65_535u, Ani.Delay(uint.MaxValue));
    }

    public static TheoryData<string, string> Broken => new()
    {
        { "cut short", "cut short" },
        { "not riff", "not a Windows animated cursor" },
        { "no header", "no header" },
        { "no frames", "no frames" },
        { "raw bitmaps", "raw bitmaps" },
        { "seq past the frames", "damaged" },
        { "damaged frame", "damaged" },
        { "huge frame", "too large" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public async Task A_broken_file_is_refused_with_a_clear_message(string kind, string message)
    {
        var good = ThreeColours().Build();
        byte[] data;
        switch (kind)
        {
            case "cut short":
                data = good[..(good.Length - 100)];
                break;
            case "not riff":
                data = "This is a text file renamed to .ani"u8.ToArray();
                break;
            case "no header":
                data = [.. "RIFF"u8, 4, 0, 0, 0, .. "ACON"u8];
                break;
            case "no frames":
                data = new AniBuilder().Build();
                break;
            case "raw bitmaps":
                var raw = ThreeColours();
                raw.IconFrames = false;
                data = raw.Build();
                break;
            case "seq past the frames":
                var seq = ThreeColours();
                seq.Sequence = [0, 1, 3];
                data = seq.Build();
                break;
            case "damaged frame":
                var damaged = new AniBuilder();
                damaged.Frames.Add([0, 0, 2, 0, 1, 0, 32, 32, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 0, 0, 22, 0, 0, 0]);
                data = damaged.Build();
                break;
            default:
                // A frame whose directory says it holds a 4096 px picture.
                var cursor = AniBuilder.Cursor((MagickColors.Red, 32, 0, 0));
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(cursor.AsSpan(6 + 12));
                BinaryPrimitives.WriteInt32LittleEndian(cursor.AsSpan((int)offset + 4), 4096);
                BinaryPrimitives.WriteInt32LittleEndian(cursor.AsSpan((int)offset + 8), 8192);
                var huge = new AniBuilder();
                huge.Frames.Add(cursor);
                data = huge.Build();
                break;
        }

        var job = await fx.ConvertAsync([Save(data)], new Preset { TargetFormat = "gif" });
        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Contains(message, file.ErrorDetail);
    }
}
