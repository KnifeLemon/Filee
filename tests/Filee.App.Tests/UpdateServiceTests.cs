using System.Net;
using System.Net.Sockets;
using Filee.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.2", "1.0.1", true)]
    [InlineData("1.1", "1.0.9", true)]
    [InlineData("v2.0.0-beta.1", "1.9.0", true)]
    [InlineData("v1.0.1", "1.0.1", false)]
    [InlineData("v1.0.0", "1.0.1", false)]
    [InlineData("nightly", "1.0.1", false)]
    [InlineData(null, "1.0.1", false)]
    public void Release_tags_compare_by_version(string? tag, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(tag, current));

    /// <summary>Filee works offline: a failed update check must neither throw nor report an update.</summary>
    public static TheoryData<string> OfflineFailures => ["no network", "timeout", "dropped while reading", "not json"];

    [Theory]
    [MemberData(nameof(OfflineFailures))]
    public async Task Update_check_without_internet_is_quiet(string failure)
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeNetwork(failure));

        var found = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(found);
        Assert.False(updates.IsUpdateAvailable);
    }

    private sealed class FakeNetwork(string failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => failure switch
        {
            "no network" => throw new HttpRequestException("No such host is known.", new SocketException((int)SocketError.HostNotFound)),
            "timeout" => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
            "dropped while reading" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) }),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>captive portal</html>") }),
        };
    }

    /// <summary>A response body whose connection breaks after the headers.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection was reset.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Download_page_is_the_latest_release()
    {
        Assert.EndsWith("/releases/latest", UpdateService.LatestReleaseUrl);
        Assert.StartsWith(UpdateService.RepositoryUrl.TrimEnd('/'), UpdateService.LatestReleaseUrl);
    }
}
