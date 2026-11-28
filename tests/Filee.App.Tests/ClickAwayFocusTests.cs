// A click on empty space takes the focus off a text box (ClickAwayFocus). A click in the text box itself, on a control that
// is meant to leave the focus where it is, or on one that handles the press itself (the annotation canvas) does not.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Filee.App.Tests;

public class ClickAwayFocusTests
{
    private static void Pump()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static Point Middle(Control control, Visual to) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), to)!.Value;

    private static void Click(Window window, Control target)
    {
        var at = Middle(target, window);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Pump();
    }

    private static object? Focused(Window window) => window.FocusManager!.GetFocusedElement();

    private sealed record Setup(Window Window, TextBox Box, TextBox Other, Button Quiet, Border Handled, Border Empty);

    /// <summary>Two text boxes, a button that does not take focus, a border that handles the press, and empty space.</summary>
    private static Setup Show()
    {
        TestServices.EnsureInitialized("en");
        var box = new TextBox { Width = 200 };
        var other = new TextBox { Width = 200 };
        var quiet = new Button { Content = "Quiet", Focusable = false };
        var handled = new Border { Height = 40, Background = Brushes.Transparent };
        handled.PointerPressed += (_, e) => e.Handled = true;
        var empty = new Border { Height = 100, Background = Brushes.Transparent };
        var window = new Window
        {
            Width = 400,
            Height = 400,
            Content = new StackPanel { Children = { box, other, quiet, handled, empty } },
        };
        window.Show();
        Pump();
        Assert.True(box.Focus());
        Pump();
        Assert.Same(box, Focused(window));
        return new Setup(window, box, other, quiet, handled, empty);
    }

    [AvaloniaFact]
    public void Clicking_empty_space_takes_the_focus_off_a_text_box()
    {
        var s = Show();
        try
        {
            Click(s.Window, s.Empty);

            Assert.Null(Focused(s.Window));
            Assert.False(s.Box.IsFocused);
        }
        finally
        {
            s.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Clicking_inside_the_text_box_keeps_its_focus()
    {
        var s = Show();
        try
        {
            Click(s.Window, s.Box);

            Assert.Same(s.Box, Focused(s.Window));
        }
        finally
        {
            s.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Clicking_another_text_box_moves_the_focus_there()
    {
        var s = Show();
        try
        {
            Click(s.Window, s.Other);

            Assert.Same(s.Other, Focused(s.Window));
        }
        finally
        {
            s.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Clicking_a_button_that_does_not_take_focus_leaves_it_in_the_text_box()
    {
        var s = Show();
        try
        {
            Click(s.Window, s.Quiet);

            Assert.Same(s.Box, Focused(s.Window));
        }
        finally
        {
            s.Window.Close();
        }
    }

    [AvaloniaFact]
    public void A_press_that_a_control_handles_leaves_the_focus_alone()
    {
        var s = Show();
        try
        {
            Click(s.Window, s.Handled);

            Assert.Same(s.Box, Focused(s.Window));
        }
        finally
        {
            s.Window.Close();
        }
    }
}
