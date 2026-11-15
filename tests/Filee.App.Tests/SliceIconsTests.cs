// The slice icons: every one draws, every built-in preset that does more than convert to a format has its own icon and
// plain format conversions have none, a preset's own icon (or none) wins over the default, and the picker's search.

using Avalonia.Headless.XUnit;
using Filee.App.Controls;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class SliceIconsTests
{
    [AvaloniaFact]
    public void Every_icon_draws_inside_its_square()
    {
        foreach (var key in SliceIcons.Keys)
        {
            var bounds = SliceIcons.Geometry(key)!.Bounds;
            Assert.True(bounds.Width > 0 && bounds.Height > 0, key);
            Assert.InRange(bounds.Left, -0.01, SliceIcons.Box);
            Assert.InRange(bounds.Right, 0, SliceIcons.Box + 0.01);
            Assert.InRange(bounds.Top, -0.01, SliceIcons.Box);
            Assert.InRange(bounds.Bottom, 0, SliceIcons.Box + 0.01);
        }
    }

    [Fact]
    public void Every_icon_is_in_the_picker_and_no_key_is_listed_twice()
    {
        var keys = SliceIcons.Keys.ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(keys.ToHashSet(), SliceIcons.Categories.SelectMany(c => c.Keys).ToHashSet());
        Assert.Equal(Enum.GetValues<SliceIconCategory>(), SliceIcons.Categories.Select(c => c.Category));
    }

    [Fact]
    public void Built_in_presets_that_do_more_than_convert_have_an_icon_and_format_conversions_none()
    {
        foreach (var preset in BuiltInData.CreatePresets())
        {
            var icon = SliceIcons.DefaultFor(preset);
            if (icon is not null)
                Assert.True(SliceIcons.Exists(icon), $"{preset.Id}: {icon}");
            else
                Assert.Equal(PresetKind.Convert, preset.Kind); // a conversion to a format, named by it
        }
        // The ones the user reviewed, spot-checked.
        Assert.Equal("crop", SliceIcons.DefaultFor(Find("tool-crop")));
        Assert.Equal("auto-fix", SliceIcons.DefaultFor(Find("remove-background")));
        Assert.Equal("rotate-right", SliceIcons.DefaultFor(Find("rotate")));
        Assert.Null(SliceIcons.DefaultFor(Find("to-png")));
        Assert.Null(SliceIcons.DefaultFor(Find("mp4-720")));
    }

    [Fact]
    public void Every_preset_given_an_icon_is_a_built_in_one()
    {
        var ids = BuiltInData.CreatePresets().Select(p => p.Id).ToHashSet();
        Assert.All(SliceIcons.DefaultIds, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Every_built_in_group_and_tool_has_an_icon()
    {
        Assert.All(BuiltInData.CreatePresets().Where(p => p.IsGroup || p.IsTool),
            p => Assert.NotNull(SliceIcons.DefaultFor(p)));
    }

    [Fact]
    public void A_copy_of_a_tool_shows_its_tools_icon()
    {
        Assert.Equal("content-cut", SliceIcons.DefaultFor(new Preset { Kind = PresetKind.Tool, Tool = Preset.ClipTool }));
        Assert.Equal("crop", SliceIcons.DefaultFor(new Preset { Kind = PresetKind.Tool }));
        Assert.Null(SliceIcons.DefaultFor(new Preset { Name = "My PNG" }));
    }

    [Fact]
    public void A_presets_own_icon_wins_and_none_means_none()
    {
        var crop = Find("tool-crop");
        Assert.Equal("crop", SliceIcons.For(crop));
        crop.Icon = "star-outline";
        Assert.Equal("star-outline", SliceIcons.For(crop));
        crop.Icon = Preset.NoIcon;
        Assert.Null(SliceIcons.For(crop));
        crop.Icon = "an-icon-from-a-newer-filee";
        Assert.Equal("crop", SliceIcons.For(crop));
        Assert.Equal("star-outline", SliceIcons.For(new Preset { Icon = "star-outline" }));
    }

    [Fact]
    public void The_picker_finds_icons_by_name_and_search_words()
    {
        Assert.Contains("crop", SliceIcons.Search("crop"));
        Assert.Contains("auto-fix", SliceIcons.Search("background"));
        Assert.Contains("console", SliceIcons.Search("term"));
        Assert.Contains("arrow-collapse-all", SliceIcons.Search("Compress"));
        Assert.Empty(SliceIcons.Search("zzzz"));
        Assert.Equal(SliceIcons.Keys, SliceIcons.Search(" "));
    }

    private static Preset Find(string id) => BuiltInData.CreatePresets().Single(p => p.Id == id);
}
