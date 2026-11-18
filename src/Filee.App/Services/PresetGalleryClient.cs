// Talks to the preset gallery on filee.sh (#1530), and only while its window is open: index.json, the pictures, and a
// preset's preset.json (to show what it does, and through the download link to install it, which the site counts).
// Every path in index.json is relative to the address it came from, so the same code reads filee.sh's index and the
// repository's.
// Nothing else is sent. Requests go through the proxy of Settings → General (HttpClient's default proxy, see
// ProxySetting), each with a time limit and a size limit, and only to filee.sh or, when filee.sh can't be reached,
// to the gallery's repository on GitHub. Pictures are kept in the work folder (gallery/) up to a size, so the window
// opens quickly the next time.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Filee.Core.Gallery;
using Filee.Core.Platform;

namespace Filee.App.Services;

public sealed class PresetGalleryClient : IDisposable
{
    /// <summary>The list the app and the site show.</summary>
    public const string IndexUrl = "https://filee.sh/presets/index.json";

    /// <summary>The gallery repository's own index.json, read when filee.sh can't be reached.</summary>
    public const string RepositoryIndexUrl = "https://raw.githubusercontent.com/KnifeLemon/Filee-Presets/main/index.json";

    /// <summary>A shared preset's comments and likes.</summary>
    public const string DiscussionsUrl = "https://github.com/KnifeLemon/Filee-Presets/discussions/";

    /// <summary>How long one request may take, from asking to the last byte.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Largest thumbnail and picture read.</summary>
    public const int MaxThumbBytes = SharedPresetFolderCheck.MaxThumbBytes, MaxImageBytes = SharedPresetFolderCheck.MaxImageBytes;

    /// <summary>How much the picture cache may hold before the oldest pictures go, and how long one is kept.</summary>
    public const long MaxCacheBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(30);

    private readonly HttpClient _http;
    private readonly string? _cache;
    private readonly Lock _cacheGate = new();
    private Uri _index = new(IndexUrl);
    private bool _fromRepository;

    /// <param name="handler">The HTTP handler (tests pass one that answers in place of the network); the default
    /// follows the proxy setting.</param>
    /// <param name="cacheFolder">Where pictures are kept; the work folder's gallery/ when null, none when empty.</param>
    public PresetGalleryClient(HttpMessageHandler? handler = null, string? cacheFolder = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true) { Timeout = RequestTimeout + TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Filee", UpdateService.CurrentVersion));
        _cache = cacheFolder ?? Path.Combine(WorkFolders.Root, "gallery");
        if (_cache.Length == 0)
            _cache = null;
    }

    /// <summary>The index came from the repository on GitHub: filee.sh couldn't be reached (no counts then).</summary>
    public bool FromRepository => _fromRepository;

    /// <summary>The list of shared presets, from filee.sh or else from the repository.</summary>
    public async Task<GalleryIndex> GetIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var index = GalleryIndex.Parse(await GetBytesAsync(new Uri(IndexUrl), GalleryIndex.MaxBytes, cancellationToken));
            (_index, _fromRepository) = (new Uri(IndexUrl), false);
            return index;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or System.Text.Json.JsonException
                                   && !cancellationToken.IsCancellationRequested)
        {
            var index = GalleryIndex.Parse(await GetBytesAsync(new Uri(RepositoryIndexUrl), GalleryIndex.MaxBytes, cancellationToken));
            (_index, _fromRepository) = (new Uri(RepositoryIndexUrl), true);
            return index;
        }
    }

    /// <summary>
    /// A picture of <paramref name="entry"/> (one of its pictures or thumbnails, in any language), from the cache when it
    /// has it for this version of the preset.
    /// </summary>
    public async Task<byte[]> GetImageAsync(GalleryEntry entry, string path, CancellationToken cancellationToken)
    {
        if (!entry.HasPicture(path))
            throw new ArgumentException("Not one of the preset's pictures.", nameof(path));
        var max = path.EndsWith("-thumb.webp", StringComparison.Ordinal) ? MaxThumbBytes : MaxImageBytes;
        var key = CacheKey(entry, path);
        if (ReadCache(key, max) is { } cached)
            return cached;
        var data = await GetBytesAsync(FileUri(path), max, cancellationToken);
        WriteCache(key, data);
        return data;
    }

    /// <summary>The preset's preset.json as the gallery has it, to show what it does (not counted as a download).</summary>
    public Task<byte[]> GetPresetAsync(GalleryEntry entry, CancellationToken cancellationToken) =>
        GetBytesAsync(FileUri(entry.Preset), SharedBundleCheck.MaxBytes, cancellationToken);

    /// <summary>
    /// The preset's preset.json through its install link, which filee.sh counts; from the repository (which has none)
    /// the file itself. Either way the installer checks it against the SHA-256 the index lists.
    /// </summary>
    public Task<byte[]> DownloadPresetAsync(GalleryEntry entry, CancellationToken cancellationToken) =>
        GetBytesAsync(entry.Download is { } link && OnIndexHost(new Uri(_index, link)) is { } download ? download : FileUri(entry.Preset),
            SharedBundleCheck.MaxBytes, cancellationToken);

    /// <summary>The page of a shared preset's discussion.</summary>
    public static Uri DiscussionUri(int number) => new(DiscussionsUrl + number.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // index.json's paths were checked to be plain relative paths into the preset's folder (GalleryIndex), so joining them
    // to the index's address stays below it; the host is checked once more all the same.
    private Uri FileUri(string path) =>
        OnIndexHost(new Uri(_index, path)) ?? throw new InvalidDataException("A gallery path leaves the gallery's address.");

    private Uri? OnIndexHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, _index.Host, StringComparison.OrdinalIgnoreCase) ? uri : null;

    private async Task<byte[]> GetBytesAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        // A redirect (the download link's) must stay on HTTPS.
        if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
            throw new HttpRequestException("The gallery redirected to an address that isn't HTTPS.");
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"{uri.AbsolutePath} is larger than {maxBytes:N0} bytes.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"{uri.AbsolutePath} is larger than {maxBytes:N0} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>One file per picture and version of the preset (its <see cref="GalleryEntry.Updated"/>).</summary>
    private static string CacheKey(GalleryEntry entry, string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{path}|{entry.Updated.ToUnixTimeSeconds()}")))[..32] + ".img";

    private byte[]? ReadCache(string key, int maxBytes)
    {
        if (_cache is null)
            return null;
        try
        {
            var file = new FileInfo(Path.Combine(_cache, key));
            if (!file.Exists || file.Length > maxBytes || DateTime.UtcNow - file.LastWriteTimeUtc > MaxCacheAge)
                return null;
            var data = File.ReadAllBytes(file.FullName);
            file.LastAccessTimeUtc = DateTime.UtcNow;
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteCache(string key, byte[] data)
    {
        if (_cache is null)
            return;
        lock (_cacheGate)
        {
            try
            {
                Directory.CreateDirectory(_cache);
                var path = Path.Combine(_cache, key);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, data);
                File.Move(temp, path, overwrite: true);
                Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A cache that can't be written only means the pictures are downloaded again.
            }
        }
    }

    /// <summary>Deletes pictures older than <see cref="MaxCacheAge"/>, then the least recently used beyond the limit.</summary>
    private void Trim()
    {
        var files = new DirectoryInfo(_cache!).GetFiles("*.img")
            .OrderByDescending(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc)
            .ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total > MaxCacheBytes || DateTime.UtcNow - file.LastWriteTimeUtc > MaxCacheAge)
                file.Delete();
        }
    }

    public void Dispose() => _http.Dispose();
}
