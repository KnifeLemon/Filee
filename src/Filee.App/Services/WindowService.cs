// Opens windows and dialogs: the main (settings) window, the preset popup, confirmations and toasts.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.App.Services;

public sealed class WindowService(ILocalizer loc, IServiceProvider services)
{
    private MainWindow? _main;
    private ToastWindow? _toast;
    private EngineSetupWindow? _engineSetup;

    public MainWindow? Main => _main;

    /// <summary>Shows (or brings to front) the main window, optionally on a page.</summary>
    public void ShowMain(string? page = null)
    {
        if (_main is null)
        {
            _main = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
            _main.Closed += (_, _) => _main = null;
        }
        if (page is not null && _main.DataContext is MainWindowViewModel vm)
            vm.Navigate(page);

        if (!_main.IsVisible)
            _main.Show();
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    /// <summary>Opens the optional-engine choice (first run); a second call brings the open window to front.</summary>
    public void ShowEngineSetup()
    {
        if (_engineSetup is not null)
        {
            _engineSetup.Activate();
            return;
        }
        _engineSetup = new EngineSetupWindow { DataContext = ActivatorUtilities.CreateInstance<EngineSetupViewModel>(services) };
        _engineSetup.Closed += (_, _) => _engineSetup = null;
        _engineSetup.Show();
    }

    /// <summary>Creates the toast window once; it shows and hides itself with the job list.</summary>
    public void EnsureToast()
    {
        _toast ??= new ToastWindow { DataContext = services.GetRequiredService<ConversionService>() };
    }

    /// <summary>Opens the preset popup. Returns true when the user saved (the preset object was updated).</summary>
    public async Task<bool> EditPresetAsync(Preset preset, Window? owner)
    {
        var dialog = new PresetEditorWindow
        {
            DataContext = new PresetEditorViewModel(preset, loc),
            Topmost = owner?.Topmost ?? false,
        };
        owner ??= _main ?? MainWindowOrNull();
        if (owner is null || !owner.IsVisible)
        {
            // No visible owner (e.g. the donut was opened from the tray): show as a normal window.
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            await closed.Task;
            return dialog.Saved;
        }
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>Simple yes/no confirmation.</summary>
    public Task<bool> ConfirmAsync(Window owner, string message, bool destructive = false) =>
        ShowMessageAsync(owner, message, destructive, withCancel: true);

    /// <summary>A message with only an OK button.</summary>
    public Task MessageAsync(Window owner, string message) => ShowMessageAsync(owner, message, destructive: false, withCancel: false);

    private async Task<bool> ShowMessageAsync(Window owner, string message, bool destructive, bool withCancel)
    {
        var dialog = new Window
        {
            Title = loc["app.name"],
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        Motion.Track(dialog);
        var yes = new Button { Content = destructive ? loc["common.delete"] : loc["common.ok"], IsDefault = true };
        yes.Classes.Add("accent");
        var no = new Button { Content = loc["common.cancel"], IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        no.IsVisible = withCancel;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { no, yes },
                },
            },
        };
        return await dialog.ShowDialog<bool>(owner);
    }

    private static Window? MainWindowOrNull() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
