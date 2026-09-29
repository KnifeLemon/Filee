// Donut toolbar editor: 1) choose a profile  2) arrange its presets on a live donut by drag & drop.
// The drag gestures themselves live in the view (ToolbarPage.axaml.cs); this class only mutates the model.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class ProfileItemViewModel(ToolbarProfile profile, ILocalizer loc) : ObservableObject
{
    public ToolbarProfile Profile { get; } = profile;

    [ObservableProperty] private string _name = loc.DisplayName(profile);

    public string Count => $"{Profile.PresetIds.Count}";

    public void Refresh(ILocalizer l)
    {
        Name = l.DisplayName(Profile);
        OnPropertyChanged(nameof(Count));
    }
}

/// <summary>A preset chip in the "available presets" palette.</summary>
public sealed record PaletteChip(string PresetId, string Label, string? Caption);

public sealed partial class ToolbarPageViewModel : ObservableObject
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly WindowService _windows;
    private bool _loading;

    public ToolbarPageViewModel(UserDataStore store, ILocalizer loc, WindowService windows)
    {
        _store = store;
        _loc = loc;
        _windows = windows;
        foreach (var profile in store.Profiles)
            Profiles.Add(new ProfileItemViewModel(profile, loc));

        _loading = true;
        OuterRadius = store.Settings.Donut.OuterRadius;
        HoleRatio = store.Settings.Donut.HoleRatio * 100;
        SliceOpacity = store.Settings.Donut.Opacity * 100;
        _loading = false;

        SelectedProfile = Profiles.FirstOrDefault();
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<PaletteChip> Palette { get; } = [];

    [ObservableProperty] private ProfileItemViewModel? _selectedProfile;
    [ObservableProperty] private IReadOnlyList<DonutItem> _donutItems = [];
    [ObservableProperty] private string _centerTitle = "";
    [ObservableProperty] private string _centerSubtitle = "";
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _extensions = "";
    [ObservableProperty] private bool _isFallback;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private double _outerRadius;
    [ObservableProperty] private double _holeRatio;
    [ObservableProperty] private double _sliceOpacity;

    private ToolbarProfile? Profile => SelectedProfile?.Profile;

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        _loading = true;
        ProfileName = value is null ? "" : _loc.DisplayName(value.Profile);
        Extensions = value is null ? "" : string.Join(", ", value.Profile.Extensions);
        IsFallback = value?.Profile.IsFallback == true;
        _loading = false;
        Message = null;
        Rebuild();
    }

    partial void OnProfileNameChanged(string value)
    {
        if (_loading || Profile is null || string.IsNullOrWhiteSpace(value) || value == _loc.DisplayName(Profile))
            return;
        Profile.Name = value.Trim();
        SelectedProfile!.Refresh(_loc);
        CenterTitle = value.Trim();
        _store.SaveLibrary();
    }

    partial void OnExtensionsChanged(string value)
    {
        if (_loading || Profile is null || Profile.IsFallback)
            return;
        Profile.Extensions = value
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Distinct()
            .ToList();
        _store.SaveLibrary();
    }

    partial void OnOuterRadiusChanged(double value) => SaveAppearance();
    partial void OnHoleRatioChanged(double value) => SaveAppearance();
    partial void OnSliceOpacityChanged(double value) => SaveAppearance();

    private void SaveAppearance()
    {
        if (_loading)
            return;
        _store.Settings.Donut.OuterRadius = Math.Round(OuterRadius);
        _store.Settings.Donut.HoleRatio = Math.Round(HoleRatio) / 100;
        _store.Settings.Donut.Opacity = Math.Round(SliceOpacity) / 100;
        _store.SaveSettings();
    }

    // ───────── Editing operations called by the view's drag & drop ─────────

    public int SliceCount => Profile?.PresetIds.Count ?? 0;

    /// <summary>Adds a palette preset at <paramref name="index"/> (clamped). Returns false when the donut is full.</summary>
    public bool Insert(string presetId, int index)
    {
        if (Profile is null || Profile.PresetIds.Contains(presetId))
            return false;
        if (Profile.PresetIds.Count >= ToolbarProfile.MaxSlices)
        {
            Message = _loc.Format("toolbar.max_reached", ToolbarProfile.MaxSlices);
            return false;
        }
        Profile.PresetIds.Insert(Math.Clamp(index, 0, Profile.PresetIds.Count), presetId);
        Commit();
        return true;
    }

    public void RemoveAt(int index)
    {
        if (Profile is null || index < 0 || index >= Profile.PresetIds.Count)
            return;
        Profile.PresetIds.RemoveAt(index);
        Commit();
    }

    /// <summary>Moves a slice onto another slice's position.</summary>
    public void Move(int from, int to)
    {
        if (Profile is null || from == to || from < 0 || from >= Profile.PresetIds.Count)
            return;
        var id = Profile.PresetIds[from];
        Profile.PresetIds.RemoveAt(from);
        Profile.PresetIds.Insert(Math.Clamp(to, 0, Profile.PresetIds.Count), id);
        Commit();
    }

    /// <summary>Opens the preset popup for the slice at <paramref name="index"/>.</summary>
    public async Task EditAsync(int index)
    {
        if (Profile is null || index < 0 || index >= Profile.PresetIds.Count)
            return;
        var preset = _store.FindPreset(Profile.PresetIds[index]);
        if (preset is not null && await _windows.EditPresetAsync(preset, _windows.Main))
            Commit();
    }

    /// <summary>Opens the preset popup for a palette chip.</summary>
    public async Task EditChipAsync(string presetId)
    {
        var preset = _store.FindPreset(presetId);
        if (preset is not null && await _windows.EditPresetAsync(preset, _windows.Main))
            Commit();
    }

    [RelayCommand]
    private async Task NewPreset()
    {
        var preset = new Preset { Name = _loc["toolbar.new_preset"], TargetFormat = "png" };
        if (!await _windows.EditPresetAsync(preset, _windows.Main))
            return;
        _store.Presets.Add(preset);
        if (Profile is not null && Profile.PresetIds.Count < ToolbarProfile.MaxSlices)
            Profile.PresetIds.Add(preset.Id);
        Commit();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new ToolbarProfile { Name = _loc["toolbar.profile_new"], PresetIds = ["to-pdf", "to-png"] };
        _store.Profiles.Insert(Math.Max(0, _store.Profiles.Count - 1), profile); // keep the fallback last
        _store.SaveLibrary();
        var item = new ProfileItemViewModel(profile, _loc);
        Profiles.Insert(Math.Max(0, Profiles.Count - 1), item);
        SelectedProfile = item;
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (Profile is null || Profile.IsFallback || _windows.Main is null)
            return;
        if (!await _windows.ConfirmAsync(_windows.Main, _loc["toolbar.delete_profile"] + "?", destructive: true))
            return;
        _store.Profiles.Remove(Profile);
        _store.SaveLibrary();
        Profiles.Remove(SelectedProfile!);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    private void Commit()
    {
        _store.SaveLibrary();
        SelectedProfile?.Refresh(_loc);
        Rebuild();
    }

    /// <summary>Rebuilds the donut slices and the palette from the model.</summary>
    private void Rebuild()
    {
        var profile = Profile;
        var used = profile?.PresetIds ?? [];
        DonutItems = used
            .Select(_store.FindPreset)
            .OfType<Preset>()
            .Select(p => new DonutItem { Id = p.Id, Label = _loc.DisplayName(p), Caption = Caption(p) })
            .ToList();

        Palette.Clear();
        foreach (var preset in _store.Presets.Where(p => !used.Contains(p.Id)))
        {
            var label = _loc.DisplayName(preset);
            var caption = Caption(preset);
            // "GIF  GIF" says nothing twice: only show the format when the name doesn't already.
            if (caption is not null && label.Contains(caption, StringComparison.OrdinalIgnoreCase))
                caption = null;
            Palette.Add(new PaletteChip(preset.Id, label, caption));
        }

        CenterTitle = profile is null ? "" : _loc.DisplayName(profile);
        CenterSubtitle = $"{used.Count} / {ToolbarProfile.MaxSlices}";
    }

    private string? Caption(Preset p) =>
        p.TargetFormat == BuiltInData.SameAsSource ? _loc["donut.same_format"] : FormatRegistry.FindById(p.TargetFormat)?.DisplayName;
}
