// Setup speaks Filee's languages from the app's own translation files.

extern alias app;
using System.Text.Json;
using System.Text.RegularExpressions;
using Filee.Engines.Infrastructure;
using Filee.Setup.Platform;
using LocalizationService = app::Filee.App.Services.LocalizationService;

namespace Filee.Setup.Tests;

public partial class SetupTextsTests
{
    private static string Repository
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(dir, "Filee.slnx")))
                dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("Repository root not found.");
            return dir;
        }
    }

    [Fact]
    public void Setup_offers_every_language_filee_has()
    {
        Assert.Equal(LocalizationService.Languages.Select(l => (l.Code, l.NativeName)), SetupTexts.Languages);
        Assert.Contains(SetupTexts.Languages, l => l.Code == "ja");
        Assert.Contains(("ar", "العربية"), SetupTexts.Languages);
    }

    [Theory]
    [InlineData("ko", "en-US", "ko")]
    [InlineData("ar-IQ", "en-US", "ar")]
    [InlineData("AR_sa", "en-US", "ar")]
    [InlineData("auto", "ar-IQ", "ar")]
    [InlineData("zhcn", "en-US", "zh-CN")]
    [InlineData("ZH-cn", "en-US", "zh-CN")]
    [InlineData("auto", "ko-KR", "ko")]
    [InlineData(null, "zh-TW", "zh-CN")]
    [InlineData(null, "es-MX", "es")]
    [InlineData(null, "pt-BR", "pt-BR")]
    [InlineData(null, "pt-PT", "pt-BR")]
    [InlineData("pt-BR", "en-US", "pt-BR")]
    [InlineData(null, "pl-PL", "pl")]
    [InlineData(null, "hi-IN", "hi")]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData("xx", "de-DE", "en")]
    [InlineData("klingon", "pl-PL", "pl")]
    public void Languages_resolve_like_the_app_and_like_inno_setup(string? setting, string windows, string expected) =>
        Assert.Equal(expected, SetupTexts.Resolve(setting, () => windows));

    [Fact]
    public void Texts_fall_back_to_english_and_format()
    {
        var texts = new SetupTexts("ko");
        Assert.Equal("ko", texts.Language);
        Assert.NotEqual("installer.install", texts["installer.install"]);
        Assert.Contains("9.9.9", texts.Format("installer.welcome.fresh", "9.9.9"));
        Assert.Equal("no.such.key", texts["no.such.key"]);
        var changed = 0;
        texts.LanguageChanged += (_, _) => changed++;
        texts.SetLanguage("en");
        Assert.Equal(1, changed);
        Assert.Equal("Install", texts["installer.install"]);
    }

    [Fact]
    public void Every_key_setup_uses_exists_in_english()
    {
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(Repository, "src", "Filee.App", "Assets", "i18n", "en.json")))!;
        var source = Path.Combine(Repository, "src", "Filee.Setup");
        var used = Directory.EnumerateFiles(source, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => DynamicResource().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Concat(Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => KeyLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)))
            .Where(k => k.Contains('.') && !k.EndsWith('.') && !k.StartsWith("Setup.", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        Assert.Contains("installer.welcome.fresh", used);
        Assert.Contains("general.context_menu", used);
        Assert.DoesNotContain(used, k => !english.ContainsKey(k));
        // Engine names come from the app's own keys.
        Assert.All(EngineDownloads.Packages, p => Assert.True(english.ContainsKey($"engines.package.{p.Id}.name"), p.Id));
    }

    [Fact]
    public void Chinese_installer_texts_use_no_fullwidth_colon_or_semicolon()
    {
        var chinese = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(Repository, "src", "Filee.App", "Assets", "i18n", "zh-CN.json")))!;
        Assert.DoesNotContain(chinese, p => p.Key.StartsWith("installer.", StringComparison.Ordinal) && (p.Value.Contains('：') || p.Value.Contains('；')));
    }

    [Fact]
    public void Setup_looks_for_downloaded_engines_where_the_app_puts_them()
    {
        Assert.Equal(EngineEnvironment.DownloadRoot, SetupPaths.Real().EnginesFolder);
        Assert.Equal(Filee.Core.Settings.UserDataStore.DefaultDirectory, SetupPaths.Real().SettingsFolder);
    }

    [GeneratedRegex(@"\{DynamicResource ([\w.]+)\}")]
    private static partial Regex DynamicResource();

    [GeneratedRegex(@"""((?:installer|setup|engines|general|common)\.[\w.]+)""")]
    private static partial Regex KeyLiteral();
}
