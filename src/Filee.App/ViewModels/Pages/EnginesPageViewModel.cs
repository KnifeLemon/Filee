// Conversion engines: optional downloads, availability and priority order. Filee only uses its own engine copies,
// so there are no paths to configure.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed class EngineItemViewModel(IConverter converter, EngineStatus status, ILocalizer loc)
{
    public IConverter Converter { get; } = converter;
    public string Name => Converter.DisplayName;
    public bool IsAvailable { get; } = status.IsAvailable;
    public string StatusText { get; } = status.IsAvailable ? loc["engines.available"] : loc[status.ReasonKey ?? "engines.unavailable"];

    /// <summary>Path of the program or other technical detail, shown as a tooltip.</summary>
    public string? Detail { get; } = status.Detail;

    /// <summary>"Markdig 1.4.0", "0.8.6" — what the engine reports; empty for engines that aren't installed.</summary>
    public string? Version { get; } = status.Version;

    /// <summary>What the engine converts (engine.&lt;id&gt;.description), or null when there is no text for it.</summary>
    public string? Description { get; } = Describe(loc, converter.Id);

    public string Conversions { get; } = loc.Format("engines.conversions", converter.Edges.Count);

    private static string? Describe(ILocalizer loc, string id)
    {
        var key = $"engine.{id}.description";
        var text = loc[key];
        return text == key ? null : text;
    }
}

public sealed partial class EnginesPageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ConverterCatalog _catalog;
    private readonly ILocalizer _loc;
    private readonly EngineDownloadService _downloads;

    public EnginesPageViewModel(UserDataStore store, ConverterCatalog catalog, ILocalizer loc, EngineDownloadService downloads)
    {
        _store = store;
        _catalog = catalog;
        _loc = loc;
        _downloads = downloads;
        _catalog.Changed += OnCatalogChanged;
        Reload();
    }

    /// <summary>Optional engines that are downloaded on demand.</summary>
    public IReadOnlyList<EnginePackageState> Packages => _downloads.Packages;

    public ObservableCollection<EngineItemViewModel> Engines { get; } = [];

    [RelayCommand]
    private Task Install(EnginePackageState package) => _downloads.InstallAsync(package);

    [RelayCommand]
    private void Cancel(EnginePackageState package) => _downloads.Cancel(package);

    /// <summary>Removes a package (after the view asked for confirmation). False when its files are in use.</summary>
    public bool Remove(EnginePackageState package) => _downloads.Uninstall(package);

    // Installing or removing an engine changes the list below (status, supported conversions).
    private void OnCatalogChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Reload);

    public void Dispose() => _catalog.Changed -= OnCatalogChanged;

    private void Reload()
    {
        Engines.Clear();
        foreach (var converter in _catalog.Ordered)
            Engines.Add(new EngineItemViewModel(converter, _catalog.StatusOf(converter), _loc));
    }

    [RelayCommand]
    private void MoveUp(EngineItemViewModel item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(EngineItemViewModel item) => Move(item, +1);

    [RelayCommand]
    private void Recheck()
    {
        _downloads.RefreshStatus();
        _catalog.Refresh();
    }

    private void Move(EngineItemViewModel item, int delta)
    {
        var index = Engines.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Engines.Count)
            return;
        Engines.Move(index, target);
        _store.Settings.EnginePriority = Engines.Select(e => e.Converter.Id).ToList();
        _store.SaveSettings();
    }
}
