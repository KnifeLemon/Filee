// Donut toolbar editor: 1) choose a profile  2) arrange its presets on a live donut by drag & drop, on the front face
// or on the back (Tab flips the open donut to it), picked with the Front / Back switch above the donut.
// The drag gestures themselves live in the view (ToolbarPage.axaml.cs); this class only mutates the model.
// A profile is either normal (extension chips, ExtensionTagsViewModel) or for mixed selections (check list,
// MixedExtensionsViewModel). Normal profiles are kept before the mixed ones, so a new mixed profile never takes over
// the catch-all role of the built-in "Mixed files" donut (see ProfileSelector). Move up / Move down (Alt+↑ / Alt+↓)
// reorder a profile among its own kind, which decides the profile that opens for a shared extension (#522).

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels.Pages;

public sealed partial class ProfileItemViewModel(ToolbarProfile profile, ILocalizer loc) : ObservableObject
{
    public ToolbarProfile Profile { get; } = profile;

    private ILocalizer _loc = loc;

    [ObservableProperty] private string _name = loc.DisplayName(profile);

    /// <summary>Slices on the front, and on the back when it has any (#425): "8" or "8 · 3".</summary>
    public string Count => Profile.BackPresetIds is { Count: > 0 } back ? $"{Profile.PresetIds.Count} · {back.Count}" : $"{Profile.PresetIds.Count}";

    /// <summary>What the two numbers are: "Front 8 · Back 3".</summary>
    public string CountTip =>
        $"{_loc["toolbar.face_front"]} {Profile.PresetIds.Count} · {_loc["toolbar.face_back"]} {Profile.BackPresetIds?.Count ?? 0}";

    /// <summary>A donut for mixed selections (marked in the list).</summary>
    public bool IsMixed => Profile.IsFallback;

    public void Refresh(ILocalizer l)
    {
        _loc = l;
        Name = l.DisplayName(Profile);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountTip));
        OnPropertyChanged(nameof(IsMixed));
    }
}

/// <summary>A preset chip in the "available presets" palette.</summary>
public enum PaletteTab { All, Image, Document, Media, Edit, Model3DCad, Map, Archive, Font }

public sealed record PaletteTabItem(PaletteTab Id, string Label);

public sealed record PaletteChip(string PresetId, string Label, string? Caption, string? Reason = null,
    PresetUse Use = PresetUse.Usable)
{
    public bool IsDimmed => Use != PresetUse.Usable;
    public bool CanAdd => Use != PresetUse.Unusable;
    public object? Tip { get; init; }
    public bool IsGroup { get; init; }
}

public sealed partial class ToolbarPageViewModel : ObservableObject, IDisposable
{
    internal static readonly (PaletteTab Id, string LabelKey, FormatCategory[] Categories)[] TabDefinitions =
    [
        (PaletteTab.All, "toolbar.tab.all", []),
        (PaletteTab.Image, "format.category.Image", [FormatCategory.Image, FormatCategory.Vector]),
        (PaletteTab.Document, "format.category.Document", [FormatCategory.Pdf, FormatCategory.Document,
            FormatCategory.Spreadsheet, FormatCategory.Presentation, FormatCategory.Hwp, FormatCategory.Text,
            FormatCategory.Ebook, FormatCategory.Data]),
        (PaletteTab.Media, "toolbar.tab.media", [FormatCategory.Video, FormatCategory.Audio, FormatCategory.Subtitle]),
        (PaletteTab.Edit, "toolbar.tab.edit", []),
        (PaletteTab.Model3DCad, "toolbar.tab.model3d_cad", [FormatCategory.Model3D, FormatCategory.Cad]),
        (PaletteTab.Map, "format.category.Geo", [FormatCategory.Geo]),
        (PaletteTab.Archive, "format.category.Archive", [FormatCategory.Archive]),
        (PaletteTab.Font, "format.category.Font", [FormatCategory.Font]),
    ];

    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly WindowService _windows;
    private readonly PresetAvailability _availability;
    private readonly ConverterCatalog _catalog;
    private readonly ILogger<ToolbarPageViewModel> _log;
    private sealed class PaletteJudging
    {
        public Dictionary<string, PresetJudgement> Judged { get; } = [];
        public PresetAvailability.PaletteSession? Session { get; set; }
    }

    private readonly Dictionary<string, PaletteJudging> _judgingByProfile = [];
    private readonly Dictionary<string, PresetFormatTip> _tipsByPreset = [];
    private object? _pass;
    private bool _disposed;
    private bool _savingOwnLibrary;
    private static readonly TimeSpan JudgeSlice = TimeSpan.FromMilliseconds(8);
    private bool _loading;
    private bool _repositioning;

    /// <summary>
    /// The selected profile is being moved in the list, which drops the list's selection for a moment: the same profile
    /// stays selected.
    /// </summary>
    internal bool IsRepositioning => _repositioning;
    private string? _newPresetToSelect;
    private PaletteTab? _pendingTabRestore;
    private bool _updatingTabs;

    public ToolbarPageViewModel(UserDataStore store, ILocalizer loc, WindowService windows,
        PresetAvailability availability, ConverterCatalog catalog, ILogger<ToolbarPageViewModel> log)
    {
        _store = store;
        _loc = loc;
        _windows = windows;
        _availability = availability;
        _catalog = catalog;
        _log = log;
        // A profile is found by its name or by an extension it handles ("png" finds the photo donut).
        VisibleProfiles = new FilteredList<ProfileItemViewModel>(Profiles, p => [p.Name, .. p.Profile.Extensions]);
        foreach (var profile in store.Profiles)
            Profiles.Add(new ProfileItemViewModel(profile, loc));

        _loading = true;
        OuterRadius = store.Settings.Donut.OuterRadius;
        HoleRatio = store.Settings.Donut.HoleRatio * 100;
        SliceOpacity = Math.Clamp(store.Settings.Donut.Opacity, DonutSettings.MinOpacity, 1) * 100;
        ShowCaptions = store.Settings.Donut.ShowCaptions;
        GroupRingSize = DonutSettings.ClampGroupRingSize(store.Settings.Donut.GroupRingSize) * 100;
        GroupRingSpread = DonutSettings.ClampGroupRingSpread(store.Settings.Donut.GroupRingSpread) * 100;
        SliceLabelChoices = [.. LabelShows.Select(v => new Choice<LabelShow>(v, loc[LabelShowKey(v)]))];
        SliceLabels = SliceLabelChoices[0];
        _loading = false;

        SelectedProfile = Profiles.FirstOrDefault();

        // How the open donut flips, as set in Settings → Shortcuts: "Flip the open donut: Tab / mouse wheel".
        var ways = new List<string>();
        if (store.Settings.Donut.EffectiveFlipKey(store.Settings.Triggers) is { } key)
            ways.Add(KeyNames.Display(key));
        if (store.Settings.Donut.FlipWithWheel)
            ways.Add(loc["toolbar.face_hint_wheel"]);
        FaceHint = ways.Count == 0 ? loc["toolbar.face_hint_off"] : loc.Format("toolbar.face_hint", string.Join(" / ", ways));
        _catalog.Changed += OnCatalogChanged;
        _store.LibraryChanged += OnLibraryChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        DonutLookSave.Flush();
        _catalog.Changed -= OnCatalogChanged;
        _store.LibraryChanged -= OnLibraryChanged;
        StopJudging();
        _judgingByProfile.Clear();
        _tipsByPreset.Clear();
    }

    private void StopJudging()
    {
        _pass = null;
    }

    private void ForgetJudgements(string? profileId = null)
    {
        StopJudging();
        if (profileId is null)
        {
            _judgingByProfile.Clear();
            _tipsByPreset.Clear();
        }
        else
            _judgingByProfile.Remove(profileId);
    }

    private void OnCatalogChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => { if (_disposed) return; LibraryVersion++; ForgetJudgements(); Rebuild(); });

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(InvalidateExternalLibrary);
            return;
        }
        if (!_savingOwnLibrary)
            InvalidateExternalLibrary();
    }

    private void InvalidateExternalLibrary()
    {
        if (_disposed)
            return;
        LibraryVersion++;
        _pendingTabRestore ??= SelectedTab is { Id: not PaletteTab.All } tab ? tab.Id : null;
        ForgetJudgements();
        Rebuild();
    }

    private T SaveOwnLibrary<T>(Func<T> action)
    {
        var previous = _savingOwnLibrary;
        _savingOwnLibrary = true;
        LibraryVersion++;
        try
        {
            DuringOwnSave?.Invoke();
            return action();
        }
        finally { _savingOwnLibrary = previous; }
    }

    private void SaveOwnLibrary() => SaveOwnLibrary(() => { _store.SaveLibrary(); return true; });

    internal Action? DuringOwnSave { get; set; }

    internal Action<string, string>? JudgementObserved { get; set; }
    internal bool HasJudgement(string profileId, string presetId) =>
        _judgingByProfile.TryGetValue(profileId, out var judging) && judging.Judged.ContainsKey(presetId);

    internal static PaletteTab TabOf(Preset preset)
    {
        if (preset.IsTool || preset.TargetFormat == BuiltInData.SameAsSource)
            return PaletteTab.Edit;
        var category = FormatRegistry.FindById(preset.TargetFormat)?.Category;
        return category is { } value
            ? TabDefinitions.First(t => t.Categories.Contains(value)).Id
            : PaletteTab.All;
    }

    /// <summary>Under the Front / Back switch: what flips the open donut (or that nothing does).</summary>
    public string FaceHint { get; }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<PaletteChip> Palette { get; } = [];
    public ObservableCollection<PaletteTabItem> Tabs { get; } = [];
    public ObservableCollection<PaletteChip> VisiblePalette { get; } = [];

    /// <summary>The profiles the search box above the list lets through.</summary>
    public FilteredList<ProfileItemViewModel> VisibleProfiles { get; }

    [ObservableProperty] private string _profileSearch = "";
    [ObservableProperty] private string _paletteSearch = "";
    [ObservableProperty] private PaletteTabItem? _selectedTab;
    [ObservableProperty] private bool _showTabRow;

    partial void OnProfileSearchChanged(string value)
    {
        VisibleProfiles.Query = value;
        NotifyMoveChanged();
    }
    partial void OnPaletteSearchChanged(string value)
    {
        _pendingTabRestore = null;
        Rebuild();
    }
    partial void OnSelectedTabChanged(PaletteTabItem? value)
    {
        if (!_updatingTabs)
            _pendingTabRestore = null;
        RefreshVisiblePalette();
    }

    [ObservableProperty] private ProfileItemViewModel? _selectedProfile;
    [ObservableProperty] private IReadOnlyList<DonutItem> _donutItems = [];
    [ObservableProperty] private string _centerTitle = "";
    [ObservableProperty] private string _centerSubtitle = "";
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string? _message;

    /// <summary>The selected profile is for mixed selections (toggle in the profile card).</summary>
    [ObservableProperty] private bool _isMixed;

    /// <summary>False for the last mixed-selection profile: Filee always needs one.</summary>
    [ObservableProperty] private bool _canChangeMixed;

    [ObservableProperty] private bool _canDelete;

    /// <summary>The selected profile is a built-in donut, which "Reset to default" can put back (a new one can't).</summary>
    [ObservableProperty] private bool _canReset;

    /// <summary>Extension chips of a normal profile (null for a mixed one).</summary>
    [ObservableProperty] private ExtensionTagsViewModel? _extensionTags;

    /// <summary>The donut shows and edits the back face of the profile (the Front / Back switch).</summary>
    [ObservableProperty] private bool _editingBack;

    /// <summary>
    /// The group open in the editor (a group slice clicked), or null: the donut then shows and edits the presets it
    /// fans out, as a folder opens, and the Front / Back switch makes way for a way back.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingGroup))]
    private Preset? _openGroup;

    /// <summary>A group is open in the editor (<see cref="OpenGroup"/>).</summary>
    public bool IsEditingGroup => OpenGroup is not null;

    /// <summary>Where the way back out of a group leads: the donut's name.</summary>
    public string GroupBackText => Profile is { } profile ? _loc.DisplayName(profile) : "";

    /// <summary>The way back's name for screen readers: "Back to Images".</summary>
    public string GroupBackName => _loc.Format("toolbar.group_back", GroupBackText);

    /// <summary>
    /// The donut's size and look are being adjusted (Edit Donut Size): the editor makes way for their settings, and the
    /// donut shows on screen at its real size.
    /// </summary>
    [ObservableProperty] private bool _isAdjustingLook;

    /// <summary>
    /// A group of the selected donut in the Group shown picker: its preset, its name and the face it's on.
    /// </summary>
    public sealed record RingGroupChoice(string Id, string Name, bool Back)
    {
        public override string ToString() => Name;
    }

    /// <summary>The groups on the selected donut, front face first, for Edit Donut Size → Group shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGroupPicker), nameof(ShowsExampleRing))]
    private IReadOnlyList<RingGroupChoice> _ringGroups = [];

    /// <summary>
    /// The donut on screen holds a group's ring open: a ring setting was changed, or a group picked, while Edit Donut
    /// Size is open. Hovering alone would fold the ring as the pointer goes back to the sliders.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsExampleRing))]
    private bool _showsRing;

    /// <summary>
    /// The group the donut on screen holds open (<see cref="ShowsRing"/>): the first one until another is picked.
    /// </summary>
    [ObservableProperty] private RingGroupChoice? _shownGroup;

    /// <summary>The Group shown picker: only when there is a choice.</summary>
    public bool ShowsGroupPicker => IsAdjustingLook && RingGroups.Count >= 2;

    /// <summary>A donut with no group shows an example ring, so the ring settings still show what they do.</summary>
    public bool ShowsExampleRing => IsAdjustingLook && ShowsRing && RingGroups.Count == 0;

    partial void OnIsAdjustingLookChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsGroupPicker));
        OnPropertyChanged(nameof(ShowsExampleRing));
        if (!value)
            (ShowsRing, ShownGroup) = (false, null);
    }

    partial void OnShowsRingChanged(bool value)
    {
        if (value)
            ShownGroup ??= RingGroups.FirstOrDefault();
    }

    partial void OnShownGroupChanged(RingGroupChoice? value)
    {
        if (value is not null && IsAdjustingLook)
            ShowsRing = true;
    }

    /// <summary>A group was clicked on the donut on screen: it is the one held open from now on.</summary>
    public void HoldGroup(string id, bool back)
    {
        if (!IsAdjustingLook)
            return;
        ShownGroup = RingGroups.FirstOrDefault(g => g.Id == id && g.Back == back)
                     ?? RingGroups.FirstOrDefault(g => g.Id == id) ?? ShownGroup;
    }

    /// <summary>The groups on the selected donut (<see cref="RingGroups"/>), kept when they haven't changed.</summary>
    private void RefreshRingGroups()
    {
        var groups = Profile is not { } profile ? [] : RingGroupsOf(profile);
        if (!groups.SequenceEqual(RingGroups))
            RingGroups = groups;
        if (ShownGroup is { } shown && !groups.Contains(shown))
            ShownGroup = null;
        if (ShowsRing)
            ShownGroup ??= groups.FirstOrDefault();
    }

    // The groups of both faces with a ring to fan out, judged as the donut on screen fills them (its own field stays,
    // RadialViewModel.LoadPreview): not one whose presets are deleted, or all of a turned-off field.
    private List<RingGroupChoice> RingGroupsOf(ToolbarProfile profile)
    {
        var off = RadialViewModel.TurnedOffBesides(_store.Settings.TurnedOffFields, RadialViewModel.PreviewFormats(profile));
        var fans = _store.Presets.Where(p => FormatFields.IsShown(p.TargetFormat, off)).Select(p => p.Id).ToHashSet();
        return Groups(profile.PresetIds, false).Concat(Groups(profile.BackPresetIds ?? [], true)).ToList();

        IEnumerable<RingGroupChoice> Groups(IEnumerable<string> ids, bool back) => ids
            .Select(_store.FindPreset)
            .OfType<Preset>()
            .Where(p => p.IsGroup && p.SubPresetIds.Any(fans.Contains))
            .Select(p => new RingGroupChoice(p.Id, back ? $"{_loc.DisplayName(p)} · {_loc["toolbar.face_back"]}" : _loc.DisplayName(p), back));
    }

    /// <summary>The donut whose labels the panel's lower part adjusts: "This donut: Images".</summary>
    public string ThisDonutText => _loc.Format("toolbar.look_this", GroupBackText);

    [RelayCommand]
    private void AdjustLook()
    {
        CloseGroup();
        IsAdjustingLook = true;
    }

    [RelayCommand]
    private void DoneAdjustingLook() => IsAdjustingLook = false;

    /// <summary>The front face is edited: the other half of the switch.</summary>
    public bool EditingFront
    {
        get => !EditingBack;
        set => EditingBack = !value;
    }

    /// <summary>Slices on the front face, for the switch.</summary>
    public int FrontCount => Profile?.PresetIds.Count ?? 0;

    /// <summary>Slices on the back face, for the switch.</summary>
    public int BackCount => Profile?.BackPresetIds?.Count ?? 0;

    /// <summary>Check list of a mixed-selection profile (null for a normal one).</summary>
    [ObservableProperty] private MixedExtensionsViewModel? _mixedExtensions;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OuterRadiusText))]
    private double _outerRadius;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoleRatioText))]
    private double _holeRatio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SliceOpacityText))]
    private double _sliceOpacity;

    /// <summary>
    /// The size as its slider's value shows it: the donut's radius in pixels, as it is stored ("150 px").
    /// </summary>
    public string OuterRadiusText => $"{Math.Round(OuterRadius).ToString(System.Globalization.CultureInfo.CurrentCulture)} px";

    /// <summary>The hole as its slider's value shows it: "42 %".</summary>
    public string HoleRatioText => Percent(HoleRatio);

    /// <summary>The opacity as its slider's value shows it: "90 %".</summary>
    public string SliceOpacityText => Percent(SliceOpacity);

    /// <summary>The output format under the slices' names (Edit Donut Size → Show output format).</summary>
    [ObservableProperty] private bool _showCaptions;

    /// <summary>How large a group's ring is, in percent (Edit Donut Size → Group ring size).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupRingSizeText))]
    private double _groupRingSize = 100;

    /// <summary>How wide a group's ring spreads, in percent (Edit Donut Size → Group ring spread).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupRingSpreadText))]
    private double _groupRingSpread = 100;

    /// <summary>The ring size as its slider shows it: "125 %".</summary>
    public string GroupRingSizeText => Percent(GroupRingSize);

    /// <summary>The ring spread as its slider shows it: "150 %".</summary>
    public string GroupRingSpreadText => Percent(GroupRingSpread);

    private static string Percent(double value) =>
        (Math.Round(value) / 100).ToString("P0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// How large the selected donut's slice names and icons are, in percent (<see cref="ToolbarProfile.LabelScale"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelScaleText))]
    private double _labelScale = 100;

    /// <summary>The Label scale as the slider's value shows it: "125 %".</summary>
    public string LabelScaleText => Percent(LabelScale);

    /// <summary>
    /// What the selected donut's slices show when their preset doesn't say (<see cref="ToolbarProfile.LabelShow"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultShow))]
    private Choice<LabelShow> _sliceLabels = null!;

    /// <summary>Auto, Icon &amp; text, Text only, Icon only.</summary>
    public IReadOnlyList<Choice<LabelShow>> SliceLabelChoices { get; private set; } = [];

    /// <summary>The preview donut's <see cref="DonutMenu.DefaultShow"/>.</summary>
    public LabelShow DefaultShow => SliceLabels?.Value ?? LabelShow.Auto;

    private ToolbarProfile? Profile => SelectedProfile?.Profile;

    /// <summary>The preset ids being edited: the open group's, or the face's (null without a profile).</summary>
    private List<string>? Slices => Profile is not { } profile ? null
        : OpenGroup is { } group ? group.SubPresetIds
        : EditingBack ? (profile.BackPresetIds ??= []) : profile.PresetIds;

    /// <summary>How many presets <see cref="Slices"/> may hold.</summary>
    private int Capacity => OpenGroup is null ? ToolbarProfile.MaxSlices : Preset.MaxSubPresets;

    /// <summary>
    /// Where the open group is on the face being edited (-1 with none open, or once it has left the face): the donut on
    /// screen opens it there.
    /// </summary>
    public int OpenGroupIndex => OpenGroup is { } group && Profile is { } profile
        ? (EditingBack ? profile.BackPresetIds ?? [] : profile.PresetIds).IndexOf(group.Id) : -1;

    /// <summary>
    /// Opens the group at <paramref name="index"/> on the face being edited, to edit what it fans out.
    /// </summary>
    public void OpenGroupAt(int index)
    {
        if (OpenGroup is not null || Slices is not { } slices || index < 0 || index >= slices.Count
            || _store.FindPreset(slices[index]) is not { IsGroup: true } group)
            return;
        Message = null;
        OpenGroup = group;
        // A search typed for the donut's presets would hide the group's.
        PaletteSearch = "";
        Rebuild();
    }

    /// <summary>Back from the open group to the whole donut.</summary>
    [RelayCommand]
    private void CloseGroup()
    {
        if (OpenGroup is null)
            return;
        Message = null;
        OpenGroup = null;
        Rebuild();
    }

    partial void OnEditingBackChanged(bool value)
    {
        OnPropertyChanged(nameof(EditingFront));
        Message = null;
        OpenGroup = null;
        Rebuild();
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        // The list box briefly drops its selection while the selected profile is moved (see Reposition).
        if (_repositioning)
            return;
        NotifyMoveChanged();
        _loading = true;
        ProfileName = value is null ? "" : _loc.DisplayName(value.Profile);
        _loading = false;
        Message = null;
        OpenGroup = null;
        OnPropertyChanged(nameof(GroupBackText));
        OnPropertyChanged(nameof(GroupBackName));
        OnPropertyChanged(nameof(ThisDonutText));
        // A search typed for the last donut's presets would hide this one's (#427).
        PaletteSearch = "";
        RebuildProfileEditor();
        StopJudging();
        _newPresetToSelect = null;
        _pendingTabRestore = null;
        SelectedTab = null;
        Rebuild();
    }

    partial void OnProfileNameChanged(string value)
    {
        if (_loading || Profile is null || string.IsNullOrWhiteSpace(value) || value == _loc.DisplayName(Profile))
            return;
        Profile.Name = value.Trim();
        SelectedProfile!.Refresh(_loc);
        OnPropertyChanged(nameof(GroupBackText));
        OnPropertyChanged(nameof(GroupBackName));
        OnPropertyChanged(nameof(ThisDonutText));
        if (OpenGroup is null)
            CenterTitle = value.Trim();
        SaveOwnLibrary();
    }

    partial void OnIsMixedChanged(bool value)
    {
        if (_loading || SelectedProfile is not { } item || item.Profile.IsFallback == value)
            return;
        if (!value && !CanChangeMixed)
        {
            RebuildProfileEditor(); // the last mixed profile stays mixed
            return;
        }
        // The extensions are kept either way: chips become checks and back, so switching is harmless.
        item.Profile.IsFallback = value;
        Reposition(item);
        SaveOwnLibrary();
        item.Refresh(_loc);
        RebuildProfileEditor();
    }

    /// <summary>Normal profiles first, then the mixed ones; a profile that switches kind moves to the border.</summary>
    private void Reposition(ProfileItemViewModel item)
    {
        var profile = item.Profile;
        _store.Profiles.Remove(profile);
        var index = profile.IsFallback ? -1 : _store.Profiles.FindIndex(p => p.IsFallback);
        index = index < 0 ? _store.Profiles.Count : index;
        _store.Profiles.Insert(index, profile);

        _repositioning = true;
        try
        {
            Profiles.Move(Profiles.IndexOf(item), index);
            SelectedProfile = item;
        }
        finally
        {
            _repositioning = false;
        }
        NotifyMoveChanged();
    }

    /// <summary>The profile the selected one would pass going <paramref name="direction"/>, among those the search shows.</summary>
    private ToolbarProfile? MoveNeighbor(int direction) =>
        Profile is { } profile
            ? ProfileOrder.Neighbor(_store.Profiles, profile, direction, p => VisibleProfiles.Any(v => v.Profile == p))
            : null;

    private bool CanMoveProfileUp() => MoveNeighbor(-1) is not null;
    private bool CanMoveProfileDown() => MoveNeighbor(1) is not null;

    /// <summary>
    /// Moves the selected profile above the profile of its kind shown above it: the higher of two profiles with the
    /// same extension opens for it (#522). With a search, it passes the one the list shows, not a hidden one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveProfileUp))]
    private void MoveProfileUp() => MoveProfile(-1);

    /// <summary>Moves the selected profile below the profile of its kind shown below it.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveProfileDown))]
    private void MoveProfileDown() => MoveProfile(1);

    private void MoveProfile(int direction)
    {
        if (SelectedProfile is not { } item
            || ProfileOrder.Move(_store.Profiles, item.Profile, direction, p => VisibleProfiles.Any(v => v.Profile == p)) is not { } move)
            return;
        _repositioning = true;
        try
        {
            Profiles.Move(move.From, move.To);
            SelectedProfile = item;
        }
        finally
        {
            _repositioning = false;
        }
        SaveOwnLibrary();
        // The remarks under the extensions say which profile comes first; that just changed.
        RebuildProfileEditor();
        NotifyMoveChanged();
    }

    private void NotifyMoveChanged()
    {
        MoveProfileUpCommand.NotifyCanExecuteChanged();
        MoveProfileDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Shows the chips or the check list for the selected profile.</summary>
    private void RebuildProfileEditor()
    {
        var profile = Profile;
        var mixedCount = _store.Profiles.Count(p => p.IsFallback);
        _loading = true;
        IsMixed = profile?.IsFallback == true;
        _loading = false;
        CanChangeMixed = profile is not null && !(profile.IsFallback && mixedCount <= 1);
        CanDelete = CanChangeMixed;
        CanReset = profile is not null && _store.CanResetProfile(profile);
        _loading = true;
        LabelScale = ToolbarProfile.ClampLabelScale(profile?.LabelScale ?? 1) * 100;
        SliceLabels = SliceLabelChoices.First(c => c.Value == (profile?.LabelShow ?? LabelShow.Auto));
        _loading = false;
        ExtensionTags = profile is { IsFallback: false } ? new ExtensionTagsViewModel(profile, _store.Profiles, _loc, OnExtensionsSaved) : null;
        MixedExtensions = profile is { IsFallback: true } ? new MixedExtensionsViewModel(profile, _store.Profiles, _loc, OnExtensionsSaved) : null;
    }

    private void OnExtensionsSaved()
    {
        SaveOwnLibrary();
        ForgetJudgements(Profile?.Id);
        Rebuild();
    }

    partial void OnOuterRadiusChanged(double value) => SaveAppearance();
    partial void OnHoleRatioChanged(double value) => SaveAppearance();
    partial void OnSliceOpacityChanged(double value) => SaveAppearance();
    partial void OnShowCaptionsChanged(bool value) => SaveAppearance();
    partial void OnGroupRingSizeChanged(double value) => RingChanged();
    partial void OnGroupRingSpreadChanged(double value) => RingChanged();

    // A ring setting changed: saved, and the donut on screen holds a group open so the change shows.
    private void RingChanged()
    {
        SaveAppearance();
        if (IsAdjustingLook && !_loading)
            ShowsRing = true;
    }

    /// <summary>The four Show modes, in the order the combo boxes list them.</summary>
    internal static readonly LabelShow[] LabelShows = [LabelShow.Auto, LabelShow.IconAndText, LabelShow.TextOnly, LabelShow.IconOnly];

    /// <summary>The name of Show mode <paramref name="show"/> ("labels.icon_text").</summary>
    internal static string LabelShowKey(LabelShow show) => show switch
    {
        LabelShow.IconAndText => "labels.icon_text",
        LabelShow.TextOnly => "labels.text_only",
        LabelShow.IconOnly => "labels.icon_only",
        _ => "labels.auto",
    };

    partial void OnLabelScaleChanged(double value)
    {
        if (_loading || Profile is not { } profile)
            return;
        var scale = ToolbarProfile.ClampLabelScale(value / 100);
        if (scale == profile.LabelScale)
            return;
        profile.LabelScale = scale;
        SaveOwnLibrary();
    }

    partial void OnSliceLabelsChanged(Choice<LabelShow>? oldValue, Choice<LabelShow> newValue)
    {
        if (Choices.Restore(oldValue, newValue, v => SliceLabels = v) || _loading || Profile is not { } profile)
            return;
        // Auto is the default: left unset, as on a donut that never chose.
        var show = newValue.Value == LabelShow.Auto ? (LabelShow?)null : newValue.Value;
        if (show == profile.LabelShow)
            return;
        profile.LabelShow = show;
        SaveOwnLibrary();
    }

    private void SaveAppearance()
    {
        if (_loading)
            return;
        _store.Settings.Donut.OuterRadius = Math.Round(OuterRadius);
        _store.Settings.Donut.HoleRatio = Math.Round(HoleRatio) / 100;
        _store.Settings.Donut.Opacity = Math.Round(SliceOpacity) / 100;
        _store.Settings.Donut.ShowCaptions = ShowCaptions;
        _store.Settings.Donut.GroupRingSize = DonutSettings.ClampGroupRingSize(GroupRingSize / 100);
        _store.Settings.Donut.GroupRingSpread = DonutSettings.ClampGroupRingSpread(GroupRingSpread / 100);
        // Quietly, and once the slider rests: re-applying every setting for each pixel a slider moved made them stick.
        DonutLookSave.Schedule(_store);
    }

    // ───────── Editing operations called by the view's drag & drop ─────────

    /// <summary>Slices on the face being edited, or in the open group.</summary>
    public int SliceCount => Slices?.Count ?? 0;

    /// <summary>
    /// Adds a palette preset to the face being edited, or to the open group, at <paramref name="index"/> (clamped).
    /// Returns false when it is full, or the preset can't go there.
    /// </summary>
    public bool Insert(string presetId, int index)
    {
        if (BelongsOnBack(_store.FindPreset(presetId)))
        {
            // Copy to folder goes on a back face only, and so does a group holding one: a group open in the editor may
            // be on a front face too, of this donut or another one.
            if (OpenGroup is { } group && _store.Profiles.Any(p => p.PresetIds.Contains(group.Id)))
            {
                Message = _loc["group.copy_front"];
                return false;
            }
            if (OpenGroup is null && !EditingBack)
            {
                Message = _loc["presets.copy_back"];
                return false;
            }
        }
        if (Slices is not { } slices || slices.Contains(presetId))
            return false;
        if (OpenGroup is not null && _store.FindPreset(presetId) is not { IsGroup: false })
            return false; // a group never holds a group
        if (slices.Count >= Capacity)
        {
            Message = OpenGroup is null ? _loc.Format("toolbar.max_reached", ToolbarProfile.MaxSlices)
                : _loc.Format("group.full", Preset.MaxSubPresets);
            return false;
        }
        slices.Insert(Math.Clamp(index, 0, slices.Count), presetId);
        Commit();
        return true;
    }

    public void RemoveAt(int index)
    {
        if (Slices is not { } slices || index < 0 || index >= slices.Count)
            return;
        slices.RemoveAt(index);
        Commit();
    }

    /// <summary>Moves a slice onto another slice's position.</summary>
    public void Move(int from, int to)
    {
        if (Slices is not { } slices || from == to || from < 0 || from >= slices.Count)
            return;
        var id = slices[from];
        slices.RemoveAt(from);
        slices.Insert(Math.Clamp(to, 0, slices.Count), id);
        Commit();
    }

    /// <summary>Opens the preset popup for the slice at <paramref name="index"/>.</summary>
    public async Task EditAsync(int index)
    {
        if (Slices is not { } slices || index < 0 || index >= slices.Count)
            return;
        var preset = _store.FindPreset(slices[index]);
        if (preset is not null && await _windows.EditPresetAsync(preset, _windows.Main, folderInput: SelectedProfile?.Profile.Id == "folders"))
            PresetEdited(preset);
    }

    /// <summary>Opens the preset popup for a palette chip.</summary>
    public async Task EditChipAsync(string presetId)
    {
        var preset = _store.FindPreset(presetId);
        if (preset is not null && await _windows.EditPresetAsync(preset, _windows.Main, folderInput: SelectedProfile?.Profile.Id == "folders"))
            PresetEdited(preset);
    }

    internal void PresetEdited(Preset preset)
    {
        StopJudging();
        _tipsByPreset.Remove(preset.Id);
        foreach (var judging in _judgingByProfile.Values)
            judging.Judged.Remove(preset.Id);
        Commit();
    }

    /// <summary>
    /// A new group (a slice that fans out presets). It joins the available presets only: no donut gets it until the
    /// user drags it there. Groups head that list, and the search is cleared, so it is right there to drag.
    /// </summary>
    [RelayCommand]
    private async Task NewGroup()
    {
        var group = new Preset { Name = _loc["toolbar.new_group"], Kind = PresetKind.Group, TargetFormat = Preset.GroupTarget };
        if (!await _windows.EditGroupAsync(group, _windows.Main, isNew: true, folderInput: SelectedProfile?.Profile.Id == "folders"))
            return;
        AddGroup(group);
    }

    /// <summary>Adds a group made in the editor to the available presets.</summary>
    internal void AddGroup(Preset group)
    {
        // Out of an open group, which can't hold it: the new group is right there to drag.
        OpenGroup = null;
        _store.Presets.Add(group);
        PaletteSearch = "";
        Commit();
    }

    private bool BelongsOnBack(Preset? preset) => preset is not null && (preset.IsCopyToFolder
        || preset.IsGroup && preset.SubPresetIds.Any(id => _store.FindPreset(id)?.IsCopyToFolder == true));

    [RelayCommand]
    private async Task NewFolderCopy()
    {
        var preset = new Preset
        {
            Name = _loc["presets.copy_to_folder"],
            Kind = PresetKind.CopyToFolder,
            TargetFormat = Preset.CopyTarget,
            Output = new OutputRule { Location = OutputLocation.CustomFolder }
        };
        if (await _windows.EditPresetAsync(preset, _windows.Main))
            AddFolderCopy(preset);
    }

    /// <summary>A new Copy to folder, saved in its editor, goes into the library and onto the back face.</summary>
    internal void AddFolderCopy(Preset preset)
    {
        _store.Presets.Add(preset);
        EditingBack = true; // a back face only: a group open on the front face closes with it
        // Through Insert, whose rules hold: not into an open group that is also on a front face. Not added, it waits in
        // the palette, picked.
        if (Slices is { } slices && slices.Count < Capacity && Insert(preset.Id, slices.Count))
            return;
        _newPresetToSelect = preset.Id;
        Commit();
    }

    [RelayCommand]
    private async Task NewPreset()
    {
        var preset = new Preset { Name = _loc["toolbar.new_preset"], TargetFormat = SelectedProfile?.Profile.Id == "folders" ? "iso" : "png" };
        if (!await _windows.EditPresetAsync(preset, _windows.Main, folderInput: SelectedProfile?.Profile.Id == "folders"))
            return;
        _store.Presets.Add(preset);
        if (Slices is { } slices && slices.Count < Capacity)
            slices.Add(preset.Id);
        else
            _newPresetToSelect = preset.Id;
        Commit();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new ToolbarProfile { Name = _loc["toolbar.profile_new"], PresetIds = ["to-pdf", "to-png"], BackPresetIds = [] };
        // Before the mixed-selection profiles (normal ones come first; see Reposition).
        var index = _store.Profiles.FindIndex(p => p.IsFallback);
        index = index < 0 ? _store.Profiles.Count : index;
        _store.Profiles.Insert(index, profile);
        SaveOwnLibrary();
        var item = new ProfileItemViewModel(profile, _loc);
        Profiles.Insert(index, item);
        SelectedProfile = item;
        NotifyMoveChanged();
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (Profile is null || !CanDelete || _windows.Main is null)
            return;
        if (!await _windows.ConfirmAsync(_windows.Main, _loc["toolbar.delete_profile"] + "?", destructive: true))
            return;
        _store.Profiles.Remove(Profile);
        var id = Profile.Id;
        _judgingByProfile.Remove(id);
        StopJudging();
        SaveOwnLibrary();
        Profiles.Remove(SelectedProfile!);
        SelectedProfile = Profiles.FirstOrDefault();
        NotifyMoveChanged();
    }

    [RelayCommand]
    private async Task ResetProfile()
    {
        if (SelectedProfile is not { } item || !CanReset || _windows.Main is null)
            return;
        if (!await _windows.ConfirmAsync(_windows.Main, _loc.Format("toolbar.reset_profile_confirm", item.Name), destructive: true))
            return;
        Reset(item);
    }

    /// <summary>Puts a built-in profile back to its defaults (see <see cref="UserDataStore.ResetProfile"/>).</summary>
    internal void Reset(ProfileItemViewModel item)
    {
        if (!SaveOwnLibrary(() => _store.ResetProfile(item.Profile)))
            return;
        // A profile that changed kind moved to the border between the normal and the mixed ones.
        var from = Profiles.IndexOf(item);
        var to = _store.Profiles.IndexOf(item.Profile);
        if (from != to)
        {
            _repositioning = true;
            try
            {
                Profiles.Move(from, to);
                SelectedProfile = item;
            }
            finally
            {
                _repositioning = false;
            }
        }
        _loading = true;
        ProfileName = _loc.DisplayName(item.Profile);
        _loading = false;
        Message = null;
        // Every profile: the extension hints of the others change with this one's extensions.
        foreach (var other in Profiles)
            other.Refresh(_loc);
        RebuildProfileEditor();
        NotifyMoveChanged();
        ForgetJudgements(item.Profile.Id);
        Rebuild();
    }

    /// <summary>
    /// Counts the changes to what the donuts hold (each save of the library, from here or elsewhere, and the converters
    /// available), but not to how the palette is searched or sorted: the donut on screen fills again only when it
    /// changed.
    /// </summary>
    internal int LibraryVersion { get; private set; }

    private void Commit()
    {
        SaveOwnLibrary();
        // Every profile: deleting a group (or a preset) takes it off every donut, not just this one.
        foreach (var item in Profiles)
            item.Refresh(_loc);
        Rebuild();
    }

    private object? TipFor(Preset preset, PresetUse use, string? reason)
    {
        if (preset.IsGroup)
            return null;
        if (use != PresetUse.Usable)
            return reason;
        if (!_tipsByPreset.TryGetValue(preset.Id, out var tip))
            _tipsByPreset[preset.Id] = tip =
                new PresetFormatTip(preset, _availability, _catalog.CreatePlanner, _loc);
        return tip;
    }

    // Unlike an unrestricted mixed profile, Folders has a concrete input type without file extensions.
    private bool JudgesCurrentProfile => Profile is { Extensions.Count: > 0 } || Profile?.Id == "folders";


    // Folder profiles have a real input type despite having no extensions. Groups must offer a compatible action too.
    private bool AllowedForFolder(Preset preset, PaletteJudging? judging)
    {
        if (Profile?.Id != "folders")
            return true;
        if (preset.IsGroup)
            return preset.SubPresetIds.Select(_store.FindPreset).OfType<Preset>()
                .Any(child => !child.IsGroup && AllowedForFolder(child, judging));
        return judging is not null && judging.Judged.TryGetValue(preset.Id, out var result) && result.Use != PresetUse.Unusable;
    }

    /// <summary>Rebuilds the donut slices (of the face being edited) and the palette from the model.</summary>
    private void Rebuild()
    {
        var profile = Profile;
        // The open group was deleted (its own editor can do that), or left the face: back to the whole donut.
        if (OpenGroup is { } open && (!_store.Presets.Contains(open) || OpenGroupIndex < 0))
            OpenGroup = null;
        RefreshRingGroups();
        var used = Slices ?? [];
        DonutItems = used
            .Select(_store.FindPreset)
            .OfType<Preset>()
            // A group is marked as one (no children: the editor doesn't fan them out), so the preview draws it as the
            // donut does.
            .Select(p => new DonutItem
            {
                Id = p.Id,
                Label = _loc.DonutName(p),
                Caption = Caption(p),
                KeepsCaption = p.IsCopyToFolder,
                Children = p.IsGroup ? [] : null,
                FullName = _loc.DisplayName(p),
                IconKey = SliceIcons.For(p),
                Show = p.Show,
                Direction = p.Direction,
            })
            .ToList();

        Palette.Clear();
        var placed = Placed(profile);
        var judge = JudgesCurrentProfile;
        _judgingByProfile.TryGetValue(profile?.Id ?? "", out var judging);
        var pending = profile?.Id == "folders" && ShownPresets().Any(p => !p.IsGroup && judging?.Judged.ContainsKey(p.Id) != true);
        var query = PaletteSearch.Trim();
        if (profile?.Id == "folders" && _newPresetToSelect is { } newFolderId
            && judging?.Judged.TryGetValue(newFolderId, out var folderJudgement) == true
            && folderJudgement.Use == PresetUse.Unusable)
            _newPresetToSelect = null;
        if (_newPresetToSelect is { } newId && (!judge || judging?.Judged.ContainsKey(newId) == true)
            && _store.FindPreset(newId) is { } newPreset)
        {
            var newName = _loc.DisplayName(newPreset);
            if (judge && judging!.Judged[newId].Use == PresetUse.Unusable && query != newName)
            {
                PaletteSearch = newName;
                return;
            }
            if (query.Length > 0 && !newName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && (Caption(newPreset)?.Contains(query, StringComparison.CurrentCultureIgnoreCase) != true))
            {
                PaletteSearch = "";
                return;
            }
        }
        var candidates = new List<(PaletteChip Chip, PaletteTab Tab, bool Group)>();
        // Groups first: few, and otherwise at the very end, below every conversion.
        foreach (var preset in ShownPresets().Where(p => !placed.Contains(p.Id)).OrderBy(p => !p.IsGroup))
        {
            string? reason = null;
            var use = PresetUse.Usable;
            if (judge && !preset.IsGroup)
            {
                if (judging is null || !judging.Judged.TryGetValue(preset.Id, out var judgement))
                {
                    pending = true;
                    continue;
                }
                use = judgement.Use;
                reason = judgement.Reason;
            }
            if (!AllowedForFolder(preset, judging))
                continue;
            var label = _loc.DisplayName(preset);
            var caption = Caption(preset);
            // "GIF  GIF" says nothing twice: only show the format when the name doesn't already.
            if (caption is not null && label.Contains(caption, StringComparison.OrdinalIgnoreCase))
                caption = null;
            var chip = new PaletteChip(preset.Id, label, caption, reason, use)
            { Tip = TipFor(preset, use, reason), IsGroup = preset.IsGroup };
            if (chip.CanAdd)
                Palette.Add(chip);
            if ((query.Length == 0 && chip.CanAdd)
                || (query.Length > 0 && (label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || caption?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true)))
                candidates.Add((chip, preset.IsGroup ? PaletteTab.All : TabOf(preset), preset.IsGroup));
        }
        if (pending && _pass is null && !_disposed)
        {
            var pass = _pass = new object();
            var profileId = profile!.Id;
            Dispatcher.UIThread.Post(() => JudgeSome(pass, profileId), DispatcherPriority.Background);
        }
        var selected = SelectedTab?.Id ?? PaletteTab.All;
        var active = candidates.Where(c => !c.Group).Select(c => c.Tab).ToHashSet();
        var wanted = TabDefinitions.Where(t => t.Id == PaletteTab.All || active.Contains(t.Id)).ToList();
        _updatingTabs = true;
        try
        {
            for (var i = Tabs.Count - 1; i >= 0; i--)
                if (!wanted.Any(t => t.Id == Tabs[i].Id))
                    Tabs.RemoveAt(i);
            for (var i = 0; i < wanted.Count; i++)
            {
                if (i < Tabs.Count && Tabs[i].Id == wanted[i].Id)
                    continue;
                var existing = Tabs.FirstOrDefault(t => t.Id == wanted[i].Id);
                if (existing is not null)
                    Tabs.Move(Tabs.IndexOf(existing), i);
                else
                    Tabs.Insert(i, new PaletteTabItem(wanted[i].Id, _loc[wanted[i].LabelKey]));
            }
            var allIds = candidates.Where(c => !c.Group).Select(c => c.Chip.PresetId).ToHashSet();
            ShowTabRow = Tabs.Count > 2 || (Tabs.Count == 2
                && !allIds.SetEquals(candidates.Where(c => !c.Group && c.Tab == Tabs[1].Id)
                    .Select(c => c.Chip.PresetId)));
            if (_newPresetToSelect is { } addedId && candidates.FirstOrDefault(c => c.Chip.PresetId == addedId) is { Chip: not null } added)
            {
                if (added.Chip.Use == PresetUse.Unusable && query != added.Chip.Label)
                {
                    PaletteSearch = added.Chip.Label;
                    return;
                }
                _newPresetToSelect = null;
                selected = added.Tab;
            }
            if (!pending && _pendingTabRestore is { } restore)
            {
                selected = restore;
                _pendingTabRestore = null;
            }
            if (!ShowTabRow || !wanted.Any(t => t.Id == selected))
                selected = PaletteTab.All;
            SelectedTab = Tabs.First(t => t.Id == selected);
        }
        finally
        {
            _updatingTabs = false;
        }
        RefreshVisiblePalette(candidates);

        CenterTitle = OpenGroup is { } inside ? _loc.DisplayName(inside) : profile is null ? "" : _loc.DisplayName(profile);
        CenterSubtitle = OpenGroup is not null ? $"{used.Count} / {Preset.MaxSubPresets}"
            : EditingBack
            ? $"{_loc["toolbar.face_back"]} · {used.Count} / {ToolbarProfile.MaxSlices}"
            : $"{used.Count} / {ToolbarProfile.MaxSlices}";
        OnPropertyChanged(nameof(FrontCount));
        OnPropertyChanged(nameof(BackCount));
    }

    /// <summary>
    /// The presets the palette leaves out: those on the donut (either face), or, with a group open, those in the group
    /// and every group (a group never holds one). The palette, its search and the judging must agree on it: judging
    /// waits for exactly the presets the palette shows.
    /// </summary>
    private HashSet<string> Placed(ToolbarProfile? profile) => profile is null ? []
        : OpenGroup is { } editing ? editing.SubPresetIds.Concat(_store.Presets.Where(p => p.IsGroup).Select(p => p.Id)).ToHashSet()
        : profile.PresetIds.Concat(profile.BackPresetIds ?? []).ToHashSet();

    private IEnumerable<Preset> ShownPresets()
    {
        var off = _store.Settings.TurnedOffFieldSet();
        return _store.Presets.Where(p => FormatFields.IsShown(p.TargetFormat, off));
    }

    private void RefreshVisiblePalette()
    {
        var query = PaletteSearch.Trim();
        var placed = Placed(Profile);
        _judgingByProfile.TryGetValue(Profile?.Id ?? "", out var judging);
        var candidates = new List<(PaletteChip Chip, PaletteTab Tab, bool Group)>();
        foreach (var preset in ShownPresets().Where(p => !placed.Contains(p.Id)).OrderBy(p => !p.IsGroup))
        {
            var judgement = judging?.Judged.GetValueOrDefault(preset.Id) ?? new(PresetUse.Usable, null);
            if (!AllowedForFolder(preset, judging))
                continue;
            if (JudgesCurrentProfile && !preset.IsGroup && !HasJudgement(Profile!.Id, preset.Id))
                continue;
            if (judgement.Use == PresetUse.Unusable && query.Length == 0)
                continue;
            var label = _loc.DisplayName(preset);
            var caption = Caption(preset);
            if (caption is not null && label.Contains(caption, StringComparison.OrdinalIgnoreCase))
                caption = null;
            if (query.Length > 0 && !label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && caption?.Contains(query, StringComparison.CurrentCultureIgnoreCase) != true)
                continue;
            var reason = judgement.Reason;
            candidates.Add((new PaletteChip(preset.Id, label, caption, reason, judgement.Use)
            { Tip = TipFor(preset, judgement.Use, reason), IsGroup = preset.IsGroup },
                preset.IsGroup ? PaletteTab.All : TabOf(preset), preset.IsGroup));
        }
        RefreshVisiblePalette(candidates);
    }

    private void RefreshVisiblePalette(List<(PaletteChip Chip, PaletteTab Tab, bool Group)> candidates)
    {
        var tab = SelectedTab?.Id ?? PaletteTab.All;
        var wanted = candidates.Where(c => c.Group || tab == PaletteTab.All || c.Tab == tab).Select(c => c.Chip).ToList();
        VisiblePalette.Clear();
        foreach (var chip in wanted)
            VisiblePalette.Add(chip);
    }

    private void JudgeSome(object pass, string profileId)
    {
        if (pass != _pass || _disposed || Profile?.Id != profileId)
            return;
        var profile = Profile;
        if (!_judgingByProfile.TryGetValue(profileId, out var judging))
            _judgingByProfile[profileId] = judging = new PaletteJudging();
        var placed = Placed(profile);
        var pending = ShownPresets().Where(p => !p.IsGroup && (profile.Id == "folders" || !placed.Contains(p.Id)) && !judging.Judged.ContainsKey(p.Id)).ToList();
        if (pending.Count > 0)
        {
            judging.Session ??= _availability.ForPalette(profile.Id == "folders" ? [FormatRegistry.FindById(FormatRegistry.Folder)!] : profile.Extensions.Select(FormatRegistry.FindByExtension)
                .OfType<FileFormat>().DistinctBy(f => f.Id).ToList());
            var clock = Stopwatch.StartNew();
            foreach (var preset in pending)
            {
                judging.Judged[preset.Id] = JudgeOne(judging.Session, preset);
                JudgementObserved?.Invoke(profileId, preset.Id);
                if (clock.Elapsed >= JudgeSlice)
                    break;
            }
        }
        if (ShownPresets().Any(p => !p.IsGroup && (profile.Id == "folders" || !placed.Contains(p.Id)) && !judging.Judged.ContainsKey(p.Id)))
            Dispatcher.UIThread.Post(() => JudgeSome(pass, profileId), DispatcherPriority.Background);
        else
        {
            _pass = null;
            Rebuild();
        }
    }

    // A failed probe must not end this dispatcher job. Folder offers fail closed; other profiles keep their fallback.
    private PresetJudgement JudgeOne(PresetAvailability.PaletteSession session, Preset preset)
    {
        try
        {
            return session.Judge(preset);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Judging preset {Preset} for the palette failed", preset.Id);
            return new PresetJudgement(Profile?.Id == "folders" ? PresetUse.Unusable : PresetUse.Usable, null);
        }
    }

    private string? Caption(Preset p) =>
        p.IsGroup ? _loc.Format("donut.group_caption", p.SubPresetIds.Count)
        : p.IsCopyToFolder ? _loc[p.Output.MoveInsteadOfCopy ? "presets.move_to_folder" : "presets.copy_to_folder"]
        : p.TargetFormat == BuiltInData.SameAsSource ? _loc["donut.same_format"] : FormatLabels.NameOf(_loc, p.TargetFormat);
}
