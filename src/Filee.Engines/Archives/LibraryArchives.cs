// Unpacks ZIP (and JAR, WBPZ …), 7z and RAR 4 / 5 in-process with SharpCompress (MIT; its RAR decoder only unpacks,
// under the same unRAR terms as 7-Zip's). The archive is listed first and checked by ArchiveSafety; encrypted
// archives and entries are refused, links are left out, sizes and (for ZIP and 7z) CRC-32s are checked as each file
// is written. Solid archives and 7z are read front to back so no block is decompressed twice.

using System.Globalization;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Readers;

namespace Filee.Engines.Archives;

/// <summary>ZIP, 7z and RAR through SharpCompress.</summary>
internal static class LibraryArchives
{
    /// <summary>Unpacks the archive file <paramref name="archive"/> (with its other volumes, for RAR and ZIP).</summary>
    public static void Extract(string archive, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        var parts = Volumes(archive);
        var length = parts.Sum(p => p.Length);
        using var opened = Open(name, () => parts.Count > 1
            ? ArchiveFactory.Open(parts, Options(TypeOf(archive)))
            : ArchiveFactory.Open(archive, Options(TypeOf(archive))));
        Extract(opened, name, length, folder, cancellationToken);
    }

    /// <summary>Unpacks an archive read from <paramref name="stream"/> (a split archive's joined volumes).</summary>
    public static void Extract(Stream stream, string name, string folder, CancellationToken cancellationToken)
    {
        ArchiveType? type = null;
        stream.Position = 0;
        var isArchive = ArchiveFactory.IsArchive(stream, out type, ReaderOptions.DefaultBufferSize);
        stream.Position = 0;
        if (!isArchive)
            throw ArchiveSafety.NotAnArchive(name);
        var options = Options(type);
        options.LeaveStreamOpen = true;
        using var opened = Open(name, () => ArchiveFactory.Open(stream, options));
        Extract(opened, name, stream.Length, folder, cancellationToken);
    }

    private static void Extract(IArchive archive, string name, long length, string folder, CancellationToken cancellationToken)
    {
        List<IArchiveEntry> entries;
        try
        {
            entries = [.. archive.Entries];
        }
        catch (CryptographicException)
        {
            throw ArchiveSafety.Encrypted(name);
        }
        if (archive is SevenZipArchive { IsEncrypted: true } || entries.Any(e => Encrypted(archive, e)))
            throw ArchiveSafety.Encrypted(name);
        if (!archive.IsComplete)
            throw ArchiveSafety.Damaged(name, "a volume is missing");
        ArchiveSafety.CheckListing([.. entries.Select(e => new ArchiveItem(e.Key ?? "", e.IsDirectory, e.IsDirectory ? 0 : e.Size, Encrypted(archive, e), IsLink(e)))],
            name, length, folder);

        var writer = new EntryWriter(folder, new UnpackBudget(name, length, folder), cancellationToken);
        var checksCrc = archive.Type is ArchiveType.Zip or ArchiveType.SevenZip;
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            // One pass from the front: a solid block is decoded once, not again for every entry in it.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Wanted(reader.Entry, writer))
                    using (var data = reader.OpenEntryStream())
                        Write(reader.Entry, data, writer, checksCrc);
            }
        }
        else
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Wanted(entry, writer))
                    using (var data = entry.OpenEntryStream())
                        Write(entry, data, writer, checksCrc);
            }
        }
        writer.Finish();
    }

    /// <summary>
    /// An encrypted entry. SharpCompress marks every empty 7z file (one without data stream) as encrypted, so for 7z
    /// only entries with data count; the archive's own flag covers the rest.
    /// </summary>
    private static bool Encrypted(IArchive archive, IEntry entry) =>
        entry.IsEncrypted && (archive.Type != ArchiveType.SevenZip || entry.Size > 0);

    /// <summary>True for a file to write; folders are made here, links and devices left out.</summary>
    private static bool Wanted(IEntry entry, EntryWriter writer)
    {
        if (entry.IsDirectory)
        {
            writer.AddFolder(entry.Key ?? "", entry.LastModifiedTime);
            return false;
        }
        return !IsLink(entry);
    }

    private static void Write(IEntry entry, Stream data, EntryWriter writer, bool checksCrc)
    {
        var crc = checksCrc && entry.Size > 0 && entry.Crc is > 0 and <= uint.MaxValue ? (uint)entry.Crc : (uint?)null;
        writer.AddFile(entry.Key ?? "", data, entry.Size, crc, entry.LastModifiedTime);
    }

    /// <summary>
    /// Symbolic links, junctions and RAR redirections: a link target, a Windows reparse point attribute, or a Unix
    /// mode of type link (ZIP and 7z keep it in the upper 16 bits of the attributes, RAR from Unix in the lower).
    /// </summary>
    internal static bool IsLink(IEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.LinkTarget) || entry is RarEntry { IsRedir: true })
            return true;
        int attributes;
        try
        {
            if (entry.Attrib is not { } value)
                return false;
            attributes = value;
        }
        catch (NotImplementedException)
        {
            return false;
        }
        const int linkType = 0xA000;
        if (entry is RarEntry)
            return (attributes & 0xF000) == linkType;
        return (attributes & 0x400) != 0 || ((attributes >> 16) & 0xF000) == linkType;
    }

    /// <summary>
    /// The archive's volumes: "name.part1.rar", "name.part2.rar" … or "name.rar", "name.r00" …, ZIP's "name.z01" …
    /// "name.zip"; just the file when it has none.
    /// </summary>
    private static List<FileInfo> Volumes(string archive)
    {
        var file = new FileInfo(archive);
        try
        {
            var parts = ArchiveFactory.GetFileParts(file).Where(p => p.Exists).ToList();
            return parts.Count > 1 ? parts : [file];
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return [file];
        }
    }

    private static ArchiveType? TypeOf(string archive)
    {
        try
        {
            return ArchiveFactory.IsArchive(archive, out var type, ReaderOptions.DefaultBufferSize) ? type : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// ZIP names without the UTF-8 flag are decoded as UTF-8 when they are valid UTF-8, else with the system's OEM
    /// code page (as 7-Zip and Windows do: CP949 on Korean Windows). Other formats store Unicode names themselves.
    /// </summary>
    private static ReaderOptions Options(ArchiveType? type) => new()
    {
        LookForHeader = false,
        ArchiveEncoding = type == ArchiveType.Zip ? new ArchiveEncoding { CustomDecoder = (bytes, index, count, _) => DecodeName(bytes.AsSpan(index, count)) } : new ArchiveEncoding(),
    };

    internal static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            try
            {
                return Encoding.GetEncoding(codePage is > 0 and not 65001 ? codePage : 437).GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Opens the archive; failures to recognise it become "not a valid archive" or "password-protected".</summary>
    private static IArchive Open(string name, Func<IArchive> open)
    {
        try
        {
            return open();
        }
        catch (CryptographicException)
        {
            throw ArchiveSafety.Encrypted(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidFormatException or ArchiveException or ArgumentException)
        {
            throw new InvalidDataException(ArchiveSafety.NotAnArchive(name).Message, ex);
        }
    }
}
