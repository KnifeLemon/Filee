// Cards are admitted by measured height, without skipping older cards or exposing waiting controls.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Filee.App.Controls;

namespace Filee.App.Tests;

public sealed class ToastStackPanelTests
{
    [AvaloniaFact]
    public void Measured_heights_reserve_the_footer_only_when_cards_wait()
    {
        var panel = Stack(300, 90, 90, 90);
        Layout(panel);
        Assert.Equal(3, panel.VisibleCount);
        Assert.Equal(0, panel.PendingCount);
        panel.Children.Add(new Border { Height = 90 });
        Layout(panel);
        Assert.Equal(2, panel.VisibleCount);
        Assert.Equal(2, panel.PendingCount);
        Assert.All(panel.Children.Take(2), c => Assert.True(c.IsEnabled));
        Assert.All(panel.Children.Skip(2), c => Assert.False(c.IsEnabled));
        panel.AvailableHeight = 400;
        Layout(panel);
        Assert.Equal(4, panel.VisibleCount);
        Assert.Equal(0, panel.PendingCount);
    }

    [AvaloniaFact]
    public void A_shorter_new_card_does_not_skip_an_older_waiting_card()
    {
        var panel = Stack(300, 90, 180, 90);
        Layout(panel);
        Assert.Equal(1, panel.VisibleCount);
        panel.Children.RemoveAt(0);
        Layout(panel);
        Assert.Equal(2, panel.VisibleCount);
    }

    [AvaloniaFact]
    public void One_oversized_card_is_admitted_for_the_scroll_fallback()
    {
        var panel = Stack(150, 500, 50);
        Layout(panel);
        Assert.Equal(1, panel.VisibleCount);
        Assert.Equal(1, panel.PendingCount);
        Assert.Equal(500, panel.DesiredSize.Height);
    }

    private static ToastStackPanel Stack(double height, params double[] cards)
    {
        var panel = new ToastStackPanel { AvailableHeight = height };
        foreach (var card in cards) panel.Children.Add(new Border { Height = card });
        return panel;
    }

    private static void Layout(ToastStackPanel panel)
    {
        panel.Measure(new Size(300, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
    }
}
