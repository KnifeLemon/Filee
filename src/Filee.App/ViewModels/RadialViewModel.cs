// State of the live donut toolbar: which files are being converted and which presets are offered, on the front and
// the back face of the donut (Tab flips between them, see DonutMenu.Flip.cs).

using Avalonia.Threading;
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

    /// <summary>The job the progress ring shows, for the download badge above it; null when the ring shows none.</summary>
    [ObservableProperty] private JobViewModel? _progressJob;
    [ObservableProperty] private double _outerRadius = 150;
    [ObservableProperty] private double _holeRatio = 0.42;
    [ObservableProperty] private double _sliceOpacity = 0.96;

    /// <summary>The output format under the slices' names (Settings → Donut toolbar → Edit Donut Size).</summary>
    [ObservableProperty] private bool _showCaptions = true;

    /// <summary>How large a group's ring is (Edit Donut Size → Group ring size).</summary>
    [ObservableProperty] private double _groupRingSize = 1;

    /// <summary>How wide a group's ring spreads (Edit Donut Size → Group ring spread).</summary>
    [ObservableProperty] private double _groupRingSpread = 1;

    /// <summary>
    /// How large the slices' names and icons are (the donut's <see cref="ToolbarProfile.LabelScale"/>).
    /// </summary>
    [ObservableProperty] private double _labelScale = 1;

    /// <summary>
    /// What a slice shows when its preset doesn't say (the donut's <see cref="ToolbarProfile.LabelShow"/>).
    /// </summary>
    [ObservableProperty] private LabelShow _defaultShow = LabelShow.Auto;

    /// <summary>The other face has a slice for these files: Tab flips to it.</summary>
    [ObservableProperty] private bool _canFlip;

    /// <summary>
    /// "⟳ Tab back" / "⟳ Tab front" (the arrow drawn, #554) in the hub (with the flip key of the settings) while <see cref="CanFlip"/> and a key
    /// or the wheel flips; else null.
    /// </summary>
    [ObservableProperty] private string? _flipHint;

    /// <summary>The hint shows a mouse instead of a key: only the wheel flips.</summary>
    [ObservableProperty] private bool _flipHintWheel;

    /// <summary>The key that flips the donut (settings name, "Tab"), or null for none.</summary>
    [ObservableProperty] private string? _flipKey;

    /// <summary>A turn of the mouse wheel flips the donut.</summary>
    [ObservableProperty] private bool _flipWithWheel;

    /// <summary>
    /// The window keeps room for the ring a group fans out. Dragging, the donut is placed before the files (and so
    /// the donut) are known: room when any donut has a group. In click mode only when this one does
    /// (<see cref="FitToThisDonut"/>), so other donuts stay as small, and as close to the cursor at a screen edge, as
    /// without groups.
    /// </summary>
    [ObservableProperty] private bool _reserveSubRing;

    /// <summary>
    /// 0 while a drag gesture waits for files, 1 otherwise. The gesture starts before anyone knows what is being
    /// dragged (a file, text, or nothing: Ctrl + a rubber-band selection), so the donut only appears when files enter.
    /// </summary>
    [ObservableProperty] private double _donutOpacity = 1;

    /// <summary>
    /// Background of the window while the donut is hidden: practically invisible, but not fully transparent, so
    /// Windows still delivers the drag to this window and the files can arrive.
    /// </summary>
    [ObservableProperty] private string _hitBackground = "Transparent";

    /// <summary>Files the donut will convert (empty while waiting for a drag to enter).</summary>
    public IReadOnlyList<string> Files { get; private set; } = [];

    /// <summary>Presets in slice order.</summary>
    public IReadOnlyList<Preset> Presets { get; private set; } = [];

    /// <summary>For each slice, the presets its group fans out (empty for an ordinary preset).</summary>
    private IReadOnlyList<IReadOnlyList<Preset>> _subPresets = [];

    /// <summary>The slices of one face of the donut, for the files on it.</summary>
    private sealed record Face(IReadOnlyList<Preset> Presets, IReadOnlyList<IReadOnlyList<Preset>> SubPresets, IReadOnlyList<DonutItem> Items)
    {
        public static readonly Face Empty = new([], [], []);

        /// <summary>
        /// A slice converts these files. A face without one isn't flipped to: all of it greyed out would only be a
        /// dead end, and Tab doing nothing there is what it does on a donut with no back face.
        /// </summary>
        public bool IsUsable => Items.Any(item => item.IsEnabled);
    }

    private Face _front = Face.Empty;

    // The back face: its presets as the profile has them, and its slices for the files once they are checked (null
    // until then). Checking every preset against the files takes as long as for the front (a moment when engines are
    // missing), so the back is checked after the front is on screen, not before (see Load).
    private IReadOnlyList<string> _backIds = [];
    private IReadOnlyList<FileFormat> _formats = [];
    // The turned-off fields the last Load hid, already without the dropped files' own fields, so the back face is
    // filtered with the same set as the front.
    private IReadOnlySet<string> _off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private Face? _back;
    private int _loads;

    /// <summary>The back face is showing (after a flip).</summary>
    public bool IsBackFace { get; private set; }

    /// <summary>
    /// The preset that slice <paramref name="index"/> converts with, or slice <paramref name="sub"/> of its group's ring;
    /// null when there is none or it can't convert these files.
    /// </summary>
    public Preset? PresetAt(int index, int sub)
    {
        if (index < 0 || index >= Items.Count)
            return null;
        if (sub < 0)
            return Items[index] is { IsEnabled: true, IsGroup: false } ? Presets[index] : null;
        return Items[index].Children is { } children && sub < children.Count && children[sub].IsEnabled
            ? _subPresets[index][sub]
            : null;
    }

    /// <summary>The preset slice <paramref name="index"/> shows, or slice <paramref name="sub"/> of its group's ring (to edit it).</summary>
    public Preset? PresetShownAt(int index, int sub) =>
        index < 0 || index >= Presets.Count ? null
        : sub < 0 ? Presets[index]
        : sub < _subPresets[index].Count ? _subPresets[index][sub] : null;

    /// <summary>True when opened with known files (keyboard, context menu, drop zone): slices are clicked.</summary>
    public bool IsClickMode { get; private set; }

    public bool HasFiles => Files.Count > 0;
    public bool IsConnectMode { get; private set; }

    public void LoadForConnect()
    {
        IsClickMode = true;
        IsConnectMode = true;
        Files = [];
        DonutOpacity = 1;
        HitBackground = "Transparent";
        ApplyAppearance();

        var connectPreset = new Preset
        {
            Id = "preset.filee_connect",
            TargetFormat = "connect",
        };

        var connectItem = new DonutItem
        {
            Id = "preset.filee_connect",
            Label = loc["preset.filee_connect"],
            Caption = loc["connect.caption"],
            IsEnabled = true,
        };

        _front = new Face([connectPreset], [[]], [connectItem]);
        (_back, _backIds, _formats, _off) = (null, [], [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Presets = _front.Presets;
        _subPresets = _front.SubPresets;
        Items = _front.Items;
        CanFlip = false;
        FlipHint = null;
        CenterTitle = loc["preset.filee_connect"];
        CenterSubtitle = loc["donut.close_hint"];
    }

    /// <summary>Drag mode before any file entered: an empty ring saying "Bring files here".</summary>
    public void ResetForDrag()
    {
        IsClickMode = false;
        IsConnectMode = false;
        Files = [];
        ApplyAppearance();
        ReserveSubRing = store.Profiles.Any(profile => HasGroups(profile.PresetIds.Concat(profile.BackPresetIds ?? [])));
        _loads++;
        (_front, _back, _backIds, _formats) = (Face.Empty, Face.Empty, [], []);
        _off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ShowFace(back: false);
        CenterTitle = loc["donut.center_drag"];
        CenterSubtitle = null;
        DonutOpacity = 0;
        HitBackground = "#01000000";
    }

    /// <summary>
    /// Fills the donut for the given files, on its front face, or on the back when <paramref name="backFace"/> and the
    /// back has a slice for them (the same files again after a preset was edited).
    /// </summary>
    public void Load(IReadOnlyList<string> files, bool clickMode, bool backFace = false)
    {
        _example = null;
        IsClickMode = clickMode;
        IsConnectMode = false;
        Files = files;
        DonutOpacity = 1;
        HitBackground = "Transparent";
        ApplyAppearance();
        _checked = true;

        // A Shapefile selected with its .dbf, .shx, ... picks the donut of the .shp, not the one for unknown files, and
        // counts as one file.
        var folders = files.Where(Directory.Exists).ToHashSet(Filee.Core.Platform.FileSystemPaths.Comparer);
        var mainFiles = CompanionFiles.WithoutCompanions(files.Where(p => !folders.Contains(p)).ToList()).ToHashSet(Filee.Core.Platform.FileSystemPaths.Comparer);
        var counted = files.Where(p => folders.Contains(p) || mainFiles.Contains(p)).ToList();
        var foldersOnly = counted.Count > 0 && counted.All(folders.Contains);
        var profile = (foldersOnly ? store.Profiles.FirstOrDefault(p => p.Id == "folders") : null)
            ?? ProfileSelector.Select(store.Profiles, counted);
        var formats = ProfileSelector.DetectFormats(counted.Where(p => !folders.Contains(p))).ToList();
        if (folders.Count > 0)
            formats.Add(FormatRegistry.FindById(FormatRegistry.Folder)!);
        // A turned-off field still shows the formats of the dropped files' own fields (FORMAT-RULES rule 4): a font file
        // offers font conversions with Fonts off. The front takes this local, not _off, which holds the last load's set
        // until the line after it.
        var off = store.Settings.TurnedOffFields
            .Except(formats.SelectMany(FormatFields.Of).Select(field => field.Id), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        LabelScale = ToolbarProfile.ClampLabelScale(profile?.LabelScale ?? 1);
        DefaultShow = profile?.LabelShow ?? LabelShow.Auto;
        // Both faces follow the same rule: a slice that can't convert these files is greyed out and says why.
        _front = BuildFace(profile?.PresetIds ?? [], formats, off);
        (_back, _backIds, _formats, _off) = (null, [.. profile?.BackPresetIds ?? []], formats, off);
        var load = ++_loads;
        if (backFace)
        {
            ShowFace(BackFace().IsUsable);
        }
        else
        {
            ShowFace(back: false, checkBack: false);
            // Right after the donut is drawn: until then Tab has nothing to flip to, nor the hub a hint.
            if (_backIds.Count > 0)
                Dispatcher.UIThread.Post(() =>
                {
                    if (load == _loads && _back is null)
                        ShowFace(IsBackFace);
                }, DispatcherPriority.Background);
        }

        CenterTitle = formats.Count == 1
            ? loc.Format("donut.files_same", counted.Count, formats[0].DisplayName)
            : loc.Format("donut.files_mixed", counted.Count);
        CenterSubtitle = clickMode ? loc["donut.close_hint"] : null;
    }

    /// <summary>
    /// Fills the donut as <paramref name="profile"/> opens for one of its files (two different ones for a mixed
    /// profile, a folder for Folders), on the front or the back: the Donut toolbar page shows it on screen, at its real
    /// size, while the donut is adjusted or a group edited. The turned-off fields are left out as <see cref="Load"/>
    /// leaves them out. Nothing converts from it.
    /// </summary>
    public void LoadPreview(ToolbarProfile profile, bool backFace)
    {
        _example = null;
        IsClickMode = false;
        IsConnectMode = false;
        Files = [];
        DonutOpacity = 1;
        HitBackground = "Transparent";
        ApplyLook(profile);
        var formats = PreviewFormats(profile);
        var off = TurnedOffBesides(store.Settings.TurnedOffFields, formats);
        _checked = formats.Count > 0;
        _front = BuildFace(profile.PresetIds, formats, off);
        (_back, _backIds, _formats, _off) = (null, [.. profile.BackPresetIds ?? []], formats, off);
        var load = ++_loads;
        if (backFace)
            ShowFace(BackFace().Items.Count > 0); // asked for its back (a group held open there): shown, greyed or not
        else
        {
            ShowFace(back: false, checkBack: false);
            // As Load: the back is checked once the front is on screen, for the flip, leaving the slices shown as they
            // are (an example ring may have replaced the top one by then).
            if (_backIds.Count > 0)
                Dispatcher.UIThread.Post(() =>
                {
                    if (load == _loads && _back is null)
                        ShowFlip(IsBackFace);
                }, DispatcherPriority.Background);
        }
        ReserveSubRing = _front.Items.Any(item => item.IsGroup) || HasGroups(_backIds);
        CenterTitle = formats.Count == 1
            ? loc.Format("donut.files_same", 1, formats[0].DisplayName)
            : formats.Count > 1 ? loc.Format("donut.files_mixed", formats.Count) : loc.DisplayName(profile);
        CenterSubtitle = null;
    }

    /// <summary>
    /// The files <see cref="LoadPreview"/> fills a profile's donut for: one of its first format; two different ones for
    /// a mixed profile (its first two extensions may share one); a folder for Folders. A profile with none (the mixed
    /// profile that takes anything) has none.
    /// </summary>
    internal static List<FileFormat> PreviewFormats(ToolbarProfile profile)
    {
        var formats = new List<FileFormat>();
        if (profile.Id == "folders")
            formats.Add(FormatRegistry.FindById(FormatRegistry.Folder)!);
        else
            foreach (var extension in profile.Extensions)
            {
                if (formats.Count == (profile.IsFallback ? 2 : 1))
                    break;
                if (ProfileSelector.DetectFormats(["preview." + extension]).FirstOrDefault() is { } format && !formats.Contains(format))
                    formats.Add(format);
            }
        return formats;
    }

    /// <summary>
    /// The fields left out for files of <paramref name="formats"/>: those turned off, except the files' own (a font
    /// file offers font conversions with Fonts off, FORMAT-RULES rule 4).
    /// </summary>
    internal static HashSet<string> TurnedOffBesides(IEnumerable<string> turnedOff, IEnumerable<FileFormat> formats) =>
        turnedOff.Except(formats.SelectMany(FormatFields.Of).Select(field => field.Id), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The id of the example group <see cref="ShowExampleGroup"/> hangs off the top slice.</summary>
    public const string ExampleGroupId = "filee-example-group";

    /// <summary>
    /// For a donut with no group, while its ring settings are adjusted: the top slice fans out an example ring of four
    /// formats, and the centre says so (<paramref name="title"/>, <paramref name="subtitle"/>). Nothing converts here.
    /// </summary>
    public void ShowExampleGroup(string title, string subtitle)
    {
        _example = (title, subtitle);
        ShowExample(title, subtitle);
    }

    // The example ring shown (flipped, the other face shows it too), until the donut is filled again.
    private (string Title, string Subtitle)? _example;

    private void ShowExample(string title, string subtitle)
    {
        if (Items.Count == 0)
            return;
        var top = Items[0];
        Items =
        [
            new DonutItem
            {
                Id = ExampleGroupId,
                Label = top.Label,
                FullName = top.FullName,
                IconKey = top.IconKey,
                Show = top.Show,
                Direction = top.Direction,
                Children = new[] { "JPG", "PNG", "WebP", "HEIC" }.Select(name => new DonutItem { Id = ExampleGroupId + "-" + name, Label = name }).ToList(),
            },
            .. Items.Skip(1),
        ];
        ReserveSubRing = true;
        (CenterTitle, CenterSubtitle) = (title, subtitle);
    }

    /// <summary>
    /// The donut's look from the settings and <paramref name="profile"/>'s labels, while it is previewed.
    /// </summary>
    public void ApplyLook(ToolbarProfile profile)
    {
        ApplyAppearance();
        LabelScale = ToolbarProfile.ClampLabelScale(profile.LabelScale);
        DefaultShow = profile.LabelShow ?? LabelShow.Auto;
    }

    /// <summary>
    /// Shows the other face (at the start of the donut's flip). Returns false, changing nothing, when it has no slice
    /// for these files (<see cref="CanFlip"/>).
    /// </summary>
    public bool ToggleFace()
    {
        if (_back is null)
            ShowFace(IsBackFace); // asked for before it was checked: check it now
        if (!CanFlip)
            return false;
        ShowFace(!IsBackFace);
        CenterSubtitle = IsClickMode ? loc["donut.close_hint"] : null;
        if (_example is { } example)
            ShowExample(example.Title, example.Subtitle);
        return true;
    }

    /// <summary>Shows a face; <paramref name="checkBack"/> false leaves an unchecked back face (and the flip) for later.</summary>
    private void ShowFace(bool back, bool checkBack = true)
    {
        var face = back ? BackFace() : _front;
        if (IsBackFace != back || !ReferenceEquals(Items, face.Items))
        {
            IsBackFace = back;
            Presets = face.Presets;
            _subPresets = face.SubPresets;
            Items = face.Items;
        }
        ShowFlip(back, checkBack);
    }

    /// <summary>Whether, and how, the face shown flips to the other (<see cref="CanFlip"/>, the hint).</summary>
    private void ShowFlip(bool back, bool checkBack = true)
    {
        var other = back ? _front : checkBack ? BackFace() : _back;
        // The front was already shown, even when every slice was disabled. Do not strand the user on the back face.
        CanFlip = back ? _front.Items.Count > 0 : other?.IsUsable == true;
        var how = FlipKey is { } key ? KeyNames.Display(key) : "";
        FlipHint = CanFlip && (FlipKey is not null || FlipWithWheel)
            ? loc.Format(back ? "donut.flip_to_front" : "donut.flip_to_back", how).Trim()
            : null;
        FlipHintWheel = FlipHint is not null && FlipKey is null;
    }

    /// <summary>The back face's slices for the files, checked now if they weren't yet.</summary>
    private Face BackFace() => _back ??= BuildFace(_backIds, _formats, _off);

    // Slices are judged against the files (LoadPreview for a profile without extensions has none to judge against).
    private bool _checked = true;

    private bool HasGroups(IEnumerable<string> presetIds) => presetIds.Any(id => store.FindPreset(id)?.IsGroup == true);

    private Face BuildFace(IEnumerable<string> presetIds, IReadOnlyList<FileFormat> formats, IReadOnlySet<string> off)
    {
        var presets = presetIds
            .Select(store.FindPreset)
            .OfType<Preset>()
            .Where(p => FormatFields.IsShown(p.TargetFormat, off))
            .ToList();

        var subPresets = presets
            .Select(p => (IReadOnlyList<Preset>)(p.IsGroup ? p.SubPresetIds : [])
                .Select(store.FindPreset).OfType<Preset>().Where(c => !c.IsGroup).Take(Preset.MaxSubPresets).ToList())
            .ToList();

        // Children are filtered after Take, so a hidden child doesn't let in one the group editor doesn't show. A group
        // whose children were all hidden leaves the donut like a hidden slice; one that was empty already stays, greyed
        // out as before. Both lists lose the same index: Items reads a group's children by its position.
        for (var i = presets.Count - 1; i >= 0; i--)
        {
            var children = subPresets[i];
            subPresets[i] = children.Where(p => FormatFields.IsShown(p.TargetFormat, off)).ToList();
            if (presets[i].IsGroup && children.Count > 0 && subPresets[i].Count == 0)
            {
                presets.RemoveAt(i);
                subPresets.RemoveAt(i);
            }
        }

        DonutItem ItemOf(Preset p, bool caption = true)
        {
            var reason = _checked ? availability.Check(p, formats) : null;
            return new DonutItem
            {
                Id = p.Id,
                // On a group's ring the group's name gives the context: "Rotate" → Left / Right / 180°.
                Label = caption ? loc.DonutName(p) : loc.RingName(p),
                Caption = !caption ? null
                    : p.IsCopyToFolder ? loc[p.Output.MoveInsteadOfCopy ? "presets.move_to_folder" : "presets.copy_to_folder"]
                    : p.TargetFormat == BuiltInData.SameAsSource
                        ? string.Join("/", formats.Select(f => f.DisplayName).Take(2))
                        : FormatLabels.NameOf(loc, p.TargetFormat),
                KeepsCaption = p.IsCopyToFolder,
                IsEnabled = reason is null,
                DisabledReason = reason,
                FullName = loc.DisplayName(p),
                IconKey = SliceIcons.For(p),
                Show = p.Show,
                Direction = p.Direction,
            };
        }

        var items = presets.Select((p, i) =>
        {
            if (!p.IsGroup)
                return ItemOf(p);
            // A group: its own name and size, and its presets on the ring it fans out (labels only, they're small).
            var children = subPresets[i].Select(c => ItemOf(c, caption: false)).ToList();
            return new DonutItem
            {
                Id = p.Id,
                Label = loc.DonutName(p),
                Caption = loc.Format("donut.group_caption", children.Count),
                IsEnabled = children.Any(c => c.IsEnabled),
                // Its presets were all deleted since it was made: say so rather than show a dead slice.
                DisabledReason = children.Count == 0 ? loc["donut.group_empty"]
                    : children.Select(c => c.DisabledReason).FirstOrDefault(r => r is not null),
                Children = children,
                FullName = loc.DisplayName(p),
                IconKey = SliceIcons.For(p),
                Show = p.Show,
                Direction = p.Direction,
            };
        }).ToList();
        return new Face(presets, subPresets, items);
    }

    /// <summary>Updates the centre hint for the emphasised slice (and slice <paramref name="sub"/> of its group's ring).</summary>
    public void ShowHint(int index, int sub, bool centerHot)
    {
        if (centerHot)
            CenterSubtitle = loc["donut.cancel"];
        else if (index >= 0 && index < Items.Count && Items[index].Children is { Count: > 0 } children &&
                 (sub >= 0 || Items[index].IsEnabled))
            CenterSubtitle = sub >= 0 && sub < children.Count
                ? children[sub].IsEnabled
                    ? loc.Format(IsClickMode ? "donut.click_to" : "donut.drop_to", $"{NameOf(Items[index])} · {NameOf(children[sub])}")
                    : children[sub].DisabledReason
                : loc["donut.pick_in_group"];
        else if (index >= 0 && index < Items.Count)
            CenterSubtitle = Items[index].IsEnabled
                ? loc.Format(IsClickMode ? "donut.click_to" : "donut.drop_to", NameOf(Items[index]))
                : Items[index].DisabledReason;
        else
            CenterSubtitle = IsClickMode ? loc["donut.close_hint"] : null;
    }

    // The slice may show an icon, a short name or a shortened one: the hint names it in full.
    private static string NameOf(DonutItem item) => item.FullName ?? item.Label;

    /// <summary>
    /// Click mode, before the window is placed: room for a group's ring only if this donut has a group, on either face
    /// (the window must not grow, and the donut move, when it flips).
    /// </summary>
    public void FitToThisDonut() => ReserveSubRing = _front.Items.Any(item => item.IsGroup) || HasGroups(_backIds);

    private void ApplyAppearance()
    {
        OuterRadius = Math.Clamp(store.Settings.Donut.OuterRadius, 100, 240);
        HoleRatio = store.Settings.Donut.HoleRatio;
        SliceOpacity = Math.Clamp(store.Settings.Donut.Opacity, DonutSettings.MinOpacity, 1);
        ShowCaptions = store.Settings.Donut.ShowCaptions;
        GroupRingSize = DonutSettings.ClampGroupRingSize(store.Settings.Donut.GroupRingSize);
        GroupRingSpread = DonutSettings.ClampGroupRingSpread(store.Settings.Donut.GroupRingSpread);
        FlipKey = store.Settings.Donut.EffectiveFlipKey(store.Settings.Triggers);
        FlipWithWheel = store.Settings.Donut.FlipWithWheel;
    }
}
