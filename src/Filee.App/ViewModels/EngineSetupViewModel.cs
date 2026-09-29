// First-run engine choice: the installer only contains the small engines, so right after installing Filee asks
// which large ones to download (like optional components in a classic setup wizard).

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;

namespace Filee.App.ViewModels;

public sealed partial class EngineSetupViewModel : ObservableObject
{
    private readonly EngineDownloadService _downloads;
    private readonly ILocalizer _loc;

    public EngineSetupViewModel(EngineDownloadService downloads, ILocalizer loc)
    {
        _downloads = downloads;
        _loc = loc;
        foreach (var package in Packages)
        {
            package.Selected = !package.IsInstalled;
            package.PropertyChanged += OnPackageChanged;
        }
    }

    public IReadOnlyList<EnginePackageState> Packages => _downloads.Packages;

    /// <summary>Installation was started from this window (checkboxes lock, progress shows).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChoose), nameof(CloseText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _started;

    public bool CanChoose => !Started;

    public string SelectionText
    {
        get
        {
            var selected = Packages.Where(p => p.Selected && p.CanInstall).ToList();
            return selected.Count == 0
                ? _loc["setup.nothing_selected"]
                : _loc.Format("setup.selection", EnginePackageState.FormatBytes(selected.Sum(p => Filee.Engines.Infrastructure.EngineDownloads.DownloadSize(p.Package))));
        }
    }

    /// <summary>"Later" before starting, "Continue in the background" while downloading, then "Done".</summary>
    public string CloseText => !Started ? _loc["setup.later"] : _downloads.IsBusy ? _loc["setup.background"] : _loc["setup.done"];

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    private bool CanInstall() => !Started && Packages.Any(p => p.Selected && p.CanInstall);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private void Install()
    {
        Started = true;
        // The service installs one package at a time; the others wait in its queue.
        foreach (var package in Packages.Where(p => p.Selected && p.CanInstall))
            _ = _downloads.InstallAsync(package);
    }

    [RelayCommand]
    private void Close()
    {
        foreach (var package in Packages)
            package.PropertyChanged -= OnPackageChanged;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPackageChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CloseText));
        InstallCommand.NotifyCanExecuteChanged();
    }
}
