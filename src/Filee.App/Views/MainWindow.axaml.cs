using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Views;

public partial class MainWindow : Window
{
    private bool _asking;
    private bool _releasePosted;
    private bool _releasing;

    public MainWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
    }

    private async void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await Launcher.LaunchUriAsync(new Uri(vm.RepositoryUrl));
    }

    private async void OnSponsor(object? sender, RoutedEventArgs e) =>
        await SponsorWindow.Create(AppHost.Get<ILocalizer>()).ShowDialog(this);

    /// <summary>
    /// Closing the window keeps Filee running in the tray, quits, or asks which (<see cref="AppSettings.CloseAction"/>).
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.CloseReason is not WindowCloseReason.WindowClosing || _releasing)
            return;
        e.Cancel = true;
        // Close() from code goes to the tray, as before; the setting is for the title bar's close button and Alt+F4.
        if (e.IsProgrammatic)
            HideToTray();
        else
            CloseAsTheUserChose();
    }

    /// <summary>What the user's close does: hide, quit or ask (<see cref="AppSettings.CloseAction"/>).</summary>
    internal void CloseAsTheUserChose()
    {
        var store = AppHost.Get<UserDataStore>();
        switch (store.Settings.CloseAction)
        {
            case CloseAction.Tray:
                HideToTray();
                break;
            case CloseAction.Quit:
                Quit();
                break;
            default:
                _ = AskAsync(store);
                break;
        }
    }

    private async Task AskAsync(UserDataStore store)
    {
        if (_asking)
            return;
        _asking = true;
        try
        {
            var dialog = new CloseAskWindow();
            if (await dialog.ShowDialog<CloseAction?>(this) is not { } action)
                return;
            if (dialog.DontAskAgain)
            {
                store.Settings.CloseAction = action;
                store.SaveSettings();
            }
            if (action == CloseAction.Quit)
                Quit();
            else
                HideToTray();
        }
        finally
        {
            _asking = false;
        }
    }

    // Posted: shutting down closes this window again, which must not happen inside its own Closing handler.
    private static void Quit() => Dispatcher.UIThread.Post(() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());

    /// <summary>
    /// Where the window was, and how large, before Settings → Donut toolbar moved it over for a moment to make room for
    /// the donut on screen: what closing it remembers instead (<see cref="WindowService"/>). Null while it is where the
    /// user put it.
    /// </summary>
    internal (PixelPoint Position, Size ClientSize)? RestPlace { get; set; }

    /// <summary>
    /// Where it landed, moved over (null while it is on its way): moved by the user since, it stays where they put it,
    /// and closing remembers that rather than <see cref="RestPlace"/>'s place.
    /// </summary>
    internal PixelPoint? Landed { get; set; }

    /// <summary>Where it was sent, moving over (null on its way back).</summary>
    internal PixelPoint? SlidTo { get; set; }

    /// <summary>
    /// Still where it moved over to: where it landed, or where it was sent (an X11 window reports its new place only
    /// once the window manager has moved it).
    /// </summary>
    internal bool StillWhereItSlid => Landed is { } at && IsNear(Position, at) || SlidTo is { } to && IsNear(Position, to);

    /// <summary>
    /// The place closing remembers: where it was before it moved over, unless the user moved it since.
    /// </summary>
    internal PixelPoint PlaceToRemember => RestPlace is { } rest && (Landed is null || StillWhereItSlid) ? rest.Position : Position;

    /// <summary>Within 2 pixels (a window manager may nudge a window it moved).</summary>
    internal static bool IsNear(PixelPoint a, PixelPoint b) => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2;

    /// <summary>
    /// Leaves Filee in the tray: hides the window at once, then closes it for real, so its pages, visuals and images
    /// don't stay in memory until the next time (issue #119). <see cref="WindowService.ShowMain"/> opens a new one.
    /// </summary>
    internal void HideToTray()
    {
        Hide();
        if (_releasePosted)
            return;
        _releasePosted = true;
        // Posted: a window can't close inside its own Closing handler.
        Dispatcher.UIThread.Post(() =>
        {
            _releasePosted = false;
            if (IsVisible)
                return; // shown again meanwhile
            _releasing = true;
            Close();
            // Still open (the close was cancelled): the next close goes to the tray again, not for good.
            if (PlatformImpl is not null)
                _releasing = false;
        });
    }

    /// <summary>
    /// Being closed for good after going to the tray, or closed: it can't be shown again, so
    /// <see cref="WindowService.ShowMain"/> opens a new one instead.
    /// </summary>
    internal bool IsReleased => _releasing || PlatformImpl is null;
}
