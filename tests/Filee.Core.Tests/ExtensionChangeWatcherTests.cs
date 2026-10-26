using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Watching;
using Xunit;

namespace Filee.Core.Tests;

public sealed class ExtensionChangeWatcherTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
    private static readonly byte[] PdfHeader = "%PDF-1.7 header mock content"u8.ToArray();

    private sealed class DummyConverter : IConverter
    {
        public string Id => "dummy";
        public string DisplayName => "Dummy";
        public IReadOnlyList<ConversionEdge> Edges =>
        [
            new("png", "pdf", 1),
            new("jpg", "png", 1),
            new("png", "webp", 1),
        ];

        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => new(true);
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([step.InputPath]);
    }

    private static RoutePlanner CreatePlanner() =>
        new([new DummyConverter()]);

    [Fact]
    public void ShouldIgnorePath_filters_system_and_temp_files()
    {
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\folder\file.tmp"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\folder\.hidden"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\folder\file.crdownload"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\folder\test.filee-conv.tmp"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\folder\desktop.ini"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\AppData\Local\file.png"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\repo\.git\hooks\file.png"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\repo\bin\Debug\file.png"));

        Assert.False(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\Users\User\Documents\report.pdf"));
        Assert.False(ExtensionChangeWatcher.ShouldIgnorePath(@"/home/user/desktop/photo.png"));
    }

    [Fact]
    public async Task HandleRename_ignores_same_extension()
    {
        var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true));
        var candidate = await watcher.HandleRenameAsync(@"C:\folder\photo1.png", @"C:\folder\photo2.png");
        Assert.Null(candidate);
    }

    [Fact]
    public async Task HandleRename_ignores_unsupported_target_format()
    {
        var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true));
        var candidate = await watcher.HandleRenameAsync(@"C:\folder\photo.png", @"C:\folder\photo.xyzrandom");
        Assert.Null(candidate);
    }

    [Fact]
    public async Task HandleRename_ignores_when_no_conversion_route_exists()
    {
        // DummyConverter has no route from png to mp4
        var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true));
        var candidate = await watcher.HandleRenameAsync(@"C:\folder\photo.png", @"C:\folder\photo.mp4");
        Assert.Null(candidate);
    }

    [Fact]
    public async Task HandleRename_ignores_paths_currently_in_progress()
    {
        var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true));
        var path = @"C:\folder\busy.pdf";
        watcher.MarkInProgress(path);

        Assert.True(watcher.IsInProgress(path));
        var candidate = await watcher.HandleRenameAsync(@"C:\folder\busy.png", path);
        Assert.Null(candidate);

        watcher.UnmarkInProgress(path);
        Assert.False(watcher.IsInProgress(path));
    }

    [Fact]
    public async Task HandleRename_ignores_file_if_already_matching_target_signature()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileeWatcherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var oldPath = Path.Combine(dir, "doc.png");
            var newPath = Path.Combine(dir, "doc.pdf");

            // Write PDF bytes even though old name was png
            await File.WriteAllBytesAsync(newPath, PdfHeader);

            var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true), settleTimeout: TimeSpan.FromSeconds(1));
            var candidate = await watcher.HandleRenameAsync(oldPath, newPath);

            // Sniff shows it is already a PDF internally, so it should NOT convert!
            Assert.Null(candidate);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HandleRename_detects_valid_candidate_when_signature_is_png_and_target_is_pdf()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileeWatcherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var oldPath = Path.Combine(dir, "doc.png");
            var newPath = Path.Combine(dir, "doc.pdf");

            // Write PNG bytes (the user renamed doc.png to doc.pdf, so doc.pdf has PNG data inside)
            await File.WriteAllBytesAsync(newPath, PngHeader);

            var detected = false;
            var watcher = new ExtensionChangeWatcher(
                CreatePlanner,
                (_, _, _, _) => Task.FromResult(true),
                settleTimeout: TimeSpan.FromSeconds(1));

            watcher.ExtensionChangeDetected += c =>
            {
                detected = true;
                Assert.Equal("png", c.SourceFormat.Id);
                Assert.Equal("pdf", c.TargetFormat.Id);
                Assert.Equal("doc.pdf", c.FileName);
                return Task.CompletedTask;
            };

            var candidate = await watcher.HandleRenameAsync(oldPath, newPath);
            Assert.NotNull(candidate);
            Assert.True(detected);
            Assert.Equal("png", candidate.SourceFormat.Id);
            Assert.Equal("pdf", candidate.TargetFormat.Id);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertSafely_converts_and_atomically_replaces_target_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileeWatcherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "photo.pdf");
            await File.WriteAllBytesAsync(filePath, PngHeader);

            var watcher = new ExtensionChangeWatcher(
                CreatePlanner,
                async (_, _, output, _) =>
                {
                    // Mock conversion producing valid PDF
                    await File.WriteAllBytesAsync(output, PdfHeader);
                    return true;
                });

            var pngFormat = FormatRegistry.Get("png");
            var pdfFormat = FormatRegistry.Get("pdf");

            var success = await watcher.ConvertSafelyAsync(filePath, pngFormat, pdfFormat);
            Assert.True(success);

            // Verify file was replaced with PDF content
            var finalBytes = await File.ReadAllBytesAsync(filePath);
            Assert.Equal(PdfHeader, finalBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertSafely_preserves_original_file_if_conversion_fails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileeWatcherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "photo.pdf");
            await File.WriteAllBytesAsync(filePath, PngHeader);

            var watcher = new ExtensionChangeWatcher(
                CreatePlanner,
                (_, _, _, _) => Task.FromResult(false)); // Conversion fails

            var pngFormat = FormatRegistry.Get("png");
            var pdfFormat = FormatRegistry.Get("pdf");

            var success = await watcher.ConvertSafelyAsync(filePath, pngFormat, pdfFormat);
            Assert.False(success);

            // Verify file was preserved with original PNG bytes
            var finalBytes = await File.ReadAllBytesAsync(filePath);
            Assert.Equal(PngHeader, finalBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
