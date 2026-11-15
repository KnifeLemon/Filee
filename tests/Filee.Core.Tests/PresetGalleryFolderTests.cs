// The preset gallery's check of a whole shared preset folder (#1530): preset.json, meta.json and 1 to 4 pictures of
// the right type and size, nothing else, and what meta.json says about the preset matching what it is.

using System.Text;
using System.Text.Json.Nodes;
using Filee.Core.Gallery;

namespace Filee.Core.Tests;

public sealed class PresetGalleryFolderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Folder(params (string Name, byte[] Data)[] files)
    {
        var folder = Path.Combine(_dir.Path, "instagram-jpg");
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        File.WriteAllBytes(Path.Combine(folder, "preset.json"), GalleryFiles.Bundle(GalleryFiles.Photo()));
        foreach (var (name, data) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, name))!);
            File.WriteAllBytes(Path.Combine(folder, name), data);
        }
        return folder;
    }

    private static readonly (string, byte[]) Cover = ("images/1.png", GalleryFiles.Png(1280, 720));

    private static void AssertFails(PresetCheckResult result, string code, string? file = null)
    {
        Assert.False(result.Ok, "expected " + code);
        Assert.Contains(result.Errors, e => e.Code == code && (file is null || e.File == file));
    }

    [Fact]
    public void A_folder_as_the_gallery_has_it_passes()
    {
        var folder = Folder(Cover, ("images/2.jpg", GalleryFiles.Jpeg(2560, 1440)), ("images/3.webp", GalleryFiles.WebP(640, 640)),
            ("images/4.jpeg", GalleryFiles.Jpeg(900, 1600)), ("images/1-thumb.webp", GalleryFiles.WebP(480, 270)));

        var result = SharedPresetFolderCheck.Check(folder);

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal(["jpg"], result.Derived!.To);
    }

    [Fact]
    public void The_decoder_is_asked_and_what_it_says_counts()
    {
        var folder = Folder(Cover);
        AssertFails(SharedPresetFolderCheck.Check(folder, new Decoder(new GalleryImageDecode(0, 0, 0, "bad data"))), "images.decode");
        AssertFails(SharedPresetFolderCheck.Check(folder, new Decoder(new GalleryImageDecode(1280, 720, 3, null))), "images.animated");
        AssertFails(SharedPresetFolderCheck.Check(folder, new Decoder(new GalleryImageDecode(64, 64, 1, null))), "images.decode");
        Assert.True(SharedPresetFolderCheck.Check(folder, new Decoder(new GalleryImageDecode(1280, 720, 1, null))).Ok);
    }

    private sealed class Decoder(GalleryImageDecode answer) : IGalleryImageDecoder
    {
        public GalleryImageDecode Decode(byte[] data) => answer;
    }

    [Fact]
    public void Pictures_are_required()
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder()), "images.missing");
        var folder = Folder(Cover);
        Directory.Delete(Path.Combine(folder, "images"), true);
        AssertFails(SharedPresetFolderCheck.Check(folder), "images.missing");
    }

    [Fact]
    public void At_most_four_pictures_numbered_from_one()
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/5.png", GalleryFiles.Png(800, 800)))), "images.name", "images/5.png");
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/2.png", GalleryFiles.Png(800, 800)))), "images.name");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/1.jpg", GalleryFiles.Jpeg(800, 800)))), "images.name");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/cover.png", GalleryFiles.Png(800, 800)))), "images.name");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/2-thumb.webp", GalleryFiles.WebP(400, 400)))), "images.name");
    }

    [Theory]
    [InlineData("images/1.gif")]
    [InlineData("images/1.svg")]
    [InlineData("images/1.PNG")]
    public void Other_picture_types_fail(string name)
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder((name, GalleryFiles.Png(800, 800)))), "images.name");
    }

    [Fact]
    public void A_picture_must_be_what_its_extension_says()
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/1.jpg", GalleryFiles.Png(800, 800)))), "images.format");
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/1.png", Encoding.ASCII.GetBytes("GIF89a....")))), "images.format");
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/1.png", [0x89, 0x50]))), "images.format");
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/1.webp", "RIFF\0\0\0\0WEBPVP8X"u8.ToArray()))), "images.format");
    }

    [Theory]
    [InlineData(639, 400)]
    [InlineData(2561, 1000)]
    [InlineData(400, 3000)]
    [InlineData(50000, 50000)]
    public void A_picture_outside_640_to_2560_pixels_fails(int width, int height)
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/1.png", GalleryFiles.Png(width, height)))), "images.size");
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/1.jpg", GalleryFiles.Jpeg(Math.Min(width, 65535), Math.Min(height, 65535))))), "images.size");
    }

    [Fact]
    public void Animated_pictures_fail()
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/1.png", GalleryFiles.Png(800, 800, animated: true)))), "images.animated");
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/1.webp", GalleryFiles.WebP(800, 800, animated: true)))), "images.animated");
    }

    [Fact]
    public void A_simple_webp_without_extended_header_is_read()
    {
        Assert.Equal(new GalleryImageInfo("webp", 800, 600, false), GalleryImageHeader.Read(GalleryFiles.WebP(800, 600, extended: false)));
    }

    [Fact]
    public void A_picture_over_2_MB_fails_unread()
    {
        var big = GalleryFiles.Png(1000, 1000).Concat(new byte[SharedPresetFolderCheck.MaxImageBytes]).ToArray();
        AssertFails(SharedPresetFolderCheck.Check(Folder(("images/1.png", big))), "images.too_large");
    }

    [Fact]
    public void A_thumbnail_is_at_most_480_pixels()
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/1-thumb.webp", GalleryFiles.WebP(800, 450)))), "images.size");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/1-thumb.webp", GalleryFiles.Png(480, 270)))), "images.format");
    }

    [Fact]
    public void Nothing_else_may_be_in_the_folder()
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("run.cmd", "calc"u8.ToArray()))), "folder.unexpected_file", "run.cmd");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("extra/a.txt", "x"u8.ToArray()))), "folder.unexpected_file", "extra");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/sub/1.png", GalleryFiles.Png(800, 800)))), "images.language", "images/sub/");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/ko/deeper/1.png", GalleryFiles.Png(800, 800)))), "images.unexpected_file");
    }

    [Fact]
    public void Pictures_in_the_apps_languages_pass_and_may_be_fewer()
    {
        var folder = Folder(Cover, ("images/2.png", GalleryFiles.Png(1280, 720)),
            ("images/ko/1.png", GalleryFiles.Png(1280, 720)), ("images/ko/1-thumb.webp", GalleryFiles.WebP(480, 270)),
            ("images/pt-BR/2.jpg", GalleryFiles.Jpeg(1600, 900)), ("images/zh-TW/1.webp", GalleryFiles.WebP(800, 800)));
        var result = SharedPresetFolderCheck.Check(folder);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
    }

    [Fact]
    public void A_language_set_follows_the_default_sets_rules()
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/xx/1.png", GalleryFiles.Png(800, 800)))), "images.language", "images/xx/");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/KO/1.png", GalleryFiles.Png(800, 800)))), "images.language");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/ko/2.png", GalleryFiles.Png(800, 800)))), "images.name", "images/ko/");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/ko/1.png", GalleryFiles.Png(300, 300)))), "images.size", "images/ko/1.png");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/ko/1.gif", GalleryFiles.Png(800, 800)))), "images.name");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/ko/1.png", GalleryFiles.Png(800, 800, animated: true)))), "images.animated");
        var empty = Folder(Cover);
        Directory.CreateDirectory(Path.Combine(empty, "images", "ja"));
        AssertFails(SharedPresetFolderCheck.Check(empty), "images.missing", "images/ja/");
    }

    [Fact]
    public void A_folder_over_40_MB_fails()
    {
        var folder = Folder(Cover);
        foreach (var language in SharedPresetFolderCheck.Languages.Take(22))
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "images", language)).FullName, "1.png"),
                [.. GalleryFiles.Png(1280, 720), .. new byte[SharedPresetFolderCheck.MaxImageBytes - 200]]);
        var result = SharedPresetFolderCheck.Check(folder);
        Assert.Contains(result.Errors, e => e.Code == "folder.too_large");
    }

    [Fact]
    public void A_missing_folder_or_preset_json_fails()
    {
        AssertFails(SharedPresetFolderCheck.Check(Path.Combine(_dir.Path, "nope")), "folder.missing");
        var folder = Folder(Cover);
        File.Delete(Path.Combine(folder, "preset.json"));
        AssertFails(SharedPresetFolderCheck.Check(folder), "folder.no_preset");
    }

    [Fact]
    public void Problems_in_preset_json_are_reported_against_it()
    {
        var folder = Folder(Cover);
        File.WriteAllBytes(Path.Combine(folder, "preset.json"), GalleryFiles.With(p => p["output"]!["customFolder"] = "C:\\Out"));
        AssertFails(SharedPresetFolderCheck.Check(folder), "field.left_out", "preset.json");
    }

    [Fact]
    public void A_link_on_disk_is_refused()
    {
        var folder = Folder(Cover);
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, "images", "2.png"), Path.Combine(folder, "images", "1.png"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links need developer mode or elevation here.");
        }
        AssertFails(SharedPresetFolderCheck.Check(folder), "folder.link");
    }

    private static JsonObject Meta() => new()
    {
        ["id"] = "instagram-jpg",
        ["title"] = "Instagram-ready JPG",
        ["description"] = "Resizes photos to 1080 px for posting.",
        ["author"] = "some-user",
        ["tags"] = new JsonArray("images", "social"),
        ["languages"] = new JsonArray("en"),
        ["kinds"] = new JsonArray("convert"),
        ["formats"] = new JsonObject { ["from"] = new JsonArray(), ["to"] = new JsonArray("jpg") },
        ["minVersion"] = "2.5.0",
        ["created"] = "2026-11-15T00:00:00Z",
        ["updated"] = "2026-11-15T00:00:00Z",
        ["discussion"] = 12,
        ["license"] = "CC0-1.0",
        ["warnings"] = new JsonArray(new JsonObject { ["code"] = "strips_metadata", ["severity"] = "info" }),
    };

    [Fact]
    public void A_meta_json_as_the_ci_writes_it_passes()
    {
        var result = SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", GalleryFiles.Bytes(Meta()))));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
    }

    [Theory]
    [InlineData("title", "\"Hi\"", "field.value")]
    [InlineData("description", "\"Short\"", "field.value")]
    [InlineData("description", "\"Get it at https://evil.example now\"", "text.url")]
    [InlineData("license", "\"MIT\"", "field.value")]
    [InlineData("tags", "[\"Images\"]", "field.value")]
    [InlineData("tags", "[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\"]", "field.range")]
    [InlineData("id", "\"Bad Slug\"", "field.value")]
    [InlineData("id", "\"files\"", "field.value")]
    [InlineData("id", "\"download\"", "field.value")]
    [InlineData("author", "\"-bad-\"", "field.value")]
    [InlineData("minVersion", "\"latest\"", "field.value")]
    [InlineData("created", "\"yesterday\"", "field.value")]
    [InlineData("homepage", "\"x\"", "field.unknown")]
    [InlineData("kinds", "[\"toolbar\"]", "meta.derived_mismatch")]
    [InlineData("formats", "{\"from\":[],\"to\":[\"png\"]}", "meta.derived_mismatch")]
    [InlineData("minVersion", "\"2.0.0\"", "meta.derived_mismatch")]
    [InlineData("warnings", "[{\"code\":\"replaces_original\",\"severity\":\"high\"}]", "meta.derived_mismatch")]
    [InlineData("warnings", "[{\"code\":\"made_up\",\"severity\":\"high\"}]", "field.value")]
    public void A_wrong_meta_json_fails(string field, string value, string code)
    {
        var meta = Meta();
        meta[field] = JsonNode.Parse(value);
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", GalleryFiles.Bytes(meta)))), code, "meta.json");
    }

    [Fact]
    public void A_meta_json_with_its_languages_passes_and_says_which_they_are()
    {
        var meta = Meta();
        meta.Remove("languages");
        meta["language"] = "ko";
        meta["titleByLang"] = new JsonObject { ["en"] = "Instagram-ready JPG", ["ja"] = "インスタ用 JPG" };
        meta["descriptionByLang"] = new JsonObject { ["en"] = "Resizes photos to 1080 px for posting." };
        var result = SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", GalleryFiles.Bytes(meta))));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal(["ko", "en", "ja"], result.Derived!.Languages);

        meta["languages"] = new JsonArray("ko", "en", "ja");
        Assert.True(SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", GalleryFiles.Bytes(meta)))).Ok);
    }

    [Fact]
    public void An_old_meta_json_with_languages_only_takes_the_first()
    {
        var result = SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", GalleryFiles.Bytes(Meta()))));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal(["en"], result.Derived!.Languages);
    }

    [Theory]
    [InlineData("{\"language\":\"xx\"}", "field.value", "language")]
    [InlineData("{\"language\":\"en\",\"titleByLang\":{\"xx\":\"Title\"}}", "field.value", "titleByLang.xx")]
    [InlineData("{\"language\":\"en\",\"titleByLang\":{\"ko\":\"Hi\"}}", "field.value", "titleByLang.ko")]
    [InlineData("{\"language\":\"en\",\"titleByLang\":{\"ko\":\"see https://evil.example\"}}", "text.url", "titleByLang.ko")]
    [InlineData("{\"language\":\"en\",\"titleByLang\":{\"en\":\"Main again\"}}", "field.value", "titleByLang.en")]
    [InlineData("{\"language\":\"en\",\"descriptionByLang\":{\"ko\":\"A description without a title.\"}}", "field.value", "descriptionByLang.ko")]
    [InlineData("{\"language\":\"en\",\"languages\":[\"en\",\"ko\"]}", "meta.derived_mismatch", "languages")]
    [InlineData("{\"languages\":[]}", "meta.language_missing", "language")]
    public void Wrong_languages_in_meta_json_fail(string patch, string code, string path)
    {
        var meta = Meta();
        meta.Remove("languages");
        foreach (var (key, value) in JsonNode.Parse(patch)!.AsObject().ToList())
            meta[key] = value?.DeepClone();
        var result = SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", GalleryFiles.Bytes(meta))));
        Assert.Contains(result.Errors, e => e.Code == code && e.File == "meta.json" && e.Path == path);
    }

    [Fact]
    public void A_broken_meta_json_fails()
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", "{"u8.ToArray()))), "meta.invalid_json");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", new byte[SharedPresetFolderCheck.MaxMetaBytes + 1]))), "meta.too_large");
    }
}
