// WebDownloader against a local HTTP server: names, redirects, the size cap, timeouts, cancelling, and what is refused.
// The server is on this computer, which dragged links may not reach: the tests let 127.0.0.1 through (IsRefused).

using System.Net;
using System.Net.Sockets;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public sealed class WebDownloaderTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Func<HttpListenerContext, Task>> _routes = new(StringComparer.Ordinal);
    private readonly TempDir _dir = new();
    private readonly string _root;

    public WebDownloaderTests()
    {
        var port = FreePort();
        _root = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_root);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_routes.TryGetValue(context.Request.Url!.AbsolutePath, out var route))
                        await route(context);
                    else
                        context.Response.StatusCode = 404;
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException or InvalidOperationException)
                {
                    // The client went away (a test that cancels or times out).
                }
            });
        }
    }

    private void Serve(string path, byte[] body, string? contentType = null, string? disposition = null, bool chunked = false) =>
        _routes[path] = async context =>
        {
            if (contentType is not null)
                context.Response.ContentType = contentType;
            if (disposition is not null)
                context.Response.AddHeader("Content-Disposition", disposition);
            if (chunked)
                context.Response.SendChunked = true;
            else
                context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
        };

    private void Redirect(string path, string location, int status = 302) =>
        _routes[path] = context =>
        {
            context.Response.StatusCode = status;
            context.Response.RedirectLocation = location;
            return Task.CompletedTask;
        };

    private Uri Url(string path) => new(_root + path.TrimStart('/'));

    private static WebDownloader Downloader() => new(userAgent: "Filee-tests/1.0") { IsRefused = AllButTheServer };

    /// <summary>Every local address but the test server's.</summary>
    private static bool AllButTheServer(IPAddress address) => !address.Equals(IPAddress.Loopback) && LocalNetwork.IsLocal(address);

    [Fact]
    public async Task Names_the_file_as_the_server_says_and_by_what_it_is()
    {
        Serve("/get", Png, "application/octet-stream", "attachment; filename*=UTF-8''%EC%82%AC%EC%A7%84.jpg");
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(Url("/get"), _dir.Path, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_dir.Path, "사진.png"), path); // named "사진.jpg", but the bytes are a PNG
        Assert.Equal(Png, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Names_the_file_after_the_address_and_never_replaces_one()
    {
        Serve("/files/report.pdf", "%PDF-1.7 test"u8.ToArray(), "application/pdf");
        File.WriteAllText(Path.Combine(_dir.Path, "report.pdf"), "mine");
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(Url("/files/report.pdf"), _dir.Path, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_dir.Path, "report (2).pdf"), path);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir.Path, "report.pdf")));
    }

    [Fact]
    public async Task Follows_a_few_redirects_and_no_more()
    {
        Serve("/real/photo.png", Png, "image/png");
        Redirect("/short", "/hop");
        Redirect("/hop", _root + "real/photo.png", 301);
        for (var i = 0; i < 8; i++)
            Redirect($"/loop{i}", $"/loop{i + 1}");
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(Url("/short"), _dir.Path, null, null, TestContext.Current.CancellationToken);
        Assert.Equal("photo.png", Path.GetFileName(path));

        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/loop0"), _dir.Path, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("error.web_download_failed", refused.ErrorKey);
    }

    [Fact]
    public async Task Saved_files_carry_the_mark_of_the_web_with_the_address_they_came_from()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Mark of the Web is a Windows (NTFS) stream.");
        Assert.SkipUnless(MarkOfTheWeb.TryWrite(_dir.File("probe.txt"), MarkOfTheWeb.Internet()), "The temp drive holds no NTFS streams.");
        Serve("/real/notes.txt", "plain notes"u8.ToArray(), "text/plain");
        Redirect("/get", "/real/notes.txt");
        using var downloader = Downloader();

        var downloaded = await downloader.DownloadAsync(Url("/get"), _dir.Path, null, null, TestContext.Current.CancellationToken);
        var dropped = await downloader.SaveAsync(new MemoryStream(Png), _dir.Path, "photo.png", "image/png", Png.Length, null,
            TestContext.Current.CancellationToken);

        var mark = MarkOfTheWeb.Read(downloaded);
        Assert.NotNull(mark);
        Assert.Contains("[ZoneTransfer]", mark);
        Assert.Contains("ZoneId=3", mark);
        Assert.Contains("HostUrl=" + _root + "real/notes.txt", mark); // where it really came from, after the redirect
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", MarkOfTheWeb.Read(dropped)); // bytes a drag brought: no address
    }

    [Fact]
    public void A_mark_names_only_web_addresses_without_sign_in_or_fragment()
    {
        var mark = MarkOfTheWeb.Internet(new Uri("https://user:secret@example.com/a/file.zip?x=1#part"), new Uri("file:///C:/page.html"));
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/a/file.zip?x=1\r\n", mark);
    }

    [Fact]
    public async Task Never_follows_a_redirect_off_the_web()
    {
        Redirect("/sneaky", "file:///C:/Windows/win.ini");
        using var downloader = Downloader();

        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/sneaky"), _dir.Path, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("error.web_download_failed", refused.ErrorKey);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Never_downloads_from_this_computer_or_the_local_network()
    {
        // As it ships: the test server on 127.0.0.1 is out of reach too, and is never asked.
        var asked = 0;
        _routes["/photo.png"] = async context =>
        {
            Interlocked.Increment(ref asked);
            await context.Response.OutputStream.WriteAsync(Png);
        };
        using var downloader = new WebDownloader();
        var port = new Uri(_root).Port;
        var ct = TestContext.Current.CancellationToken;

        foreach (var address in new[]
                 {
                     _root + "photo.png", $"http://localhost:{port}/photo.png", $"http://[::1]:{port}/photo.png",
                     $"http://[::ffff:127.0.0.1]:{port}/photo.png", "http://192.168.0.1/photo.png", "http://printer.local/photo.png",
                     "http://169.254.169.254/latest/photo.png", "http://0x7f000001/photo.png",
                 })
        {
            var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
                downloader.DownloadAsync(new Uri(address), _dir.Path, null, null, ct));
            Assert.Equal("error.web_local_address", refused.ErrorKey);
        }
        Assert.Equal(0, asked);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Never_follows_a_redirect_into_the_local_network()
    {
        var asked = 0;
        _routes["/photo.png"] = async context =>
        {
            Interlocked.Increment(ref asked);
            await context.Response.OutputStream.WriteAsync(Png);
        };
        var port = new Uri(_root).Port;
        Redirect("/to-loopback", $"http://127.0.0.2:{port}/photo.png");
        Redirect("/to-localhost", $"http://localhost:{port}/photo.png");
        Redirect("/to-mapped", $"http://[::ffff:127.0.0.1]:{port}/photo.png");
        Redirect("/to-router", "http://192.168.1.1/photo.png", 301);
        Redirect("/to-name", $"http://rebind.test:{port}/photo.png"); // a public-looking name with a local answer
        Redirect("/hop", "/to-router");
        using var downloader = new WebDownloader
        {
            IsRefused = AllButTheServer,
            ResolveAsync = (name, _) => Task.FromResult(name == "rebind.test" ? new[] { IPAddress.Parse("10.0.0.7") } : []),
        };
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in new[] { "/to-loopback", "/to-localhost", "/to-mapped", "/to-router", "/to-name", "/hop" })
        {
            var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url(path), _dir.Path, null, null, ct));
            Assert.Equal("error.web_local_address", refused.ErrorKey);
        }
        Assert.Equal(0, asked);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task With_the_local_network_allowed_a_link_to_it_is_downloaded()
    {
        // Settings → General: the shipped downloader, no test hook. The drop's fetcher passes the setting on.
        Serve("/nas/photo.png", Png, "image/png");
        using var downloader = new WebDownloader();
        using var work = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        WebDrop Drop() => new([WebDropItem.FromAddress("photo.png", Url("/nas/photo.png"))], work.Path);

        using (var allowed = Drop())
        {
            var path = await allowed.CreateFetcher(new WebDropOptions(_dir.Path, false, AllowLocalNetwork: true), downloader)
                .FetchAsync(allowed.Placeholders[0], new Progress<TransferProgress>(), ct);
            Assert.Equal(Path.Combine(_dir.Path, "photo.png"), path);
            Assert.Equal(Png, await File.ReadAllBytesAsync(path, ct));
        }

        // Off again for the next drop: refused, also with a connection to the server still open from the last one.
        using var refused = Drop();
        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            refused.CreateFetcher(new WebDropOptions(_dir.Path, false), downloader)
                .FetchAsync(refused.Placeholders[0], new Progress<TransferProgress>(), ct));
        Assert.Equal("error.web_local_address", error.ErrorKey);
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Connects_only_to_the_checked_addresses_of_a_name()
    {
        // The name answers with a local address and one allowed here (the test's own server): the download goes to
        // the allowed one, the very address that was checked, not to whatever another lookup would say.
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var serve = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            var request = new List<byte>();
            var one = new byte[1];
            while (!request.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()) && await stream.ReadAsync(one, ct) == 1)
                request.Add(one[0]);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: 16\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
            await stream.WriteAsync(Png, ct);
            return System.Text.Encoding.ASCII.GetString([.. request]);
        }, ct);
        var lookups = 0;
        using var downloader = new WebDownloader
        {
            IsRefused = AllButTheServer,
            ResolveAsync = (_, _) =>
            {
                Interlocked.Increment(ref lookups);
                return Task.FromResult(new[] { IPAddress.Parse("10.0.0.7"), IPAddress.Loopback });
            },
        };

        var path = await downloader.DownloadAsync(new Uri($"http://files.test:{port}/photo.png"), _dir.Path, null, null, ct);

        Assert.Equal("photo.png", Path.GetFileName(path));
        Assert.Contains($"Host: files.test:{port}", await serve);
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task Refuses_a_file_over_the_size_cap_said_or_not_and_leaves_nothing_behind()
    {
        var big = new byte[64 * 1024];
        Png.CopyTo(big, 0);
        Serve("/said.png", big, "image/png");
        Serve("/unsaid.png", big, "image/png", chunked: true);
        using var downloader = new WebDownloader { MaxBytes = 10_000, IsRefused = AllButTheServer };

        var said = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/said.png"), _dir.Path, null, null, TestContext.Current.CancellationToken));
        var unsaid = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/unsaid.png"), _dir.Path, null, null, TestContext.Current.CancellationToken));

        Assert.Equal("error.web_too_large", said.ErrorKey);
        Assert.Equal("error.web_too_large", unsaid.ErrorKey);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Refuses_web_pages_and_programs()
    {
        Serve("/doc.pdf", "<!doctype html><title>Sign in</title>"u8.ToArray(), "text/html; charset=utf-8");
        Serve("/sneaky.pdf", "<html><body>Sign in</body></html>"u8.ToArray(), "application/octet-stream");
        Serve("/tool.png", "MZ\x90\0\x03\0\0\0"u8.ToArray(), "image/png");
        Serve("/x.bin", "%PDF-1.4"u8.ToArray(), "application/x-msdownload");
        using var downloader = Downloader();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("error.web_page", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/doc.pdf"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Equal("error.web_page", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/sneaky.pdf"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Equal("error.web_blocked", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/tool.png"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Equal("error.web_blocked", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/x.bin"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Reports_a_missing_file_with_its_status()
    {
        using var downloader = Downloader();
        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/nothing.pdf"), _dir.Path, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("error.web_download_failed", refused.ErrorKey);
        Assert.Equal("HTTP 404", refused.Detail);
    }

    [Fact]
    public async Task Gives_up_on_a_server_that_doesnt_answer_or_stalls()
    {
        _routes["/silent.png"] = async _ => await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
        _routes["/stalls.png"] = async context =>
        {
            context.Response.ContentType = "image/png";
            context.Response.ContentLength64 = 10_000;
            await context.Response.OutputStream.WriteAsync(Png);
            await context.Response.OutputStream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
        };
        using var downloader = new WebDownloader
        {
            IsRefused = AllButTheServer,
            ResponseTimeout = TimeSpan.FromMilliseconds(300),
            StallTimeout = TimeSpan.FromMilliseconds(300),
        };
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("error.web_timeout", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/silent.png"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Equal("error.web_timeout", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/stalls.png"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Cancelling_stops_the_download_and_removes_the_partial_file()
    {
        // A first chunk past the sniffed head, so the file gets created, then nothing until the test is over.
        var chunk = new byte[WebFormats.SniffLength * 4];
        Png.CopyTo(chunk, 0);
        _routes["/slow.png"] = async context =>
        {
            context.Response.ContentType = "image/png";
            context.Response.ContentLength64 = 10_000;
            await context.Response.OutputStream.WriteAsync(chunk);
            await context.Response.OutputStream.FlushAsync();
            await Task.Delay(Timeout.Infinite, _stop.Token);
        };
        using var downloader = Downloader();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // The first report comes right after the file is created and the head written into it.
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var download = downloader.DownloadAsync(Url("/slow.png"), _dir.Path, null, new ProgressCallback(_ => written.TrySetResult()), cancel.Token);
        await await Task.WhenAny(written.Task, download); // a download that fails before writing fails the test with its reason
        Assert.Single(Directory.GetFiles(_dir.Path));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.GetFiles(_dir.Path)); // deleted before the cancellation reaches the caller
    }

    [Fact]
    public async Task Reports_the_bytes_so_far_and_the_size_when_the_server_says_it()
    {
        var body = new byte[300_000];
        Png.CopyTo(body, 0);
        Serve("/sized.png", body, "image/png");
        Serve("/unsized.png", body, "image/png", chunked: true);
        using var downloader = new WebDownloader { ReportInterval = TimeSpan.Zero, IsRefused = AllButTheServer };
        var sized = new List<TransferProgress>();
        var unsized = new List<TransferProgress>();

        await downloader.DownloadAsync(Url("/sized.png"), _dir.Path, null, new SyncProgress(sized), TestContext.Current.CancellationToken);
        await downloader.DownloadAsync(Url("/unsized.png"), _dir.Path, null, new SyncProgress(unsized), TestContext.Current.CancellationToken);

        Assert.True(sized.Count >= 2);
        Assert.All(sized, p => Assert.Equal(body.Length, p.Total));
        Assert.Equal(sized.Select(p => p.Bytes).Order(), sized.Select(p => p.Bytes)); // only forward
        Assert.Equal(new TransferProgress(body.Length, body.Length), sized[^1]);
        Assert.Equal(1, sized[^1].Fraction);
        Assert.All(unsized, p => Assert.Null(p.Total));
        Assert.Equal(body.Length, unsized[^1].Bytes);
        Assert.Null(unsized[^1].Fraction);
    }

    [Fact]
    public async Task Reports_are_spaced_out_but_the_last_one_always_comes()
    {
        var body = new byte[400_000];
        Png.CopyTo(body, 0);
        Serve("/many.png", body, "image/png");
        using var downloader = new WebDownloader { ReportInterval = TimeSpan.FromHours(1), IsRefused = AllButTheServer };
        var reports = new List<TransferProgress>();

        await downloader.DownloadAsync(Url("/many.png"), _dir.Path, null, new SyncProgress(reports), TestContext.Current.CancellationToken);

        Assert.Equal([new TransferProgress(WebFormats.SniffLength, body.Length), new TransferProgress(body.Length, body.Length)], reports);
    }

    [Fact]
    public async Task Bytes_a_browser_handed_over_are_saved_like_a_download()
    {
        using var downloader = Downloader();
        var item = WebDropItem.FromBytes("image.jpg", Png);

        var path = await item.SaveAsync(_dir.Path, downloader, null, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_dir.Path, "image.png"), path);
        var program = WebDropItem.FromBytes("innocent.png", "MZ\x90\0"u8.ToArray());
        Assert.Equal("error.web_blocked", (await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            program.SaveAsync(_dir.Path, downloader, null, TestContext.Current.CancellationToken))).ErrorKey);
    }

    [Fact]
    public async Task An_https_link_tunnels_through_a_proxy_on_this_computer()
    {
        // Clash and the like listen on 127.0.0.1. For an https site the handler first sends CONNECT to the proxy,
        // naming the proxy as its address: that connection must not be taken for a link to this computer.
        await using var proxy = new FakeProxy("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
        using var downloader = new WebDownloader(null, null, new SettingsWebProxy(() => proxy.Address, null));
        var ct = TestContext.Current.CancellationToken;

        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(new Uri("https://files.example/photo.png"), _dir.Path, null, null, ct));

        Assert.Equal("error.web_download_failed", error.ErrorKey);
        Assert.StartsWith("CONNECT files.example:443 ", await proxy.Request);
    }

    [Fact]
    public async Task An_http_link_is_fetched_through_a_proxy_on_this_computer()
    {
        var head = "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: 16\r\nConnection: close\r\n\r\n"u8.ToArray();
        await using var proxy = new FakeProxy([.. head, .. Png]);
        using var downloader = new WebDownloader(null, null, new SettingsWebProxy(() => proxy.Address, null));
        var ct = TestContext.Current.CancellationToken;

        var path = await downloader.DownloadAsync(new Uri("http://files.example/photo.png"), _dir.Path, null, null, ct);

        Assert.Equal(Png, await File.ReadAllBytesAsync(path, ct));
        Assert.StartsWith("GET http://files.example/photo.png ", await proxy.Request);
    }

    [Fact]
    public async Task With_a_proxy_a_link_to_this_computer_is_still_refused_and_never_sent_to_it()
    {
        await using var proxy = new FakeProxy([]);
        using var downloader = new WebDownloader(null, null, new SettingsWebProxy(() => proxy.Address, null));
        var ct = TestContext.Current.CancellationToken;

        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/photo.png"), _dir.Path, null, null, ct));

        Assert.Equal("error.web_local_address", error.ErrorKey);
        Assert.False(proxy.Request.IsCompleted);
    }

    /// <summary>A proxy on 127.0.0.1 that reads one request's head and answers with fixed bytes.</summary>
    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly TcpListener _server = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeProxy(byte[] answer)
        {
            _server.Start();
            Address = $"http://127.0.0.1:{((IPEndPoint)_server.LocalEndpoint).Port}";
            Request = Task.Run(async () =>
            {
                using var client = await _server.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var head = new List<byte>();
                var one = new byte[1];
                while (!head.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()) && await stream.ReadAsync(one, _stop.Token) == 1)
                    head.Add(one[0]);
                await stream.WriteAsync(answer, _stop.Token);
                return System.Text.Encoding.ASCII.GetString([.. head]);
            });
        }

        public string Address { get; }

        /// <summary>The request the proxy got, from its first line on.</summary>
        public Task<string> Request { get; }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _server.Stop();
            try
            {
                await Request;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException
                                           or InvalidOperationException)
            {
                // Never connected to, or the client hung up; or stopped before the accept even began.
            }
            _stop.Dispose();
        }
    }

    private sealed class ProgressCallback(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private sealed class SyncProgress(List<TransferProgress> values) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            lock (values)
                values.Add(value);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _dir.Dispose();
    }
}
