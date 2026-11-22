// What a marked video frame's copy carries (#1205): its facts round-trip as text whatever the regional format, and
// text that doesn't read as a frame's is no frame's.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class FrameTagsTests
{
    private static readonly FrameFacts Facts = new("Site walk.mp4", 312.4, 48_211_932, 124.7, 3741, "配線が緩い · كابل مفكوك 🔌");

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")] // a decimal comma
    [InlineData("ar-SA")]
    public void Facts_round_trip_as_plain_ascii_whatever_the_regional_format(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var text = FrameTags.ToText(Facts);
            Assert.All(text, c => Assert.InRange(c, ' ', '~'));
            Assert.Contains("124.7", text, StringComparison.Ordinal);
            Assert.Equal(Facts, FrameTags.FromText(text));
            var bare = Facts with { FrameNumber = null, Note = "" };
            Assert.Equal(bare, FrameTags.FromText(FrameTags.ToText(bare)));
            var awkward = Facts with { Note = "line one\nline two\0 \"quoted\" \\ end" };
            Assert.Equal(awkward, FrameTags.FromText(FrameTags.ToText(awkward)));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>
    /// The frame's time comes back as the playhead had it (going back to it shows that frame; 2.067 would be after the
    /// frame at 2.0667 s), so its millisecond, which names the copy and tells copies apart, is the frame's own
    /// (2.0664999 stays 2066); the video's length to the millisecond.
    /// </summary>
    [Fact]
    public void A_frames_time_comes_back_as_it_was_and_the_videos_length_to_the_millisecond()
    {
        var read = FrameTags.FromText(FrameTags.ToText(Facts with { FrameSeconds = 62 / 30.0, VideoSeconds = 312.4004 }));
        Assert.Equal((62 / 30.0, 312.4), (read!.FrameSeconds, read.VideoSeconds));
        var random = new Random(1205);
        foreach (var time in new[] { 2.0664999, 2.0665, 1001 / 30000.0 * 4321 }.Concat(Enumerable.Range(0, 10_000).Select(_ => random.NextDouble() * 7200)))
        {
            var back = FrameTags.FromText(FrameTags.ToText(Facts with { FrameSeconds = time }))!.FrameSeconds;
            Assert.Equal(time, back);
            Assert.Equal(FrameTags.Milliseconds(time), FrameTags.Milliseconds(back));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"videoName\":\"\",\"videoSeconds\":1,\"videoBytes\":1,\"frameSeconds\":1,\"note\":\"\"}")]
    [InlineData("{\"videoName\":\"a.mp4\",\"videoSeconds\":1,\"videoBytes\":-1,\"frameSeconds\":1,\"note\":\"\"}")]
    [InlineData("{\"videoName\":\"a.mp4\",\"videoSeconds\":1,\"videoBytes\":1,\"frameSeconds\":-2,\"note\":\"\"}")]
    [InlineData("{\"videoName\":\"a.mp4\",\"videoSeconds\":1,\"videoBytes\":1,\"frameSeconds\":1,\"frameNumber\":-3,\"note\":\"\"}")]
    [InlineData("{\"videoName\":\"a.mp4\",\"videoSeconds\":1,\"videoBytes\":1,\"frameSeconds\":\"1\",\"note\":\"\"}")]
    [InlineData("{\"videoName\":\"a.mp4\",\"videoSeconds\":1,\"videoBytes\":1,\"frameSeconds\":1}")] // no note
    public void Text_that_isnt_a_frames_reads_as_none(string? text)
    {
        Assert.Null(FrameTags.FromText(text));
    }

    [Fact]
    public void The_label_gives_the_time_to_the_millisecond_and_the_number_grouped()
    {
        Assert.Equal("2:04.700 · frame 3,741", FrameTags.Label(Facts, "frame {0}", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("2:04.700 · Bild 3.741", FrameTags.Label(Facts, "Bild {0}", CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("2:04.700", FrameTags.Label(Facts with { FrameNumber = null }, "frame {0}", CultureInfo.InvariantCulture));
        Assert.Equal("1:02:05.300", FrameTags.Time(3725.3));
        Assert.Equal("0:00.000", FrameTags.Time(double.NaN));
        Assert.Equal("1:00.000", FrameTags.Time(59.9996));
    }

    [Fact]
    public void A_failed_frame_save_leaves_no_retry_with_its_note_in_the_history()
    {
        var conversion = AnnotateTool.ConversionOf(BuiltInData.AnnotatePresets().Single(), BuiltInData.SameAsSource, []);
        conversion.Image.Frame = Facts;
        var job = new ConversionJob(["Site walk 2m04.700s.png"], conversion, "Annotate");
        job.Files[0].State = FileState.Failed;
        Assert.Null(HistoryEntry.From(job).Preset);
    }

    [Fact]
    public void Values_no_video_has_read_as_none()
    {
        Assert.Null(FrameTags.FromText(FrameTags.ToText(Facts with { FrameSeconds = 1e300 })));
        Assert.Null(FrameTags.FromText(FrameTags.ToText(Facts with { Note = new string('x', 5000) })));
        Assert.Null(FrameTags.FromText(FrameTags.ToText(Facts with { VideoName = new string('x', 2000) })));
    }

    [Theory]
    [InlineData("frame {1}")]
    [InlineData("frame {0")]
    public void A_broken_translation_still_labels_the_frame(string word)
    {
        Assert.Equal("2:04.700 · 3,741", FrameTags.Label(Facts, word, CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void A_video_of_the_same_name_elsewhere_is_another()
    {
        Assert.True(FrameTags.SameVideo(Facts, "Site walk.mp4", 312.4004, 48_211_932));
        Assert.False(FrameTags.SameVideo(Facts, "Site walk.mp4", 312.4, 48_211_933));
        Assert.False(FrameTags.SameVideo(Facts, "Site walk.mp4", 300, 48_211_932));
        Assert.False(FrameTags.SameVideo(Facts, "site walk.mp4", 312.4, 48_211_932));
        // The same name in another Unicode form (as a synced folder may hand it over) is the same video.
        var composed = Facts with { VideoName = "Caf\u00E9 \uD68C\uC758.mp4" };
        Assert.True(FrameTags.SameVideo(composed, "Cafe\u0301 \u1112\u116C\u110B\u1174.mp4", 312.4, 48_211_932));
    }
}

public class FrameTagsFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-frame-tags-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A minimal PNG: signature, IHDR, the given chunks, an empty IDAT and IEND (CRCs left at 0).</summary>
    private string Png(string name, params (string Type, byte[] Data)[] chunks)
    {
        var path = Path.Combine(_folder, name);
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            file.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            file.Write(System.Text.Encoding.ASCII.GetBytes(type));
            file.Write(data);
            file.Write([0, 0, 0, 0]);
        }
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        foreach (var (type, data) in chunks)
            Chunk(type, data);
        Chunk("IDAT", []);
        Chunk("tEXt", Text(FrameTags.Key, "after the picture")); // never read: past IDAT
        Chunk("IEND", []);
        return path;
    }

    private static byte[] Text(string key, string value) =>
        [.. System.Text.Encoding.Latin1.GetBytes(key), 0, .. System.Text.Encoding.Latin1.GetBytes(value)];

    private static byte[] Compressed(string key, string value)
    {
        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal))
            zlib.Write(System.Text.Encoding.Latin1.GetBytes(value));
        return [.. System.Text.Encoding.Latin1.GetBytes(key), 0, 0, .. packed.ToArray()];
    }

    private static byte[] International(string key, string value, bool compressed)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(value);
        if (compressed)
        {
            using var packed = new MemoryStream();
            using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal))
                zlib.Write(text);
            text = packed.ToArray();
        }
        return [.. System.Text.Encoding.Latin1.GetBytes(key), 0, (byte)(compressed ? 1 : 0), 0, 0, 0, .. text];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_copys_text_is_read_without_its_picture(bool compressed)
    {
        var note = "配線が緩い · كابل مفكوك 🔌";
        var facts = new FrameFacts("Site walk.mp4", 312.4, 48_211_932, 124.7, 3741, note);
        var path = Png("copy.png", ("tEXt", Text("Comment", "someone else's")), ("iTXt", International(FrameTags.Key, FrameTags.ToText(facts), compressed)));
        Assert.Equal(facts, FrameTags.ReadFile(path));
        var packed = Png("packed.png", ("zTXt", Compressed(FrameTags.Key, FrameTags.ToText(facts))));
        Assert.Equal(facts, FrameTags.ReadFile(packed));
    }

    [Fact]
    public void A_damaged_text_of_another_program_doesnt_hide_the_frames()
    {
        var facts = new FrameFacts("Site walk.mp4", 312.4, 48_211_932, 124.7, 3741, "note");
        var path = Png("damaged.png", ("zTXt", [.. System.Text.Encoding.Latin1.GetBytes("Comment"), 0, 0, 1, 2, 3, 4, 5]),
            ("tEXt", Text(FrameTags.Key, FrameTags.ToText(facts))));
        Assert.Equal(facts, FrameTags.ReadFile(path));
    }

    [Fact]
    public void Anything_that_isnt_a_marked_frames_png_reads_as_none()
    {
        Assert.Null(FrameTags.ReadFile(Png("plain.png", ("tEXt", Text("Comment", "a photo")))));
        var notPng = Path.Combine(_folder, "notes.png");
        File.WriteAllText(notPng, "not a picture");
        Assert.Null(FrameTags.ReadFile(notPng));
        var truncated = Path.Combine(_folder, "cut.png");
        File.WriteAllBytes(truncated, File.ReadAllBytes(Png("whole.png", ("tEXt", Text(FrameTags.Key, "x"))))[..20]);
        Assert.Null(FrameTags.ReadFile(truncated));
        Assert.Null(FrameTags.ReadFile(Path.Combine(_folder, "missing.png")));
        var huge = Png("huge.png", ("tEXt", [.. Text(FrameTags.Key, ""), .. new byte[70_000]]));
        Assert.Null(FrameTags.ReadFile(huge));
    }

    /// <summary>
    /// A video name Windows allows with a lone surrogate in it, written into a copy's text (which holds it as U+FFFD),
    /// still matches its video, without an error.
    /// </summary>
    [Fact]
    public void A_name_with_a_lone_surrogate_matches_after_it_is_written()
    {
        var odd = new FrameFacts("clip\uD800.mp4", 312.4, 1000, 124.7, 3741, "");
        var read = Assert.IsType<FrameFacts>(FrameTags.FromText(FrameTags.ToText(odd)));
        Assert.True(FrameTags.SameVideo(read, "clip\uD800.mp4", odd.VideoSeconds, odd.VideoBytes));
        Assert.False(FrameTags.SameVideo(read, "clip.mp4", odd.VideoSeconds, odd.VideoBytes));
        // U+FFFE, which no Unicode form allows (NTFS and ext4 do): compared as it is, never an error.
        var noncharacter = new FrameFacts("odd\uFFFEname.mp4", 312.4, 1000, 124.7, 3741, "");
        Assert.True(FrameTags.SameVideo(noncharacter, "odd\uFFFEname.mp4", 312.4, 1000));
        Assert.False(FrameTags.SameVideo(noncharacter, "Site walk.mp4", 312.4, 1000));
    }
}
