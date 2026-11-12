// Readable, copyable progress cards, bounded by their display's working area.

using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;

namespace Filee.App.Views;

/// <summary>Bottom-right progress stack. Visible exactly while ConversionService.ToastJobs is non-empty.</summary>
public partial class ToastWindow : Window
{
    private const int ScreenMargin = 12;
    private PixelRect? _area;
    private double _scaling = 1;

    public ToastWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        SizeChanged += (_, _) => AnchorBottomRight();
        Opened += (_, _) => PlaceBottomRight();
        ScalingChanged += (_, _) => PlaceBottomRight();
        Screens.Changed += OnScreensChanged;
    }

    private ConversionService? _service;

    private void OnScreensChanged(object? sender, EventArgs e) => PlaceBottomRight();

    private async void OnCopyNotification(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: JobViewModel job } button || Clipboard is not { } clipboard) return;
        var original = button.Content;
        button.IsEnabled = false;
        try
        {
            await clipboard.SetTextAsync(job.NotificationText);
            var copied = AppHost.Get<ILocalizer>()["toast.copied"];
            button.Content = new TextBlock { Text = copied };
            AutomationProperties.SetName(button, copied);
            await Task.Delay(1500);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"Copying a notification failed: {ex.Message}");
        }
        finally
        {
            button.Content = original;
            AutomationProperties.SetName(button, AppHost.Get<ILocalizer>()["toast.copy"]);
            button.IsEnabled = true;
        }
    }

    private async void OnRequestFormat(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is JobViewModel { CanRequestFormat: true } job)
            await Launcher.LaunchUriAsync(FormatRequest.Url(job.UnknownExtensions));
    }

    private async void OnGetFonts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not JobViewModel { CanGetFonts: true } job)
            return;
        if (job.MissingFonts.Select(FontSources.Url).FirstOrDefault(uri => uri is not null) is { } source)
            await Launcher.LaunchUriAsync(source);
    }
    private bool _closed;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_service is not null)
            _service.ToastJobs.CollectionChanged -= OnJobsChanged;
        _service = DataContext as ConversionService;
        if (_service is not null)
            _service.ToastJobs.CollectionChanged += OnJobsChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        Screens.Changed -= OnScreensChanged;
        if (_service is not null)
            _service.ToastJobs.CollectionChanged -= OnJobsChanged;
        base.OnClosed(e);
    }

    // After the change: showing the window inside CollectionChanged built the list with the new job already in it, and
    // the list then added it again, so one job had two cards (issue #60).
    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(UpdateVisibility);

    private void UpdateVisibility()
    {
        if (_closed || _service is not { } service)
            return;
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
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;
        FitTo(screen.WorkingArea, screen.Scaling);
    }

    /// <summary>Caps the stack in logical pixels and anchors it inside the display, including its taskbar.</summary>
    internal void FitTo(PixelRect area, double scaling)
    {
        if (scaling <= 0 || !double.IsFinite(scaling)) return;
        _area = area;
        _scaling = scaling;
        MaxHeight = Math.Max(1, area.Height / scaling - 2 * ScreenMargin);
        Width = Math.Min(360, Math.Max(1, area.Width / scaling - 2 * ScreenMargin));
        AnchorBottomRight();
    }

    private void AnchorBottomRight()
    {
        if (_area is not { } area) return;
        var margin = (int)Math.Round(ScreenMargin * _scaling);
        var width = (int)Math.Ceiling(Width * _scaling);
        var height = (int)Math.Ceiling(Math.Min(Bounds.Height, MaxHeight) * _scaling);
        Position = new PixelPoint(Math.Max(area.X, area.Right - width - margin), Math.Max(area.Y, area.Bottom - height - margin));
    }
}
