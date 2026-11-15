using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class AskCompressionWindow : Window
{
    public AskCompressionWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
        // Ready for the arrow keys: the first setting shown has the focus.
        Opened += (_, _) =>
        {
            Control? first = QualitySlider.IsEffectivelyVisible ? QualitySlider
                : VideoQuality.IsEffectivelyVisible ? VideoQuality
                : AudioBitrate.IsEffectivelyVisible ? AudioBitrate
                : PdfLevel.IsEffectivelyVisible ? PdfLevel : StartCompression;
            first.Focus();
        };
    }

    /// <summary>The settings chosen, or null when the window was cancelled or closed.</summary>
    public CompressionAnswer? Result { get; private set; }

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AskCompressionViewModel vm)
            return;
        Result = vm.Answer();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
