// DVD-Video disc images (DvdVideoIso): the title FFmpeg's dvdvideo demuxer is given, read from generated images with
// IFO files written here, and what is refused (data discs, CSS-scrambled video). No FFmpeg needed.

using System.Buffers.Binary;
using DiscUtils.Iso9660;
using Filee.Core.Conversion;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public sealed class DvdVideoIsoTests : IDisposable
{
    private const int Sector = DvdVideoIso.Sector;

    private readonly string _root = Directory.CreateTempSubdirectory("filee-dvd-iso-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>A title of a generated disc: its title set, its number in that set and its playing time.</summary>
    private sealed record TitleSpec(int TitleSet, int TitleInSet, TimeSpan Length);

    [Fact]
    public void The_longest_title_is_given_to_the_dvdvideo_demuxer()
    {
        // Title 1 is a warning, title 2 the film, title 3 a trailer in the same title set as the warning.
        var iso = Disc("film.iso",
            new(1, 1, TimeSpan.FromSeconds(20)), new(2, 1, new TimeSpan(1, 23, 45)), new(1, 2, TimeSpan.FromMinutes(2)));
        var notices = new List<string>();

        var input = MediaInput.For(iso, notice: notices.Add);

        Assert.Equal($"dvdvideo:2:{iso}", input);
        Assert.True(MediaInput.IsUrl(input));
        Assert.Equal(["-f", "dvdvideo", "-title", "2", "-i", iso], MediaInput.Open(input));
        // The trailer is long enough to say it was left out; the warning alone wouldn't be.
        Assert.Equal(["notice.dvd_titles_left_out"], notices);
    }

    [Fact]
    public void A_disc_with_one_title_says_nothing_is_left_out()
    {
        var iso = Disc("one.iso", new(1, 1, new TimeSpan(0, 45, 0)), new(1, 2, TimeSpan.FromSeconds(15)));
        var notices = new List<string>();

        Assert.Equal($"dvdvideo:1:{iso}", MediaInput.For(iso, notice: notices.Add));
        Assert.Empty(notices);
    }

    [Fact]
    public void The_titles_and_their_lengths_come_from_the_ifo_files()
    {
        var iso = Disc("lengths.iso", new(1, 1, new TimeSpan(0, 1, 2, 3, 400)), new(3, 1, TimeSpan.FromMinutes(90)));
        using var file = File.OpenRead(iso);

        var titles = DvdVideoIso.Titles(file, DvdVideoIso.Read(file));

        Assert.Equal([1, 2], titles.Select(t => t.Number));
        Assert.Equal([1, 3], titles.Select(t => t.TitleSet));
        // 400 ms is 10 frames at 25 fps.
        Assert.Equal(new TimeSpan(0, 1, 2, 3, 400), titles[0].Length);
        Assert.Equal(TimeSpan.FromMinutes(90), titles[1].Length);
    }

    [Fact]
    public void A_data_disc_is_refused_and_still_an_archive()
    {
        var iso = Path.Combine(_root, "data.iso");
        var builder = new CDBuilder();
        builder.AddFile("SETUP.EXE", "MZ"u8.ToArray());
        builder.Build(iso);

        var refused = Assert.Throws<ConversionRefusedException>(() => MediaInput.For(iso));
        Assert.Equal("error.iso_no_dvd_video", refused.ErrorKey);
    }

    [Fact]
    public void A_dvd_with_scrambled_video_is_refused()
    {
        var iso = ScrambledOrNot("scrambled.iso", scrambled: true, new TitleSpec(1, 1, TimeSpan.FromMinutes(80)));

        var refused = Assert.Throws<ConversionRefusedException>(() => MediaInput.For(iso));
        Assert.Equal("error.dvd_scrambled", refused.ErrorKey);
    }

    [Fact]
    public void A_file_that_is_no_disc_image_is_damaged()
    {
        var iso = Path.Combine(_root, "not.iso");
        File.WriteAllBytes(iso, new byte[40 * Sector]);

        Assert.Throws<InvalidDataException>(() => MediaInput.For(iso));
    }

    [Fact]
    public void A_title_whose_ifo_offsets_point_past_2_gib_is_left_out()
    {
        // Title 1's chapter offset and title 2's program chain offset are 0x80000000 or more: read as int they'd be
        // negative and pass the bounds checks. Only title 3 is left to choose.
        var iso = Disc("offsets.iso",
            new(1, 1, new TimeSpan(1, 30, 0)), new(2, 1, new TimeSpan(1, 20, 0)), new(3, 1, TimeSpan.FromMinutes(5)));
        using (var file = File.Open(iso, FileMode.Open, FileAccess.ReadWrite))
        {
            var files = DvdVideoIso.Read(file);
            Patch(file, files.Single(f => f.Name == "VTS_01_0.IFO").Start + Sector + 8, 0x80000000);
            Patch(file, files.Single(f => f.Name == "VTS_02_0.IFO").Start + 2 * Sector + 8 + 4, 0xFFFFFFF0);
        }

        using (var file = File.OpenRead(iso))
            Assert.Equal([3], DvdVideoIso.Titles(file, DvdVideoIso.Read(file)).Select(t => t.Number));
        Assert.Equal($"dvdvideo:3:{iso}", MediaInput.For(iso));

        static void Patch(FileStream file, long at, uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            file.Position = at;
            file.Write(bytes);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x23, 0x45, 0x10 }, 1, 23, 45, 400)]  // 25 fps (rate bits 01), frame 10
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0xD5 }, 0, 0, 1, 500)]    // 30 fps (rate bits 11), frame 15
    public void Playing_times_are_bcd_with_frames(byte[] time, int hours, int minutes, int seconds, int milliseconds) =>
        Assert.Equal(new TimeSpan(0, hours, minutes, seconds, milliseconds), DvdVideoIso.PlayingTime(time));

    [Fact]
    public void Dvd_titles_are_opened_wherever_ffmpeg_or_ffprobe_reads_them()
    {
        var input = MediaInput.DvdVideo(@"D:\DVD\Film.iso", 4);
        var other = Path.Combine(_root, "clip.mp4");

        Assert.Equal(["-v", "error", "-f", "dvdvideo", "-title", "4", "-i", @"D:\DVD\Film.iso", "-map", "0:V:0", "out.mkv"],
            MediaInput.Arguments(["-v", "error", "-i", input, "-map", "0:V:0", "out.mkv"]));
        // ffprobe takes its input without -i.
        Assert.Equal(["-show_streams", "-f", "dvdvideo", "-title", "4", "-i", @"D:\DVD\Film.iso"],
            MediaInput.Arguments(["-show_streams", input]));
        string[] plain = ["-i", other, "out.mkv"];
        Assert.Same(plain, MediaInput.Arguments(plain));
        Assert.Equal(@"D:\DVD\Film.iso", MediaInput.DvdVideoPath(input));
    }

    [Fact]
    public void Scrambling_is_read_from_the_pes_header_of_audio_and_video_packets()
    {
        Assert.Equal(0, DvdVideoIso.ScramblingOf(Pack(0xE0, scrambled: false)));
        Assert.Equal(1, DvdVideoIso.ScramblingOf(Pack(0xBD, scrambled: true)));
        // A navigation pack (private stream 2) holds no A/V packet.
        Assert.Null(DvdVideoIso.ScramblingOf(Pack(0xBF, scrambled: false)));
        Assert.Null(DvdVideoIso.ScramblingOf(new byte[Sector]));
    }

    // ───────────────────────── Generated discs ─────────────────────────

    private string Disc(string name, params TitleSpec[] titles) => ScrambledOrNot(name, false, titles);

    /// <summary>
    /// A DVD-Video image: VIDEO_TS.IFO with the title table, one VTS_nn_0.IFO per title set with a program chain per
    /// title, and a VTS_nn_1.VOB of MPEG-2 packs.
    /// </summary>
    private string ScrambledOrNot(string name, bool scrambled, params TitleSpec[] titles)
    {
        var builder = new CDBuilder();
        builder.AddDirectory("AUDIO_TS");
        builder.AddFile(@"VIDEO_TS\VIDEO_TS.IFO", VideoManager(titles));
        foreach (var set in titles.GroupBy(t => t.TitleSet))
        {
            builder.AddFile($@"VIDEO_TS\VTS_{set.Key:00}_0.IFO", TitleSet(set.OrderBy(t => t.TitleInSet).ToList()));
            builder.AddFile($@"VIDEO_TS\VTS_{set.Key:00}_1.VOB", Vob(scrambled));
        }
        var path = Path.Combine(_root, name);
        builder.Build(path);
        return path;
    }

    /// <summary>VIDEO_TS.IFO: VMGI_MAT, and the title table (TT_SRPT) in its second sector.</summary>
    private static byte[] VideoManager(TitleSpec[] titles)
    {
        var ifo = new byte[2 * Sector];
        "DVDVIDEO-VMG"u8.CopyTo(ifo);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xC4), 1);
        var table = ifo.AsSpan(Sector);
        BinaryPrimitives.WriteUInt16BigEndian(table, (ushort)titles.Length);
        BinaryPrimitives.WriteUInt32BigEndian(table[4..], (uint)(8 + titles.Length * 12 - 1));
        for (var i = 0; i < titles.Length; i++)
        {
            var entry = table.Slice(8 + i * 12, 12);
            entry[1] = 1; // angles
            BinaryPrimitives.WriteUInt16BigEndian(entry[2..], 1); // chapters
            entry[6] = (byte)titles[i].TitleSet;
            entry[7] = (byte)titles[i].TitleInSet;
        }
        return ifo;
    }

    /// <summary>VTS_nn_0.IFO: VTSI_MAT, the chapter table (sector 1) and the program chains (sector 2), one per title.</summary>
    private static byte[] TitleSet(IReadOnlyList<TitleSpec> titles)
    {
        var ifo = new byte[3 * Sector];
        "DVDVIDEO-VTS"u8.CopyTo(ifo);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xC8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xCC), 2);
        var count = titles.Max(t => t.TitleInSet);

        // Chapter table: count, end, an offset per title, then one chapter (program chain n, program 1) per title.
        var chapters = ifo.AsSpan(Sector, Sector);
        BinaryPrimitives.WriteUInt16BigEndian(chapters, (ushort)count);
        var entries = 8 + count * 4;
        BinaryPrimitives.WriteUInt32BigEndian(chapters[4..], (uint)(entries + count * 4 - 1));
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(chapters[(8 + i * 4)..], (uint)(entries + i * 4));
            BinaryPrimitives.WriteUInt16BigEndian(chapters[(entries + i * 4)..], (ushort)(i + 1));
            BinaryPrimitives.WriteUInt16BigEndian(chapters[(entries + i * 4 + 2)..], 1);
        }

        // Program chains: count, end, an 8-byte entry per chain, then each chain's 16 bytes (playing time at 4).
        var chains = ifo.AsSpan(2 * Sector, Sector);
        BinaryPrimitives.WriteUInt16BigEndian(chains, (ushort)count);
        var first = 8 + count * 8;
        BinaryPrimitives.WriteUInt32BigEndian(chains[4..], (uint)(first + count * 16 - 1));
        for (var i = 0; i < count; i++)
        {
            var at = first + i * 16;
            BinaryPrimitives.WriteUInt32BigEndian(chains[(8 + i * 8 + 4)..], (uint)at);
            var length = titles.FirstOrDefault(t => t.TitleInSet == i + 1)?.Length ?? TimeSpan.Zero;
            Bcd(length).CopyTo(chains[(at + 4)..]);
        }
        return ifo;
    }

    /// <summary>A playing time as the disc states it: BCD hours, minutes, seconds, and frames at 25 fps.</summary>
    private static byte[] Bcd(TimeSpan time)
    {
        static byte Two(int value) => (byte)((value / 10 << 4) | (value % 10));
        return [Two((int)time.TotalHours), Two(time.Minutes), Two(time.Seconds), (byte)(0x40 | Two(time.Milliseconds / 40))];
    }

    /// <summary>A VOB of video packs, their PES scrambling bits set or not.</summary>
    private static byte[] Vob(bool scrambled)
    {
        const int packs = 64;
        var vob = new byte[packs * Sector];
        for (var i = 0; i < packs; i++)
            Pack(i % 2 == 0 ? (byte)0xE0 : (byte)0xBD, scrambled).CopyTo(vob, i * Sector);
        return vob;
    }

    /// <summary>One MPEG-2 pack holding a single PES packet of stream <paramref name="id"/>.</summary>
    private static byte[] Pack(byte id, bool scrambled)
    {
        var pack = new byte[Sector];
        byte[] header = [0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 1, 0x89, 0xC3, 0xF8];
        header.CopyTo(pack, 0);
        var at = header.Length;
        pack[at + 2] = 1;
        pack[at + 3] = id;
        BinaryPrimitives.WriteUInt16BigEndian(pack.AsSpan(at + 4), (ushort)(Sector - at - 6));
        pack[at + 6] = (byte)(0x80 | (scrambled ? 0x10 : 0));
        return pack;
    }
}
