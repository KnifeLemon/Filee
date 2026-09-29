// State of the live donut toolbar: which files are being converted and which presets are offered.

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.App.ViewModels;

public sealed partial class RadialViewModel(UserDataStore store, ILocalizer loc, PresetAvailability availability) : ObservableObject
{
    [ObservableProperty] private IReadOnlyList<DonutItem> _items = [];
    [ObservableProperty] private string _centerTitle = "";
    [ObservableProperty] private string? _centerSubtitle;
    [ObservableProperty] private double _outerRadius = 150;
    [ObservableProperty] private double _holeRatio = 0.42;
    [ObservableProperty] private double _sliceOpacity = 0.96;

    /// <summary>Files the donut will convert (empty while waiting for a drag to enter).</summary>
    public IReadOnlyList<string> Files { get; private set; } = [];

    /// <summary>Presets in slice order.</summary>
    public IReadOnlyList<Preset> Presets { get; private set; } = [];

    /// <summary>True when opened with known files (keyboard, context menu, drop zone): slices are clicked.</summary>
    public bool IsClickMode { get; private set; }

    public bool HasFiles => Files.Count > 0;

    /// <summary>Drag mode before any file entered: an empty ring saying "Bring files here".</summary>
    public void ResetForDrag()
    {
        IsClickMode = false;
        Files = [];
        Presets = [];
        ApplyAppearance();
        Items = [];
        CenterTitle = loc["donut.center_drag"];
        CenterSubtitle = null;
    }

    /// <summary>Fills the donut for the given files.</summary>
    public void Load(IReadOnlyList<string> files, bool clickMode)
    {
        IsClickMode = clickMode;
        Files = files;
        ApplyAppearance();

        var profile = ProfileSelector.Select(store.Profiles, files);
        var formats = ProfileSelector.DetectFormats(files);
        Presets = (profile?.PresetIds ?? [])
            .Select(store.FindPreset)
            .OfType<Preset>()
            .ToList();

        Items = Presets.Select(p =>
        {
            var reason = availability.Check(p, formats);
            return new DonutItem
            {
                Id = p.Id,
                Label = loc.DisplayName(p),
                Caption = p.TargetFormat == BuiltInData.SameAsSource
                    ? string.Join("/", formats.Select(f => f.DisplayName).Take(2))
                    : FormatRegistry.FindById(p.TargetFormat)?.DisplayName,
                IsEnabled = reason is null,
                DisabledReason = reason,
            };
        }).ToList();

        CenterTitle = formats.Count == 1
            ? loc.Format("donut.files_same", files.Count, formats[0].DisplayName)
            : loc.Format("donut.files_mixed", files.Count);
        CenterSubtitle = clickMode ? loc["donut.close_hint"] : null;
    }

    /// <summary>Updates the centre hint for the emphasised slice.</summary>
    public void ShowHint(int index, bool centerHot)
    {
        if (centerHot)
            CenterSubtitle = loc["donut.cancel"];
        else if (index >= 0 && index < Items.Count)
            CenterSubtitle = Items[index].IsEnabled
                ? loc.Format(IsClickMode ? "donut.click_to" : "donut.drop_to", Items[index].Label)
                : Items[index].DisabledReason;
        else
            CenterSubtitle = IsClickMode ? loc["donut.close_hint"] : null;
    }

    private void ApplyAppearance()
    {
        OuterRadius = Math.Clamp(store.Settings.Donut.OuterRadius, 100, 240);
        HoleRatio = store.Settings.Donut.HoleRatio;
        SliceOpacity = store.Settings.Donut.Opacity;
    }
}
