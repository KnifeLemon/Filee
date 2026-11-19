// "Export for the gallery" (#1530): the presets one ticks, and one donut if one likes, saved as a preset.json for a
// submission to the preset gallery. The file is checked as the gallery's CI checks it each time the choice changes, so
// what can't be shared and what will be shown as a warning are listed before it is saved; the submission form opens in
// the browser.

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.Core.Gallery;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.App.ViewModels;

/// <summary>A preset of the library, ticked or not for the export.</summary>
public sealed partial class GalleryExportChoice(Preset preset, string name, bool chosen) : ObservableObject
{
    public Preset Preset { get; } = preset;
    public string Name { get; } = name;

    [ObservableProperty] private bool _isChosen = chosen;
}

public sealed partial class GalleryExportViewModel : ObservableObject
{
    /// <summary>The gallery repository's form for a new preset.</summary>
    public static readonly Uri SubmitUri = new("https://github.com/KnifeLemon/Filee-Presets/issues/new?template=submit-preset.yml");

    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly Func<Uri, Task> _open;
    private readonly Func<Task<string?>> _pickPath;
    private GalleryExportFile? _file;

    /// <param name="selected">The preset ticked to begin with (the one chosen on the Presets page).</param>
    /// <param name="pickPath">Asks where to save preset.json; null when the user cancels.</param>
    public GalleryExportViewModel(UserDataStore store, ILocalizer loc, Preset? selected, Func<Uri, Task> open, Func<Task<string?>> pickPath)
    {
        _store = store;
        _loc = loc;
        _open = open;
        _pickPath = pickPath;
        foreach (var preset in store.Presets)
        {
            var choice = new GalleryExportChoice(preset, loc.DisplayName(preset), preset == selected);
            choice.PropertyChanged += OnChoiceChanged;
            Presets.Add(choice);
        }
        Donuts = [new(null, loc["gallery.export_no_donut"]), .. store.Profiles.Select(p => new Choice<ToolbarProfile?>(p, loc.DisplayName(p)))];
        _donut = Donuts[0];
        Recheck();
    }

    public ObservableCollection<GalleryExportChoice> Presets { get; } = [];

    public IReadOnlyList<Choice<ToolbarProfile?>> Donuts { get; }

    [ObservableProperty] private Choice<ToolbarProfile?> _donut;

    partial void OnDonutChanged(Choice<ToolbarProfile?> value) => Recheck();

    /// <summary>What keeps the file from being shared, as the gallery's check says it (English, with where).</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>What the gallery will warn about, in the user's words.</summary>
    public ObservableCollection<PresetGalleryWarning> Warnings { get; } = [];

    [ObservableProperty] private bool _hasProblems;
    [ObservableProperty] private bool _hasWarnings;

    /// <summary>"Ready to share", "Choose at least one preset", "Saved".</summary>
    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _canSave;

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryExportChoice.IsChosen))
            Recheck();
    }

    /// <summary>Builds the file anew from the choice and checks it.</summary>
    private void Recheck()
    {
        Problems.Clear();
        Warnings.Clear();
        var chosen = Presets.Where(c => c.IsChosen).Select(c => c.Preset).ToList();
        if (chosen.Count == 0 && Donut.Value is null)
        {
            _file = null;
            (HasProblems, HasWarnings, CanSave) = (false, false, false);
            Status = _loc["gallery.export_none"];
            return;
        }
        _file = GalleryExport.Build(_store.Presets, chosen, Donut.Value, _loc.Language);
        foreach (var error in _file.Check.Errors)
            Problems.Add(error.Path.Length == 0 ? error.Message : $"{error.Path}: {error.Message}");
        foreach (var warning in _file.Check.Derived?.Warnings ?? [])
            Warnings.Add(new PresetGalleryWarning(warning.Code, _loc[$"gallery.warning.{warning.Code}"], warning.Severity == SharedPresetWarnings.High));
        HasProblems = Problems.Count > 0;
        HasWarnings = Warnings.Count > 0;
        CanSave = _file.Check.Ok;
        Status = CanSave ? _loc.Format("gallery.export_ok", _file.Presets.Count) : _loc["gallery.export_problems"];
    }

    /// <summary>Saves preset.json where the user says.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_file is null || await _pickPath() is not { } path)
            return;
        try
        {
            await File.WriteAllBytesAsync(path, _file.Json);
            Status = _loc["gallery.export_saved"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
    }

    /// <summary>Opens the gallery's submission form in the browser.</summary>
    [RelayCommand]
    private Task OpenFormAsync() => _open(SubmitUri);
}
