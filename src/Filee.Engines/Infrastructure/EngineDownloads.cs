// Engines that are downloaded on demand instead of being bundled with the installer (they are large), and the
// pinned download list they come from (engines.json, shared with build/fetch-engines.ps1).

using System.Text.Json;

namespace Filee.Engines.Infrastructure;

/// <summary>One download from engines.json.</summary>
/// <param name="Kind">
/// "zip" (extracted), "msi" (administrative install, no system changes; LibreOffice, calibre), "oxt" (LibreOffice
/// extension) or "conda" (conda-forge package: its Windows binaries are extracted).
/// </param>
/// <param name="Size">Download size in bytes.</param>
public sealed record EngineComponent(string Id, string Version, string Url, string Sha256, long Size, string Kind);

/// <summary>A set of components the user installs together, e.g. LibreOffice with its HWP import filter and Java.</summary>
/// <param name="Components">Installed in this order (the LibreOffice extension needs LibreOffice first).</param>
/// <param name="InstalledSize">Approximate size on disk in bytes, shown before installing.</param>
/// <param name="ConverterIds">Engines (<see cref="Filee.Core.Conversion.IConverter.Id"/>) the package provides.</param>
public sealed record EnginePackage(string Id, IReadOnlyList<string> Components, long InstalledSize, IReadOnlyList<string> ConverterIds);

public static class EngineDownloads
{
    /// <summary>Every component of engines.json by id.</summary>
    public static IReadOnlyDictionary<string, EngineComponent> Components { get; } = Load();

    /// <summary>
    /// Packages offered in Settings → Engines and on first run. rhwp and 7-Zip are not listed: they are small and bundled.
    /// </summary>
    public static IReadOnlyList<EnginePackage> Packages { get; } =
    [
        new("libreoffice", ["libreoffice", "h2orestart", "jre"], 1_265_000_000, ["libreoffice"]),
        new("pandoc", ["pandoc"], 236_000_000, ["pandoc"]),
        // Ghostscript from conda-forge plus the Microsoft C++ runtime it was built against (copied next to it).
        new("ghostscript", ["ghostscript", "vcruntime"], 31_000_000, ["ghostscript"]),
        new("ffmpeg", ["ffmpeg"], 272_000_000, ["ffmpeg"]),
        new("calibre", ["calibre"], 663_000_000, ["calibre"]),
    ];

    /// <summary>Total download size of a package in bytes.</summary>
    public static long DownloadSize(EnginePackage package) => package.Components.Sum(c => Components[c].Size);

    private static Dictionary<string, EngineComponent> Load()
    {
        using var stream = typeof(EngineDownloads).Assembly.GetManifestResourceStream("Filee.Engines.engines.json")
                           ?? throw new InvalidOperationException("Embedded engines.json is missing.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("components").EnumerateObject().ToDictionary(
            p => p.Name,
            p => new EngineComponent(
                p.Name,
                p.Value.GetProperty("version").GetString()!,
                p.Value.GetProperty("url").GetString()!,
                p.Value.GetProperty("sha256").GetString()!,
                p.Value.GetProperty("size").GetInt64(),
                p.Value.GetProperty("kind").GetString()!));
    }
}
