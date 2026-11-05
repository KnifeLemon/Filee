// Generated archives exercise extraction-to-ISO, supported routes and safe failure behavior.

using System.IO.Compression;
using DiscUtils.Iso9660;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public sealed class ArchiveIsoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-archive-iso-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    public static IEnumerable<object[]> ReadableFormats() => ArchiveExtractor.ReadableFormats.Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(ReadableFormats))]
    public void Every_supported_archive_has_a_direct_iso_route(string format)
    {
        Assert.SkipUnless(SevenZip.Locate() is not null, "7-Zip is required.");
        var planner = new ConverterCatalog([new ArchiveConverter(), new FolderIsoConverter()]).CreatePlanner();
        Assert.Equal("archive", Assert.Single(planner.Plan(format, "iso")!.Steps).Converter.Id);
        Assert.Null(planner.Plan("png", "iso"));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    [InlineData("tar")]
    [InlineData("tgz")]
    [InlineData("tbz2")]
    [InlineData("txz")]
    [InlineData("alz")]
    [InlineData("egg")]
    [InlineData("jar")]
    public async Task Archives_become_iso_with_nested_unicode_files_and_empty_directories(string format)
    {
        Assert.SkipUnless(SevenZip.Locate() is not null, "7-Zip is required.");
        var input = Path.Combine(_root, "sample." + FormatRegistry.Get(format).PrimaryExtension);
        if (format == "alz")
            WriteAlz(input, [new("nested/한글.txt", "contents"u8.ToArray()), new("empty", null)]);
        else if (format == "egg")
            WriteEgg(input, [new("nested/한글.txt", false, new EggBlock("contents"u8.ToArray(), 0)), new("empty", true)]);
        else
        {
            var tree = Directory.CreateDirectory(Path.Combine(_root, "tree")).FullName;
            Directory.CreateDirectory(Path.Combine(tree, "nested"));
            Directory.CreateDirectory(Path.Combine(tree, "empty"));
            File.WriteAllText(Path.Combine(tree, "nested", "한글.txt"), "contents");
            await new ArchiveConverter().CombineAsync(Directory.GetFileSystemEntries(tree), input, format == "jar" ? "zip" : format,
                new Preset(), _root, null, TestContext.Current.CancellationToken);
        }
        var original = File.ReadAllBytes(input);
        var job = await Run(input);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        using var stream = File.OpenRead(Assert.Single(job.Outputs));
        using var iso = new CDReader(stream, true);
        Assert.True(iso.DirectoryExists("empty"));
        using var reader = new StreamReader(iso.OpenFile("nested\\한글.txt", FileMode.Open));
        Assert.Equal("contents", reader.ReadToEnd());
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData("gz", "gzip")]
    [InlineData("bz2", "bzip2")]
    [InlineData("xz", "xz")]
    [InlineData("lzma", "lzma")]
    [InlineData("lz", "lzip")]
    public async Task Single_compressed_files_can_be_put_in_an_iso(string format, string sevenZipType)
    {
        Assert.SkipUnless(SevenZip.Locate() is not null, "7-Zip is required.");
        var source = Path.Combine(_root, "source.txt");
        File.WriteAllText(source, "compressed contents");
        var input = Path.Combine(_root, "source.txt." + format);
        if (format == "lzma")
        {
            var data = File.ReadAllBytes(source);
            var packed = await RawLzmaAsync(data, true, _root);
            using var writer = new BinaryWriter(File.Create(input));
            writer.Write((byte)0x5d);
            writer.Write(1 << 20);
            writer.Write((long)data.Length);
            writer.Write(packed);
        }
        else if (format == "lz")
            File.WriteAllBytes(input, await LzipAsync(File.ReadAllBytes(source), _root));
        else
            await Run7zAsync("a", "-t" + sevenZipType, input, source);
        var job = await Run(input);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        using var stream = File.OpenRead(Assert.Single(job.Outputs));
        using var iso = new CDReader(stream, true);
        var file = Assert.Single(iso.GetFiles("\\"));
        using var reader = new StreamReader(iso.OpenFile(file, FileMode.Open));
        Assert.Equal("compressed contents", reader.ReadToEnd());
    }

    [Fact]
    public async Task Archive_to_plain_iso_respects_the_selected_standard()
    {
        Assert.SkipUnless(SevenZip.Locate() is not null, "7-Zip is required.");
        var input = Path.Combine(_root, "plain.zip");
        using (var zip = ZipFile.Open(input, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open())) writer.Write("plain contents");
        var job = await Run(input, IsoStandard.Iso9660);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        using var stream = File.OpenRead(Assert.Single(job.Outputs));
        using var iso = new CDReader(stream, true);
        var file = Assert.Single(iso.GetFiles("\\"));
        Assert.Contains("README.TXT", file, StringComparison.Ordinal);
        using var reader = new StreamReader(iso.OpenFile(file, FileMode.Open));
        Assert.Equal("plain contents", reader.ReadToEnd());
    }

    [Theory]
    [InlineData("zip", false)]
    [InlineData("7z", false)]
    [InlineData("7z", true)]
    public async Task Password_protected_archives_fail_clearly_and_preserve_existing_iso(string format, bool encryptedNames)
    {
        Assert.SkipUnless(SevenZip.Locate() is not null, "7-Zip is required.");
        var secret = Path.Combine(_root, "secret.txt");
        File.WriteAllText(secret, "secret contents");
        var input = Path.Combine(_root, "locked." + format);
        var args = new List<string> { "a", "-t" + format, "-ptest-password" };
        if (encryptedNames) args.Add("-mhe=on");
        args.AddRange([input, secret]);
        await Run7zAsync(args.ToArray());
        var output = Path.Combine(_root, "locked.iso");
        File.WriteAllText(output, "keep this image");
        var job = await Run(input);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("password-protected", job.Files[0].ErrorDetail);
        Assert.Equal("keep this image", File.ReadAllText(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_archive_entries_or_joliet_names_preserve_existing_iso(bool escapingPath)
    {
        Assert.SkipUnless(SevenZip.Locate() is not null, "7-Zip is required.");
        var input = Path.Combine(_root, "invalid.zip");
        using (var zip = ZipFile.Open(input, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry(escapingPath ? "../escape.txt" : new string('a', 65) + ".txt").Open()))
            writer.Write("test contents");
        var output = Path.Combine(_root, "invalid.iso");
        File.WriteAllText(output, "old image");
        var job = await Run(input);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("old image", File.ReadAllText(output));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task Empty_zip_becomes_an_empty_iso()
    {
        Assert.SkipUnless(SevenZip.Locate() is not null, "7-Zip is required.");
        var input = Path.Combine(_root, "empty.zip");
        using (ZipFile.Open(input, ZipArchiveMode.Create)) { }
        var job = await Run(input);
        Assert.True(job.State == JobState.Completed, job.Files[0].ErrorDetail);
        using var stream = File.OpenRead(Assert.Single(job.Outputs));
        using var iso = new CDReader(stream, true);
        Assert.Empty(iso.GetFiles("\\"));
        Assert.Empty(iso.GetDirectories("\\"));
    }

    [Fact]
    public async Task Cancellation_after_extraction_preserves_destination_and_cleans_scratch()
    {
        Assert.SkipUnless(SevenZip.Locate() is not null, "7-Zip is required.");
        var input = Path.Combine(_root, "cancel.zip");
        using (var zip = ZipFile.Open(input, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("file.txt").Open())) writer.Write("contents");
        var output = Path.Combine(_root, "cancel.iso");
        File.WriteAllText(output, "old image");
        using var cancel = new CancellationTokenSource();
        var converter = new ArchiveConverter();
        var step = new ConversionStep(input, "zip", "iso", new Preset { TargetFormat = "iso" },
            new CancellingOutput(output, cancel), _root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(step, null, cancel.Token));
        Assert.Equal("old image", File.ReadAllText(output));
        Assert.Empty(Directory.GetDirectories(_root, "archive-*"));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*.tmp"));
    }

    private async Task<ConversionJob> Run(string input, IsoStandard standard = IsoStandard.Iso9660Joliet)
    {
        await using var queue = new JobQueue(new ConverterCatalog([new ArchiveConverter(), new FolderIsoConverter()]));
        var job = new ConversionJob([input], new Preset
        {
            TargetFormat = "iso",
            Output = { Location = OutputLocation.SameFolder, Conflict = ConflictPolicy.Overwrite },
            Iso = { Standard = standard },
        }, "ISO image");
        await queue.RunAsync(job);
        return job;
    }

    private sealed class CancellingOutput(string path, CancellationTokenSource cancel) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) { cancel.Cancel(); return path; }
    }
}
