using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Presets;

namespace Filee.App.Views;

public partial class AskDvdWindow : Window
{
    public AskDvdWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
        // Ready to type a title's number, or to start with Enter.
        Opened += (_, _) => TitleBox.Focus();
    }

    /// <summary>The title and tracks chosen, or null when the window was cancelled or closed.</summary>
    public DvdChoice? Result { get; private set; }

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AskDvdViewModel vm || vm.Result() is not { } choice)
            return;
        Result = choice;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
