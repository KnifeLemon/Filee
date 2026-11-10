// Shown in place of a settings page that threw while it was being built (#1327). Without it the navigation moved to
// the new page while the old one stayed on screen, and nothing said why. The exception itself goes to the log.

using CommunityToolkit.Mvvm.ComponentModel;

namespace Filee.App.ViewModels.Pages;

public sealed class ErrorPageViewModel(string? title) : ObservableObject
{
    /// <summary>The page that failed, as the navigation names it; none when only its view is known.</summary>
    public string? Title { get; } = title;

    public bool HasTitle => !string.IsNullOrEmpty(Title);
}
