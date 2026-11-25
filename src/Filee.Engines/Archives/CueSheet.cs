// BIN/CUE disc images (.cue + .bin) → ISO 9660: parses single-track cue sheets and streams the 2048-byte user data
// of each sector verbatim into an ISO without re-mastering. Multi-track discs (e.g. PS1 with audio tracks), Mode 2
// Form 2 sectors and non-ISO payloads are refused.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Platform;

namespace Filee.Engines.Archives;

/// <summary>Sector geometry for a supported CD/DVD data track mode.</summary>
/// <param name="SectorSize">Bytes per raw sector in the BIN file.</param>
/// <param name="DataOffset">Byte offset of the 2048-byte user data inside each sector.</param>
/// <param name="SubheaderOffset">Byte offset of the 8-byte Mode 2 XA subheader, or -1 for Mode 1.</param>
internal readonly record struct CueSectorLayout(int SectorSize, int DataOffset, int SubheaderOffset)
{
    /// <summary>True for Mode 2 tracks, whose subheader submode bit 0x20 marks Form 2 sectors.</summary>
    public bool IsMode2 => SubheaderOffset >= 0;
}

/// <summary>Parsed single-track BIN/CUE reference ready to be streamed to an ISO.</summary>
/// <param name="BinPath">Resolved path to the companion BIN file.</param>
/// <param name="Layout">Raw sector size and user-data offset for the track's mode.</param>
/// <param name="StartSector">Starting sector from <c>INDEX 01 mm:ss:ff</c> (75 frames per second).</param>
internal sealed record CueTrack(string BinPath, CueSectorLayout Layout, long StartSector);

/// <summary>Parses single-track CUE sheets and streams their data track into an ISO 9660 image.</summary>
internal static class CueSheet
{
    private const int UserDataSize = 2048;
    private const int SectorsPerChunk = 256;
    private const long MaxCueBytes = 1024 * 1024;
    private const long IsoDescriptorSector = 16;
    private const byte Mode2Form2Flag = 0x20;

    /// <summary>Reads <paramref name="cuePath"/> and resolves its single data track and companion BIN file.</summary>
    public static CueTrack ParseFile(string cuePath)
    {
        var fullCuePath = Path.GetFullPath(cuePath);
        if (new FileInfo(fullCuePath).Length > MaxCueBytes)
            throw Refused("the cue sheet is too large");
        var cueDirectory = Path.GetDirectoryName(fullCuePath) ?? ".";
        using var reader = new StringReader(DecodeCue(File.ReadAllBytes(fullCuePath)));
        return Parse(reader, cueDirectory);
    }

    /// <summary>
    /// The cue sheet's text: UTF-8 (with or without a byte order mark) when its bytes are valid UTF-8, otherwise the
    /// system's ANSI code page, which older ripping tools wrote names in (Windows-1252, Shift-JIS, CP949, …).
    /// </summary>
    internal static string DecodeCue(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return LegacyEncoding().GetString(bytes);
        }
    }

    /// <summary>The system's ANSI code page, or Latin-1 where there is none (or it is UTF-8).</summary>
    internal static Encoding LegacyEncoding()
    {
        var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        if (codePage is <= 0 or 65001)
            return Encoding.Latin1;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Parses a cue sheet from <paramref name="reader"/> and resolves the BIN path relative to <paramref name="cueDirectory"/>.</summary>
    internal static CueTrack Parse(TextReader reader, string cueDirectory)
    {
        string? currentFile = null;
        string? currentFileType = null;
        var tracks = new List<RawTrack>();
        var firstLine = true;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (firstLine)
            {
                firstLine = false;
                if (line.Length > 0 && line[0] == '\uFEFF')
                    line = line[1..];
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var keyword = FirstToken(trimmed);
            if (keyword.Equals("FILE", StringComparison.OrdinalIgnoreCase))
            {
                (currentFile, currentFileType) = ParseFileLine(trimmed);
            }
            else if (keyword.Equals("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                if (currentFile is null || currentFileType is null)
                    throw Refused("the cue sheet lists a track before any FILE entry");
                var (number, mode) = ParseTrackLine(trimmed);
                tracks.Add(new RawTrack(number, mode, currentFile, currentFileType));
            }
            else if (keyword.Equals("INDEX", StringComparison.OrdinalIgnoreCase))
            {
                if (tracks.Count == 0)
                    throw Refused("the cue sheet has an INDEX before any TRACK");
                if (TryParseIndex01(trimmed, out var sector))
                    tracks[^1].Index01Sector = sector;
            }
        }

        if (tracks.Count == 0)
            throw Refused("the cue sheet has no tracks");
        if (tracks.Count > 1)
            throw Refused($"the disc has {tracks.Count} tracks; only a single data track fits in an ISO");

        var track = tracks[0];
        if (!track.FileType.Equals("BINARY", StringComparison.OrdinalIgnoreCase))
            throw Refused($"unsupported file type {track.FileType}; only BINARY is supported");

        var layout = ParseMode(track.Mode);
        if (track.Index01Sector is not { } startSector)
            throw Refused("the track has no INDEX 01");

        var binPath = ResolveBinPath(cueDirectory, track.FileName);
        return new CueTrack(binPath, layout, startSector);
    }

    /// <summary>Converts a BIN/CUE disc image to an ISO 9660 file at the path allocated by <paramref name="step"/>.</summary>
    public static IReadOnlyList<string> ConvertToIso(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0);

        var track = ParseFile(step.InputPath);
        var layout = track.Layout;

        using var source = new FileStream(track.BinPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var binLength = source.Length;
        if (binLength == 0 || binLength % layout.SectorSize != 0)
            throw Refused("the BIN file length is not a whole number of sectors (file may be damaged)");

        var totalSectors = binLength / layout.SectorSize;
        if (track.StartSector >= totalSectors)
            throw Refused("INDEX 01 points past the end of the BIN file");

        var sectorsToCopy = totalSectors - track.StartSector;
        // Checked first, so a track that isn't ISO 9660 is refused without copying up to 700 MB.
        VerifyIso9660(source, layout, track.StartSector, sectorsToCopy);
        var output = step.Output.Allocate("iso");
        if (output is null)
            return [];

        source.Position = track.StartSector * layout.SectorSize;
        // Written beside the output under a temporary name and moved over it only when complete and checked, so a
        // refusal or cancellation leaves an image already at that path (the "overwrite" policy) as it was, like the
        // folder → ISO writer does.
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,
            $".{Path.GetFileName(output)}.{Guid.NewGuid().ToString("N")[..8]}.filee-tmp");
        var succeeded = false;
        try
        {
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                StreamUserData(source, destination, layout, sectorsToCopy, progress, cancellationToken);
            }
            File.Move(temporary, output, overwrite: true);
            if (MarkOfTheWeb.Read(track.BinPath) is { } mark)
                MarkOfTheWeb.TryWrite(output, mark);
            succeeded = true;
        }
        finally
        {
            if (!succeeded)
                ArchiveFiles.TryDeleteFile(temporary);
        }

        progress?.Report(1);
        return [output];
    }

    private static void StreamUserData(FileStream source, FileStream destination, CueSectorLayout layout,
        long sectorsToCopy, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var rawCopy = layout.SectorSize == UserDataSize && layout.DataOffset == 0 && !layout.IsMode2;
        var inBuffer = new byte[SectorsPerChunk * layout.SectorSize];
        var outBuffer = rawCopy ? inBuffer : new byte[SectorsPerChunk * UserDataSize];
        long written = 0;
        var lastReport = 0L;

        while (written < sectorsToCopy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(SectorsPerChunk, sectorsToCopy - written);
            var bytesToRead = count * layout.SectorSize;
            var read = source.ReadAtLeast(inBuffer.AsSpan(0, bytesToRead), bytesToRead, throwOnEndOfStream: false);
            if (read != bytesToRead)
                throw Refused("the BIN file is truncated or damaged");

            if (!rawCopy)
            {
                for (var i = 0; i < count; i++)
                {
                    var sectorStart = i * layout.SectorSize;
                    if (layout.IsMode2 && (inBuffer[sectorStart + layout.SubheaderOffset + 2] & Mode2Form2Flag) != 0)
                        throw Refused("the disc has Mode 2 Form 2 sectors (video or XA audio)");
                    inBuffer.AsSpan(sectorStart + layout.DataOffset, UserDataSize).CopyTo(outBuffer.AsSpan(i * UserDataSize, UserDataSize));
                }
            }

            destination.Write(outBuffer, 0, count * UserDataSize);
            written += count;

            var now = Stopwatch.GetTimestamp();
            if (written == sectorsToCopy || Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds >= 100)
            {
                lastReport = now;
                progress?.Report(Math.Min(0.99, (double)written / sectorsToCopy));
            }
        }
    }

    /// <summary>
    /// Refuses a track whose sector 16 (the first volume descriptor) lacks the "CD001" signature, read straight from
    /// the BIN file before anything is copied.
    /// </summary>
    private static void VerifyIso9660(FileStream source, CueSectorLayout layout, long startSector, long sectors)
    {
        Span<byte> signature = stackalloc byte[5];
        if (sectors <= IsoDescriptorSector)
            throw Refused("the data track isn't an ISO 9660 file system");
        source.Position = (startSector + IsoDescriptorSector) * layout.SectorSize + layout.DataOffset + 1;
        if (source.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) < signature.Length
            || !signature.SequenceEqual("CD001"u8))
            throw Refused("the data track isn't an ISO 9660 file system");
    }

    private static (string FileName, string FileType) ParseFileLine(string line)
    {
        var rest = line[4..].Trim();
        if (rest.Length == 0)
            throw Refused("invalid FILE line in cue sheet");

        string fileName;
        string fileType;
        if (rest[0] == '"')
        {
            var endQuote = rest.IndexOf('"', 1);
            if (endQuote < 0)
                throw Refused("unterminated quote in FILE line");
            fileName = rest[1..endQuote];
            fileType = rest[(endQuote + 1)..].Trim();
        }
        else
        {
            var lastSpace = rest.LastIndexOfAny([' ', '\t']);
            if (lastSpace <= 0)
                throw Refused("invalid FILE line in cue sheet");
            fileName = rest[..lastSpace].Trim();
            fileType = rest[(lastSpace + 1)..].Trim();
        }

        if (fileName.Length == 0 || fileType.Length == 0)
            throw Refused("invalid FILE line in cue sheet");
        return (fileName, fileType);
    }

    private static (int Number, string Mode) ParseTrackLine(string line)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 99)
            throw Refused("invalid TRACK line in cue sheet");
        return (number, parts[2]);
    }

    private static bool TryParseIndex01(string line, out long sector)
    {
        sector = 0;
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            return false;
        if (index != 1)
            return false;

        var time = parts[2].Split(':');
        if (time.Length != 3
            || !int.TryParse(time[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(time[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(time[2], NumberStyles.None, CultureInfo.InvariantCulture, out var frames)
            || seconds >= 60 || frames >= 75)
            throw Refused($"invalid INDEX 01 time \"{parts[2]}\"");

        sector = (long)minutes * 60 * 75 + seconds * 75 + frames;
        return true;
    }

    private static CueSectorLayout ParseMode(string mode) =>
        mode.ToUpperInvariant() switch
        {
            "MODE1/2048" => new(2048, 0, -1),
            "MODE1/2352" => new(2352, 16, -1),
            "MODE2/2352" => new(2352, 24, 16),
            "MODE2/2336" => new(2336, 8, 0),
            _ => throw Refused($"unsupported track mode {mode}; only a single data track fits in an ISO"),
        };

    /// <summary>
    /// The BIN file the cue names, always inside the cue's folder. A relative path is followed only while it stays in
    /// that folder. An absolute path (a drive, a root, a UNC share such as <c>\\host\share</c>) or one that climbs out
    /// with <c>..</c> is never opened or even checked: a downloaded cue could otherwise read any file on the PC, or
    /// make Windows connect to another computer and send it the user's credentials. For those, and for old absolute
    /// paths from the ripping PC, the file name alone is looked for in the cue's folder.
    /// </summary>
    internal static string ResolveBinPath(string cueDirectory, string fileName)
    {
        var folder = Path.GetFullPath(cueDirectory);
        if (!LooksRooted(fileName) && InFolder(folder, fileName) is { } candidate && File.Exists(candidate))
            return candidate;

        // After the last separator or drive colon ("C:disc.bin" names disc.bin).
        var slash = fileName.LastIndexOfAny(['/', '\\', ':']);
        var nameOnly = slash >= 0 ? fileName[(slash + 1)..] : fileName;
        if (nameOnly is { Length: > 0 } and not ("." or "..") && !LooksRooted(nameOnly)
            && InFolder(folder, nameOnly) is { } local && File.Exists(local))
            return local;

        throw Refused($"the BIN file \"{fileName}\" is missing");
    }

    /// <summary>Rooted on any system: <c>/x</c>, <c>\x</c>, <c>\\host\share</c>, or a drive (<c>C:\x</c>, <c>C:x</c>).</summary>
    private static bool LooksRooted(string path) =>
        path.StartsWith('/') || path.StartsWith('\\') || path.Length >= 2 && path[1] == ':' || Path.IsPathRooted(path);

    /// <summary>The full path of <paramref name="relative"/> under <paramref name="folder"/>, or null when it leaves it.</summary>
    private static string? InFolder(string folder, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        var back = Path.GetRelativePath(folder, full);
        return back == "." || back == ".." || Path.IsPathRooted(back)
               || back.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || back.StartsWith("../", StringComparison.Ordinal)
            ? null
            : full;
    }

    private static ReadOnlySpan<char> FirstToken(string line)
    {
        var end = line.AsSpan().IndexOfAny(' ', '\t');
        return end < 0 ? line.AsSpan() : line.AsSpan(0, end);
    }

    private static ConversionRefusedException Refused(string reason) => new("error.unsupported_source", reason);

    private sealed class RawTrack(int number, string mode, string fileName, string fileType)
    {
        public int Number { get; } = number;
        public string Mode { get; } = mode;
        public string FileName { get; } = fileName;
        public string FileType { get; } = fileType;
        public long? Index01Sector { get; set; }
    }
}
