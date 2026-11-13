using Avalonia.Controls;
using Filee.App.ViewModels.Capture;

namespace Filee.App.Views.Capture;

public partial class CaptureStudioConfigWindow : Window
{
    public CaptureStudioConfigWindow()
    {
        InitializeComponent();
    }

    public CaptureStudioConfigWindow(CaptureStudioConfigViewModel vm) : this()
    {
        DataContext = vm;
        vm.CancelRequested += Close;
        vm.RecordingRequested += _ => Close();
    }
}
