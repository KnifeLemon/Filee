using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.ViewModels;
using Filee.Core.Presets;

namespace Filee.App.Views;

/// <summary>
/// The "Annotate" window (<see cref="AnnotateViewModel"/>): the keys, and the box text is typed in, over the picture
/// where the tag goes.
/// </summary>
public partial class AnnotateWindow : Window
{
    private AnnotationCanvas.TextRequest? _typing;

    public AnnotateWindow()
    {
        InitializeComponent();
        Canvas.TextRequested += (_, request) => StartTyping(request);
        TextEditor.LostFocus += (_, _) => FinishTyping(keep: true);
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
    internal AnnotationCanvas AnnotationCanvas => Canvas;

    /// <summary>
    /// Shows the tag over its place as the file will get it (AnnotationPainter.DrawTag): the mark's colour, rounded, as
    /// wide as the text from an empty tag's size, the text bold at its size and inset, text and caret in its ink; with
    /// its text when typed again, the mark itself hidden meanwhile.
    /// </summary>
    private void StartTyping(AnnotationCanvas.TextRequest request)
    {
        if (ViewModel is not { } vm)
            return;
        _typing = request;
        var mark = request.Index >= 0 ? vm.Marks.Marks[request.Index] : null;
        var size = Math.Max(12, request.FontSize);
        var color = Color.Parse(mark?.Color ?? vm.Color);
        var ink = new SolidColorBrush(0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B > 170 ? Color.Parse("#1C1C1C") : Colors.White);
        TextTag.Background = new SolidColorBrush(color);
        TextTag.CornerRadius = new CornerRadius(size * 0.3);
        TextTag.MinWidth = request.Screen.Width;
        TextTag.MinHeight = request.Screen.Height;
        TextEditor.Text = mark?.Text ?? "";
        TextEditor.FontSize = size;
        TextEditor.FontWeight = FontWeight.Bold;
        TextEditor.Padding = new Thickness(size * 0.45, 0);
        TextEditor.Foreground = ink;
        TextEditor.CaretBrush = ink;
        Avalonia.Controls.Canvas.SetLeft(TextTag, request.Screen.X);
        Avalonia.Controls.Canvas.SetTop(TextTag, request.Screen.Y);
        Canvas.TypingIndex = request.Index;
        TextEditor.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            TextEditor.Focus();
            TextEditor.SelectAll();
        });
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
            case Key.Escape:
                vm.CloseCommand.Execute(null);
                break;
            case Key.Enter when plain:
                vm.SaveCommand.Execute(null);
                break;
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
