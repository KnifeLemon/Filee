using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Tests;

public class PageRangeTests
{
    [Theory]
    [InlineData("", 5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData("2", 5, new[] { 1 })]
    [InlineData("2-4", 5, new[] { 1, 2, 3 })]
    [InlineData("4-", 5, new[] { 3, 4 })]
    [InlineData("-2", 5, new[] { 0, 1 })]
    [InlineData("5,1, 3", 5, new[] { 0, 2, 4 })]
    [InlineData("4-2", 5, new[] { 1, 2, 3 })]
    [InlineData("9-12", 5, new[] { 0, 1, 2, 3, 4 })] // nothing valid → everything
    [InlineData("x, 2", 5, new[] { 1 })]
    public void Parses_ranges(string expression, int pages, int[] expected) =>
        Assert.Equal(expected, PageRange.Parse(expression, pages));
}

public class FormatRegistryTests
{
    [Theory]
    [InlineData("a.JPEG", "jpg")]
    [InlineData("b.tif", "tiff")]
    [InlineData("c.HWPX", "hwpx")]
    [InlineData(".md", "md")]
    public void Detects_formats(string path, string id) => Assert.Equal(id, FormatRegistry.Detect(path)?.Id);

    [Fact]
    public void Unknown_extension_is_null() => Assert.Null(FormatRegistry.Detect("x.xyz"));

    [Fact]
    public void Extensions_are_unique() =>
        Assert.Equal(
            FormatRegistry.Known.SelectMany(f => f.Extensions).Count(),
            FormatRegistry.Known.SelectMany(f => f.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).Count());
}

public class ProfileSelectorTests
{
    private readonly List<ToolbarProfile> _profiles = BuiltInData.CreateProfiles();

    [Fact]
    public void Picks_profile_matching_all_files() =>
        Assert.Equal("images", ProfileSelector.Select(_profiles, ["a.jpg", "b.PNG"])!.Id);

    [Fact]
    public void Mixed_files_use_the_fallback() =>
        Assert.Equal("mixed", ProfileSelector.Select(_profiles, ["a.jpg", "b.docx"])!.Id);

    [Fact]
    public void Unknown_files_use_the_fallback() =>
        Assert.Equal("mixed", ProfileSelector.Select(_profiles, ["a.xyz"])!.Id);
}

public class BuiltInDataTests
{
    [Fact]
    public void Profiles_only_reference_existing_presets()
    {
        var ids = BuiltInData.CreatePresets().Select(p => p.Id).ToHashSet();
        foreach (var profile in BuiltInData.CreateProfiles())
            Assert.All(profile.PresetIds, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Preset_ids_are_unique()
    {
        var presets = BuiltInData.CreatePresets();
        Assert.Equal(presets.Count, presets.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Exactly_one_fallback_profile() =>
        Assert.Single(BuiltInData.CreateProfiles(), p => p.IsFallback);

    [Fact]
    public void Preset_targets_are_known_formats()
    {
        foreach (var preset in BuiltInData.CreatePresets())
            Assert.True(preset.TargetFormat == BuiltInData.SameAsSource || FormatRegistry.FindById(preset.TargetFormat) is not null, preset.Id);
    }
}
