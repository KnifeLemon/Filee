// The icons a donut slice can show: a set chosen for file work from Material Design Icons (the set the rest of the app
// uses), each by its own name as key, grouped for the icon picker, and the built-in presets' own icons. The path data
// is in SliceIcons.Data.cs.

using Avalonia.Media;
using Filee.Core.Presets;

namespace Filee.App.Controls;

/// <summary>The icon picker's groups.</summary>
public enum SliceIconCategory
{
    FilesAndFolders,
    Pictures,
    CropAndTransform,
    PdfAndPages,
    VideoAndAudio,
    TextAndData,
    ShareAndMove,
    General,
}

/// <summary>The icons a slice can show (<see cref="Preset.Icon"/>), drawn from 24 × 24 path data.</summary>
internal static partial class SliceIcons
{
    /// <summary>The side of the square every icon's path data is drawn in.</summary>
    internal const double Box = 24;

    // Icons a tool shows when its preset has no icon of its own (a copy of a built-in tool, say).
    private static readonly Dictionary<string, string> ToolDefaults = new()
    {
        [Preset.CropTool] = "crop",
        [Preset.ClipTool] = "content-cut",
        [Preset.ImageEditTool] = "image-edit-outline",
        [Preset.HideTool] = "blur",
    };

    // Built on first use: the data is in another file of this class, and static fields of a class split over files
    // are not set up in a known order.
    private static Dictionary<string, int>? _index;

    private static Dictionary<string, int> Index =>
        _index ??= Data.Select((icon, i) => (icon.Key, i)).ToDictionary(e => e.Key, e => e.i, StringComparer.Ordinal);

    private static readonly Dictionary<string, Geometry> Geometries = new(StringComparer.Ordinal);

    /// <summary>Every icon's key, in the picker's order.</summary>
    internal static IEnumerable<string> Keys => Data.Select(icon => icon.Key);

    /// <summary>The picker's groups and their icons, in order.</summary>
    internal static IEnumerable<(SliceIconCategory Category, IReadOnlyList<string> Keys)> Categories =>
        CategoryData.Select(c => (c.Category, (IReadOnlyList<string>)c.Keys));

    internal static bool Exists(string? key) => key is not null && Index.ContainsKey(key);

    /// <summary>
    /// The icon <paramref name="preset"/> shows when it has none of its own: a built-in preset's (by id), else its tool's,
    /// else none. Plain format conversions have none: their name is their label.
    /// </summary>
    internal static string? DefaultFor(Preset preset) =>
        DefaultData.TryGetValue(preset.Id, out var key) ? key
        : preset.IsTool && ToolDefaults.TryGetValue(preset.Tool ?? Preset.CropTool, out var tool) ? tool
        : null;

    /// <summary>
    /// The icon <paramref name="preset"/>'s slice shows: its own, none for <see cref="Preset.NoIcon"/>, or (unset, or a
    /// key this version doesn't have) its default.
    /// </summary>
    internal static string? For(Preset preset) =>
        preset.Icon == Preset.NoIcon ? null
        : Exists(preset.Icon) ? preset.Icon
        : DefaultFor(preset);

    /// <summary>The shape of icon <paramref name="key"/> in a <see cref="Box"/> × <see cref="Box"/> square, or null.</summary>
    internal static Geometry? Geometry(string? key)
    {
        if (key is null || !Index.TryGetValue(key, out var i))
            return null;
        if (!Geometries.TryGetValue(key, out var geometry))
            Geometries[key] = geometry = StreamGeometry.Parse(Data[i].Path);
        return geometry;
    }

    /// <summary>
    /// The icons matching <paramref name="text"/>: every word of it starts a word of the icon's name or search words.
    /// Blank: every icon.
    /// </summary>
    internal static IEnumerable<string> Search(string? text)
    {
        var wanted = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Data.Where(icon =>
        {
            var words = icon.Words.Split(' ');
            return wanted.All(w => words.Any(word => word.StartsWith(w, StringComparison.OrdinalIgnoreCase)));
        }).Select(icon => icon.Key);
    }
}
