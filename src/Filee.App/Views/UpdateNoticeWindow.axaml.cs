using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>Bottom-right notice about a new version. "Download" opens the latest release page.</summary>
public partial class UpdateNoticeWindow : Window
{
    private const int ScreenMargin = 12;
    private readonly UpdateService? _updates;

    public UpdateNoticeWindow() => InitializeComponent();

    public UpdateNoticeWindow(UpdateService updates, string title) : this()
    {
        _updates = updates;
        TitleText.Text = title;
        Motion.Track(this);
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    /// <summary>Extra distance from the bottom of the screen, e.g. to stay above the conversion toast.</summary>
    public int BottomOffset { get; set; }

    private void OnDownload(object? sender, RoutedEventArgs e)
    {
        _updates?.OpenDownloadPage();
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void PlaceBottomRight()
    {
        var screen = Screens.Primary;
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(Bounds.Width * screen.Scaling);
        var height = (int)Math.Ceiling(Bounds.Height * screen.Scaling);
        Position = new PixelPoint(area.Right - width - ScreenMargin, area.Bottom - height - ScreenMargin - BottomOffset);
    }
}
