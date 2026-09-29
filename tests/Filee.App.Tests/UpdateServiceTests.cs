using Filee.App.Services;

namespace Filee.App.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.2", "1.0.1", true)]
    [InlineData("1.1", "1.0.9", true)]
    [InlineData("v2.0.0-beta.1", "1.9.0", true)]
    [InlineData("v1.0.1", "1.0.1", false)]
    [InlineData("v1.0.0", "1.0.1", false)]
    [InlineData("nightly", "1.0.1", false)]
    [InlineData(null, "1.0.1", false)]
    public void Release_tags_compare_by_version(string? tag, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(tag, current));

    [Fact]
    public void Download_page_is_the_latest_release()
    {
        Assert.EndsWith("/releases/latest", UpdateService.LatestReleaseUrl);
        Assert.StartsWith(UpdateService.RepositoryUrl.TrimEnd('/'), UpdateService.LatestReleaseUrl);
    }
}
