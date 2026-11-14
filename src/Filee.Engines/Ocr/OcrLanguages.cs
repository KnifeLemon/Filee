// The languages Tesseract can read text in (tessdata_fast 4.1.0, Apache-2.0), pinned in ocr-languages.json like the
// engines in engines.json. A language is downloaded the first time a preset needs it, checked against its SHA-256 and
// kept in engines/ocr-languages/<code>/<code>.traineddata by EngineInstaller, so a damaged or partial download never
// counts as installed. A preset can name several languages for text that mixes them (#1123), joined with + in
// PdfOptions.OcrLanguage, which is how Tesseract takes them too (chi_sim+eng); each one is its own download.

using System.Globalization;
using System.Text.Json;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Ocr;

/// <summary>One language Tesseract reads, by its Tesseract code (<c>eng</c>, <c>chi_sim</c>).</summary>
/// <param name="Culture">The language as a culture name, for its name in the UI.</param>
public sealed record OcrLanguage(string Code, string Culture, long Size)
{
    /// <summary>The language's own name ("Deutsch", "日本語"), the same in every UI language.</summary>
    public string NativeName
    {
        get
        {
            try
            {
                var name = CultureInfo.GetCultureInfo(Culture).NativeName;
                return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.GetCultureInfo(Culture)) + name[1..] : Code;
            }
            catch (CultureNotFoundException)
            {
                return Code;
            }
        }
    }
}

public static class OcrLanguages
{
    /// <summary>Read when a preset doesn't say (and the first language offered).</summary>
    public const string Default = "eng";

    private const string Version = "4.1.0";

    private static readonly (IReadOnlyList<OcrLanguage> All, IReadOnlyDictionary<string, EngineComponent> Components) List = Load();

    /// <summary>Every language offered, English first.</summary>
    public static IReadOnlyList<OcrLanguage> All => List.All;

    /// <summary>The downloads, for <see cref="EngineInstaller"/> (one component per language).</summary>
    internal static IReadOnlyDictionary<string, EngineComponent> Components => List.Components;

    /// <summary>Folder that receives one sub folder per language.</summary>
    public static string Root => Path.Combine(EngineEnvironment.DownloadRoot, "ocr-languages");

    public static OcrLanguage? Find(string code) => All.FirstOrDefault(l => l.Code == code);

    /// <summary>
    /// The languages a preset names (PdfOptions.OcrLanguage: one code, or several joined with <c>+</c>), the main one
    /// first and each once. A code that isn't in the list is left out, and with none left <see cref="Default"/> is read.
    /// </summary>
    public static IReadOnlyList<OcrLanguage> Parse(string? codes)
    {
        var languages = (codes ?? "").Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Find).OfType<OcrLanguage>().Distinct().ToList();
        return languages.Count > 0 ? languages : [Find(Default)!];
    }

    /// <summary>Codes as PdfOptions.OcrLanguage keeps them and Tesseract's <c>-l</c> takes them, each once.</summary>
    public static string Join(IEnumerable<string> codes) => string.Join(Separator, codes.Distinct());

    private const char Separator = '+';

    /// <summary>The language data, or null while it isn't downloaded.</summary>
    public static string? FileOf(string code) =>
        EngineEnvironment.FirstExisting(Path.Combine(Root, code, code + ".traineddata")) is { } file
        && new EngineInstaller(Http.Value, Root, Components).IsInstalled(PackageOf(code))
            ? file : null;

    public static bool IsInstalled(string code) => FileOf(code) is not null;

    /// <summary>Downloads <paramref name="code"/> if it isn't there yet; one download at a time.</summary>
    public static async Task<string> EnsureAsync(string code, CancellationToken cancellationToken)
    {
        if (!Components.ContainsKey(code))
            throw new ArgumentException($"Tesseract has no language '{code}' in Filee's list.", nameof(code));
        await OneAtATime.WaitAsync(cancellationToken);
        try
        {
            if (FileOf(code) is { } file)
                return file;
            await new EngineInstaller(Http.Value, Root, Components).InstallAsync(PackageOf(code), progress: null, cancellationToken);
            return FileOf(code) ?? throw new InvalidDataException($"The {code} language data is missing after its download.");
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static readonly Lazy<HttpClient> Http = new(() => EngineInstaller.CreateHttpClient(TimeSpan.FromMinutes(10)));

    private static EnginePackage PackageOf(string code) => new("ocr-" + code, [code], Components[code].Size, ["tesseract"]);

    private static (IReadOnlyList<OcrLanguage>, IReadOnlyDictionary<string, EngineComponent>) Load()
    {
        using var stream = typeof(OcrLanguages).Assembly.GetManifestResourceStream("Filee.Engines.ocr-languages.json")
                           ?? throw new InvalidOperationException("Embedded ocr-languages.json is missing.");
        using var json = JsonDocument.Parse(stream);
        var url_template = json.RootElement.GetProperty("url").GetString()!;
        var languages = new List<OcrLanguage>();
        var components = new Dictionary<string, EngineComponent>();
        foreach (var entry in json.RootElement.GetProperty("languages").EnumerateObject())
        {
            var size = entry.Value.GetProperty("size").GetInt64();
            languages.Add(new OcrLanguage(entry.Name, entry.Value.GetProperty("culture").GetString()!, size));
            // A language may pin its own source (ara reads tessdata_best, the rest tessdata_fast).
            var url = entry.Value.TryGetProperty("url", out var own) ? own.GetString()! : url_template;
            components[entry.Name] = new EngineComponent(entry.Name, Version, string.Format(CultureInfo.InvariantCulture, url, entry.Name),
                entry.Value.GetProperty("sha256").GetString()!, size, "traineddata");
        }
        return (languages, components);
    }
}
