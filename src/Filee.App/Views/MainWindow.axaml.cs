using Avalonia.Controls;
using Filee.App.Services;

namespace Filee.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Motion.Track(this);
    }

    /// <summary>Closing the window keeps Filee running in the tray (Quit is in the tray menu).</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
