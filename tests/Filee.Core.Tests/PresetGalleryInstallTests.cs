// Installing from the preset gallery (#1530) only adds: new ids, " (2)" on names the library has, the user's presets
// and donuts untouched, and nothing at all when the file isn't the one the gallery lists or fails its rules.

using System.Security.Cryptography;
using Filee.Core.Gallery;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public sealed class PresetGalleryInstallTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly UserDataStore _store;

    public PresetGalleryInstallTests()
    {
        _store = new UserDataStore(_dir.Path);
        _store.Load();
    }

    public void Dispose() => _dir.Dispose();

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string NameOf(Preset p) => p.Name.Length > 0 ? p.Name : p.NameKey ?? p.TargetFormat;

    private GalleryInstallResult Install(byte[] json, string? sha = null) =>
        GalleryInstaller.Install(_store, json, sha ?? Sha(json), NameOf, p => p.Name.Length > 0 ? p.Name : p.NameKey ?? p.Id);

    [Fact]
    public void Presets_come_in_as_new_ones_and_the_library_is_saved()
    {
        var before = _store.Presets.Count;
        var result = Install(GalleryFiles.Bundle(GalleryFiles.Photo()));

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var added = Assert.Single(result.Presets);
        Assert.NotEqual("insta", added.Id);
        Assert.Equal("Instagram JPG", added.Name);
        Assert.Equal(before + 1, _store.Presets.Count);
        var reloaded = new UserDataStore(_dir.Path);
        reloaded.Load();
        Assert.NotNull(reloaded.FindPreset(added.Id));
    }

    [Fact]
    public void A_preset_with_the_id_of_one_of_ours_never_replaces_it()
    {
        var ours = _store.FindPreset("to-jpg")!;
        var quality = ours.Image.Quality;
        var shared = ours.Clone();
        shared.Image.Quality = 10;

        var result = Install(GalleryFiles.Bundle(shared));

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Same(ours, _store.FindPreset("to-jpg"));
        Assert.Equal(quality, ours.Image.Quality);
        Assert.Equal(10, _store.FindPreset(result.Presets[0].Id)!.Image.Quality);
        // Same built-in name: the new one is told apart.
        Assert.Equal(NameOf(ours) + " (2)", result.Presets[0].Name);
    }

    [Fact]
    public void A_name_the_library_has_gets_a_number()
    {
        Install(GalleryFiles.Bundle(GalleryFiles.Photo()));
        var second = Install(GalleryFiles.Bundle(GalleryFiles.Photo()));
        var third = Install(GalleryFiles.Bundle(GalleryFiles.Photo()));

        Assert.Equal("Instagram JPG (2)", second.Presets[0].Name);
        Assert.Equal("Instagram JPG (3)", third.Presets[0].Name);
    }

    [Fact]
    public void Groups_and_the_donut_follow_the_new_ids()
    {
        var left = new Preset { Id = "left", Name = "Left", TargetFormat = "*", Image = { Rotation = ImageRotation.Left90 } };
        var group = new Preset { Id = "turn", Name = "Turn", Kind = PresetKind.Group, TargetFormat = Preset.GroupTarget, SubPresetIds = ["left"] };
        var profile = new ToolbarProfile { Id = "images", Name = "My images", Extensions = ["jpg", "png"], PresetIds = ["turn", "left"], BackPresetIds = ["left"] };
        var profiles = _store.Profiles.Count;

        var result = Install(GalleryFiles.Bundle([left, group], profile));

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var newLeft = result.Presets.Single(p => p.Name == "Left");
        var newGroup = result.Presets.Single(p => p.Name == "Turn");
        Assert.Equal([newLeft.Id], newGroup.SubPresetIds);
        var donut = Assert.IsType<ToolbarProfile>(result.Profile);
        Assert.NotEqual("images", donut.Id);
        Assert.Equal([newGroup.Id, newLeft.Id], donut.PresetIds);
        Assert.Equal([newLeft.Id], donut.BackPresetIds);
        Assert.Equal(profiles + 1, _store.Profiles.Count);
        Assert.Same(donut, _store.Profiles[^1]);
        // The built-in images donut still opens for JPG: the new one comes after it.
        Assert.Equal("images", ProfileSelector.Select(_store.Profiles, ["a.jpg"])!.Id);
        // And the built-in donut keeps its slices.
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "images").PresetIds, _store.Profiles.Single(p => p.Id == "images").PresetIds);
    }

    [Fact]
    public void A_donut_name_the_library_has_gets_a_number()
    {
        _store.Profiles.Add(new ToolbarProfile { Id = "mine", Name = "Social", Extensions = ["heic"] });
        var profile = new ToolbarProfile { Id = "s", Name = "Social", Extensions = ["jpg"], PresetIds = ["insta"] };
        var result = Install(GalleryFiles.Bundle([GalleryFiles.Photo()], profile));
        Assert.Equal("Social (2)", result.Profile!.Name);
    }

    [Fact]
    public void A_file_other_than_the_listed_one_installs_nothing()
    {
        var json = GalleryFiles.Bundle(GalleryFiles.Photo());
        var before = _store.Presets.Count;
        var result = Install(json, new string('0', 64));

        Assert.False(result.Ok);
        Assert.Equal("install.sha256", Assert.Single(result.Errors).Code);
        Assert.Equal(before, _store.Presets.Count);
    }

    [Fact]
    public void A_file_the_rules_refuse_installs_nothing_even_with_the_right_hash()
    {
        // The server could list anything: the app checks the file itself.
        var json = GalleryFiles.With(p => p["output"]!["customFolder"] = "C:\\Users\\Public");
        var before = _store.Presets.Count;
        var result = Install(json);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Code == "field.left_out");
        Assert.Equal(before, _store.Presets.Count);
    }

    [Fact]
    public void A_bundle_from_before_back_faces_gets_none_rather_than_null()
    {
        var root = GalleryFiles.Node(GalleryFiles.Bundle([GalleryFiles.Photo()],
            new ToolbarProfile { Id = "x", Name = "X", Extensions = ["jpg"], PresetIds = ["insta"] }));
        root["profiles"]![0]!.AsObject().Remove("backPresetIds");
        root.Remove("settingsSchema");
        var result = Install(GalleryFiles.Bytes(root));

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal([], result.Profile!.BackPresetIds);
    }

    [Fact]
    public void Export_writes_the_settings_schema_into_the_bundle()
    {
        var path = Path.Combine(_dir.Path, "bundle.json");
        _store.Export(path);
        Assert.Contains($"\"settingsSchema\": {AppSettings.CurrentSchemaVersion}", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Installed_presets_keep_their_settings_and_say_where_they_came_from()
    {
        var shared = GalleryFiles.Photo();
        shared.Output.ReplaceOriginal = true;
        shared.Output.Conflict = ConflictPolicy.Overwrite;
        var json = GalleryFiles.Bundle(shared);
        var result = GalleryInstaller.Install(_store, json, Sha(json), NameOf, p => p.Name, gallery: "instagram-jpg");

        var added = Assert.Single(result.Presets);
        Assert.Equal("instagram-jpg", added.Gallery);
        Assert.True(added.Output.ReplaceOriginal);
        Assert.Equal(ConflictPolicy.Overwrite, added.Output.Conflict);
        // Shared again, it no longer says so (the gallery check leaves the field out).
        Assert.Null(added.ForSharing().Gallery);
        Assert.Equal("instagram-jpg", added.Gallery);
    }

    [Fact]
    public void Export_for_the_gallery_takes_the_chosen_presets_with_what_their_groups_and_donut_need()
    {
        var turn = _store.Presets.First(p => p.IsGroup);
        var donut = new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["png"], PresetIds = ["to-jpg"], BackPresetIds = [] };
        var file = GalleryExport.Build(_store.Presets, [turn], donut);

        Assert.True(file.Check.Ok, string.Join("\n", file.Check.Errors));
        Assert.Equal(turn.SubPresetIds.Append(turn.Id).Append("to-jpg").ToHashSet(), file.Presets.Select(p => p.Id).ToHashSet());
        Assert.Contains("toolbar", file.Check.Derived!.Kinds);
        Assert.Null(file.Check.Bundle!.Language);
        var korean = GalleryExport.Build(_store.Presets, [turn], null, "ko");
        Assert.True(korean.Check.Ok, string.Join("\n", korean.Check.Errors));
        Assert.Equal("ko", korean.Check.Bundle!.Language);
        Assert.Null(GalleryExport.Build(_store.Presets, [turn], null, "xx").Check.Bundle!.Language);

        var own = _store.FindPreset("to-png")!.Clone();
        own.Output.Location = OutputLocation.CustomFolder;
        own.Output.CustomFolder = Path.Combine(_dir.Path, "out");
        own.Pdf.Password = "secret";
        var refused = GalleryExport.Build([own], [own], null);
        Assert.Contains(refused.Check.Errors, e => e.Path == "presets[0].output.customFolder");
        Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(refused.Json), StringComparison.Ordinal);
    }

    [Fact]
    public void Unique_names_count_up()
    {
        var taken = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { "A", "a (2)" };
        Assert.Equal("A (3)", GalleryInstaller.Unique("A", taken));
        Assert.Equal("B", GalleryInstaller.Unique("B", taken));
    }
}
