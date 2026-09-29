using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>Bottom-right progress stack. Visible exactly while ConversionService.ToastJobs is non-empty.</summary>
public partial class ToastWindow : Window
{
    private const int ScreenMargin = 12;

    public ToastWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ConversionService service)
            service.ToastJobs.CollectionChanged += (_, args) => OnJobsChanged(service, args);
    }

    private void OnJobsChanged(ConversionService service, NotifyCollectionChangedEventArgs e)
    {
        if (service.ToastJobs.Count > 0 && !IsVisible)
        {
            Show();
            PlaceBottomRight();
        }
        else if (service.ToastJobs.Count == 0 && IsVisible)
        {
            Hide();
        }
    }

    private void PlaceBottomRight()
    {
        var screen = Screens.Primary;
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);
        Position = new PixelPoint(area.Right - width - ScreenMargin, area.Bottom - height - ScreenMargin);
    }
}
