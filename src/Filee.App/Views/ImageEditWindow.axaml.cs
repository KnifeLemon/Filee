using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class ImageEditWindow : Window
{
    public ImageEditWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
        AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
        // The format first, so the keys work at once; Tab goes on down the window.
        Opened += (_, _) => TargetChoice.Focus();
    }

    /// <summary>
    /// Before any focused control: Enter converts, as in the crop window, also on a closed list (which would open) or the
    /// grayscale box (which would tick). An open list keeps Enter to pick, a number being typed takes it first so it
    /// counts (then the Convert button converts), and the Cancel and Convert buttons keep their own.
    /// </summary>
    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;
        var source = e.Source as Visual;
        if (source?.FindAncestorOfType<NumericUpDown>(includeSelf: true) is not null
            || source is Button and not ToggleButton
            || this.GetVisualDescendants().OfType<ComboBox>().Any(list => list.IsDropDownOpen))
            return;
        Convert();
        e.Handled = true;
    }

    private void OnConvert(object? sender, RoutedEventArgs e) => Convert();

    private void Convert()
    {
        if (DataContext is not ImageEditViewModel vm || vm.Pictures.Count == 0) return;
        vm.Convert();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
