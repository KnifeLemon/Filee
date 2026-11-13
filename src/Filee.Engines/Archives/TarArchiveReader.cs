// Unpacks TAR (ustar, PAX, GNU and old V7 headers) in-process with .NET's System.Formats.Tar, which tells symbolic
// from hard links (SharpCompress's TAR entries do not). Passes over a seekable stream: Filee's own walk over the
// headers (System.Formats.Tar allocates PAX headers and GNU long names at their stated size), the listing (the reader
// seeks over the data), checked by ArchiveSafety, then the files. Symbolic links, devices and FIFOs are left out; a
// hard link becomes a copy of the file it names and counts at its size. GNU sparse files are left to 7-Zip.

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
        CheckHeaders(stream, name, cancellationToken);
        stream.Position = 0;
        var items = new List<ArchiveItem>();
        var names = new NameTally(name);
        Read(stream, name, copyData: false, entry =>
        {
            if (items.Count >= ArchiveSafety.EntryLimit)
                throw ArchiveSafety.TooManyEntries(name);
            names.Add(entry.Name);
            if (entry.EntryType == TarEntryType.HardLink)
                names.Add(entry.LinkName);
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

    /// <summary>The largest PAX extended header ('x', 'g') read; real ones hold a few attributes.</summary>
    private const int MaxExtendedHeader = 8 * 1024 * 1024;

    /// <summary>The largest GNU long name or link ('L', 'K'): a path of the longest length allowed, in UTF-8, and its NUL.</summary>
    private const int MaxLongName = ArchiveSafety.MaxPathLength * 4 + 1;

    /// <summary>
    /// Walks the 512-byte headers before System.Formats.Tar reads them, because it allocates a PAX extended header or a
    /// GNU long name at whatever size its header states (gigabytes, from a file of a few bytes). Refuses those beyond
    /// what any real archive holds. Stops without a word at the end, or at a header it can't follow: the reader reports
    /// the damage then.
    /// </summary>
    private static void CheckHeaders(Stream stream, string name, CancellationToken cancellationToken)
    {
        var header = new byte[512];
        long position = 0;
        long? paxSize = null; // a "size" record of a PAX header, for the entry it describes
        for (var count = 0; ; count++)
        {
            if ((count & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            stream.Position = position;
            // All-zero blocks (the end) fail the checksum too.
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !ChecksumMatches(header)
                || Number(header.AsSpan(124, 12)) is not { } size)
                return;
            var type = (char)header[156];
            if (type is 'x' or 'g' && size > MaxExtendedHeader)
                throw ArchiveSafety.Oversized(name, $"a TAR extended header is larger than {MaxExtendedHeader / (1024 * 1024)} MB");
            if (type is 'L' or 'K' && size > MaxLongName)
                throw ArchiveSafety.NameTooLong(name);
            var dataSize = size;
            if (type == 'x')
            {
                var data = new byte[size];
                if (stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) < data.Length)
                    return;
                paxSize = PaxSize(data) ?? paxSize;
            }
            else if (type is not ('g' or 'L' or 'K'))
            {
                dataSize = paxSize ?? size;
                paxSize = null;
            }
            position += 512 + (dataSize + 511) / 512 * 512;
            if (position < 0 || position >= stream.Length)
                return;
        }
    }

    /// <summary>True when the header's checksum field matches its bytes (as unsigned or, as old tars wrote it, signed bytes).</summary>
    private static bool ChecksumMatches(ReadOnlySpan<byte> header)
    {
        if (Number(header.Slice(148, 8)) is not { } stored)
            return false;
        long unsigned = 0, signed = 0;
        for (var i = 0; i < header.Length; i++)
        {
            var b = i is >= 148 and < 156 ? (byte)' ' : header[i];
            unsigned += b;
            signed += (sbyte)b;
        }
        return stored == unsigned || stored == signed;
    }

    /// <summary>A numeric header field: octal digits (spaces and NULs around them), or big-endian binary after 0x80.</summary>
    private static long? Number(ReadOnlySpan<byte> field)
    {
        if (field[0] == 0x80)
        {
            long binary = 0;
            foreach (var b in field[1..])
            {
                if (binary > long.MaxValue >> 8)
                    return null;
                binary = binary << 8 | b;
            }
            return binary;
        }
        long value = 0;
        var digits = 0;
        foreach (var b in field)
        {
            if (b is >= (byte)'0' and <= (byte)'7')
            {
                if (value > long.MaxValue >> 3)
                    return null;
                value = value << 3 | (long)(b - '0');
                digits++;
            }
            else if (b is (byte)' ' or 0)
            {
                if (digits > 0)
                    break;
            }
            else
            {
                return null;
            }
        }
        return value;
    }

    /// <summary>The "size" record of a PAX extended header ("27 size=1234567\n" …), or null.</summary>
    private static long? PaxSize(ReadOnlySpan<byte> data)
    {
        long? size = null;
        while (data.Length > 0)
        {
            var space = data.IndexOf((byte)' ');
            if (space <= 0 || !int.TryParse(data[..space], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length)
                || length <= space || length > data.Length)
                break;
            var record = data[(space + 1)..length];
            if (record.StartsWith("size="u8) && long.TryParse(record[5..].TrimEnd((byte)'\n'), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                size = value;
            data = data[length..];
        }
        return size;
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
