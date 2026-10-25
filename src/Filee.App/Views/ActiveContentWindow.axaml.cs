// Documents with macros or other active content: the user's decision before a job opens them in an office suite.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.Core.Conversion;

namespace Filee.App.Views;

public partial class ActiveContentWindow : Window
{
    public ActiveContentWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
    }

    /// <summary>What the user chose; closing the window cancels the job.</summary>
    public ActiveContentDecision Result { get; private set; } = ActiveContentDecision.Cancel;

    /// <summary>"Don't ask again": the choice becomes the setting (Settings → Security and limits).</summary>
    public bool Remember => RememberChoice.IsChecked == true;

    private void OnTrust(object? sender, RoutedEventArgs e) => CloseWith(ActiveContentDecision.Trust);

    private void OnSkip(object? sender, RoutedEventArgs e) => CloseWith(ActiveContentDecision.Skip);

    private void OnCancel(object? sender, RoutedEventArgs e) => CloseWith(ActiveContentDecision.Cancel);

    private void CloseWith(ActiveContentDecision decision)
    {
        Result = decision;
        Close();
    }
}
