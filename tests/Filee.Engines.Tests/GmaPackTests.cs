// Packing Garry's Mod addons (.gma, #1526): an addon folder packs into a GMA as gmad does (addon.json for the title, type,
// tags and ignores; only the files Garry's Mod loads; lower-case names; a CRC per file and over the whole addon), and an
// unpacked addon packs back into the same addon.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class GmaPackTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private async Task<string> ConvertOkAsync(IReadOnlyList<string> sources, Preset preset)
    {
        var job = await fx.ConvertAsync(sources, preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    private static Preset Extract => BuiltInData.CreatePresets().Single(p => p.Id == "extract");

    /// <summary>An addon folder: addon.json, a Lua script, a texture, a model part, and files Garry's Mod doesn't load.</summary>
    private static string AddonFolder(string parent, string name, bool withJson = true)
    {
        var folder = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        if (withJson)
        {
            File.WriteAllText(Path.Combine(folder, "addon.json"), """
                {
                    "title": "Wire Gates",
                    "type": "Tool",
                    "tags": ["Fun", "build"],
                    "description": "Logic gates.",
                    "ignore": ["*.psd", "src/*"]
                }
                """);
        }
        Write(folder, "lua/autorun/Gates.lua", Encoding.UTF8.GetBytes("print('gates')\n"));
        Write(folder, "materials/gates/gate.vtf", Noise(300, 1));
        Write(folder, "materials/gates/gate.vmt", Encoding.UTF8.GetBytes("\"VertexLitGeneric\" { \"$basetexture\" \"gates/gate\" }"));
        Write(folder, "models/gates/gate.mdl", Noise(200, 2));
        Write(folder, "models/gates/gate.sw.vtx", Noise(50, 3)); // unused by the game: whitelist takes it back
        Write(folder, "materials/gates/gate.psd", Noise(100, 4)); // ignored by addon.json
        Write(folder, "readme.txt", Encoding.UTF8.GetBytes("not loaded by the game"));
        return folder;
    }

    private static void Write(string folder, string name, byte[] data)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static Gma.Header ReadHeader(string gma)
    {
        using var stream = File.OpenRead(gma);
        return Gma.ReadHeader(stream, Path.GetFileName(gma));
    }

    [Fact]
    public async Task An_addon_folder_packs_as_gmad_does()
    {
        var folder = AddonFolder(fx.NewFolder(), "wire-gates");

        var gma = await ConvertOkAsync([folder], new Preset { TargetFormat = "gma", Archive = { CombineIntoOne = true } });

        Assert.Equal("wire-gates.gma", Path.GetFileName(gma));
        var bytes = File.ReadAllBytes(gma);
        Assert.Equal("GMAD"u8.ToArray(), bytes[..4]);
        Assert.Equal(3, bytes[4]);
        // The CRC of everything before the last four bytes.
        Assert.Equal(Crc32.Compute(bytes.AsSpan(0, bytes.Length - 4)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4)));

        var header = ReadHeader(gma);
        Assert.Equal("Wire Gates", header.Title);
        Assert.Equal("Logic gates.", header.Description);
        Assert.Equal("tool", header.Type);
        Assert.Equal(new[] { "fun", "build" }, header.Tags);
        // Lower case, sorted, only what Garry's Mod loads: no addon.json, no .sw.vtx, no ignored .psd, no readme.
        Assert.Equal(
            ["lua/autorun/gates.lua", "materials/gates/gate.vmt", "materials/gates/gate.vtf", "models/gates/gate.mdl"],
            header.Entries.Select(e => e.Name));
        var texture = header.Entries.Single(e => e.Name.EndsWith(".vtf", StringComparison.Ordinal));
        Assert.Equal(300, texture.Size);
        Assert.Equal(Crc32.Compute(Noise(300, 1)), texture.Crc);
    }

    [Fact]
    public async Task A_folder_without_addon_json_takes_its_name_as_the_title()
    {
        var folder = AddonFolder(fx.NewFolder(), "My Props", withJson: false);

        var header = ReadHeader(await ConvertOkAsync([folder], new Preset { TargetFormat = "gma", Archive = { CombineIntoOne = true } }));

        Assert.Equal("My Props", header.Title);
        Assert.Equal("servercontent", header.Type);
        // Without addon.json nothing is ignored by it, but the whitelist still applies.
        Assert.DoesNotContain(header.Entries, e => e.Name.EndsWith(".psd", StringComparison.Ordinal));
        Assert.Contains(header.Entries, e => e.Name == "models/gates/gate.mdl");
    }

    [Fact]
    public async Task A_folder_with_nothing_garrys_mod_loads_is_refused()
    {
        var folder = Directory.CreateDirectory(Path.Combine(fx.NewFolder(), "notes")).FullName;
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "hello");

        var job = await fx.ConvertAsync([folder], new Preset { TargetFormat = "gma", Archive = { CombineIntoOne = true } });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Empty(job.Outputs);
    }

    [Fact]
    public async Task An_addon_unpacks_and_packs_back_into_the_same_addon()
    {
        SkipWithout7Zip();
        var folder = AddonFolder(fx.NewFolder(), "wire-gates");
        var gma = await ConvertOkAsync([folder], new Preset { TargetFormat = "gma", Archive = { CombineIntoOne = true } });

        var unpacked = await ConvertOkAsync([gma], Extract);

        Assert.Equal(Encoding.UTF8.GetBytes("print('gates')\n"), File.ReadAllBytes(Path.Combine(unpacked, "lua", "autorun", "gates.lua")));
        Assert.Equal(Noise(300, 1), File.ReadAllBytes(Path.Combine(unpacked, "materials", "gates", "gate.vtf")));
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(unpacked, "addon.json")))!;
        Assert.Equal("Wire Gates", (string?)json["title"]);
        Assert.Equal("tool", (string?)json["type"]);
        Assert.Equal("Logic gates.", (string?)json["description"]);

        var again = ReadHeader(await ConvertOkAsync([unpacked], new Preset { TargetFormat = "gma", Archive = { CombineIntoOne = true } }));
        var first = ReadHeader(gma);
        Assert.Equal(first.Title, again.Title);
        Assert.Equal(first.Type, again.Type);
        Assert.Equal(first.Tags, again.Tags);
        Assert.Equal(first.Entries.Select(e => (e.Name, e.Size, e.Crc)), again.Entries.Select(e => (e.Name, e.Size, e.Crc)));
    }

    private void SkipWithout7Zip()
    {
        var engine = fx.Converters.Single(c => c.Id == "archive");
        Assert.SkipUnless(fx.Catalog.StatusOf(engine).IsAvailable, "7-Zip not found (pwsh build/fetch-engines.ps1 -Only 7zip)");
    }

    [Fact]
    public async Task A_zip_of_an_addon_folder_without_addon_json_packs_from_inside_it()
    {
        SkipWithout7Zip();
        var parent = fx.NewFolder();
        // As addons are shared: "My Props/lua/…" with a readme beside the folder, no addon.json.
        var wrapper = Directory.CreateDirectory(Path.Combine(parent, "download")).FullName;
        AddonFolder(wrapper, "My Props", withJson: false);
        File.WriteAllText(Path.Combine(wrapper, "README.txt"), "install into garrysmod/addons");
        var zip = Path.Combine(parent, "my-props.zip");
        ZipFile.CreateFromDirectory(wrapper, zip);

        var header = ReadHeader(await ConvertOkAsync([zip], new Preset { TargetFormat = "gma" }));

        Assert.Contains(header.Entries, e => e.Name == "lua/autorun/gates.lua");
        Assert.Contains(header.Entries, e => e.Name == "models/gates/gate.mdl");
        Assert.DoesNotContain(header.Entries, e => e.Name.Contains("props", StringComparison.Ordinal) || e.Name.EndsWith(".txt", StringComparison.Ordinal));
    }

    [Fact]
    public void The_addon_root_is_found_through_wrapper_folders()
    {
        var parent = fx.NewFolder();
        var nested = Directory.CreateDirectory(Path.Combine(parent, "a", "b", "My Addon", "materials", "x")).FullName;
        File.WriteAllBytes(Path.Combine(nested, "y.vtf"), Noise(10, 1));
        File.WriteAllText(Path.Combine(parent, "a", "readme.txt"), "hi");
        Assert.Equal(Path.Combine(parent, "a", "b", "My Addon"), Gma.Root(parent));

        // addon.json marks the root even without a known folder; a folder with several subfolders is the root itself.
        var withJson = Directory.CreateDirectory(Path.Combine(fx.NewFolder(), "wrap", "addon")).FullName;
        File.WriteAllText(Path.Combine(withJson, "addon.json"), "{}");
        Assert.Equal(withJson, Gma.Root(Path.GetDirectoryName(withJson)!));
        var several = fx.NewFolder();
        Directory.CreateDirectory(Path.Combine(several, "one"));
        Directory.CreateDirectory(Path.Combine(several, "two"));
        Assert.Equal(several, Gma.Root(several));
    }

    [Theory]
    [InlineData("lua/autorun/x.lua", true)]
    [InlineData("materials/a/b/c.vtf", true)]
    [InlineData("models/a.dx90.vtx", true)]
    [InlineData("models/a.sw.vtx", false)]
    [InlineData("gamemodes/sandbox/sandbox.txt", true)]
    [InlineData("gamemodes/sandbox/sub/x.txt", false)]
    [InlineData("data_static/x.json", true)]
    [InlineData("readme.txt", false)]
    [InlineData("lua/x.luac", false)]
    public void The_whitelist_matches_gmads(string name, bool allowed) => Assert.Equal(allowed, Gma.IsAllowed(name));
}
