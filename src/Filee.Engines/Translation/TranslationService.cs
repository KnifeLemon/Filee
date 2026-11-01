using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filee.Engines.Translation;

/// <summary>
/// Offline document and text translation service powered by local AI (Ollama).
/// Strictly runs on the user's computer: no text is ever uploaded or sent to third-party web services.
/// </summary>
public sealed class TranslationService : ITranslationService
{
    private static readonly TimeSpan OllamaTimeout = TimeSpan.FromMinutes(2);
    private static readonly Uri LocalOllamaBase = new("http://127.0.0.1:11434");

    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = OllamaTimeout
    };

    private static readonly HttpClient SharedPingClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromMilliseconds(500)
    })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly TranslateOptions _options;
    private readonly ILogger? _logger;
    private readonly HttpClient? _http;
    private string? _resolvedModel;

    public TranslationService(TranslateOptions? options = null, HttpClient? httpClient = null, ILogger? logger = null)
    {
        _options = options ?? new TranslateOptions();
        _http = httpClient;
        _logger = logger;
    }

    public static string NormalizeLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return "en";
        var trimmed = lang.Trim();
        return LanguageMap.TryGetValue(trimmed, out var code) ? code : trimmed.ToLowerInvariant();
    }

    public static string LanguageDisplayName(string code)
    {
        var norm = NormalizeLanguage(code);
        return CodeToNameMap.TryGetValue(norm, out var name) ? name : code;
    }

    /// <summary>
    /// Checks whether an Ollama model forwards prompts to the cloud (e.g. models ending in '-cloud' or containing 'cloud').
    /// Cloud models are strictly prohibited to ensure document text never leaves the computer.
    /// </summary>
    public static bool IsCloudModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return false;
        var name = modelName.Trim();
        return name.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-cloud:", StringComparison.OrdinalIgnoreCase)
            || name.Contains("/cloud", StringComparison.OrdinalIgnoreCase)
            || name.IndexOf("cloud", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsCloudModelElement(JsonElement modelElement)
    {
        if (modelElement.TryGetProperty("name", out var n) && IsCloudModel(n.GetString()))
            return true;
        if (modelElement.TryGetProperty("remote_host", out var host) && !string.IsNullOrWhiteSpace(host.GetString()))
            return true;
        if (modelElement.TryGetProperty("remote_model", out var rModel) && !string.IsNullOrWhiteSpace(rModel.GetString()))
            return true;
        if (modelElement.TryGetProperty("details", out var details) && details.TryGetProperty("parent_model", out var parent) && IsCloudModel(parent.GetString()))
            return true;
        return false;
    }

    public static async Task<bool> IsLocalOllamaAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            using var res = await SharedPingClient.GetAsync(new Uri(LocalOllamaBase, "/api/tags"), cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string?> GetPreferredLocalOllamaModelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var res = await SharedPingClient.GetAsync(new Uri(LocalOllamaBase, "/api/tags"), cts.Token);
            if (!res.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                var names = models.EnumerateArray()
                    .Where(m => !IsCloudModelElement(m))
                    .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToList();

                if (names.Count == 0)
                    return null;

                // Prefer specialized or standard instruction models
                string[] preferred = ["translategemma", "qwen2.5", "llama3.2", "llama3", "mistral", "gemma2", "phi3", "phi4"];
                foreach (var p in preferred)
                {
                    var match = names.FirstOrDefault(n => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                        return match;
                }
                return names[0];
            }
        }
        catch
        {
            // Ignore connection errors
        }
        return null;
    }

    private async Task<string> EnsureModelResolvedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_resolvedModel))
            return _resolvedModel;

        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            if (IsCloudModel(_options.Model) || await CheckModelRemoteCloudAsync(_options.Model, cancellationToken))
                throw new ConversionRefusedException("error.translation_cloud_model_forbidden");
            _resolvedModel = _options.Model;
            return _resolvedModel;
        }

        var preferred = await GetPreferredLocalOllamaModelAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(preferred))
            throw new ConversionRefusedException("engines.package.translator.no_model");

        if (IsCloudModel(preferred) || await CheckModelRemoteCloudAsync(preferred, cancellationToken))
            throw new ConversionRefusedException("error.translation_cloud_model_forbidden");

        _resolvedModel = preferred;
        return _resolvedModel;
    }

    private async Task<bool> CheckModelRemoteCloudAsync(string modelName, CancellationToken cancellationToken)
    {
        try
        {
            var client = _http ?? SharedHttpClient;
            var body = new { model = modelName };
            using var res = await client.PostAsJsonAsync(new Uri(LocalOllamaBase, "/api/show"), body, cancellationToken);
            if (!res.IsSuccessStatusCode)
                return true;

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return IsCloudModelElement(doc.RootElement);
        }
        catch
        {
            return true;
        }
    }

    public async Task<string> TranslateTextAsync(string text, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var targetLang = NormalizeLanguage(toLang);
        var sourceLang = string.IsNullOrWhiteSpace(fromLang) || fromLang.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto" : NormalizeLanguage(fromLang);

        if (string.Equals(sourceLang, targetLang, StringComparison.OrdinalIgnoreCase))
            return text;

        var model = await EnsureModelResolvedAsync(cancellationToken);
        return await TranslateWithLocalOllamaAsync(text, targetLang, sourceLang, model, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
            return [];

        await EnsureModelResolvedAsync(cancellationToken);
        var results = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await TranslateTextAsync(text, toLang, fromLang, cancellationToken));
        }
        return results;
    }

    private async Task<string> TranslateWithLocalOllamaAsync(string text, string targetLang, string sourceLang, string model, CancellationToken cancellationToken)
    {
        var targetName = LanguageDisplayName(targetLang);
        var system = $"You are a professional book and document translator. Translate the given text into {targetName}.\n" +
                     "Rules:\n" +
                     "- Preserve all formatting tags (such as <b>, <i>, <a>, <div>, etc.) and slot tags (<s0>, <s1>, etc.) exactly.\n" +
                     "- Maintain the exact same paragraph and line structure.\n" +
                     "- Output ONLY the translated text without commentary, intro, rules, or explanation.";

        var requestBody = new
        {
            model,
            system,
            prompt = text,
            stream = false,
            think = false,
            options = new { temperature = 0.1 }
        };

        var client = _http ?? SharedHttpClient;
        using var response = await client.PostAsJsonAsync(new Uri(LocalOllamaBase, "/api/generate"), requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (json.RootElement.TryGetProperty("response", out var resp) && resp.GetString() is { } result && !string.IsNullOrWhiteSpace(result))
        {
            return StripThinkTags(result).Trim();
        }

        throw new ConversionRefusedException("error.translation_empty_response");
    }

    private static string StripThinkTags(string text) =>
        Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();

    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["th"] = "th",
        ["thai"] = "th",
        ["ไทย"] = "th",
        ["id"] = "id",
        ["indonesian"] = "id",
        ["bahasa indonesia"] = "id",
        ["en"] = "en",
        ["english"] = "en",
        ["es"] = "es",
        ["spanish"] = "es",
        ["español"] = "es",
        ["fr"] = "fr",
        ["french"] = "fr",
        ["français"] = "fr",
        ["de"] = "de",
        ["german"] = "de",
        ["deutsch"] = "de",
        ["zh"] = "zh",
        ["chinese"] = "zh",
        ["zh-cn"] = "zh",
        ["zh-tw"] = "zh",
        ["hi"] = "hi",
        ["hindi"] = "hi",
        ["हिन्दी"] = "hi",
        ["ja"] = "ja",
        ["japanese"] = "ja",
        ["日本語"] = "ja",
        ["ko"] = "ko",
        ["korean"] = "ko",
        ["한국어"] = "ko",
        ["ru"] = "ru",
        ["russian"] = "ru",
        ["русский"] = "ru",
        ["pt"] = "pt",
        ["portuguese"] = "pt",
        ["português"] = "pt",
        ["ar"] = "ar",
        ["arabic"] = "ar",
        ["العربية"] = "ar",
        ["ne"] = "ne",
        ["nepali"] = "ne",
        ["नेपाली"] = "ne",
        ["it"] = "it",
        ["italian"] = "it",
        ["italiano"] = "it",
        ["nl"] = "nl",
        ["dutch"] = "nl",
        ["nederlands"] = "nl",
        ["pl"] = "pl",
        ["polish"] = "pl",
        ["polski"] = "pl",
        ["tr"] = "tr",
        ["turkish"] = "tr",
        ["türkçe"] = "tr"
    };

    private static readonly Dictionary<string, string> CodeToNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["de"] = "German",
        ["zh"] = "Chinese",
        ["hi"] = "Hindi",
        ["ja"] = "Japanese",
        ["ko"] = "Korean",
        ["ru"] = "Russian",
        ["pt"] = "Portuguese",
        ["ar"] = "Arabic",
        ["ne"] = "Nepali",
        ["it"] = "Italian",
        ["nl"] = "Dutch",
        ["pl"] = "Polish",
        ["tr"] = "Turkish",
        ["th"] = "Thai",
        ["id"] = "Indonesian"
    };
}
