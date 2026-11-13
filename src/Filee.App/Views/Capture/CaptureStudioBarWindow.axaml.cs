using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Filee.App.ViewModels.Capture;

namespace Filee.App.Views.Capture;

public partial class CaptureStudioBarWindow : Window
{
    public event Action<bool>? HoverChanged;
    public event Action<Rect>? BoundsChangedNotification;

    public CaptureStudioBarWindow()
    {
        InitializeComponent();

        PointerEntered += (_, _) => HoverChanged?.Invoke(true);
        PointerExited += (_, _) => HoverChanged?.Invoke(false);
        Loaded += (_, _) => CenterOnTopScreen();
    }

    public void CenterOnTopScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var wa = screen.WorkingArea;
            var w = Bounds.Width > 0 ? Bounds.Width : (DesiredSize.Width > 0 ? DesiredSize.Width : 460);
            Position = new PixelPoint((int)(wa.X + (wa.Width - w) / 2), wa.Y + 16);
            NotifyBounds();
        }
    }

    public CaptureStudioBarWindow(CaptureStudioBarViewModel vm) : this()
    {
        DataContext = vm;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            NotifyBounds();
        }
    }

    public void NotifyBounds()
    {
        var pos = Position;
        var b = Bounds;
        BoundsChangedNotification?.Invoke(new Rect(pos.X, pos.Y, b.Width, b.Height));
    }

    private void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
