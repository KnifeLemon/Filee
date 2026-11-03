// Fields and grades of every format (#844): the table is complete, matches the classification published in #844, and
// IsShown hides a format only when every field it belongs to can be turned off and is off.
//
// The id lists below are pinned on purpose (#844): adding a format or changing a grade or field must update them, so the
// change shows up in review. They are written out here, not computed from FormatFields.

using Filee.Core.Formats;

namespace Filee.Core.Tests;

public class FormatFieldsTests
{
    private static readonly FileFormat[] Formats = [.. FormatRegistry.Known.Where(f => f.Id != FormatRegistry.Folder)];

    private static readonly string[] TurnOffFields =
        ["cad", "model3d", "geo", "font", "game", "design", "science", "developer", "writing", "video-pro", "audio-pro", "legacy"];

    private static HashSet<string> Off(params string[] fields) => new(fields);

    private static string[] Hidden(IReadOnlySet<string> turnedOff) =>
        [.. Formats.Where(f => !FormatFields.IsShown(f.Id, turnedOff)).Select(f => f.Id).Order(StringComparer.Ordinal)];

    private static string[] Sorted(string[] ids) => [.. ids.Order(StringComparer.Ordinal)];

    // Every specialist and legacy format.
    private static readonly string[] HiddenWithEverythingOff =
    [
        "3g2", "3gp", "3mf", "602", "ac3", "adoc", "ai", "amr", "ape", "arj", "ass", "au", "azw", "azw4", "bik",
        "bz2", "cab", "caf", "cavs", "cdr", "cgm", "chm", "cpio", "cur", "cwk", "dav", "dbk", "dcm", "dds", "deb",
        "dff", "dot", "dsf", "dss", "dst", "dts", "dv", "dwg", "eac3", "eot", "eps", "exr", "fbx", "fit", "fits",
        "flv", "fodg", "fodp", "fods", "fodt", "fon", "geojson", "glb", "gltf", "gpx", "gz", "h264", "h265", "har",
        "hdr", "htmlz", "icns", "ipynb", "jar", "jp2", "jsonl", "kml", "kmz", "lha", "lit", "lrf", "lwp", "lz",
        "lzma", "m4s", "man", "mcw", "mediawiki", "miff", "mng", "mobi", "mod", "mp2", "mpc", "mxf", "obj", "oeb",
        "org", "otf", "pam", "pcd", "pcx", "pdb", "pfm", "plt", "ply", "pml", "ppm", "prc", "ps", "psb", "psd",
        "psw", "qoi", "ras", "raw", "rb", "rm", "rpm", "rst", "scc", "sda", "sdc", "sdw", "sgi", "shp", "snb", "spx",
        "ssa", "stl", "sub", "swf", "tak", "tar", "tar7z", "tbz2", "tcr", "tex", "textile", "tgz", "tlz", "toml",
        "tta", "ttf", "ttml", "txtz", "txz", "typ", "tz", "usm", "vob", "voc", "w64", "wbmp", "wmf", "woff", "woff2",
        "wpd", "wtv", "wv", "xbm", "xcf", "xlt", "xpm", "xz", "yaml", "z"
    ];

    public static TheoryData<string, string[]> HiddenWithOneFieldOff => new()
    {
        { "cad", ["dwg"] },
        { "model3d", ["fbx", "glb", "gltf", "ply"] },
        { "geo", ["fit", "geojson", "gpx", "kml", "kmz", "shp"] },
        { "font", ["otf", "ttf", "woff", "woff2"] },
        { "game", ["bik", "dds", "usm"] },
        { "design", ["ai", "cdr", "dst", "eps", "exr", "hdr", "plt", "ps", "psb", "psd", "raw", "xcf"] },
        { "science", ["dcm", "fits"] },
        { "developer", [
                "bz2", "cab", "cpio", "cur", "deb", "fodg", "fodp", "fods", "fodt", "gz", "har", "icns", "jar",
                "jsonl", "lz", "lzma", "m4s", "man", "miff", "pam", "pfm", "ppm", "qoi", "rpm", "tar", "tar7z",
                "tbz2", "tgz", "tlz", "toml", "txz", "xbm", "xpm", "xz", "yaml"
            ] },
        { "writing", ["adoc", "cgm", "dbk", "htmlz", "mediawiki", "org", "rst", "tex", "textile", "txtz", "typ"] },
        { "video-pro", [
                "ac3", "ass", "cavs", "dav", "dts", "dv", "eac3", "h264", "h265", "mp2", "mxf", "scc", "ssa", "ttml"
            ] },
        { "audio-pro", ["ape", "dff", "dsf", "dss", "tak", "tta", "w64", "wv"] },
        { "legacy", [
                "3g2", "3gp", "602", "amr", "arj", "au", "azw", "azw4", "cwk", "dot", "flv", "lha", "lit", "lrf",
                "lwp", "mcw", "mng", "mobi", "mod", "oeb", "pcx", "pdb", "pml", "prc", "psw", "ras", "rb", "rm",
                "sda", "sdc", "sdw", "sgi", "snb", "spx", "sub", "swf", "tcr", "tz", "vob", "voc", "wbmp", "wmf",
                "wpd", "wtv", "xlt", "z"
            ] },
    };

    public static TheoryData<string, string[]> FieldMembers => new()
    {
        { "cad", ["3mf", "dwg", "dxf", "obj", "stl"] },
        { "model3d", ["3mf", "fbx", "glb", "gltf", "obj", "ply", "stl"] },
        { "geo", ["fit", "geojson", "gpx", "kml", "kmz", "shp"] },
        { "font", ["eot", "fon", "otf", "ttf", "woff", "woff2"] },
        { "game", ["bik", "dds", "tga", "usm"] },
        { "design", ["ai", "cdr", "dst", "eps", "exr", "hdr", "jp2", "pcd", "plt", "ps", "psb", "psd", "raw", "xcf"] },
        { "science", ["dcm", "fits", "ipynb", "jp2"] },
        { "developer", [
                "bz2", "cab", "caf", "chm", "cpio", "cur", "deb", "fodg", "fodp", "fods", "fodt", "gz", "har",
                "icns", "ipynb", "jar", "jsonl", "lz", "lzma", "m4s", "man", "miff", "pam", "pfm", "ppm", "qoi",
                "rpm", "tar", "tar7z", "tbz2", "tgz", "tlz", "toml", "txz", "xbm", "xpm", "xz", "yaml"
            ] },
        { "writing", ["adoc", "cgm", "chm", "dbk", "htmlz", "mediawiki", "org", "rst", "tex", "textile", "txtz", "typ"] },
        { "video-pro", [
                "ac3", "ass", "cavs", "dav", "dts", "dv", "eac3", "h264", "h265", "mp2", "mxf", "scc", "ssa", "ttml"
            ] },
        { "audio-pro", ["ape", "caf", "dff", "dsf", "dss", "mpc", "tak", "tta", "w64", "wv"] },
        { "legacy", [
                "3g2", "3gp", "602", "amr", "arj", "au", "azw", "azw4", "cwk", "dot", "eot", "flv", "fon", "lha",
                "lit", "lrf", "lwp", "mcw", "mng", "mobi", "mod", "mpc", "oeb", "pcd", "pcx", "pdb", "pml", "prc",
                "psw", "ras", "rb", "rm", "sda", "sdc", "sdw", "sgi", "snb", "spx", "sub", "swf", "tcr", "tz", "vob",
                "voc", "wbmp", "wmf", "wpd", "wtv", "xlt", "z"
            ] },
    };

    // Every format registered when the admission rule came in (#844): they aren't held to it.
    private static readonly HashSet<string> FormatsBeforeAdmissionRule =
    [
        "png", "jpg", "webp", "tiff", "bmp", "gif", "ico", "avif", "heic", "jxl", "jp2", "psd", "psb", "tga", "ppm",
        "icns", "apng", "mng", "qoi", "dds", "pcx", "cur", "xpm", "xbm", "wbmp", "pam", "pfm", "sgi", "ras", "miff",
        "dcm", "pcd", "exr", "hdr", "fits", "jxr", "xcf", "livp", "raw", "svg", "svgz", "eps", "ps", "ai", "emf",
        "wmf", "cdr", "cgm", "plt", "vsd", "dst", "odg", "fodg", "pdf", "docx", "docm", "dotx", "dotm", "doc", "dot",
        "odt", "fodt", "ott", "rtf", "wps", "ofd", "wpd", "lwp", "abw", "pages", "sdw", "cwk", "mcw", "602", "psw",
        "pub", "eml", "msg", "hwpx", "hwp", "xlsx", "xlsm", "xltx", "xls", "xlt", "ods", "ots", "fods", "csv", "tsv",
        "et", "numbers", "sdc", "pptx", "pptm", "potx", "potm", "ppsx", "ppt", "odp", "otp", "fodp", "key", "dps",
        "sda", "txt", "md", "html", "mhtml", "rst", "tex", "org", "adoc", "typ", "mediawiki", "dbk", "ipynb",
        "textile", "man", "opml", "json", "jsonl", "yaml", "toml", "xml", "vcf", "har", "ics", "epub", "kepub",
        "mobi", "azw3", "azw", "azw4", "prc", "fb2", "cbz", "cbr", "cb7", "cbt", "cbc", "htmlz", "txtz", "chm",
        "lit", "lrf", "pdb", "pml", "rb", "snb", "tcr", "oeb", "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv",
        "flv", "mpg", "ts", "m2ts", "3gp", "3g2", "ogv", "vob", "mod", "dv", "mxf", "rm", "swf", "wtv", "cavs",
        "usm", "m4s", "dav", "bik", "h265", "h264", "mp3", "m4a", "m4b", "aac", "wav", "flac", "ogg", "opus", "wma",
        "aiff", "ac3", "amr", "au", "caf", "weba", "mka", "mp2", "dss", "voc", "wv", "tta", "w64", "eac3", "ape",
        "mpc", "tak", "dsf", "dff", "dts", "spx", "qta", "zip", "7z", "rar", "tar", "tgz", "tbz2", "txz", "tar7z",
        "tlz", "tz", "gz", "bz2", "xz", "lz", "lzma", "z", "jar", "cab", "cpio", "deb", "rpm", "dmg", "iso", "img",
        "lha", "arj", "alz", "egg", "split", "dwg", "dxf", "ttf", "otf", "woff", "woff2", "eot", "fon", "srt", "ass",
        "ssa", "vtt", "smi", "lrc", "ttml", "sub", "scc", "glb", "gltf", "stl", "obj", "3mf", "ply", "fbx",
        "geojson", "kml", "kmz", "gpx", "shp", "fit"
    ];

    [Fact]
    public void Every_format_has_a_grade_and_at_least_one_known_field()
    {
        var fieldIds = FormatFields.All.Select(f => f.Id).ToHashSet();

        foreach (var format in Formats)
        {
            FormatFields.GradeOf(format);
            var fields = FormatFields.Of(format);
            Assert.NotEmpty(fields);
            Assert.All(fields, field => Assert.Contains(field.Id, fieldIds));
        }
    }

    [Fact]
    public void Field_ids_are_unique() =>
        Assert.Equal(FormatFields.All.Count, FormatFields.All.Select(f => f.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void Grade_counts_match_the_published_table()
    {
        var counts = Formats.GroupBy(FormatFields.GradeOf).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(
            new Dictionary<FormatGrade, int>
            {
                [FormatGrade.Everyday] = 19,
                [FormatGrade.General] = 94,
                [FormatGrade.Specialist] = 107,
                [FormatGrade.Legacy] = 50,
            },
            counts);
    }

    [Fact]
    public void Turning_every_field_off_hides_exactly_the_specialist_and_legacy_formats() =>
        Assert.Equal(Sorted(HiddenWithEverythingOff), Hidden(Off(TurnOffFields)));

    [Theory]
    [MemberData(nameof(HiddenWithOneFieldOff))]
    public void Turning_one_field_off_hides_only_the_formats_nobody_else_makes(string field, string[] hidden) =>
        Assert.Equal(Sorted(hidden), Hidden(Off(field)));

    [Fact]
    public void A_format_in_two_fields_hides_only_when_both_are_off()
    {
        Assert.True(FormatFields.IsShown("stl", Off("cad")));
        Assert.True(FormatFields.IsShown("stl", Off("model3d")));
        Assert.False(FormatFields.IsShown("stl", Off("cad", "model3d")));
    }

    [Theory]
    [MemberData(nameof(FieldMembers))]
    public void Fields_that_can_be_turned_off_hold_the_published_formats(string field, string[] members) =>
        Assert.Equal(
            Sorted(members),
            Formats.Where(f => FormatFields.Of(f).Any(x => x.Id == field)).Select(f => f.Id).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("tga", new[] { "image", "game" })]
    [InlineData("dxf", new[] { "cad", "vector" })]
    [InlineData("srt", new[] { "subtitle", "video", "audio" })]
    [InlineData("png", new[] { "image" })]
    public void Formats_get_their_exact_fields(string id, string[] fields) =>
        Assert.Equal(fields, FormatFields.Of(FormatRegistry.Get(id)).Select(f => f.Id));

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("gif")]
    [InlineData("heic")]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("hwpx")]
    [InlineData("hwp")]
    [InlineData("xlsx")]
    [InlineData("csv")]
    [InlineData("pptx")]
    [InlineData("txt")]
    [InlineData("md")]
    [InlineData("mp4")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("srt")]
    [InlineData("zip")]
    [InlineData("svg")]
    [InlineData("json")]
    [InlineData("epub")]
    public void Everyday_formats_stay_with_every_field_off(string id) =>
        Assert.True(FormatFields.IsShown(id, Off(TurnOffFields)));

    [Fact]
    public void Turning_off_fields_that_cant_be_turned_off_or_dont_exist_hides_nothing() =>
        Assert.Empty(Hidden(Off("image", "video", "archive", "no-such-field")));

    [Fact]
    public void Ids_ignore_case()
    {
        Assert.False(FormatFields.IsShown("DWG", Off("cad")));
        Assert.False(FormatFields.IsShown("dwg", Off("CAD")));
        Assert.True(FormatFields.IsShown("DXF", Off("CAD")));
    }

    [Theory]
    [InlineData("no-such-format")]
    [InlineData("*")]
    // Folder is in the archive field, which can't be turned off today; this guards "Extract" if that changes.
    [InlineData(FormatRegistry.Folder)]
    public void Ids_that_arent_formats_are_always_shown(string id) =>
        Assert.True(FormatFields.IsShown(id, Off([.. TurnOffFields, "archive"])));

    [Fact]
    public void Specialist_and_legacy_formats_belong_only_to_fields_that_can_be_turned_off() =>
        Assert.All(
            Formats.Where(f => FormatFields.GradeOf(f) is FormatGrade.Specialist or FormatGrade.Legacy),
            f => Assert.All(FormatFields.Of(f), field => Assert.True(field.CanTurnOff, $"{f.Id} is in {field.Id}")));

    [Fact]
    public void Legacy_formats_belong_to_the_legacy_field() =>
        Assert.All(
            Formats.Where(f => FormatFields.GradeOf(f) == FormatGrade.Legacy),
            f => Assert.Contains(FormatFields.Of(f), field => field.Id == "legacy"));

    [Fact]
    public void Everyday_and_general_formats_belong_to_a_field_that_cant_be_turned_off() =>
        Assert.All(
            Formats.Where(f => FormatFields.GradeOf(f) is FormatGrade.Everyday or FormatGrade.General),
            f => Assert.Contains(FormatFields.Of(f), field => !field.CanTurnOff));

    [Fact]
    public void New_specialist_and_legacy_outputs_go_only_into_fields_that_are_off_by_default() =>
        Assert.All(
            Formats.Where(f => f.Writable && !FormatsBeforeAdmissionRule.Contains(f.Id)
                && FormatFields.GradeOf(f) is FormatGrade.Specialist or FormatGrade.Legacy),
            f => Assert.All(FormatFields.Of(f), field => Assert.True(field.CanTurnOff && !field.OnByDefault, $"{f.Id} is in {field.Id}")));

    [Fact]
    public void Only_the_extra_fields_and_four_categories_can_be_turned_off_and_only_developer_starts_on()
    {
        Assert.Equal(TurnOffFields.Order(StringComparer.Ordinal), FormatFields.All.Where(f => f.CanTurnOff).Select(f => f.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["developer"], FormatFields.All.Where(f => f.CanTurnOff && f.OnByDefault).Select(f => f.Id));
        Assert.All(FormatFields.All.Where(f => !f.CanTurnOff), f => Assert.True(f.OnByDefault, f.Id));
    }

    [Fact]
    public void The_folder_target_has_no_grade() =>
        Assert.Throws<KeyNotFoundException>(() => FormatFields.GradeOf(FormatRegistry.Get(FormatRegistry.Folder)));
}
