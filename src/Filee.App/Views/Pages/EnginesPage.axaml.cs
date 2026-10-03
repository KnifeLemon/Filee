using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;

namespace Filee.App.Views.Pages;

public partial class EnginesPage : UserControl
{
    public EnginesPage() => InitializeComponent();

    /// <summary>Picks the program of a copy the user already has (ffmpeg.exe, ebook-convert.exe, …) and uses it.</summary>
    private async void OnUseOwnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EnginePackageState package } || DataContext is not EnginesPageViewModel vm ||
            TopLevel.GetTopLevel(this) is not Window window)
            return;
        var loc = AppHost.Get<ILocalizer>();
        var programs = Filee.Engines.Infrastructure.EngineEnvironment.OwnCopyPrograms[package.Package.Id];
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = loc.Format("engines.use_own_title", package.Name),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(programs[0]) { Patterns = [programs[0]] }, FilePickerFileTypes.All],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;
        if (!vm.UseOwnCopy(package, path))
            await AppHost.Get<WindowService>().MessageAsync(window, loc.Format("engines.use_own_missing", string.Join(", ", programs)));
    }

    private void OnStopOwnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EnginePackageState package } && DataContext is EnginesPageViewModel vm)
            vm.StopUsingOwnCopy(package);
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EnginePackageState package } || DataContext is not EnginesPageViewModel vm ||
            TopLevel.GetTopLevel(this) is not Window window)
            return;
        var loc = AppHost.Get<ILocalizer>();
        if (!await AppHost.Get<WindowService>().ConfirmAsync(window, loc.Format("engines.remove_confirm", package.Name), destructive: true))
            return;
        if (!vm.Remove(package))
            await AppHost.Get<WindowService>().MessageAsync(window, loc["engines.remove_failed"]);
    }
}
