// System tray icon: open settings, pause gestures, quit.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Services;

public sealed class TrayService(UserDataStore store, ILocalizer loc, WindowService windows)
{
    private TrayIcon? _tray;
    private NativeMenuItem? _open, _pause, _quit;

    public void Create(Application app)
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Filee/Assets/Icons/filee.ico"));
        _open = new NativeMenuItem();
        _open.Click += (_, _) => windows.ShowMain();
        _pause = new NativeMenuItem();
        _pause.Click += (_, _) =>
        {
            store.Settings.Paused = !store.Settings.Paused;
            store.SaveSettings();
        };
        _quit = new NativeMenuItem();
        _quit.Click += (_, _) => (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            Menu = new NativeMenu { Items = { _open, _pause, new NativeMenuItemSeparator(), _quit } },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => windows.ShowMain();
        TrayIcon.SetIcons(app, [_tray]);

        Refresh();
        loc.LanguageChanged += (_, _) => Refresh();
        store.SettingsChanged += (_, _) => Refresh();
    }

    private void Refresh()
    {
        if (_tray is null)
            return;
        _open!.Header = loc["tray.open"];
        _pause!.Header = store.Settings.Paused ? loc["tray.resume"] : loc["tray.pause"];
        _quit!.Header = loc["tray.quit"];
        var drag = store.Settings.Triggers.FirstOrDefault(t => t.Enabled && t.Kind == TriggerKind.Drag);
        _tray.ToolTipText = store.Settings.Paused
            ? loc["triggers.paused"]
            : loc.Format("tray.tooltip", drag is null ? "—" : GestureText.Modifiers(drag.Modifiers));
    }

    public void Dispose() => _tray?.Dispose();
}
