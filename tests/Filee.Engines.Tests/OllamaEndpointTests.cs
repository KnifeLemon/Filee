using System.Net;
using System.Net.Sockets;
using System.Text;
using Filee.Engines.Translation;
using Xunit;

namespace Filee.Engines.Tests;

public class OllamaEndpointTests
{
    [Theory]
    [InlineData(null, "http://127.0.0.1:11434/")]
    [InlineData("", "http://127.0.0.1:11434/")]
    [InlineData("   ", "http://127.0.0.1:11434/")]
    [InlineData(":8080", "http://127.0.0.1:8080/")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("127.0.0.1", "http://127.0.0.1:11434/")]
    [InlineData("127.0.0.2:8080", "http://127.0.0.2:8080/")]
    [InlineData("localhost:9000", "http://127.0.0.1:9000/")]
    [InlineData("LOCALHOST", "http://127.0.0.1:11434/")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("http://localhost:8080/", "http://127.0.0.1:8080/")]
    [InlineData("http://127.0.0.1:8080/ollama", "http://127.0.0.1:8080/")]
    [InlineData("http://localhost", "http://127.0.0.1:80/")]
    [InlineData("https://127.0.0.1", "https://127.0.0.1:443/")]
    [InlineData("\"127.0.0.1:8080\"", "http://127.0.0.1:8080/")]
    [InlineData("[::1]:8080", "http://[::1]:8080/")]
    [InlineData("::1", "http://[::1]:11434/")]
    [InlineData("0.0.0.0", "http://127.0.0.1:11434/")]
    [InlineData("0.0.0.0:8080", "http://127.0.0.1:8080/")]
    [InlineData("[::]:8080", "http://[::1]:8080/")]
    [InlineData("127.0.0.1:99999", "http://127.0.0.1:11434/")]
    [InlineData("127.0.0.1:abc", "http://127.0.0.1:11434/")]
    public void Follows_OLLAMA_HOST_when_it_names_this_computer(string? value, string expected)
    {
        var (baseUri, ignored) = OllamaEndpoint.Resolve(value);

        Assert.Equal(new Uri(expected), baseUri);
        Assert.Null(ignored);
    }

    [Theory]
    [InlineData("192.168.1.10:11434")]
    [InlineData("10.0.0.5")]
    [InlineData("203.0.113.5:8080")]
    [InlineData("example.com:11434")]
    [InlineData("http://ollama.example.com")]
    [InlineData("https://203.0.113.5:443")]
    [InlineData("ollama.com")]
    [InlineData("localhost.example.com:11434")]
    [InlineData("127.0.0.1.nip.io:11434")]
    [InlineData("[fe80::1]:11434")]
    [InlineData("[2001:db8::1]:11434")]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    public void Ignores_OLLAMA_HOST_that_points_off_this_computer(string value)
    {
        var (baseUri, ignored) = OllamaEndpoint.Resolve(value);

        Assert.Null(OllamaEndpoint.Parse(value));
        Assert.Equal(OllamaEndpoint.DefaultBase, baseUri);
        Assert.Equal(value, ignored);
    }

    [Fact]
    public void Address_has_no_trailing_slash()
    {
        Assert.Equal("http://127.0.0.1:11434", OllamaEndpoint.DefaultBase.GetLeftPart(UriPartial.Authority));
        Assert.False(OllamaEndpoint.Address.EndsWith('/'));
    }

    [Fact]
    public async Task A_slow_Ollama_still_counts_as_running()
    {
        // An Ollama that is busy loading a model can take a while to list its models. The old 250 ms limit called
        // that "not available"; the background check now waits long enough.
        using var server = new SlowServer(TimeSpan.FromMilliseconds(600));
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await TranslatorConverter.IsOllamaAnsweringAsync(server.Base, TimeSpan.FromMilliseconds(250), ct));
        Assert.True(await TranslatorConverter.IsOllamaAnsweringAsync(server.Base, TranslatorConverter.CheckLimit, ct));
    }

    [Fact]
    public async Task Nothing_listening_is_not_running()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Assert.False(await TranslatorConverter.IsOllamaAnsweringAsync(new Uri($"http://127.0.0.1:{port}"), TranslatorConverter.CheckLimit, TestContext.Current.CancellationToken));
    }

    /// <summary>Answers <c>/api/tags</c> with an empty model list after a delay, like a busy Ollama.</summary>
    private sealed class SlowServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public SlowServer(TimeSpan delay)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Base = new Uri($"http://127.0.0.1:{port}");
            _listener.Prefixes.Add($"{Base}");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
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
                            await Task.Delay(delay);
                            var body = Encoding.UTF8.GetBytes("""{"models":[]}""");
                            context.Response.ContentType = "application/json";
                            await context.Response.OutputStream.WriteAsync(body);
                            context.Response.Close();
                        }
                        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException or InvalidOperationException)
                        {
                            // The client gave up first (the short-limit check).
                        }
                    });
                }
            });
        }

        public Uri Base { get; }

        public void Dispose() => _listener.Close();
    }
}
