// Guards for unpacking archives from anywhere: entries that would land outside the target folder ("zip slip"),
// symbolic links, encrypted content, archives that would not fit on the disk and decompression bombs. Every reader
// (Filee's own, SharpCompress and the bundled 7-Zip) lists the archive first and goes through CheckListing; what is
// written while unpacking is counted by UnpackBudget. Also the error messages for them.

using System.Globalization;

namespace Filee.Engines.Archives;

/// <summary>Checks and path helpers shared by the 7-Zip and the built-in archive readers.</summary>
internal static class ArchiveSafety
{
    /// <summary>More entries than this are refused: real archives stay far below, "entry bombs" do not.</summary>
    public const int MaxEntries = 1_000_000;

    /// <summary>Space that must stay free on the drive after unpacking.</summary>
    private const long Reserve = 64L * 1024 * 1024;

    /// <summary>
    /// Archives that unpack to more than this and to more than <see cref="MaxRatio"/> times their own size are
    /// refused as decompression bombs. Below it any ratio is fine (a gigabyte of zeros is no threat).
    /// </summary>
    public const long RatioFloor = 1L << 30;

    /// <summary>
    /// Deflate tops out near 1:1032 and LZMA / XZ of zeros near 1:7000; only overlapping-entry ZIP bombs and bzip2 of
    /// long runs go beyond this.
    /// </summary>
    public const long MaxRatio = 10_000;

    private static readonly HashSet<char> InvalidNameChars =
        [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True if an entry path is absolute, starts with a drive, or climbs out of the target with "..".</summary>
    public static bool IsUnsafePath(string entryPath)
    {
        if (entryPath.Length == 0)
            return false;
        if (entryPath[0] is '/' or '\\')
            return true;
        if (entryPath.Length >= 2 && entryPath[1] == ':' && char.IsAsciiLetter(entryPath[0]))
            return true;
        return entryPath.Split('/', '\\').Any(part => part == "..");
    }

    /// <summary>
    /// Refuses a 7-Zip listing with encrypted entries, entries or hard links pointing outside the target folder,
    /// too many entries, more data than the drive of <paramref name="targetDirectory"/> can hold, or a bomb.
    /// </summary>
    public static void CheckListing(IReadOnlyList<SevenZipEntry> entries, string archiveName, long archiveLength, string targetDirectory) =>
        CheckListing([.. entries.Select(e => new ArchiveItem(e.Path, e.IsFolder, e.Size, e.IsEncrypted, e.IsSymbolicLink, e.HardLink))],
            archiveName, archiveLength, targetDirectory);

    /// <summary>
    /// The same checks for a listing made in-process. Symbolic links pass: they are left out, never followed.
    /// </summary>
    public static void CheckListing(IReadOnlyList<ArchiveItem> items, string archiveName, long archiveLength, string targetDirectory)
    {
        if (items.Count > EntryLimit)
            throw TooManyEntries(archiveName);
        long total = 0;
        foreach (var item in items)
        {
            if (item.IsEncrypted)
                throw Encrypted(archiveName);
            if (IsUnsafePath(item.Path) || item.HardLink is { Length: > 0 } link && IsUnsafePath(link))
                throw Unsafe(archiveName, item.Path);
            if (item.Size < 0)
                throw Damaged(archiveName, "an entry has a negative size");
            if (!item.IsFolder && !item.IsLink)
                total = total > long.MaxValue - item.Size ? long.MaxValue : total + item.Size;
        }
        CheckRatio(total, archiveLength, archiveName);
        EnsureRoom(total, targetDirectory, archiveName);
    }

    /// <summary>
    /// Lower bomb limits for the code that runs from here on (this async flow only), so tests see them work without
    /// writing gigabytes.
    /// </summary>
    internal static readonly AsyncLocal<(long Floor, long Ratio, int Entries)?> TestLimits = new();

    /// <summary>The entry count beyond which archives are refused: <see cref="MaxEntries"/>, lower in tests.</summary>
    public static int EntryLimit => TestLimits.Value?.Entries ?? MaxEntries;

    /// <summary>Throws when <paramref name="unpacked"/> bytes out of <paramref name="packed"/> look like a bomb.</summary>
    public static void CheckRatio(long unpacked, long packed, string archiveName)
    {
        var (floor, ratio, _) = TestLimits.Value ?? (RatioFloor, MaxRatio, MaxEntries);
        if (unpacked > floor && unpacked / Math.Max(packed, 1) > ratio)
            throw Bomb(archiveName, unpacked);
    }

    /// <summary>Throws when <paramref name="bytes"/> would not fit on the drive of <paramref name="directory"/>.</summary>
    public static void EnsureRoom(long bytes, string directory, string archiveName)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrEmpty(root) || bytes <= 0)
            return;
        long free;
        try
        {
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // network shares have no DriveInfo; the write itself fails if space runs out
        }
        if (bytes > free - Reserve)
            throw new IOException($"{archiveName} unpacks to {FormatSize(bytes)}, but only {FormatSize(free)} is free on {root}.");
    }

    /// <summary>
    /// Turns an entry name read by Filee's own readers into a path inside <paramref name="root"/>: separators are
    /// normalized, characters Windows does not allow are replaced, and anything escaping the root is refused.
    /// </summary>
    public static string ResolveEntryPath(string root, string entryName, string archiveName)
    {
        if (IsUnsafePath(entryName))
            throw Unsafe(archiveName, entryName);
        var parts = entryName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != ".")
            .Select(SanitizeSegment)
            .ToList();
        if (parts.Count == 0)
            throw Damaged(archiveName, "an entry has no name");

        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine([fullRoot, .. parts]));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Unsafe(archiveName, entryName);
        return full;
    }

    /// <summary>
    /// Deletes symbolic links and junctions below <paramref name="root"/> without following them, so nothing packed or
    /// moved later can reach outside the unpacked folder.
    /// </summary>
    public static void RemoveLinks(string root)
    {
        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (info is DirectoryInfo linkedDir)
                    linkedDir.Delete(recursive: false); // removes the link, not its target
                else
                    info.Delete();
            }
            else if (info is DirectoryInfo dir)
            {
                RemoveLinks(dir.FullName);
            }
        }
    }

    /// <summary>
    /// An archive Filee refuses on purpose (password, unsafe path, bomb, too many entries). Unlike damage or a
    /// feature an in-process reader lacks, it is final: the bundled 7-Zip is not asked to try again.
    /// </summary>
    private static InvalidDataException Refusal(string message)
    {
        var exception = new InvalidDataException(message);
        exception.Data[RefusalKey] = true;
        return exception;
    }

    private const string RefusalKey = "Filee.ArchiveRefusal";

    /// <summary>True for the exceptions of <see cref="Refusal"/>.</summary>
    public static bool IsRefusal(Exception exception) => exception.Data.Contains(RefusalKey);

    public static Exception Encrypted(string archiveName) =>
        Refusal($"{archiveName} is password-protected. Filee cannot unpack encrypted archives.");

    public static Exception Unsafe(string archiveName, string entry) =>
        Refusal($"{archiveName} was not unpacked: the entry \"{entry}\" points outside the target folder.");

    public static Exception TooManyEntries(string archiveName) =>
        Refusal($"{archiveName} has more than {EntryLimit:N0} entries; Filee does not unpack it.");

    public static Exception Bomb(string archiveName, long bytes) =>
        Refusal($"{archiveName} was not unpacked: it grows to more than {FormatSize(bytes)}, over "
                                    + $"{(TestLimits.Value?.Ratio ?? MaxRatio):N0} times its own size (a decompression bomb).");

    public static Exception NotAnArchive(string archiveName) =>
        new InvalidDataException($"{archiveName} is not a valid archive, or not the kind its extension says.");

    public static Exception Damaged(string archiveName, string detail) =>
        new InvalidDataException($"{archiveName} is damaged or incomplete ({detail}).");

    /// <summary>"1.4 GB" style size for messages.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} bytes"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>One path segment made valid on Windows: invalid characters become "_", device names get a prefix.</summary>
    private static string SanitizeSegment(string segment)
    {
        var chars = segment.Select(c => InvalidNameChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var name = new string(chars).TrimEnd('.', ' ');
        if (name.Length == 0)
            return "_";
        var stem = name.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + name : name;
    }
}

/// <summary>One entry of an archive listing, whoever made it.</summary>
/// <param name="Path">Path inside the archive.</param>
/// <param name="IsLink">A symbolic link, device or other entry that is neither file nor folder: never unpacked.</param>
/// <param name="HardLink">Target of a hard link entry, which becomes a copy of that earlier file.</param>
internal sealed record ArchiveItem(string Path, bool IsFolder, long Size, bool IsEncrypted = false, bool IsLink = false, string? HardLink = null);

/// <summary>
/// Counts what one extraction writes: stops a decompression bomb whose sizes are not known up front (a .gz, .xz or
/// .bz2 declares none it has to keep), and a drive that runs full meanwhile.
/// </summary>
internal sealed class UnpackBudget(string archiveName, long archiveLength, string targetDirectory)
{
    /// <summary>The free space is looked at again after every this many bytes.</summary>
    private const long SpaceCheckInterval = 256L * 1024 * 1024;

    private long _written;
    private long _nextSpaceCheck = SpaceCheckInterval;

    public string ArchiveName { get; } = archiveName;

    /// <summary>Bytes written so far.</summary>
    public long Written => _written;

    public void Add(long bytes)
    {
        _written += bytes;
        ArchiveSafety.CheckRatio(_written, archiveLength, ArchiveName);
        if (_written >= _nextSpaceCheck)
        {
            _nextSpaceCheck = _written + SpaceCheckInterval;
            ArchiveSafety.EnsureRoom(SpaceCheckInterval, targetDirectory, ArchiveName);
        }
    }
}
