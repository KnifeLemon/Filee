using System.IO.Compression;
using System.Text;
using Filee.App.Services.Connect;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public sealed class FileeConnectTests : IDisposable
{
    private readonly string _storageDir;
    private readonly string _tempDir;
    private readonly SharedFolderManager _manager;

    public FileeConnectTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "FileeConnectTests_" + Guid.NewGuid().ToString("N"));
        _storageDir = Path.Combine(baseDir, "Storage");
        _tempDir = Path.Combine(baseDir, "Temp");
        _manager = new SharedFolderManager(NullLogger<SharedFolderManager>.Instance, _storageDir, _tempDir);
        _manager.Initialize();
    }

    public void Dispose()
    {
        try
        {
            var parent = Path.GetDirectoryName(_storageDir);
            if (parent != null && Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public void Manager_initializes_empty_folder_and_reports_full_free_space()
    {
        var items = _manager.GetFiles();
        Assert.Empty(items);
        Assert.Equal(SharedFolderManager.MaxTotalStorage, _manager.StorageLimitRemaining);
    }

    [Fact]
    public async Task Save_file_persists_content_and_retrieves_item()
    {
        var content = "Hello Filee Connect!";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var item = await _manager.SaveStreamAsync(stream, "test.txt", "test.txt", "pc", "text/plain");

        Assert.NotNull(item);
        Assert.Equal("test.txt", item.Name);
        Assert.Equal(content.Length, item.Size);
        Assert.True(File.Exists(item.DiskPath));
        Assert.Equal(content, File.ReadAllText(item.DiskPath));

        var files = _manager.GetFiles();
        Assert.Single(files);
        Assert.Equal("test.txt", files[0].Name);
    }

    [Fact]
    public async Task Delete_removes_file_and_updates_free_space()
    {
        using var stream = new MemoryStream(new byte[1024]);
        var item = await _manager.SaveStreamAsync(stream, "to-delete.bin", "to-delete.bin", "mobile", "application/octet-stream");

        var beforeDelete = _manager.GetFiles();
        Assert.Single(beforeDelete);

        var deleted = _manager.DeleteFile(item.Id);
        Assert.True(deleted);

        var afterDelete = _manager.GetFiles();
        Assert.Empty(afterDelete);
        Assert.Equal(SharedFolderManager.MaxTotalStorage, _manager.StorageLimitRemaining);
    }

    [Fact]
    public async Task WriteFolderZip_packages_folder_content_correctly()
    {
        var textContent = "File inside ZIP";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(textContent));
        await _manager.SaveStreamAsync(stream, "doc.txt", "subfolder/doc.txt", "pc", "text/plain");

        using var zipStream = new MemoryStream();
        _manager.WriteFolderZip("subfolder", zipStream);
        zipStream.Position = 0;

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name == "doc.txt");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        Assert.Equal(textContent, reader.ReadToEnd());
    }

    [Fact]
    public void Path_traversal_throws_argument_exception()
    {
        Assert.Throws<ArgumentException>(() => SharedFolderManager.CleanRelativePath("../../Windows/System32/calc.exe"));
        Assert.Throws<ArgumentException>(() => SharedFolderManager.CleanRelativePath("../secret.txt"));
        Assert.Throws<ArgumentException>(() => SharedFolderManager.CleanRelativePath(""));
    }

    [Fact]
    public void Manifest_resources_contain_connect_assets()
    {
        var asm = typeof(FileeConnectService).Assembly;
        var names = asm.GetManifestResourceNames();
        Assert.Contains(names, n => n.EndsWith("mobile.html", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.EndsWith("app.css", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.EndsWith("mobile.js", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.EndsWith("shared-files.js", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartSessionAsync_starts_kestrel_and_serves_mobile_html()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            Assert.NotNull(service.MobileUrl);

            using var http = new HttpClient();
            var response = await http.GetAsync(service.MobileUrl, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"Failed to fetch {service.MobileUrl}: {response.StatusCode} Body: {body}");
            Assert.Contains("Filee Connect", body);
            Assert.Contains("/css/app.css", body);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }
}
