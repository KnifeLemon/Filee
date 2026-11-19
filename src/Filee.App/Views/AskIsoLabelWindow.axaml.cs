using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class AskIsoLabelWindow : Window
{
    public AskIsoLabelWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
        Opened += (_, _) => { LabelBox.Focus(); LabelBox.SelectAll(); };
    }

    public string? Result { get; private set; }

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AskIsoLabelViewModel { IsValid: true } vm) return;
        Result = vm.Label;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
