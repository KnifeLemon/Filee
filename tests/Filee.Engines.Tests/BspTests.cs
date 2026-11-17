// Source engine maps (.bsp, #1526): the files packed into a map are a ZIP in its pakfile lump (lump 40). It unpacks like
// an archive; Left 4 Dead 2's lump order (version, offset, length) is read too. The test maps are built here: a header,
// some lump data and the ZIP.

using System.Buffers.Binary;
using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class BspTests(EngineFixture fx) : IClassFixture<EngineFixture>
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

    /// <summary>A ZIP as VBSP packs it: stored, with the map's custom files.</summary>
    private static byte[] PakfileZip(params (string Name, byte[] Data)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                entry.Write(data);
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// A map of version <paramref name="version"/>: the header, 4 KB of other lump data, then <paramref name="pakfile"/>
    /// as lump 40 (in Left 4 Dead 2's order when <paramref name="l4d2"/>).
    /// </summary>
    private static byte[] BuildMap(int version, byte[] pakfile, bool l4d2 = false)
    {
        const int headerSize = 8 + 64 * 16 + 4;
        var other = Noise(4096, 7);
        var map = new byte[headerSize + other.Length + pakfile.Length];
        "VBSP"u8.CopyTo(map);
        BinaryPrimitives.WriteInt32LittleEndian(map.AsSpan(4), version);
        // Lump 0 (entities) points at the other data, as in a real map.
        BinaryPrimitives.WriteInt32LittleEndian(map.AsSpan(8), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(map.AsSpan(12), other.Length);
        var lump = map.AsSpan(8 + 40 * 16, 16);
        var offset = headerSize + other.Length;
        if (l4d2)
        {
            BinaryPrimitives.WriteInt32LittleEndian(lump, 0);
            BinaryPrimitives.WriteInt32LittleEndian(lump[4..], offset);
            BinaryPrimitives.WriteInt32LittleEndian(lump[8..], pakfile.Length);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(lump, offset);
            BinaryPrimitives.WriteInt32LittleEndian(lump[4..], pakfile.Length);
        }
        other.CopyTo(map, headerSize);
        pakfile.CopyTo(map, offset);
        return map;
    }

    private static readonly (string Name, byte[] Data)[] Packed =
    [
        ("materials/maps/gm_test/brick.vtf", Noise(2000, 1)),
        ("materials/maps/gm_test/brick.vmt", "\"LightmappedGeneric\" { \"$basetexture\" \"maps/gm_test/brick\" }"u8.ToArray()),
        ("sound/ambient/wind.wav", Noise(800, 2)),
    ];

    [Fact]
    public void Bsp_is_recognised_and_unpacks_in_one_step()
    {
        Assert.Equal("bsp", FormatRegistry.Detect("gm_construct.BSP")?.Id);
        var planner = fx.Catalog.CreatePlanner(["archive"]);
        Assert.Single(planner.Plan("bsp", FormatRegistry.Folder)!.Steps);
        Assert.Single(planner.Plan("bsp", "zip")!.Steps);
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public async Task A_maps_packed_files_unpack(int version, bool l4d2)
    {
        SkipWithout7Zip();
        var path = Path.Combine(fx.NewFolder(), "gm_test.bsp");
        File.WriteAllBytes(path, BuildMap(version, PakfileZip(Packed), l4d2));

        var folder = await ConvertOkAsync(path, "extract");

        foreach (var (name, data) in Packed)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(folder, name)));
    }

    [Fact]
    public async Task A_maps_packed_files_become_a_zip()
    {
        SkipWithout7Zip();
        var path = Path.Combine(fx.NewFolder(), "gm_test.bsp");
        File.WriteAllBytes(path, BuildMap(20, PakfileZip(Packed)));

        var zip = await ConvertOkAsync(path, "to-zip");

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(Packed.Select(p => p.Name).Order(StringComparer.Ordinal),
            archive.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_map_without_packed_files_and_a_quake_map_are_refused_clearly()
    {
        var folder = fx.NewFolder();

        var empty = Path.Combine(folder, "empty.bsp");
        File.WriteAllBytes(empty, BuildMap(20, PakfileZip()));
        var error = Assert.Throws<InvalidDataException>(() => Bsp.ExtractPakfile(empty, Path.Combine(folder, "a.zip"), Ct));
        Assert.Contains("no files packed", error.Message);

        var quake = Path.Combine(folder, "e1m1.bsp");
        File.WriteAllBytes(quake, [29, 0, 0, 0, .. Noise(2000, 3)]); // Quake's BSP starts with its version
        error = Assert.Throws<InvalidDataException>(() => Bsp.ExtractPakfile(quake, Path.Combine(folder, "b.zip"), Ct));
        Assert.Contains("not a Source engine map", error.Message);

        var cut = Path.Combine(folder, "cut.bsp");
        File.WriteAllBytes(cut, BuildMap(20, PakfileZip(Packed))[..5000]);
        Assert.Throws<InvalidDataException>(() => Bsp.ExtractPakfile(cut, Path.Combine(folder, "c.zip"), Ct));
    }
}
