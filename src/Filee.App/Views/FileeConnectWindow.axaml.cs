using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class FileeConnectWindow : Window
{
    public FileeConnectWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FileeConnectViewModel vm)
        {
            vm.RequestClose += () => Close();
            vm.RequestFilePreview += OnRequestFilePreview;
        }
    }

    private void OnRequestFilePreview(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch { }
    }

    private async void OnEndSessionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileeConnectViewModel vm)
        {
            await vm.EndSessionCommand.ExecuteAsync(null);
            Close();
        }
    }

    private async void OnUploadFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileeConnectViewModel vm) return;

        var storage = StorageProvider;
        if (!storage.CanOpen) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select files to share",
            AllowMultiple = true
        });

        if (files.Count > 0)
        {
            var paths = files.Select(f => f.Path.LocalPath).ToList();
            await vm.AddLocalFilesAsync(paths);
        }
    }

    private async void OnUploadFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileeConnectViewModel vm) return;

        var storage = StorageProvider;
        if (!storage.CanOpen) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder to share",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var paths = folders.Select(f => f.Path.LocalPath).ToList();
            await vm.AddLocalFilesAsync(paths);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            if (DataContext is FileeConnectViewModel vm)
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is FileeConnectViewModel vm)
        {
            await vm.ConnectService.StopSessionAsync();
        }
    }
}
