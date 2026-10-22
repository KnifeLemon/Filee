// CorelDRAW 2022 downgrade: a ZIP-based CDR keeps its artwork streams; only the version markers change.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Corel;

namespace Filee.Engines.Tests;

public sealed class CorelDrawTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-cdr-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Every_version_marker_is_written_as_coreldraw_2022()
    {
        var input = MakeCdr("newer.cdr", 2510);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken));
        var output = Path.Combine(_folder, "out.cdr");

        var written = await new CorelDrawConverter().ConvertAsync(Step(input, output), null, TestContext.Current.CancellationToken);

        Assert.Equal(output, Assert.Single(written));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken)));
        using var zip = ZipFile.OpenRead(output);
        var root = Read(zip, "content/root.dat");
        var fver = Find(root, "fver");
        var vrsn = Find(root, "vrsn");
        Assert.Equal(2400, root[fver + 16] | root[fver + 17] << 8);
        Assert.Equal(0, root[fver + 21]);
        Assert.Equal(24, root[fver + 22]);
        Assert.Equal(2400, BitConverter.ToInt32(root, vrsn + 16));
        Assert.Contains("<cdr:CoreVersion>2400</cdr:CoreVersion>", Encoding.UTF8.GetString(Read(zip, "META-INF/metadata.xml")));
        Assert.Contains("<cdr:AppVersion>2400</cdr:AppVersion>", Encoding.UTF8.GetString(Read(zip, "META-INF/metadata.xml")));
        Assert.Contains("\"version\": 2400", Encoding.UTF8.GetString(Read(zip, "styles/document.cdss")));

        // CorelDRAW requires the mimetype first and uncompressed; the artwork stream itself must not be re-encoded.
        Assert.Equal(["mimetype", "content/root.dat", "content/data/Bitmaps.dat", "content/data/page1.dat", "META-INF/metadata.xml", "styles/document.cdss"],
            zip.Entries.Select(e => e.FullName));
        Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
        Assert.Equal("bitmap-payload", Encoding.ASCII.GetString(Read(zip, "content/data/Bitmaps.dat")));
    }

    [Fact]
    public async Task A_file_at_or_below_coreldraw_2022_is_copied_unchanged()
    {
        var input = MakeCdr("older.cdr", 2300);
        var original = await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken);
        var output = Path.Combine(_folder, "out.cdr");

        await new CorelDrawConverter().ConvertAsync(Step(input, output), null, TestContext.Current.CancellationToken);

        Assert.Equal(original, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_donut_preset_saves_a_v24_copy()
    {
        using var fixture = new EngineFixture();
        var folder = fixture.NewFolder();
        var input = MakeCdr("drawing.cdr", 2510, folder);

        var job = await fixture.ConvertAsync([input], BuiltInData.CreatePresets().Single(p => p.Id == "cdr-v24"));

        Assert.Equal(JobState.Completed, job.State);
        var output = Assert.Single(job.Outputs);
        Assert.Equal("drawing_v24.cdr", Path.GetFileName(output));
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(2400, BitConverter.ToUInt16(Read(zip, "content/root.dat"), 28));
    }

    [Fact]
    public async Task A_binary_cdr_is_refused_with_a_localized_reason()
    {
        var input = Path.Combine(_folder, "old.cdr");
        await File.WriteAllBytesAsync(input, [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x43, 0x44, 0x52, 0x39], TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            new CorelDrawConverter().ConvertAsync(Step(input, Path.Combine(_folder, "out.cdr")), null, TestContext.Current.CancellationToken));

        Assert.Equal("error.cdr_not_zip", error.ErrorKey);
    }

    [Fact]
    public async Task A_container_without_a_version_record_is_refused()
    {
        var input = Path.Combine(_folder, "broken.cdr");
        using (var file = File.Create(input))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            Write(zip, "mimetype", Encoding.ASCII.GetBytes("application/x-vnd.corel.zcf.draw.document+zip"), CompressionLevel.NoCompression);
            Write(zip, "content/root.dat", Encoding.ASCII.GetBytes("RIFF0000CDRS"), CompressionLevel.Optimal);
        }

        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            new CorelDrawConverter().ConvertAsync(Step(input, Path.Combine(_folder, "out.cdr")), null, TestContext.Current.CancellationToken));

        Assert.Equal("error.cdr_no_version", error.ErrorKey);
    }

    private ConversionStep Step(string input, string output) =>
        new(input, "cdr", "cdr", new Preset { TargetFormat = "cdr" }, new FixedPath(output), _folder);

    private string MakeCdr(string name, int version, string? folder = null)
    {
        var path = Path.Combine(folder ?? _folder, name);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write(zip, "mimetype", Encoding.ASCII.GetBytes("application/x-vnd.corel.zcf.draw.document+zip"), CompressionLevel.NoCompression);
        Write(zip, "content/root.dat", Root(version), CompressionLevel.Optimal);
        Write(zip, "content/data/Bitmaps.dat", Encoding.ASCII.GetBytes("bitmap-payload"), CompressionLevel.Optimal);
        Write(zip, "content/data/page1.dat", Encoding.ASCII.GetBytes("page-payload"), CompressionLevel.Optimal);
        Write(zip, "META-INF/metadata.xml",
            Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><metadata><cdr:CoreVersion>{version}</cdr:CoreVersion><cdr:AppVersion>{version}</cdr:AppVersion></metadata>"),
            CompressionLevel.Optimal);
        Write(zip, "styles/document.cdss", Encoding.UTF8.GetBytes($"{{\"version\": {version}, \"style\": \"x\"}}"), CompressionLevel.Optimal);
        return path;
    }

    private static byte[] Root(int version)
    {
        var root = new byte[60];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(root, 0);
        BitConverter.GetBytes(52).CopyTo(root, 4);
        Encoding.ASCII.GetBytes("CDRS").CopyTo(root, 8);

        Encoding.ASCII.GetBytes("fver").CopyTo(root, 12);
        BitConverter.GetBytes(16).CopyTo(root, 16);
        root[20] = root[21] = root[22] = root[23] = 0xFF;
        BitConverter.GetBytes(8).CopyTo(root, 24);
        BitConverter.GetBytes((ushort)version).CopyTo(root, 28);
        root[30] = 1;
        root[33] = (byte)(version % 100 / 10);
        root[34] = (byte)(version / 100);

        Encoding.ASCII.GetBytes("vrsn").CopyTo(root, 36);
        BitConverter.GetBytes(16).CopyTo(root, 40);
        root[44] = root[45] = root[46] = root[47] = 0xFF;
        BitConverter.GetBytes(2).CopyTo(root, 48);
        BitConverter.GetBytes(version).CopyTo(root, 52);
        return root;
    }

    private static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        using var stream = zip.CreateEntry(name, level).Open();
        stream.Write(data);
    }

    private static byte[] Read(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static int Find(byte[] data, string id)
    {
        var position = 12;
        while (position + 8 <= data.Length)
        {
            var size = BitConverter.ToInt32(data, position + 4);
            if (Encoding.ASCII.GetString(data, position, 4) == id)
                return position;
            position += 8 + size;
        }
        return -1;
    }

    private sealed class FixedPath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
