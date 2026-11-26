namespace Filee.App.Tests;

public class ScreenshotPathTests
{
    [Theory]
    [InlineData("page-theme-en.png", "en")]
    [InlineData("whatsnew-2.3.0-id-light-4-fields.png", "id")]
    [InlineData("whatsnew-poster-pt-BR-crop.png", "pt-BR")]
    [InlineData("copy-folder-group-zh-CN.png", "zh-CN")]
    [InlineData("setup-1-welcome-bn-BD.png", "bn-BD")]
    [InlineData("page-theme-PT-br.png", "pt-BR")]
    [InlineData("donut-download-badge-dark.png", null)]
    [InlineData("annotate-summary.png", null)]
    [InlineData("toast-stack-0-0-1.5.png", null)]
    [InlineData("readability-A_presets_own_direction_is_followed.txt", null)]
    public void Language_code_must_be_a_whole_piece_of_the_name(string name, string? expected) =>
        Assert.Equal(expected, TestServices.ScreenshotLanguage(name));

    [Fact]
    public void Screenshots_with_a_language_go_in_its_folder_and_the_others_stay_in_the_main_folder()
    {
        var main = TestServices.ScreenshotDirectory;

        Assert.Equal(Path.Combine(main, "ko", "page-theme-ko.png"), TestServices.ScreenshotPath("page-theme-ko.png"));
        Assert.True(Directory.Exists(Path.Combine(main, "ko")));
        Assert.Equal(Path.Combine(main, "annotate-summary.png"), TestServices.ScreenshotPath("annotate-summary.png"));
    }
}
