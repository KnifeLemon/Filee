using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Controls;

namespace Filee.App.Views.Pages;

public partial class ThemePage : UserControl
{
    public ThemePage() => InitializeComponent();

    /// <summary>Plays the donut's open animation so the "reduce animations" setting can be felt right away.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PreviewDonut.PlayOpenAnimation();
    }

    /// <summary>The sample Save button shows the same tick as the real Save buttons, but saves nothing.</summary>
    private void OnPreviewSaveClick(object? sender, RoutedEventArgs e)
    {
        PreviewSaveFeedback.Result = SaveResult.None; // So a second click plays it again.
        PreviewSaveFeedback.Result = SaveResult.Succeeded;
    }
}
