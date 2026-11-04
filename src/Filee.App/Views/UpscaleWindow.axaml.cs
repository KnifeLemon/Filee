using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;
using Filee.Engines.Upscale;

namespace Filee.App.Views;

public partial class UpscaleWindow : Window
{
    public UpscaleWindow()
    {
        InitializeComponent();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpscaleViewModel vm && vm.IsProcessing)
        {
            vm.Cancel();
        }
        Close();
    }

    private async void OnDeleteModel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not UpscaleViewModel vm || vm.SelectedModel is null)
            return;

        var loc = AppHost.Get<ILocalizer>();
        var windows = AppHost.Get<WindowService>();
        var message = loc.Format("engines.model.delete_confirm", vm.SelectedModel.Name);
        if (await windows.ConfirmAsync(this, message, destructive: true))
        {
            UpscaleModels.Uninstall(vm.SelectedModel.Id);
            vm.NotifyModelStatusChanged();
        }
    }
}
