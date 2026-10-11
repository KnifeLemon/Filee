// "Sponsor" on the About page leads to Fairy in Korean (Korean cards and easy-pay) and to GitHub Sponsors otherwise.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class AboutSponsorTests
{
    [AvaloniaTheory]
    [InlineData("ko", AboutPageViewModel.FairyUrl)]
    [InlineData("en", AboutPageViewModel.GitHubSponsorsUrl)]
    [InlineData("zh-CN", AboutPageViewModel.GitHubSponsorsUrl)]
    public void Sponsor_leads_where_the_language_can_pay(string language, string url)
    {
        TestServices.EnsureInitialized(language);
        using var about = new AboutPageViewModel(AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), AppHost.Get<UpdateService>());

        Assert.Equal(url, about.SponsorUrl);
    }
}
