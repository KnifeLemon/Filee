// DVD-Video disc images (.iso). FFmpeg's dvdvideo demuxer (libdvdnav / libdvdread, in Filee's Windows download) plays a
// title the way a player does: the film's video, every audio track and every subtitle track with their languages, its
// chapters and its real length, read where they lie in the image. Left alone it plays title 1, which on many discs is
// a trailer or a warning, so Filee picks the title itself: the longest one, from the disc's IFO files.
// Filee reads those from the image's ISO 9660 file system (every DVD-Video disc carries one next to its UDF one, the
// "UDF bridge"). It also refuses what FFmpeg would turn into garbage or a cryptic error: a disc whose video is still
// CSS-scrambled (a straight copy of a store-bought disc; Filee doesn't break copy protection) and an image without a
// DVD-Video title (a data disc, which still unpacks as an archive).
// IFO layout: dvdread's ifo_types.h (VMGI_MAT, TT_SRPT, VTSI_MAT, VTS_PGCITI), see https://dvd.sourceforge.net/dvdinfo/.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Media;

internal static class DvdVideoIso
{
    /// <summary>The format id of disc images, which FFmpeg reads as DVD-Video.</summary>
    public const string Format = "iso";

    /// <summary>FFmpeg's demuxer for DVD-Video.</summary>
    public const string Demuxer = "dvdvideo";

    /// <summary>ISO 9660 logical block size; DVD-Video sectors are the same 2048 bytes.</summary>
    internal const int Sector = 2048;

    /// <summary>The primary volume descriptor's sector.</summary>
    private const int PrimaryVolumeDescriptor = 16;

    /// <summary>Largest directory or IFO file read; real ones are a few hundred KiB at most.</summary>
    private const int MaxTableBytes = 4 << 20;

    /// <summary>Packs looked at for CSS scrambling, spread over the first VOB of the title.</summary>
    private const int ScrambleSamples = 256;

    /// <summary>Titles shorter than this (logos, warnings, menus played as titles) aren't worth a notice when left out.</summary>
    private static readonly TimeSpan NoticeableTitle = TimeSpan.FromMinutes(1);

    /// <summary>A file of the disc's VIDEO_TS folder: its name and where its data lies in the image (bytes).</summary>
    internal sealed record DiscFile(string Name, long Start, long Length);

    /// <summary>
    /// A title of the disc (1-based, as FFmpeg's <c>-title</c> counts them), its title set, length and chapter count.
    /// </summary>
    internal sealed record Title(int Number, int TitleSet, TimeSpan Length, int Chapters = 0);

    /// <summary>An audio or subtitle track of a title, as the "Choose the DVD title" window lists it.</summary>
    /// <param name="Id">The audio track's number among the title's audio tracks, or the subtitle track's stream index.</param>
    internal sealed record Track(int Id, string? Language, string? Codec, int Channels = 0);

    /// <summary>The audio and subtitle tracks of a title, in the order FFmpeg reads them.</summary>
    internal sealed record Tracks(IReadOnlyList<Track> Audio, IReadOnlyList<Track> Subtitles);

    /// <summary>
    /// The input to give FFmpeg for the DVD-Video image at <paramref name="path"/> (see <see cref="MediaInput"/>): its
    /// longest title, read by the dvdvideo demuxer. Refuses an image without a DVD-Video title and one whose video is
    /// CSS-scrambled; a damaged image throws <see cref="InvalidDataException"/>. When the disc holds other titles of
    /// some length (bonus features, other cuts), <paramref name="notice"/> hears that they are left out.
    /// </summary>
    /// <param name="title">The title chosen in the "Choose the DVD title" window; null for the longest. Nothing is said
    /// about the titles left out then: the user saw them.</param>
    public static string InputFor(string path, Action<string>? notice = null, int? title = null)
    {
        Title main;
        bool others;
        using (var file = File.OpenRead(path))
        {
            var files = Read(file);
            var titles = Titles(file, files);
            if (titles.Count == 0)
                throw new ConversionRefusedException("error.iso_no_dvd_video");
            main = title is { } chosen
                ? titles.FirstOrDefault(t => t.Number == chosen) ?? throw new InvalidDataException($"This disc has no title {chosen}.")
                : Longest(titles);
            others = title is null && titles.Any(t => t != main && t.Length >= NoticeableTitle);
            var firstVob = files.FirstOrDefault(f => f.Name.Equals($"VTS_{main.TitleSet:00}_1.VOB", StringComparison.OrdinalIgnoreCase));
            if (firstVob is not null && IsScrambled(file, firstVob))
                throw new ConversionRefusedException("error.dvd_scrambled");
        }
        if (others)
            notice?.Invoke("notice.dvd_titles_left_out");
        return MediaInput.DvdVideo(path, main.Number);
    }

    /// <summary>The title converted when none is chosen: the longest, on a tie the first, as a player's "play" would.</summary>
    internal static Title Longest(IReadOnlyList<Title> titles) => titles.OrderByDescending(t => t.Length).ThenBy(t => t.Number).First();

    /// <summary>
    /// The titles of the DVD-Video image at <paramref name="path"/>, for the "Choose the DVD title" window: none for an
    /// image without a DVD-Video title or one that can't be read.
    /// </summary>
    public static IReadOnlyList<Title> TitlesOf(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return Titles(file, Read(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>The audio and subtitle tracks of title <paramref name="title"/>, as ffprobe reads them with the dvdvideo demuxer.</summary>
    public static async Task<Tracks> TracksAsync(string path, int title, CancellationToken cancellationToken)
    {
        var (_, ffprobe) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var info = await MediaInfo.ProbeAsync(ffprobe, MediaInput.DvdVideo(path, title), cancellationToken).ConfigureAwait(false);
        return new Tracks(
            [.. info.AudioTracks.Select(a => new Track(a.Ordinal, a.Language, a.Codec, a.Channels))],
            [.. info.Subtitles.Select(s => new Track(s.Index, s.Language, s.Codec))]);
    }

    /// <summary><paramref name="info"/> of a DVD title with only the audio and subtitle tracks of <paramref name="choice"/>.</summary>
    internal static MediaInfo Keep(MediaInfo info, DvdChoice choice)
    {
        var audio = info.AudioTracks.Where(a => choice.AudioTracks.Contains(a.Ordinal)).ToList();
        var subtitles = info.Subtitles.Where(s => choice.SubtitleTracks.Contains(s.Index)).ToList();
        return info with
        {
            Audio = audio.FirstOrDefault(),
            AudioStreams = audio.Count > 1 ? audio : null,
            CopyAudio = audio.FirstOrDefault(),
            SubtitleStreams = subtitles.Count > 0 ? subtitles : null,
        };
    }

    /// <summary>
    /// The files in the image's VIDEO_TS folder (none when it has no such folder); throws
    /// <see cref="InvalidDataException"/> when it is not an ISO 9660 image or its directories are damaged.
    /// </summary>
    public static IReadOnlyList<DiscFile> Read(Stream file)
    {
        var descriptor = new byte[Sector];
        ReadAt(file, (long)PrimaryVolumeDescriptor * Sector, descriptor);
        if (descriptor[0] != 1 || !descriptor.AsSpan(1, 5).SequenceEqual("CD001"u8))
            throw new InvalidDataException("This disc image has no ISO 9660 file system.");
        // The root directory record is at offset 156 of the primary volume descriptor.
        var root = Record(descriptor.AsSpan(156, 34));
        var videoTs = Directory(file, root.Extent, root.Size)
            .FirstOrDefault(e => e.IsDirectory && e.Name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase));
        if (videoTs is null)
            return [];
        var files = new List<DiscFile>();
        foreach (var entry in Directory(file, videoTs.Extent, videoTs.Size).Where(e => !e.IsDirectory))
        {
            var found = new DiscFile(entry.Name, (long)entry.Extent * Sector, entry.Size);
            if (found.Start + found.Length > file.Length)
                throw Damaged();
            files.Add(found);
        }
        return files;
    }

    /// <summary>
    /// The disc's titles with their lengths, from VIDEO_TS.IFO (the title table) and each title set's VTS_nn_0.IFO (the
    /// program chain of the title's first chapter, whose playing time the disc states). None without VIDEO_TS.IFO.
    /// </summary>
    internal static List<Title> Titles(Stream file, IReadOnlyList<DiscFile> files)
    {
        var vmg = Load(file, files, "VIDEO_TS.IFO");
        if (vmg is null || vmg.Length < 0x100 || !vmg.AsSpan(0, 12).SequenceEqual("DVDVIDEO-VMG"u8))
            return [];
        // VMGI_MAT 0xC4: sector of the title table (TT_SRPT): count, reserved, end, then 12-byte entries.
        var table = Table(vmg, BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(0xC4)), 8);
        if (table.IsEmpty)
            return [];
        var count = BinaryPrimitives.ReadUInt16BigEndian(table);
        var titles = new List<Title>(count);
        var titleSets = new Dictionary<int, byte[]?>();
        for (var i = 0; i < count && 8 + (i + 1) * 12 <= table.Length; i++)
        {
            var entry = table.Slice(8 + i * 12, 12);
            int set = entry[6], titleInSet = entry[7], chapters = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
            if (set is < 1 or > 99 || titleInSet < 1)
                continue;
            if (!titleSets.TryGetValue(set, out var vts))
                titleSets[set] = vts = Load(file, files, $"VTS_{set:00}_0.IFO");
            if (vts is not null && LengthOf(vts, titleInSet) is { } length)
                titles.Add(new Title(i + 1, set, length, chapters));
        }
        return titles;
    }

    /// <summary>The playing time of title <paramref name="titleInSet"/> of a title set, from its VTS_nn_0.IFO.</summary>
    private static TimeSpan? LengthOf(byte[] vts, int titleInSet)
    {
        if (vts.Length < 0x100 || !vts.AsSpan(0, 12).SequenceEqual("DVDVIDEO-VTS"u8))
            return null;
        // VTSI_MAT 0xC8: sector of the chapter table (VTS_PTT_SRPT), 0xCC: of the program chains (VTS_PGCITI).
        var chapters = Table(vts, BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(0xC8)), 8);
        var chains = Table(vts, BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(0xCC)), 8);
        if (chapters.IsEmpty || chains.IsEmpty || titleInSet > BinaryPrimitives.ReadUInt16BigEndian(chapters)
            || 8 + titleInSet * 4 > chapters.Length)
            return null;
        // The title's first chapter: its program chain number.
        var at = (int)BinaryPrimitives.ReadUInt32BigEndian(chapters[(8 + (titleInSet - 1) * 4)..]);
        if (at + 2 > chapters.Length)
            return null;
        var chain = BinaryPrimitives.ReadUInt16BigEndian(chapters[at..]);
        if (chain < 1 || chain > BinaryPrimitives.ReadUInt16BigEndian(chains) || 8 + chain * 8 > chains.Length)
            return null;
        var pgc = (int)BinaryPrimitives.ReadUInt32BigEndian(chains[(8 + (chain - 1) * 8 + 4)..]);
        // The program chain states its playing time at offset 4 (hours, minutes, seconds, frames in BCD).
        return pgc + 8 > chains.Length ? null : PlayingTime(chains.Slice(pgc + 4, 4));
    }

    /// <summary>A DVD playing time: BCD hours, minutes, seconds and frames, the frame rate in the top two bits.</summary>
    internal static TimeSpan PlayingTime(ReadOnlySpan<byte> time)
    {
        static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
        var fps = (time[3] >> 6) == 3 ? 30 : 25;
        return new TimeSpan(Bcd(time[0]), Bcd(time[1]), Bcd(time[2])) + TimeSpan.FromSeconds(Bcd((byte)(time[3] & 0x3F)) / (double)fps);
    }

    /// <summary>The table at <paramref name="sector"/> of an IFO file, empty when it lies outside the file.</summary>
    private static ReadOnlySpan<byte> Table(byte[] ifo, uint sector, int header)
    {
        var start = (long)sector * Sector;
        if (sector == 0 || start + header > ifo.Length)
            return [];
        // Each table states its last byte at offset 4, counted from its start.
        var end = Math.Min(ifo.Length, start + BinaryPrimitives.ReadUInt32BigEndian(ifo.AsSpan((int)start + 4)) + 1);
        return end < start + header ? [] : ifo.AsSpan((int)start, (int)(end - start));
    }

    /// <summary>The bytes of the VIDEO_TS file <paramref name="name"/>, null when the disc doesn't have it.</summary>
    private static byte[]? Load(Stream file, IReadOnlyList<DiscFile> files, string name)
    {
        var found = files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (found is null || found.Length > MaxTableBytes)
            return null;
        var data = new byte[found.Length];
        ReadAt(file, found.Start, data);
        return data;
    }

    /// <summary>
    /// True when the video or sound packets of <paramref name="vob"/> are CSS-scrambled: the PES scrambling control
    /// bits are set on them (a decrypted copy clears them).
    /// </summary>
    internal static bool IsScrambled(Stream file, DiscFile vob)
    {
        var sectors = vob.Length / Sector;
        if (sectors == 0)
            return false;
        var step = Math.Max(1, sectors / ScrambleSamples);
        var pack = new byte[Sector];
        int seen = 0, scrambled = 0;
        for (long i = 0; i < sectors && seen < ScrambleSamples; i += step)
        {
            ReadAt(file, vob.Start + i * Sector, pack);
            if (ScramblingOf(pack) is not { } bits)
                continue;
            seen++;
            if (bits != 0)
                scrambled++;
        }
        // CSS scrambles every A/V pack after the first few; half is a safe line either way.
        return seen > 0 && scrambled * 2 > seen;
    }

    /// <summary>
    /// The PES scrambling control bits of the first video, private (AC-3, DTS …) or MPEG audio packet in an MPEG-2
    /// pack, or null when the pack holds none (navigation packs, padding).
    /// </summary>
    internal static int? ScramblingOf(ReadOnlySpan<byte> pack)
    {
        // Pack header: 00 00 01 BA, 10 bytes of clock and rate, then stuffing (low 3 bits of byte 13).
        if (pack.Length < 14 || !pack[..4].SequenceEqual((ReadOnlySpan<byte>)[0, 0, 1, 0xBA]) || (pack[4] & 0xC0) != 0x40)
            return null;
        var at = 14 + (pack[13] & 7);
        while (at + 7 <= pack.Length && pack[at] == 0 && pack[at + 1] == 0 && pack[at + 2] == 1)
        {
            var id = pack[at + 3];
            var length = BinaryPrimitives.ReadUInt16BigEndian(pack[(at + 4)..]);
            if (id is 0xE0 or 0xBD or (>= 0xC0 and <= 0xC7))
                return (pack[at + 6] >> 4) & 3;
            at += 6 + length;
        }
        return null;
    }

    private sealed record Entry(string Name, uint Extent, uint Size, bool IsDirectory);

    /// <summary>The entries of the directory at sector <paramref name="extent"/>, <paramref name="size"/> bytes long.</summary>
    private static List<Entry> Directory(Stream file, uint extent, uint size)
    {
        if (size > MaxTableBytes || (long)extent * Sector + size > file.Length)
            throw Damaged();
        var data = new byte[size];
        ReadAt(file, (long)extent * Sector, data);
        var entries = new List<Entry>();
        var at = 0;
        while (at < data.Length)
        {
            var length = data[at];
            // Records never cross a sector: a zero length pads to the next one.
            if (length == 0)
            {
                at = (at / Sector + 1) * Sector;
                continue;
            }
            if (length < 34 || at + length > data.Length)
                throw Damaged();
            var entry = Record(data.AsSpan(at, length));
            // "\0" and "\1" are the directory itself and its parent.
            if (entry.Name is not ("\0" or "\u0001"))
                entries.Add(entry);
            at += length;
        }
        return entries;
    }

    /// <summary>One directory record: name (without the ";1" version), first sector, size and whether it is a folder.</summary>
    private static Entry Record(ReadOnlySpan<byte> record)
    {
        var nameLength = record[32];
        if (33 + nameLength > record.Length)
            throw Damaged();
        var name = Encoding.ASCII.GetString(record.Slice(33, nameLength));
        var version = name.IndexOf(';', StringComparison.Ordinal);
        if (version >= 0)
            name = name[..version];
        if (name.Length > 1)
            name = name.TrimEnd('.');
        return new Entry(name, BinaryPrimitives.ReadUInt32LittleEndian(record[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[10..]), (record[25] & 2) != 0);
    }

    private static void ReadAt(Stream file, long at, Span<byte> buffer)
    {
        file.Position = at;
        if (file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
            throw Damaged();
    }

    private static InvalidDataException Damaged() => new("This disc image is damaged: its directories don't match the file.");
}
