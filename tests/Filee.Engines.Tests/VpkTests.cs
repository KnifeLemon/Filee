// Valve paks (.vpk, #1526): the directory file lists the files; their data follows the tree in it (archive 0x7FFF) or is
// in "<name>_nnn.vpk" next to it, after a few preload bytes in the tree. The test paks are built here byte by byte;
// a PS Vita .vpk is a ZIP and goes to 7-Zip.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class VpkTests(EngineFixture fx) : IClassFixture<EngineFixture>
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

    /// <summary>A file in the pak: its folder, name and extension, content, preload length and where its data goes.</summary>
    private sealed record PakFile(string Folder, string Name, string Extension, byte[] Content, int Preload, int Archive);

    private const int InDirectory = 0x7FFF;

    /// <summary>
    /// Writes "<paramref name="name"/>_dir.vpk" (version <paramref name="version"/>) and its "_000.vpk" part into
    /// <paramref name="folder"/>; returns the directory file.
    /// </summary>
    private static string BuildPak(string folder, string name, int version, params PakFile[] files)
    {
        var tree = new MemoryStream();
        var inDirectory = new MemoryStream();
        var part = new MemoryStream();
        void Text(string s) => tree.Write(Encoding.ASCII.GetBytes(s + "\0"));
        var fields = new byte[18];
        foreach (var byExtension in files.GroupBy(f => f.Extension))
        {
            Text(byExtension.Key);
            foreach (var byFolder in byExtension.GroupBy(f => f.Folder))
            {
                Text(byFolder.Key);
                foreach (var file in byFolder)
                {
                    Text(file.Name);
                    var data = file.Content[file.Preload..];
                    var target = file.Archive == InDirectory ? inDirectory : part;
                    BinaryPrimitives.WriteUInt32LittleEndian(fields, Crc32.Compute(file.Content));
                    BinaryPrimitives.WriteUInt16LittleEndian(fields.AsSpan(4), (ushort)file.Preload);
                    BinaryPrimitives.WriteUInt16LittleEndian(fields.AsSpan(6), (ushort)file.Archive);
                    BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(8), (uint)target.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(12), (uint)data.Length);
                    BinaryPrimitives.WriteUInt16LittleEndian(fields.AsSpan(16), 0xFFFF);
                    tree.Write(fields);
                    tree.Write(file.Content.AsSpan(0, file.Preload));
                    target.Write(data);
                }
                Text("");
            }
            Text("");
        }
        Text("");

        var dir = new MemoryStream();
        var header = new byte[version == 2 ? 28 : 12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x55AA1234);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)tree.Length);
        if (version == 2)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)inDirectory.Length);
        dir.Write(header);
        tree.WriteTo(dir);
        inDirectory.WriteTo(dir);
        var path = Path.Combine(folder, name + "_dir.vpk");
        File.WriteAllBytes(path, dir.ToArray());
        if (part.Length > 0)
            File.WriteAllBytes(Path.Combine(folder, name + "_000.vpk"), part.ToArray());
        return path;
    }

    private static PakFile[] SampleFiles =>
    [
        new("materials/brick", "brickwall001", "vtf", Noise(5000, 1), 0, 0),
        new("materials/brick", "brickwall001", "vmt", Encoding.ASCII.GetBytes("\"LightmappedGeneric\" { \"$basetexture\" \"brick/brickwall001\" }"), 16, InDirectory),
        new("scripts", "game_sounds", "txt", Words(700), 0, InDirectory),
        new(" ", "readme", " ", Encoding.ASCII.GetBytes("top level, no extension"), 0, 0),
    ];

    [Fact]
    public void Vpk_is_recognised_and_unpacks_in_one_step()
    {
        Assert.Equal("vpk", FormatRegistry.Detect("pak01_dir.VPK")?.Id);
        var planner = fx.Catalog.CreatePlanner(["archive"]);
        Assert.Single(planner.Plan("vpk", FormatRegistry.Folder)!.Steps);
        Assert.Single(planner.Plan("vpk", "zip")!.Steps);
        Assert.False(FormatRegistry.Get("vpk")!.Writable);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_pak_unpacks_with_its_preload_bytes_and_its_parts(int version)
    {
        SkipWithout7Zip();
        var dir = BuildPak(fx.NewFolder(), "pak01", version, SampleFiles);

        var folder = await ConvertOkAsync(dir, "extract");

        foreach (var file in SampleFiles)
        {
            var name = (file.Folder.Trim() is "" ? "" : file.Folder + "/") + file.Name + (file.Extension.Trim() is "" ? "" : "." + file.Extension);
            Assert.Equal(file.Content, File.ReadAllBytes(Path.Combine(folder, name)));
        }
    }

    [Fact]
    public async Task A_pak_becomes_a_zip()
    {
        SkipWithout7Zip();
        var dir = BuildPak(fx.NewFolder(), "hl2_textures", 2, SampleFiles);

        var zip = await ConvertOkAsync(dir, "to-zip");

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["materials/brick/brickwall001.vmt", "materials/brick/brickwall001.vtf", "readme", "scripts/game_sounds.txt"],
            archive.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_missing_part_a_data_part_and_a_bad_checksum_are_refused_clearly()
    {
        var folder = fx.NewFolder();
        var dir = BuildPak(folder, "pak01", 2, SampleFiles);
        var target = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;

        var part = Path.Combine(folder, "pak01_000.vpk");
        var error = Assert.Throws<InvalidDataException>(() => Vpk.Extract(part, target, Ct));
        Assert.Contains("pak01_dir.vpk", error.Message);

        File.Move(part, part + ".bak");
        var missing = Assert.Throws<FileNotFoundException>(() => Vpk.Extract(dir, Directory.CreateDirectory(Path.Combine(folder, "out2")).FullName, Ct));
        Assert.Contains("pak01_000.vpk", missing.Message);
        File.Move(part + ".bak", part);

        // Change one byte of the texture's data in the part.
        var bytes = File.ReadAllBytes(part);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(part, bytes);
        Assert.Throws<InvalidDataException>(() => Vpk.Extract(dir, Directory.CreateDirectory(Path.Combine(folder, "out3")).FullName, Ct));

        File.WriteAllBytes(dir, File.ReadAllBytes(dir)[..20]);
        Assert.Throws<InvalidDataException>(() => Vpk.Extract(dir, Directory.CreateDirectory(Path.Combine(folder, "out4")).FullName, Ct));
    }

    [Theory]
    [InlineData("../..", "evil")]
    [InlineData("/etc", "evil")]
    [InlineData("C:/Windows", "evil")]
    public void A_name_leaving_the_output_folder_is_refused(string folderName, string name)
    {
        var folder = fx.NewFolder();
        var dir = BuildPak(folder, "evil", 1,
            new PakFile("lua", "fine", "lua", "x = 1"u8.ToArray(), 0, InDirectory),
            new PakFile(folderName, name, "lua", "boom"u8.ToArray(), 0, InDirectory));
        var target = Directory.CreateDirectory(Path.Combine(folder, "a", "b", "out")).FullName;

        var error = Assert.Throws<InvalidDataException>(() => Vpk.Extract(dir, target, Ct));

        Assert.Contains("outside the target folder", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target)); // nothing written, not even the safe file
    }

    [Fact]
    public void A_tree_pointing_outside_itself_is_refused()
    {
        var folder = fx.NewFolder();
        var dir = BuildPak(folder, "pak01", 1, new PakFile("lua", "a", "lua", Words(100), 10, InDirectory));
        var good = File.ReadAllBytes(dir);
        var fieldsAt = good.AsSpan().IndexOf("\0a\0"u8) + 3; // after the name "a", behind "lua\0lua\0"

        // Preload bytes running past the end of the tree.
        var preload = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(preload.AsSpan(fieldsAt + 4), 60000);
        File.WriteAllBytes(dir, preload);
        Assert.Throws<InvalidDataException>(() => Vpk.Extract(dir, Directory.CreateDirectory(Path.Combine(folder, "a")).FullName, Ct));

        // A data part beyond "_999".
        var index = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(index.AsSpan(fieldsAt + 6), 4000);
        File.WriteAllBytes(dir, index);
        Assert.Throws<InvalidDataException>(() => Vpk.Extract(dir, Directory.CreateDirectory(Path.Combine(folder, "b")).FullName, Ct));

        // A tree larger than Filee reads.
        var tree = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(8), uint.MaxValue);
        File.WriteAllBytes(dir, tree);
        Assert.Throws<InvalidDataException>(() => Vpk.Extract(dir, Directory.CreateDirectory(Path.Combine(folder, "c")).FullName, Ct));
    }

    [Fact]
    public async Task A_ps_vita_package_is_a_zip()
    {
        SkipWithout7Zip();
        var folder = fx.NewFolder();
        var vpk = Path.Combine(folder, "homebrew.vpk");
        // A real package has eboot.bin beside sce_sys, so the folder is not flattened.
        using (var zip = ZipFile.Open(vpk, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry("eboot.bin").Open())
                entry.Write(Noise(32, 8));
            using (var entry = zip.CreateEntry("sce_sys/param.sfo").Open())
                entry.Write(Noise(64, 9));
        }

        var unpacked = await ConvertOkAsync(vpk, "extract");

        Assert.Equal(Noise(32, 8), File.ReadAllBytes(Path.Combine(unpacked, "eboot.bin")));
        Assert.Equal(Noise(64, 9), File.ReadAllBytes(Path.Combine(unpacked, "sce_sys", "param.sfo")));
    }
}
