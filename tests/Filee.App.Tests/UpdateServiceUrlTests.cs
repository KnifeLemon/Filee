// The links the About page opens for feedback: the Report tile goes straight to the bug report form, the Suggest tile
// to the feature request form.

using Filee.App.Services;

namespace Filee.App.Tests;

public class UpdateServiceUrlTests
{
    [Fact]
    public void The_bug_report_link_opens_the_bug_report_form_of_the_repository()
    {
        var url = UpdateService.BugReportUrl;
        Assert.StartsWith(UpdateService.RepositoryUrl.TrimEnd('/') + "/issues/new", url);
        Assert.EndsWith("?template=bug_report.yml", url);
    }

    [Fact]
    public void The_suggestion_link_opens_the_feature_request_form_of_the_repository()
    {
        var url = UpdateService.FeatureRequestUrl;
        Assert.StartsWith(UpdateService.RepositoryUrl.TrimEnd('/') + "/issues/new", url);
        Assert.EndsWith("?template=feature_request.yml", url);
    }
}
