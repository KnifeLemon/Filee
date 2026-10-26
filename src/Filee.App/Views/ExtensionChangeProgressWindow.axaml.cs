using Avalonia.Controls;
using Avalonia.Input;
using Filee.App.Services;

namespace Filee.App.Views;

public partial class ExtensionChangeProgressWindow : Window
{
    public ExtensionChangeProgressWindow()
    {
        InitializeComponent();
        Motion.Track(this);
    }

    public ExtensionChangeProgressWindow(string message) : this()
    {
        MessageText.Text = message;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
