// Theme → Use the system font (#1065): the system's UI fonts go ahead of the embedded Noto ones, which stay behind them
// for the scripts the system's lack, and Han keeps the UI language's shapes (#1121).

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class SystemFontTests
{
    public static TheoryData<string> AllLanguages => new(LocalizationService.Languages.Select(l => l.Code));

    /// <summary>Each CJK region's fonts, embedded and system (Windows and macOS).</summary>
    private static readonly Dictionary<string, string[]> CjkFonts = new()
    {
        ["ja"] = ["Noto Sans JP", "Yu Gothic UI", "Meiryo", "Hiragino Sans"],
        ["ko"] = ["Noto Sans KR", "Malgun Gothic", "Apple SD Gothic Neo"],
        ["zh-CN"] = ["Noto Sans SC", "Microsoft YaHei", "PingFang SC"],
        ["zh-TW"] = ["Microsoft JhengHei UI", "Microsoft JhengHei", "PingFang TC", "Noto Sans CJK TC"],
    };

    private static List<string> Chain(string language, bool systemFont) =>
        ThemeService.FontFamilies(language, systemFont).Split(", ").Select(f => f.Replace("fonts:Filee#", "").Replace("fonts:System#", "")).ToList();

    /// <summary>
    /// Asked by name: the system collection lists a family under its name in the system's language ("맑은 고딕"), and
    /// only once it has been read.
    /// </summary>
    private static bool Installed(string family) =>
        FontManager.Current.SystemFonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var face)
        && face.FamilyName.StartsWith(family.Replace(" Text", "").Replace(" JhengHei UI", " JhengHei"), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void The_embedded_chain_stays_whole_behind_the_system_fonts(string language)
    {
        var embedded = ThemeService.FontFamilies(language);
        var system = ThemeService.FontFamilies(language, systemFont: true);

        Assert.EndsWith(embedded, system);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.False(system.StartsWith("fonts:Filee#", StringComparison.Ordinal), system);
    }

    /// <summary>The first font that draws Han is still one of the UI language's (Korean for the others).</summary>
    [Theory]
    [InlineData("ja", "ja")]
    [InlineData("ko", "ko")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("en", "ko")]
    [InlineData("ar", "ko")]
    public void Han_keeps_the_ui_languages_shapes_with_the_system_font(string language, string region)
    {
        var families = Chain(language, systemFont: true);
        var allCjk = CjkFonts.Values.SelectMany(f => f).ToHashSet();

        Assert.Contains(families.First(allCjk.Contains), CjkFonts[region]);
    }

    /// <summary>What Windows' own UI draws each script in; skipped where the machine lacks that font.</summary>
    [AvaloniaTheory]
    [InlineData("en", 'A', "Segoe UI Variable Text", "Segoe UI")]
    [InlineData("ar", 'م', "Segoe UI", null)]
    [InlineData("hi", 'न', "Nirmala UI", null)]
    [InlineData("th", 'ก', "Leelawadee UI", null)]
    [InlineData("ja", '直', "Yu Gothic UI", null)] // 直 has a different shape in each region's font
    [InlineData("ko", '直', "Malgun Gothic", null)]
    [InlineData("en", '直', "Malgun Gothic", null)]
    [InlineData("zh-CN", '直', "Microsoft YaHei", null)]
    [InlineData("zh-TW", '直', "Microsoft JhengHei UI", null)]
    public void On_windows_a_letter_takes_the_systems_font(string language, char letter, string expected, string? orElse)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' UI fonts.");
        TestServices.EnsureInitialized("en");
        var want = Installed(expected) ? expected : orElse;
        Assert.SkipUnless(want is not null && Installed(want), $"{expected} isn't installed.");

        foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold })
        {
            Assert.True(FontManager.Current.TryMatchCharacter(letter, FontStyle.Normal, weight, FontStretch.Normal,
                SoftPalette.FontFamilyFor(language, systemFont: true), null, out var typeface));
            // Windows names a weight's face by its own family ("Segoe UI Semibold"), and a font by the family of its
            // file ("Segoe UI Variable", "Microsoft JhengHei").
            var stem = want.Replace(" Text", "").Replace(" JhengHei UI", " JhengHei");
            Assert.StartsWith(stem, typeface.GlyphTypeface.FamilyName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_chain_names_the_system_fonts_by_the_collections_key() =>
        Assert.Equal(SystemUiFontCollection.KeyName, SoftPalette.SystemKey);

    /// <summary>Avalonia's own system collection answers Han in Yu Gothic UI whatever family is asked (#1065).</summary>
    [AvaloniaFact]
    public void A_letter_is_only_matched_in_the_system_family_asked_for()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' UI fonts.");
        TestServices.EnsureInitialized("en");
        Assert.SkipUnless(Installed("Segoe UI") && Installed("Malgun Gothic"), "Segoe UI and Malgun Gothic.");
        using var fonts = new SystemUiFontCollection();

        Assert.True(fonts.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, "Segoe UI", null, out _));
        Assert.False(fonts.TryMatchCharacter('直', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, "Segoe UI", null, out _));
        Assert.True(fonts.TryMatchCharacter('直', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, "Malgun Gothic", null, out var typeface));
        Assert.Equal("Malgun Gothic", typeface.GlyphTypeface.FamilyName);
        Assert.False(fonts.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, "Not a font", null, out _));
    }

    [AvaloniaFact]
    public void The_switch_is_saved_and_puts_the_system_font_into_the_ui()
    {
        TestServices.EnsureInitialized("en");
        var directory = Directory.CreateTempSubdirectory("filee-theme-").FullName;
        var store = new UserDataStore(directory);
        store.Load();
        var vm = new ThemePageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>());
        try
        {
            Assert.False(vm.UseSystemFont);
            vm.UseSystemFont = true;
            Assert.True(store.Settings.Theme.UseSystemFont);
            var reloaded = new UserDataStore(directory);
            reloaded.Load();
            Assert.True(reloaded.Settings.Theme.UseSystemFont);

            AppHost.Get<ThemeService>().Apply(store.Settings.Theme, "en");
            Assert.Equal(SoftPalette.FontFamilyFor("en", systemFont: true), Application.Current!.Resources["AppFontFamily"]);

            vm.UseSystemFont = false;
            AppHost.Get<ThemeService>().Apply(store.Settings.Theme, "en");
            Assert.Equal(SoftPalette.FontFamilyFor("en"), Application.Current!.Resources["AppFontFamily"]);
        }
        finally
        {
            vm.Dispose();
            TestServices.EnsureInitialized("en");
            Directory.Delete(directory, recursive: true);
        }
    }
}
