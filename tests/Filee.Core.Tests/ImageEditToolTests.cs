// "Edit pictures" (#584): the conversion its window starts, the format a picture keeps, the choices kept in the tool, and
// the tool's place in a new library and in one from before settings schema 51.

using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ImageEditToolTests
{
    private static Preset Tool() => BuiltInData.ImageEditPresets().Single();

    [Fact]
    public void The_tool_is_a_tool_of_its_own_and_survives_saving()
    {
        var tool = Tool();
        Assert.True(tool.IsImageEditTool);
        Assert.False(tool.IsCropTool);
        Assert.False(tool.IsClipTool);

        var json = JsonSerializer.Serialize(new List<Preset> { tool }, FileeJsonContext.Default.ListPreset);
        Assert.Contains("\"tool\": \"image-edit\"", json);
        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();
        Assert.True(read.IsImageEditTool);
    }

    [Fact]
    public void The_conversion_has_the_chosen_format_and_options_and_leaves_the_tool_as_it_is()
    {
        var tool = Tool();
        tool.Name = "Icons";
        tool.Image.KeepMetadata = false; // set in the preset editor, not in the window
        var image = tool.Image.Clone();
        image.Resize = ResizeMode.LongEdge;
        image.LongEdge = 32;
        image.Rotation = ImageRotation.Right90;
        image.Grayscale = true;

        var conversion = ImageEditTool.ConversionOf(tool, "jpg", image);

        Assert.Equal(PresetKind.Convert, conversion.Kind);
        Assert.Null(conversion.Tool);
        Assert.Equal("jpg", conversion.TargetFormat);
        Assert.Equal((ResizeMode.LongEdge, 32, ImageRotation.Right90, true, false),
            (conversion.Image.Resize, conversion.Image.LongEdge, conversion.Image.Rotation, conversion.Image.Grayscale, conversion.Image.KeepMetadata));
        Assert.Equal(("tool-image-edit", "Icons", "{name}_edited"), (conversion.Id, conversion.Name, conversion.Output.FileNamePattern));
        Assert.NotSame(image, conversion.Image);

        Assert.True(tool.IsImageEditTool);
        Assert.Equal((BuiltInData.SameAsSource, ResizeMode.None), (tool.TargetFormat, tool.Image.Resize));
    }

    [Fact]
    public void The_choices_are_kept_in_the_tool_and_the_rest_of_it_stays()
    {
        var tool = Tool();
        tool.Image.IcoSizes = [32, 16];
        var image = new ImageOptions { Resize = ResizeMode.Width, Width = 640, Flip = ImageFlip.Horizontal, Quality = 70, IcoSizes = [256] };

        ImageEditTool.Remember(tool, "webp", image);

        Assert.Equal("webp", tool.TargetFormat);
        Assert.Equal((ResizeMode.Width, 640, ImageFlip.Horizontal, 70), (tool.Image.Resize, tool.Image.Width, tool.Image.Flip, tool.Image.Quality));
        Assert.Equal([32, 16], tool.Image.IcoSizes);
        Assert.True(tool.IsImageEditTool);
    }

    [Fact]
    public void A_width_and_height_is_kept_in_the_tool_with_its_unit_and_survives_saving()
    {
        var tool = Tool();
        var image = tool.Image.Clone();
        image.CustomSize = new CustomImageSize { Width = 413, Height = 531, Unit = SizeUnit.Centimetres, Dpi = 300 };
        ImageEditTool.Remember(tool, "jpg", image);
        image.CustomSize.Width = 1; // the tool has its own copy

        Assert.Equal((413, SizeUnit.Centimetres, 300), (tool.Image.CustomSize!.Width, tool.Image.CustomSize.Unit, tool.Image.CustomSize.Dpi));
        var clone = tool.Image.Clone().CustomSize!;
        Assert.Equal((SizeUnit.Centimetres, 300), (clone.Unit, clone.Dpi));

        var json = JsonSerializer.Serialize(new List<Preset> { tool }, FileeJsonContext.Default.ListPreset);
        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single().Image.CustomSize!;
        Assert.Equal((413, 531, SizeUnit.Centimetres, 300), (read.Width, read.Height, read.Unit, read.Dpi));

        // A unit a later version adds reads as pixels, instead of the whole presets file failing to read.
        var later = json.Replace("\"Centimetres\"", "\"Points\"", StringComparison.Ordinal);
        Assert.Equal(SizeUnit.Pixels, JsonSerializer.Deserialize(later, FileeJsonContext.Default.ListPreset)!.Single().Image.CustomSize!.Unit);
    }

    [Theory]
    [InlineData(3.5, SizeUnit.Centimetres, 300, 413)]
    [InlineData(45, SizeUnit.Millimetres, 300, 531)]
    [InlineData(2, SizeUnit.Inches, 300, 600)]
    [InlineData(640, SizeUnit.Pixels, 300, 640)]
    public void A_printed_size_is_turned_into_pixels_and_back(double value, SizeUnit unit, int dpi, int pixels)
    {
        Assert.Equal(pixels, CustomImageSize.ToPixels(value, unit, dpi));
        Assert.Equal(value, CustomImageSize.FromPixels(pixels, unit, dpi), 1);
    }

    [Fact]
    public void A_picture_keeps_its_format_where_it_can_be_written_else_becomes_png()
    {
        var magick = new FakeConverter("magick", new ConversionEdge("png", "png"), new ConversionEdge("jpg", "jpg"),
            new ConversionEdge("dcm", "png"), new ConversionEdge("png", "webp"));
        var planner = new ConverterCatalog([magick]).CreatePlanner();

        Assert.Equal(BuiltInData.SameAsSource, ImageEditTool.TargetFor("png", BuiltInData.SameAsSource, planner));
        Assert.Equal(BuiltInData.SameAsSource, ImageEditTool.TargetFor("jpg", BuiltInData.SameAsSource, planner));
        Assert.Equal("png", ImageEditTool.TargetFor("dcm", BuiltInData.SameAsSource, planner)); // DICOM is only read
        Assert.Equal("webp", ImageEditTool.TargetFor("png", "webp", planner));
        Assert.True(ImageEditTool.CanEdit("png"));
        Assert.False(ImageEditTool.CanEdit("mp4"));
        Assert.False(ImageEditTool.CanEdit("pdf"));
    }

    [Fact]
    public void The_formats_offered_are_those_a_picture_can_be_written_in()
    {
        // HEIC is written with the FFmpeg download: without it, not offered.
        var magick = new FakeConverter("magick", new ConversionEdge("png", "jpg"), new ConversionEdge("png", "webp"),
            new ConversionEdge("dcm", "png"), new ConversionEdge("jpg", "ico"));
        var planner = new ConverterCatalog([magick]).CreatePlanner();

        Assert.Equal(["png", "jpg", "webp", "ico"], ImageEditTool.TargetsFrom(["png", "dcm", "jpg"], planner));
        Assert.Equal(["ico"], ImageEditTool.TargetsFrom(["jpg"], planner));
        Assert.DoesNotContain("heic", ImageEditTool.TargetsFrom(["png"], planner));
    }

    // ───────────────────────── Settings schema 51 ─────────────────────────

    [Fact]
    public void A_new_library_has_the_tool_after_crop_and_on_the_images_donut_after_half_size()
    {
        var presets = BuiltInData.CreatePresets();
        var edit = presets.Single(p => p.Id == "tool-image-edit");
        Assert.Equal("tool-crop", presets[presets.IndexOf(edit) - 1].Id);
        var images = BuiltInData.CreateProfiles().Single(p => p.Id == "images");
        Assert.Equal("tool-image-edit", images.PresetIds[images.PresetIds.IndexOf("half-size") + 1]);
        Assert.DoesNotContain("tool-image-edit", images.BackPresetIds!);
        Assert.InRange(images.PresetIds.Count, 1, ToolbarProfile.MaxSlices);
    }

    [Fact]
    public void Version_50_libraries_get_the_tool_on_the_images_donut()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "tool-image-edit");
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.PresetIds.Remove("tool-image-edit");

        Assert.True(SettingsMigrations.ApplyToLibrary(50, presets, profiles));

        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id));
        Assert.True(presets.Single(p => p.Id == "tool-image-edit").IsImageEditTool);
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "images").PresetIds, images.PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(50, presets, profiles)); // once only

        // A front laid out by the user (without "Half size"), a full one, or the tool already on the back, stays as it is.
        var own = BuiltInData.CreateProfiles();
        var ownFront = own.Single(p => p.Id == "images").PresetIds;
        ownFront.Clear();
        ownFront.AddRange(["to-webp", "to-png"]);
        SettingsMigrations.ApplyToLibrary(50, BuiltInData.CreatePresets(), own);
        Assert.Equal(["to-webp", "to-png"], ownFront);

        var full = BuiltInData.CreateProfiles();
        var fullFront = full.Single(p => p.Id == "images").PresetIds;
        fullFront.Remove("tool-image-edit");
        while (fullFront.Count < ToolbarProfile.MaxSlices)
            fullFront.Add("to-gif");
        var before = fullFront.ToList();
        SettingsMigrations.ApplyToLibrary(50, BuiltInData.CreatePresets(), full);
        Assert.Equal(before, fullFront);

        var back = BuiltInData.CreateProfiles();
        var backImages = back.Single(p => p.Id == "images");
        backImages.PresetIds.Remove("tool-image-edit");
        backImages.BackPresetIds![^1] = "tool-image-edit";
        SettingsMigrations.ApplyToLibrary(50, BuiltInData.CreatePresets(), back);
        Assert.DoesNotContain("tool-image-edit", backImages.PresetIds);
    }

    [Fact]
    public void Back_from_an_older_Filee_the_tool_edits_pictures_again_and_keeps_the_users_changes()
    {
        // A Filee before 45 (2.0) has no "tool": it reads the tool as a crop and saves it so.
        var presets = BuiltInData.CreatePresets();
        var edit = presets.Single(p => p.Id == "tool-image-edit");
        edit.Tool = null;
        edit.Name = "Icons";
        edit.TargetFormat = "ico";
        Assert.True(edit.IsCropTool);

        Assert.True(SettingsMigrations.ApplyToLibrary(50, presets, BuiltInData.CreateProfiles()));

        Assert.True(edit.IsImageEditTool);
        Assert.Equal(("Icons", "ico"), (edit.Name, edit.TargetFormat));
        Assert.Single(presets, p => p.Id == "tool-image-edit");
    }
}
