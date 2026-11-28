// A click on empty space takes the focus off a text box. Avalonia moves the focus only to a control that takes it, so a
// click on a label, a margin or a card used to leave the caret and the accent focus border in the text box, and typing
// kept going into it.
//
// Registered once for every window of the application (like SingleSelectionGuard). A click leaves the focus alone when it
// lands in the focused text box itself (or in the control that owns it: a number box, the tag editor), or on a control that
// is not meant to move it: buttons, list and menu items, scroll bars, thumbs. A click on anything that takes focus has
// already moved it by the time the check runs, so the text box is no longer the focused element and nothing is cleared.
// A press that a control handled is left alone too.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Filee.App.Controls;

public static class ClickAwayFocus
{
    private static bool _registered;

    /// <summary>Installs the class handler once for every window and popup of the application.</summary>
    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        // Tunnel: it runs before any control handles the press, so the text box that is focused now is the one being left.
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>(OnPointerPressed, RoutingStrategies.Tunnel);
    }

    private static void OnPointerPressed(TopLevel top, PointerPressedEventArgs e)
    {
        if (top.FocusManager?.GetFocusedElement() is not TextBox box
            || !e.GetCurrentPoint(top).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || Keeps(source, box))
            return;

        // Posted: the control that was clicked takes the focus while the press is handled, and then it is no longer ours to clear.
        // A press that a control handled (the annotation canvas starting a new text, say) is that control's to deal with.
        Dispatcher.UIThread.Post(() =>
        {
            if (!e.Handled && top.FocusManager is { } focus && ReferenceEquals(focus.GetFocusedElement(), box))
                focus.Focus(null);
        }, DispatcherPriority.Background);
    }

    /// <summary>The press is in the text box (or the control built around it), or on a control that leaves the focus where it is.</summary>
    private static bool Keeps(Visual source, TextBox box)
    {
        var owner = box.TemplatedParent as Visual ?? box;
        foreach (var visual in source.GetSelfAndVisualAncestors())
            if (ReferenceEquals(visual, owner) || visual is Button or ListBoxItem or MenuItem or ScrollBar or Thumb)
                return true;
        return false;
    }
}
