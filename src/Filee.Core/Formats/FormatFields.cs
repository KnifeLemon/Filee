// Who makes each format (its fields) and how widely it is made today (its grade), issue #844.
// The field switches of #806 ask IsShown; FormatCategory stays as it is, so engines, routes and the CLI don't see this.
// Adding a format: put its id in one grade list below and, if people outside its category's field commonly make it or it
// belongs to a field that can be turned off, give it an Overlaps row. A new output format graded specialist or legacy
// belongs only to fields that can be turned off and are off by default.

namespace Filee.Core.Formats;

/// <summary>
/// How widely files in a format are made today, whether or not Filee can write it. Check legacy first, then everyday,
/// then specialist; anything else is general.
/// </summary>
public enum FormatGrade
{
    /// <summary>Made by mainstream consumer setups by default (stock OS and phone apps, major web services, a
    /// country's de facto office suite).</summary>
    Everyday,

    /// <summary>Made across fields, but not a mainstream default.</summary>
    General,

    /// <summary>The common ways of making it are in one or more specialist fields.</summary>
    Specialist,

    /// <summary>Replaced by a successor, and hardly anyone makes new files in it.</summary>
    Legacy,
}

/// <summary>A group of people who make formats. Fields that can be turned off hide the formats only they make.</summary>
/// <param name="Id">Lower-case id: a category name (<c>image</c>, <c>model3d</c>) or one of the extra fields.</param>
/// <param name="CanTurnOff">Whether a user can turn the field off.</param>
/// <param name="OnByDefault">Whether the field is on in a new install.</param>
public sealed record FormatField(string Id, bool CanTurnOff, bool OnByDefault);

/// <summary>Fields and grades of every format, and whether a format is shown with some fields turned off.</summary>
public static class FormatFields
{
    private static readonly HashSet<string> TurnOffCategories = new(["cad", "model3d", "geo", "font"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Every field: one per category (named after it), then the fields that aren't categories.</summary>
    public static IReadOnlyList<FormatField> All { get; } =
    [
        .. Enum.GetValues<FormatCategory>().Select(category => category.ToString().ToLowerInvariant())
            .Select(id => TurnOffCategories.Contains(id) ? new FormatField(id, true, false) : new FormatField(id, false, true)),
        new("game", true, false),
        new("design", true, false),
        new("science", true, false),
        // On: off would hide TAR, GZ, YAML, ... in new installs (decided in #846's review, docs/FORMAT-RULES.md).
        new("developer", true, true),
        new("writing", true, false),
        new("video-pro", true, false),
        new("audio-pro", true, false),
        new("legacy", true, false),
    ];

    private static readonly Dictionary<string, FormatField> FieldsById = All.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, FormatGrade> Grades = BuildGrades(
    [
        (FormatGrade.Everyday,
        [
            "png", "jpg", "webp", "gif", "heic", "pdf", "docx", "hwpx", "hwp", "xlsx", "csv", "pptx", "txt", "mp4",
            "mov", "mp3", "m4a", "wav", "zip"
        ]),
        (FormatGrade.General,
        [
            "tiff", "bmp", "ico", "avif", "jxl", "tga", "apng", "jxr", "livp", "svg", "svgz", "emf", "vsd", "odg",
            "docm", "dotx", "dotm", "doc", "odt", "ott", "rtf", "wps", "ofd", "abw", "pages", "pub", "eml", "msg",
            "xlsm", "xltx", "xls", "ods", "ots", "tsv", "et", "numbers", "pptm", "potx", "potm", "ppsx", "ppt",
            "odp", "otp", "key", "dps", "md", "html", "mhtml", "opml", "json", "xml", "vcf", "ics", "epub", "kepub",
            "azw3", "fb2", "cbz", "cbr", "cb7", "cbt", "cbc", "m4v", "mkv", "webm", "avi", "wmv", "mpg", "ts",
            "m2ts", "ogv", "m4b", "aac", "flac", "ogg", "opus", "wma", "aiff", "weba", "mka", "qta", "7z", "rar",
            "dmg", "iso", "img", "alz", "egg", "split", "dxf", "srt", "vtt", "smi", "lrc"
        ]),
        (FormatGrade.Specialist,
        [
            "jp2", "psd", "psb", "ppm", "icns", "qoi", "dds", "cur", "xpm", "xbm", "pam", "pfm", "miff", "dcm",
            "exr", "hdr", "fits", "xcf", "raw", "eps", "ps", "ai", "cdr", "cgm", "plt", "dst", "fodg", "fodt",
            "fods", "fodp", "rst", "tex", "org", "adoc", "typ", "mediawiki", "dbk", "ipynb", "textile", "man",
            "jsonl", "yaml", "toml", "har", "htmlz", "txtz", "chm", "dv", "mxf", "cavs", "usm", "m4s", "dav", "bik",
            "h265", "h264", "ac3", "caf", "mp2", "dss", "wv", "tta", "w64", "eac3", "ape", "tak", "dsf", "dff",
            "dts", "tar", "tgz", "tbz2", "txz", "tar7z", "tlz", "gz", "bz2", "xz", "lz", "lzma", "jar", "cab",
            "cpio", "deb", "rpm", "dwg", "ttf", "otf", "woff", "woff2", "ass", "ssa", "ttml", "scc", "glb", "gltf",
            "stl", "obj", "3mf", "ply", "fbx", "geojson", "kml", "kmz", "gpx", "shp", "fit"
        ]),
        (FormatGrade.Legacy,
        [
            "mng", "pcx", "wbmp", "sgi", "ras", "pcd", "wmf", "dot", "wpd", "lwp", "sdw", "cwk", "mcw", "602", "psw",
            "xlt", "sdc", "sda", "mobi", "azw", "azw4", "prc", "lit", "lrf", "pdb", "pml", "rb", "snb", "tcr", "oeb",
            "flv", "3gp", "3g2", "vob", "mod", "rm", "swf", "wtv", "amr", "au", "voc", "mpc", "spx", "tz", "z",
            "lha", "arj", "eot", "fon", "sub"
        ]),
    ]);

    // Formats whose fields differ from their category's. A row replaces the category's field, it doesn't add to it.
    private static readonly Dictionary<string, FormatField[]> Overlaps = BuildOverlaps(
    [
        // Layout, print, photo post-production, signs and embroidery.
        (["psd", "psb", "exr", "hdr", "xcf", "raw", "eps", "ps", "ai", "cdr", "plt", "dst"], ["design"]),
        // Game textures and cutscenes.
        (["dds", "usm", "bik"], ["game"]),
        // Medical imaging and astronomy.
        (["dcm", "fits"], ["science"]),
        // Software work: raw image formats of graphics tools, app icons and cursors, config and data files, flat XML
        // ODF for version control, man pages, streaming segments, packages and compressed archives.
        ([
            "ppm", "icns", "qoi", "cur", "xpm", "xbm", "pam", "pfm", "miff", "fodg", "fodt", "fods", "fodp", "man",
            "jsonl", "yaml", "toml", "har", "m4s", "tar", "tgz", "tbz2", "txz", "tar7z", "tlz", "gz", "bz2", "xz",
            "lz", "lzma", "jar", "cab", "cpio", "deb", "rpm"
        ], ["developer"]),
        // Markup sources, papers and outlines, maintenance-doc drawings (CGM), Calibre's ebook formats.
        (["cgm", "rst", "tex", "org", "adoc", "typ", "mediawiki", "dbk", "textile", "htmlz", "txtz"], ["writing"]),
        // Broadcast and editing masters, broadcast audio and captions, fansub and delivery subtitles, CCTV and
        // raw streams.
        ([
            "dv", "mxf", "cavs", "dav", "h265", "h264", "ac3", "mp2", "eac3", "dts", "ass", "ssa", "ttml", "scc"
        ], ["video-pro"]),
        // Lossless archiving and recorders.
        (["dss", "wv", "tta", "w64", "ape", "tak", "dsf", "dff"], ["audio-pro"]),
        // 3D printing exports from CAD designs (STL, 3MF) and CAD models handed to renderers (OBJ).
        (["stl", "obj", "3mf"], ["model3d", "cad"]),
        // JPEG 2000: digital cinema, archives and medical imaging.
        (["jp2"], ["design", "science"]),
        // Jupyter notebooks: development and data science.
        (["ipynb"], ["developer", "science"]),
        // Core Audio: app sound assets, and Apple Loops made by musicians.
        (["caf"], ["developer", "audio-pro"]),
        // Compiled help files for programs.
        (["chm"], ["writing", "developer"]),
        // Replaced by a successor and hardly made any more.
        ([
            "mng", "pcx", "wbmp", "sgi", "ras", "wmf", "dot", "wpd", "lwp", "sdw", "cwk", "mcw", "602", "psw", "xlt",
            "sdc", "sda", "mobi", "azw", "azw4", "prc", "lit", "lrf", "pdb", "pml", "rb", "snb", "tcr", "oeb", "flv",
            "3gp", "3g2", "vob", "mod", "rm", "swf", "wtv", "amr", "au", "voc", "spx", "tz", "z", "lha", "arj",
            "sub"
        ], ["legacy"]),
        // Kodak Photo CD.
        (["pcd"], ["legacy", "design"]),
        // Musepack.
        (["mpc"], ["legacy", "audio-pro"]),
        // EOT gave way to WOFF, FON to TrueType.
        (["eot", "fon"], ["legacy", "font"]),
        // Alpha-channel image sequences in video and motion graphics, as well as game textures.
        (["tga"], ["image", "game"]),
        // Vector tools make it for laser cutting, plotters and CNC, not only CAD.
        (["dxf"], ["cad", "vector"]),
        // The video and audio donuts make it (subtitle extraction, AI captions).
        (["srt"], ["subtitle", "video", "audio"]),
    ]);

    /// <summary>The fields a format belongs to: its Overlaps row, or else its category's field.</summary>
    public static IReadOnlyList<FormatField> Of(FileFormat format) =>
        Overlaps.TryGetValue(format.Id, out var fields) ? fields : [FieldsById[format.Category.ToString()]];

    /// <summary>The grade of a format. The <see cref="FormatRegistry.Folder"/> target is not a format and has none.</summary>
    public static FormatGrade GradeOf(FileFormat format) =>
        Grades.TryGetValue(format.Id, out var grade) ? grade : throw new KeyNotFoundException($"Format '{format.Id}' has no grade.");

    /// <summary>
    /// Whether a format is shown when the given fields are turned off: hidden only when every field it belongs to can be
    /// turned off and is off. Ids that aren't formats (<c>*</c> for "same as source") and the folder target are always
    /// shown; turned-off ids of fields that can't be turned off, or don't exist, are ignored. Ids ignore case.
    /// </summary>
    public static bool IsShown(string formatId, IReadOnlySet<string> turnedOff)
    {
        // The folder target belongs to the archive field, which can't be turned off today; this keeps "Extract" if it can.
        if (FormatRegistry.FindById(formatId) is not { } format || format.Id == FormatRegistry.Folder)
            return true;
        return Of(format).Any(field => !field.CanTurnOff || !turnedOff.Contains(field.Id, StringComparer.OrdinalIgnoreCase));
    }

    private static Dictionary<string, FormatGrade> BuildGrades((FormatGrade Grade, string[] Ids)[] lists)
    {
        var grades = new Dictionary<string, FormatGrade>(StringComparer.OrdinalIgnoreCase);
        foreach (var (grade, ids) in lists)
            foreach (var id in ids)
                grades.Add(FormatRegistry.Get(id).Id, grade);
        return grades;
    }

    private static Dictionary<string, FormatField[]> BuildOverlaps((string[] Ids, string[] Fields)[] rows)
    {
        var overlaps = new Dictionary<string, FormatField[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ids, fields) in rows)
            foreach (var id in ids)
                overlaps.Add(FormatRegistry.Get(id).Id, [.. fields.Select(f => FieldsById[f])]);
        return overlaps;
    }
}
