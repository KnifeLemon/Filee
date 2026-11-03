using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class OllamaModelManagerWindow : Window
{
    public OllamaModelManagerWindow()
    {
        InitializeComponent();
        var vm = new OllamaModelManagerViewModel();
        vm.OwnerWindow = this;
        DataContext = vm;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
