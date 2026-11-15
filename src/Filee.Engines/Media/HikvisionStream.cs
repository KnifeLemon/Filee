// Hikvision cameras and NVRs record MPEG program streams. Exports from iVMS-4200 and the NVR (.mp4) start with a 40-byte
// "IMKH" header, which tells FFmpeg's mpegps demuxer the stream is Hikvision's: its G.711 mu-law sound (stream type
// 0x91 in the stream map) is then read as such. The recordings on a camera's SD card or a NAS (hiv00000.mp4) are the
// same stream without the header, so FFmpeg takes that sound for MP2 and the conversion fails. Filee puts the header in
// front of the file for FFmpeg with the concat protocol, so the recording is not copied.

using Microsoft.Win32.SafeHandles;

namespace Filee.Engines.Media;

internal static class HikvisionStream
{
    /// <summary>Bytes looked at for the stream map; cameras write one with every key frame, well before this.</summary>
    internal const int ScanBytes = 256 * 1024;

    /// <summary>Stream type Hikvision gives G.711 mu-law sound in the program stream map.</summary>
    private const byte MuLaw = 0x91;

    /// <summary>
    /// The input to give FFmpeg for <paramref name="path"/>: the path itself, or for a Hikvision recording without its
    /// IMKH header, a concat of a header written to <paramref name="workDirectory"/> and the file.
    /// </summary>
    public static string InputFor(string path, string workDirectory)
    {
        // The concat protocol separates its parts with '|' (not allowed in Windows names, rare elsewhere).
        if (path.Contains('|', StringComparison.Ordinal) || workDirectory.Contains('|', StringComparison.Ordinal))
            return path;
        try
        {
            using (var file = File.OpenHandle(path))
            {
                if (!NeedsHeader(file))
                    return path;
            }
            return $"concat:{WriteHeader(workDirectory)}|{path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path; // FFmpeg reports what is wrong with the file.
        }
    }

    /// <summary>
    /// Writes the header to <paramref name="folder"/>, unless it is there already (the shared folder of the Cut a clip
    /// editor). It is written under another name first, so a reader never sees half of it.
    /// </summary>
    private static string WriteHeader(string folder)
    {
        Directory.CreateDirectory(folder);
        var header = Path.Combine(folder, "hikvision-imkh.bin");
        var bytes = Header();
        if (File.Exists(header) && new FileInfo(header).Length == bytes.Length && File.ReadAllBytes(header).AsSpan().SequenceEqual(bytes))
            return header;
        var staged = header + "." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(staged, bytes);
        File.Move(staged, header, overwrite: true);
        return header;
    }

    /// <summary>The 40-byte header of Hikvision's exports; FFmpeg only looks at its "IMKH" magic.</summary>
    internal static byte[] Header()
    {
        var header = new byte[40];
        "IMKH"u8.CopyTo(header);
        return header;
    }

    /// <summary>
    /// True for a program stream that starts with a pack header (no IMKH header before it) and whose stream map holds
    /// mu-law sound. False for anything else, including damaged data: FFmpeg then reads the file as it is. Only the first
    /// four bytes are read of a file that doesn't start with a pack header (an MP4, a Matroska file, ...).
    /// </summary>
    public static bool NeedsHeader(SafeFileHandle file)
    {
        Span<byte> start = stackalloc byte[4];
        if (RandomAccess.Read(file, start, 0) < 4 || !start.SequenceEqual(PackStart))
            return false;
        var buffer = new byte[(int)Math.Min(ScanBytes, RandomAccess.GetLength(file))];
        var read = 0;
        while (read < buffer.Length && RandomAccess.Read(file, buffer.AsSpan(read), read) is > 0 and var got)
            read += got;
        return NeedsHeader(buffer.AsSpan(0, read));
    }

    /// <summary>The same as <see cref="NeedsHeader(SafeFileHandle)"/> for the start of a file already read.</summary>
    public static bool NeedsHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length > ScanBytes)
            data = data[..ScanBytes];
        if (data.Length < 4 || !data[..4].SequenceEqual(PackStart))
            return false;
        for (var at = data.IndexOf(MapStart); at >= 0;)
        {
            if (MapHasMuLaw(data[at..]) is { } found)
                return found;
            var next = data[(at + 1)..].IndexOf(MapStart);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return false;
    }

    private static ReadOnlySpan<byte> PackStart => [0, 0, 1, 0xBA];

    private static ReadOnlySpan<byte> MapStart => [0, 0, 1, 0xBC];

    /// <summary>
    /// Whether the program stream map at the start of <paramref name="map"/> lists mu-law sound; null when it is cut
    /// short or its lengths don't add up (start code emulation in a payload, or a damaged map), so the next one is tried.
    /// </summary>
    private static bool? MapHasMuLaw(ReadOnlySpan<byte> map)
    {
        // start code (4), length (2), flags (2), program_stream_info_length (2), info, elementary_stream_map_length (2),
        // entries: stream_type (1), elementary_stream_id (1), elementary_stream_info_length (2), info. CRC32 at the end.
        if (map.Length < 6)
            return null;
        var length = map[4] << 8 | map[5];
        if (length < 10 || 6 + length > map.Length)
            return null;
        var body = map.Slice(6, length - 4);
        var infoLength = body[2] << 8 | body[3];
        if (4 + infoLength + 2 > body.Length)
            return null;
        var entries = body[(4 + infoLength)..];
        var entriesLength = entries[0] << 8 | entries[1];
        if (2 + entriesLength > entries.Length)
            return null;
        entries = entries.Slice(2, entriesLength);
        var found = false;
        while (entries.Length > 0)
        {
            if (entries.Length < 4)
                return null;
            var entryInfo = entries[2] << 8 | entries[3];
            if (4 + entryInfo > entries.Length)
                return null;
            found |= entries[0] == MuLaw;
            entries = entries[(4 + entryInfo)..];
        }
        return found;
    }
}
