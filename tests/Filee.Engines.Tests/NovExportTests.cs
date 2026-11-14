// Reading the index of Wisenet WAVE / Nx Witness exports (NovExport): no FFmpeg needed.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public class NovExportTests
{
    private static readonly (string, byte[])[] Layout =
    [
        ("uuid.bin", Encoding.ASCII.GetBytes("{5b3c0f8e-0000-4000-8000-000000000001}")),
        ("range.bin", new byte[16]),
        ("e3e9a385-7fe0-3ba5-5482-a86cde7faf48_1791212400000.mkv", new byte[300]),
        ("e3e9a385-7fe0-3ba5-5482-a86cde7faf48_1791212460000.mkv", new byte[900]),
        ("watermark.txt", "{}"u8.ToArray()),
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_streams_are_found_by_name_with_their_data(int version)
    {
        var file = NovFile.Write(Layout, version);

        var index = NovExport.Read(new MemoryStream(file));

        Assert.False(index.Encrypted);
        Assert.Equal(Layout.Select(s => s.Item1), index.Streams.Select(s => s.Name));
        Assert.Equal(Layout.Select(s => (long)s.Item2.Length), index.Streams.Select(s => s.Length));
        var uuid = index.Streams[0];
        Assert.Equal(Layout[0].Item2, file[(int)uuid.Start..(int)uuid.End]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_protected_export_is_refused(int version)
    {
        var path = Save(NovFile.Write(Layout, version, encrypted: true));
        try
        {
            Assert.True(NovExport.Read(new MemoryStream(File.ReadAllBytes(path))).Encrypted);
            var refused = Assert.Throws<ConversionRefusedException>(() => NovExport.InputFor(path));
            Assert.Equal("error.nov_password", refused.ErrorKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_largest_recording_is_given_to_ffmpeg_through_subfile()
    {
        var file = NovFile.Write(Layout);
        var path = Save(file);
        try
        {
            var recording = NovExport.Read(new MemoryStream(file)).Streams[3];

            Assert.Equal($"subfile,,start,{recording.Start},end,{recording.End},,:{path}", NovExport.InputFor(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_export_without_a_recording_is_refused()
    {
        var path = Save(NovFile.Write([.. Layout.Where(s => !s.Item1.EndsWith(".mkv", StringComparison.Ordinal))]));
        try
        {
            Assert.Equal("error.nov_no_video", Assert.Throws<ConversionRefusedException>(() => NovExport.InputFor(path)).ErrorKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_damaged_index_throws_invalid_data()
    {
        var file = NovFile.Write(Layout);
        // Not an export at all, cut short, an entry count that doesn't fit, and a stream offset past the data.
        Assert.Throws<InvalidDataException>(() => NovExport.Read(new MemoryStream(new byte[64])));
        Assert.Throws<InvalidDataException>(() => NovExport.Read(new MemoryStream(file[..^1])));
        var count = file.ToArray();
        count[^(20 + 5 * 16 + 4)] = 0xFF;
        Assert.Throws<InvalidDataException>(() => NovExport.Read(new MemoryStream(count)));
        var offset = file.ToArray();
        offset[^(20 + 16 - 7)] = 0x7F;
        Assert.Throws<InvalidDataException>(() => NovExport.Read(new MemoryStream(offset)));
        // A version 1 index claiming more than its 256 entries.
        var v1 = NovFile.Write(Layout, version: 1);
        v1[12] = 0x01;
        v1[13] = 0x01;
        Assert.Throws<InvalidDataException>(() => NovExport.Read(new MemoryStream(v1)));
    }

    private static string Save(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"filee-nov-{Guid.NewGuid():N}.nov");
        File.WriteAllBytes(path, data);
        return path;
    }
}
