using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.ViewModels;
using Filee.Core.Presets;
using Filee.Engines.Annotations;

namespace Filee.App.Views;

/// <summary>
/// The "Annotate" window (<see cref="AnnotateViewModel"/>): the keys, and the box text is typed in, over the picture
/// where the tag goes.
/// </summary>
public partial class AnnotateWindow : Window
{
    private AnnotationCanvas.TextRequest? _typing;
    private double _tagMiddle;

    /// <summary>The focus came from the keyboard (Tab, arrows), not from a click.</summary>
    private bool _focusByKeyboard;

    public AnnotateWindow()
    {
        InitializeComponent();
        Canvas.TextRequested += (_, request) => StartTyping(request);
        // The picture moves and scales with the window: the box being typed in goes with it.
        Canvas.SizeChanged += (_, _) =>
        {
            if (_typing is not { } typing)
                return;
            var moved = Canvas.RequestFor(typing.Index, typing.At);
            _typing = moved;
            Place(moved);
        };
        TextEditor.LostFocus += (_, _) => FinishTyping(keep: true);
        // The tag follows the box's width; the box stays centred on the tag.
        TextTag.SizeChanged += (_, e) =>
        {
            TagFill.Width = e.NewSize.Width;
            Avalonia.Controls.Canvas.SetTop(TextTag, _tagMiddle - e.NewSize.Height / 2);
        };
        // Right to left when its first letter is (Arabic, Hebrew), as the tag will be drawn.
        TextEditor.TextChanged += (_, _) => TextEditor.FlowDirection =
            AnnotationPainter.IsRightToLeft(TextEditor.Text ?? "") ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        TextEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FinishTyping(keep: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                FinishTyping(keep: false);
                e.Handled = true;
            }
        };
        AddHandler(GotFocusEvent, (_, e) => _focusByKeyboard = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDownFirst, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Enter is the window's own (save), so a button that was clicked can't be pressed by it later; nor by its
        // key-up (as in Pixelate / blur).
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && !TextEditor.IsVisible && !ControlTakes(e))
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not AnnotateViewModel vm)
                return;
            vm.CloseRequested += Close;
            Closed += (_, _) => vm.Dispose();
        };
    }

    private AnnotateViewModel? ViewModel => DataContext as AnnotateViewModel;

    internal TextBox Editor => TextEditor;

    /// <summary>The coloured tag shown while text is typed.</summary>
    internal Control TypingTag => TagFill;
    internal AnnotationCanvas AnnotationCanvas => Canvas;

    /// <summary>
    /// Shows the tag over its place as the file will get it (AnnotationPainter.DrawTag): the mark's colour, rounded, as
    /// wide as the text from an empty tag's size, the text bold at its size and inset, text and caret in its ink; with
    /// its text when typed again, the mark itself hidden meanwhile. The coloured tag (TagFill) is the tag's own height;
    /// the box over it is as high as its line needs and centred on the tag, as the painter centres the line: a script
    /// whose font reaches further (Arabic) grew the tag, or was cut off in it.
    /// </summary>
    private void StartTyping(AnnotationCanvas.TextRequest request)
    {
        if (ViewModel is not { } vm)
            return;
        // A click elsewhere with the Text tool keeps what was typed: the picture takes no focus from the box. Finishing
        // can remove a mark (a text cleared), so the mark asked for is found again by itself, not by its place.
        if (_typing is not null)
        {
            var asked = request.Index >= 0 && request.Index < vm.Marks.Count ? vm.Marks.Marks[request.Index] : null;
            FinishTyping(keep: true);
            if (asked is not null)
            {
                var index = vm.Marks.Marks.ToList().IndexOf(asked);
                if (index < 0)
                    return;
                request = request with { Index = index };
            }
        }
        _typing = request;
        var mark = request.Index >= 0 ? vm.Marks.Marks[request.Index] : null;
        var color = Color.Parse(mark?.Color ?? vm.Color);
        // The tag's own ink (white on dark, near-black on light), as the painter picks it.
        var inkOn = AnnotationPainter.InkOn(new SkiaSharp.SKColor(color.R, color.G, color.B));
        var ink = new SolidColorBrush(Color.FromRgb(inkOn.Red, inkOn.Green, inkOn.Blue));
        TagFill.Background = new SolidColorBrush(color);
        TextEditor.Text = mark?.Text ?? "";
        TextEditor.FontWeight = FontWeight.Bold;
        // From the left inset whichever way the text reads, as the tag draws it: right to left text set to the right of
        // a box a pixel wider than the tag (rounded to whole pixels) sat that pixel off.
        TextEditor.TextAlignment = TextAlignment.Left;
        TextEditor.Foreground = ink;
        TextEditor.CaretBrush = ink;
        Place(request);
        Canvas.TypingIndex = request.Index;
        TextEditor.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            TextEditor.Focus();
            TextEditor.SelectAll();
        });
    }

    /// <summary>The tag and its box at the request's place and size on screen.</summary>
    private void Place(AnnotationCanvas.TextRequest request)
    {
        var size = request.FontSize;
        TagFill.CornerRadius = new CornerRadius(size * 0.3);
        TagFill.Height = request.Screen.Height;
        TextTag.MinWidth = request.Screen.Width;
        TextTag.MinHeight = request.Screen.Height;
        _tagMiddle = request.Screen.Center.Y;
        TextEditor.FontSize = size;
        TextEditor.Padding = new Thickness(size * 0.45, 0);
        Avalonia.Controls.Canvas.SetLeft(TagFill, request.Screen.X);
        Avalonia.Controls.Canvas.SetTop(TagFill, request.Screen.Y);
        Avalonia.Controls.Canvas.SetLeft(TextTag, request.Screen.X);
        Avalonia.Controls.Canvas.SetTop(TextTag, _tagMiddle - TextTag.Bounds.Height / 2);
    }

    /// <summary>Puts the typed text on the picture (or into the mark typed again), or drops it.</summary>
    internal void FinishTyping(bool keep)
    {
        if (_typing is not { } request || ViewModel is not { } vm)
            return;
        _typing = null;
        TextEditor.IsVisible = false;
        Canvas.TypingIndex = -1;
        var text = TextEditor.Text ?? "";
        if (!keep)
            return;
        if (request.Index >= 0)
            vm.EditText(request.Index, text);
        else
            vm.AddText(request.At, text);
    }

    /// <summary>
    /// The control that has the focus, reached with the keyboard, takes <paramref name="e"/>'s key as it would anywhere
    /// else: Space and Enter press a button, Space chooses a tool, colour or width.
    /// </summary>
    private bool ControlTakes(KeyEventArgs e)
    {
        if (!_focusByKeyboard || e.KeyModifiers != KeyModifiers.None)
            return false;
        return FocusManager?.GetFocusedElement() switch
        {
            // Space chooses a tool, colour or width; Enter still saves.
            RadioButton => e.Key == Key.Space,
            Button => e.Key is Key.Space or Key.Enter,
            _ => false,
        };
    }

    /// <summary>A control in the bar has the focus from the keyboard.</summary>
    private bool InBar => _focusByKeyboard && FocusManager?.GetFocusedElement() is Control control && control != this && control != TextEditor;

    /// <summary>
    /// Before the focused control: Enter saves and Esc closes (from the bar, Esc goes back to the picture). The marks
    /// come first in the Tab order: Tab and Shift+Tab go from mark to mark (with Select), and past the last one on into
    /// the bar. The arrows move the picked mark a pixel of the picture, ten with Shift; the picture is never mirrored,
    /// so Left is always left.
    /// </summary>
    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || TextEditor.IsVisible || ControlTakes(e))
            return;
        var shiftOnly = (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Escape when InBar:
                FocusManager?.Focus(null);
                _focusByKeyboard = false;
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when InBar && e.KeyModifiers == KeyModifiers.None
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
            case Key.Tab when shiftOnly && !InBar && vm.Marks.Count > 0 && !vm.IsBusy:
                var back = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                // Past the last mark (or before the first), on into the bar: its first tool (or Save).
                if (vm.Marks.Selected == (back ? 0 : vm.Marks.Count - 1))
                {
                    vm.Marks.Select(-1);
                    Control into = back ? SaveButton.IsEffectivelyEnabled ? SaveButton : SelectTool : SelectTool;
                    into.Focus(NavigationMethod.Tab);
                    e.Handled = true;
                    break;
                }
                vm.Mode = AnnotateMode.Select;
                vm.Marks.SelectNext(back ? -1 : 1);
                e.Handled = true;
                break;
            // Not while a control in the bar has the keyboard's focus: the arrows are about it there.
            case Key.Left or Key.Right or Key.Up or Key.Down when vm.HasSelection && shiftOnly && !vm.IsBusy && !InBar:
                var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
                vm.Marks.Nudge(e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 },
                    e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 }, vm.PictureSize.Width, vm.PictureSize.Height);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The arrows go along a row of choices (tools, colours, widths) and choose, as in a group of radio buttons: Right
    /// and Down to the next, Left and Up to the one before, round and round; Left is left in a right-to-left UI too.
    /// </summary>
    private void ChooseAlong(RadioButton choice, string group, Key key)
    {
        var row = this.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == group && r.IsEffectivelyEnabled).ToList();
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || ViewModel is not { } vm || TextEditor.IsVisible)
            return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var plain = e.KeyModifiers == KeyModifiers.None;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Delete or Key.Back when plain:
                vm.RemoveSelectedCommand.Execute(null);
                break;
            case Key.Z when command && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Y when command:
                vm.RedoCommand.Execute(null);
                break;
            case Key.Z when command:
                vm.UndoCommand.Execute(null);
                break;
            case Key.V when plain:
                vm.Mode = AnnotateMode.Select;
                break;
            case Key.P when plain:
                vm.Mode = AnnotateMode.Pen;
                break;
            case Key.A when plain:
                vm.Mode = AnnotateMode.Arrow;
                break;
            case Key.B when plain:
                vm.Mode = AnnotateMode.Box;
                break;
            case Key.H when plain:
                vm.Mode = AnnotateMode.Highlight;
                break;
            case Key.T when plain:
                vm.Mode = AnnotateMode.Text;
                break;
            case Key.N when plain:
                vm.Mode = AnnotateMode.Number;
                break;
            default:
                e.Handled = false;
                break;
        }
    }
}
