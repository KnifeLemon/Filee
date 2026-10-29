// Setup speaks Filee's languages from the app's own translation files.

extern alias app;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
        Assert.Contains(("tr", "Türkçe"), SetupTexts.Languages);
        Assert.Equal("Kur", new SetupTexts("tr")["installer.install"]);
    }

    [Theory]
    [InlineData("tr", "en-US", "tr")]
    [InlineData("TR", "en-US", "tr")]
    [InlineData("auto", "tr-TR", "tr")]
    [InlineData(null, "tr-CY", "tr")]
    [InlineData(null, "tru", "en")]
    [InlineData("ko", "en-US", "ko")]
    [InlineData("ar-IQ", "en-US", "ar")]
    [InlineData("AR_sa", "en-US", "ar")]
    [InlineData("auto", "ar-IQ", "ar")]
    [InlineData("zhcn", "en-US", "zh-CN")]
    [InlineData("ZH-cn", "en-US", "zh-CN")]
    [InlineData("auto", "ko-KR", "ko")]
    [InlineData(null, "zh-TW", "zh-TW")]
    [InlineData(null, "zh-Hant-TW", "zh-TW")]
    [InlineData(null, "zh-HK", "zh-TW")]
    [InlineData("zh-TW", "en-US", "zh-TW")]
    [InlineData(null, "zh-Hans-CN", "zh-CN")]
    [InlineData(null, "zh-SG", "zh-CN")]
    [InlineData(null, "zh-Hans-TW", "zh-CN")] // an explicit script wins over the region
    [InlineData(null, "es-MX", "es")]
    [InlineData(null, "pt-BR", "pt-BR")]
    [InlineData(null, "pt-PT", "pt-PT")]
    [InlineData("pt-BR", "en-US", "pt-BR")]
    [InlineData(null, "fr-FR", "fr")]
    [InlineData(null, "fr-CA", "fr")]
    [InlineData("fr", "en-US", "fr")]
    [InlineData(null, "pl-PL", "pl")]
    [InlineData(null, "hi-IN", "hi")]
    [InlineData(null, "ne-NP", "ne")]
    [InlineData(null, "th-TH", "th")]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData("xx", "de-DE", "de")]
    [InlineData("de", "en-US", "de")]
    [InlineData("auto", "de-AT", "de")]
    [InlineData("auto", "de-CH", "de")]
    [InlineData("auto", "it-IT", "it")]
    [InlineData("it", "en-US", "it")]
    [InlineData(null, "itl-RU", "en")]
    [InlineData("xx", "sv-SE", "en")]
    [InlineData("klingon", "pl-PL", "pl")]
    [InlineData("auto", "pt-BR", "pt-BR")]
    [InlineData("auto", "pt-PT", "pt-PT")]
    [InlineData("auto", "pt-Latn-BR", "pt-BR")]
    [InlineData("auto", "pt_BR", "pt-BR")]
    [InlineData("auto", "pt-PT-x-br", "pt-PT")]
    [InlineData("auto", "pt-x-br", "pt-PT")]
    [InlineData("auto", "pt-Latn-PT-x-br", "pt-PT")]
    [InlineData("auto", "pt-PT-u-ca-gregory-x-br", "pt-PT")]
    [InlineData("auto", "pt-BR-x-pt", "pt-BR")]
    [InlineData("auto", "pt-Latn-BR-u-ca-gregory", "pt-BR")]
    [InlineData("auto", "pt-u-rg-brzzzz", "pt-PT")]
    [InlineData("auto", "pt", "pt-PT")]
    [InlineData("auto", "pt-AO", "pt-PT")]
    [InlineData("pt-PT", "pt-BR", "pt-PT")]
    [InlineData("PT-br", "pt-PT", "pt-BR")]
    [InlineData("pt_BR", "en-US", "pt-BR")]
    [InlineData("ptbr", "en-US", "pt-BR")]
    [InlineData("ptpt", "en-US", "pt-PT")]
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

    [AvaloniaFact]
    public void Publishing_a_language_switch_raises_one_resource_change()
    {
        var app = Application.Current!;
        Assert.IsType<ResourceDictionary>(app.Resources); // Setup publishes to this dictionary in ApplyLook.
        var resources = new ResourceDictionary();
        resources["unrelated"] = "kept";
        var texts = new SetupTexts("en");
        texts.Publish(resources);
        app.Resources.MergedDictionaries.Add(resources);
        var changes = 0;
        void Count(object? sender, ResourcesChangedEventArgs e) => changes++;
        app.ResourcesChanged += Count;
        try
        {
            texts.SetLanguage("ko");
            texts.Publish(resources);

            Assert.Equal(1, changes);
            Assert.True(resources.TryGetResource("installer.install", null, out var install));
            Assert.Equal(texts["installer.install"], install);
            Assert.NotEqual("Install", install);
            Assert.True(resources.TryGetResource("general.language", null, out var language));
            Assert.Equal(texts["general.language"], language);
            Assert.Equal("kept", resources["unrelated"]);
        }
        finally
        {
            app.ResourcesChanged -= Count;
            app.Resources.MergedDictionaries.Remove(resources);
        }
    }

    [AvaloniaFact]
    public void A_language_switch_through_apply_look_shows_the_new_texts()
    {
        var app = Application.Current!;
        var texts = new SetupTexts("en");
        App.ApplyLook(app, texts);
        var block = new TextBlock();
        block.Bind(TextBlock.TextProperty, block.GetResourceObservable("installer.install"));
        var window = new Window { Content = block };
        window.Show();
        var changes = 0;
        void Count(object? sender, ResourcesChangedEventArgs e) => changes++;
        try
        {
            Assert.Equal("Install", block.Text);

            app.ResourcesChanged += Count;
            texts.SetLanguage("ko");

            var korean = new SetupTexts("ko")["installer.install"];
            Assert.True(app.TryGetResource("installer.install", null, out var install));
            Assert.Equal(korean, install);
            Assert.Equal(korean, block.Text);
            // The texts are one change; the font and the flow direction add a few more. None of them grows with the
            // number of keys (one change per key was over a thousand).
            Assert.InRange(changes, 1, 10);
        }
        finally
        {
            window.Close();
            app.ResourcesChanged -= Count;
            texts.SetLanguage("en");
        }
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
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The installer uses Windows settings and engine paths.");
        Assert.Equal(EngineEnvironment.DownloadRoot, SetupPaths.Real().EnginesFolder);
        Assert.Equal(Filee.Core.Settings.UserDataStore.DefaultDirectory, SetupPaths.Real().SettingsFolder);
    }

    [GeneratedRegex(@"\{DynamicResource ([\w.]+)\}")]
    private static partial Regex DynamicResource();

    [GeneratedRegex(@"""((?:installer|setup|engines|general|common)\.[\w.]+)""")]
    private static partial Regex KeyLiteral();
}
