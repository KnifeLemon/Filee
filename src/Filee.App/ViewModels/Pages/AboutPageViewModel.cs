// About: version, license, third-party notices.

using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Services;
using Filee.Core.Localization;

namespace Filee.App.ViewModels.Pages;

public sealed partial class AboutPageViewModel(ILocalizer loc) : ObservableObject
{
    public string Version { get; } = loc.Format("about.version", UpdateService.CurrentVersion);
    public string RepositoryUrl { get; } = UpdateService.RepositoryUrl;

    public string Notices { get; } = ReadNotices();

    private static string ReadNotices()
    {
        var uri = new Uri("avares://Filee/Assets/THIRD-PARTY-NOTICES.md");
        if (!AssetLoader.Exists(uri))
            return "";
        using var reader = new StreamReader(AssetLoader.Open(uri));
        return reader.ReadToEnd();
    }
}
