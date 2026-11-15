// Checks one folder of the preset gallery's repository (#1530): presets/<slug>/ with preset.json, an optional
// meta.json and 1 to 4 pictures in images/ (1 is the cover), the thumbnails the gallery's CI makes next to them. This
// is what 'filee presets check <folder>' runs. Nothing else may be in the folder, and nothing in it may be a link.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Filee.Core.Gallery;

/// <summary>Checks a shared preset's folder: its preset.json, meta.json and pictures.</summary>
public static partial class SharedPresetFolderCheck
{
    public const string MetaName = "meta.json";
    public const string ImagesFolder = "images";

    /// <summary>Fewest and most pictures a shared preset has.</summary>
    public const int MinImages = 1, MaxImages = 4;

    /// <summary>Largest picture, in bytes.</summary>
    public const int MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>Shortest and longest long edge of a picture, in pixels.</summary>
    public const int MinLongEdge = 640, MaxLongEdge = 2560;

    /// <summary>Longest long edge of a thumbnail the CI made, and its largest size.</summary>
    public const int MaxThumbEdge = 480, MaxThumbBytes = 512 * 1024;

    /// <summary>Largest meta.json, in bytes.</summary>
    public const int MaxMetaBytes = 16 * 1024;

    /// <summary>The licence every shared preset and picture is published under.</summary>
    public const string License = "CC0-1.0";

    /// <summary>Ids the site's own pages use (filee.sh/presets/files/, …/download/, …/index.json): no preset has them.</summary>
    public static readonly IReadOnlyList<string> ReservedSlugs = ["files", "download", "index"];

    /// <summary>A preset's id: 3 to 48 of a-z, 0-9 and '-', not starting or ending with '-', and not reserved.</summary>
    public static bool IsSlug(string text) => Slug().IsMatch(text) && !ReservedSlugs.Contains(text);

    /// <summary>Largest folder of a shared preset, every file in it counted, in bytes.</summary>
    public const long MaxFolderBytes = 40L * 1024 * 1024;

    /// <summary>
    /// The app's languages (its i18n files): images/&lt;language&gt;/ may hold pictures in one of them, shown to people
    /// using Filee in that language. The app's tests keep this in step with its languages.
    /// </summary>
    public static readonly IReadOnlyList<string> Languages =
    [
        "ar", "bn-BD", "de", "en", "es", "fa", "fr", "hi", "id", "it", "ja", "ko", "mn", "ne", "pl", "pt-BR", "pt-PT", "ru",
        "th", "tr", "vi", "zh-CN", "zh-TW",
    ];

    /// <summary>The kinds meta.json may list (see <see cref="PresetCheckDerived.Kinds"/>).</summary>
    public static readonly IReadOnlyList<string> Kinds = ["convert", "tool", "toolbar"];

    /// <summary>
    /// Checks <paramref name="folder"/>. <paramref name="decoder"/> decodes each picture fully; without it only the
    /// headers are read.
    /// </summary>
    public static PresetCheckResult Check(string folder, IGalleryImageDecoder? decoder = null)
    {
        var result = new PresetCheckResult();
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            result.Add("folder.missing", folder, "", "is not a folder");
            return result;
        }

        var hasImages = false;
        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                result.Add("folder.link", entry.Name, "", "is a link; the folder may only hold real files");
                continue;
            }
            switch (entry)
            {
                case FileInfo { Name: SharedBundleCheck.FileName or MetaName }:
                    break;
                case DirectoryInfo { Name: ImagesFolder }:
                    hasImages = true;
                    break;
                default:
                    result.Add("folder.unexpected_file", entry.Name, "", $"doesn't belong in a shared preset's folder (only {SharedBundleCheck.FileName}, {MetaName} and {ImagesFolder}/)");
                    break;
            }
        }

        var bundleFile = new FileInfo(Path.Combine(root.FullName, SharedBundleCheck.FileName));
        if (!bundleFile.Exists || bundleFile.Attributes.HasFlag(FileAttributes.ReparsePoint))
            result.Add("folder.no_preset", SharedBundleCheck.FileName, "", "is missing");
        else if (bundleFile.Length > SharedPresetRules.MaxBundleBytes)
            result.Add("bundle.too_large", SharedBundleCheck.FileName, "", $"is {bundleFile.Length:N0} bytes; a shared preset.json may have at most {SharedPresetRules.MaxBundleBytes:N0}");
        else
        {
            var bundle = SharedBundleCheck.Check(File.ReadAllBytes(bundleFile.FullName));
            result.AddRange(bundle);
            result.Derived = bundle.Derived;
        }

        var metaFile = new FileInfo(Path.Combine(root.FullName, MetaName));
        if (metaFile.Exists && !metaFile.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // The languages the preset is written in come from meta.json, not preset.json.
            if (CheckMeta(metaFile, result.Derived, result) is { } languages && result.Derived is { } derived)
                result.Derived = derived with { Languages = languages };
        }

        if (!hasImages)
            result.Add("images.missing", ImagesFolder + "/", "", $"is missing: a shared preset needs {MinImages} to {MaxImages} pictures (1 is the cover)");
        else
            CheckImages(new DirectoryInfo(Path.Combine(root.FullName, ImagesFolder)), decoder, result);

        var size = root.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Sum(f => f.Length);
        if (size > MaxFolderBytes)
            result.Add("folder.too_large", "", "", $"holds {size:N0} bytes; a shared preset's folder may hold at most {MaxFolderBytes:N0}");
        return result;
    }

    /// <summary>
    /// The pictures: 1.png … 4.webp (1 to 4, no gaps), each with an optional 1-thumb.webp, and the language sets in
    /// images/&lt;language&gt;/ (<see cref="Languages"/>): the same names, 1 to 4 of the default set's numbers (a number a
    /// set leaves out shows the default picture).
    /// </summary>
    private static void CheckImages(DirectoryInfo folder, IGalleryImageDecoder? decoder, PresetCheckResult result)
    {
        var languages = new List<DirectoryInfo>();
        var numbers = CheckImageSet(folder, ImagesFolder, decoder, result, languages);
        if (numbers.Count < MinImages)
            result.Add("images.missing", ImagesFolder + "/", "", $"has no pictures: a shared preset needs {MinImages} to {MaxImages} (1 is the cover)");
        else if (numbers.Max != numbers.Count)
            result.Add("images.name", ImagesFolder + "/", "", "the pictures must be numbered from 1 without gaps (1 is the cover)");

        foreach (var language in languages)
        {
            var prefix = $"{ImagesFolder}/{language.Name}";
            if (!Languages.Contains(language.Name))
            {
                result.Add("images.language", prefix + "/", "", $"is not a language of Filee's ({string.Join(", ", Languages)})");
                continue;
            }
            var set = CheckImageSet(language, prefix, decoder, result, null);
            if (set.Count < MinImages)
                result.Add("images.missing", prefix + "/", "", "has no pictures; leave the folder out instead");
            foreach (var number in set.Where(n => !numbers.Contains(n)))
                result.Add("images.name", prefix + "/", "", $"has a picture {number}, which the default set (images/) doesn't have");
        }
    }

    /// <summary>
    /// One set of pictures in <paramref name="folder"/>: checks each, returns their numbers. Folders go to
    /// <paramref name="languages"/> (the default set's language sets), or are refused where there is none.
    /// </summary>
    private static SortedSet<int> CheckImageSet(DirectoryInfo folder, string prefix, IGalleryImageDecoder? decoder, PresetCheckResult result,
        List<DirectoryInfo>? languages)
    {
        var numbers = new SortedSet<int>();
        var thumbs = new List<(int Number, FileInfo File)>();
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            var name = $"{prefix}/{entry.Name}";
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                result.Add("folder.link", name, "", "is a link; the folder may only hold real files");
                continue;
            }
            if (entry is DirectoryInfo directory && languages is not null)
            {
                languages.Add(directory);
                continue;
            }
            if (entry is not FileInfo file)
            {
                result.Add("images.unexpected_file", name, "", "doesn't belong here (only 1.png … 4.webp, and a folder per language in images/)");
                continue;
            }
            var match = ImageName().Match(file.Name);
            if (!match.Success)
            {
                result.Add("images.name", name, "", "must be named 1 to 4 with .png, .jpg, .jpeg or .webp (1 is the cover)");
                continue;
            }
            var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (match.Groups[2].Success)
            {
                thumbs.Add((number, file));
                continue;
            }
            if (!numbers.Add(number))
            {
                result.Add("images.name", name, "", $"is a second picture {number}");
                continue;
            }
            CheckImage(file, name, match.Groups[3].Value.ToLowerInvariant(), thumb: false, decoder, result);
        }
        foreach (var (number, file) in thumbs)
        {
            var name = $"{prefix}/{file.Name}";
            if (!numbers.Contains(number))
                result.Add("images.name", name, "", $"is a thumbnail of picture {number}, which isn't there");
            else
                CheckImage(file, name, "webp", thumb: true, decoder, result);
        }
        return numbers;
    }

    [GeneratedRegex(@"^([1-4])(?:(-thumb)\.webp|\.(png|jpg|jpeg|webp))$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageName();

    private static void CheckImage(FileInfo file, string name, string extension, bool thumb, IGalleryImageDecoder? decoder, PresetCheckResult result)
    {
        var limit = thumb ? MaxThumbBytes : MaxImageBytes;
        if (file.Length > limit)
        {
            result.Add("images.too_large", name, "", $"is {file.Length:N0} bytes; at most {limit:N0}");
            return;
        }
        var data = File.ReadAllBytes(file.FullName);
        if (GalleryImageHeader.Read(data) is not { } info)
        {
            result.Add("images.format", name, "", "is not a PNG, JPEG or WebP picture");
            return;
        }
        if (info.Format != (extension is "jpg" or "jpeg" ? "jpeg" : extension))
        {
            result.Add("images.format", name, "", $"is a {info.Format.ToUpperInvariant()} picture with the extension .{extension}");
            return;
        }
        if (info.Animated)
        {
            result.Add("images.animated", name, "", "is animated; only still pictures are allowed");
            return;
        }
        var longEdge = Math.Max(info.Width, info.Height);
        if (thumb ? longEdge > MaxThumbEdge : longEdge is < MinLongEdge or > MaxLongEdge)
        {
            result.Add("images.size", name, "", thumb
                ? $"is {info.Width} x {info.Height} pixels; a thumbnail's long edge is at most {MaxThumbEdge}"
                : $"is {info.Width} x {info.Height} pixels; the long edge must be {MinLongEdge} to {MaxLongEdge}");
            return;
        }
        if (decoder is null)
            return;
        var decoded = decoder.Decode(data);
        if (!decoded.Ok)
            result.Add("images.decode", name, "", "can't be decoded: " + decoded.Error);
        else if (decoded.Frames > 1)
            result.Add("images.animated", name, "", "is animated; only still pictures are allowed");
        else if (decoded.Width != info.Width || decoded.Height != info.Height)
            result.Add("images.decode", name, "", $"decodes as {decoded.Width} x {decoded.Height}, not the {info.Width} x {info.Height} its header says");
    }

    // Before MetaFields, which uses them (static fields start in the order they are written).
    private static readonly StringRule Title = new(60, text => text.Trim().Length >= 3 ? null : "must be 3 to 60 characters long");
    private static readonly StringRule Description = new(600, text => text.Trim().Length >= 10 ? null : "must be 10 to 600 characters long");

    /// <summary>The fields of meta.json (see the gallery's SPEC), each optional here: the CI fills some in.</summary>
    private static readonly ObjectRule MetaFields = new(new Dictionary<string, ValueRule>(StringComparer.Ordinal)
    {
        ["id"] = new StringRule(48, text => !Slug().IsMatch(text) ? "must be 3 to 48 of a-z, 0-9 and '-'"
            : ReservedSlugs.Contains(text) ? "is reserved for the site's own pages" : null),
        ["title"] = Title,
        ["description"] = Description,
        ["language"] = new StringRule(16, AppLanguage),
        ["titleByLang"] = new MapRule(Languages.Count, AppLanguage, _ => Title),
        ["descriptionByLang"] = new MapRule(Languages.Count, AppLanguage, _ => Description),
        ["author"] = new StringRule(39, text => Login().IsMatch(text) ? null : "is not a GitHub login"),
        ["tags"] = new ListRule(new StringRule(24, text => Tag().IsMatch(text) ? null : "must be lowercase a-z, 0-9 and '-'"), 6),
        ["languages"] = new ListRule(new StringRule(16, AppLanguage), Languages.Count),
        ["kinds"] = new ListRule(new EnumRule(Kinds), Kinds.Count),
        ["formats"] = new ObjectRule(new Dictionary<string, ValueRule>(StringComparer.Ordinal)
        {
            ["from"] = new ListRule(new StringRule(32, SharedText.Id), 1000),
            ["to"] = new ListRule(new StringRule(32, SharedText.Id), 1000),
        }),
        ["minVersion"] = new StringRule(20, text => Version.TryParse(text, out var v) && v.Build >= 0 && v.Revision < 0 ? null : "must be a version like 2.5.0"),
        ["created"] = new StringRule(40, Timestamp),
        ["updated"] = new StringRule(40, Timestamp),
        ["discussion"] = new IntRule(1, int.MaxValue) { Nullable = true },
        ["license"] = new StringRule(16, text => text == License ? null : $"must be {License}"),
        ["warnings"] = new ListRule(new ObjectRule(new Dictionary<string, ValueRule>(StringComparer.Ordinal)
        {
            ["code"] = new EnumRule([.. SharedPresetWarnings.All.Select(w => w.Code)]),
            ["severity"] = new EnumRule([SharedPresetWarnings.High, SharedPresetWarnings.Medium, SharedPresetWarnings.Info]),
        }), SharedPresetWarnings.All.Count),
    });

    private static string? AppLanguage(string code) =>
        Languages.Contains(code) ? null : $"is not a language of Filee's ({string.Join(", ", Languages)})";

    private static string? Timestamp(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _) ? null : "is not a date and time like 2026-11-15T00:00:00Z";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{1,46}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$", RegexOptions.CultureInvariant)]
    private static partial Regex Login();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    /// <summary>Checks meta.json; returns the languages the preset is written in, or null when that can't be told.</summary>
    private static List<string>? CheckMeta(FileInfo file, PresetCheckDerived? derived, PresetCheckResult result)
    {
        if (file.Length > MaxMetaBytes)
        {
            result.Add("meta.too_large", MetaName, "", $"is {file.Length:N0} bytes; at most {MaxMetaBytes:N0}");
            return null;
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(file.FullName), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException ex)
        {
            result.Add("meta.invalid_json", MetaName, "", "is not valid JSON: " + ex.Message.Split('\n')[0]);
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                result.Add("meta.invalid_json", MetaName, "", "must be a JSON object");
                return null;
            }
            var before = result.Errors.Count;
            new SharedBundleCheck.Walker(result, MetaName).Check(root, MetaFields, "");
            if (result.Errors.Count != before)
                return null;
            var languages = WrittenIn(root, result);
            // What the CI derived from preset.json must still be what preset.json is.
            if (derived is null)
                return languages;
            if (root.TryGetProperty("kinds", out var kinds) && !SameSet(kinds, derived.Kinds))
                result.Add("meta.derived_mismatch", MetaName, "kinds", $"must be [{string.Join(", ", derived.Kinds)}], as preset.json is");
            if (root.TryGetProperty("formats", out var formats))
            {
                if (formats.TryGetProperty("from", out var from) && !SameSet(from, derived.From))
                    result.Add("meta.derived_mismatch", MetaName, "formats.from", $"must be [{string.Join(", ", derived.From)}], as preset.json is");
                if (formats.TryGetProperty("to", out var to) && !SameSet(to, derived.To))
                    result.Add("meta.derived_mismatch", MetaName, "formats.to", $"must be [{string.Join(", ", derived.To)}], as preset.json is");
            }
            if (root.TryGetProperty("warnings", out var warnings)
                && !warnings.EnumerateArray().Select(w => w.TryGetProperty("code", out var c) ? c.GetString() : null).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(derived.Warnings.Select(w => w.Code)))
                result.Add("meta.derived_mismatch", MetaName, "warnings", $"must be [{string.Join(", ", derived.Warnings.Select(w => w.Code))}], as preset.json is");
            if (root.TryGetProperty("minVersion", out var min) && Version.Parse(min.GetString()!) < Version.Parse(derived.MinVersion))
                result.Add("meta.derived_mismatch", MetaName, "minVersion", $"must be at least {derived.MinVersion}, the first Filee that can install it");
            return languages;
        }
    }

    /// <summary>
    /// The languages a preset is written in: <c>language</c> (an old meta.json's first of <c>languages</c>) and every
    /// language <c>titleByLang</c> has a title in. A description needs its language's title; <c>languages</c>, when
    /// given with <c>language</c>, must be exactly these.
    /// </summary>
    private static List<string>? WrittenIn(JsonElement root, PresetCheckResult result)
    {
        var listed = root.TryGetProperty("languages", out var l) ? l.EnumerateArray().Select(e => e.GetString()!).ToList() : null;
        var main = root.TryGetProperty("language", out var m) ? m.GetString()! : listed?.FirstOrDefault();
        if (main is null)
        {
            result.Add("meta.language_missing", MetaName, "language", "is missing: the language the title and description are written in");
            return null;
        }
        var titled = root.TryGetProperty("titleByLang", out var t) ? t.EnumerateObject().Select(p => p.Name).ToList() : [];
        if (titled.Contains(main))
            result.Add("field.value", MetaName, $"titleByLang.{main}", "is the main language; its title is \"title\"");
        if (root.TryGetProperty("descriptionByLang", out var d))
            foreach (var language in d.EnumerateObject().Select(p => p.Name).Where(n => n != main && !titled.Contains(n)))
                result.Add("field.value", MetaName, $"descriptionByLang.{language}", "has no title in titleByLang, so the preset isn't shown in that language");
        List<string> languages = [main, .. titled.Where(c => c != main).Order(StringComparer.Ordinal)];
        if (root.TryGetProperty("language", out _) && listed is not null && !listed.ToHashSet(StringComparer.Ordinal).SetEquals(languages))
            result.Add("meta.derived_mismatch", MetaName, "languages", $"must be [{string.Join(", ", languages)}]: language and every language titleByLang has");
        return languages;
    }

    private static bool SameSet(JsonElement list, IReadOnlyList<string> expected) =>
        list.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal).SetEquals(expected);
}
