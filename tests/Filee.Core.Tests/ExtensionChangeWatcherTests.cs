using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;
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
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"/folder/.hidden"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\folder\file.crdownload"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\folder\test.filee-conv.tmp"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\folder\desktop.ini"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\AppData\Local\file.png"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\repo\.git\hooks\file.png"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"/repo/.git/hooks/file.png"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\repo\bin\Debug\file.png"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath(@"/repo/bin/Debug/file.png"));

        Assert.False(ExtensionChangeWatcher.ShouldIgnorePath(@"C:\Users\User\Documents\report.pdf"));
        Assert.False(ExtensionChangeWatcher.ShouldIgnorePath(@"/home/user/desktop/photo.png"));
    }

    [Fact]
    public async Task HandleRename_ignores_same_extension()
    {
        var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true));
        var candidate = await watcher.HandleRenameAsync(@"C:\folder\photo1.png", @"C:\folder\photo2.png", TestContext.Current.CancellationToken);
        Assert.Null(candidate);
    }

    [Fact]
    public async Task HandleRename_ignores_unsupported_target_format()
    {
        var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true));
        var candidate = await watcher.HandleRenameAsync(@"C:\folder\photo.png", @"C:\folder\photo.xyzrandom", TestContext.Current.CancellationToken);
        Assert.Null(candidate);
    }

    [Fact]
    public async Task HandleRename_ignores_when_no_conversion_route_exists()
    {
        // DummyConverter has no route from png to mp4
        var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true));
        var candidate = await watcher.HandleRenameAsync(@"C:\folder\photo.png", @"C:\folder\photo.mp4", TestContext.Current.CancellationToken);
        Assert.Null(candidate);
    }

    [Fact]
    public async Task HandleRename_ignores_paths_currently_in_progress()
    {
        var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true));
        var path = @"C:\folder\busy.pdf";
        watcher.MarkInProgress(path);

        Assert.True(watcher.IsInProgress(path));
        var candidate = await watcher.HandleRenameAsync(@"C:\folder\busy.png", path, TestContext.Current.CancellationToken);
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
            await File.WriteAllBytesAsync(newPath, PdfHeader, TestContext.Current.CancellationToken);

            var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true), settleTimeout: TimeSpan.FromSeconds(1));
            var candidate = await watcher.HandleRenameAsync(oldPath, newPath, TestContext.Current.CancellationToken);

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
            await File.WriteAllBytesAsync(newPath, PngHeader, TestContext.Current.CancellationToken);

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

            var candidate = await watcher.HandleRenameAsync(oldPath, newPath, TestContext.Current.CancellationToken);
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
            await File.WriteAllBytesAsync(filePath, PngHeader, TestContext.Current.CancellationToken);

            var watcher = new ExtensionChangeWatcher(
                CreatePlanner,
                async (_, _, output, ct) =>
                {
                    // Mock conversion producing valid PDF
                    await File.WriteAllBytesAsync(output, PdfHeader, ct);
                    return true;
                });

            var pngFormat = FormatRegistry.Get("png");
            var pdfFormat = FormatRegistry.Get("pdf");

            var success = await watcher.ConvertSafelyAsync(filePath, pngFormat, pdfFormat, TestContext.Current.CancellationToken);
            Assert.True(success);

            // Verify file was replaced with PDF content
            var finalBytes = await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken);
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
            await File.WriteAllBytesAsync(filePath, PngHeader, TestContext.Current.CancellationToken);

            var watcher = new ExtensionChangeWatcher(
                CreatePlanner,
                (_, _, _, _) => Task.FromResult(false)); // Conversion fails

            var pngFormat = FormatRegistry.Get("png");
            var pdfFormat = FormatRegistry.Get("pdf");

            var success = await watcher.ConvertSafelyAsync(filePath, pngFormat, pdfFormat, TestContext.Current.CancellationToken);
            Assert.False(success);

            // Verify file was preserved with original PNG bytes
            var finalBytes = await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken);
            Assert.Equal(PngHeader, finalBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertSafely_succeeds_even_when_destination_file_is_read_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileeWatcherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "photo.pdf");
            await File.WriteAllBytesAsync(filePath, PngHeader, TestContext.Current.CancellationToken);

            // Mark destination file as ReadOnly (which normally causes UnauthorizedAccessException on overwrite)
            File.SetAttributes(filePath, FileAttributes.ReadOnly);

            var watcher = new ExtensionChangeWatcher(
                CreatePlanner,
                async (_, _, output, ct) =>
                {
                    await File.WriteAllBytesAsync(output, PdfHeader, ct);
                    return true;
                });

            var pngFormat = FormatRegistry.Get("png");
            var pdfFormat = FormatRegistry.Get("pdf");

            var success = await watcher.ConvertSafelyAsync(filePath, pngFormat, pdfFormat, TestContext.Current.CancellationToken);
            Assert.True(success);

            // Verify file was replaced with PDF content
            var finalBytes = await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken);
            Assert.Equal(PdfHeader, finalBytes);
        }
        finally
        {
            // Clear ReadOnly attribute before directory deletion
            var filePath = Path.Combine(dir, "photo.pdf");
            if (File.Exists(filePath))
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
            }
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShouldIgnorePath_filters_unix_virtual_and_system_files()
    {
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath("/proc/1/mounts"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath("/sys/devices/system/cpu"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath("/dev/shm/file.png"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath("/Users/User/._file.png"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath("/home/user/.cache/file.png"));
        Assert.True(ExtensionChangeWatcher.ShouldIgnorePath("/Users/User/Library/Caches/file.png"));

        Assert.False(ExtensionChangeWatcher.ShouldIgnorePath("/home/user/Downloads/document.pdf"));
        Assert.False(ExtensionChangeWatcher.ShouldIgnorePath("/Users/User/Desktop/photo.jpg"));
    }

    [Fact]
    public async Task ConvertSafely_moves_original_to_trash_when_trash_available()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileeWatcherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "photo.pdf");
            await File.WriteAllBytesAsync(filePath, PngHeader, TestContext.Current.CancellationToken);

            string? trashedPath = null;
            TrashRefusal? MockTrash(string path)
            {
                trashedPath = path;
                File.Delete(path);
                return null;
            }

            var watcher = new ExtensionChangeWatcher(
                CreatePlanner,
                async (_, _, output, ct) =>
                {
                    await File.WriteAllBytesAsync(output, PdfHeader, ct);
                    return true;
                },
                moveToTrash: MockTrash);

            var pngFormat = FormatRegistry.Get("png");
            var pdfFormat = FormatRegistry.Get("pdf");

            var success = await watcher.ConvertSafelyAsync(filePath, pngFormat, pdfFormat, TestContext.Current.CancellationToken);
            Assert.True(success);

            Assert.Equal(filePath, trashedPath);

            var finalBytes = await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken);
            Assert.Equal(PdfHeader, finalBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertSafely_preserves_original_file_as_backup_when_trash_is_not_available()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileeWatcherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "photo.pdf");
            await File.WriteAllBytesAsync(filePath, PngHeader, TestContext.Current.CancellationToken);

            var watcher = new ExtensionChangeWatcher(
                CreatePlanner,
                async (_, _, output, ct) =>
                {
                    await File.WriteAllBytesAsync(output, PdfHeader, ct);
                    return true;
                },
                moveToTrash: null);

            var pngFormat = FormatRegistry.Get("png");
            var pdfFormat = FormatRegistry.Get("pdf");

            var success = await watcher.ConvertSafelyAsync(filePath, pngFormat, pdfFormat, TestContext.Current.CancellationToken);
            Assert.True(success);

            var backupPath = Path.Combine(dir, "photo.original.png");
            Assert.True(File.Exists(backupPath));
            var backupBytes = await File.ReadAllBytesAsync(backupPath, TestContext.Current.CancellationToken);
            Assert.Equal(PngHeader, backupBytes);

            var finalBytes = await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken);
            Assert.Equal(PdfHeader, finalBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WaitForFileReady_waits_for_locked_file_to_become_accessible()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileeWatcherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "locked.pdf");
            await File.WriteAllBytesAsync(filePath, PngHeader, TestContext.Current.CancellationToken);

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            _ = Task.Run(async () =>
            {
                await Task.Delay(300, TestContext.Current.CancellationToken);
                stream.Dispose();
            }, TestContext.Current.CancellationToken);

            var ready = await ExtensionChangeWatcher.WaitForFileReadyAsync(filePath, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(ready);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Real_FileSystemWatcher_detects_extension_rename_on_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileeWatcherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var watcher = new ExtensionChangeWatcher(CreatePlanner, (_, _, _, _) => Task.FromResult(true), settleTimeout: TimeSpan.FromSeconds(2));
        try
        {
            var tcs = new TaskCompletionSource<ExtensionChangeCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.ExtensionChangeDetected += candidate =>
            {
                tcs.TrySetResult(candidate);
                return Task.CompletedTask;
            };

            watcher.Start([dir]);

            var oldPath = Path.Combine(dir, "sample.png");
            var newPath = Path.Combine(dir, "sample.pdf");

            await File.WriteAllBytesAsync(oldPath, PngHeader, TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            File.Move(oldPath, newPath);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var detected = await tcs.Task.WaitAsync(cts.Token);

            Assert.NotNull(detected);
            Assert.Equal("png", detected.SourceFormat.Id);
            Assert.Equal("pdf", detected.TargetFormat.Id);
            Assert.Equal("sample.pdf", detected.FileName);
        }
        finally
        {
            watcher.Stop();
            await watcher.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }
}
