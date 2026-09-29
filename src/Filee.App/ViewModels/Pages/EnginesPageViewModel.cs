// Conversion engines: optional downloads, availability, priority order and custom executable paths.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class EngineItemViewModel : ObservableObject
{
    private readonly Action<EngineItemViewModel, string> _pathChanged;

    public EngineItemViewModel(IConverter converter, EngineStatus status, string? customPath, ILocalizer loc,
        Action<EngineItemViewModel, string> pathChanged)
    {
        Converter = converter;
        _pathChanged = pathChanged;
        IsAvailable = status.IsAvailable;
        StatusText = status.IsAvailable ? loc["engines.available"] : loc[status.ReasonKey ?? "engines.unavailable"];
        Detail = status.Detail;
        Conversions = loc.Format("engines.conversions", converter.Edges.Count);
        _customPath = customPath ?? "";
        // Only command line engines can be pointed at a custom executable.
        SupportsCustomPath = converter.Id is "libreoffice" or "rhwp" or "pandoc";
    }

    public IConverter Converter { get; }
    public string Name => Converter.DisplayName;
    public bool IsAvailable { get; }
    public string StatusText { get; }
    public string? Detail { get; }
    public string Conversions { get; }
    public bool SupportsCustomPath { get; }

    [ObservableProperty] private string _customPath;

    partial void OnCustomPathChanged(string value) => _pathChanged(this, value);
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
            Engines.Add(new EngineItemViewModel(converter, _catalog.StatusOf(converter),
                _store.Settings.EnginePaths.GetValueOrDefault(converter.Id), _loc, OnPathChanged));
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

    private void OnPathChanged(EngineItemViewModel item, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            _store.Settings.EnginePaths.Remove(item.Converter.Id);
        else
            _store.Settings.EnginePaths[item.Converter.Id] = path.Trim().Trim('"');
        _store.SaveSettings();
    }
}
