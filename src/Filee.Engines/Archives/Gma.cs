// Garry's Mod addons (.gma), the format of the Steam Workshop: read and written by Filee, as Facepunch's gmad does.
// Layout (little endian, from gmad's AddonFormat.h, AddonReader.h and create_gmad.cpp):
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
// addon.json beside the files, so the folder packs back into the same addon. Packing reads it like gmad: title, type,
// tags and the "ignore" wildcards; files outside gmad's whitelist (the only ones Garry's Mod loads) are left out, and
// names are stored in lower case.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Filee.Engines.Archives;

/// <summary>Unpacks and writes Garry's Mod addons.</summary>
internal static class Gma
{
    private static ReadOnlySpan<byte> Ident => "GMAD"u8;

    /// <summary>The version gmad writes.</summary>
    private const byte Version = 3;

    /// <summary>The settings file of an addon folder; never packed, written back on unpacking.</summary>
    public const string AddonJson = "addon.json";

    /// <summary>
    /// gmad's whitelist (AddonWhiteList.h), in order: the files Garry's Mod mounts from an addon. A "!" entry takes back
    /// what the entries before it allowed. "*" matches any characters, slashes included.
    /// </summary>
    private static readonly string[] WhiteList =
    [
        "lua/*.lua", "scenes/*.vcd", "particles/*.pcf", "resource/fonts/*.ttf", "scripts/vehicles/*.txt",
        "resource/localization/*/*.properties", "maps/*.bsp", "maps/*.lmp", "maps/*.nav", "maps/*.ain", "maps/thumb/*.png",
        "sound/*.wav", "sound/*.mp3", "sound/*.ogg", "materials/*.vmt", "materials/*.vtf", "materials/*.png",
        "materials/*.jpg", "materials/*.jpeg", "materials/colorcorrection/*.raw", "models/*.mdl", "models/*.phy",
        "models/*.ani", "models/*.vvd", "models/*.vtx", "!models/*.sw.vtx", "!models/*.360.vtx", "!models/*.xbox.vtx",
        "gamemodes/*/*.txt", "!gamemodes/*/*/*.txt", "gamemodes/*/*.fgd", "!gamemodes/*/*/*.fgd", "gamemodes/*/logo.png",
        "gamemodes/*/icon24.png", "gamemodes/*/gamemode/*.lua", "gamemodes/*/entities/effects/*.lua",
        "gamemodes/*/entities/weapons/*.lua", "gamemodes/*/entities/entities/*.lua", "gamemodes/*/backgrounds/*.png",
        "gamemodes/*/backgrounds/*.jpg", "gamemodes/*/backgrounds/*.jpeg", "gamemodes/*/content/models/*.mdl",
        "gamemodes/*/content/models/*.phy", "gamemodes/*/content/models/*.ani", "gamemodes/*/content/models/*.vvd",
        "gamemodes/*/content/models/*.vtx", "!gamemodes/*/content/models/*.sw.vtx", "!gamemodes/*/content/models/*.360.vtx",
        "!gamemodes/*/content/models/*.xbox.vtx", "gamemodes/*/content/materials/*.vmt", "gamemodes/*/content/materials/*.vtf",
        "gamemodes/*/content/materials/*.png", "gamemodes/*/content/materials/*.jpg", "gamemodes/*/content/materials/*.jpeg",
        "gamemodes/*/content/materials/colorcorrection/*.raw", "gamemodes/*/content/scenes/*.vcd",
        "gamemodes/*/content/particles/*.pcf", "gamemodes/*/content/resource/fonts/*.ttf",
        "gamemodes/*/content/scripts/vehicles/*.txt", "gamemodes/*/content/resource/localization/*/*.properties",
        "gamemodes/*/content/maps/*.bsp", "gamemodes/*/content/maps/*.nav", "gamemodes/*/content/maps/*.ain",
        "gamemodes/*/content/maps/thumb/*.png", "gamemodes/*/content/sound/*.wav", "gamemodes/*/content/sound/*.mp3",
        "gamemodes/*/content/sound/*.ogg", "data_static/*.txt", "data_static/*.dat", "data_static/*.json",
        "data_static/*.xml", "data_static/*.csv", "shaders/fxc/*.vcs",
    ];

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

    /// <summary>The top folders of an addon, the ones gmad's whitelist starts with.</summary>
    private static readonly HashSet<string> AddonFolders = new(
        WhiteList.Select(rule => rule.TrimStart('!').Split('/')[0]), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The folder whose content goes into an addon: <paramref name="folder"/>, or the folder inside it that holds
    /// addon.json or the addon's top folders (lua, materials, models …). An addon zipped as its folder, maybe with a
    /// readme beside it ("My Addon/lua/…"), is found through folders that hold one subfolder, up to a few levels.
    /// </summary>
    public static string Root(string folder)
    {
        var current = folder;
        for (var depth = 0; depth < 8; depth++)
        {
            if (File.Exists(Path.Combine(current, AddonJson))
                || Directory.EnumerateDirectories(current).Any(d => AddonFolders.Contains(Path.GetFileName(d))))
                return current;
            var folders = Directory.GetDirectories(current);
            if (folders.Length != 1)
                break;
            current = folders[0];
        }
        return folder;
    }

    /// <summary>The settings gmad takes from addon.json.</summary>
    internal sealed record AddonSettings(string? Title, string Description, string Type, IReadOnlyList<string> Tags, IReadOnlyList<string> Ignore);

    /// <summary>Reads addon.json; a missing file gives the defaults (title from the folder, type "servercontent").</summary>
    internal static AddonSettings ReadSettings(string? jsonPath)
    {
        if (jsonPath is null || !File.Exists(jsonPath))
            return new AddonSettings(null, "Description", "servercontent", [], []);
        JsonObject json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(jsonPath), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject ?? throw new InvalidDataException("addon.json is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"addon.json can't be read: {ex.Message}");
        }

        static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static List<string> Texts(JsonNode? node) => node is JsonArray array ? [.. array.Select(Text).OfType<string>()] : [];

        var title = Text(json["title"]);
        var type = Text(json["type"]);
        return new AddonSettings(
            string.IsNullOrWhiteSpace(title) ? null : title,
            Text(json["description"]) ?? "Description",
            string.IsNullOrWhiteSpace(type) ? "servercontent" : type.ToLowerInvariant(),
            [.. Texts(json["tags"]).Where(t => t.Length > 0).Select(t => t.ToLowerInvariant())],
            Texts(json["ignore"]));
    }

    /// <summary>True when gmad's whitelist lets <paramref name="name"/> (lower case, "/" separated) into an addon.</summary>
    internal static bool IsAllowed(string name)
    {
        var valid = false;
        foreach (var rule in WhiteList)
        {
            if (rule.StartsWith('!'))
            {
                if (Wildcard(rule[1..], name))
                    valid = false;
            }
            else if (!valid)
            {
                valid = Wildcard(rule, name);
            }
        }
        return valid;
    }

    /// <summary>True when one of gmad's own rules or an addon.json "ignore" wildcard leaves <paramref name="name"/> out.</summary>
    internal static bool IsIgnored(string name, IReadOnlyList<string> ignore)
    {
        var lower = name.ToLowerInvariant();
        if (lower == AddonJson || Wildcard("*thumbs.db", lower) || Wildcard("*desktop.ini", lower) || Wildcard(".git*", lower)
            || name == ".DS_Store" || Wildcard("*/.DS_Store", name))
            return true;
        return ignore.Any(pattern => Wildcard(pattern.Replace('\\', '/'), name));
    }

    /// <summary>Bootil's wildcard test: "*" matches any run of characters (slashes too), everything else itself.</summary>
    internal static bool Wildcard(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && pattern[p] == text[t])
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    /// <summary>
    /// Writes an addon of <paramref name="files"/> (entry name relative to the addon folder, "/" separated, and the file
    /// on disk). addon.json among them gives the title, type, tags and ignores; <paramref name="fallbackTitle"/> is the
    /// title without one. Returns the names left out because Garry's Mod would not load them.
    /// </summary>
    public static IReadOnlyList<string> Write(IReadOnlyList<(string EntryName, string Path)> files, string output, string fallbackTitle,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var json = files.FirstOrDefault(f => string.Equals(f.EntryName, AddonJson, StringComparison.OrdinalIgnoreCase));
        var settings = ReadSettings(json.Path);
        var title = settings.Title ?? fallbackTitle;

        var packed = new List<(string Name, string Path, long Size)>();
        var skipped = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entryName, path) in files.OrderBy(f => f.EntryName, StringComparer.Ordinal))
        {
            var name = entryName.Replace('\\', '/').TrimStart('/');
            if (IsIgnored(name, settings.Ignore))
                continue;
            var lower = name.ToLowerInvariant();
            if (!IsAllowed(lower) || !names.Add(lower))
            {
                skipped.Add(name);
                continue;
            }
            packed.Add((lower, path, new FileInfo(path).Length));
        }
        if (packed.Count == 0)
            throw new InvalidDataException(
                "None of these files can go into a Garry's Mod addon. An addon holds lua/, materials/, models/, sound/, maps/ and the like, as Garry's Mod loads them.");

        var description = new JsonObject
        {
            ["description"] = settings.Description,
            ["type"] = settings.Type,
            ["tags"] = new JsonArray([.. settings.Tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var total = Math.Max(1, packed.Sum(f => f.Size));
        long done = 0;
        var crc = new Crc32();
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        using var stream = new CrcStream(file, crc);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Ident);
            writer.Write(Version);
            writer.Write(0UL); // SteamID
            writer.Write((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.Write((byte)0); // no required content
            WriteString(writer, title);
            WriteString(writer, description);
            WriteString(writer, "Author Name"); // as gmad: unused
            writer.Write(1); // addon version, unused

            var number = 0u;
            foreach (var (name, path, size) in packed)
            {
                writer.Write(++number);
                WriteString(writer, name);
                writer.Write(size);
                writer.Write(FileCrc(path, cancellationToken));
            }
            writer.Write(0u);
        }
        foreach (var (_, path, size) in packed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var copied = ArchiveFiles.CopyLimited(source, stream, size, cancellationToken);
            if (copied != size)
                throw new IOException($"{path} changed while it was packed.");
            done += size;
            progress?.Report((double)done / total);
        }
        // The CRC of everything written so far, which the game checks after a download.
        Span<byte> tail = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tail, crc.Value);
        file.Write(tail);
        return skipped;
    }

    private static void WriteString(BinaryWriter writer, string text)
    {
        writer.Write(Encoding.UTF8.GetBytes(text.Replace("\0", "")));
        writer.Write((byte)0);
    }

    private static uint FileCrc(string path, CancellationToken cancellationToken)
    {
        var crc = new Crc32();
        var buffer = new byte[1 << 16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            crc.Append(buffer.AsSpan(0, read));
        }
        return crc.Value;
    }
}
