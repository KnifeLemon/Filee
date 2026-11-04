// ExifTool (an optional engine, Settings → Engines) for the Metadata tool: reads every field of a file, grouped by
// where it lives, and writes what the user changed into a copy without touching the picture or document itself.
// Arguments go through an argument file (UTF-8, one per line, taken literally), so no value can be read as an option; a
// value of several lines is read from a file of its own. ExifTool runs in a work folder of its own and edits a copy of
// the file there under a plain name, so nothing in the file's name can reach it ("%" is expanded in the paths it takes,
// and Perl on Windows reads a command line in the ANSI code page). A list is written one entry at a time, so a comma
// inside an entry stays in it. What the copy doesn't carry as asked, read back from it, fails the copy instead: a value
// ExifTool can't take, a field with no place in the file, one that won't come out of it.

using System.Text;
using System.Text.Json;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Metadata;

public static class ExifTool
{
    /// <summary>The longest a read may take.</summary>
    private static readonly TimeSpan ReadLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Families whose fields are written into the file and can be changed. The others describe the file or are
    /// worked out from it (File, System, Composite, ExifTool), or are kept whole: maker notes, colour profiles,
    /// Photoshop's resources, JFIF.
    /// </summary>
    private static readonly HashSet<string> EditableFamilies = ["EXIF", "IPTC", "XMP", "PDF", "QuickTime", "PNG", "File"];

    /// <summary>
    /// Groups inside an editable family that are part of the file's structure: the header and tracks of a video or HEIC
    /// (QuickTime, Meta, Track1 …: brands, sizes, dates ExifTool can't remove) and the like.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyGroups = ["InteropIFD", "MPF0", "QuickTime", "Meta", "XMP-pdfaid", "XMP-Container", "XMP-GContainer", "XMP-Item"];

    /// <summary>Fields of EXIF and IPTC the file's structure decides, or ExifTool writes by itself.</summary>
    private static readonly HashSet<string> ReadOnlyTags =
    [
        "ExifImageWidth", "ExifImageHeight", "ExifVersion", "FlashpixVersion", "ComponentsConfiguration", "XMPToolkit",
        "CodedCharacterSet", "EnvelopeRecordVersion", "ApplicationRecordVersion", "CurrentIPTCDigest",
        // How a Motion Photo's video is found: changed by hand, the video is lost to whatever reads it.
        "MotionPhoto", "MotionPhotoVersion", "MotionPhotoPresentationTimestampUs", "MicroVideo", "MicroVideoVersion", "MicroVideoOffset",
        "MicroVideoPresentationTimestampUs",
    ];

    /// <summary>
    /// What is a field in an image file directory (IFD0, and IFD1 … of a TIFF's other pages): only what describes the
    /// picture. Everything else there says how to read it, a TIFF's layout, a DNG's colour calibration, and goes for
    /// good if it is taken out.
    /// </summary>
    private static readonly HashSet<string> IfdFields =
    [
        "Make", "Model", "Software", "Artist", "Copyright", "ImageDescription", "ModifyDate", "DocumentName", "PageName",
        "HostComputer", "Orientation", "Rating", "RatingPercent", "XPTitle", "XPComment", "XPAuthor", "XPKeywords",
        "XPSubject", "OwnerName", "SerialNumber", "CameraSerialNumber", "ImageUniqueID", "UserComment", "ImageHistory",
        "ProcessingSoftware",
    ];

    /// <summary>
    /// What is a field in a PDF: its document info. The rest (page mode, version) is how the document opens.
    /// </summary>
    private static readonly HashSet<string> PdfFields =
        ["Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreateDate", "ModifyDate", "Trapped"];

    /// <summary>
    /// What is a field in a PNG: the text chunks ExifTool writes. The rest says how to read its colours; custom chunks
    /// it can't write one by one, though Remove everything takes them.
    /// </summary>
    private static readonly HashSet<string> PngFields =
        ["Title", "Author", "Description", "Copyright", "CreationTime", "Software", "Disclaimer", "Warning", "Source", "Comment", "ModifyDate"];

    /// <summary>
    /// Fields that hold a list however many entries they have: ExifTool's JSON gives a list of one as plain text.
    /// </summary>
    public static readonly IReadOnlySet<string> ListFields = new HashSet<string>
    {
        "XMP-dc:Creator", "XMP-dc:Subject", "XMP-dc:Contributor", "XMP-dc:Publisher", "XMP-dc:Relation", "XMP-dc:Type",
        "XMP-dc:Language", "XMP-dc:Identifier", "XMP-lr:HierarchicalSubject", "XMP-iptcExt:PersonInImage",
        "XMP-photoshop:SupplementalCategories", "IPTC:Keywords", "IPTC:By-line", "IPTC:By-lineTitle", "IPTC:Contact",
        "IPTC:SupplementalCategories",
    };

    /// <summary>
    /// What Remove location takes out at once, whole groups and structures among it; the fields it finds in the file
    /// (<see cref="IsLocation"/>) go by name as well.
    /// </summary>
    private static readonly string[] LocationTags =
    [
        "-GPS:all=", "-XMP-exif:GPS*=", "-XMP-iptcCore:Location=", "-XMP-iptcExt:LocationCreated=", "-XMP-iptcExt:LocationShown=",
        "-XMP-photoshop:City=", "-XMP-photoshop:State=", "-XMP-photoshop:Country=", "-IPTC:City=", "-IPTC:Sub-location=",
        "-IPTC:Province-State=", "-IPTC:Country-PrimaryLocationName=", "-IPTC:Country-PrimaryLocationCode=",
        "-QuickTime:GPSCoordinates=", "-Keys:GPSCoordinates=", "-UserData:GPSCoordinates=", "-ItemList:GPSCoordinates=",
    ];

    /// <summary>Groups worked out from the file or describing Filee's view of it, not stored in it.</summary>
    public static readonly IReadOnlySet<string> NotInTheFile = new HashSet<string> { "Composite", "System", "ExifTool" };

    /// <summary>
    /// A field that says where the file was made: what Remove location takes of the editable fields, what the window
    /// strikes through for it and what must be gone from the copy. Any spelling of GPS (a drone's "GpsLatitude"), any
    /// name with Location or Altitude in it (a video's "LocationInformation", XMP's "LocationCreatedCity"), and the
    /// address fields. A field that can't be edited stays, in plain sight (a camera's "FocusLocation" is no place).
    /// </summary>
    public static bool IsLocation(MetadataField field) =>
        field.Group == "GPS" || field.Tag.StartsWith("GPS", StringComparison.OrdinalIgnoreCase)
        || field.Tag.Contains("Location", StringComparison.Ordinal) || field.Tag.Contains("Altitude", StringComparison.Ordinal)
        || field.Tag is "City" or "State" or "Country" or "CountryCode" or "Sub-location" or "Province-State";

    /// <summary>
    /// The extensions of the files ExifTool writes (<c>exiftool -listwf</c>): only these get the Metadata tool's
    /// editing, every other file is shown read-only.
    /// </summary>
    private static readonly HashSet<string> WritableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "360", "3g2", "3gp", "3gp2", "3gpp", "aax", "ai", "ait", "apng", "arq", "arw", "avif", "ciff", "cr2", "cr3", "crm",
        "crw", "cs1", "dcp", "dng", "dr4", "dvb", "eps", "eps2", "eps3", "epsf", "erf", "exif", "exv", "f4a", "f4b", "f4p",
        "f4v", "fff", "flif", "gif", "gpr", "hdp", "heic", "heif", "hif", "icc", "icm", "iiq", "ind", "indd", "indt", "insp",
        "j2k", "jng", "jp2", "jpe", "jpeg", "jpf", "jpg", "jpm", "jps", "jpx", "jxl", "jxr", "lrv", "m4a", "m4b", "m4p",
        "m4v", "mef", "mng", "mos", "mov", "mp4", "mpo", "mqv", "mrw", "nef", "nksc", "nrw", "orf", "ori", "pbm",
        "pdf", "pef", "pgm", "png", "ppm", "ps", "ps2", "ps3", "psb", "psd", "psdt", "qt", "raf", "raw", "rw2", "rwl",
        "sr2", "srw", "thm", "tif", "tiff", "vrd", "wdp", "webp", "x3f", "xmp", "jfif",
    };

    /// <summary>The formats whose metadata ExifTool writes: one of their extensions is one it writes.</summary>
    public static IReadOnlyList<string> WritableFormats { get; } =
        [.. FormatRegistry.Known.Where(f => f.Extensions.Any(WritableExtensions.Contains)).Select(f => f.Id)];

    /// <summary>
    /// True when ExifTool writes the metadata of a file of <paramref name="formatId"/> (some of its extensions: see
    /// <see cref="WritesFile"/>).
    /// </summary>
    public static bool Writes(string? formatId) => formatId is not null && WritableFormats.Contains(formatId);

    /// <summary>
    /// True when ExifTool writes the metadata of the file at <paramref name="path"/>, by its own extension: a format
    /// can have others it doesn't (F4V yes, FLV no).
    /// </summary>
    public static bool WritesFile(string path) => WritableExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <summary>
    /// True when the file holds more than its one picture or sound, with metadata of its own the fields don't show: a
    /// Motion Photo's video, a second picture or preview (MPF), a gain map, a Samsung trailer. Remove everything would
    /// orphan or lose what's there and leave that metadata, so it isn't offered, and Remove location can't reach it.
    /// </summary>
    public static bool HoldsMore(IEnumerable<MetadataField> fields) => fields.Any(f =>
        f.Family is "MPF" or "Trailer" || f.Group is "XMP-Container" or "XMP-GContainer" or "XMP-hdrgm"
        || f.Group == "XMP-GCamera" && f.Tag is "MicroVideo" or "MotionPhoto" && f.Value != "0"
        // Samsung's maker notes share the group with its trailer.
        || f.Group == "Samsung" && (f.Tag.StartsWith("EmbeddedVideo", StringComparison.Ordinal) || f.Tag is "MCCData" or "TimeStamp" or "SurroundShotVideo" or "DepthMapData"));

    /// <summary>
    /// False for camera RAW and DNG: ExifTool can't take everything out of one, warns against trying, and what it would
    /// take is what a raw converter needs.
    /// </summary>
    public static bool RemovesEverythingFrom(string path) => !RawExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "arq", "arw", "cr2", "cr3", "crm", "crw", "cs1", "dng", "dr4", "erf", "fff", "gpr", "iiq", "mef", "mos", "mrw", "nef",
        "nrw", "orf", "ori", "pef", "raf", "raw", "rw2", "rwl", "sr2", "srw", "x3f",
    };

    /// <summary>
    /// TIFFs: ExifTool's "all" leaves the IFDs in them, where they are the file's own; their fields go by name.
    /// </summary>
    private static readonly HashSet<string> TiffExtensions = new(StringComparer.OrdinalIgnoreCase) { "tif", "tiff" };

    /// <summary>
    /// The file to read goes on ExifTool's command line, as an argument of its own (where a line break in its name
    /// starts no argument), except on Windows: Perl gets a command line in the ANSI code page, so a name outside it
    /// goes through the UTF-8 argument file (Windows file names have no line breaks).
    /// </summary>
    internal static bool PathsInArgumentFile { get; set; } = OperatingSystem.IsWindows();

    /// <summary>The program of the ExifTool in use (the user's, Filee's download, one on the PC), or null.</summary>
    public static string? Locate() =>
        EngineEnvironment.OwnProgram("exiftool", "exiftool.exe") ?? EngineEnvironment.BundledProgram("exiftool", "exiftool.exe")
        ?? EngineEnvironment.SystemProgram("exiftool", "exiftool.exe");

    /// <summary>
    /// Every field of <paramref name="path"/>, in the file's order. Throws when ExifTool can't read it.
    /// </summary>
    public static async Task<IReadOnlyList<MetadataField>> ReadAsync(string exifTool, string path, CancellationToken cancellationToken = default)
    {
        // Absolute, so the path can never start with "-" and be read as an option. No separator: a list comes as a
        // JSON array, its entries whole.
        path = Path.GetFullPath(path);
        var work = Directory.CreateTempSubdirectory("filee-exiftool-").FullName;
        try
        {
            var result = await RunAsync(exifTool, work, ["-j", "-G0:1", "-a", "-s"], [], path, ReadLimit, cancellationToken);
            if (string.IsNullOrWhiteSpace(result.StandardOutput))
                throw new IOException(ErrorOf(result) ?? "ExifTool could not read the file.");
            var fields = Parse(result.StandardOutput);
            // A file it can't read still gets its JSON, the file's own facts and this.
            return fields.FirstOrDefault(f => f is { Family: "ExifTool", Tag: "Error" }) is { } error ? throw new IOException(error.Value) : fields;
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    /// <summary>
    /// The fields in ExifTool's JSON output (<c>-j -G0:1</c>): "Family:Group:Tag" → value or list, "Group:Tag" where
    /// the family and the group have the same name (IPTC, PDF, File).
    /// </summary>
    internal static IReadOnlyList<MetadataField> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            return [];
        var fields = new List<MetadataField>();
        foreach (var property in document.RootElement[0].EnumerateObject())
        {
            var parts = property.Name.Split(':');
            if (parts.Length is not (2 or 3))
                continue; // SourceFile
            var (family, group, tag) = parts.Length == 3 ? (parts[0], parts[1], parts[2]) : (parts[0], parts[0], parts[1]);
            var isList = property.Value.ValueKind == JsonValueKind.Array || ListFields.Contains($"{group}:{tag}");
            IReadOnlyList<string> items = property.Value.ValueKind == JsonValueKind.Array
                ? [.. property.Value.EnumerateArray().Select(Text)] : [Text(property.Value)];
            fields.Add(new MetadataField(family, group, tag, items, isList, IsEditable(family, group, tag, items)));
        }
        return fields;

        // As ExifTool printed it: a title "true" comes without quotes, and isn't "True".
        static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    }

    internal static bool IsEditable(string family, string group, string tag, IReadOnlyList<string> items) =>
        EditableFamilies.Contains(family) && !ReadOnlyGroups.Contains(group) && !group.StartsWith("Track", StringComparison.Ordinal)
        && !group.StartsWith("SubIFD", StringComparison.Ordinal)
        && !ReadOnlyTags.Contains(tag)
        && (family, IsIfd(group)) switch
        {
            ("PDF", _) => PdfFields.Contains(tag),
            ("PNG", _) => PngFields.Contains(tag),
            // The file's own facts, and the one thing there it holds for someone to read.
            ("File", _) => tag == "Comment",
            (_, true) => IfdFields.Contains(tag),
            _ => true,
        }
        // A name that isn't one field ExifTool can be told to write ("Tag (1)" of a second copy) is shown, not offered.
        && MetadataEdits.IsFieldKey($"{group}:{tag}")
        // Binary data, and bytes that aren't text (ExifTool's JSON gives them as base64): written back, they'd be text.
        && !items.Any(item => item.StartsWith("(Binary data", StringComparison.Ordinal) || item.StartsWith("base64:", StringComparison.Ordinal));

    /// <summary>IFD0, IFD1 …: EXIF's own group for the picture, and a TIFF's other pages.</summary>
    private static bool IsIfd(string group) => group.Length > 3 && group.StartsWith("IFD", StringComparison.Ordinal) && group[3..].All(char.IsAsciiDigit);

    /// <summary>
    /// Writes <paramref name="edits"/> into a copy of <paramref name="source"/> at <paramref name="target"/>, in place
    /// of any file there. The picture, sound or document data is never re-encoded. Returns ExifTool's warnings that
    /// change nothing asked, if any. Throws <see cref="MetadataRefusedException"/> when the copy wouldn't carry the
    /// changes as asked (a field unknown or not writable there, a value it can't take, text in characters the field
    /// can't hold, a field that won't come out) and <see cref="IOException"/> when ExifTool fails; then no copy is left
    /// behind.
    /// </summary>
    public static async Task<string?> WriteAsync(string exifTool, string source, string target, MetadataEdits edits,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        // Beside the target: on its disk, where a long video has room, and the finished copy is only moved.
        var work = Path.Combine(Path.GetDirectoryName(target)!, FolderCopy.WorkName("exiftool"));
        Directory.CreateDirectory(work);
        try
        {
            // What Remove location and Remove everything find, and the IPTC that may have to be written again.
            var needsIptcUtf8 = edits.Set.Where(e => IsIptc(e.Key)).SelectMany(e => e.Value).Any(NeedsUtf8);
            IReadOnlyList<MetadataField> fields = edits.RemoveAll || edits.RemoveLocation || needsIptcUtf8
                ? await ReadAsync(exifTool, source, cancellationToken) : [];
            var written = "copy" + Path.GetExtension(source);
            await CopyAsync(source, Path.Combine(work, written), cancellationToken);
            progress?.Report(0.4);

            // The edits, one per line: names checked, values on one plain line or read from a file of their own. A list
            // is one assignment per entry, which replaces the field's entries.
            var args = new List<string> { "-overwrite_original" };
            // Taken by name, and so read back by name.
            var byName = new List<string>();
            var kept = new List<string>();
            if (edits.RemoveAll)
            {
                // Not what comes after the picture, if anything the fields don't tell does: it may be the file's
                // content.
                args.AddRange(["-all=", "--Trailer:all"]);
                // What "all" leaves: a TIFF's IFDs, where they are the file's own, and a comment in some kinds of file.
                var tiff = TiffExtensions.Contains(Path.GetExtension(source).TrimStart('.'));
                byName.AddRange(fields.Where(f => f.Editable && !KeptByRemoveAll(f) && (tiff && IsIfd(f.Group) || f.Key == "File:Comment")).Select(f => f.Key));
                kept.AddRange(fields.Where(f => f.Key is "IFD0:Orientation" or "XMP-tiff:Orientation" || tiff && IsIfd(f.Group) && KeptByRemoveAll(f)).Select(f => f.Key));
                args.AddRange(byName.Select(key => $"-{key}="));
                // Each from where it is: a bare "-Orientation" would make a thumbnail's the picture's.
                // PNG's gamma and sRGB too: they say how its colours are read, and are no one's business.
                args.AddRange(["-tagsFromFile", "@", "-IFD0:Orientation", "-XMP-tiff:Orientation", "-ICC_Profile", "-PNG:Gamma", "-PNG:SRGBRendering"]);
            }
            else if (edits.RemoveLocation)
            {
                args.AddRange(LocationTags);
                byName.AddRange(fields.Where(f => f.Editable && IsLocation(f)).Select(f => f.Key));
                args.AddRange(byName.Select(key => $"-{key}="));
            }
            foreach (var key in edits.Remove)
                args.Add($"-{CheckedKey(key)}=");
            var index = 0;
            // IPTC text outside Latin-1 needs IPTC in UTF-8; a file whose IPTC is Latin-1 has every text of it written
            // again in UTF-8, as ExifTool reads it now, or switching would garble it.
            foreach (var (key, items) in needsIptcUtf8 ? edits.Set.Concat(IptcToUtf8(fields, edits)) : edits.Set)
            {
                if (items.Count == 0)
                {
                    args.Add($"-{CheckedKey(key)}=");
                    continue;
                }
                foreach (var item in items)
                {
                    if (IsPlain(item))
                    {
                        args.Add($"-{CheckedKey(key)}={item}");
                        continue;
                    }
                    // A line break, a tab or spaces at either end: read from a file, byte for byte.
                    var file = $"value{index++}.txt";
                    await File.WriteAllTextAsync(Path.Combine(work, file), item, new UTF8Encoding(false), cancellationToken);
                    args.Add($"-{CheckedKey(key)}<={file}");
                }
            }

            await File.WriteAllTextAsync(Path.Combine(work, Config), GpsRefsConfig, new UTF8Encoding(false), cancellationToken);
            var result = await RunAsync(exifTool, work, ["-config", Config, "-m"], args, written, WriteLimit(source), cancellationToken);
            var copy = Path.Combine(work, written);
            // First: a refused change alone leaves ExifTool nothing to write, which it ends with an error of its own.
            if (Refusals(result, edits) is { } refused)
                throw new MetadataRefusedException(refused);
            if (result.ExitCode != 0 || !File.Exists(copy))
                throw new IOException(ErrorOf(result) ?? "ExifTool could not write the file.");
            if (await NotWrittenAsync(exifTool, copy, edits, byName, kept, cancellationToken) is { } missing)
                throw new MetadataRefusedException(missing);
            // Private stays private: a copy made here gets the folder's defaults.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(copy, File.GetUnixFileMode(source));
            File.Move(copy, target, overwrite: true);
            return Warnings(result);
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    /// <summary>
    /// A copy of its own to edit, whatever the source's attributes: read-only stays with the original.
    /// </summary>
    private static async Task CopyAsync(string source, string copy, CancellationToken cancellationToken)
    {
        await using var from = File.OpenRead(source);
        await using var to = File.Create(copy);
        await from.CopyToAsync(to, cancellationToken);
    }

    /// <summary>
    /// What Remove everything keeps of the fields: the orientation, as the picture would turn without it.
    /// </summary>
    public static bool KeptByRemoveAll(MetadataField field) => field.Tag == "Orientation" && (IsIfd(field.Group) || field.Group == "XMP-tiff");

    private const string Config = "filee.config";

    /// <summary>
    /// ExifTool's own configuration for a write: macOS puts these in XMP, where ExifTool reads them but wouldn't write or
    /// remove them, so Remove location could not finish on a picture macOS has saved.
    /// </summary>
    internal const string GpsRefsConfig = """
        %Image::ExifTool::UserDefined = (
            'Image::ExifTool::XMP::exif' => {
                GPSLatitudeRef => { Writable => 'string' },
                GPSLongitudeRef => { Writable => 'string' },
                GPSDestLatitudeRef => { Writable => 'string' },
                GPSDestLongitudeRef => { Writable => 'string' },
            },
        );
        1;

        """;

    /// <summary>
    /// The longest a write may take: ExifTool copies the whole file, so a long video takes as long as copying it.
    /// Two minutes, and one more for every 500 MB.
    /// </summary>
    internal static TimeSpan WriteLimit(string source) =>
        TimeSpan.FromMinutes(2 + (File.Exists(source) ? new FileInfo(source).Length / 500_000_000.0 : 0));

    private static bool IsIptc(string key) => key.StartsWith("IPTC:", StringComparison.Ordinal);

    /// <summary>Text IPTC can hold only in UTF-8: a character outside Latin-1.</summary>
    internal static bool NeedsUtf8(string text) => text.Any(c => c > 'ÿ');

    /// <summary>
    /// What switches a file's IPTC to UTF-8 without garbling it: nothing when it is UTF-8 already, otherwise its
    /// character set and every text field the edits leave alone, written again as they read now.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, List<string>>> IptcToUtf8(IReadOnlyList<MetadataField> fields, MetadataEdits edits)
    {
        if (fields.Any(f => f is { Group: "IPTC", Tag: "CodedCharacterSet" } && f.Value.Contains("UTF8", StringComparison.OrdinalIgnoreCase)))
            return [];
        var again = edits.RemoveAll ? [] : fields
            .Where(f => f.Family == "IPTC" && f.Editable && !edits.Set.ContainsKey(f.Key) && !edits.Remove.Contains(f.Key)
                        && !(edits.RemoveLocation && IsLocation(f)))
            .Select(f => new KeyValuePair<string, List<string>>(f.Key, [.. f.Items]));
        return [.. again, new("IPTC:CodedCharacterSet", ["UTF8"])];
    }

    /// <summary>
    /// The changes the copy doesn't carry, read back from it: a field set that isn't there, one removed that still is,
    /// IPTC text beyond ASCII that isn't as asked (IPTC has a character set; a date or a number prints as it likes,
    /// "45.5" as "45 deg 30' 0.00\""), the editable fields Remove location or Remove everything should have taken, and
    /// what Remove everything should have kept. Null when all is there.
    /// </summary>
    private static async Task<string?> NotWrittenAsync(string exifTool, string copy, MetadataEdits edits, IReadOnlyList<string> byName,
        IReadOnlyList<string> kept, CancellationToken cancellationToken)
    {
        var fields = await ReadAsync(exifTool, copy, cancellationToken);
        var written = fields.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var remaining = fields.Where(f => f.Editable && !edits.Set.ContainsKey(f.Key)
                                          && (edits.RemoveLocation && IsLocation(f) || edits.RemoveAll && !KeptByRemoveAll(f)));
        var missing = edits.Set.Where(e => e.Value.Count > 0 && (!written.TryGetValue(e.Key, out var field) || IsIptc(e.Key) && e.Value.Any(item => !item.All(char.IsAscii)) && !field.Items.SequenceEqual(e.Value)))
            .Select(e => e.Key)
            .Concat(edits.Remove.Concat(byName).Concat(edits.Set.Where(e => e.Value.Count == 0).Select(e => e.Key)).Where(written.ContainsKey))
            .Concat(remaining.Select(f => f.Key))
            .Concat(kept.Where(key => !written.ContainsKey(key) && !edits.Remove.Contains(key)))
            .Distinct().ToList();
        return missing.Count == 0 ? null : string.Join(", ", missing);
    }

    /// <summary>
    /// <paramref name="key"/> ("XMP-dc:Title") when it is a group and a field name and nothing else; throws otherwise,
    /// so no name can carry an option or a second line into the argument file.
    /// </summary>
    internal static string CheckedKey(string key) =>
        MetadataEdits.IsFieldKey(key) ? key : throw new ArgumentException($"Not a metadata field name: {key}", nameof(key));

    /// <summary>
    /// True when <paramref name="value"/> can go on a line of the argument file as it is: one line, nothing at its
    /// ends that ExifTool would trim.
    /// </summary>
    internal static bool IsPlain(string value) =>
        value.IndexOfAny(['\n', '\r', '\t']) < 0 && value.Trim() == value;

    /// <summary>
    /// ExifTool's argument file: one argument per line, as it is (ExifTool takes the line literally).
    /// </summary>
    internal static string ArgumentFile(IEnumerable<string> args)
    {
        var text = new StringBuilder();
        foreach (var arg in args)
            text.Append(arg).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// ExifTool's warnings that mean something asked wasn't written as asked: any warning about a field the edits name
    /// (a value it can't take, text cut short), a field unknown or not writable, text in characters the field can't
    /// hold. Others (a digest out of date, the colour profile removed before it is copied back, the file's own
    /// structure) don't.
    /// </summary>
    internal static string? Refusals(ProcessResult result, MetadataEdits edits)
    {
        string[] loss = ["not defined", "not writable", "could not be encoded", "Sorry", "Error opening"];
        var named = edits.Set.Keys.Concat(edits.Remove).ToList();
        var refused = WarningLines(result)
            .Where(l => loss.Any(word => l.Contains(word, StringComparison.OrdinalIgnoreCase)) || named.Any(key => l.Contains(key, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return refused.Count == 0 ? null : string.Join(Environment.NewLine, refused);
    }

    private static IEnumerable<string> WarningLines(ProcessResult result) =>
        result.StandardError.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("Warning", StringComparison.Ordinal));

    /// <summary>
    /// Runs ExifTool in <paramref name="work"/> on <paramref name="path"/>: <paramref name="options"/> on its command
    /// line, <paramref name="lines"/> in its argument file, which goes by its plain name, read in UTF-8 as the file
    /// names in it are.
    /// </summary>
    private static async Task<ProcessResult> RunAsync(string exifTool, string work, IEnumerable<string> options, IEnumerable<string> lines, string path,
        TimeSpan limit, CancellationToken cancellationToken)
    {
        const string argFile = "args.txt";
        await File.WriteAllTextAsync(Path.Combine(work, argFile), ArgumentFile(PathsInArgumentFile ? [.. lines, path] : lines), new UTF8Encoding(false), cancellationToken);
        // The options first: ExifTool takes "-config" nowhere else.
        string[] command = [.. options, "-charset", "filename=utf8", "-api", "LargeFileSupport=1", "-@", argFile];
        return await ProcessRunner.RunAsync(exifTool, PathsInArgumentFile ? command : [.. command, path], limit, cancellationToken, workingDirectory: work);
    }

    private static string? ErrorOf(ProcessResult result) =>
        result.StandardError.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Error", StringComparison.Ordinal))
        ?? (string.IsNullOrWhiteSpace(result.StandardError) ? null : result.StandardError.Trim());

    private static string? Warnings(ProcessResult result)
    {
        var warnings = WarningLines(result).ToList();
        return warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A change ExifTool would not write as asked; the message says which (ExifTool's words, in English).
/// </summary>
public sealed class MetadataRefusedException(string message) : IOException(message);
