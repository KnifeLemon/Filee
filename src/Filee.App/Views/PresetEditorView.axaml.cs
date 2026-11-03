using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class PresetEditorView : UserControl
{
    public PresetEditorView() => InitializeComponent();

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PresetEditorViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            vm.CustomFolder = path;
    }

    /// <summary>"Run a program": picks the program to start.</summary>
    private async void OnBrowseProgram(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PresetEditorViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            vm.ProgramExecutable = path;
    }
}
