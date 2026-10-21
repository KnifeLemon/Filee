using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

/// <summary>
/// The clip window's editor: its keys (Space / K play, J / L five seconds back and on, , and . a frame, C cuts, Delete
/// cuts out (Shift: closing up), Ctrl+C / V / D copy, paste and duplicate, Ctrl+Z / Y undo and redo, T a title, F the
/// full preview, E or Enter Export, Esc steps back), files dropped or picked (they fly into Files, a check draws itself),
/// the narrow layout (Files and Properties slide out) and the full preview.
/// </summary>
public partial class ClipEditorView : UserControl
{
    /// <summary>Narrower than this, Files and Properties slide out over the preview.</summary>
    private const double NarrowWidth = 1120;

    /// <summary>Narrower than this, the tools show only their icons.</summary>
    private const double CompactWidth = 1280;

    private ClipEditorViewModel? _vm;
    private WindowState _stateBeforeFull = WindowState.Normal;
    private IDisposable? _addedTimer;

    public ClipEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Root.SizeChanged += (_, _) => Lay();
        AddFilesButton.Click += OnAddFiles;
        MaximizeButton.Click += (_, _) =>
        {
            if (Window is { } window)
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        };
        CloseButton.Click += (_, _) => (Window?.DataContext as ClipWindowViewModel)?.Cancel();
        FilesList.DoubleTapped += (_, e) =>
        {
            if (e.Source is StyledElement { DataContext: EditorMedia media })
                _vm?.UseMedia(media);
        };
        // A card's menu: put it in, show it in its folder, take it out of the project.
        FilesList.ContextRequested += (_, e) =>
        {
            if (_vm is not { } vm || e.Source is not Control { DataContext: EditorMedia media } source)
                return;
            EditorMenus.Build(vm.MenuForMedia(media)).Open(source);
            e.Handled = true;
        };
        DragDrop.AddDragEnterHandler(this, OnDragEnter);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDragLeaveHandler(this, (_, _) => DropGlow.Classes.Remove("on"));
        DragDrop.AddDropHandler(this, OnDrop);
    }

    private Window? Window => TopLevel.GetTopLevel(this) as Window;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_vm, DataContext))
            return;
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.FilesAdded -= OnFilesAdded;
            _vm.RevealRequested -= OnRevealRequested;
            _vm.TextEditRequested -= OnTextEditRequested;
        }
        _vm = DataContext as ClipEditorViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.FilesAdded += OnFilesAdded;
            _vm.RevealRequested += OnRevealRequested;
            _vm.TextEditRequested += OnTextEditRequested;
            Lay();
        }
        else if (Window is { WindowState: WindowState.FullScreen } window)
            window.WindowState = _stateBeforeFull;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipEditorViewModel.IsFullPreview) or nameof(ClipEditorViewModel.IsNarrow)
            or nameof(ClipEditorViewModel.IsFilesOpen) or nameof(ClipEditorViewModel.IsPropertiesOpen))
            Lay();
        if (e.PropertyName == nameof(ClipEditorViewModel.IsFullPreview) && Window is { } window && _vm is { } vm)
        {
            if (vm.IsFullPreview)
            {
                _stateBeforeFull = window.WindowState;
                window.WindowState = WindowState.FullScreen;
            }
            else if (window.WindowState == WindowState.FullScreen)
                window.WindowState = _stateBeforeFull;
        }
    }

    /// <summary>
    /// Lays the editor out for its width: side by side when wide; narrow, Files and Properties slide out over the
    /// preview (one at a time) and the tools show only their icons; the full preview alone.
    /// </summary>
    private void Lay()
    {
        if (_vm is not { } vm)
            return;
        var width = Root.Bounds.Width;
        if (width > 0)
            vm.IsNarrow = width < NarrowWidth;
        Root.Classes.Set("compact", width > 0 && width < CompactWidth);
        var full = vm.IsFullPreview;
        var narrow = vm.IsNarrow && !full;
        Middle.ColumnDefinitions = narrow || full ? ColumnDefinitions.Parse("0,*,0") : ColumnDefinitions.Parse("250,*,300");
        Middle.ColumnSpacing = narrow || full ? 0 : 10;
        Place(FilesPanel, narrow, column: 0, HorizontalAlignment.Left, 260);
        Place(PropertiesPanel, narrow, column: 2, HorizontalAlignment.Right, 300);
        FilesPanel.IsVisible = !full && vm.ShowFiles;
        PropertiesPanel.IsVisible = !full && vm.ShowProperties;
        ShapeBar.IsVisible = width <= 0 || width >= 900;
    }

    private static void Place(Control panel, bool sliding, int column, HorizontalAlignment side, double width)
    {
        Grid.SetColumn(panel, sliding ? 1 : column);
        panel.HorizontalAlignment = sliding ? side : HorizontalAlignment.Stretch;
        panel.Width = sliding ? width : double.NaN;
        panel.ZIndex = sliding ? 10 : 0;
        panel.Margin = sliding ? new Thickness(8) : default;
        panel.Classes.Set("sliding", sliding);
    }

    // ───────────────────────── Keys ─────────────────────────

    /// <summary>The editor's keys, before any control has them. True when the key was the editor's.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_vm is not { } vm)
            return false;
        var plain = e.KeyModifiers == KeyModifiers.None;
        var shift = e.KeyModifiers == KeyModifiers.Shift;
        var command = e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta;
        var commandShift = e.KeyModifiers is (KeyModifiers.Control | KeyModifiers.Shift) or (KeyModifiers.Meta | KeyModifiers.Shift);

        // Typing a text: the keys are the text box's, Esc leaves it.
        if (TopLevel.GetTopLevel(this)?.FocusManager is { } focus && focus.GetFocusedElement() is TextBox or NumericUpDown)
        {
            if (e.Key != Key.Escape)
                return false;
            // The editor itself takes the keyboard back (it has no look of focus).
            Root.Focusable = true;
            Root.Focus();
            return true;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (vm.IsExportOpen)
                    vm.CloseExport();
                else if (vm.IsBackConfirmOpen)
                    vm.IsBackConfirmOpen = false;
                else if (vm.IsFullPreview)
                    vm.IsFullPreview = false;
                else if (vm.IsPlaying)
                    vm.StopPlaying();
                else
                    vm.ClearSelection();
                return true;
            case Key.Enter:
                if (vm.IsExportOpen)
                    vm.Export();
                else if (vm.IsBackConfirmOpen)
                    vm.ConfirmBackCommand.Execute(null);
                else
                    vm.OpenExport();
                return true;
        }
        if (vm.IsExportOpen || vm.IsBackConfirmOpen)
            return e.Key == Key.Space;

        switch (e.Key)
        {
            case Key.Space or Key.K when plain:
                vm.PlayPause();
                return true;
            case Key.Space:
                return true;
            case Key.J when plain:
                vm.Jump("-5");
                return true;
            case Key.L when plain:
                vm.Jump("5");
                return true;
            case Key.OemComma when plain:
                vm.StepFrame("-1");
                return true;
            case Key.OemPeriod when plain:
                vm.StepFrame("1");
                return true;
            // Time runs left to right on the timeline in every language: Left is earlier.
            case Key.Left when plain:
                vm.StepFrame("-1");
                return true;
            case Key.Right when plain:
                vm.StepFrame("1");
                return true;
            case Key.Left when shift:
                vm.Jump("-1");
                return true;
            case Key.Right when shift:
                vm.Jump("1");
                return true;
            case Key.Home:
                vm.GoToStart();
                return true;
            case Key.End:
                vm.GoToEnd();
                return true;
            case Key.C when plain:
                vm.Split();
                return true;
            case Key.Delete or Key.Back when plain || shift:
                vm.Delete(closeUp: shift);
                return true;
            case Key.Z when command:
                vm.Undo();
                return true;
            case Key.Y when command:
            case Key.Z when commandShift:
                vm.Redo();
                return true;
            case Key.C when command:
                vm.Copy();
                return true;
            case Key.V when command:
                vm.Paste();
                return true;
            case Key.D when command:
                vm.Duplicate();
                return true;
            case Key.A when command:
                vm.SelectAll();
                return true;
            case Key.T when plain:
                vm.AddText("Title");
                return true;
            case Key.F when plain:
                vm.ToggleFullPreview();
                return true;
            case Key.E when plain || command:
                vm.OpenExport();
                return true;
        }
        return false;
    }

    // ───────────────────────── Files ─────────────────────────

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || Window is not { } window)
            return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Video, audio, pictures")
                    {
                        Patterns =
                        [
                            "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi", "*.m4v", "*.wmv", "*.flv", "*.ts", "*.mts", "*.3gp",
                            "*.mp3", "*.m4a", "*.aac", "*.wav", "*.flac", "*.ogg", "*.opus", "*.wma",
                            "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif",
                        ],
                    },
                    FilePickerFileTypes.All,
                ],
            });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
                await vm.AddFilesAsync(paths, toTimeline: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No picker here (a test, an unusual desktop): nothing to add.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var files = DraggedFiles.Offered(e.DataTransfer);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropGlow.Classes.Set("on", files);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = DraggedFiles.Offered(e.DataTransfer);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropGlow.Classes.Set("on", files);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropGlow.Classes.Remove("on");
        if (e.Handled || _vm is not { } vm)
            return;
        var files = DraggedFiles.Local(e.DataTransfer);
        if (files.Count == 0)
            return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        try
        {
            await vm.AddFilesAsync(files, toTimeline: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    /// <summary>Files came in: their cards fly into Files and a check in a circle draws itself.</summary>
    private void OnFilesAdded(IReadOnlyList<EditorMedia> added)
    {
        DropGlow.Classes.Remove("on");
        if (!Motion.Enabled)
        {
            foreach (var media in added)
                media.JustAdded = false;
            return;
        }
        AddedText.Text = _vm?.Message ?? "";
        Added.Classes.Remove("play");
        Added.Classes.Add("play");
        _addedTimer?.Dispose();
        _addedTimer = DispatcherTimer.RunOnce(() =>
        {
            Added.Classes.Remove("play");
            foreach (var media in added)
                media.JustAdded = false;
        }, TimeSpan.FromSeconds(1.7));
    }

    private static void OnRevealRequested(string path)
    {
        try
        {
            AppHost.Get<Filee.Core.Platform.IPlatformServices>().RevealInFileManager(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No file manager here (a test): nothing to show.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void OnTextEditRequested() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!TextContentBox.IsEffectivelyVisible)
                return;
            TextContentBox.Focus();
            TextContentBox.SelectAll();
        }, DispatcherPriority.Background);
}
