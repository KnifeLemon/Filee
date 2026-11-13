// Generated UDF images verify names, directory layout and atomic cancellation without committed fixtures.

using System.IO.Compression;
using DiscUtils.Udf;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public sealed class Udf1443Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-udf-").FullName;

    [Fact]
    public async Task An_actual_sparse_file_over_four_gib_converts_and_reads_back()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = Path.Combine(folder, "large.bin");
        using (File.Create(source)) { }
        if (OperatingSystem.IsWindows())
        {
            var result = await ProcessRunner.RunAsync("fsutil.exe", ["sparse", "setflag", source], TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, result.ExitCode);
        }
        var length = (1L << 32) + 65536;
        using (var file = File.OpenWrite(source))
        {
            file.SetLength(length);
            file.WriteByte(42);
            file.Position = length - 1;
            file.WriteByte(99);
        }
        var output = Path.Combine(_root, "large.iso");
        await Write(folder, output, ct: TestContext.Current.CancellationToken);
        using var image = File.OpenRead(output);
        using var udf = new UdfReader(image);
        using var stored = udf.OpenFile("large.bin", FileMode.Open);
        Assert.Equal(length, stored.Length);
        Assert.Equal(42, stored.ReadByte());
        stored.Position = (1L << 32) + 1;
        Assert.Equal(0, stored.ReadByte());
        stored.Position = length - 1;
        Assert.Equal(99, stored.ReadByte());
        var sevenZip = SevenZip.Locate();
        Assert.NotNull(sevenZip);
        var entries = await sevenZip.ListAsync(output, TestContext.Current.CancellationToken);
        Assert.Contains(entries, e => e.Path == "large.bin" && e.Size == length);
    }

    [Fact]
    public async Task Zip_to_udf_preserves_the_selected_filesystem_and_readable_contents()
    {
        var archive = Path.Combine(_root, "source.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry("empty/");
            using var writer = new StreamWriter(zip.CreateEntry(new string('x', 90) + ".txt").Open());
            writer.Write("archive contents");
        }
        var output = Path.Combine(_root, "archive.iso");
        await new ArchiveConverter().ConvertAsync(new ConversionStep(archive, "zip", "iso",
            new Preset { TargetFormat = "iso", Archive = { IsoFileSystem = IsoFileSystem.Udf201 } }, new Output(output), _root), null, TestContext.Current.CancellationToken);
        using var stream = File.OpenRead(output);
        using var udf = new UdfReader(stream);
        Assert.True(udf.DirectoryExists("empty"));
        using var reader = new StreamReader(udf.OpenFile(new string('x', 90) + ".txt", FileMode.Open));
        Assert.Equal("archive contents", reader.ReadToEnd());
    }

    [Fact]
    public async Task Unsupported_names_leave_existing_image_untouched()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        // Fits a Unix filesystem's 255-byte UTF-8 component, but exceeds UDF's 254-byte wide-name payload.
        File.WriteAllText(Path.Combine(folder, new string('\u0100', 126) + "ab"), "invalid UDF identifier");
        var output = Path.Combine(_root, "result.iso");
        File.WriteAllText(output, "keep");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Write(folder, output, ct: TestContext.Current.CancellationToken));
        Assert.Equal("keep", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*.tmp"));
    }

    [Fact]
    public async Task Source_changes_during_streaming_preserve_the_previous_image()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = Path.Combine(folder, "data.bin");
        File.WriteAllBytes(source, new byte[2 * 1024 * 1024]);
        var output = Path.Combine(_root, "result.iso");
        File.WriteAllText(output, "keep");
        var progress = new CallbackProgress(value =>
        {
            if (value > 0) File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-1));
        });
        await Assert.ThrowsAnyAsync<IOException>(() => Write(folder, output, progress, TestContext.Current.CancellationToken));
        Assert.Equal("keep", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*.tmp"));
    }

    [Fact]
    public async Task Mark_of_the_web_is_preserved_on_udf_output()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NTFS mark of the web.");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = Path.Combine(folder, "file.txt");
        File.WriteAllText(source, "marked");
        Assert.SkipUnless(Filee.Core.Platform.MarkOfTheWeb.TryWrite(source, Filee.Core.Platform.MarkOfTheWeb.Internet()), "NTFS required.");
        var output = Path.Combine(_root, "marked.iso");
        await Write(folder, output, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(Filee.Core.Platform.MarkOfTheWeb.Read(output));
    }

    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }

    [Fact]
    public void A_large_generated_file_is_readable_beyond_four_gib_without_materializing_the_image()
    {
        // A synthetic seekable source represents a sparse file without allocating multi-gigabyte test data.
        var length = (1L << 32) + 65536;
        var builder = new UdfBuilder();
        builder.AddFile("large.bin", length, () => new ZeroStream(length));
        using var image = builder.Build();
        using var udf = new UdfReader(image);
        using var file = udf.OpenFile("large.bin", FileMode.Open);
        Assert.Equal(length, file.Length);
        file.Position = length - 1;
        Assert.Equal(0, file.ReadByte());
        Assert.Equal(-1, file.ReadByte());
    }

    [Fact]
    public async Task Unicode_long_names_and_empty_directories_round_trip()
    {
        var folder = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var name = new string('a', 100) + "-한글.txt";
        File.WriteAllText(Path.Combine(folder, name), "UDF contents");
        var output = Path.Combine(_root, "result.iso");
        await Write(folder, output, ct: TestContext.Current.CancellationToken);
        using var image = File.OpenRead(output);
        using var udf = new UdfReader(image);
        Assert.True(udf.DirectoryExists("empty"));
        using var reader = new StreamReader(udf.OpenFile(name, FileMode.Open));
        Assert.Equal("UDF contents", reader.ReadToEnd());
    }

    [Fact]
    public async Task Cancellation_preserves_existing_destination_and_removes_temporary_output()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "large.bin"), new byte[2 * 1024 * 1024]);
        var output = Path.Combine(_root, "result.iso");
        File.WriteAllText(output, "keep");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write(folder, output, new CancelProgress(cancel), cancel.Token));
        Assert.Equal("keep", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*.tmp"));
    }

    private Task<IReadOnlyList<string>> Write(string folder, string output, IProgress<double>? progress = null, CancellationToken ct = default) =>
        new FolderIsoConverter().ConvertAsync(new ConversionStep(folder, "folder", "iso",
            new Preset { TargetFormat = "iso", Archive = { IsoFileSystem = IsoFileSystem.Udf201 } }, new Output(output), _root), progress, ct);

    private sealed class Output(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }

    private sealed class ZeroStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - Position);
            Array.Clear(buffer, offset, read);
            Position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset,
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelProgress(CancellationTokenSource cancel) : IProgress<double>
    {
        public void Report(double value) { if (value > 0) cancel.Cancel(); }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
