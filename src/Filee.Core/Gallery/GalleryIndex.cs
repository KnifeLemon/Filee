// index.json of the preset gallery (#1530): the list of shared presets the app and filee.sh show. It comes from the
// network, so it is read field by field with limits: an entry that isn't as the contract says is left out, every text
// is cut to a length, and the files it names are paths relative to index.json that end in the preset's own folder
// (filee.sh's index has files/presets/<id>/…, the repository's presets/<id>/…), never a URL or "..".

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Filee.Core.Gallery;

/// <summary>Likes, comments and downloads of a shared preset, and its scores for the Hot and Best orders.</summary>
public sealed record GalleryStats(int Likes, int Comments, int Downloads, double Hot, IReadOnlyDictionary<string, double> Best)
{
    public static readonly GalleryStats None = new(0, 0, 0, 0, new Dictionary<string, double>());

    /// <summary>The periods of the Best order: day, week, month, year and all time.</summary>
    public static readonly IReadOnlyList<string> Periods = ["day", "week", "month", "year", "all"];

    public double BestOf(string period) => Best.GetValueOrDefault(period);
}

/// <summary>One shared preset in index.json.</summary>
public sealed record GalleryEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<string> Kinds { get; init; } = [];
    public IReadOnlyList<string> FormatsFrom { get; init; } = [];
    public IReadOnlyList<string> FormatsTo { get; init; } = [];

    /// <summary>The lowest Filee that can install it.</summary>
    public Version MinVersion { get; init; } = new(0, 0, 0);

    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }

    /// <summary>Its discussion in the gallery's repository (likes and comments), when it has one.</summary>
    public int? Discussion { get; init; }

    public string License { get; init; } = SharedPresetFolderCheck.License;

    /// <summary>Its pictures (1 to 4, the cover first), as paths relative to index.json.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    /// <summary>The cover's thumbnail; null without one (the cover is shown instead).</summary>
    public string? Thumb { get; init; }

    /// <summary>Its preset.json, as a path relative to index.json.</summary>
    public required string Preset { get; init; }

    /// <summary>
    /// The install link filee.sh counts (<c>https://filee.sh/presets/download/&lt;id&gt;</c>, or relative to index.json),
    /// when the index has one; the repository's index has none.
    /// </summary>
    public string? Download { get; init; }

    /// <summary>The SHA-256 of its preset.json (lowercase hex): what is installed must be exactly that file.</summary>
    public required string Sha256 { get; init; }

    public GalleryStats Stats { get; init; } = GalleryStats.None;

    /// <summary>True when index.json had stats for it (the site adds them; the repository's own index has none).</summary>
    public bool HasStats { get; init; }
}

/// <summary>The gallery's list, as read by <see cref="GalleryIndex.Parse"/>.</summary>
public sealed partial record GalleryIndex(int Version, DateTimeOffset Generated, IReadOnlyList<GalleryEntry> Entries)
{
    /// <summary>Largest index.json the app reads, in bytes.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Most entries read; the rest are left out.</summary>
    public const int MaxEntries = 5000;

    /// <summary>Reads index.json. Throws <see cref="JsonException"/> when it isn't an index at all.</summary>
    public static GalleryIndex Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
            throw new JsonException("index.json is too large.");
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("presets", out var presets) || presets.ValueKind != JsonValueKind.Array)
            throw new JsonException("index.json has no presets list.");
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number) ? number : 0;
        var entries = new List<GalleryEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in presets.EnumerateArray())
        {
            if (entries.Count >= MaxEntries)
                break;
            if (Entry(item) is { } entry && ids.Add(entry.Id))
                entries.Add(entry);
        }
        return new GalleryIndex(version, Date(root, "generated"), entries);
    }

    /// <summary>One entry, or null when it lacks what an install needs or names a file outside its folder.</summary>
    private static GalleryEntry? Entry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var id = Text(item, "id", 48);
        if (!SharedPresetFolderCheck.IsSlug(id))
            return null;
        var preset = Text(item, "preset", 200);
        var sha256 = Text(item, "sha256", 64).ToLowerInvariant();
        var title = Text(item, "title", 60).Trim();
        if (InFolder(preset, id) != "preset.json" || !Sha256Pattern().IsMatch(sha256) || title.Length == 0)
            return null;
        var images = List(item, "images", 4, 200).Where(path => InFolder(path, id) is { } file && ImageName().IsMatch(file)).ToList();
        var thumb = Text(item, "thumb", 200);
        var download = Text(item, "download", 200);
        var formats = item.TryGetProperty("formats", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
        var stats = item.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        return new GalleryEntry
        {
            Id = id,
            Title = title,
            Description = Text(item, "description", 600),
            Author = Text(item, "author", 39),
            Tags = List(item, "tags", 6, 24),
            Languages = List(item, "languages", 10, 16),
            Kinds = List(item, "kinds", 3, 16).Where(SharedPresetFolderCheck.Kinds.Contains).ToList(),
            FormatsFrom = formats.ValueKind == JsonValueKind.Object ? List(formats, "from", 1000, 32) : [],
            FormatsTo = formats.ValueKind == JsonValueKind.Object ? List(formats, "to", 1000, 32) : [],
            MinVersion = System.Version.TryParse(Text(item, "minVersion", 20), out var min) ? min : new System.Version(0, 0, 0),
            Created = Date(item, "created"),
            Updated = Date(item, "updated"),
            Discussion = item.TryGetProperty("discussion", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) && n > 0 ? n : null,
            License = Text(item, "license", 16) is { Length: > 0 } license ? license : SharedPresetFolderCheck.License,
            Images = images,
            Thumb = InFolder(thumb, id) is { } thumbFile && ThumbName().IsMatch(thumbFile) ? thumb : null,
            Preset = preset,
            Download = IsDownload(download, id) ? download : null,
            Sha256 = sha256,
            HasStats = stats.ValueKind == JsonValueKind.Object,
            Stats = stats.ValueKind == JsonValueKind.Object ? Stats(stats) : GalleryStats.None,
        };
    }

    private static GalleryStats Stats(JsonElement stats)
    {
        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        if (stats.TryGetProperty("best", out var b) && b.ValueKind == JsonValueKind.Object)
            foreach (var period in GalleryStats.Periods)
                best[period] = Number(b, period);
        return new GalleryStats((int)Number(stats, "likes"), (int)Number(stats, "comments"), (int)Number(stats, "downloads"),
            Number(stats, "hot"), best);
    }

    /// <summary>
    /// The part of <paramref name="path"/> inside the preset's folder (<c>preset.json</c>, <c>images/1.webp</c>), when it is
    /// a relative path to that folder (<c>files/presets/&lt;id&gt;/…</c>, <c>presets/&lt;id&gt;/…</c>); null otherwise.
    /// </summary>
    private static string? InFolder(string path, string id)
    {
        var match = RelativePath().Match(path);
        return match.Success && match.Groups["id"].Value == id ? match.Groups["file"].Value : null;
    }

    // Up to three plain folders before presets/<id>/ (no "..", no scheme, no leading slash), then a file of the folder.
    [GeneratedRegex(@"^(?:[a-z0-9][a-z0-9_\-]*/){0,3}presets/(?<id>[a-z0-9\-]{3,48})/(?<file>preset\.json|images/[1-4](?:-thumb)?\.[a-z]{3,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePath();

    [GeneratedRegex(@"^images/[1-4]\.(webp|png|jpg|jpeg)$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageName();

    [GeneratedRegex(@"^images/[1-4]-thumb\.webp$", RegexOptions.CultureInvariant)]
    private static partial Regex ThumbName();

    /// <summary>
    /// An install link of the preset's: <c>https://filee.sh/presets/download/&lt;id&gt;</c> or relative to index.json
    /// (<c>download/&lt;id&gt;</c>). The app also checks that it stays on the host index.json came from.
    /// </summary>
    private static bool IsDownload(string link, string id) =>
        link == "https://filee.sh/presets/download/" + id || DownloadPath().Match(link) is { Success: true } m && m.Groups["id"].Value == id;

    [GeneratedRegex(@"^(?:[a-z0-9][a-z0-9_\-]*/){0,3}download/(?<id>[a-z0-9\-]{3,48})$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadPath();

    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private static string Text(JsonElement item, string name, int max)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return "";
        var text = value.GetString() ?? "";
        // No control or direction-changing characters in what the window shows.
        text = new string([.. text.Where(c => !char.IsControl(c) || c == '\n').Where(c => c is not (>= '‪' and <= '‮') and not (>= '⁦' and <= '⁩'))]);
        return text.Length <= max ? text : text[..max];
    }

    private static List<string> List(JsonElement item, string name, int maxCount, int maxLength)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return [.. value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(text => text.Length is > 0 && text.Length <= maxLength && !text.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal)
            .Take(maxCount)];
    }

    private static double Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0
            ? Math.Min(d, int.MaxValue)
            : 0;

    private static DateTimeOffset Date(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : DateTimeOffset.MinValue;
}
