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
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("images/sub/1.png", GalleryFiles.Png(800, 800)))), "images.unexpected_file");
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
    public void A_wrong_meta_json_fails(string field, string value, string code)
    {
        var meta = Meta();
        meta[field] = JsonNode.Parse(value);
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", GalleryFiles.Bytes(meta)))), code, "meta.json");
    }

    [Fact]
    public void A_broken_meta_json_fails()
    {
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", "{"u8.ToArray()))), "meta.invalid_json");
        AssertFails(SharedPresetFolderCheck.Check(Folder(Cover, ("meta.json", new byte[SharedPresetFolderCheck.MaxMetaBytes + 1]))), "meta.too_large");
    }
}
