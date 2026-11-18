// Label fitting and validation are independent of the host filesystem and the image writer.
using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public sealed class IsoVolumeLabelTests
{
    [Theory]
    [InlineData("my photos", IsoFileSystem.Joliet, "MY_PHOTOS")]
    [InlineData("照片", IsoFileSystem.Joliet, "FILEE")]
    [InlineData("__", IsoFileSystem.Joliet, "FILEE")]
    [InlineData("abcdefghijklmnopq", IsoFileSystem.Joliet, "ABCDEFGHIJKLMNOP")]
    [InlineData("照片", IsoFileSystem.Udf201, "照片")]
    [InlineData("  ", IsoFileSystem.Udf201, "FILEE")]
    [InlineData("abcdefghijklmn text字", IsoFileSystem.Udf201, "abcdefghijklmn")]
    public void Automatic_names_fit_the_selected_filesystem(string input, IsoFileSystem filesystem, string expected)
    {
        var label = IsoVolumeLabels.FromName(input, filesystem);
        Assert.Equal(expected, label);
        Assert.Null(IsoVolumeLabels.ErrorKey(label, filesystem));
    }

    [Theory]
    [InlineData("", IsoFileSystem.Joliet, true)]
    [InlineData("ABCDEFGHIJKLMNOP", IsoFileSystem.Joliet, true)]
    [InlineData("ABCDEFGHIJKLMNOPQ", IsoFileSystem.Joliet, false)]
    [InlineData("lower", IsoFileSystem.Joliet, false)]
    [InlineData("MY DISC", IsoFileSystem.Joliet, false)]
    [InlineData("é", IsoFileSystem.Joliet, false)]
    [InlineData("My disc", IsoFileSystem.Udf201, true)]
    [InlineData(" data", IsoFileSystem.Udf201, false)]
    [InlineData("data ", IsoFileSystem.Udf201, false)]
    [InlineData("a\0b", IsoFileSystem.Udf201, false)]
    public void Explicit_labels_are_accepted_or_refused_without_changes(string label, IsoFileSystem filesystem, bool valid) =>
        Assert.Equal(valid, IsoVolumeLabels.ErrorKey(label, filesystem) is null);

    [Fact]
    public void Udf_field_limits_use_encoded_bytes_and_keep_surrogate_pairs()
    {
        Assert.Null(IsoVolumeLabels.ErrorKey(new string('é', 30), IsoFileSystem.Udf201));
        Assert.NotNull(IsoVolumeLabels.ErrorKey(new string('é', 31), IsoFileSystem.Udf201));
        Assert.Null(IsoVolumeLabels.ErrorKey(new string('字', 15), IsoFileSystem.Udf201));
        Assert.NotNull(IsoVolumeLabels.ErrorKey(new string('字', 16), IsoFileSystem.Udf201));
        Assert.NotNull(IsoVolumeLabels.ErrorKey(new string((char)0xd800, 1), IsoFileSystem.Udf201));
        var fitted = IsoVolumeLabels.FromName(new string('a', 14) + "😀", IsoFileSystem.Udf201);
        Assert.Equal(new string('a', 14), fitted);
        Assert.Null(IsoVolumeLabels.ErrorKey(fitted, IsoFileSystem.Udf201));
    }

    [Fact]
    public void Label_survives_json_clone_and_old_presets_need_no_migration()
    {
        var old = JsonSerializer.Deserialize("{\"targetFormat\":\"iso\"}", FileeJsonContext.Default.Preset)!;
        Assert.Equal("", old.Archive.IsoVolumeLabel);
        old.Archive.IsoVolumeLabel = "MY_DISC";
        var copy = old.Clone();
        var json = JsonSerializer.Serialize(copy, FileeJsonContext.Default.Preset);
        Assert.Equal("MY_DISC", JsonSerializer.Deserialize(json, FileeJsonContext.Default.Preset)!.Archive.IsoVolumeLabel);
    }

    [Fact]
    public void Custom_labels_can_be_shared_in_the_gallery()
    {
        var preset = new Preset { Id = "iso-label", Name = "ISO", TargetFormat = "iso", Archive = { IsoVolumeLabel = "MY_DISC" } };
        var result = Filee.Core.Gallery.SharedBundleCheck.Check(GalleryFiles.Bundle(preset));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
    }
}
