// General settings: language, start-up, Explorer integration, updates, preset library import/export.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class GeneralPageViewModel : ObservableObject
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
        ShowUpdateState(updates.LatestVersion);
    }

    public IReadOnlyList<Choice<string>> Languages { get; }
    public string DataFolder => _store.Directory;
    public bool IsWindows => OperatingSystem.IsWindows();

    [ObservableProperty] private Choice<string> _language;
    [ObservableProperty] private bool _startWithSystem;
    [ObservableProperty] private bool _contextMenu;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string? _libraryMessage;

    partial void OnLanguageChanged(Choice<string> value) => Save(s => s.Language = value.Value);
    partial void OnStartWithSystemChanged(bool value) => Save(s => s.StartWithSystem = value);
    partial void OnContextMenuChanged(bool value) => Save(s => s.ContextMenuEnabled = value);
    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s.CheckForUpdates = value);

    private void Save(Action<AppSettings> change)
    {
        change(_store.Settings);
        _store.SaveSettings();
    }

    [RelayCommand]
    private void OpenDataFolder() => _platform.RevealInFileManager(_store.Directory);

    [RelayCommand]
    private async Task CheckNow() => ShowUpdateState(await _updates.CheckAsync(), afterCheck: true);

    /// <summary>Opens the latest release page; the new installer updates Filee in place.</summary>
    [RelayCommand]
    private void DownloadUpdate() => _updates.OpenDownloadPage();

    private void ShowUpdateState(string? newer, bool afterCheck = false)
    {
        UpdateAvailable = newer is not null;
        UpdateStatus = newer is not null ? _loc.Format("general.update_available", newer)
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
