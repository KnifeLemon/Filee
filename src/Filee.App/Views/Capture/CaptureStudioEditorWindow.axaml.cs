using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Filee.App.ViewModels.Capture;

namespace Filee.App.Views.Capture;

public partial class CaptureStudioEditorWindow : Window
{
    public CaptureStudioEditorWindow()
    {
        InitializeComponent();
    }

    public CaptureStudioEditorWindow(CaptureStudioEditorViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestSaveFileHandler = async defaultName =>
        {
            var ext = Path.GetExtension(defaultName).TrimStart('.');
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Captured Video",
                SuggestedFileName = defaultName,
                DefaultExtension = ext,
                FileTypeChoices =
                [
                    new FilePickerFileType($"{ext.ToUpperInvariant()} Video") { Patterns = [$"*.{ext}"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });

            return file?.Path.LocalPath;
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is CaptureStudioEditorViewModel vm && vm.IsPlaying)
        {
            vm.TogglePlayCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}
