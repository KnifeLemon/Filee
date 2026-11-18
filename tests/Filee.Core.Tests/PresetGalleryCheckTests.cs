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
    public void Every_built_in_preset_can_be_shared()
    {
        // Copies of the built-in presets are what most users start from: the allow-list's ranges must take them all.
        var shareable = BuiltInData.CreatePresets();
        foreach (var chunk in shareable.Chunk(SharedPresetRules.MaxPresets))
        {
            // A group with its members, so the references resolve.
            var members = chunk.Where(p => p.IsGroup).SelectMany(g => g.SubPresetIds)
                .Select(id => shareable.Single(p => p.Id == id)).Where(p => !chunk.Contains(p));
            var bundle = GalleryFiles.Bundle(chunk.Concat(members));
            var result = SharedBundleCheck.Check(bundle, maxPresets: 100);
            Assert.True(result.Ok, string.Join("\n", result.Errors));
        }
    }

    [Fact]
    public void Copying_to_a_folder_and_the_metadata_tool_are_shared_with_their_warnings()
    {
        var copy = new Preset
        {
            Id = "copy",
            Name = "Move to NAS",
            Kind = PresetKind.CopyToFolder,
            TargetFormat = Preset.CopyTarget,
            Output = { Location = OutputLocation.CustomFolder, MoveInsteadOfCopy = true },
        };
        var result = Check(GalleryFiles.Bundle(copy));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal(["tool"], result.Derived!.Kinds);
        Assert.Equal(["moves_original", "copies_to_folder"], result.Derived.Warnings.Select(w => w.Code));
        Assert.Equal("high", result.Derived.Warnings[0].Severity);
        Assert.Equal(["copy"], result.Derived.Warnings[0].Presets);

        var metadata = BuiltInData.CreatePresets().Single(p => p.Tool == Preset.MetadataTool);
        Assert.True(Check(GalleryFiles.Bundle(metadata)).Ok);
        // A tool this version doesn't know could be anything, a program it runs included.
        AssertFails(GalleryFiles.With(p => p["kind"] = "Tool", new Preset { Id = "x", Name = "X", Kind = PresetKind.Tool, Tool = "run-program" }),
            "preset.tool_not_allowed");
        AssertFails(GalleryFiles.With(p => p["targetFormat"] = "jpg", copy), "preset.target_unknown");
    }

    private static IEnumerable<string> WarningCodes(Preset preset) =>
        Check(GalleryFiles.Bundle(preset)).Derived!.Warnings.Select(w => w.Code);

    private static Preset Kept(Action<Preset>? change = null)
    {
        var preset = GalleryFiles.Photo();
        preset.Image.KeepMetadata = true;
        change?.Invoke(preset);
        return preset;
    }

    [Fact]
    public void Each_risky_setting_gives_its_warning()
    {
        Assert.Empty(WarningCodes(Kept()));
        Assert.Equal(["strips_metadata"], WarningCodes(GalleryFiles.Photo()));
        Assert.Equal(["replaces_original"], WarningCodes(Kept(p => p.Output.ReplaceOriginal = true)));
        // An export leaves "Move the original to the Recycle Bin / Trash" out (Preset.ForSharing), so only a preset.json
        // written by hand carries it.
        Assert.Equal(["trashes_original"], Check(GalleryFiles.With(p => p["output"]!["trashOriginalAfterConvert"] = true, Kept()))
            .Derived!.Warnings.Select(w => w.Code));
        Assert.Equal(["overwrites_existing"], WarningCodes(Kept(p => p.Output.Conflict = ConflictPolicy.Overwrite)));
        Assert.Equal(["edits_metadata", "strips_metadata"], WarningCodes(Kept(p => p.Metadata = new MetadataEdits
        {
            Set = new() { ["XMP-dc:Title"] = ["Holiday"] },
            RemoveLocation = true,
        })));
        Assert.Equal(["fixed_edits"], WarningCodes(Kept(p => p.Image.HiddenAreas = [new HiddenArea { Width = 0.5, Height = 0.5 }])));
        Assert.Equal(["uses_translate_model"], WarningCodes(new Preset
        {
            Id = "tr",
            Name = "To English",
            TargetFormat = "docx",
            Translate = { Enabled = true, Model = "llama3.1:8b" },
        }));
        Assert.Equal(["high", "high", "high", "medium", "medium", "medium", "medium", "info", "info"], SharedPresetWarnings.All.Select(w => w.Severity));
    }

    [Theory]
    [InlineData("XMP-dc:Creator")]
    [InlineData("EXIF:Artist")]
    [InlineData("XMP-dc:Rights")]
    [InlineData("IPTC:By-line")]
    [InlineData("GPS:GPSLatitude")]
    [InlineData("XMP-photoshop:City")]
    [InlineData("EXIF:OwnerName")]
    public void Metadata_naming_people_places_or_owners_may_only_be_removed(string field)
    {
        AssertFails(GalleryFiles.With(p => p["metadata"] = JsonNode.Parse("{\"set\":{\"" + field + "\":[\"Kim\"]}}")),
            "field.left_out", $"presets[0].metadata.set.{field}");
        var removed = Check(GalleryFiles.With(p => p["metadata"] = JsonNode.Parse("{\"set\":{\"" + field + "\":[]},\"remove\":[\"" + field + "\"]}")));
        Assert.True(removed.Ok, string.Join("\n", removed.Errors));
    }

    [Theory]
    [InlineData("XMP-dc:Title")]
    [InlineData("EXIF:DocumentName")]
    [InlineData("XMP-dc:Description")]
    [InlineData("PDF:Subject")]
    [InlineData("XMP-dc:Subject")]
    public void Fields_that_name_a_document_not_a_person_may_be_written(string field)
    {
        Assert.False(SharedText.IsPersonalField(field));
        var result = Check(GalleryFiles.With(p => p["metadata"] = JsonNode.Parse("{\"set\":{\"" + field + "\":[\"Quarterly report\"]}}")));
        Assert.True(result.Ok, string.Join('\n', result.Errors));
    }

    [Fact]
    public void Other_metadata_may_be_written_and_bad_field_names_fail()
    {
        Assert.True(Check(GalleryFiles.With(p => p["metadata"] = JsonNode.Parse("{\"set\":{\"XMP-dc:Title\":[\"Scan\"],\"EXIF:Software\":[\"Filee\"]}}"))).Ok);
        AssertFails(GalleryFiles.With(p => p["metadata"] = JsonNode.Parse("{\"set\":{\"-all=\":[\"x\"]}}")), "field.value");
        AssertFails(GalleryFiles.With(p => p["metadata"] = JsonNode.Parse("{\"set\":{\"XMP-dc:Title\":[\"see https://evil.example\"]}}")), "text.url");
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
        Assert.Equal(["gallery", "image.backdrop.picture", "image.backdrop.pictureLength", "image.backdrop.pictureWritten",
            "image.backdropLook.picture", "image.backdropLook.pictureLength", "image.backdropLook.pictureWritten",
            "media.clip.music.path", "output.customFolder", "output.keepFoldersFrom", "pdf.password"], leftOut.Order());
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
    [InlineData("output.customFolder", "\"C:/Users/me/Pictures\"", "output.customFolder")]
    [InlineData("output.keepFoldersFrom", "\"D:/Inbox\"", "output.keepFoldersFrom")]
    [InlineData("pdf.password", "\"hunter2\"", "pdf.password")]
    [InlineData("image.backdrop", "{\"kind\":\"Picture\",\"picture\":\"C:/bg.png\"}", "image.backdrop.picture")]
    [InlineData("media.clip", "{\"music\":{\"path\":\"/home/me/song.mp3\"}}", "media.clip.music.path")]
    [InlineData("gallery", "\"some-preset\"", "gallery")]
    public void A_left_out_field_with_a_value_fails(string field, string value, string path)
    {
        var json = GalleryFiles.With(p => Set(p, field, JsonNode.Parse(value)));
        AssertFails(json, "field.left_out", "presets[0]." + path);
    }

    [Theory]
    [InlineData("output.location", "\"CustomFolder\"")]
    [InlineData("output.conflict", "\"Overwrite\"")]
    [InlineData("output.replaceOriginal", "true")]
    [InlineData("pdf.passwordLeftOut", "true")]
    [InlineData("translate.model", "\"llama3.1:70b\"")]
    [InlineData("image.hiddenAreas", "[{\"x\":0.1,\"y\":0.1,\"width\":0.2,\"height\":0.2,\"style\":\"Box\"}]")]
    [InlineData("image.annotations", "[{\"kind\":\"Arrow\",\"color\":\"#FF0000\",\"points\":[{\"x\":0.1,\"y\":0.2},{\"x\":0.5,\"y\":0.5}]}]")]
    [InlineData("media.clip", "{\"parts\":[{\"start\":0,\"end\":5}],\"music\":{\"path\":\"\",\"volume\":0.5}}")]
    public void Settings_once_left_out_are_shared_now(string path, string value)
    {
        var result = Check(GalleryFiles.With(p => Set(p, path, JsonNode.Parse(value))));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
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
    [InlineData("kind", "\"RunProgram\"", "field.value")]
    [InlineData("translate.model", "\"http://evil.example/m\"", "text.url")]
    [InlineData("image.hiddenAreas", "[{\"x\":2}]", "field.range")]
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
