// Get fonts must lead to the font's own source: Aptos and DengXian aren't on Google Fonts (#1422, #1423).

using Filee.App.Services;

namespace Filee.App.Tests;

public class FontSourcesTests
{
    [Theory]
    [InlineData("Aptos")]
    [InlineData("Aptos Display")]
    [InlineData("Aptos Narrow")]
    [InlineData("aptos serif")]
    public void Aptos_goes_to_microsofts_download(string family) =>
        Assert.Equal("https://www.microsoft.com/en-us/download/details.aspx?id=106087", FontSources.Url(family)!.ToString());

    [Theory]
    [InlineData("DengXian")]
    [InlineData("DengXian Light")]
    [InlineData("等线")]
    public void DengXian_goes_to_its_microsoft_typography_page(string family) =>
        Assert.Equal("https://learn.microsoft.com/en-us/typography/font-list/dengxian", FontSources.Url(family)!.ToString());

    [Fact]
    public void A_family_that_only_starts_with_aptos_letters_is_still_searched() =>
        Assert.Equal("https://fonts.google.com/?query=Aptosa", FontSources.Url("Aptosa")!.ToString());
}
