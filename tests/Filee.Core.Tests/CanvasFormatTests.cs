// Obsidian's boards (.canvas, JSON Canvas) are read as drawings: they open the vector donut (PNG, PDF, SVG first), which
// existing libraries learn without taking a donut of the user's own. Read only; general, as Visio drawings are.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class CanvasFormatTests
{
    [Theory]
    [InlineData("plan.canvas")]
    [InlineData("Untitled.CANVAS")]
    public void A_canvas_uses_the_vector_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("canvas"), FormatRegistry.Detect(file));
        Assert.Equal("vector", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void It_is_read_only_general_and_always_shown()
    {
        var canvas = FormatRegistry.Get("canvas")!;
        Assert.Equal(FormatCategory.Vector, canvas.Category);
        Assert.False(canvas.Writable);
        Assert.Equal(FormatGrade.General, FormatFields.GradeOf(canvas));
        Assert.Equal(["vector"], FormatFields.Of(canvas).Select(f => f.Id));
    }

    [Fact]
    public void Version_86_libraries_learn_canvas_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var vector = profiles.Single(p => p.Id == "vector");
        vector.Extensions.Remove("canvas");
        Assert.True(SettingsMigrations.ApplyToLibrary(86, presets, profiles));
        Assert.Contains("canvas", vector.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(86, presets, profiles));

        vector.Extensions.Remove("canvas");
        profiles.Insert(0, new ToolbarProfile { Id = "my-boards", Extensions = ["CANVAS"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(86, presets, profiles));
        Assert.DoesNotContain("canvas", vector.Extensions); // the user's own donut has it
        Assert.Equal("my-boards", ProfileSelector.Select(profiles, ["plan.canvas"])?.Id);
    }
}
