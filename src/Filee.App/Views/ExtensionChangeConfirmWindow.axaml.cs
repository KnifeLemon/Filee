using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;

namespace Filee.App.Views;

public partial class ExtensionChangeConfirmWindow : Window
{
    public ExtensionChangeConfirmWindow()
    {
        InitializeComponent();
        Motion.Track(this);
    }

    public ExtensionChangeConfirmWindow(string message) : this()
    {
        MessageText.Text = message;
    }

    /// <summary>True if "Always convert directly without asking" is checked.</summary>
    public bool DontAskAgain => DontAsk.IsChecked == true;

    private void OnConvert(object? sender, RoutedEventArgs e)
    {
        Tag = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Tag = false;
        Close();
    }
}
