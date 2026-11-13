// GIMP pictures (.xcf), read and written by Filee: they open the images donut and are a target only where the Design &
// print field is on.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Tests;

public class XcfFormatTests
{
    [Theory]
    [InlineData("drawing.xcf")]
    [InlineData("LOGO.XCF")]
    public void An_xcf_uses_the_image_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("xcf"), FormatRegistry.Detect(file));
        Assert.Equal("images", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void An_xcf_is_a_design_target()
    {
        var xcf = FormatRegistry.Get("xcf")!;
        Assert.Equal(FormatCategory.Image, xcf.Category);
        Assert.True(xcf.Writable);
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(xcf));
        Assert.False(FormatFields.IsShown("xcf", new HashSet<string> { "design" }));
        Assert.True(FormatFields.IsShown("xcf", new HashSet<string>()));
    }
}
