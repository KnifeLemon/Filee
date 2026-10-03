using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.Core.Localization;

namespace Filee.App.Views;

/// <summary>
/// Bottom-right notice about a new version. "Update" downloads and runs the new installer and the card shows the
/// download progress; a portable copy gets "Download", which opens the release page.
/// </summary>
public partial class UpdateNoticeWindow : Window
{
    private const int ScreenMargin = 12;
    private readonly UpdateService? _updates;
    private readonly ILocalizer? _loc;

    public UpdateNoticeWindow() => InitializeComponent();

    public UpdateNoticeWindow(UpdateService updates, ILocalizer loc, string title) : this()
    {
        _updates = updates;
        _loc = loc;
        TitleText.Text = title;
        DownloadText.Text = loc[UpdateTexts.ButtonKey];
        Motion.Track(this);
        SizeChanged += (_, _) => PlaceBottomRight();
        updates.PropertyChanged += OnUpdatesChanged;
        Closed += (_, _) => updates.PropertyChanged -= OnUpdatesChanged;
        ShowState();
    }

    /// <summary>Extra distance from the bottom of the screen, e.g. to stay above the conversion toast.</summary>
    public int BottomOffset { get; set; }

    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        if (_updates is null)
            return;
        if (!UpdateService.IsInstalled)
        {
            _updates.OpenDownloadPage();
            Close();
            return;
        }
        // Stays open: the download takes a while and this card is where the user is looking.
        await _updates.UpdateAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (UpdateTexts.Affects(e.PropertyName))
            ShowState();
    }

    private void ShowState()
    {
        if (_updates is null || _loc is null)
            return;
        CaptionText.Text = UpdateTexts.Status(_updates, _loc) ?? _loc[UpdateTexts.HowKey];
        Progress.IsVisible = _updates.Step == UpdateStep.Downloading;
        Progress.Value = _updates.DownloadPercent;
        Buttons.IsVisible = !_updates.IsBusy;
    }

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
