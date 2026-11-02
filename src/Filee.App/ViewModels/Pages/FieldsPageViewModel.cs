using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

/// <summary>Settings → Fields: one switch per field that can be turned off (docs/FORMAT-RULES.md, #806).</summary>
public sealed class FieldsPageViewModel : ObservableObject
{
    public FieldsPageViewModel(UserDataStore store, ILocalizer loc)
    {
        Fields = FormatFields.All.Where(f => f.CanTurnOff)
            .Select(f => new FieldSwitchViewModel(f, store, loc)).ToList();
        // The fields that can't be turned off are the categories, so their names are the category names.
        AlwaysShown = loc.Format("fields.always_shown", string.Join(", ", FormatFields.All.Where(f => !f.CanTurnOff)
            .Select(f => loc[$"format.category.{Enum.Parse<FormatCategory>(f.Id, true)}"])));
    }

    public IReadOnlyList<FieldSwitchViewModel> Fields { get; }
    public string AlwaysShown { get; }
}

/// <summary>One field's switch: on removes its id from <see cref="AppSettings.TurnedOffFields"/>, off adds it, and either saves.</summary>
public sealed partial class FieldSwitchViewModel : ObservableObject
{
    private readonly UserDataStore _store;

    public FieldSwitchViewModel(FormatField field, UserDataStore store, ILocalizer loc)
    {
        _store = store;
        Id = field.Id;
        Name = loc[$"fields.name.{Id}"];
        // Only formats this switch can hide: one that is also in a field that can't be turned off (TGA in Image, DXF in
        // Vector) stays whatever the switches say.
        var formats = FormatRegistry.Known
            .Where(f => f.Writable && FormatFields.Of(f).Contains(field) && FormatFields.Of(f).All(x => x.CanTurnOff))
            .Select(f => f.DisplayName).ToList();
        Formats = formats.Count == 0 ? "" : loc.Format("fields.formats",
            string.Join(", ", formats.Take(6)) + (formats.Count > 6 ? "\u2026" : ""));
        // Set the field, not the property: reading the saved list must not save it.
        _isOn = !store.Settings.TurnedOffFields.Contains(Id, StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; }
    public string Name { get; }
    public string Formats { get; }

    [ObservableProperty] private bool _isOn;

    // Only this field's id is added or removed: ids this version doesn't know (a newer Filee's fields) stay in the list.
    partial void OnIsOnChanged(bool value)
    {
        var off = _store.Settings.TurnedOffFields;
        if (value)
            off.RemoveAll(id => string.Equals(id, Id, StringComparison.OrdinalIgnoreCase));
        else if (!off.Contains(Id, StringComparer.OrdinalIgnoreCase))
            off.Add(Id);
        _store.SaveSettings();
    }
}
