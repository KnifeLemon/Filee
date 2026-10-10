using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.ViewModels.Pages;

namespace Filee.App.Views.Pages;

public partial class WatchFoldersPage : UserControl
{
    public WatchFoldersPage() => InitializeComponent();

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WatchRuleViewModel rule && await PickFolderAsync() is { } path)
            rule.Folder = path;
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WatchRuleViewModel rule && await PickFolderAsync() is { } path)
            rule.OutputFolder = path;
    }

    /// <summary>Double-clicking a ready-made replacement adds it as a step, like its + button.</summary>
    private void OnRecipeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RenameRecipeViewModel recipe)
            recipe.AddCommand.Execute(null);
    }

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
