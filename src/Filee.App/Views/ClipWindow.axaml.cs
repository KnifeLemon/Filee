using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

/// <summary>
/// The "Cut a clip" window: placement, keys (Enter saves, Esc closes, S / E pick the start or end handle, the arrow
/// keys move it by 0.1 s, with Shift by 1 s), the timeline's drags and moving, sizing and maximizing the frameless
/// window.
/// </summary>
public partial class ClipWindow : Window
{
    /// <summary>How far the arrow keys move a handle, in seconds; with Shift <see cref="LargeStep"/>.</summary>
    public const double SmallStep = 0.1, LargeStep = 1;

    private ClipWindowViewModel? _vm;

    public ClipWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
        ResizeGrip.PointerPressed += OnGripPressed;
    }

    internal ClipTimeline ClipTimeline => Timeline;

    /// <summary>
    /// Opens near <paramref name="near"/> (the screen of the donut), else on the primary screen, centred in its working
    /// area and no larger than most of it.
    /// </summary>
    public void PositionOnScreen(PixelPoint? near = null)
    {
        var screen = (near.HasValue ? Screens.ScreenFromPoint(near.Value) : null)
            ?? Screens.ScreenFromPoint(Screens.Primary?.Bounds.Center ?? default)
            ?? Screens.Primary;
        if (screen is null)
            return;
        var scaling = screen.Scaling;
        var area = screen.WorkingArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width / scaling * 0.94));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height / scaling * 0.92));
        var x = area.X + Math.Max(0, (area.Width - (int)(Width * scaling)) / 2);
        var y = area.Y + Math.Max(0, (area.Height - (int)(Height * scaling)) / 2);
        Position = new PixelPoint(x, y);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ClipWindowViewModel vm && !ReferenceEquals(vm, _vm))
        {
            _vm = vm;
            vm.CloseRequested += Close;
            Timeline.HandleMoved += vm.MoveHandle;
            Timeline.ClipMoved += vm.MoveClip;
            Closed += (_, _) => vm.Dispose();
        }
    }

    // ───────────────────────── Keys ─────────────────────────

    /// <summary>Before any focused control: Enter always saves (not "click the focused button").</summary>
    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        var plain = e.KeyModifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Escape:
                vm.Cancel();
                e.Handled = true;
                break;
            case Key.Enter:
                if (vm.IsFinished)
                    vm.Cancel();
                else
                    _ = vm.SaveAsync();
                e.Handled = true;
                break;
            case Key.S when plain && vm.CanEdit:
                vm.Activate(ClipHandle.Start);
                e.Handled = true;
                break;
            case Key.E when plain && vm.CanEdit:
                vm.Activate(ClipHandle.End);
                e.Handled = true;
                break;
            // Time runs left to right on the timeline, also in right-to-left languages: Left is always earlier.
            case Key.Left or Key.Right when vm.CanEdit && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None:
                var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? LargeStep : SmallStep;
                vm.Nudge(e.Key == Key.Left ? -step : step);
                e.Handled = true;
                break;
        }
    }

    // ───────────────────────── Moving and sizing the window ─────────────────────────

    /// <summary>The stage beside the picture moves the window; a double click maximizes it.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
            ToggleMaximized();
        else if (WindowState == WindowState.Normal)
            BeginMoveDrag(e);
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthEast, e);
        e.Handled = true;
    }

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            var maximized = WindowState == WindowState.Maximized;
            Classes.Set("maximized", maximized);
            ResizeGrip.IsVisible = !maximized;
            var key = maximized ? "crop.restore" : "crop.maximize";
            MaximizeButton.Bind(ToolTip.TipProperty, MaximizeButton.GetResourceObservable(key));
            MaximizeButton.Bind(Avalonia.Automation.AutomationProperties.NameProperty, MaximizeButton.GetResourceObservable(key));
        }
    }
}
