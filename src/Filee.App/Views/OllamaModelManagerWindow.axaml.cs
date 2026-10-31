using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class OllamaModelManagerWindow : Window
{
    public OllamaModelManagerWindow()
    {
        InitializeComponent();
        DataContext = new OllamaModelManagerViewModel();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
