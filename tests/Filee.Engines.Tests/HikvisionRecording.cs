// Hikvision's recordings, as far as FFmpeg's mpegps demuxer reads them: an MPEG-2 program stream with a stream map
// (H.264 as type 0x1B, G.711 A-law as 0x90, mu-law as 0x91) before every key frame. iVMS-4200 and NVR exports start
// with a 40-byte "IMKH" header; the recordings on a camera's SD card (hiv00000.mp4) don't. FFmpeg has no muxer for
// either, so the tests put them together from FFmpeg-made H.264 frames and G.711 sound.

using System.Buffers.Binary;

namespace Filee.Engines.Tests;

internal static class HikvisionRecording
{
    /// <summary>
    /// One access unit (each starting with an access unit delimiter) per pack at 25 fps, each followed by 40 ms of
    /// 8 kHz mono G.711 sound.
    /// </summary>
    public static byte[] Write(byte[] annexB, byte[] g711, bool muLaw, bool imkhHeader)
    {
        var output = new MemoryStream();
        if (imkhHeader)
        {
            // Version 0x0101, system format 2 (PS), video 1 (H.264), audio 0x7111 / 0x7110 (G.711 u / A), 1 channel.
            var header = new byte[40];
            "IMKH"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0x0101);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), muLaw ? (ushort)0x7111 : (ushort)0x7110);
            header[12] = 1;
            output.Write(header);
        }
        var units = AccessUnits(annexB);
        for (var i = 0; i < units.Count; i++)
        {
            var pts = 90_000L + i * 3600;
            Pack(output, pts);
            if (IsKeyFrame(units[i]))
            {
                SystemHeader(output);
                StreamMap(output, muLaw ? (byte)0x91 : (byte)0x90);
            }
            for (var at = 0; at < units[i].Length; at += 60_000)
                Pes(output, 0xE0, units[i].AsSpan(at, Math.Min(60_000, units[i].Length - at)), at == 0 ? pts : null);
            var sound = g711.AsSpan().Slice(Math.Min(i * 320, g711.Length), Math.Clamp(g711.Length - i * 320, 0, 320));
            if (!sound.IsEmpty)
            {
                Pack(output, pts);
                Pes(output, 0xC0, sound, pts);
            }
        }
        return output.ToArray();
    }

    /// <summary>A stream map listing <paramref name="types"/> (stream type, stream id) as it is written in a pack.</summary>
    public static byte[] StreamMap(params (byte Type, byte Id)[] types)
    {
        var entries = types.SelectMany(t => new byte[] { t.Type, t.Id, 0, 0 }).ToArray();
        var body = new byte[] { 0xE0, 0xFF, 0, 0, (byte)(entries.Length >> 8), (byte)entries.Length }.Concat(entries).ToArray();
        var map = new byte[] { 0, 0, 1, 0xBC, (byte)((body.Length + 4) >> 8), (byte)(body.Length + 4) }.Concat(body).ToArray();
        // The CRC32 at the end is left at zero: FFmpeg doesn't check it.
        return [.. map, 0, 0, 0, 0];
    }

    /// <summary>An MPEG-2 pack header with its system clock at <paramref name="scr"/> (90 kHz).</summary>
    public static byte[] PackHeader(long scr) =>
    [
        0, 0, 1, 0xBA,
        (byte)(0x44 | (scr >> 27 & 0x38) | (scr >> 28 & 0x03)), (byte)(scr >> 20),
        (byte)((scr >> 12 & 0xF8) | 0x04 | (scr >> 13 & 0x03)), (byte)(scr >> 5), (byte)((scr << 3 & 0xF8) | 0x04), 0x01,
        // Mux rate 6106 (x 50 bytes/s), no stuffing.
        0x00, 0x5F, 0x6B, 0xF8,
    ];

    private static void Pack(MemoryStream output, long scr) => output.Write(PackHeader(scr));

    private static void SystemHeader(MemoryStream output) =>
        output.Write([0, 0, 1, 0xBB, 0, 12, 0x80, 0x01, 0x05, 0x04, 0xE1, 0xFF, 0xE0, 0xE0, 0x80, 0xC0, 0xC0, 0x08]);

    private static void StreamMap(MemoryStream output, byte audioType) => output.Write(StreamMap((0x1B, 0xE0), (audioType, 0xC0)));

    private static void Pes(MemoryStream output, byte id, ReadOnlySpan<byte> payload, long? pts)
    {
        Span<byte> head = stackalloc byte[14];
        head[0] = 0;
        head[1] = 0;
        head[2] = 1;
        head[3] = id;
        var optional = pts is null ? 0 : 5;
        BinaryPrimitives.WriteUInt16BigEndian(head[4..], (ushort)(3 + optional + payload.Length));
        head[6] = 0x80;
        head[7] = pts is null ? (byte)0 : (byte)0x80;
        head[8] = (byte)optional;
        if (pts is { } t)
        {
            head[9] = (byte)(0x21 | (t >> 29 & 0x0E));
            head[10] = (byte)(t >> 22);
            head[11] = (byte)((t >> 14 & 0xFE) | 1);
            head[12] = (byte)(t >> 7);
            head[13] = (byte)((t << 1 & 0xFE) | 1);
        }
        output.Write(head[..(9 + optional)]);
        output.Write(payload);
    }

    private static List<byte[]> AccessUnits(byte[] annexB)
    {
        byte[] delimiter = [0, 0, 0, 1, 9];
        var starts = new List<int>();
        for (var at = annexB.AsSpan().IndexOf(delimiter); at >= 0;)
        {
            starts.Add(at);
            var next = annexB.AsSpan(at + 1).IndexOf(delimiter);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return starts.Select((start, i) => annexB[start..(i + 1 < starts.Count ? starts[i + 1] : annexB.Length)]).ToList();
    }

    /// <summary>An access unit with an IDR slice (NAL type 5).</summary>
    private static bool IsKeyFrame(byte[] unit)
    {
        for (var i = 0; i + 3 < unit.Length; i++)
        {
            if (unit[i] == 0 && unit[i + 1] == 0 && unit[i + 2] == 1 && (unit[i + 3] & 0x1F) == 5)
                return true;
        }
        return false;
    }
}
