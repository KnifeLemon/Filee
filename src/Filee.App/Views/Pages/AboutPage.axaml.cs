using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;

namespace Filee.App.Views.Pages;

public partial class AboutPage : UserControl
{
    /// <summary>Below this width (DIPs) the information and update cards are stacked instead of side by side.</summary>
    internal const double StackBelowWidth = 760;

    public AboutPage()
    {
        InitializeComponent();
        InfoUpdateGrid.SizeChanged += (_, e) => InfoUpdateGrid.Classes.Set("stacked", e.NewSize.Width < StackBelowWidth);
        AttachedToVisualTree += (_, _) => ApplyVersionColour();
        ActualThemeVariantChanged += (_, _) => ApplyVersionColour();
    }

    /// <summary>
    /// The text of the version pill: the accent itself on dark, the deeper accent on light (enough contrast on both),
    /// like the version pill in Setup. The pill's tint (AccentSoftBrush) follows the accent by itself.
    /// </summary>
    private void ApplyVersionColour()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        if (Application.Current?.FindResource(dark ? "AccentBrush" : "AccentStrongBrush") is IBrush brush)
            VersionText.Foreground = brush;
    }

    private async void OnOpenRepository(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutPageViewModel vm && TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(new Uri(vm.RepositoryUrl));
    }

    // A star is given on the repository page itself.
    private void OnStar(object? sender, RoutedEventArgs e) => OnOpenRepository(sender, e);

    private async void OnSponsor(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            await SponsorWindow.Create(AppHost.Get<ILocalizer>()).ShowDialog(owner);
    }

    private async void OnIssue(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(new Uri(Services.UpdateService.NewIssueUrl));
    }
}
