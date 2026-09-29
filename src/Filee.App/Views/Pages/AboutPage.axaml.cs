using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.ViewModels.Pages;

namespace Filee.App.Views.Pages;

public partial class AboutPage : UserControl
{
    public AboutPage() => InitializeComponent();

    private async void OnOpenRepository(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutPageViewModel vm && TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(new Uri(vm.RepositoryUrl));
    }
}
