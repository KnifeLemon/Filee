// Garry's Mod addons (.gma, #1526): an addon unpacks into its files with every CRC checked (0, from gmad -nocrc, is not
// checked) and an addon.json for its title, description, type and tags, as gmad reads it to pack the folder again. Names
// that would leave the output folder and damaged addons are refused. The test addons are built here byte by byte.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class GmaTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SkipWithout7Zip()
    {
        var engine = fx.Converters.Single(c => c.Id == "archive");
        Assert.SkipUnless(fx.Catalog.StatusOf(engine).IsAvailable, "7-Zip not found (pwsh build/fetch-engines.ps1 -Only 7zip)");
    }

    private async Task<string> ConvertOkAsync(string source, string presetId)
    {
        var job = await fx.ConvertAsync([source], BuiltInData.CreatePresets().Single(p => p.Id == presetId));
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    private static readonly (string Name, byte[] Data)[] Files =
    [
        ("lua/autorun/gates.lua", "print('gates')\n"u8.ToArray()),
        ("materials/gates/gate.vtf", Noise(300, 1)),
        ("models/gates/gate.mdl", Noise(200, 2)),
    ];

    /// <summary>
    /// A version 3 addon as gmad writes it: one required-content entry, <paramref name="description"/>, a CRC per file
    /// (0 for each with <paramref name="crcs"/> off, as gmad -nocrc) and a CRC over the whole addon.
    /// </summary>
    private static byte[] BuildGma((string Name, byte[] Data)[] files, string description, bool crcs = true)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("GMAD"u8);
            writer.Write((byte)3);
            writer.Write(0UL);
            writer.Write(1_600_000_000UL);
            writer.Write("base\0"u8); // one required content entry
            writer.Write((byte)0);
            writer.Write("Wire Gates\0"u8);
            writer.Write(Encoding.UTF8.GetBytes(description + "\0"));
            writer.Write("Author Name\0"u8);
            writer.Write(1);
            var number = 0u;
            foreach (var (name, data) in files)
            {
                writer.Write(++number);
                writer.Write(Encoding.UTF8.GetBytes(name + "\0"));
                writer.Write((long)data.Length);
                writer.Write(crcs ? Crc32.Compute(data) : 0u);
            }
            writer.Write(0u);
            foreach (var (_, data) in files)
                writer.Write(data);
        }
        var bytes = stream.ToArray();
        return [.. bytes, .. BitConverter.GetBytes(crcs ? Crc32.Compute(bytes) : 0u)];
    }

    private const string JsonDescription = """{ "description": "Logic gates.", "type": "tool", "tags": ["fun", "build"] }""";

    [Fact]
    public void Gma_is_recognised_and_packs_and_unpacks_in_one_step()
    {
        Assert.Equal("gma", FormatRegistry.Detect("Addon.GMA")?.Id);
        Assert.True(FormatRegistry.Get("gma")!.Writable);
        var planner = fx.Catalog.CreatePlanner(["archive"]);
        Assert.Single(planner.Plan("gma", "zip")!.Steps);
        Assert.Single(planner.Plan("gma", FormatRegistry.Folder)!.Steps);
        Assert.Single(planner.Plan("zip", "gma")!.Steps);
        Assert.Single(planner.Plan(FormatRegistry.Folder, "gma")!.Steps);
    }

    [Fact]
    public async Task An_addon_unpacks_with_an_addon_json_gmad_packs_it_from()
    {
        SkipWithout7Zip();
        var path = Path.Combine(fx.NewFolder(), "wire-gates.gma");
        File.WriteAllBytes(path, BuildGma(Files, JsonDescription));

        var folder = await ConvertOkAsync(path, "extract");

        foreach (var (name, data) in Files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(folder, name)));
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "addon.json")))!;
        Assert.Equal("Wire Gates", (string?)json["title"]);
        Assert.Equal("tool", (string?)json["type"]);
        Assert.Equal("Logic gates.", (string?)json["description"]);
        Assert.Equal(["fun", "build"], json["tags"]!.AsArray().Select(t => (string?)t));
    }

    [Fact]
    public async Task An_addon_without_checksums_becomes_a_zip()
    {
        SkipWithout7Zip();
        var path = Path.Combine(fx.NewFolder(), "old.gma");
        File.WriteAllBytes(path, BuildGma([("lua/a.lua", "x = 1"u8.ToArray()), ("sound/b.wav", Noise(64, 5))], "Just text.", crcs: false));

        var zip = await ConvertOkAsync(path, "to-zip");

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["addon.json", "lua/a.lua", "sound/b.wav"],
            archive.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).Order(StringComparer.Ordinal));
        // An old addon's description is plain text, kept as it is.
        var json = JsonNode.Parse(new StreamReader(archive.GetEntry("addon.json")!.Open()).ReadToEnd())!;
        Assert.Equal("Just text.", (string?)json["description"]);
    }

    [Theory]
    [InlineData("../evil.lua")]
    [InlineData("lua/../../evil.lua")]
    [InlineData("/etc/evil.lua")]
    [InlineData("C:/Windows/evil.lua")]
    public void A_name_leaving_the_output_folder_is_refused(string name)
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "evil.gma");
        File.WriteAllBytes(path, BuildGma([("lua/fine.lua", "x = 1"u8.ToArray()), (name, "boom"u8.ToArray())], JsonDescription));
        var target = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;

        var error = Assert.Throws<InvalidDataException>(() => Gma.Extract(path, target, Ct));

        Assert.Contains("outside the target folder", error.Message);
        Assert.False(File.Exists(Path.Combine(folder, "evil.lua")));
    }

    [Fact]
    public void A_damaged_addon_is_refused()
    {
        var folder = fx.NewFolder();
        var good = BuildGma([("lua/a.lua", "x = 1"u8.ToArray())], JsonDescription);

        // Cut inside the file's content.
        var cut = Path.Combine(folder, "cut.gma");
        File.WriteAllBytes(cut, good[..^8]);
        Assert.Throws<InvalidDataException>(() => Gma.Extract(cut, Directory.CreateDirectory(Path.Combine(folder, "a")).FullName, Ct));

        // A wrong CRC for the file: it follows the name and the 8-byte size.
        var bad = (byte[])good.Clone();
        var nameEnd = bad.AsSpan().IndexOf("lua/a.lua\0"u8) + "lua/a.lua\0"u8.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(nameEnd + 8), 0xDEADBEEF);
        var wrong = Path.Combine(folder, "wrong.gma");
        File.WriteAllBytes(wrong, bad);
        Assert.Throws<InvalidDataException>(() => Gma.Extract(wrong, Directory.CreateDirectory(Path.Combine(folder, "b")).FullName, Ct));

        // A file larger than the addon itself.
        var huge = (byte[])good.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(huge.AsSpan(nameEnd), long.MaxValue / 2);
        var tooLarge = Path.Combine(folder, "huge.gma");
        File.WriteAllBytes(tooLarge, huge);
        Assert.Throws<InvalidDataException>(() => Gma.Extract(tooLarge, Directory.CreateDirectory(Path.Combine(folder, "c")).FullName, Ct));

        File.WriteAllBytes(Path.Combine(folder, "not.gma"), "PK\x03\x04"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => Gma.Extract(Path.Combine(folder, "not.gma"), folder, Ct));
    }
}
