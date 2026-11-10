// The slice-label fields of the preset and group editors: the slice's icon (picked in the icon picker), a short name
// for the donut, what the slice shows, and which way its name runs. Shared by PresetEditorViewModel and
// GroupEditorViewModel.

using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Controls;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

public sealed partial class SliceLabelFields : ObservableObject
{
    private readonly ILocalizer _loc;
    private readonly Preset _preset;
    private string _name;

    /// <param name="preset">The preset or group being edited, for its current values and its default icon.</param>
    public SliceLabelFields(Preset preset, ILocalizer loc)
    {
        _loc = loc;
        _preset = preset;
        _name = loc.DisplayName(preset);
        _icon = SliceIcons.Exists(preset.Icon) || preset.Icon == Preset.NoIcon ? preset.Icon : null;
        _shortName = preset.ShortName ?? "";
        ShowChoices = [new Choice<LabelShow?>(null, loc["presets.show_donut"]),
            .. ToolbarPageViewModel.LabelShows.Select(v => new Choice<LabelShow?>(v, loc[ToolbarPageViewModel.LabelShowKey(v)]))];
        _show = ShowChoices.FirstOrDefault(c => c.Value == preset.Show) ?? ShowChoices[0];
        DirectionChoices = [new Choice<LabelDirection?>(null, loc["labels.auto"]),
            new Choice<LabelDirection?>(LabelDirection.Straight, loc["labels.dir_straight"]),
            new Choice<LabelDirection?>(LabelDirection.OutFromCentre, loc["labels.dir_out"]),
            new Choice<LabelDirection?>(LabelDirection.AlongRing, loc["labels.dir_ring"])];
        _direction = DirectionChoices.FirstOrDefault(c => c.Value == preset.Direction) ?? DirectionChoices[0];
    }

    /// <summary>The slice's own icon: null for its default, <see cref="Preset.NoIcon"/> for none, else a key.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconData), nameof(IconText), nameof(IconPicker))]
    private string? _icon;

    /// <summary>The icon the slice shows with this choice (its default for Default), or null for none.</summary>
    public Geometry? IconData => SliceIcons.Geometry(Icon == Preset.NoIcon ? null : SliceIcons.Exists(Icon) ? Icon : SliceIcons.DefaultFor(_preset));

    /// <summary>What the Icon button says: "Default", "None", or the icon's name.</summary>
    public string IconText => Icon is null ? _loc["presets.icon_default"]
        : Icon == Preset.NoIcon ? _loc["presets.icon_none"]
        : IconPickerViewModel.NameOf(Icon);

    /// <summary>The picker in the Icon button's flyout; a pick sets <see cref="Icon"/>.</summary>
    public IconPickerViewModel IconPicker => new(_loc, Icon, key => Icon = key);

    /// <summary>The name on the donut instead of the compact name; blank for that.</summary>
    [ObservableProperty] private string _shortName;

    /// <summary>The compact name the donut shows without a short name, as the Short name box's placeholder.</summary>
    public string ShortNamePlaceholder
    {
        get
        {
            var probe = _preset.Clone();
            probe.ShortName = null;
            if (_name.Trim() != _loc.DisplayName(_preset))
                probe.Name = _name.Trim();
            return _loc.DonutName(probe);
        }
    }

    /// <summary>Use the donut's setting, Auto, Icon &amp; text, Text only, Icon only.</summary>
    public IReadOnlyList<Choice<LabelShow?>> ShowChoices { get; }

    [ObservableProperty] private Choice<LabelShow?> _show;

    partial void OnShowChanged(Choice<LabelShow?>? oldValue, Choice<LabelShow?> newValue) =>
        Choices.Restore(oldValue, newValue, v => Show = v);

    /// <summary>Auto, Straight, Out from the centre, Along the ring.</summary>
    public IReadOnlyList<Choice<LabelDirection?>> DirectionChoices { get; }

    [ObservableProperty] private Choice<LabelDirection?> _direction;

    partial void OnDirectionChanged(Choice<LabelDirection?>? oldValue, Choice<LabelDirection?> newValue) =>
        Choices.Restore(oldValue, newValue, v => Direction = v);

    /// <summary>The editor's name box changed: the placeholder follows it.</summary>
    public void NameChanged(string name)
    {
        _name = name;
        OnPropertyChanged(nameof(ShortNamePlaceholder));
    }

    /// <summary>Writes the fields into <paramref name="preset"/>.</summary>
    public void Apply(Preset preset)
    {
        preset.Icon = Icon;
        preset.ShortName = string.IsNullOrWhiteSpace(ShortName) ? null : ShortName.Trim();
        preset.Show = Show.Value;
        preset.Direction = Direction.Value;
    }
}
