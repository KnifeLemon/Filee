// Reads Unix "ar" archives, the container of Debian packages (.deb: debian-binary, control.tar.*, data.tar.*) and of
// static libraries. All numbers are ASCII text:
//
//   "!<arch>\n"                                                  signature
//   name[16] mtime[12] uid[6] gid[6] mode[8] size[10] "`\n"      60-byte header, then the data, padded to even
//
// GNU names end with "/"; "//" holds the long names ("name/\n" each) and "/123" points into it; "/" and "/SYM64/" are
// symbol tables. BSD writes "#1/<length>" and puts the name before the data. Written from the format description in
// the ar(5) and GNU binutils documentation; the archive is listed first, then unpacked.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>Unpacks ar archives and Debian packages.</summary>
internal static class ArReader
{
    private const int HeaderSize = 60;
    private const int MaxNameLength = 4096;

    private sealed record Member(string Name, long Offset, long Size, DateTime? Modified);

    public static void Extract(string archive, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var members = List(stream, name);
        ArchiveSafety.CheckListing([.. members.Select(m => new ArchiveItem(m.Name, false, m.Size))], name, stream.Length, folder);

        var writer = new EntryWriter(folder, new UnpackBudget(name, stream.Length, folder), cancellationToken);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = member.Offset;
            writer.AddFile(member.Name, new BoundedStream(stream, member.Size), member.Size, null, member.Modified);
        }
        writer.Finish();
    }

    private static List<Member> List(FileStream stream, string name)
    {
        Span<byte> signature = stackalloc byte[8];
        if (stream.ReadAtLeast(signature, 8, throwOnEndOfStream: false) < 8 || !signature.SequenceEqual("!<arch>\n"u8))
            throw signature.SequenceEqual("!<thin>\n"u8)
                ? new InvalidDataException($"{name} is a thin ar archive, which only points to files elsewhere.")
                : ArchiveSafety.NotAnArchive(name);

        var members = new List<Member>();
        byte[]? longNames = null;
        var header = new byte[HeaderSize];
        while (stream.Position < stream.Length)
        {
            if ((stream.Position & 1) != 0)
                stream.Position++;
            if (stream.Position == stream.Length)
                break;
            if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize || header[58] != '`' || header[59] != '\n')
                throw ArchiveSafety.Damaged(name, "invalid ar member header");

            var rawName = Encoding.Latin1.GetString(header, 0, 16).TrimEnd(' ');
            var size = Number(header, 48, 10, name);
            var dataOffset = stream.Position;
            if (size > stream.Length - dataOffset)
                throw ArchiveSafety.Damaged(name, "the archive ends in the middle of a member");
            var seconds = Number(header, 16, 12, name, allowEmpty: true);
            DateTime? modified = seconds > 0 && seconds < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime : null;

            string? memberName;
            if (rawName is "/" or "/SYM64/" or "__.SYMDEF" or "__.SYMDEF SORTED")
            {
                memberName = null; // symbol table
            }
            else if (rawName == "//")
            {
                if (size > 16 * 1024 * 1024)
                    throw ArchiveSafety.Damaged(name, "the long-name table is too large");
                longNames = new byte[size];
                stream.ReadExactly(longNames);
                memberName = null;
            }
            else if (rawName.StartsWith("#1/", StringComparison.Ordinal))
            {
                // BSD: the name is the first <length> bytes of the data.
                if (!int.TryParse(rawName[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > MaxNameLength || length > size)
                    throw ArchiveSafety.Damaged(name, "invalid ar member name");
                var bytes = new byte[length];
                stream.ReadExactly(bytes);
                memberName = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                dataOffset += length;
                size -= length;
            }
            else if (rawName.Length > 1 && rawName[0] == '/' && char.IsAsciiDigit(rawName[1]))
            {
                if (longNames is null || !int.TryParse(rawName[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset >= longNames.Length)
                    throw ArchiveSafety.Damaged(name, "a member name points outside the long-name table");
                var end = Array.IndexOf(longNames, (byte)'\n', offset);
                var text = Encoding.UTF8.GetString(longNames, offset, (end < 0 ? longNames.Length : end) - offset);
                memberName = text.TrimEnd('/');
            }
            else
            {
                memberName = rawName.EndsWith('/') ? rawName[..^1] : rawName;
            }

            if (memberName is not null)
            {
                if (memberName.Length == 0)
                    throw ArchiveSafety.Damaged(name, "a member has no name");
                if (members.Count >= ArchiveSafety.EntryLimit)
                    throw ArchiveSafety.TooManyEntries(name);
                members.Add(new Member(memberName, dataOffset, size, modified));
            }
            stream.Position = dataOffset + size;
        }
        return members;
    }

    /// <summary>A decimal field padded with spaces.</summary>
    private static long Number(byte[] header, int offset, int length, string name, bool allowEmpty = false)
    {
        var text = Encoding.Latin1.GetString(header, offset, length).Trim(' ');
        if (text.Length == 0 && allowEmpty)
            return 0;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw ArchiveSafety.Damaged(name, "invalid number in an ar member header");
        return value;
    }
}
