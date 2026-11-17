// What the "Metadata" window changes in a file's metadata, on the conversion it starts (Preset.Metadata): fields given
// a value or removed, the location or everything taken out at once. Only an engine that writes metadata and nothing
// else (ExifTool, ConversionEdge.Metadata) applies it.

using System.Text.RegularExpressions;

namespace Filee.Core.Presets;

public sealed partial class MetadataEdits
{
    private Dictionary<string, List<string>> _set = [];
    private List<string> _remove = [];

    /// <summary>
    /// Fields given a new value, or added: "Group:Tag" (<see cref="IsFieldKey"/>) → its value, one item, or one per
    /// entry of a list (keywords, creators), each whole (a comma inside one is part of it). No items removes the field.
    /// Read leniently: an entry that names no one field, or has no list (a file edited by hand), is left out rather
    /// than written, and so is an item that is missing.
    /// </summary>
    public Dictionary<string, List<string>> Set
    {
        get => _set;
        set => _set = (value ?? []).Where(e => IsFieldKey(e.Key) && e.Value is not null)
            .ToDictionary(e => e.Key, e => e.Value.Where(item => item is not null).ToList());
    }

    /// <summary>Fields removed, "Group:Tag". Read leniently, as <see cref="Set"/>.</summary>
    public List<string> Remove
    {
        get => _remove;
        set => _remove = [.. (value ?? []).Where(key => key is not null && IsFieldKey(key)).Distinct()];
    }

    /// <summary>
    /// Every field that says where the file was made: GPS, and the place fields of XMP, IPTC and QuickTime.
    /// </summary>
    public bool RemoveLocation { get; set; }

    /// <summary>
    /// Everything that can go: the orientation and the colour profile stay (the picture would look different without
    /// them), and so do fields the file's structure holds on to.
    /// </summary>
    public bool RemoveAll { get; set; }

    /// <summary>Nothing to change.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Set.Count == 0 && Remove.Count == 0 && !RemoveLocation && !RemoveAll;

    /// <summary>
    /// True for one field named by its group and its name ("XMP-dc:Title"), and nothing else: no option, no wildcard,
    /// no second line, no group-less name ExifTool would write wherever it likes.
    /// </summary>
    public static bool IsFieldKey(string key) => FieldKey().IsMatch(key);

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+:[A-Za-z0-9_-]+\z")]
    private static partial Regex FieldKey();

    public MetadataEdits Clone() => new()
    {
        Set = Set.ToDictionary(e => e.Key, e => e.Value.ToList()),
        Remove = [.. Remove],
        RemoveLocation = RemoveLocation,
        RemoveAll = RemoveAll,
    };
}
