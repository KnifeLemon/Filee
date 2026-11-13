// Which files Filee gives FFmpeg with a Hikvision header in front (HikvisionStream): no FFmpeg needed.

using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public class HikvisionStreamTests
{
    private static bool NeedsHeader(byte[] data) => HikvisionStream.NeedsHeader(new MemoryStream(data));

    private static byte[] Stream(params (byte Type, byte Id)[] types) =>
        [.. HikvisionRecording.PackHeader(90_000), .. HikvisionRecording.StreamMap(types)];

    [Fact]
    public void An_sd_card_recording_with_mu_law_sound_needs_the_header()
    {
        Assert.True(NeedsHeader(Stream((0x1B, 0xE0), (0x91, 0xC0))));
        Assert.True(NeedsHeader(Stream((0x24, 0xE0), (0x91, 0xC0)))); // H.265
    }

    [Fact]
    public void Streams_ffmpeg_reads_as_they_are_are_left_alone()
    {
        // A-law (0x90) and AAC (0x0F) are read without the header; an export already starts with it.
        Assert.False(NeedsHeader(Stream((0x1B, 0xE0), (0x90, 0xC0))));
        Assert.False(NeedsHeader(Stream((0x1B, 0xE0), (0x0F, 0xC0))));
        Assert.False(NeedsHeader([.. HikvisionStream.Header(), .. Stream((0x1B, 0xE0), (0x91, 0xC0))]));
        // A program stream without a stream map, and an ISO MP4.
        Assert.False(NeedsHeader([.. HikvisionRecording.PackHeader(90_000), .. new byte[64]]));
        Assert.False(NeedsHeader([0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', .. new byte[64]]));
        Assert.False(NeedsHeader([]));
    }

    [Fact]
    public void A_damaged_stream_map_is_left_to_ffmpeg()
    {
        var map = Stream((0x1B, 0xE0), (0x91, 0xC0));
        // Cut short, and with a map length that runs past the file.
        Assert.False(NeedsHeader(map[..^6]));
        var tooLong = map.ToArray();
        tooLong[14 + 4] = 0xFF;
        Assert.False(NeedsHeader(tooLong));
        // Entries that claim more info bytes than the map holds.
        var badEntry = map.ToArray();
        badEntry[14 + 12 + 3] = 0xFF;
        Assert.False(NeedsHeader(badEntry));
    }

    [Fact]
    public void Only_the_start_of_the_file_is_looked_at()
    {
        byte[] late = [.. HikvisionRecording.PackHeader(90_000), .. new byte[HikvisionStream.ScanBytes],
            .. HikvisionRecording.StreamMap((0x1B, 0xE0), (0x91, 0xC0))];
        Assert.False(NeedsHeader(late));
    }

    [Fact]
    public void The_input_is_a_concat_of_the_header_and_the_file()
    {
        var folder = Directory.CreateTempSubdirectory("filee-hik-").FullName;
        try
        {
            var recording = Path.Combine(folder, "hiv00000.mp4");
            File.WriteAllBytes(recording, Stream((0x1B, 0xE0), (0x91, 0xC0)));
            var work = Path.Combine(folder, "work");

            var input = HikvisionStream.InputFor(recording, work);

            var header = Path.Combine(work, "hikvision-imkh.bin");
            Assert.Equal($"concat:{header}|{recording}", input);
            Assert.Equal(HikvisionStream.Header(), File.ReadAllBytes(header));
            Assert.Equal("IMKH"u8.ToArray(), File.ReadAllBytes(header)[..4]);

            var export = Path.Combine(folder, "export.mp4");
            File.WriteAllBytes(export, Stream((0x1B, 0xE0), (0x90, 0xC0)));
            Assert.Equal(export, HikvisionStream.InputFor(export, work));
            Assert.Equal(Path.Combine(folder, "missing.mp4"), HikvisionStream.InputFor(Path.Combine(folder, "missing.mp4"), work));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
