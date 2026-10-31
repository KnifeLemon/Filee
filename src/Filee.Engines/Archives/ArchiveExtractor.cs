// Unpacks every archive format Filee reads into a folder. In-process first: SharpCompress (ZIP, 7z, RAR), .NET's TAR
// reader, gzip / bzip2 / xz / LZMA / zstd decoders, and Filee's own readers (ALZ, EGG, lzip, Unix compress, ar / DEB,
// cpio, RPM, ISO 9660). The bundled 7-Zip reads what is left (CAB, DMG, IMG, LHA, ARJ, UDF discs) and is asked again
// when an in-process reader meets a feature it lacks or damage it can't read past; deliberate refusals (password,
// paths leaving the folder, bombs) are final. Compressed TARs (.tar.gz, .tar.lz …) and RPM payloads are unpacked
// stage by stage until the files appear. Every stage is listed and checked first, and links are removed.

using Filee.Core.Formats;

namespace Filee.Engines.Archives;

/// <summary>Unpacks archives into scratch folders.</summary>
internal sealed class ArchiveExtractor(SevenZip? sevenZip)
{
    /// <summary>Formats read in-process (the bundled 7-Zip only as a fallback).</summary>
    public static readonly IReadOnlyList<string> BuiltInFormats =
    [
        "zip", "jar", "7z", "rar", "tar", "tgz", "tbz2", "txz", "tar7z", "tz", "tlz", "gz", "bz2", "xz", "lzma", "z",
        "lz", "cpio", "deb", "rpm", "iso", "alz", "egg", "split",
    ];

    /// <summary>Formats only the bundled 7-Zip reads.</summary>
    public static readonly IReadOnlyList<string> SevenZipFormats = ["cab", "dmg", "img", "lha", "arj"];

    /// <summary>Everything that can be unpacked when 7-Zip is there.</summary>
    public static IEnumerable<string> ReadableFormats => BuiltInFormats.Concat(SevenZipFormats);

    /// <summary>Formats 7-Zip can read too, so it may try again after an in-process reader gave up.</summary>
    private static readonly HashSet<string> SevenZipAlsoReads =
    [
        "zip", "jar", "7z", "rar", "tar", "tgz", "tbz2", "txz", "tar7z", "tz", "gz", "bz2", "xz", "lzma", "z", "cpio",
        "deb", "rpm", "iso", "split",
    ];

    /// <summary>A TAR inside a compressed stream (or a 7z): unpacking the outer layer yields one .tar file.</summary>
    private static readonly HashSet<string> TarWrapped = ["tgz", "tbz2", "txz", "tar7z", "tz", "tlz"];

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
        if (SevenZipFormats.Contains(format) || format == "iso" && sevenZip is not null && IsoReader.HasUdf(archive))
        {
            await WithSevenZipAsync(archive, folder, scratchRoot, cancellationToken);
            return;
        }
        try
        {
            await Task.Run(() => ExtractInProcess(archive, format, folder, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (MayRetry(ex) && SevenZipAlsoReads.Contains(format) && sevenZip is not null)
        {
            ArchiveFiles.TryDeleteFolder(folder);
            Directory.CreateDirectory(folder);
            await WithSevenZipAsync(archive, folder, scratchRoot, cancellationToken);
        }
        catch (Exception ex) when (MayRetry(ex))
        {
            throw Explain(ex, Path.GetFileName(archive), SevenZipAlsoReads.Contains(format));
        }
    }

    /// <summary>Unpacks one layer with the in-process readers.</summary>
    internal static void ExtractInProcess(string archive, string format, string folder, CancellationToken cancellationToken)
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
                IsoReader.Extract(archive, folder, cancellationToken);
                break;
            case "split":
                ExtractSplit(archive, folder, cancellationToken);
                break;
            case "zip":
            case "jar":
            case "7z":
            case "rar":
            case "tar7z":
                LibraryArchives.Extract(archive, folder, cancellationToken);
                break;
            default:
                if (!CompressedFile.Formats.Contains(format))
                    throw new NotSupportedException($"Filee has no built-in reader for {format}.");
                CompressedFile.Decompress(archive, format, Path.Combine(folder, CompressedFile.OutputName(archive, TarWrapped.Contains(format))), cancellationToken);
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

    private async Task WithSevenZipAsync(string archive, string folder, string scratchRoot, CancellationToken cancellationToken)
    {
        var tool = sevenZip ?? throw new InvalidDataException(
            $"{Path.GetFileName(archive)} needs the bundled 7-Zip (engines/7zip), which was not found.");
        var entries = await tool.ListAsync(archive, cancellationToken);
        ArchiveSafety.CheckListing(entries, Path.GetFileName(archive), new FileInfo(archive).Length, folder);
        var links = entries.Where(e => e.IsSymbolicLink).Select(e => e.Path).ToList();
        await tool.ExtractAsync(archive, folder, links, scratchRoot, cancellationToken);
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

    /// <summary>The message for an in-process failure when 7-Zip can't try: Filee's own, or a "damaged" one.</summary>
    private static Exception Explain(Exception ex, string name, bool sevenZipWouldTry)
    {
        if (ex is InvalidDataException && ex.Message.Contains(name, StringComparison.Ordinal))
            return ex;
        if (ex is NotSupportedException)
            return new InvalidDataException(sevenZipWouldTry
                ? $"{name} uses something Filee's built-in reader does not unpack ({ex.Message.TrimEnd('.')}); the bundled 7-Zip may, but it was not found."
                : $"{name} could not be unpacked: {ex.Message}", ex);
        return ArchiveSafety.Damaged(name, ex.Message.TrimEnd('.'));
    }

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
