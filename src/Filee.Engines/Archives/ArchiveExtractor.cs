// Unpacks every archive format Filee reads into a folder: the bundled 7-Zip for most of them, Filee's own readers for
// ALZ, EGG and lzip. Compressed TARs (.tar.gz, .tar.lz …) and RPM payloads are unpacked stage by stage until the
// files appear. Every stage is checked first (encryption, paths leaving the folder, disk space) and links are removed.
// A single compressed stream (.gz, .bz2 …) is written by Filee as 7-Zip decodes it, so it can't fill the drive. The
// archive's Mark of the Web goes onto the unpacked files.

using Filee.Core.Formats;
using Filee.Core.Platform;

namespace Filee.Engines.Archives;

/// <summary>Unpacks archives into scratch folders.</summary>
/// <param name="streamLimit">
/// The most a single compressed stream may write into a folder; <see cref="ArchiveSafety.StreamLimit"/> unless a test
/// sets a small one.
/// </param>
internal sealed class ArchiveExtractor(SevenZip? sevenZip, Func<string, long>? streamLimit = null)
{
    /// <summary>Formats read by 7-Zip.</summary>
    public static readonly IReadOnlyList<string> SevenZipFormats =
    [
        "zip", "jar", "zxp", "nupkg", "apks", "aab", "7z", "rar", "tar", "tgz", "tbz2", "txz", "tar7z", "tz", "gz", "bz2", "xz", "lzma", "z",
        "cab", "cpio", "deb", "rpm", "dmg", "iso", "img", "lha", "arj", "split",
        "mcpack", "mcaddon", "mcworld", "mctemplate", "mrpack",
    ];

    /// <summary>Formats read by Filee's own code (lzip still needs 7-Zip's LZMA decoder).</summary>
    public static readonly IReadOnlyList<string> BuiltInFormats = ["alz", "egg", "lz", "tlz"];

    /// <summary>Everything that can be unpacked.</summary>
    public static IEnumerable<string> ReadableFormats => SevenZipFormats.Concat(BuiltInFormats);

    /// <summary>A TAR inside a compressed stream (or a 7z): unpacking the outer layer yields one .tar file.</summary>
    private static readonly HashSet<string> TarWrapped = ["tgz", "tbz2", "txz", "tar7z", "tz", "tlz"];

    /// <summary>
    /// One compressed stream, no names or sizes to check beforehand (bzip2 states no size, gzip one modulo 4 GiB that
    /// its author chose).
    /// </summary>
    private static readonly HashSet<string> SingleStream = ["gz", "bz2", "xz", "lzma", "z", "zst", "tgz", "tbz2", "txz", "tz"];

    /// <summary>Formats an RPM payload comes in (7-Zip shows it as one compressed CPIO file).</summary>
    private static readonly HashSet<string> RpmPayload = ["cpio", "gz", "bz2", "xz", "lzma", "z", "zst"];

    private const int MaxStages = 3;

    /// <summary>
    /// Unpacks <paramref name="archive"/> (of format <paramref name="format"/>) into a new folder below
    /// <paramref name="scratchRoot"/> and returns that folder.
    /// </summary>
    public async Task<string> ExtractAsync(string archive, string format, string scratchRoot, CancellationToken cancellationToken)
    {
        var current = archive;
        var currentFormat = format;
        string? previous = null;
        for (var stage = 0; ; stage++)
        {
            var folder = Path.Combine(scratchRoot, "x" + stage);
            Directory.CreateDirectory(folder);
            await ExtractOnceAsync(current, currentFormat, folder, scratchRoot, cancellationToken);
            ArchiveSafety.RemoveLinks(folder);
            if (previous is not null)
                ArchiveFiles.TryDeleteFolder(previous);

            if (stage + 1 < MaxStages && InnerArchive(format, folder) is { } inner)
            {
                previous = folder;
                (current, currentFormat) = inner;
                continue;
            }
            return folder;
        }
    }

    private async Task ExtractOnceAsync(string archive, string format, string folder, string scratchRoot, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        switch (format)
        {
            case "alz":
                await Task.Run(() => AlzReader.Extract(archive, folder, cancellationToken), cancellationToken);
                PassOnMark(archive, folder);
                break;
            case "egg":
                await EggReader.ExtractAsync(archive, folder, sevenZip, scratchRoot, cancellationToken);
                PassOnMark(archive, folder);
                break;
            case "lz":
            case "tlz":
                var output = FormatRegistry.NameWithoutExtension(archive) + (format == "tlz" ? ".tar" : "");
                await LzipReader.DecompressAsync(archive, Path.Combine(folder, output), Require(), scratchRoot, cancellationToken);
                PassOnMark(archive, folder);
                break;
            default:
                var tool = Require();
                var entries = await tool.ListAsync(archive, cancellationToken);
                ArchiveSafety.CheckListing(entries, name, folder);
                // 7-Zip lists no name for some (.bz2, .xz), and never more than one file.
                if (SingleStream.Contains(format) && entries.Count(e => !e.IsFolder) <= 1)
                {
                    await DecodeStreamAsync(tool, archive, format, entries.FirstOrDefault(e => !e.IsFolder), folder, cancellationToken);
                    PassOnMark(archive, folder);
                    break;
                }
                var links = entries.Where(e => e.IsSymbolicLink).Select(e => e.Path).ToList();
                await tool.ExtractAsync(archive, folder, links, scratchRoot, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Unpacks a single compressed stream into <paramref name="folder"/> under the name 7-Zip gives it (the one stored
    /// in a .gz, else the archive's name without its extension), stopping at <see cref="ArchiveSafety.StreamLimit"/>.
    /// </summary>
    private async Task DecodeStreamAsync(SevenZip tool, string archive, string format, SevenZipEntry? entry,
        string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        var entryName = entry is { Path.Length: > 0 }
            ? entry.Path
            : FormatRegistry.NameWithoutExtension(archive) + (TarWrapped.Contains(format) ? ".tar" : "");
        var path = ArchiveSafety.ResolveEntryPath(folder, entryName, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var limit = (streamLimit ?? ArchiveSafety.StreamLimit)(folder);
        var tooLarge = ArchiveSafety.TooLarge(name, limit, folder); // with the free space before writing
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            await tool.DecodeAsync(archive, output, limit, () => tooLarge, cancellationToken);
        }
        catch
        {
            ArchiveFiles.TryDeleteFile(path);
            throw;
        }
        File.SetLastWriteTimeUtc(path, entry?.Modified ?? File.GetLastWriteTimeUtc(archive));
    }

    /// <summary>
    /// Gives the files Filee's own code unpacked into <paramref name="folder"/> the archive's Mark of the Web, if it has
    /// one (7-Zip does this itself for the files it writes).
    /// </summary>
    private static void PassOnMark(string archive, string folder) =>
        MarkOfTheWeb.Copy(archive, Directory.EnumerateFiles(folder, "*",
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }));

    /// <summary>The single archive file a stage produced that must be unpacked as well, or null when done.</summary>
    private static (string Path, string Format)? InnerArchive(string originalFormat, string folder)
    {
        if (!TarWrapped.Contains(originalFormat) && originalFormat != "rpm")
            return null;
        var entries = Directory.GetFileSystemEntries(folder);
        if (entries.Length != 1 || !File.Exists(entries[0]))
            return null;

        var file = entries[0];
        if (IsTar(file))
            return (file, "tar");
        if (originalFormat == "rpm")
        {
            var detected = FormatRegistry.Detect(file)?.Id ?? Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            if (RpmPayload.Contains(detected))
                return (file, detected);
        }
        return null;
    }

    /// <summary>True for a .tar file or a file with the "ustar" signature at offset 257.</summary>
    internal static bool IsTar(string path)
    {
        if (path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            return true;
        Span<byte> header = stackalloc byte[262];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
               && header[257..262].SequenceEqual("ustar"u8);
    }

    private SevenZip Require() => sevenZip ?? throw new InvalidOperationException("7-Zip was not found (engines/7zip).");
}
