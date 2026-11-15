using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.ViewModels.Pages;

namespace Filee.App.Views.Pages;

public partial class PresetsPage : UserControl
{
    private const int FixedMenuItems = 3; // "Empty preset", the separator and the heading
    private PresetsPageViewModel? _viewModel;

    public PresetsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Unsubscribe();
            FillAddMenu();
            if (TopLevel.GetTopLevel(this) is not null)
                Subscribe();
        };
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (_viewModel is not null || DataContext is not PresetsPageViewModel vm)
            return;
        _viewModel = vm;
        vm.ValidationFailed += RevealInvalidField;
    }

    private void Unsubscribe()
    {
        if (_viewModel is not null)
            _viewModel.ValidationFailed -= RevealInvalidField;
        _viewModel = null;
    }

    private void RevealInvalidField(object? sender, EventArgs e)
    {
        // Binding updates and the editor's conditional cards must be laid out before scrolling to their error.
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this) is null)
                return;
            var error = PresetEditor.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.IsEffectivelyVisible && t.Classes.Contains("danger") && !string.IsNullOrEmpty(t.Text));
            error?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>One "New" menu item per ready-made preset, so the menu follows the list (#553).</summary>
    private void FillAddMenu()
    {
        var menu = (MenuFlyout)AddPresetButton.Flyout!; // a flyout isn't in the page's name scope
        while (menu.Items.Count > FixedMenuItems)
            menu.Items.RemoveAt(menu.Items.Count - 1);
        if (DataContext is not PresetsPageViewModel vm)
            return;
        foreach (var place in vm.PlacePresets)
            menu.Items.Add(new MenuItem { Header = place.Name, Command = vm.AddPlaceCommand, CommandParameter = place.Preset });
    }
}
