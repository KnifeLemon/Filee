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
