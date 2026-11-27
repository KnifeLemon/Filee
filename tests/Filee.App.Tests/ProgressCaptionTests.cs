// The label in the middle of the tick progress dial (Settings → Theme → Progress ring): "MP4 → WEBP".

using Filee.App.Services;

namespace Filee.App.Tests;

public class ProgressCaptionTests
{
    [Fact]
    public void Shows_the_source_format_and_the_preset_in_capitals() =>
        Assert.Equal("MP4 → WEBP", ProgressCaptions.For([@"C:\clips\new.mp4"], "WebP"));

    [Fact]
    public void Several_files_of_one_kind_still_show_the_format() =>
        Assert.Equal("PNG → JPG", ProgressCaptions.For(["a.png", "b.PNG"], "jpg"));

    [Fact]
    public void Files_of_different_kinds_show_the_preset_alone() =>
        Assert.Equal("PDF", ProgressCaptions.For(["a.png", "b.jpg"], "PDF"));

    [Fact]
    public void A_source_without_an_extension_shows_the_preset_alone() =>
        Assert.Equal("ZIP", ProgressCaptions.For([@"C:\photos"], "Zip"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_preset_name_there_is_no_label(string? preset) =>
        Assert.Null(ProgressCaptions.For(["new.mp4"], preset));
}
