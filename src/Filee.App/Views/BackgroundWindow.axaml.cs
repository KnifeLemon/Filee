using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Filee.App.ViewModels;
using Filee.Engines.Magick;

namespace Filee.App.Views;

/// <summary>
/// The "Add background" window (<see cref="BackgroundViewModel"/>): the keys and the picker for a picture behind.
/// </summary>
public partial class BackgroundWindow : Window
{
    /// <summary>The focus came from the keyboard (Tab, arrows), not from a click.</summary>
    private bool _focusByKeyboard;

    public BackgroundWindow()
    {
        InitializeComponent();
        AddHandler(GotFocusEvent, (_, e) => _focusByKeyboard = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDownFirst, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Enter is the window's own (save), so a button that was clicked can't be pressed by it later; nor by its
        // key-up (as in Pixelate / blur and Annotate).
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && !ControlTakes(e))
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not BackgroundViewModel vm)
                return;
            vm.CloseRequested += Close;
            vm.PickPicture = PickPictureAsync;
            Closed += (_, _) => vm.Dispose();
        };
    }

    private BackgroundViewModel? ViewModel => DataContext as BackgroundViewModel;

    /// <summary>The preview of the file: what the tests compare with it.</summary>
    internal Panel PreviewCanvas => Canvas;

    /// <summary>
    /// The control that has the focus, reached with the keyboard, takes <paramref name="e"/>'s key as it would anywhere
    /// else: Space and Enter press a button or open a colour picker, Space chooses a background, swatch or canvas, or
    /// ticks "same look".
    /// </summary>
    private bool ControlTakes(KeyEventArgs e)
    {
        if (!_focusByKeyboard || e.KeyModifiers != KeyModifiers.None)
            return false;
        return FocusManager?.GetFocusedElement() switch
        {
            // Space chooses or ticks; Enter still saves.
            RadioButton or CheckBox => e.Key == Key.Space,
            Button => e.Key is Key.Space or Key.Enter,
            _ => false,
        };
    }

    /// <summary>
    /// Before the focused control: Enter saves (on the summary, closes) and Esc closes. The arrows go along a row of
    /// choices and choose, as in a group of radio buttons; on a slider they move it, as anywhere.
    /// </summary>
    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || ControlTakes(e))
            return;
        // A colour picker's flyout is open: its keys are its own.
        if (FocusManager?.GetFocusedElement() is Visual focused && TopLevel.GetTopLevel(focused) != this)
            return;
        switch (e.Key)
        {
            case Key.Left or Key.Right or Key.Up or Key.Down when _focusByKeyboard && e.KeyModifiers == KeyModifiers.None
                && FocusManager?.GetFocusedElement() is RadioButton { GroupName: { } group } choice:
                ChooseAlong(choice, group, e.Key);
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
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                if (vm.SaveCommand.CanExecute(null))
                    vm.SaveCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The arrows go along a row of choices (backgrounds, swatches, canvases) and choose, as in a group of radio
    /// buttons: Right and Down to the next, Left and Up to the one before, round and round; Left is left in a
    /// right-to-left UI too.
    /// </summary>
    private void ChooseAlong(RadioButton choice, string group, Key key)
    {
        var row = this.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == group && r.IsEffectivelyVisible && r.IsEffectivelyEnabled).ToList();
        var at = row.IndexOf(choice);
        if (at < 0 || row.Count < 2)
            return;
        var step = key is Key.Right or Key.Down ? 1 : -1;
        if (key is Key.Left or Key.Right && choice.FlowDirection == FlowDirection.RightToLeft)
            step = -step;
        var next = row[((at + step) % row.Count + row.Count) % row.Count];
        next.Focus(NavigationMethod.Directional);
        if (next.Command?.CanExecute(next.CommandParameter) == true)
            next.Command.Execute(next.CommandParameter);
    }

    /// <summary>
    /// A picture to put behind: only of the kinds the file can be made with, so none is left out silently.
    /// </summary>
    private async Task<string?> PickPictureAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(ViewModel is { } vm ? vm.PicturesFilterName : "Images")
            {
                Patterns = BackdropComposer.PictureBehindPatterns,
            }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
