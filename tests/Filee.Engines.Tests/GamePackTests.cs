// Minecraft packs and Modrinth modpacks are ZIPs under their own extensions (#824, #898): read like ZIP, and written with
// the marker file Minecraft or Modrinth looks for at the top.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class GamePackTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string[] Packs = ["mcpack", "mcaddon", "mcworld", "mctemplate", "mrpack"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SkipWithout7Zip()
    {
        var engine = fx.Converters.Single(c => c.Id == "archive");
        Assert.SkipUnless(fx.Catalog.StatusOf(engine).IsAvailable, "7-Zip not found (pwsh build/fetch-engines.ps1 -Only 7zip)");
    }

    private async Task<string> ConvertOkAsync(IReadOnlyList<string> sources, Preset preset)
    {
        var job = await fx.ConvertAsync(sources, preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    private static Preset To(string format) => BuiltInData.CreatePresets().Single(p => p.Id == "to-" + format);

    /// <summary>A resource pack's folder: its manifest, an icon and a texture below a subfolder.</summary>
    private static string PackFolder(string parent, string name, string marker = "manifest.json")
    {
        var folder = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        File.WriteAllText(Path.Combine(folder, marker), "{\"format_version\": 2}");
        File.WriteAllBytes(Path.Combine(folder, "pack_icon.png"), Noise(64, 1));
        Directory.CreateDirectory(Path.Combine(folder, "textures"));
        File.WriteAllBytes(Path.Combine(folder, "textures", "stone.png"), Noise(128, 2));
        return folder;
    }

    private static List<string> Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName.Replace('\\', '/')).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void Packs_are_recognised_and_convert_to_and_from_zip_in_one_step()
    {
        var planner = fx.Catalog.CreatePlanner(["archive"]);
        foreach (var pack in Packs)
        {
            Assert.Equal(pack, FormatRegistry.Detect("Pack." + pack.ToUpperInvariant())?.Id);
            Assert.Single(planner.Plan(pack, "zip")!.Steps);
            Assert.Single(planner.Plan("zip", pack)!.Steps);
            Assert.Single(planner.Plan(pack, FormatRegistry.Folder)!.Steps);
        }
    }

    [Fact]
    public async Task A_pack_folder_packed_into_one_pack_has_its_manifest_at_the_top()
    {
        var folder = PackFolder(fx.NewFolder(), "Stone Pack");

        // Like "ZIP all" (a dropped folder goes into an archive by packing), with the pack as the target.
        var pack = await ConvertOkAsync([folder], new Preset { TargetFormat = "mcpack", Archive = { CombineIntoOne = true } });

        Assert.EndsWith(".mcpack", pack);
        Assert.Equal(["manifest.json", "pack_icon.png", "textures/", "textures/stone.png"],
            Entries(pack).Where(e => !e.EndsWith('/') || e == "textures/").ToList());
    }

    [Theory]
    [InlineData("mcpack", "manifest.json")]
    [InlineData("mcworld", "level.dat")]
    [InlineData("mrpack", "modrinth.index.json")]
    public async Task A_zip_of_the_pack_folder_gives_a_pack_with_the_marker_at_the_top(string format, string marker)
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var folder = PackFolder(dir, "My Pack", marker);
        var zip = Path.Combine(dir, "My Pack.zip");
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        Assert.Contains($"My Pack/{marker}", Entries(zip));

        var pack = await ConvertOkAsync([zip], To(format));

        Assert.EndsWith("." + format, pack);
        Assert.Contains(marker, Entries(pack));
        Assert.DoesNotContain(Entries(pack), e => e.StartsWith("My Pack/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_add_on_keeps_its_packs_side_by_side()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var addOn = Directory.CreateDirectory(Path.Combine(dir, "add-on")).FullName;
        PackFolder(addOn, "behavior");
        PackFolder(addOn, "resources");
        var zip = Path.Combine(dir, "add-on.zip");
        ZipFile.CreateFromDirectory(addOn, zip);

        var pack = await ConvertOkAsync([zip], To("mcaddon"));

        Assert.Contains("behavior/manifest.json", Entries(pack));
        Assert.Contains("resources/manifest.json", Entries(pack));
    }

    [Theory]
    [InlineData("mcpack")]
    [InlineData("mrpack")]
    public async Task A_pack_becomes_a_zip_with_the_same_files(string format)
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var folder = PackFolder(dir, "pack", format == "mrpack" ? "modrinth.index.json" : "manifest.json");
        var pack = Path.Combine(dir, "pack." + format);
        ZipFile.CreateFromDirectory(folder, pack);

        var zip = await ConvertOkAsync([pack], To("zip"));

        var check = Path.Combine(dir, "check");
        ZipFile.ExtractToDirectory(zip, check);
        Assert.Equal(Snapshot(folder), Snapshot(check));
        await Task.CompletedTask.WaitAsync(Ct);
    }
}
