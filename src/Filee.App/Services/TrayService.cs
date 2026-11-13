// System tray icon: open settings, pause gestures, install a new version, quit.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.Services;

public sealed class TrayService(UserDataStore store, ILocalizer loc, IPlatformServices platform, WindowService windows,
    UpdateService updates)
{
    private TrayIcon? _tray;
    private NativeMenuItem? _open, _record, _pause, _quit, _update;
    private bool? _templateIcon;

    public void Create(Application app)
    {
        _open = new NativeMenuItem();
        _open.Click += (_, _) => windows.ShowMain();
        _record = new NativeMenuItem();
        _record.Click += (_, _) => windows.ShowCaptureStudio();
        _pause = new NativeMenuItem();
        _pause.Click += (_, _) =>
        {
            store.Settings.Paused = !store.Settings.Paused;
            store.SaveSettings();
        };
        _quit = new NativeMenuItem();
        _quit.Click += (_, _) => (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        _update = new NativeMenuItem();
        _update.Click += async (_, _) => await windows.StartUpdateAsync(updates);

        _tray = new TrayIcon
        {
            // Set before the icon is attached: on macOS Avalonia passes the tooltip to native code, which can't take null.
            ToolTipText = "Filee",
            Menu = new NativeMenu { Items = { _open, _record, _pause, new NativeMenuItemSeparator(), _quit } },
            IsVisible = platform.SupportsDockVisibility ? store.Settings.ShowMenuBarIcon : true,
        };
        ApplyIcon();
        _tray.Clicked += (_, _) => windows.ShowMain();
        TrayIcon.SetIcons(app, [_tray]);

        Refresh();
        loc.LanguageChanged += (_, _) => Refresh();
        store.SettingsChanged += (_, _) => Refresh();
        updates.PropertyChanged += (_, e) =>
        {
            if (UpdateTexts.Affects(e.PropertyName))
                Refresh();
        };
    }

    private void Refresh()
    {
        if (_tray?.Menu is not { } menu)
            return;
        _tray.IsVisible = platform.SupportsDockVisibility ? store.Settings.ShowMenuBarIcon : true;
        ApplyIcon();
        // "Update to x.y.z" sits at the top of the menu while a newer release is out ("Downloading… 42%" meanwhile).
        if (updates.LatestVersion is { } latest)
        {
            _update!.Header = updates.Step is UpdateStep.Downloading or UpdateStep.Installing
                ? UpdateTexts.Short(updates, loc)
                : loc.Format("tray.update", latest);
            if (!menu.Items.Contains(_update))
                menu.Items.Insert(0, _update);
        }
        else
        {
            menu.Items.Remove(_update!);
        }
        _open!.Header = loc["tray.open"];
        _record!.Header = loc["capture_studio.caption"] ?? "Screen Recording";
        _pause!.Header = store.Settings.Paused ? loc["tray.resume"] : loc["tray.pause"];
        _quit!.Header = loc["tray.quit"];
        var drag = store.Settings.Triggers.FirstOrDefault(t => t.Enabled && t.Kind == TriggerKind.Drag);
        _tray.ToolTipText = store.Settings.Paused
            ? loc["triggers.paused"]
            : loc.Format("tray.tooltip", drag is null ? "—" : GestureText.Modifiers(drag.Modifiers));
    }

    /// <summary>
    /// The macOS menu bar gets a one-colour template icon unless the coloured one is chosen; the system paints a
    /// template white or black to match the bar. Windows and Linux keep the coloured icon.
    /// </summary>
    private void ApplyIcon()
    {
        var template = platform.SupportsDockVisibility && !store.Settings.ColoredMenuBarIcon;
        if (_tray is null || _templateIcon == template)
            return;
        _templateIcon = template;
        using var stream = AssetLoader.Open(new Uri(template
            ? "avares://Filee/Assets/Icons/filee-menubar.png"
            : "avares://Filee/Assets/Icons/filee.ico"));
        // Icon first: Avalonia marks the image already in the menu bar as a template, not the next one.
        _tray.Icon = new WindowIcon(stream);
        MacOSProperties.SetIsTemplateIcon(_tray, template);
    }

    public void Dispose() => _tray?.Dispose();
}
