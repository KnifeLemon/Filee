// Garry's Mod addons (.gma), the format of the Steam Workshop, unpacked by Filee. Layout (little endian), as Facepunch's
// gmad writes it:
//
//   "GMAD" + version (1 byte, 1–3; gmad writes 3)
//   SteamID u64 (unused, 0) + timestamp u64 (Unix seconds)
//   version > 1: required content, NUL-terminated strings ending with an empty one
//   title, description, author (NUL-terminated); addon version i32 (unused, 1)
//   file list: per file a number u32 (1, 2, …), its name (NUL-terminated, lower case), size i64 and CRC-32 u32;
//              a number 0 ends the list
//   the files' content one after another, then a CRC-32 of everything before it (0 when gmad ran with -nocrc)
//
// The description is the JSON gmad builds from addon.json ({"description", "type", "tags"}). Unpacking writes an
// addon.json beside the files with the title, description, type and tags, as gmad reads it to pack the folder again.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Filee.Engines.Archives;

/// <summary>Unpacks Garry's Mod addons.</summary>
internal static class Gma
{
    private static ReadOnlySpan<byte> Ident => "GMAD"u8;

    /// <summary>The newest version (gmad's).</summary>
    private const byte Version = 3;

    /// <summary>The settings file of an addon folder, which gmad packs it from; written on unpacking.</summary>
    public const string AddonJson = "addon.json";

    /// <summary>The longest name Filee reads from a file list (gmad reserves 180 bytes; longer is a damaged file).</summary>
    private const int MaxNameBytes = 4096;

    /// <summary>One file of an addon.</summary>
    internal sealed record Entry(string Name, long Size, uint Crc, long Offset);

    /// <summary>What the header says about an addon.</summary>
    internal sealed record Header(string Title, string Description, string? Type, IReadOnlyList<string> Tags, string Author,
        DateTimeOffset? Created, IReadOnlyList<Entry> Entries);

    /// <summary>True for a file that starts like an addon.</summary>
    public static bool IsGma(ReadOnlySpan<byte> start) => start.StartsWith(Ident);

    /// <summary>
    /// Unpacks every file of <paramref name="archive"/> into <paramref name="targetDirectory"/>, with an addon.json for
    /// its title, description, type and tags.
    /// </summary>
    public static void Extract(string archive, string targetDirectory, CancellationToken cancellationToken)
    {
        var archiveName = Path.GetFileName(archive);
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var header = ReadHeader(stream, archiveName);
        // Every name is checked before anything is written: one that leaves the folder stops the whole addon.
        foreach (var entry in header.Entries)
            ArchiveSafety.ResolveEntryPath(targetDirectory, entry.Name, archiveName);
        ArchiveSafety.EnsureRoom(header.Entries.Sum(e => e.Size), targetDirectory, archiveName);

        var dataStart = stream.Position;
        foreach (var entry in header.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ArchiveSafety.ResolveEntryPath(targetDirectory, entry.Name, archiveName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            path = ArchiveFiles.UniquePath(path);
            stream.Position = dataStart + entry.Offset;
            var crc = new Crc32();
            using (var output = new CrcStream(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), crc))
                ArchiveFiles.CopyLimited(new BoundedStream(stream, entry.Size), output, entry.Size, cancellationToken);
            if (new FileInfo(path).Length != entry.Size)
                throw ArchiveSafety.Damaged(archiveName, $"{entry.Name} is cut off");
            // gmad -nocrc writes 0 for every file.
            if (entry.Crc != 0 && crc.Value != entry.Crc)
                throw ArchiveSafety.Damaged(archiveName, $"checksum error in {entry.Name}");
            if (header.Created is { } created)
                File.SetLastWriteTimeUtc(path, created.UtcDateTime);
        }

        var json = Path.Combine(targetDirectory, AddonJson);
        if (!File.Exists(json))
            File.WriteAllText(json, AddonJsonText(header), new UTF8Encoding(false));
    }

    /// <summary>Reads the header and file list; leaves <paramref name="stream"/> at the first file's content.</summary>
    internal static Header ReadHeader(Stream stream, string archiveName)
    {
        try
        {
            Span<byte> ident = stackalloc byte[4];
            stream.ReadExactly(ident);
            if (!ident.SequenceEqual(Ident))
                throw new InvalidDataException($"{archiveName} is not a Garry's Mod addon (GMA).");
            var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var version = reader.ReadByte();
            if (version is 0 or > Version)
                throw new InvalidDataException($"{archiveName} is a GMA of version {version}; Filee reads versions 1 to {Version}.");
            reader.ReadUInt64(); // SteamID
            var timestamp = reader.ReadUInt64();
            if (version > 1)
            {
                while (ReadString(stream, archiveName).Length > 0)
                {
                    // required content, unused by Garry's Mod
                }
            }
            var title = ReadString(stream, archiveName);
            var description = ReadString(stream, archiveName);
            var author = ReadString(stream, archiveName);
            reader.ReadInt32(); // addon version

            var entries = new List<Entry>();
            long offset = 0;
            while (reader.ReadUInt32() != 0)
            {
                if (entries.Count >= ArchiveSafety.MaxEntries)
                    throw new InvalidDataException($"{archiveName} has more than {ArchiveSafety.MaxEntries:N0} files; Filee does not unpack it.");
                var name = ReadString(stream, archiveName);
                var size = reader.ReadInt64();
                var crc = reader.ReadUInt32();
                if (size < 0 || name.Length == 0)
                    throw ArchiveSafety.Damaged(archiveName, "the file list is broken");
                // Checked per file, so the sum never overflows: no file may be larger than what is left of the addon.
                if (size > stream.Length - stream.Position - offset)
                    throw ArchiveSafety.Damaged(archiveName, $"{name} is larger than the addon");
                entries.Add(new Entry(name, size, crc, offset));
                offset += size;
            }
            // Every file's content must be there (the CRC of the whole addon follows it).
            if (stream.Position + offset > stream.Length)
                throw ArchiveSafety.Damaged(archiveName, "the addon is cut off");

            var (text, type, tags) = ParseDescription(description);
            DateTimeOffset? created = timestamp is > 0 and < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds((long)timestamp) : null;
            return new Header(title, text, type, tags, author, created, entries);
        }
        catch (EndOfStreamException)
        {
            throw ArchiveSafety.Damaged(archiveName, "the addon is cut off");
        }
    }

    /// <summary>A NUL-terminated UTF-8 string.</summary>
    private static string ReadString(Stream stream, string archiveName)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                throw new EndOfStreamException();
            if (b == 0)
                return Encoding.UTF8.GetString([.. bytes]);
            if (bytes.Count >= MaxNameBytes)
                throw ArchiveSafety.Damaged(archiveName, "a name in the addon is too long");
            bytes.Add((byte)b);
        }
    }

    /// <summary>
    /// The description, type and tags from gmad's JSON description; an old addon whose description is plain text keeps it
    /// as it is.
    /// </summary>
    internal static (string Description, string? Type, IReadOnlyList<string> Tags) ParseDescription(string description)
    {
        try
        {
            if (JsonNode.Parse(description) is JsonObject json)
            {
                var text = json["description"] is JsonValue d && d.TryGetValue<string>(out var s) ? s : "";
                var type = json["type"] is JsonValue t && t.TryGetValue<string>(out var typeName) ? typeName : null;
                var tags = json["tags"] is JsonArray array
                    ? array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var tag) ? tag : null).OfType<string>().ToList()
                    : [];
                return (text, type, tags);
            }
        }
        catch (JsonException)
        {
            // plain text
        }
        return (description, null, []);
    }

    /// <summary>The addon.json for an unpacked addon, as gmad reads it.</summary>
    internal static string AddonJsonText(Header header)
    {
        var json = new JsonObject
        {
            ["title"] = header.Title,
            ["type"] = header.Type ?? "",
            ["tags"] = new JsonArray([.. header.Tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]),
            ["description"] = header.Description,
            ["ignore"] = new JsonArray(),
        };
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
