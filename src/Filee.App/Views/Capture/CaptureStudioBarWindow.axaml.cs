using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Filee.App.ViewModels.Capture;

namespace Filee.App.Views.Capture;

public partial class CaptureStudioBarWindow : Window
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (OperatingSystem.IsWindows())
        {
            var handle = TryGetPlatformHandle()?.Handle ?? 0;
            if (handle != 0)
            {
                SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);
            }
        }
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
