// Unpacks every archive format Filee reads into a folder, in-process: Filee's own readers (7z, xz, LZMA, ALZ, EGG,
// lzip, Unix compress, ar / DEB, cpio, RPM, ISO 9660, CAB, LHA, ARJ, FAT disk images), SharpCompress (ZIP, RAR),
// DiscUtils (UDF discs, HFS+ volumes, DMG blocks; see DiskImageReader), .NET's TAR reader and gzip / bzip2 / zstd
// decoders. 7-Zip is an optional engine (Settings → Engines) for the long tail: when it is installed it reads 7z
// archives with a method Filee does not decode (PPMd, RISC-V …, see SevenZipReader.NeedsOtherDecoder) and is asked
// again when an in-process reader meets a feature it lacks (Quantum CABs, cabinet sets, other LHA methods,
// multi-volume ARJ; NTFS, APFS, exFAT or LZFSE in disk images) or damage it can't read past. Without it such an
// archive is refused with error.archive_needs_7zip, which suggests installing it; a bridge disc whose UDF part fails
// is read by its ISO 9660 part first. Deliberate refusals (password, paths leaving the folder, bombs) are final.
// Compressed TARs (.tar.gz, .tar.lz …) and RPM payloads are unpacked stage by stage until the files appear. Every
// stage is listed and checked first, and links are removed. A single compressed stream (.gz, .bz2 …) stops at the
// room on the drive (ArchiveSafety.StreamLimit), in-process or written by Filee as 7-Zip decodes it. The archive's
// Mark of the Web goes onto the unpacked files.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;

namespace Filee.Engines.Archives;

/// <summary>Unpacks archives into scratch folders.</summary>
/// <param name="streamLimit">
/// The most a single compressed stream may write into a folder; <see cref="ArchiveSafety.StreamLimit"/> unless a test
/// sets a small one.
/// </param>
/// <param name="sevenZip">The optional 7-Zip (<see cref="SevenZip.Locate"/>), or null when it isn't installed.</param>
internal sealed class ArchiveExtractor(SevenZip? sevenZip, Func<string, long>? streamLimit = null)
{
    /// <summary>
    /// The refusal of an archive only 7-Zip unpacks (a method or feature Filee's readers lack) while it isn't
    /// installed; the message suggests installing it in Settings → Engines.
    /// </summary>
    public const string NeedsSevenZip = "error.archive_needs_7zip";

    /// <summary>Formats Filee reads (all in-process; 7-Zip, when installed, only as a fallback).</summary>
    public static readonly IReadOnlyList<string> BuiltInFormats =
    [
        "zip", "jar", "zxp", "7z", "rar", "tar", "tgz", "tbz2", "txz", "tar7z", "tz", "tlz", "gz", "bz2", "xz", "lzma", "z",
        "lz", "cpio", "deb", "rpm", "iso", "alz", "egg", "split", "cab", "lha", "arj", "dmg", "img",
        "mcpack", "mcaddon", "mcworld", "mctemplate", "mrpack",
    ];

    /// <summary>Formats 7-Zip can read too, so it may try again after an in-process reader gave up.</summary>
    private static readonly HashSet<string> SevenZipAlsoReads =
    [
        "zip", "jar", "zxp", "7z", "rar", "tar", "tgz", "tbz2", "txz", "tar7z", "tz", "gz", "bz2", "xz", "lzma", "z", "cpio",
        "deb", "rpm", "iso", "split", "cab", "lha", "arj", "dmg", "img", "mcpack", "mcaddon", "mcworld", "mctemplate", "mrpack",
    ];

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
        if (sevenZip is not null && format is "7z" or "tar7z" && SevenZipReader.NeedsOtherDecoder(archive))
        {
            await WithSevenZipAsync(sevenZip, archive, format, folder, scratchRoot, cancellationToken);
            return;
        }
        try
        {
            var limit = (streamLimit ?? ArchiveSafety.StreamLimit)(folder);
            await Task.Run(() => ExtractInProcess(archive, format, folder, cancellationToken, limit), cancellationToken);
            PassOnMark(archive, folder);
        }
        catch (Exception ex) when (MayRetry(ex) && SevenZipAlsoReads.Contains(format) && sevenZip is not null)
        {
            ArchiveFiles.TryDeleteFolder(folder);
            Directory.CreateDirectory(folder);
            await WithSevenZipAsync(sevenZip, archive, format, folder, scratchRoot, cancellationToken);
        }
        catch (Exception ex) when (MayRetry(ex) && format == "iso" && IsoReader.HasUdf(archive))
        {
            // No 7-Zip to read the UDF part: a bridge disc may still have its files in the ISO 9660 part.
            ArchiveFiles.TryDeleteFolder(folder);
            Directory.CreateDirectory(folder);
            try
            {
                await Task.Run(() => IsoReader.Extract(archive, folder, cancellationToken), cancellationToken);
                PassOnMark(archive, folder);
            }
            catch (Exception other) when (MayRetry(other))
            {
                throw Explain(ex, Path.GetFileName(archive), sevenZipWouldTry: true);
            }
        }
        catch (Exception ex) when (MayRetry(ex))
        {
            throw Explain(ex, Path.GetFileName(archive), SevenZipAlsoReads.Contains(format));
        }
    }

    /// <summary>
    /// Unpacks one layer with the in-process readers; a single compressed stream stops past
    /// <paramref name="streamLimit"/> bytes (<see cref="ArchiveSafety.StreamLimit"/>).
    /// </summary>
    internal static void ExtractInProcess(string archive, string format, string folder, CancellationToken cancellationToken,
        long streamLimit = long.MaxValue)
    {
        switch (format)
        {
            case "alz":
                AlzReader.Extract(archive, folder, cancellationToken);
                break;
            case "egg":
                EggReader.Extract(archive, folder, cancellationToken);
                break;
            case "lz":
            case "tlz":
                LzipReader.Decompress(archive, Path.Combine(folder, CompressedFile.OutputName(archive, tar: format == "tlz")), cancellationToken);
                break;
            case "tar":
                TarArchiveReader.Extract(archive, folder, cancellationToken);
                break;
            case "cpio":
                CpioReader.Extract(archive, folder, cancellationToken);
                break;
            case "deb":
                ArReader.Extract(archive, folder, cancellationToken);
                break;
            case "rpm":
                RpmReader.Extract(archive, folder, cancellationToken);
                break;
            case "iso":
                if (IsoReader.HasUdf(archive))
                    DiskImageReader.ExtractUdf(archive, folder, cancellationToken);
                else
                    IsoReader.Extract(archive, folder, cancellationToken);
                break;
            case "dmg":
            case "img":
                DiskImageReader.Extract(archive, format, folder, cancellationToken);
                break;
            case "split":
                ExtractSplit(archive, folder, cancellationToken);
                break;
            case "cab":
                CabReader.Extract(archive, folder, cancellationToken);
                break;
            case "lha":
                LhaReader.Extract(archive, folder, cancellationToken);
                break;
            case "arj":
                ArjReader.Extract(archive, folder, cancellationToken);
                break;
            case "zip":
            case "jar":
            case "zxp":
            case "mcpack":
            case "mcaddon":
            case "mcworld":
            case "mctemplate":
            case "mrpack":
            case "7z":
            case "rar":
            case "tar7z":
                LibraryArchives.Extract(archive, folder, cancellationToken);
                break;
            default:
                if (!CompressedFile.Formats.Contains(format))
                    throw new NotSupportedException($"Filee has no built-in reader for {format}.");
                CompressedFile.Decompress(archive, format, Path.Combine(folder, CompressedFile.OutputName(archive, TarWrapped.Contains(format))), cancellationToken,
                    streamLimit);
                break;
        }
    }

    /// <summary>The volumes as one stream: a TAR goes to the TAR reader, 7z, ZIP and RAR to SharpCompress.</summary>
    private static void ExtractSplit(string archive, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        using var volumes = SplitVolumes.Open(archive);
        Span<byte> header = stackalloc byte[262];
        var read = volumes.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        volumes.Position = 0;
        if (read == header.Length && header[257..262].SequenceEqual("ustar"u8))
            TarArchiveReader.Extract(volumes, name, folder, cancellationToken);
        else
            LibraryArchives.Extract(volumes, name, folder, cancellationToken);
    }

    private async Task WithSevenZipAsync(SevenZip tool, string archive, string format, string folder, string scratchRoot,
        CancellationToken cancellationToken)
    {
        var entries = await tool.ListAsync(archive, cancellationToken);
        ArchiveSafety.CheckListing(entries, Path.GetFileName(archive), new FileInfo(archive).Length, folder);
        // 7-Zip lists no name for some (.bz2, .xz), and never more than one file.
        if (SingleStream.Contains(format) && entries.Count(e => !e.IsFolder) <= 1)
        {
            await DecodeStreamAsync(tool, archive, format, entries.FirstOrDefault(e => !e.IsFolder), folder, cancellationToken);
            PassOnMark(archive, folder);
            return;
        }
        var links = entries.Where(e => e.IsSymbolicLink).Select(e => e.Path).ToList();
        await tool.ExtractAsync(archive, folder, links, scratchRoot, cancellationToken);
        PassOnMark(archive, folder);
    }

    /// <summary>
    /// True for failures 7-Zip may get past: damage, a method or feature the in-process reader lacks. Not for
    /// refusals, cancellation or a full or unwritable disk.
    /// </summary>
    private static bool MayRetry(Exception ex) => !ArchiveSafety.IsRefusal(ex) && ex switch
    {
        OperationCanceledException or UnauthorizedAccessException or OutOfMemoryException => false,
        EndOfStreamException => true,
        IOException => false,
        _ => true,
    };

    /// <summary>
    /// The message for an in-process failure when 7-Zip can't try: what only 7-Zip unpacks is refused with
    /// <see cref="NeedsSevenZip"/> (the reader's reason as the detail), otherwise Filee's own message or a "damaged" one.
    /// </summary>
    private static Exception Explain(Exception ex, string name, bool sevenZipWouldTry)
    {
        if (ex is InvalidDataException && ex.Message.Contains(name, StringComparison.Ordinal))
            return ex;
        if (ex is NotSupportedException)
            return sevenZipWouldTry
                ? new ConversionRefusedException(NeedsSevenZip, ex.Message.TrimEnd('.'))
                : new InvalidDataException($"{name} could not be unpacked: {ex.Message}", ex);
        return ArchiveSafety.Damaged(name, ex.Message.TrimEnd('.'), ex);
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
    /// Gives the files unpacked into <paramref name="folder"/> (by Filee's own code or by 7-Zip) the archive's Mark of
    /// the Web, if it has one.
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
}
