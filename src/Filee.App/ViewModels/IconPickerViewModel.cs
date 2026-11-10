// The icon picker of the preset and group editors: the slice icons by group, a search over their names and search
// words, and Default (the preset's own default icon) and None. Icon names are English (from their keys); the groups
// are translated.

using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

/// <summary>An icon in the picker.</summary>
public sealed class IconChoice(string key, bool isCurrent)
{
    public string Key { get; } = key;

    /// <summary>Its key in words, for the tooltip and screen readers: "image-edit-outline" → "Image edit outline".</summary>
    public string Name { get; } = IconPickerViewModel.NameOf(key);

    public Geometry? Data => SliceIcons.Geometry(Key);

    /// <summary>The preset has this icon now.</summary>
    public bool IsCurrent { get; } = isCurrent;
}

/// <summary>A group of icons in the picker, with its translated name.</summary>
public sealed record IconSection(string Name, IReadOnlyList<IconChoice> Icons);

public sealed partial class IconPickerViewModel : ObservableObject
{
    private readonly ILocalizer _loc;
    private readonly string? _current;
    private readonly Action<string?> _picked;

    /// <param name="current">The preset's <see cref="Preset.Icon"/>: null (its default), <see cref="Preset.NoIcon"/> or a key.</param>
    /// <param name="picked">Called with the choice: null for Default, <see cref="Preset.NoIcon"/> for None, else a key.</param>
    public IconPickerViewModel(ILocalizer loc, string? current, Action<string?> picked)
    {
        _loc = loc;
        _current = SliceIcons.Exists(current) || current == Preset.NoIcon ? current : null;
        _picked = picked;
        Rebuild();
    }

    /// <summary>Filters the icons: every word of it starts a word of an icon's name or search words.</summary>
    [ObservableProperty] private string _search = "";

    /// <summary>The groups that have an icon matching <see cref="Search"/>, in order.</summary>
    [ObservableProperty] private IReadOnlyList<IconSection> _sections = [];

    /// <summary>No icon matches the search.</summary>
    public bool IsEmpty => Sections.Count == 0;

    /// <summary>The preset shows its default icon (Icon unset).</summary>
    public bool IsDefault => _current is null;

    /// <summary>The preset shows no icon.</summary>
    public bool IsNone => _current == Preset.NoIcon;

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnSectionsChanged(IReadOnlyList<IconSection> value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(First));
    }

    private void Rebuild()
    {
        var found = SliceIcons.Search(Search).ToHashSet(StringComparer.Ordinal);
        Sections = SliceIcons.Categories
            .Select(c => new IconSection(_loc[CategoryKey(c.Category)],
                c.Keys.Where(found.Contains).Select(key => new IconChoice(key, key == _current)).ToList()))
            .Where(s => s.Icons.Count > 0)
            .ToList();
    }

    /// <summary>The first icon the search shows, picked by Enter in the search box.</summary>
    public IconChoice? First => Sections.FirstOrDefault()?.Icons[0];

    [RelayCommand]
    private void Pick(IconChoice icon) => _picked(icon.Key);

    [RelayCommand]
    private void PickDefault() => _picked(null);

    [RelayCommand]
    private void PickNone() => _picked(Preset.NoIcon);

    /// <summary>The name of a group ("icons.cat.files_and_folders").</summary>
    internal static string CategoryKey(SliceIconCategory category) => category switch
    {
        SliceIconCategory.FilesAndFolders => "icons.cat.files_and_folders",
        SliceIconCategory.Pictures => "icons.cat.pictures",
        SliceIconCategory.CropAndTransform => "icons.cat.crop_and_transform",
        SliceIconCategory.PdfAndPages => "icons.cat.pdf_and_pages",
        SliceIconCategory.VideoAndAudio => "icons.cat.video_and_audio",
        SliceIconCategory.TextAndData => "icons.cat.text_and_data",
        SliceIconCategory.ShareAndMove => "icons.cat.share_and_move",
        _ => "icons.cat.general",
    };

    /// <summary>An icon's key in words: "image-edit-outline" → "Image edit outline".</summary>
    internal static string NameOf(string key)
    {
        var words = key.Replace('-', ' ');
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
