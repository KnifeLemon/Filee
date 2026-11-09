using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.Services.Connect;
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
        }
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

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = vm.Localizer["connect.picker_files_title"],
                AllowMultiple = true
            });

            if (files is null || files.Count == 0) return;

            foreach (var file in files)
            {
                var localPath = file.TryGetLocalPath();
                if (!string.IsNullOrEmpty(localPath) && System.IO.File.Exists(localPath))
                {
                    var targetRel = string.IsNullOrEmpty(vm.CurrentPath)
                        ? file.Name
                        : $"{vm.CurrentPath.TrimEnd('/')}/{file.Name}";
                    await vm.ConnectService.AddLocalFileAsync(localPath, targetRel);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Upload files failed: {ex.Message}");
        }
    }

    private async void OnUploadFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileeConnectViewModel vm) return;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = vm.Localizer["connect.picker_folder_title"]
            });

            if (folders is null || folders.Count == 0) return;

            var folderPath = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(folderPath) || !System.IO.Directory.Exists(folderPath)) return;

            var baseFolder = new System.IO.DirectoryInfo(folderPath);
            var files = System.IO.Directory.EnumerateFiles(folderPath, "*", System.IO.SearchOption.AllDirectories);

            foreach (var f in files)
            {
                var relFromFolder = System.IO.Path.GetRelativePath(baseFolder.FullName, f).Replace('\\', '/');
                var targetRel = string.IsNullOrEmpty(vm.CurrentPath)
                    ? $"{baseFolder.Name}/{relFromFolder}"
                    : $"{vm.CurrentPath.TrimEnd('/')}/{baseFolder.Name}/{relFromFolder}";
                await vm.ConnectService.AddLocalFileAsync(f, targetRel);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Upload folder failed: {ex.Message}");
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
}
