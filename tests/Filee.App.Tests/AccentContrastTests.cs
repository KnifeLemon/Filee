// What sits on an accent fill reads on that fill (#1148): one label, white or dark by WCAG contrast, for presets, custom
// accents and Material You, in light and dark, at rest, hovered and pressed, and it follows the accent in an open window.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class AccentContrastTests
{
    private const string Monochrome = "monochrome";
    private static readonly Color DarkestLabel = Color.Parse("#16141A");

    /// <summary>A fill and what Fluent draws on it, fixed white unless the theme sets it.</summary>
    private static readonly (string Fill, string Label)[] FluentPairs =
    [
        ("AccentButtonBackground", "AccentButtonForeground"),
        ("ToggleButtonBackgroundChecked", "ToggleButtonForegroundChecked"),
        ("CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckGlyphForegroundChecked"),
        ("CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckGlyphForegroundIndeterminate"),
        ("RadioButtonOuterEllipseCheckedFill", "RadioButtonCheckGlyphFill"),
        ("ToggleSwitchFillOn", "ToggleSwitchKnobFillOn"),
    ];

    private static readonly string[] States = ["", "PointerOver", "Pressed"];

    /// <summary>Every preset and swatch, and the extremes a custom colour can be: near white, yellow, very dark, black.</summary>
    private static IEnumerable<ThemeSettings> Accents()
    {
        foreach (var preset in ThemeService.Presets)
            yield return new ThemeSettings { Accent = preset.LightHex, PresetKey = preset.Key };
        foreach (var hex in ThemeService.Swatches.Concat(["#FFFFFF", "#F1EFE8", "#FFFF00", "#101018", "#000000", "#1F1E1D"]))
            yield return new ThemeSettings { Accent = hex };
    }

    private static IEnumerable<(ThemeSettings Theme, ThemeVariant Variant)> EveryLook()
    {
        foreach (var style in new[] { ThemeStyle.Soft, ThemeStyle.MaterialYou })
            foreach (var accent in Accents())
                foreach (var dark in new[] { false, true })
                {
                    yield return (
                        new ThemeSettings { Style = style, Accent = accent.Accent, PresetKey = accent.PresetKey, Mode = dark ? ThemeMode.Dark : ThemeMode.Light },
                        dark ? ThemeVariant.Dark : ThemeVariant.Light);
                }
    }

    private static string Name(ThemeSettings theme, ThemeVariant variant) => $"{theme.Style} {theme.PresetKey ?? theme.Accent} {variant}";

    private static ThemeService Theme()
    {
        TestServices.EnsureInitialized("en");
        return AppHost.Get<ThemeService>();
    }

    private static ISolidColorBrush Brush(string key, ThemeVariant variant)
    {
        var window = new Window();
        Assert.True(window.TryFindResource(key, variant, out var value), $"{key} not found");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value);
    }

    private static Color ColorOf(string key, ThemeVariant variant) => Brush(key, variant).Color;

    [AvaloniaFact]
    public void Every_accent_control_takes_the_label_with_the_higher_contrast_in_every_state()
    {
        var service = Theme();
        try
        {
            foreach (var (theme, variant) in EveryLook())
            {
                service.Apply(theme, "en");
                var name = Name(theme, variant);
                var accent = ColorOf("AccentBrush", variant);
                var label = ColorOf("OnAccentBrush", variant);
                var ratio = MaterialYouPalette.ContrastRatio(accent, label);

                if (label == Colors.White)
                    Assert.True(ratio >= MaterialYouPalette.ContrastRatio(accent, DarkestLabel), $"{name}: white on {accent} should lose to a dark label");
                else
                    Assert.True(ratio > MaterialYouPalette.ContrastRatio(accent, Colors.White), $"{name}: {label} on {accent} should beat white");

                foreach (var (fillKey, labelKey) in FluentPairs)
                {
                    Assert.Equal(accent, ColorOf(fillKey, variant));
                    foreach (var state in States)
                    {
                        Assert.True(label == ColorOf(labelKey + state, variant), $"{name}: {labelKey + state} is not the label {label}");

                        // Hover and press may move the fill, but not closer to the label than a readable 4.5:1.
                        var fill = ColorOf(fillKey + state, variant);
                        var shifted = MaterialYouPalette.ContrastRatio(fill, label);
                        Assert.True(shifted >= Math.Min(ratio, MaterialYouPalette.MinAccentContrast),
                            $"{name}: {label} on {fillKey + state} {fill} has contrast {shifted:F2}, {ratio:F2} at rest");
                    }
                }
            }
        }
        finally
        {
            service.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaTheory]
    [InlineData(ThemeStyle.Soft, ThemeMode.Dark)]
    [InlineData(ThemeStyle.MaterialYou, ThemeMode.Dark)]
    public void The_monochrome_presets_bright_accent_gets_dark_text_on_its_button(ThemeStyle style, ThemeMode mode)
    {
        var service = Theme();
        try
        {
            service.Apply(new ThemeSettings { Style = style, Mode = mode, Accent = "#1F1E1D", PresetKey = Monochrome }, "en");
            var fill = ColorOf("AccentButtonBackground", ThemeVariant.Dark);
            var text = ColorOf("AccentButtonForeground", ThemeVariant.Dark);
            Assert.Equal(Color.Parse("#F1EFE8"), fill);
            Assert.True(MaterialYouPalette.ContrastRatio(fill, text) >= 7, $"{text} on {fill}");
        }
        finally
        {
            service.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaTheory]
    [InlineData("#101018", "a very dark blue")]
    [InlineData("#000000", "black")]
    [InlineData("#1F1E1D", "charcoal")]
    [InlineData("#5F5E5A", "the grey swatch")]
    [InlineData("#0B57D0", "a deep blue")]
    public void White_stays_on_dark_accents_in_both_themes(string hex, string what)
    {
        var service = Theme();
        try
        {
            service.Apply(new ThemeSettings { Accent = hex }, "en");
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (var state in States)
                    Assert.True(ColorOf("AccentButtonForeground" + state, variant) == Colors.White, $"{what} {variant} {state}");
        }
        finally
        {
            service.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaTheory]
    [InlineData("#FFFFFF")]
    [InlineData("#F1EFE8")]
    [InlineData("#000000")]
    [InlineData("#1F1E1D")]
    public void Hover_and_press_still_show_on_the_lightest_and_darkest_accents(string hex)
    {
        var service = Theme();
        try
        {
            service.Apply(new ThemeSettings { Accent = hex }, "en");
            var rest = ColorOf("AccentButtonBackground", ThemeVariant.Light);
            var hover = ColorOf("AccentButtonBackgroundPointerOver", ThemeVariant.Light);
            var pressed = ColorOf("AccentButtonBackgroundPressed", ThemeVariant.Light);
            Assert.True(MaterialYouPalette.ContrastRatio(rest, hover) >= 1.05, $"hover {hover} on {rest}");
            Assert.True(MaterialYouPalette.ContrastRatio(rest, pressed) >= 1.05, $"pressed {pressed} on {rest}");
            Assert.NotEqual(hover, pressed);
        }
        finally
        {
            service.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void The_label_wins_on_any_colour_and_never_falls_below_the_floor()
    {
        Theme();
        for (var r = 0; r <= 255; r += 15)
            for (var g = 0; g <= 255; g += 15)
                for (var b = 0; b <= 255; b += 15)
                {
                    var accent = Color.FromRgb((byte)r, (byte)g, (byte)b);
                    var res = new ResourceDictionary();
                    SoftPalette.ApplyAccent(res, accent);
                    var label = Assert.IsType<SolidColorBrush>(res["OnAccentBrush"]).Color;
                    var ratio = MaterialYouPalette.ContrastRatio(accent, label);
                    Assert.True(ratio >= MaterialYouPalette.ContrastRatio(accent, Colors.White), $"{label} on {accent}");
                    Assert.True(ratio >= MaterialYouPalette.ContrastRatio(accent, DarkestLabel), $"{label} on {accent}");
                    Assert.True(ratio >= 4.2, $"{label} on {accent} has contrast {ratio:F2}");
                }
    }

    [AvaloniaFact]
    public void A_preset_after_a_custom_accent_is_not_shadowed_by_the_custom_accents_states()
    {
        // A custom accent writes one set at the top level, a preset one per theme: leftovers on top would win in both themes.
        var service = Theme();
        try
        {
            service.Apply(new ThemeSettings { Accent = "#123456", Mode = ThemeMode.Dark }, "en");
            service.Apply(new ThemeSettings { Accent = "#1F1E1D", PresetKey = Monochrome, Mode = ThemeMode.Dark }, "en");
            foreach (var key in SoftPalette.AccentResourceKeys)
                Assert.False(Application.Current!.Resources.ContainsKey(key), $"{key} is still at the top level");

            Assert.True(ColorOf("AccentButtonForeground", ThemeVariant.Dark) != Colors.White);
            Assert.Equal(Colors.White, ColorOf("AccentButtonForeground", ThemeVariant.Light));
        }
        finally
        {
            service.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void An_open_window_follows_the_accent_without_being_reopened()
    {
        var service = Theme();
        var button = new Button { Content = "Save", Classes = { "accent" } };
        var check = new ToggleButton { Content = "Ctrl", IsChecked = true };
        var donut = new DonutMenu { Items = [new DonutItem { Id = "a", Label = "A" }, new DonutItem { Id = "b", Label = "B" }], OuterRadius = 80 };
        var window = new Window { Width = 500, Height = 500, Content = new StackPanel { Children = { button, check, donut } } };
        window.Show();
        try
        {
            foreach (var (theme, label) in new[]
                     {
                         (new ThemeSettings { Accent = "#101018", Mode = ThemeMode.Dark }, Colors.White),
                         (new ThemeSettings { Accent = "#FFFFFF", Mode = ThemeMode.Dark }, DarkestLabel),
                         (new ThemeSettings { Accent = "#1F1E1D", PresetKey = Monochrome, Mode = ThemeMode.Light }, Colors.White),
                         (new ThemeSettings { Accent = "#1F1E1D", PresetKey = Monochrome, Mode = ThemeMode.Dark }, Color.Parse("#1F1E1D")),
                         (new ThemeSettings { Accent = "#000000", Mode = ThemeMode.Dark }, Colors.White),
                         (new ThemeSettings { Accent = "#F1EFE8", Style = ThemeStyle.MaterialYou, Mode = ThemeMode.Light }, Color.Parse("#1F1E1D")),
                     })
            {
                service.Apply(theme, "en");
                RenderTests.Pump();
                var name = Name(theme, ThemeVariant.Default);
                Assert.True(label == Foreground(button), $"{name}: the button's text is {Foreground(button)}, expected {label}");
                Assert.True(label == Foreground(check), $"{name}: the toggle's text is {Foreground(check)}, expected {label}");
                Assert.True(label == Assert.IsAssignableFrom<ISolidColorBrush>(donut.OnAccentBrush).Color, $"{name}: the donut's lit slice");
            }
        }
        finally
        {
            window.Close();
            service.Apply(new ThemeSettings(), "en");
        }

        static Color Foreground(Control control) =>
            Assert.IsAssignableFrom<ISolidColorBrush>(control.GetVisualDescendants().OfType<ContentPresenter>().First().Foreground).Color;
    }

    /// <summary>Text drawn at rest, hovered and pressed: its strongest ink against the fill behind it, in the real pixels.</summary>
    [AvaloniaTheory]
    [InlineData("#1F1E1D", Monochrome, ThemeMode.Dark, ThemeStyle.Soft)]
    [InlineData("#1F1E1D", Monochrome, ThemeMode.Light, ThemeStyle.Soft)]
    [InlineData("#1F1E1D", Monochrome, ThemeMode.Dark, ThemeStyle.MaterialYou)]
    [InlineData("#FFFFFF", null, ThemeMode.Light, ThemeStyle.Soft)]
    [InlineData("#FFFF00", null, ThemeMode.Dark, ThemeStyle.Soft)]
    [InlineData("#101018", null, ThemeMode.Dark, ThemeStyle.Soft)]
    [InlineData("#BCAEE5", null, ThemeMode.Dark, ThemeStyle.Soft)]
    public void A_drawn_accent_button_shows_its_text_in_every_state(string hex, string? preset, ThemeMode mode, ThemeStyle style)
    {
        var service = Theme();
        var variant = mode == ThemeMode.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            service.Apply(new ThemeSettings { Accent = hex, PresetKey = preset, Mode = mode, Style = style }, "en");
            foreach (var state in States)
            {
                var button = new Button
                {
                    Content = "Save Hg",
                    FontSize = 30,
                    FontWeight = FontWeight.Bold,
                    Classes = { "accent" },
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                };
                var window = new Window { Width = 300, Height = 90, Content = button };
                service.ApplyWindowClass(window);
                window.Show();
                RenderTests.Pump();
                var middle = new Point(window.Width / 2, window.Height / 2);
                if (state != "")
                    window.MouseMove(middle);
                if (state == "Pressed")
                    window.MouseDown(middle, MouseButton.Left);
                RenderTests.Pump();

                Assert.Equal(state == "PointerOver" || state == "Pressed", button.IsPointerOver);
                Assert.Equal(state == "Pressed", button.IsPressed);
                var fill = ColorOf("AccentButtonBackground" + state, variant);
                var ink = StrongestInk(button, fill);
                window.Close();
                Assert.True(ink >= 4, $"{hex} {mode} {style} {state}: the text's contrast on {fill} is {ink:F2}");
            }
        }
        finally
        {
            service.Apply(new ThemeSettings(), "en");
        }
    }

    /// <summary>The highest contrast with <paramref name="fill"/> in the control's middle, where the text is.</summary>
    private static double StrongestInk(Control control, Color fill)
    {
        var width = (int)control.Bounds.Width;
        var height = (int)control.Bounds.Height;
        using var image = new RenderTargetBitmap(new PixelSize(width, height));
        image.Render(control);
        var pixels = new byte[width * height * 4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { image.CopyPixels(new PixelRect(0, 0, width, height), pin.AddrOfPinnedObject(), pixels.Length, width * 4); }
        finally { pin.Free(); }

        var strongest = 1.0;
        for (var y = height / 4; y < height * 3 / 4; y++)
            for (var x = width / 4; x < width * 3 / 4; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i + 3] == 255)
                    strongest = Math.Max(strongest, MaterialYouPalette.ContrastRatio(fill, Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i])));
            }
        return strongest;
    }

    [AvaloniaFact]
    public void Selected_rows_stay_a_tint_their_text_reads_on_with_any_accent()
    {
        var service = Theme();
        (string Fill, string Text)[] rows =
        [
            ("ComboBoxItemBackgroundSelected", "ComboBoxItemForegroundSelected"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemForegroundSelectedPointerOver"),
            ("ComboBoxItemBackgroundSelectedPressed", "ComboBoxItemForegroundSelectedPressed"),
            ("SystemControlHighlightListAccentLowBrush", "SystemControlHighlightAltBaseHighBrush"),
            ("SystemControlHighlightListAccentMediumBrush", "SystemControlHighlightAltBaseHighBrush"),
            ("SystemControlHighlightListAccentHighBrush", "SystemControlHighlightAltBaseHighBrush"),
        ];
        try
        {
            foreach (var (theme, variant) in EveryLook())
            {
                service.Apply(theme, "en");
                var surface = ColorOf("SurfaceBrush", variant);
                foreach (var (fillKey, textKey) in rows)
                {
                    var fill = Brush(fillKey, variant);
                    var alpha = fill.Opacity * fill.Color.A / 255;
                    byte Over(byte top, byte under) => (byte)Math.Round(top * alpha + under * (1 - alpha));
                    var shown = Color.FromRgb(Over(fill.Color.R, surface.R), Over(fill.Color.G, surface.G), Over(fill.Color.B, surface.B));
                    var text = ColorOf(textKey, variant);
                    var ratio = MaterialYouPalette.ContrastRatio(shown, text);
                    Assert.True(ratio >= MaterialYouPalette.MinAccentContrast, $"{Name(theme, variant)}: {textKey} {text} on {fillKey} {shown} has contrast {ratio:F2}");
                }
            }
        }
        finally
        {
            service.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void Selected_text_takes_the_label_of_the_accent_behind_it()
    {
        var service = Theme();
        var box = new TextBox { Text = "selected" };
        var window = new Window { Content = box };
        window.Show();
        try
        {
            foreach (var hex in new[] { "#FFFFFF", "#101018" })
            {
                service.Apply(new ThemeSettings { Accent = hex, Mode = ThemeMode.Dark }, "en");
                RenderTests.Pump();
                Assert.Equal(ColorOf("OnAccentBrush", ThemeVariant.Dark), Assert.IsAssignableFrom<ISolidColorBrush>(box.SelectionForegroundBrush).Color);
            }
        }
        finally
        {
            window.Close();
            service.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void A_swatchs_check_mark_reads_on_that_swatch_even_when_the_accent_differs()
    {
        // Material You turns the grey swatch into its monochrome palette: the accent is near white, the swatch stays grey.
        var service = Theme();
        var directory = Directory.CreateTempSubdirectory("filee-swatch-test-").FullName;
        var store = new UserDataStore(directory);
        store.Load();
        var vm = new ThemePageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>());
        var window = new Window { Width = 1280, Height = 900, Content = new ThemePage { DataContext = vm } };
        window.Show();
        try
        {
            foreach (var hex in ThemeService.Swatches)
            {
                service.Apply(new ThemeSettings { Style = ThemeStyle.MaterialYou, Accent = hex, Mode = ThemeMode.Dark }, "en");
                var swatch = Color.Parse(hex);
                vm.Accent = swatch;
                RenderTests.Pump();
                var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "AccentSwatches");
                var item = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsSelected);
                var check = item.GetVisualDescendants().OfType<PathIcon>().Single();
                var ratio = MaterialYouPalette.ContrastRatio(swatch, Assert.IsAssignableFrom<ISolidColorBrush>(check.Foreground).Color);
                Assert.True(ratio >= 4.2, $"the check mark on {hex} has contrast {ratio:F2}");
            }
        }
        finally
        {
            window.Close();
            service.Apply(new ThemeSettings(), "en");
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void The_donuts_lit_slice_is_drawn_in_the_label_on_a_bright_accent()
    {
        var service = Theme();
        Motion.Configure(false, () => false);
        try
        {
            service.Apply(new ThemeSettings { Accent = "#1F1E1D", PresetKey = Monochrome, Mode = ThemeMode.Dark }, "en");
            var donut = new DonutMenu { Items = [new DonutItem { Id = "a", Label = "Save" }, new DonutItem { Id = "b", Label = "B" }, new DonutItem { Id = "c", Label = "C" }], OuterRadius = 150 };
            var window = new Window { Width = 640, Height = 640, Content = donut };
            window.Show();
            donut.SetExternalHighlight(0);
            for (var i = 0; i < 60; i++)
                donut.Advance(1 / 60.0);
            RenderTests.Pump();
            Assert.Equal(ColorOf("OnAccentBrush", ThemeVariant.Dark), Assert.IsAssignableFrom<ISolidColorBrush>(donut.OnAccentBrush).Color);
            Assert.True(ColorOf("OnAccentBrush", ThemeVariant.Dark) != Colors.White);
            window.Close();
        }
        finally
        {
            service.Apply(new ThemeSettings(), "en");
        }
    }
}
