using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class LocalizationResourceTests
{
    [AvaloniaFact]
    public void Arabic_font_chain_draws_trimming_ellipsis_and_matches_arabic_letters()
    {
        TestServices.EnsureInitialized("ar");
        try
        {
            var family = FontFamily.Parse(ThemeService.FontFamilies("ar"));
            // Avalonia takes the trimming symbol from the primary typeface rather than applying character fallback.
            Assert.NotEqual((ushort)0, new Typeface(family).GlyphTypeface.CharacterToGlyphMap.GetGlyph(0x2026));
            Assert.True(FontManager.Current.TryMatchCharacter(0x0645, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, family, System.Globalization.CultureInfo.GetCultureInfo("ar"), out var arabic));
            Assert.NotEqual((ushort)0, arabic.GlyphTypeface.CharacterToGlyphMap.GetGlyph(0x0645));
        }
        finally { TestServices.EnsureInitialized("en"); }
    }

    /// <summary>What a piece of text is drawn in, run by run, as a TextBlock lays it out (it passes no culture).</summary>
    private static List<(string Text, string Family)> FontsDrawing(string text, string language, FontWeight weight = FontWeight.Normal)
    {
        var family = FontFamily.Parse(ThemeService.FontFamilies(language));
        using var layout = new TextLayout(text, new Typeface(family, FontStyle.Normal, weight), 14, Brushes.Black);
        return [.. layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>()
            .Select(r => (r.Text.ToString(), r.GlyphRun.GlyphTypeface.FamilyName))];
    }

    /// <summary>
    /// #1095: these Han characters differ between the regions and every embedded CJK font has them, so only the font
    /// chain's order can pick the font. Before, it was Noto Sans JP in every language.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("zh-CN", "Noto Sans SC")]
    [InlineData("ja", "Noto Sans JP")]
    [InlineData("ko", "Noto Sans KR")]
    [InlineData("en", "Noto Sans KR")]
    public void Han_characters_take_the_embedded_font_of_the_ui_languages_region(string language, string font)
    {
        Assert.SkipUnless(AssetLoader.Exists(new Uri("avares://Filee/Assets/Fonts/NotoSansSC-Regular.otf")), "Needs build/fetch-fonts.ps1.");
        TestServices.EnsureInitialized(language);
        try
        {
            var family = FontFamily.Parse(ThemeService.FontFamilies(language));
            foreach (var character in "将系与所")
            {
                Assert.True(FontManager.Current.TryMatchCharacter(character, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, family, null, out var typeface));
                Assert.Equal(font, typeface.GlyphTypeface.FamilyName);
            }
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
                Assert.Equal([("Save ", "Noto Sans"), ("将系与所", font)],
                    FontsDrawing("Save 将系与所", language, weight));
        }
        finally { TestServices.EnsureInitialized("en"); }
    }

    /// <summary>#1095's strings: 跟随系 stayed Japanese up to 统, the first character Noto Sans JP doesn't have.</summary>
    [AvaloniaTheory]
    [InlineData("将文件拖放到这里")]
    [InlineData("跟随系统")]
    [InlineData("与源文件相同的文件夹")]
    [InlineData("所有转换都保留原文件日期")]
    public void Simplified_chinese_text_is_one_run_of_noto_sans_sc(string text)
    {
        Assert.SkipUnless(AssetLoader.Exists(new Uri("avares://Filee/Assets/Fonts/NotoSansSC-Regular.otf")), "Needs build/fetch-fonts.ps1.");
        TestServices.EnsureInitialized("zh-CN");
        try
        {
            Assert.Equal([(text, "Noto Sans SC")], FontsDrawing(text, "zh-CN"));
        }
        finally { TestServices.EnsureInitialized("en"); }
    }

    /// <summary>
    /// #1095: the only embedded font zh-TW may land on is Noto Sans SC, and that only on a machine with no Traditional
    /// one (macOS answers PingFang TC here, Windows Microsoft JhengHei UI).
    /// </summary>
    [AvaloniaFact]
    public void Traditional_chinese_text_reaches_the_system_traditional_font()
    {
        Assert.SkipUnless(AssetLoader.Exists(new Uri("avares://Filee/Assets/Fonts/NotoSansSC-Regular.otf")), "Needs build/fetch-fonts.ps1.");
        TestServices.EnsureInitialized("zh-TW");
        try
        {
            string[] allowed = ["Microsoft JhengHei UI", "Microsoft JhengHei", "PingFang TC", "Noto Sans CJK TC", "Noto Sans SC"];
            Assert.All(FontsDrawing("將文件體", "zh-TW"), run => Assert.Contains(run.Family, allowed));
        }
        finally { TestServices.EnsureInitialized("en"); }
    }

    [AvaloniaFact]
    public void Arabic_mirrors_the_main_window_and_english_brings_it_back()
    {
        TestServices.EnsureInitialized("en");
        using var model = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        model.Navigate("general");
        var window = new MainWindow { DataContext = model, Width = 1080, Height = 820 };
        window.Show();
        try
        {
            RenderTests.Pump();
            var navigation = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("nav"));
            var page = window.FindControl<ScrollViewer>("PageScroller")!;
            double Left(Visual visual) => new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(window)!.Value).Left;
            double Center(Visual visual) =>
                new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(window)!.Value).Center.X;
            Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
            Assert.True(Center(navigation) < Center(page));
            var englishNavigationLeft = Left(navigation);

            TestServices.EnsureInitialized("ar");
            RenderTests.Pump();
            Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
            Assert.Equal(FlowDirection.RightToLeft, page.FlowDirection);
            // The sidebar moves to the right edge, the page to its left.
            Assert.True(Center(navigation) > Center(page));
            Assert.Equal(window.Bounds.Width - englishNavigationLeft, Left(navigation) + navigation.Bounds.Width, 1);
            Assert.Equal("عام", AppHost.Get<ILocalizer>()["general.title"]);

            TestServices.EnsureInitialized("en");
            RenderTests.Pump();
            Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
            Assert.Equal(englishNavigationLeft, Left(navigation), 1);
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void Arabic_windows_read_right_to_left_but_pictures_and_the_donut_do_not_mirror()
    {
        TestServices.EnsureInitialized("ar");
        var canvas = new CropCanvas();
        var donut = new DonutMenu();
        var picture = new AnimatedImage();
        var window = new Window { Content = new StackPanel { Children = { new TextBlock(), canvas, donut, picture } } };
        window.Show();
        try
        {
            Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
            Assert.Equal(FlowDirection.RightToLeft, ((StackPanel)window.Content).Children[0].FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, canvas.FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, donut.FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, picture.FlowDirection);
            Assert.True(Application.Current!.TryGetResource("general.language", null, out var value));
            Assert.Equal("اللغة", value);

            TestServices.EnsureInitialized("en");
            Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void Strings_are_available_as_application_resources()
    {
        TestServices.EnsureInitialized("ko");
        Assert.True(Application.Current!.TryGetResource("home.title", null, out var value));
        Assert.Equal("홈", value);
    }

    [AvaloniaFact]
    public void DynamicResource_in_xaml_resolves_and_follows_language_changes()
    {
        TestServices.EnsureInitialized("en");
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, text.GetResourceObservable("home.title"));
        var toggle = new ToggleSwitch();
        var window = new Window { Content = new StackPanel { Children = { text, toggle } } };
        window.Show();
        Assert.Equal("Home", text.Text);

        TestServices.EnsureInitialized("ko");
        Assert.Equal("홈", text.Text);
        TestServices.EnsureInitialized("ja");
        Assert.Equal("ホーム", text.Text);
        Assert.Equal("オン", toggle.OnContent);
        Assert.Equal("オフ", toggle.OffContent);
        TestServices.EnsureInitialized("de");
        Assert.Equal("Start", text.Text);
        Assert.Equal("Ein", toggle.OnContent);
        Assert.Equal("Aus", toggle.OffContent);
        window.Close();
    }

    /// <summary>
    /// A language switch publishes its strings in one resource change: a change per key reached every control of every
    /// window still alive, and made the later App tests minutes slower (#540).
    /// </summary>
    [AvaloniaFact]
    public void A_language_switch_publishes_its_strings_in_one_resource_change()
    {
        TestServices.EnsureInitialized("en");
        var app = Application.Current!;
        var changes = 0;
        void Count(object? sender, ResourcesChangedEventArgs e) => changes++;
        app.ResourcesChanged += Count;
        try
        {
            AppHost.Get<LocalizationService>().SetLanguage("ko");
            Assert.Equal(1, changes);
            Assert.True(app.TryGetResource("home.title", null, out var title));
            Assert.Equal("홈", title);
        }
        finally
        {
            app.ResourcesChanged -= Count;
            TestServices.EnsureInitialized("en");
        }
    }

    /// <summary>A switch before the dictionary is attached still takes effect, and attaching later shows the last language.</summary>
    [AvaloniaFact]
    public void A_language_set_before_attaching_shows_once_attached()
    {
        TestServices.EnsureInitialized("en");
        var app = Application.Current!;
        var merged = app.Resources.MergedDictionaries;
        var count = merged.Count;
        var localization = new LocalizationService();
        var changed = 0;
        localization.LanguageChanged += (_, _) => changed++;
        try
        {
            localization.SetLanguage("ko");
            Assert.Equal("ko", localization.Language);
            Assert.Equal("ko", System.Globalization.CultureInfo.CurrentUICulture.Name);
            Assert.Equal("홈", localization["home.title"]);
            Assert.Equal(1, changed);

            localization.Attach(app);
            Assert.Equal(count + 1, merged.Count);
            Assert.True(app.TryGetResource("home.title", null, out var title));
            Assert.Equal("홈", title);
        }
        finally
        {
            while (merged.Count > count)
                merged.RemoveAt(merged.Count - 1);
            TestServices.EnsureInitialized("en");
        }
    }
}
