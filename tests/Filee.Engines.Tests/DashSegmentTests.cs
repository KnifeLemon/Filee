// .m4s pieces (#354): which ones need a separate init segment, decided from the top-level ISO BMFF boxes alone.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public class DashSegmentTests
{
    private static byte[] Box(string type, int payload = 8)
    {
        var box = new byte[8 + payload];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.Latin1.GetBytes(type, box.AsSpan(4));
        return box;
    }

    private static bool NeedsInit(params string[] boxes) =>
        DashSegment.NeedsInitSegment(new MemoryStream(boxes.SelectMany(b => Box(b)).ToArray()));

    [Fact]
    public void A_media_segment_without_a_moov_needs_its_init_segment()
    {
        Assert.True(NeedsInit("styp", "sidx", "moof", "mdat", "styp", "sidx", "moof", "mdat"));
        Assert.True(NeedsInit("moof", "mdat"));
    }

    [Fact]
    public void A_fragmented_mp4_with_its_own_moov_is_left_to_ffmpeg()
    {
        // The reporter's file: ftyp + moov, then styp / sidx / moof / mdat sets (GPAC).
        Assert.False(NeedsInit("ftyp", "moov", "styp", "sidx", "moof", "mdat", "styp", "sidx", "moof", "mdat"));
        // A plain MP4 renamed to .m4s, also with the moov at the end.
        Assert.False(NeedsInit("ftyp", "moov", "mdat"));
        Assert.False(NeedsInit("ftyp", "free", "mdat", "moov"));
    }

    [Fact]
    public void Data_that_is_not_a_box_structure_is_left_to_ffmpeg()
    {
        Assert.False(DashSegment.NeedsInitSegment(new MemoryStream([])));
        Assert.False(DashSegment.NeedsInitSegment(new MemoryStream(Encoding.ASCII.GetBytes("#EXTM3U\n#EXT-X-VERSION:7\n"))));
        // A box that claims more bytes than the file has.
        var cut = Box("styp").Concat(Box("moof", 64)).Take(40).ToArray();
        Assert.False(DashSegment.NeedsInitSegment(new MemoryStream(cut)));
    }

    [Fact]
    public void Only_m4s_files_are_checked_and_a_bare_segment_is_refused_with_its_own_message()
    {
        var folder = Directory.CreateTempSubdirectory("filee-m4s-").FullName;
        try
        {
            var segment = Box("styp").Concat(Box("moof")).Concat(Box("mdat")).ToArray();
            var m4s = Path.Combine(folder, "video-3.m4s");
            File.WriteAllBytes(m4s, segment);
            var refused = Assert.Throws<ConversionRefusedException>(() => DashSegment.ThrowIfNeedsInitSegment(m4s));
            Assert.Equal("error.m4s_needs_init", refused.ErrorKey);

            // The same bytes under another name are FFmpeg's to judge.
            var mp4 = Path.Combine(folder, "video-3.mp4");
            File.WriteAllBytes(mp4, segment);
            DashSegment.ThrowIfNeedsInitSegment(mp4);

            var whole = Path.Combine(folder, "whole.m4s");
            File.WriteAllBytes(whole, Box("ftyp").Concat(Box("moov")).Concat(segment).ToArray());
            DashSegment.ThrowIfNeedsInitSegment(whole);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
