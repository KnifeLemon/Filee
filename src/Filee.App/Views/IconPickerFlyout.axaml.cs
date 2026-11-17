using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.ViewModels;

namespace Filee.App.Views;

/// <summary>
/// The icon picker shown in a flyout: the search box takes the focus, ↓ goes to the icons, the arrows move between
/// them as they are laid out, Enter in the search picks the first match, and a pick (or Esc) closes the flyout.
/// </summary>
public partial class IconPickerFlyout : UserControl
{
    public IconPickerFlyout()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // After the button's command ran: the pick is made, the flyout can go.
        AddHandler(Button.ClickEvent, (_, _) => Dispatcher.UIThread.Post(Close), RoutingStrategies.Bubble, handledEventsToo: true);
        Loaded += (_, _) => Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
    }

    /// <summary>The icon buttons in reading order (group by group, row by row).</summary>
    internal IReadOnlyList<Button> IconButtons =>
        SectionList.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();

    private void Close() => this.FindLogicalAncestorOfType<Popup>()?.Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Source is TextBox)
        {
            if (e.Key == Key.Down && IconButtons.FirstOrDefault() is { } first)
            {
                Focus(first);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && DataContext is IconPickerViewModel { First: { } icon } vm)
            {
                vm.PickCommand.Execute(icon);
                Close();
                e.Handled = true;
            }
            return;
        }
        if (e.Source is not Button button || !IconButtons.Contains(button))
            return;
        if (Neighbour(button, e.Key) is { } next)
            Focus(next);
        else if (e.Key == Key.Up)
            SearchBox.Focus(NavigationMethod.Directional);
        else if (e.Key is not (Key.Down or Key.Left or Key.Right))
            return;
        e.Handled = true;
    }

    private static void Focus(Button button)
    {
        button.Focus(NavigationMethod.Directional);
        button.BringIntoView();
    }

    /// <summary>
    /// The icon beside <paramref name="from"/> in the direction of <paramref name="key"/>: ← → the previous or next in
    /// reading order, ↑ ↓ the nearest in the row above or below (across groups). Null at the edge, or for another key.
    /// </summary>
    internal Button? Neighbour(Button from, Key key)
    {
        var buttons = IconButtons;
        var index = buttons.ToList().IndexOf(from);
        if (index < 0)
            return null;
        switch (key)
        {
            case Key.Left:
                return index > 0 ? buttons[index - 1] : null;
            case Key.Right:
                return index < buttons.Count - 1 ? buttons[index + 1] : null;
            case Key.Up or Key.Down:
                var at = CenterOf(from);
                var sign = key == Key.Down ? 1 : -1;
                // The nearest row that way, then the icon in it nearest across.
                var below = buttons.Select(b => (Button: b, Center: CenterOf(b)))
                    .Where(b => (b.Center.Y - at.Y) * sign > from.Bounds.Height / 2)
                    .ToList();
                if (below.Count == 0)
                    return null;
                var row = below.Min(b => Math.Abs(b.Center.Y - at.Y));
                return below.Where(b => Math.Abs(Math.Abs(b.Center.Y - at.Y) - row) < 1)
                    .MinBy(b => Math.Abs(b.Center.X - at.X)).Button;
            default:
                return null;
        }
    }

    private Point CenterOf(Button button) =>
        button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), SectionList) ?? default;
}
