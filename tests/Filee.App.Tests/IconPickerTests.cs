// The icon picker of the preset and group editors: its search, what Default / None / an icon return, the current
// choice marked, and the keyboard (↓ from the search, the arrows across the grid, Enter, Esc).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class IconPickerTests
{
    private static (IconPickerViewModel Vm, List<string?> Picked) Make(string? current = null)
    {
        TestServices.EnsureInitialized("en");
        var picked = new List<string?>();
        return (new IconPickerViewModel(AppHost.Get<ILocalizer>(), current, picked.Add), picked);
    }

    [AvaloniaFact]
    public void Every_icon_is_listed_under_its_translated_group_and_the_search_filters_them()
    {
        var (vm, _) = Make();
        Assert.Equal(SliceIcons.Keys.Order(), vm.Sections.SelectMany(s => s.Icons).Select(i => i.Key).Distinct().Order());
        Assert.Equal("Files & folders", vm.Sections[0].Name);
        Assert.Equal("Image edit outline", vm.Sections.SelectMany(s => s.Icons).Single(i => i.Key == "image-edit-outline").Name);

        vm.Search = "background";
        Assert.Contains("auto-fix", vm.Sections.SelectMany(s => s.Icons).Select(i => i.Key));
        Assert.Equal(SliceIcons.Search("background").Order(), vm.Sections.SelectMany(s => s.Icons).Select(i => i.Key).Distinct().Order());
        Assert.Equal(vm.Sections[0].Icons[0], vm.First);

        vm.Search = "zzzz";
        Assert.True(vm.IsEmpty);
        Assert.Null(vm.First);
    }

    [AvaloniaFact]
    public void Default_none_and_an_icon_return_their_values_and_the_current_one_is_marked()
    {
        var (vm, picked) = Make("crop");
        Assert.False(vm.IsDefault);
        Assert.True(vm.Sections.SelectMany(s => s.Icons).Single(i => i.IsCurrent).Key == "crop");
        vm.PickDefaultCommand.Execute(null);
        vm.PickNoneCommand.Execute(null);
        vm.PickCommand.Execute(vm.Sections[0].Icons[0]);
        Assert.Equal([null, Preset.NoIcon, vm.Sections[0].Icons[0].Key], picked);

        Assert.True(Make().Vm.IsDefault);
        Assert.True(Make(Preset.NoIcon).Vm.IsNone);
        Assert.True(Make("an-icon-from-a-newer-filee").Vm.IsDefault); // unknown here: the default shows
    }

    [AvaloniaFact]
    public void The_keyboard_moves_through_the_grid_picks_and_closes()
    {
        var (vm, picked) = Make();
        var picker = new IconPickerFlyout { DataContext = vm };
        var button = new Button { Content = "Icon", Flyout = new Flyout { Content = picker } };
        var window = new Window { Content = button, Width = 500, Height = 700 };
        window.Show();
        button.Flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Assert.True(picker.SearchBox.IsFocused);

        // ↓ from the search: the first icon. → the next. ↓ the one under it in the next row. ↑ back.
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        var icons = picker.IconButtons;
        Assert.True(icons[0].IsFocused);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.True(icons[1].IsFocused);
        double Top(Button b) => b.TranslatePoint(default, picker)!.Value.Y;
        var perRow = icons.TakeWhile(b => Top(b) == Top(icons[0])).Count();
        Assert.InRange(perRow, 6, 10);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.True(icons[1 + perRow].IsFocused);
        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Assert.True(icons[1].IsFocused);
        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Assert.True(picker.SearchBox.IsFocused);

        // Enter on an icon picks it and closes the flyout.
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([vm.Sections[0].Icons[1].Key], picked);
        Assert.False(button.Flyout.IsOpen);

        // Enter in the search picks the first match; Esc closes without a pick.
        button.Flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        vm.Search = "background";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("auto-fix", picked[^1]);
        button.Flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(button.Flyout.IsOpen);
        Assert.Equal(2, picked.Count);
        window.Close();
    }
}
