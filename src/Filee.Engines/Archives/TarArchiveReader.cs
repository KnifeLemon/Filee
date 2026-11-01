// Unpacks TAR (ustar, PAX, GNU and old V7 headers) in-process with .NET's System.Formats.Tar, which tells symbolic
// from hard links (SharpCompress's TAR entries do not). Two passes over a seekable stream: the listing first (the
// reader seeks over the data), checked by ArchiveSafety, then the files. Symbolic links, devices and FIFOs are left
// out; a hard link becomes a copy of the file it names. GNU sparse files are left to 7-Zip.

using System.Formats.Tar;

namespace Filee.Engines.Archives;

/// <summary>TAR archives through System.Formats.Tar.</summary>
internal static class TarArchiveReader
{
    public static void Extract(string archive, string folder, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Extract(stream, Path.GetFileName(archive), folder, cancellationToken);
    }

    /// <summary>Unpacks the TAR in <paramref name="stream"/>, which must be seekable.</summary>
    public static void Extract(Stream stream, string name, string folder, CancellationToken cancellationToken)
    {
        var items = new List<ArchiveItem>();
        Read(stream, name, copyData: false, entry =>
        {
            if (items.Count >= ArchiveSafety.EntryLimit)
                throw ArchiveSafety.TooManyEntries(name);
            var kind = KindOf(entry.EntryType, name);
            items.Add(new ArchiveItem(entry.Name, kind == Kind.Folder, kind == Kind.File ? entry.Length : 0,
                IsLink: kind == Kind.Skipped, HardLink: kind == Kind.HardLink ? entry.LinkName : null));
        }, cancellationToken);
        ArchiveSafety.CheckListing(items, name, stream.Length, folder);

        var writer = new EntryWriter(folder, new UnpackBudget(name, stream.Length, folder), cancellationToken);
        stream.Position = 0;
        Read(stream, name, copyData: false, entry =>
        {
            var modified = entry.ModificationTime.UtcDateTime;
            switch (KindOf(entry.EntryType, name))
            {
                case Kind.Folder:
                    writer.AddFolder(entry.Name, modified.ToLocalTime());
                    break;
                case Kind.File:
                    using (var data = entry.DataStream ?? Stream.Null)
                        writer.AddFile(entry.Name, data, entry.Length, null, modified.ToLocalTime());
                    break;
                case Kind.HardLink:
                    writer.AddHardLink(entry.Name, entry.LinkName, modified.ToLocalTime());
                    break;
            }
        }, cancellationToken);
        writer.Finish();
    }

    private enum Kind
    {
        File,
        Folder,
        HardLink,
        Skipped,
    }

    private static Kind KindOf(TarEntryType type, string name) => type switch
    {
        TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => Kind.File,
        TarEntryType.Directory => Kind.Folder,
        TarEntryType.HardLink => Kind.HardLink,
        TarEntryType.SparseFile => throw new NotSupportedException($"{name} holds a sparse file, which Filee's built-in TAR reader does not unpack."),
        TarEntryType.MultiVolume => throw new NotSupportedException($"{name} is part of a multi-volume TAR."),
        _ => Kind.Skipped, // symbolic links, devices, FIFOs, PAX global headers, volume labels
    };

    private static void Read(Stream stream, string name, bool copyData, Action<TarEntry> handle, CancellationToken cancellationToken)
    {
        using var reader = new TarReader(stream, leaveOpen: true);
        while (Next(reader, copyData, name) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            handle(entry);
        }
    }

    private static TarEntry? Next(TarReader reader, bool copyData, string name)
    {
        try
        {
            return reader.GetNextEntry(copyData);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or OverflowException
                                       or InvalidOperationException or EndOfStreamException)
        {
            throw new InvalidDataException(ArchiveSafety.Damaged(name, ex.Message).Message, ex);
        }
    }
}
