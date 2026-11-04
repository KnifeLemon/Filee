using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class AskPagesWindow : Window
{
    public AskPagesWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
        // Ready to type: the range the preset has is selected, so typing replaces it.
        Opened += (_, _) =>
        {
            RangeBox.Focus();
            RangeBox.SelectAll();
        };
    }

    /// <summary>The pages chosen, or null when the window was cancelled or closed.</summary>
    public PageChoice? Result { get; private set; }

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AskPagesViewModel vm || vm.Result() is not { } choice)
            return;
        Result = choice;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
