using Avalonia.Controls;
using Filee.App.ViewModels.Pages;

namespace Filee.App.Views.Pages;

public partial class PresetsPage : UserControl
{
    private const int FixedMenuItems = 3; // "Empty preset", the separator and the heading

    public PresetsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => FillAddMenu();
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
