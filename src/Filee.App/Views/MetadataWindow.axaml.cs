using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.ViewModels;

namespace Filee.App.Views;

/// <summary>
/// The "Metadata" window (<see cref="MetadataViewModel"/>): the keys, and where the focus goes as values are edited,
/// removed and put back.
/// </summary>
public partial class MetadataWindow : Window
{
    /// <summary>The focus came from the keyboard (Tab, arrows), not from a click.</summary>
    private bool _focusByKeyboard;

    public MetadataWindow()
    {
        InitializeComponent();
        AddHandler(GotFocusEvent, (_, e) => _focusByKeyboard = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
        // Enter is the window's own (save), so a button that was clicked can't be pressed by it later; nor by its
        // key-up (as in Pixelate / blur, Annotate and Add background).
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && !ControlTakes(e) && Editing() is null && FocusManager?.GetFocusedElement() is not TextBox)
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MetadataViewModel vm)
                return;
            vm.CloseRequested += Close;
            Closed += (_, _) => vm.Dispose();
        };
        // Back from Settings › Engines with ExifTool installed: editing becomes possible without opening the window
        // again.
        Activated += async (_, _) =>
        {
            if (DataContext is MetadataViewModel vm)
                await vm.RecheckEngineAsync();
        };
    }

    private MetadataViewModel? ViewModel => DataContext as MetadataViewModel;

    /// <summary>The value being edited, when its box has the focus.</summary>
    private MetadataRowViewModel? Editing() =>
        FocusManager?.GetFocusedElement() is TextBox { DataContext: MetadataRowViewModel { IsEditing: true } row } ? row : null;

    /// <summary>
    /// The control that has the focus, reached with the keyboard, takes <paramref name="e"/>'s key as it would anywhere
    /// else: Space and Enter press a button (a value opens its box), Space ticks "Replace the original".
    /// </summary>
    private bool ControlTakes(KeyEventArgs e)
    {
        if (!_focusByKeyboard || e.KeyModifiers != KeyModifiers.None)
            return false;
        return FocusManager?.GetFocusedElement() switch
        {
            CheckBox => e.Key == Key.Space,
            Button => e.Key is Key.Space or Key.Enter,
            _ => false,
        };
    }

    /// <summary>
    /// Before the focused control: in a value's box Enter keeps it and Esc puts it back (Shift+Enter starts a new
    /// line); in the search Esc clears it; Ctrl+F searches; elsewhere Enter saves (on the summary, closes) and Esc
    /// closes.
    /// </summary>
    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        if (Editing() is { } row && FocusManager?.GetFocusedElement() is TextBox editor)
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                row.CommitCommand.Execute(null);
                FocusValueOf(editor);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                row.CancelEditCommand.Execute(null);
                FocusValueOf(editor);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.F && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta && vm.IsEditing)
        {
            Search.Focus(NavigationMethod.Tab);
            Search.SelectAll();
            e.Handled = true;
            return;
        }
        if (ControlTakes(e))
            return;
        var inSearch = FocusManager?.GetFocusedElement() == Search;
        switch (e.Key)
        {
            case Key.Escape when inSearch && vm.SearchText.Length > 0:
                vm.SearchText = "";
                e.Handled = true;
                break;
            case Key.Escape:
                vm.CloseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when vm.IsFinished:
                vm.CloseCommand.Execute(null);
                e.Handled = true;
                break;
            // The search's Enter is the search's: the list follows as one types.
            case Key.Enter when inSearch:
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                if (vm.SaveCommand.CanExecute(null))
                    vm.SaveCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>After a value's box closes with the keyboard, the focus goes back to that value.</summary>
    private void FocusValueOf(Control editor)
    {
        var row = editor.FindAncestorOfType<Grid>();
        Dispatcher.UIThread.Post(() => row?.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("value") && b.IsEffectivelyVisible)?.Focus(NavigationMethod.Tab), DispatcherPriority.Input);
    }

    /// <summary>The box takes the focus as it appears, its text selected, ready to be typed over.</summary>
    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is Panel editor && e.Property == IsVisibleProperty)
            FocusBoxOf(editor);
    }

    /// <summary>A field just added has its box from the start: it never appears.</summary>
    private void OnEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Panel editor)
            FocusBoxOf(editor);
    }

    private static void FocusBoxOf(Panel editor)
    {
        if (editor.IsVisible && editor.Children.OfType<TextBox>().FirstOrDefault() is { } box)
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus(NavigationMethod.Tab);
                box.SelectAll();
            });
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MetadataRowViewModel { IsEditing: true } row)
            row.CommitCommand.Execute(null);
    }

    /// <summary>× hides as the field goes: the focus moves to the arrow that puts it back, beside it.</summary>
    private void OnRemoveClick(object? sender, RoutedEventArgs e) => FocusSibling(sender as Control, undo: true);

    /// <summary>The arrow hides as the field is back: the focus moves to ×, or the value.</summary>
    private void OnUndoClick(object? sender, RoutedEventArgs e) => FocusSibling(sender as Control, undo: false);

    private void FocusSibling(Control? button, bool undo)
    {
        if (button?.FindAncestorOfType<Grid>() is not { } row)
            return;
        var how = _focusByKeyboard ? NavigationMethod.Tab : NavigationMethod.Pointer;
        Dispatcher.UIThread.Post(() =>
        {
            var buttons = row.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            var next = undo ? buttons.FirstOrDefault(b => b.Command is { } c && c == (row.DataContext as MetadataRowViewModel)?.UndoCommand)
                : buttons.FirstOrDefault(b => b.Classes.Contains("remove")) ?? buttons.FirstOrDefault(b => b.Classes.Contains("value"));
            // The row itself may be gone (a field just added): the search, not nowhere.
            if (next is null && TopLevel.GetTopLevel(row) is null)
                Search.Focus(how);
            next?.Focus(how);
        }, DispatcherPriority.Input);
    }
}
