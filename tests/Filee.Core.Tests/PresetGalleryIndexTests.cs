// index.json of the preset gallery (#1530) is read leniently but safely: entries as the contract has them come
// through, entries that name files outside their own folder, or lack what an install needs, are left out.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Filee.Core.Gallery;

namespace Filee.Core.Tests;

public sealed class PresetGalleryIndexTests
{
    internal static JsonObject Entry(string id = "instagram-jpg") => new()
    {
        ["id"] = id,
        ["title"] = "Instagram-ready JPG",
        ["description"] = "Resizes photos.",
        ["author"] = "some-user",
        ["tags"] = new JsonArray("images", "social"),
        ["languages"] = new JsonArray("en", "ko"),
        ["kinds"] = new JsonArray("convert", "hack"),
        ["formats"] = new JsonObject { ["from"] = new JsonArray("jpg", "png"), ["to"] = new JsonArray("jpg") },
        ["minVersion"] = "2.5.0",
        ["created"] = "2026-11-15T00:00:00Z",
        ["updated"] = "2026-11-16T00:00:00Z",
        ["discussion"] = 12,
        ["license"] = "CC0-1.0",
        ["images"] = new JsonArray($"presets/{id}/images/1.webp", $"presets/{id}/images/2.webp"),
        ["thumb"] = $"presets/{id}/images/1-thumb.webp",
        ["preset"] = $"presets/{id}/preset.json",
        ["sha256"] = new string('a', 64),
        ["stats"] = new JsonObject
        {
            ["likes"] = 3,
            ["comments"] = 1,
            ["downloads"] = 40,
            ["hot"] = 2.5,
            ["best"] = new JsonObject { ["day"] = 1, ["week"] = 2, ["month"] = 3, ["year"] = 4, ["all"] = 5 },
        },
    };

    private static GalleryIndex Parse(params JsonObject[] entries) =>
        GalleryIndex.Parse(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["version"] = 1,
            ["generated"] = "2026-11-15T00:00:00Z",
            ["presets"] = new JsonArray([.. entries.Select(e => e.DeepClone())]),
        }.ToJsonString()));

    [Fact]
    public void An_entry_as_the_contract_has_it_is_read()
    {
        var index = Parse(Entry());
        var entry = Assert.Single(index.Entries);

        Assert.Equal(1, index.Version);
        Assert.Equal("instagram-jpg", entry.Id);
        Assert.Equal(["convert"], entry.Kinds); // an unknown kind is dropped
        Assert.Equal(["jpg", "png"], entry.FormatsFrom);
        Assert.Equal(new Version(2, 5, 0), entry.MinVersion);
        Assert.Equal(12, entry.Discussion);
        Assert.Equal(2, entry.Images.Count);
        Assert.Equal("presets/instagram-jpg/images/1-thumb.webp", entry.Thumb);
        Assert.True(entry.HasStats);
        Assert.Equal(40, entry.Stats.Downloads);
        Assert.Equal(4, entry.Stats.BestOf("year"));
    }

    [Fact]
    public void Paths_relative_to_the_sites_index_are_read_with_the_install_link()
    {
        // filee.sh serves its index at /presets/index.json, the files under files/ next to it.
        var entry = Entry();
        entry["images"] = new JsonArray("files/presets/instagram-jpg/images/1.webp");
        entry["thumb"] = "files/presets/instagram-jpg/images/1-thumb.webp";
        entry["preset"] = "files/presets/instagram-jpg/preset.json";
        entry["download"] = "https://filee.sh/presets/download/instagram-jpg";
        var read = Assert.Single(Parse(entry).Entries);
        Assert.Equal(["files/presets/instagram-jpg/images/1.webp"], read.Images);
        Assert.Equal("files/presets/instagram-jpg/images/1-thumb.webp", read.Thumb);
        Assert.Equal("https://filee.sh/presets/download/instagram-jpg", read.Download);
        Assert.Equal(new Uri("https://filee.sh/presets/files/presets/instagram-jpg/preset.json"),
            new Uri(new Uri("https://filee.sh/presets/index.json"), read.Preset));

        entry["download"] = "download/instagram-jpg";
        Assert.Equal("download/instagram-jpg", Assert.Single(Parse(entry).Entries).Download);
    }

    [Theory]
    [InlineData("https://evil.example/presets/download/instagram-jpg")]
    [InlineData("https://filee.sh/presets/download/other")]
    [InlineData("download/other")]
    [InlineData("../download/instagram-jpg")]
    public void An_install_link_elsewhere_is_dropped(string link)
    {
        var entry = Entry();
        entry["download"] = link;
        Assert.Null(Assert.Single(Parse(entry).Entries).Download);
    }

    [Fact]
    public void Pictures_per_language_are_read_and_picked_with_the_base_language_and_the_default_as_fallbacks()
    {
        var entry = Entry();
        entry["imagesByLang"] = new JsonObject
        {
            ["ko"] = new JsonArray("presets/instagram-jpg/images/ko/1.webp", "presets/instagram-jpg/images/2.webp"),
            ["pt"] = new JsonArray("presets/instagram-jpg/images/pt-BR/1.webp"),
            ["xx"] = new JsonArray("presets/instagram-jpg/images/xx/1.webp"),
            ["ja"] = new JsonArray("presets/instagram-jpg/images/xx/1.webp", "https://evil.example/1.webp"),
        };
        entry["thumbByLang"] = new JsonObject
        {
            ["ko"] = "presets/instagram-jpg/images/ko/1-thumb.webp",
            ["de"] = "presets/other/images/de/1-thumb.webp",
        };
        var read = Assert.Single(Parse(entry).Entries);

        Assert.Equal(["ko", "pt"], read.ImagesByLang.Keys.Order());
        Assert.Equal(["presets/instagram-jpg/images/ko/1.webp", "presets/instagram-jpg/images/2.webp"], read.ImagesFor("ko"));
        Assert.Equal("presets/instagram-jpg/images/ko/1-thumb.webp", read.ThumbFor("ko"));
        Assert.Equal(["presets/instagram-jpg/images/pt-BR/1.webp"], read.ImagesFor("pt-PT")); // the base language's set
        Assert.Equal(read.Images, read.ImagesFor("de"));
        Assert.Equal(read.Thumb, read.ThumbFor("de"));
        Assert.True(read.HasPicture("presets/instagram-jpg/images/ko/1-thumb.webp"));
        Assert.False(read.HasPicture("presets/instagram-jpg/images/xx/1.webp"));
    }

    [Fact]
    public void Titles_and_descriptions_per_language_say_which_languages_a_preset_is_written_in()
    {
        var entry = Entry();
        entry["language"] = "ko";
        entry["title"] = "인스타그램용 JPG";
        entry["titleByLang"] = new JsonObject { ["en"] = "Instagram-ready JPG", ["pt-BR"] = "JPG para Instagram", ["xx"] = "?" };
        entry["descriptionByLang"] = new JsonObject { ["pt-BR"] = "Fotos menores." };
        entry["languages"] = new JsonArray("ko", "en");
        var read = Assert.Single(Parse(entry).Entries);

        Assert.Equal("ko", read.Language);
        Assert.Equal(["ko", "en", "pt-BR"], read.Languages);
        Assert.Equal("인스타그램용 JPG", read.TitleIn("ko"));
        Assert.Equal("Instagram-ready JPG", read.TitleIn("en"));
        Assert.Equal("Resizes photos.", read.DescriptionIn("en")); // no English description: the main one
        Assert.Equal("JPG para Instagram", read.TitleIn("pt-BR"));
        Assert.Equal("Fotos menores.", read.DescriptionIn("pt-BR"));
        Assert.False(read.Supports("pt-PT")); // no "pt" set to fall back to
        Assert.False(read.Supports("ja"));
        Assert.Null(read.DescriptionIn("ja"));
        Assert.DoesNotContain("xx", read.TitleByLang.Keys);
    }

    [Fact]
    public void An_index_from_before_language_takes_the_first_of_languages()
    {
        var read = Assert.Single(Parse(Entry()).Entries); // "languages": ["en", "ko"], no "language"
        Assert.Equal("en", read.Language);
        Assert.True(read.Supports("en"));
        Assert.False(read.Supports("ko")); // listed, but without a title in Korean
    }

    [Fact]
    public void Stats_are_optional()
    {
        var bare = Entry();
        bare.Remove("stats");
        var entry = Assert.Single(Parse(bare).Entries);
        Assert.False(entry.HasStats);
        Assert.Equal(0, entry.Stats.Likes);
    }

    [Theory]
    [InlineData("id", "\"../etc\"")]
    [InlineData("id", "\"UPPER\"")]
    [InlineData("id", "\"files\"")]
    [InlineData("id", "\"download\"")]
    [InlineData("id", "\"index\"")]
    [InlineData("preset", "\"/presets/instagram-jpg/preset.json\"")]
    [InlineData("preset", "\"../presets/instagram-jpg/preset.json\"")]
    [InlineData("preset", "\"//evil.example/presets/instagram-jpg/preset.json\"")]
    [InlineData("preset", "\"presets/other/preset.json\"")]
    [InlineData("preset", "\"https://evil.example/preset.json\"")]
    [InlineData("preset", "\"presets/instagram-jpg/../../x/preset.json\"")]
    [InlineData("sha256", "\"abc\"")]
    [InlineData("title", "\"   \"")]
    public void An_entry_that_cant_be_installed_safely_is_left_out(string field, string value)
    {
        var bad = Entry();
        bad[field] = JsonNode.Parse(value);
        Assert.Single(Parse(bad, Entry("other-preset")).Entries, e => e.Id == "other-preset");
    }

    [Fact]
    public void Pictures_outside_the_presets_folder_are_dropped()
    {
        var entry = Entry();
        entry["images"] = new JsonArray("presets/instagram-jpg/images/1.webp", "https://evil.example/a.webp",
            "presets/instagram-jpg/images/../../../x.webp", "presets/other/images/1.webp", "presets/instagram-jpg/images/9.webp");
        entry["thumb"] = "//evil.example/t.webp";
        var read = Assert.Single(Parse(entry).Entries);
        Assert.Equal(["presets/instagram-jpg/images/1.webp"], read.Images);
        Assert.Null(read.Thumb);
    }

    [Fact]
    public void Texts_are_cut_and_cleaned()
    {
        var entry = Entry();
        entry["title"] = "A‮b" + new string('x', 100);
        entry["tags"] = new JsonArray(new string('t', 30), "ok", "ok");
        var read = Assert.Single(Parse(entry).Entries);
        Assert.Equal(60, read.Title.Length);
        Assert.DoesNotContain('‮', read.Title);
        Assert.Equal(["ok"], read.Tags);
    }

    [Fact]
    public void A_second_entry_with_the_same_id_is_left_out()
    {
        var second = Entry();
        second["title"] = "Second";
        Assert.Equal("Instagram-ready JPG", Assert.Single(Parse(Entry(), second).Entries).Title);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"presets\":{}}")]
    [InlineData("nope")]
    public void A_file_that_is_no_index_throws(string text)
    {
        Assert.ThrowsAny<JsonException>(() => GalleryIndex.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Hostile_numbers_and_types_dont_break_reading()
    {
        var entry = Entry();
        entry["stats"] = new JsonObject { ["likes"] = -5, ["downloads"] = 1e300, ["hot"] = "high", ["best"] = "x" };
        entry["discussion"] = -1;
        entry["minVersion"] = "x";
        entry["tags"] = new JsonArray(1, true, null, "fine");
        var read = Assert.Single(Parse(entry).Entries);
        Assert.Equal(0, read.Stats.Likes);
        Assert.Equal(int.MaxValue, read.Stats.Downloads);
        Assert.Null(read.Discussion);
        Assert.Equal(new Version(0, 0, 0), read.MinVersion);
        Assert.Equal(["fine"], read.Tags);
    }
}
