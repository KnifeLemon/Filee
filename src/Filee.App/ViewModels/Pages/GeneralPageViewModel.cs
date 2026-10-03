// General settings: language, start-up, Explorer integration, updates, preset library import/export.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class GeneralPageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly UpdateService _updates;

    public GeneralPageViewModel(UserDataStore store, ILocalizer loc, IPlatformServices platform, UpdateService updates)
    {
        _store = store;
        _loc = loc;
        _platform = platform;
        _updates = updates;

        Languages =
        [
            new Choice<string>("auto", loc["general.language_auto"]),
            .. LocalizationService.Languages.Select(l => new Choice<string>(l.Code, l.NativeName)),
        ];
        _language = Languages.FirstOrDefault(l => l.Value == store.Settings.Language) ?? Languages[0];
        _startWithSystem = store.Settings.StartWithSystem;
        _contextMenu = store.Settings.ContextMenuEnabled;
        _checkForUpdates = store.Settings.CheckForUpdates;
        _noHistory = !store.Settings.KeepHistory;
        OutputLocations = new[] { OutputLocation.SameFolder, OutputLocation.Subfolder, OutputLocation.CustomFolder }
            .Select(v => new Choice<OutputLocation>(v, loc[$"presets.location.{v}"])).ToList();
        var output = store.Settings.DefaultOutput;
        _defaultLocation = OutputLocations.FirstOrDefault(l => l.Value == output.Location) ?? OutputLocations[0];
        _defaultSubfolder = output.SubfolderName;
        _defaultFolder = output.CustomFolder;
        _historyCount = store.History.Count;
        ShowUpdateState(updates.LatestVersion);
        updates.PropertyChanged += OnUpdatesChanged;
        RefreshModernMenu();
    }

    /// <summary>The page is rebuilt on every visit; the update service lives on.</summary>
    public void Dispose() => _updates.PropertyChanged -= OnUpdatesChanged;

    private void OnUpdatesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (UpdateTexts.Affects(e.PropertyName))
            ShowUpdateState(_updates.LatestVersion);
    }

    /// <summary>"Update" in an installed copy, "Download" in a portable one.</summary>
    public string UpdateButtonText => _loc[UpdateTexts.ButtonKey];

    public IReadOnlyList<Choice<string>> Languages { get; }
    public string DataFolder => _store.Directory;
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Choices for the default save location (everything but "Default" itself).</summary>
    public IReadOnlyList<Choice<OutputLocation>> OutputLocations { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDefaultSubfolder), nameof(ShowDefaultFolder))]
    private Choice<OutputLocation> _defaultLocation;

    [ObservableProperty] private string _defaultSubfolder;
    [ObservableProperty] private string _defaultFolder;

    public bool ShowDefaultSubfolder => DefaultLocation.Value == OutputLocation.Subfolder;
    public bool ShowDefaultFolder => DefaultLocation.Value == OutputLocation.CustomFolder;

    partial void OnDefaultLocationChanged(Choice<OutputLocation> value) => Save(s => s.DefaultOutput.Location = value.Value);

    partial void OnDefaultSubfolderChanged(string value) =>
        Save(s => s.DefaultOutput.SubfolderName = string.IsNullOrWhiteSpace(value) ? "converted" : value.Trim());

    partial void OnDefaultFolderChanged(string value) => Save(s => s.DefaultOutput.CustomFolder = value.Trim());

    [ObservableProperty] private Choice<string> _language;
    [ObservableProperty] private bool _startWithSystem;
    [ObservableProperty] private bool _contextMenu;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string? _libraryMessage;

    // Windows 11 top-level Explorer menu entry (see ExplorerMenuRegistration). Only shown when this build ships it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModernMenu), nameof(CanAddModernMenu), nameof(CanRemoveModernMenu))]
    private ModernContextMenuState _modernMenuState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddModernMenu), nameof(CanRemoveModernMenu))]
    private bool _modernMenuBusy;

    [ObservableProperty] private string? _modernMenuStatus;

    public bool ShowModernMenu => ContextMenu && ModernMenuState != ModernContextMenuState.Unsupported;
    public bool CanAddModernMenu => !ModernMenuBusy && ModernMenuState is ModernContextMenuState.Off or ModernContextMenuState.Outdated;
    public bool CanRemoveModernMenu => !ModernMenuBusy && ModernMenuState is ModernContextMenuState.On or ModernContextMenuState.Outdated;

    partial void OnLanguageChanged(Choice<string> value) => Save(s => s.Language = value.Value);
    partial void OnStartWithSystemChanged(bool value) => Save(s => s.StartWithSystem = value);

    partial void OnContextMenuChanged(bool value)
    {
        Save(s => s.ContextMenuEnabled = value);
        OnPropertyChanged(nameof(ShowModernMenu));
    }

    /// <summary>Registers the top-level entry; Windows shows one administrator prompt.</summary>
    [RelayCommand]
    private Task AddModernMenu() => ChangeModernMenu(true);

    [RelayCommand]
    private Task RemoveModernMenu() => ChangeModernMenu(false);

    private async Task ChangeModernMenu(bool enabled)
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        ModernMenuBusy = true;
        ModernMenuStatus = _loc["general.modern_menu_busy"];
        ModernContextMenuResult result;
        try
        {
            result = await _platform.SetModernContextMenuAsync(enabled, exe);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A command that throws would end the app (async void path): report it on the page instead.
            result = new ModernContextMenuResult(false, Error: ex.Message);
        }
        ModernMenuBusy = false;
        RefreshModernMenu();
        if (result.Cancelled)
            ModernMenuStatus = _loc["general.modern_menu_cancelled"];
        else if (!result.Succeeded)
            ModernMenuStatus = _loc.Format(enabled ? "general.modern_menu_failed" : "general.modern_menu_remove_failed", result.Error ?? "");
    }

    private void RefreshModernMenu()
    {
        ModernMenuState = Environment.ProcessPath is { } exe
            ? _platform.GetModernContextMenuState(exe)
            : ModernContextMenuState.Unsupported;
        ModernMenuStatus = _loc[ModernMenuState switch
        {
            ModernContextMenuState.On => "general.modern_menu_on",
            ModernContextMenuState.Outdated => "general.modern_menu_outdated",
            _ => "general.modern_menu_hint",
        }];
    }
    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s.CheckForUpdates = value);

    // Conversion history (home page): don't record it, or clear what is there.
    [ObservableProperty] private bool _noHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryText))]
    [NotifyCanExecuteChangedFor(nameof(ClearHistoryCommand))]
    private int _historyCount;

    [ObservableProperty] private bool _historyCleared;

    public string HistoryText => HistoryCleared ? _loc["general.history_cleared"] : _loc.Format("general.history_count", HistoryCount);

    partial void OnNoHistoryChanged(bool value) => Save(s => s.KeepHistory = !value);

    partial void OnHistoryClearedChanged(bool value) => OnPropertyChanged(nameof(HistoryText));

    private bool CanClearHistory() => HistoryCount > 0;

    [RelayCommand(CanExecute = nameof(CanClearHistory))]
    private void ClearHistory()
    {
        _store.ClearHistory();
        HistoryCount = 0;
        HistoryCleared = true;
    }

    private void Save(Action<AppSettings> change)
    {
        change(_store.Settings);
        _store.SaveSettings();
    }

    [RelayCommand]
    private void OpenDataFolder() => _platform.RevealInFileManager(_store.Directory);

    [RelayCommand]
    private async Task CheckNow() => ShowUpdateState(await _updates.CheckAsync(), afterCheck: true);

    /// <summary>Downloads and runs the new installer, which updates Filee in place (a portable copy opens the download page).</summary>
    [RelayCommand]
    private Task DownloadUpdate() => _updates.UpdateAsync();

    private void ShowUpdateState(string? newer, bool afterCheck = false)
    {
        UpdateAvailable = newer is not null && !_updates.IsBusy;
        UpdateStatus = UpdateTexts.Status(_updates, _loc) is { } progress ? progress
            : newer is not null ? _loc.Format("general.update_available", newer)
            : afterCheck ? _loc.Format("general.update_none", UpdateService.CurrentVersion)
            : _loc.Format("about.version", UpdateService.CurrentVersion);
    }

    public void Export(string path)
    {
        _store.Export(path);
        LibraryMessage = Path.GetFileName(path);
    }

    public void Import(string path)
    {
        try
        {
            var count = _store.Import(path);
            LibraryMessage = _loc.Format("general.imported", count);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            LibraryMessage = ex.Message;
        }
    }

    public void ResetLibrary() => _store.ResetLibrary();
}
