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

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EngineItemViewModel engine } || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            engine.CustomPath = path;
    }
}
