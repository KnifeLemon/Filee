// The preset gallery's check of a shared preset.json (#1530): what Filee exports passes, what the allow-list leaves
// out fails with a code the gallery's CI can explain, and hostile files (huge, deep, odd JSON, path tricks, unknown
// fields) are refused before anything reads them as presets.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Filee.Core.Gallery;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public sealed class PresetGalleryCheckTests
{
    private static PresetCheckResult Check(byte[] json) => SharedBundleCheck.Check(json);

    private static void AssertFails(byte[] json, string code, string? path = null)
    {
        var result = Check(json);
        Assert.False(result.Ok, "expected " + code);
        Assert.Contains(result.Errors, e => e.Code == code && (path is null || e.Path == path || e.Path.StartsWith(path + "[", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_preset_as_filee_exports_it_passes_and_says_what_it_is()
    {
        var result = Check(GalleryFiles.Bundle(GalleryFiles.Photo()));

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var derived = Assert.IsType<PresetCheckDerived>(result.Derived);
        Assert.Equal(["convert"], derived.Kinds);
        Assert.Equal(["jpg"], derived.To);
        Assert.Empty(derived.From);
        Assert.Equal(SharedBundleCheck.BaselineVersion, derived.MinVersion);
        Assert.Equal(64, derived.Sha256.Length);
    }

    [Fact]
    public void Every_built_in_preset_but_copying_to_a_folder_and_metadata_can_be_shared()
    {
        // Copies of the built-in presets are what most users start from: the allow-list's ranges must take them all.
        var shareable = BuiltInData.CreatePresets()
            .Where(p => p.Kind != PresetKind.CopyToFolder && p.Tool != Preset.MetadataTool)
            .ToList();
        var actions = shareable.Where(p => !p.IsGroup).Select(p => p.Id).ToHashSet();
        foreach (var chunk in shareable.Chunk(SharedPresetRules.MaxPresets))
        {
            // A group with its members, so the references resolve.
            var members = chunk.Where(p => p.IsGroup).SelectMany(g => g.SubPresetIds)
                .Select(id => shareable.Single(p => p.Id == id)).Where(p => !chunk.Contains(p));
            var bundle = GalleryFiles.Bundle(chunk.Concat(members));
            var result = SharedBundleCheck.Check(bundle, maxPresets: 100);
            Assert.True(result.Ok, string.Join("\n", result.Errors));
        }
        Assert.NotEmpty(actions);
    }

    [Fact]
    public void Copying_to_a_folder_and_the_metadata_tool_are_not_shared()
    {
        var copy = new Preset { Id = "copy", Name = "Copy to NAS", Kind = PresetKind.CopyToFolder, TargetFormat = Preset.CopyTarget };
        AssertFails(GalleryFiles.Bundle(copy), "field.value", "presets[0].kind");
        var metadata = BuiltInData.CreatePresets().Single(p => p.Tool == Preset.MetadataTool);
        AssertFails(GalleryFiles.Bundle(metadata), "preset.tool_not_allowed");
        AssertFails(GalleryFiles.With(p => p["kind"] = "Tool", new Preset { Id = "x", Name = "X", Kind = PresetKind.Tool, Tool = "run-program" }),
            "preset.tool_not_allowed");
    }

    [Fact]
    public void A_donut_with_its_presets_passes_and_reads_its_extensions_as_formats()
    {
        var profile = new ToolbarProfile { Id = "social", Name = "Social", Extensions = ["jpg", "jpeg", "png", "heic"], PresetIds = ["insta"], BackPresetIds = [] };
        var result = Check(GalleryFiles.Bundle([GalleryFiles.Photo()], profile));

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal(["convert", "toolbar"], result.Derived!.Kinds);
        Assert.Equal(["heic", "jpg", "png"], result.Derived.From);
    }

    [Fact]
    public void The_allow_list_names_every_field_of_the_model_and_nothing_else()
    {
        // A field added to Preset or ToolbarProfile must be decided on (allowed with its range, or left out); a field
        // renamed must not leave a stale name behind.
        AssertCovers(typeof(Preset), SharedPresetRules.PresetFields);
        AssertCovers(typeof(ToolbarProfile), SharedPresetRules.ProfileFields);
        Assert.Equal(ModelPaths(typeof(PresetBundle)).Where(p => !p.StartsWith("presets[]", StringComparison.Ordinal) && !p.StartsWith("profiles[]", StringComparison.Ordinal)).Order(),
            SharedPresetRules.BundleFields.Fields.Keys.Order());
    }

    [Fact]
    public void Fields_that_carry_the_sharers_own_things_are_left_out()
    {
        var leftOut = SharedPresetRules.Paths(SharedPresetRules.PresetFields).Where(p => p.LeftOut).Select(p => p.Path).ToHashSet();
        foreach (var path in (string[])["output.customFolder", "output.keepFoldersFrom", "pdf.password", "image.backdrop",
                     "image.backdropLook.picture", "media.clip", "metadata", "translate.model", "image.hiddenAreas", "image.annotations"])
            Assert.Contains(path, leftOut);
    }

    /// <summary>The allow-list has every field of <paramref name="type"/>, but none inside a field it leaves out.</summary>
    private static void AssertCovers(Type type, ObjectRule rules)
    {
        var listed = SharedPresetRules.Paths(rules).ToList();
        var leftOut = listed.Where(p => p.LeftOut).Select(p => p.Path).ToList();
        var model = ModelPaths(type).Where(path => !leftOut.Any(l => path.StartsWith(l + ".", StringComparison.Ordinal) || path.StartsWith(l + "[].", StringComparison.Ordinal)));
        Assert.Equal(model.Order(), listed.Select(p => p.Path).Order());
    }

    private static IEnumerable<string> ModelPaths(Type type, string prefix = "")
    {
        var info = FileeJsonContext.Default.GetTypeInfo(type)!;
        // [JsonIgnore] properties are listed without a getter or setter.
        foreach (var property in info.Properties.Where(p => p.Get is not null || p.Set is not null))
        {
            var path = prefix + property.Name;
            yield return path;
            var inner = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var list = inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(List<>);
            var item = list ? inner.GetGenericArguments()[0] : inner;
            if (FileeJsonContext.Default.GetTypeInfo(item) is { Kind: JsonTypeInfoKind.Object })
                foreach (var child in ModelPaths(item, path + (list ? "[]." : ".")))
                    yield return child;
        }
    }

    [Theory]
    [InlineData("output.customFolder", "\"C:/Users/me/Pictures\"")]
    [InlineData("output.keepFoldersFrom", "\"D:/Inbox\"")]
    [InlineData("pdf.password", "\"hunter2\"")]
    [InlineData("image.backdrop", "{\"kind\":\"Color\"}")]
    [InlineData("image.hiddenAreas", "[{\"x\":0,\"y\":0,\"width\":1,\"height\":1}]")]
    [InlineData("image.annotations", "[{\"kind\":\"Text\",\"text\":\"Ann\"}]")]
    [InlineData("media.clip", "{\"music\":{\"path\":\"/home/me/song.mp3\"}}")]
    [InlineData("metadata", "{\"set\":{\"Artist\":[\"Me\"]},\"removeLocation\":false}")]
    [InlineData("translate.model", "\"llama3:70b\"")]
    public void A_left_out_field_with_a_value_fails(string path, string value)
    {
        var json = GalleryFiles.With(p => Set(p, path, JsonNode.Parse(value)));
        AssertFails(json, "field.left_out", "presets[0]." + path);
    }

    [Fact]
    public void A_left_out_picture_path_in_the_backdrop_look_fails()
    {
        var preset = BuiltInData.CreatePresets().Single(p => p.IsBackgroundTool);
        preset.Image.BackdropLook = new BackdropOptions { Kind = BackdropKind.Picture };
        var json = GalleryFiles.With(p => p["image"]!["backdropLook"]!["picture"] = "C:\\Users\\me\\bg.png", preset);
        AssertFails(json, "field.left_out", "presets[0].image.backdropLook.picture");
    }

    [Theory]
    [InlineData("output.location", "\"CustomFolder\"", "field.value")]
    [InlineData("output.conflict", "\"Overwrite\"", "field.value")]
    [InlineData("output.replaceOriginal", "true", "field.not_allowed")]
    [InlineData("output.moveInsteadOfCopy", "true", "field.not_allowed")]
    [InlineData("pdf.passwordLeftOut", "true", "field.not_allowed")]
    [InlineData("image.quality", "101", "field.range")]
    [InlineData("image.quality", "0", "field.range")]
    [InlineData("image.quality", "85.5", "field.type")]
    [InlineData("image.quality", "\"85\"", "field.type")]
    [InlineData("image.quality", "1e400", "field.type")]
    [InlineData("image.animationSpeed", "100", "field.range")]
    [InlineData("image.resize", "2", "field.type")]
    [InlineData("image.resize", "\"Huge\"", "field.value")]
    [InlineData("image.icoSizes", "[16, 17]", "field.range")]
    [InlineData("image.background", "\"red\"", "field.value")]
    [InlineData("pdf.pagesPerSheet", "5", "field.range")]
    [InlineData("pdf.pageRange", "\"1-x\"", "field.value")]
    [InlineData("pdf.ocrLanguage", "\"eng;rm -rf\"", "field.value")]
    [InlineData("media.spokenLanguage", "\"klingon\"", "field.value")]
    [InlineData("media.cropFrame", "\"crop=iw:ih\"", "field.value")]
    [InlineData("translate.targetLanguage", "\"en; drop\"", "field.value")]
    [InlineData("kind", "\"CopyToFolder\"", "field.value")]
    [InlineData("image", "null", "field.type")]
    [InlineData("image", "[]", "field.type")]
    [InlineData("subPresetIds", "\"a\"", "field.type")]
    public void A_value_out_of_its_range_fails(string path, string value, string code)
    {
        var json = GalleryFiles.With(p => Set(p, path, JsonNode.Parse(value)));
        AssertFails(json, code, "presets[0]." + path);
    }

    [Theory]
    [InlineData("runProgram")]
    [InlineData("command")]
    [InlineData("hook")]
    [InlineData("proxy")]
    [InlineData("apiKey")]
    [InlineData("endpoint")]
    [InlineData("Name")] // the app reads names as they are written: "Name" is not "name"
    public void An_unknown_field_fails(string name)
    {
        AssertFails(GalleryFiles.With(p => p[name] = "calc.exe"), "field.unknown", "presets[0]." + name);
        AssertFails(GalleryFiles.With(p => p["output"]![name] = "x"), "field.unknown", "presets[0].output." + name);
        var root = GalleryFiles.Node(GalleryFiles.Bundle(GalleryFiles.Photo()));
        root[name] = 1;
        AssertFails(GalleryFiles.Bytes(root), "field.unknown", name);
    }

    [Theory]
    [InlineData("../{name}")]
    [InlineData("..\\{name}")]
    [InlineData("out/{name}")]
    [InlineData("C:{name}")]
    [InlineData("{name}..")]
    [InlineData("{user}")]
    [InlineData("{name")]
    [InlineData("{name}?")]
    public void A_file_name_pattern_that_could_leave_its_folder_fails(string pattern)
    {
        AssertFails(GalleryFiles.With(p => p["output"]!["fileNamePattern"] = pattern), "field.value", "presets[0].output.fileNamePattern");
    }

    [Theory]
    [InlineData("{name}_{date}-{time} #{index} ({preset}).{ext}")]
    [InlineData("")]
    [InlineData("사진 {name}")]
    public void A_file_name_pattern_of_text_and_known_placeholders_passes(string pattern)
    {
        var result = Check(GalleryFiles.With(p => p["output"]!["fileNamePattern"] = pattern));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
    }

    [Theory]
    [InlineData("../up")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void A_subfolder_name_with_a_separator_fails(string name)
    {
        AssertFails(GalleryFiles.With(p =>
        {
            p["output"]!["location"] = "Subfolder";
            p["output"]!["subfolderName"] = name;
        }), "field.value", "presets[0].output.subfolderName");
    }

    [Theory]
    [InlineData("name", "Visit https://example.com", "text.url")]
    [InlineData("name", "see www.example.com", "text.url")]
    [InlineData("name", "mail me@example.com", "text.url")]
    [InlineData("pdf.watermark", "C:\\Users\\kim\\stamp", "text.personal_path")]
    [InlineData("pdf.watermark", "from /home/kim/files", "text.personal_path")]
    [InlineData("pdf.watermark", "~/Documents", "text.personal_path")]
    [InlineData("pdf.watermark", "%USERPROFILE%", "text.personal_path")]
    [InlineData("pdf.watermark", "\\\\server\\share", "text.personal_path")]
    [InlineData("name", "evil\u202Egpj.exe", "text.character")]
    [InlineData("name", "line\nbreak", "text.character")]
    [InlineData("shortName", "zero\u200Bwidth", "text.character")]
    public void A_text_with_a_link_a_personal_path_or_hidden_characters_fails(string path, string text, string code)
    {
        AssertFails(GalleryFiles.With(p => Set(p, path, JsonValue.Create(text))), code, "presets[0]." + path);
    }

    [Fact]
    public void A_link_to_filee_sh_is_allowed()
    {
        var result = Check(GalleryFiles.With(p => p["pdf"]!["watermark"] = "Made with https://filee.sh/"));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
    }

    [Fact]
    public void A_text_longer_than_its_limit_fails()
    {
        AssertFails(GalleryFiles.With(p => p["name"] = new string('a', 81)), "text.length", "presets[0].name");
    }

    [Fact]
    public void A_file_over_256_KB_is_refused_unread()
    {
        var json = Encoding.UTF8.GetBytes("{\"presets\":[],\"pad\":\"" + new string(' ', SharedPresetRules.MaxBundleBytes) + "\"}");
        var result = Check(json);
        Assert.Equal("bundle.too_large", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Deep_nesting_is_refused()
    {
        var json = Encoding.UTF8.GetBytes("{\"presets\":" + new string('[', 100) + new string(']', 100) + "}");
        Assert.Equal("bundle.too_deep", Assert.Single(Check(json).Errors).Code);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"presets\":[]")]
    [InlineData("{\"presets\":[] /* comment */}")]
    [InlineData("{\"presets\":[],}")]
    [InlineData("")]
    public void Json_the_export_never_writes_is_refused(string text)
    {
        Assert.Equal("bundle.invalid_json", Assert.Single(Check(Encoding.UTF8.GetBytes(text)).Errors).Code);
    }

    [Theory]
    [InlineData("[]", "bundle.not_object")]
    [InlineData("\"presets\"", "bundle.not_object")]
    [InlineData("{}", "bundle.no_presets")]
    [InlineData("{\"presets\":[]}", "bundle.no_presets")]
    [InlineData("{\"schemaVersion\":2,\"presets\":[]}", "field.range")]
    public void A_file_that_is_not_a_bundle_is_refused(string text, string code)
    {
        Assert.Contains(Check(Encoding.UTF8.GetBytes(text)).Errors, e => e.Code == code);
    }

    [Fact]
    public void A_bundle_from_a_newer_filee_is_refused()
    {
        var root = GalleryFiles.Node(GalleryFiles.Bundle(GalleryFiles.Photo()));
        root["settingsSchema"] = AppSettings.CurrentSchemaVersion + 1;
        AssertFails(GalleryFiles.Bytes(root), "field.range", "settingsSchema");
    }

    [Fact]
    public void A_bundle_without_settings_schema_from_an_older_filee_passes()
    {
        var root = GalleryFiles.Node(GalleryFiles.Bundle(GalleryFiles.Photo()));
        root.Remove("settingsSchema");
        Assert.True(Check(GalleryFiles.Bytes(root)).Ok);
    }

    [Fact]
    public void A_byte_order_mark_is_tolerated()
    {
        var json = GalleryFiles.Bundle(GalleryFiles.Photo());
        Assert.True(Check([0xEF, 0xBB, 0xBF, .. json]).Ok);
    }

    [Fact]
    public void A_field_written_twice_fails()
    {
        var text = Encoding.UTF8.GetString(GalleryFiles.Bundle(GalleryFiles.Photo()))
            .Replace("\"targetFormat\": \"jpg\"", "\"targetFormat\": \"jpg\", \"targetFormat\": \"exe\"", StringComparison.Ordinal);
        AssertFails(Encoding.UTF8.GetBytes(text), "field.duplicate", "presets[0].targetFormat");
    }

    [Fact]
    public void Too_many_presets_or_donuts_fail()
    {
        var many = Enumerable.Range(0, SharedPresetRules.MaxPresets + 1).Select(i => GalleryFiles.Photo("p" + i));
        AssertFails(GalleryFiles.Bundle(many), "field.range", "presets");

        var root = GalleryFiles.Node(GalleryFiles.Bundle(GalleryFiles.Photo()));
        root["profiles"] = new JsonArray(Profile("a"), Profile("b"));
        AssertFails(GalleryFiles.Bytes(root), "field.range", "profiles");

        static JsonObject Profile(string id) => new() { ["id"] = id, ["name"] = id, ["presetIds"] = new JsonArray("insta") };
    }

    [Fact]
    public void References_must_stay_inside_the_file()
    {
        var group = new Preset { Id = "turn", Name = "Turn", Kind = PresetKind.Group, TargetFormat = Preset.GroupTarget, SubPresetIds = ["insta", "elsewhere"] };
        AssertFails(GalleryFiles.Bundle(GalleryFiles.Photo(), group), "preset.reference", "presets[1].subPresetIds");

        var nested = new Preset { Id = "outer", Name = "Outer", Kind = PresetKind.Group, TargetFormat = Preset.GroupTarget, SubPresetIds = ["turn"] };
        group.SubPresetIds = ["insta"];
        AssertFails(GalleryFiles.Bundle(GalleryFiles.Photo(), group, nested), "preset.reference", "presets[2].subPresetIds");

        var profile = new ToolbarProfile { Id = "d", Name = "D", Extensions = ["jpg"], PresetIds = ["insta", "to-pdf"] };
        AssertFails(GalleryFiles.Bundle([GalleryFiles.Photo()], profile), "profile.reference");
    }

    [Fact]
    public void Ids_must_be_unique_and_presets_named()
    {
        AssertFails(GalleryFiles.Bundle(GalleryFiles.Photo("a"), GalleryFiles.Photo("a")), "preset.duplicate_id");
        AssertFails(GalleryFiles.With(p => p["name"] = ""), "preset.no_name");
        AssertFails(GalleryFiles.With(p => p["id"] = "../../x"), "field.value", "presets[0].id");
    }

    [Theory]
    [InlineData("zzz")]
    [InlineData("copy-to-folder")]
    [InlineData("group")]
    public void A_target_filee_doesnt_write_fails(string target)
    {
        AssertFails(GalleryFiles.With(p => p["targetFormat"] = target), "preset.target_unknown");
    }

    [Fact]
    public void A_donut_with_an_unknown_extension_fails()
    {
        var profile = new ToolbarProfile { Id = "d", Name = "D", Extensions = ["jpg", "notaformat"], PresetIds = ["insta"] };
        AssertFails(GalleryFiles.Bundle([GalleryFiles.Photo()], profile), "profile.extension_unknown");
    }

    [Fact]
    public void A_rename_by_regular_expression_must_be_valid()
    {
        var preset = GalleryFiles.Photo();
        preset.Output.Renames = [new RenameStep { Kind = RenameKind.Regex, Find = "(unclosed", Replace = "x" }];
        AssertFails(GalleryFiles.Bundle(preset), "output.rename_regex");

        preset.Output.Renames = [new RenameStep { Kind = RenameKind.Regex, Find = "IMG_(\\d+)", Replace = "photo_$1" }];
        Assert.True(Check(GalleryFiles.Bundle(preset)).Ok);

        preset.Output.Renames = [new RenameStep { Kind = RenameKind.Replace, Find = "IMG", Replace = "../x" }];
        AssertFails(GalleryFiles.Bundle(preset), "field.value", "presets[0].output.renames[0].replace");
    }

    [Fact]
    public void Problems_are_capped_so_a_hostile_file_cant_flood_the_output()
    {
        var root = GalleryFiles.Node(GalleryFiles.Bundle(GalleryFiles.Photo()));
        var preset = root["presets"]![0]!.AsObject();
        for (var i = 0; i < 1000; i++)
            preset["unknown" + i] = i;
        var result = Check(GalleryFiles.Bytes(root));
        Assert.Equal(PresetCheckResult.MaxErrors + 1, result.Errors.Count);
        Assert.Equal("check.too_many_errors", result.Errors[^1].Code);
    }

    /// <summary>Sets <paramref name="path"/> ("output.customFolder") in a preset's JSON.</summary>
    internal static void Set(JsonObject preset, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var node = preset;
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject next)
                node[part] = next = new JsonObject();
            node = next;
        }
        node[parts[^1]] = value;
    }
}
