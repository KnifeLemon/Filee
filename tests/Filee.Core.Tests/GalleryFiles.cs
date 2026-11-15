// Builds what the preset gallery tests check, in code: preset.json bundles (as Settings → General → Export writes
// them) and the first bytes of PNG, JPEG and WebP pictures of any size, still or animated.

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

internal static class GalleryFiles
{
    /// <summary>A preset.json with <paramref name="presets"/> and <paramref name="profile"/>, each as an export has it.</summary>
    public static byte[] Bundle(IEnumerable<Preset> presets, ToolbarProfile? profile = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new PresetBundle
        {
            SettingsSchema = AppSettings.CurrentSchemaVersion,
            Presets = [.. presets.Select(p => p.ForSharing())],
            Profiles = profile is null ? [] : [profile],
        }, FileeJsonContext.Default.PresetBundle);

    public static byte[] Bundle(params Preset[] presets) => Bundle(presets, null);

    /// <summary>A plain conversion a user might share: photos to a smaller JPG.</summary>
    public static Preset Photo(string id = "insta") => new()
    {
        Id = id,
        Name = "Instagram JPG",
        TargetFormat = "jpg",
        Image = { Quality = 85, Resize = ResizeMode.LongEdge, LongEdge = 1080, KeepMetadata = false },
        Output = { FileNamePattern = "{name}_insta" },
    };

    /// <summary><paramref name="json"/> as a node to change, and back.</summary>
    public static JsonObject Node(byte[] json) => JsonNode.Parse(json)!.AsObject();

    public static byte[] Bytes(JsonNode node) => JsonSerializer.SerializeToUtf8Bytes(node);

    /// <summary>The bundle of <paramref name="preset"/> with <paramref name="change"/> made to its first preset's JSON.</summary>
    public static byte[] With(Action<JsonObject> change, Preset? preset = null)
    {
        var root = Node(Bundle(preset ?? Photo()));
        change(root["presets"]![0]!.AsObject());
        return Bytes(root);
    }

    /// <summary>A PNG's signature, IHDR and (with <paramref name="animated"/>) an acTL chunk, then an IDAT.</summary>
    public static byte[] Png(int width, int height, bool animated = false)
    {
        var data = new List<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(data, "IHDR", ihdr);
        if (animated)
            Chunk(data, "acTL", new byte[8]);
        Chunk(data, "IDAT", new byte[16]);
        Chunk(data, "IEND", []);
        return [.. data];
    }

    private static void Chunk(List<byte> data, string type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        data.AddRange(length);
        data.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        data.AddRange(body);
        data.AddRange(new byte[4]); // CRC: the header check doesn't read it
    }

    /// <summary>A JPEG's start, an APP0 segment and a baseline frame header.</summary>
    public static byte[] Jpeg(int width, int height)
    {
        var data = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        data.AddRange("JFIF\0"u8.ToArray());
        data.AddRange(new byte[9]);
        data.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        data.AddRange(new byte[9]);
        data.AddRange([0xFF, 0xD9]);
        return [.. data];
    }

    /// <summary>A WebP: a VP8X header (with its animation flag) and a VP8L frame.</summary>
    public static byte[] WebP(int width, int height, bool animated = false, bool extended = true)
    {
        var chunks = new List<byte>();
        if (extended)
        {
            var vp8x = new byte[10];
            vp8x[0] = (byte)(animated ? 0x02 : 0);
            WriteUInt24(vp8x.AsSpan(4), width - 1);
            WriteUInt24(vp8x.AsSpan(7), height - 1);
            RiffChunk(chunks, "VP8X", vp8x);
        }
        var vp8l = new byte[10];
        vp8l[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(vp8l.AsSpan(1), (uint)((width - 1) | (height - 1) << 14));
        RiffChunk(chunks, animated ? "ANMF" : "VP8L", vp8l);
        var data = new List<byte>();
        data.AddRange("RIFF"u8.ToArray());
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(chunks.Count + 4));
        data.AddRange(size);
        data.AddRange("WEBP"u8.ToArray());
        data.AddRange(chunks);
        return [.. data];
    }

    private static void RiffChunk(List<byte> data, string type, byte[] body)
    {
        data.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)body.Length);
        data.AddRange(length);
        data.AddRange(body);
        if (body.Length % 2 == 1)
            data.Add(0);
    }

    private static void WriteUInt24(Span<byte> span, int value)
    {
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
    }
}
