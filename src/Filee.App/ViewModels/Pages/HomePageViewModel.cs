// Home: drop zone (handled in the view) and recent conversions.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class HistoryItemViewModel(HistoryEntry entry, ILocalizer loc, IPlatformServices platform) : ObservableObject
{
    public string Title { get; } = entry.PresetName;

    public string Subtitle { get; } = entry.Sources.Count == 1
        ? Path.GetFileName(entry.Sources[0])
        : loc.Format("home.files", entry.Sources.Count);

    public string Time { get; } = entry.FinishedAt.Date == DateTime.Today
        ? entry.FinishedAt.ToString("t")
        : entry.FinishedAt.ToString("d");

    public bool Succeeded { get; } = entry.State == JobState.Completed;
    public bool HasErrors { get; } = entry.Errors.Count > 0 || entry.State == JobState.Failed;
    public string? Errors { get; } = entry.Errors.Count == 0 ? null : string.Join(Environment.NewLine, entry.Errors);
    public bool HasOutputs { get; } = entry.Outputs.Any(Exists);

    [RelayCommand]
    private void OpenFolder()
    {
        // An output may be a folder ("Extract"): revealing it opens the extracted files.
        var output = entry.Outputs.FirstOrDefault(Exists);
        if (output is not null)
            platform.RevealInFileManager(output);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}

public sealed partial class HomePageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly Services.Triggers.TriggerService? _triggers;

    public HomePageViewModel(UserDataStore store, ILocalizer loc, IPlatformServices platform, Services.Triggers.TriggerService? triggers = null)
    {
        _store = store;
        _loc = loc;
        _platform = platform;
        _triggers = triggers;
        if (triggers is not null)
            triggers.StatusChanged += OnTriggerStatusChanged;
        var drag = store.Settings.Triggers.FirstOrDefault(t => t.Enabled && t.Kind == TriggerKind.Drag);
        DropHint = loc.Format(OperatingSystem.IsMacOS() ? "home.drop_hint_macos" : OperatingSystem.IsLinux() ? "home.drop_hint_linux" : "home.drop_hint", drag is null ? "—" : GestureText.Modifiers(drag.Modifiers));
        store.HistoryChanged += OnHistoryChanged;
        Reload();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Reload);

    public void Dispose()
    {
        _store.HistoryChanged -= OnHistoryChanged;
        if (_triggers is not null)
            _triggers.StatusChanged -= OnTriggerStatusChanged;
    }

    private void OnTriggerStatusChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(NeedsAccessibility));

    /// <summary>macOS: gestures wait for the Accessibility permission (the reminder above the drop zone).</summary>
    public bool NeedsAccessibility => _triggers?.NeedsPermission == true;

    [RelayCommand]
    private void OpenAccessibility()
    {
        if (OperatingSystem.IsMacOS() && _platform is Filee.Platform.MacOS.MacOSPlatformServices mac)
            mac.OpenAccessibilitySettings();
    }

    public string DropHint { get; }

    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    [ObservableProperty] private bool _isEmpty;

    private void Reload()
    {
        History.Clear();
        foreach (var entry in _store.History.Take(40))
            History.Add(new HistoryItemViewModel(entry, _loc, _platform));
        IsEmpty = History.Count == 0;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _store.ClearHistory();
        Reload();
    }
}
