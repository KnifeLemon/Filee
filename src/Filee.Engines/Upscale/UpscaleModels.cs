// Curated image super-resolution models from upscayl/custom-models, pinned in upscale-models.json like the
// engines in engines.json. A model is downloaded when used, checked against its SHA-256 for both .param and .bin,
// and kept in engines/upscale-models/<model>/ by EngineInstaller.

using System.Text.Json;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Upscale;

/// <summary>One curated image super-resolution model.</summary>
public sealed record UpscaleModel(
    string Id,
    string Name,
    int Scale,
    string License,
    string Author,
    string Description,
    long ParamSize,
    string ParamSha256,
    long BinSize,
    string BinSha256)
{
    public long TotalSize => ParamSize + BinSize;
    public string ParamFileName => $"{Id}.param";
    public string BinFileName => $"{Id}.bin";
}

public static class UpscaleModels
{
    public const string Default = "4x_NMKD-Superscale-SP_178000_G";

    private static readonly (IReadOnlyList<UpscaleModel> All, IReadOnlyDictionary<string, EngineComponent> Components) List = Load();

    /// <summary>Every curated model offered.</summary>
    public static IReadOnlyList<UpscaleModel> All => List.All;

    /// <summary>The downloads, for <see cref="EngineInstaller"/> (param and bin per model).</summary>
    internal static IReadOnlyDictionary<string, EngineComponent> Components => List.Components;

    /// <summary>Folder that receives one sub folder per model.</summary>
    public static string Root => Path.Combine(EngineEnvironment.DownloadRoot, "upscale-models");

    public static UpscaleModel? Find(string id) => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The folder containing both model files, or null while not installed.</summary>
    public static string? FolderOf(string id)
    {
        if (Find(id) is not { } model)
            return null;
        var folder = Path.Combine(Root, model.Id);
        if (!File.Exists(Path.Combine(folder, model.ParamFileName)) || !File.Exists(Path.Combine(folder, model.BinFileName)))
            return null;
        return new EngineInstaller(Http.Value, Root, Components).IsInstalled(PackageOf(model.Id)) ? folder : null;
    }

    public static bool IsInstalled(string id) => FolderOf(id) is not null;

    /// <summary>All models that have been downloaded and verified.</summary>
    public static IReadOnlyList<UpscaleModel> InstalledModels => All.Where(m => IsInstalled(m.Id)).ToList();

    /// <summary>Downloads model <paramref name="id"/> if not already installed; one download at a time.</summary>
    public static async Task<string> EnsureAsync(string id, IProgress<EngineInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Find(id) is not { } model)
            throw new ArgumentException($"Unknown upscale model '{id}'.", nameof(id));

        await OneAtATime.WaitAsync(cancellationToken);
        try
        {
            if (FolderOf(model.Id) is { } existing)
                return existing;

            var installer = new EngineInstaller(Http.Value, Root, Components);
            await installer.InstallAsync(PackageOf(model.Id), progress, cancellationToken);
            return FolderOf(model.Id) ?? throw new InvalidDataException($"The {model.Id} model files are missing after download.");
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>Removes a downloaded model cleanly.</summary>
    public static void Uninstall(string id)
    {
        if (Find(id) is not { } model)
            return;
        new EngineInstaller(Http.Value, Root, Components).Uninstall(PackageOf(model.Id));
    }

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static readonly Lazy<HttpClient> Http = new(() => EngineInstaller.CreateHttpClient(TimeSpan.FromMinutes(10)));

    private static EnginePackage PackageOf(string id) =>
        new("upscale-model-" + id, [$"{id}-param", $"{id}-bin"], Components[$"{id}-param"].Size + Components[$"{id}-bin"].Size, ["upscaler"]);

    private static (IReadOnlyList<UpscaleModel>, IReadOnlyDictionary<string, EngineComponent>) Load()
    {
        using var stream = typeof(UpscaleModels).Assembly.GetManifestResourceStream("Filee.Engines.upscale-models.json")
                           ?? throw new InvalidOperationException("Embedded upscale-models.json is missing.");
        using var json = JsonDocument.Parse(stream);
        var urlTemplate = json.RootElement.GetProperty("url").GetString()!;
        var commit = json.RootElement.GetProperty("commit").GetString()!;

        var models = new List<UpscaleModel>();
        var components = new Dictionary<string, EngineComponent>();

        foreach (var entry in json.RootElement.GetProperty("models").EnumerateObject())
        {
            var id = entry.Name;
            var name = entry.Value.GetProperty("name").GetString()!;
            var scale = entry.Value.GetProperty("scale").GetInt32();
            var license = entry.Value.GetProperty("license").GetString()!;
            var author = entry.Value.GetProperty("author").GetString()!;
            var description = entry.Value.GetProperty("description").GetString()!;

            var paramEl = entry.Value.GetProperty("param");
            var paramSha256 = paramEl.GetProperty("sha256").GetString()!;
            var paramSize = paramEl.GetProperty("size").GetInt64();

            var binEl = entry.Value.GetProperty("bin");
            var binSha256 = binEl.GetProperty("sha256").GetString()!;
            var binSize = binEl.GetProperty("size").GetInt64();

            models.Add(new UpscaleModel(id, name, scale, license, author, description, paramSize, paramSha256, binSize, binSha256));

            components[$"{id}-param"] = new EngineComponent(
                $"{id}-param",
                commit,
                string.Format(System.Globalization.CultureInfo.InvariantCulture, urlTemplate, $"{id}.param"),
                paramSha256,
                paramSize,
                "ncnn-param");

            components[$"{id}-bin"] = new EngineComponent(
                $"{id}-bin",
                commit,
                string.Format(System.Globalization.CultureInfo.InvariantCulture, urlTemplate, $"{id}.bin"),
                binSha256,
                binSize,
                "ncnn-bin");
        }

        return (models, components);
    }
}
