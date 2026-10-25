// The donut for screen readers and the keyboard: every slice is a list item with its label and the reason it's
// disabled, a screen reader can select and invoke slices, and the editor's keyboard chooses, edits and moves them.

using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Filee.App.Controls;

namespace Filee.App.Tests;

[Collection("Motion")]
public class DonutAccessibilityTests
{
    private static (DonutMenu Donut, Window Window) Open(bool editMode = false)
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu
        {
            Items =
            [
                new DonutItem { Id = "png", Label = "PNG" },
                new DonutItem { Id = "pdf", Label = "Scan", Caption = "PDF" },
                new DonutItem { Id = "hwp", Label = "HWP", IsEnabled = false, DisabledReason = "Needs rhwp" },
                new DonutItem { Id = "group", Label = "More", Children = [new DonutItem { Id = "webp", Label = "WEBP" }] },
            ],
            IsEditMode = editMode,
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (donut, window);
    }

    [AvaloniaFact]
    public void Screen_readers_find_every_slice_with_its_name_and_why_it_is_disabled()
    {
        var (donut, window) = Open();

        var peer = ControlAutomationPeer.CreatePeerForElement(donut);
        var slices = peer.GetChildren();

        Assert.Equal(AutomationControlType.List, peer.GetAutomationControlType());
        Assert.Equal(["PNG", "Scan, PDF", "HWP", "More"], slices.Select(s => s.GetName()));
        Assert.All(slices, s => Assert.Equal(AutomationControlType.ListItem, s.GetAutomationControlType()));
        Assert.False(slices[2].IsEnabled());
        Assert.Equal("Needs rhwp", slices[2].GetHelpText());
        Assert.True(slices[0].GetBoundingRectangle().Width > 0);
        window.Close();
    }

    [AvaloniaFact]
    public void A_slice_a_screen_reader_selects_becomes_the_keyboards()
    {
        var (donut, window) = Open();
        var peer = ControlAutomationPeer.CreatePeerForElement(donut);
        var slices = peer.GetChildren();

        ((ISelectionItemProvider)slices[1]).Select();

        Assert.Equal(1, donut.ActiveIndex);
        Assert.True(((ISelectionItemProvider)slices[1]).IsSelected);
        Assert.Same(slices[1], Assert.Single(((ISelectionProvider)peer).GetSelection()));
        window.Close();
    }

    [AvaloniaFact]
    public void The_editors_keyboard_chooses_edits_and_moves_slices()
    {
        var (donut, window) = Open(editMode: true);
        var edited = new List<int>();
        var moved = new List<(int From, int To)>();
        donut.SegmentContextRequested += (_, index) => edited.Add(index);
        donut.SegmentMoveRequested += (_, move) => moved.Add(move);

        Assert.True(donut.HandleEditKey(Key.Right));
        Assert.True(donut.HandleEditKey(Key.Right));
        Assert.Equal(1, donut.ActiveIndex);
        Assert.True(donut.HandleEditKey(Key.Enter));
        Assert.True(donut.HandleEditKey(Key.Left, KeyModifiers.Control));
        Assert.False(donut.HandleEditKey(Key.Tab)); // left to the page: the focus moves on

        Assert.Equal([1], edited);
        Assert.Equal([(1, 0)], moved);
        Assert.Equal(0, donut.ActiveIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void Outside_the_editor_the_editors_keys_do_nothing()
    {
        var (donut, window) = Open();

        Assert.False(donut.HandleEditKey(Key.Right));

        window.Close();
    }

    [AvaloniaFact]
    public void High_contrast_draws_the_ring_in_the_themes_colours()
    {
        var (donut, window) = Open();

        donut.Classes.Add("high-contrast");
        RenderTests.Pump();

        Assert.True(donut.IsHighContrast);
        RenderTests.Save(window, "donut-high-contrast-en.png");
        window.Close();
    }
}
