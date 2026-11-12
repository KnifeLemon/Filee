using Avalonia.Controls;
using Avalonia.Input;
using Filee.App.ViewModels.Capture;

namespace Filee.App.Views.Capture;

public partial class CaptureStudioBarWindow : Window
{
    public CaptureStudioBarWindow()
    {
        InitializeComponent();
    }

    public CaptureStudioBarWindow(CaptureStudioBarViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
