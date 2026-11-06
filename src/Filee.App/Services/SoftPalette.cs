// The resources behind Filee's look that are computed rather than written in Styles/Soft.axaml: the accent colour
// with its shades, the corner radii, the UI font order and the reading direction. ThemeService applies them with the user's choices; the
// installer (src/Filee.Setup links this file) applies the defaults, so Setup looks like the app.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Filee.App.Services;

internal static class SoftPalette
{
    /// <summary>The default accent colour (the first swatch of the Theme page).</summary>
    public const string DefaultAccent = "#7F77DD";

    /// <summary>The default corner radius (ThemeSettings.CornerRadius).</summary>
    public const double DefaultCornerRadius = 16;

    /// <summary>Embedded Noto fonts (see build/fetch-fonts.ps1). Missing files fall back to system fonts.</summary>
    private const string Latin = "fonts:Filee#Noto Sans";
    private const string Arabic = "fonts:Filee#Noto Sans Arabic";
    private const string Devanagari = "fonts:Filee#Noto Sans Devanagari";
    private const string Thai = "fonts:Filee#Noto Sans Thai";

    /// <summary>Each CJK region's embedded font and its system fonts (Windows, then macOS).</summary>
    private static readonly (string Embedded, string System) Japanese =
        ("fonts:Filee#Noto Sans JP", "Yu Gothic UI, Meiryo, Hiragino Sans");
    private static readonly (string Embedded, string System) Korean =
        ("fonts:Filee#Noto Sans KR", "Malgun Gothic, Apple SD Gothic Neo");
    private static readonly (string Embedded, string System) Chinese =
        ("fonts:Filee#Noto Sans SC", "Microsoft YaHei, PingFang SC");

    /// <summary>
    /// Traditional Chinese has no embedded font (two more Noto CJK files would add 11 MB): Windows and macOS always carry
    /// their two, Linux often has the Noto one. Listed before the embedded Noto Sans SC, whose Han shapes differ, so zh-TW
    /// text takes Traditional shapes.
    /// </summary>
    private const string TraditionalChineseSystem = "Microsoft JhengHei UI, Microsoft JhengHei, PingFang TC, Noto Sans CJK TC";

    /// <summary>Minimum contrast (WCAG AA, normal-size text) that a shifted hover or pressed fill keeps with its label.</summary>
    public const double MinAccentContrast = 4.5;

    /// <summary>Below this contrast with the resting fill, a hover or pressed fill can't be told from it.</summary>
    private const double MinVisibleShift = 1.05;

    /// <summary>The dark label on a bright accent. Dark enough that the higher of it and white never falls below 4.2:1.</summary>
    private static readonly Color DarkLabel = Color.Parse("#16141A");

    /// <summary>Fluent's filled accent controls, each with a PointerOver and a Pressed key.</summary>
    private static readonly string[] AccentFillKeys =
    [
        "AccentButtonBackground",
        "ToggleButtonBackgroundChecked",
        "CheckBoxCheckBackgroundFillChecked",
        "CheckBoxCheckBackgroundStrokeChecked",
        "CheckBoxCheckBackgroundFillIndeterminate",
        "CheckBoxCheckBackgroundStrokeIndeterminate",
        "RadioButtonOuterEllipseCheckedFill",
        "RadioButtonOuterEllipseCheckedStroke",
        "ToggleSwitchFillOn",
    ];

    /// <summary>What Fluent draws on those fills, fixed white unless set here. Each with a PointerOver and a Pressed key.</summary>
    private static readonly string[] OnAccentKeys =
    [
        "AccentButtonForeground",
        "ToggleButtonForegroundChecked",
        "CheckBoxCheckGlyphForegroundChecked",
        "CheckBoxCheckGlyphForegroundIndeterminate",
        "RadioButtonCheckGlyphFill",
        "ToggleSwitchKnobFillOn",
    ];

    /// <summary>
    /// Selected rows in Fluent's lists and dropdowns (selected, hover, pressed). Their text is black or white by theme, not
    /// by accent, so these stay a tint it reads on with any accent: Fluent's own 60% and more left white text on a light
    /// accent unreadable.
    /// </summary>
    private static readonly (string Key, double Opacity)[] SelectedRowKeys =
    [
        ("SystemControlHighlightListAccentLowBrush", 0.18),
        ("SystemControlHighlightListAccentMediumBrush", 0.26),
        ("SystemControlHighlightListAccentHighBrush", 0.34),
        ("ComboBoxItemBackgroundSelected", 0.18),
        ("ComboBoxItemBackgroundSelectedPointerOver", 0.26),
        ("ComboBoxItemBackgroundSelectedPressed", 0.34),
    ];

    /// <summary>All resource keys written by ApplyAccent that must be cleared from top level when per-variant dictionaries are used.</summary>
    public static readonly string[] AccentResourceKeys =
    [
        "AccentColor",
        "AccentBrush",
        "AccentSoftBrush",
        "AccentFaintBrush",
        "AccentStrongBrush",
        "OnAccentBrush",
        "SystemAccentColor",
        "SystemAccentColorLight1",
        "SystemAccentColorLight2",
        "SystemAccentColorLight3",
        "SystemAccentColorDark1",
        "SystemAccentColorDark2",
        "SystemAccentColorDark3",
        .. AccentFillKeys.Concat(OnAccentKeys).SelectMany(key => new[] { key, key + "PointerOver", key + "Pressed" }),
        .. SelectedRowKeys.Select(row => row.Key),
    ];

    /// <summary>Removes top-level accent keys so ThemeDictionaries take effect without being shadowed.</summary>
    public static void ClearTopLevelAccent(IResourceDictionary res)
    {
        foreach (var key in AccentResourceKeys)
            res.Remove(key);
    }

    /// <summary>Accent colour + derived shades. Fluent controls read the System* keys.</summary>
    public static void ApplyAccent(IResourceDictionary res, Color accent)
    {
        res["AccentColor"] = accent;
        res["AccentBrush"] = new SolidColorBrush(accent);
        res["AccentSoftBrush"] = new SolidColorBrush(accent, 0.18);
        res["AccentFaintBrush"] = new SolidColorBrush(accent, 0.09);
        res["AccentStrongBrush"] = new SolidColorBrush(Shade(accent, -0.25));
        ApplyAccentStates(res, accent, OnAccent(accent), hover: 0.15, pressed: -0.20);
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorLight1"] = Shade(accent, 0.15);
        res["SystemAccentColorLight2"] = Shade(accent, 0.30);
        res["SystemAccentColorLight3"] = Shade(accent, 0.45);
        res["SystemAccentColorDark1"] = Shade(accent, -0.15);
        res["SystemAccentColorDark2"] = Shade(accent, -0.30);
        res["SystemAccentColorDark3"] = Shade(accent, -0.45);
    }

    /// <summary>
    /// Applies variant-specific monochrome accent resources into a theme dictionary so selection
    /// maintains high contrast in both light (dark charcoal) and dark (silvery white) modes.
    /// </summary>
    public static void ApplyMonochromeAccent(IResourceDictionary dict, bool isDark)
    {
        var accent = isDark ? Color.Parse("#F1EFE8") : Color.Parse("#1F1E1D");
        var onAccent = isDark ? Color.Parse("#1F1E1D") : Colors.White;
        dict["AccentColor"] = accent;
        dict["AccentBrush"] = new SolidColorBrush(accent);
        dict["AccentSoftBrush"] = new SolidColorBrush(accent, isDark ? 0.18 : 0.12);
        dict["AccentFaintBrush"] = new SolidColorBrush(accent, isDark ? 0.08 : 0.05);
        dict["AccentStrongBrush"] = new SolidColorBrush(accent);
        ApplyAccentStates(dict, accent, onAccent, hover: 0.15, pressed: -0.20);
        dict["SystemAccentColor"] = accent;
        dict["SystemAccentColorLight1"] = Shade(accent, 0.15);
        dict["SystemAccentColorLight2"] = Shade(accent, 0.30);
        dict["SystemAccentColorLight3"] = Shade(accent, 0.45);
        dict["SystemAccentColorDark1"] = Shade(accent, -0.15);
        dict["SystemAccentColorDark2"] = Shade(accent, -0.30);
        dict["SystemAccentColorDark3"] = Shade(accent, -0.45);
    }

    /// <summary>
    /// The label on an accent fill and the fills of Fluent's accent controls in every state, so nothing keeps its fixed
    /// white. One label for all three states; a state's fill moves out of its way if it has to (<see cref="ShiftFill"/>).
    /// </summary>
    public static void ApplyAccentStates(IResourceDictionary res, Color accent, Color onAccent, double hover, double pressed)
    {
        var rest = new SolidColorBrush(accent);
        var hoverFill = new SolidColorBrush(ShiftFill(accent, onAccent, hover));
        var pressedFill = new SolidColorBrush(ShiftFill(accent, onAccent, pressed));
        foreach (var key in AccentFillKeys)
        {
            res[key] = rest;
            res[key + "PointerOver"] = hoverFill;
            res[key + "Pressed"] = pressedFill;
        }

        var label = new SolidColorBrush(onAccent);
        res["OnAccentBrush"] = label;
        foreach (var key in OnAccentKeys)
        {
            res[key] = label;
            res[key + "PointerOver"] = label;
            res[key + "Pressed"] = label;
        }

        foreach (var (key, opacity) in SelectedRowKeys)
            res[key] = new SolidColorBrush(accent, opacity);
    }

    /// <summary>
    /// A hover or pressed fill: <paramref name="fill"/> shaded by <paramref name="amount"/>, or the other way when that
    /// would cost the label contrast (down to 4.5:1 is fine) or barely show, as lightening a near-white fill does.
    /// </summary>
    public static Color ShiftFill(Color fill, Color label, double amount)
    {
        var shifted = Shade(fill, amount);
        var keepsLabel = ContrastRatio(shifted, label) >= Math.Min(ContrastRatio(fill, label), MinAccentContrast);
        return keepsLabel && ContrastRatio(shifted, fill) >= MinVisibleShift ? shifted : Shade(fill, -amount);
    }

    /// <summary>
    /// The label on an accent fill: white or dark, whichever has the higher contrast on it. Lavender (#7F77DD) takes the
    /// dark one (4.86:1, white 3.76:1), the grey swatch (#5F5E5A) keeps white (6.49:1, dark 2.82:1).
    /// </summary>
    public static Color OnAccent(Color fill) =>
        ContrastRatio(fill, Colors.White) >= ContrastRatio(fill, DarkLabel) ? Colors.White : DarkLabel;

    /// <summary>"Softness": one radius drives every control.</summary>
    public static void ApplyCornerRadius(IResourceDictionary res, double radius)
    {
        var r = Math.Clamp(radius, 0, 28);
        res["SoftRadius"] = new CornerRadius(r);
        res["SoftRadiusSmall"] = new CornerRadius(Math.Round(r * 0.55));
        res["SoftRadiusLarge"] = new CornerRadius(Math.Round(r * 1.35));
        res["ControlCornerRadius"] = new CornerRadius(Math.Round(r * 0.55));
        res["OverlayCornerRadius"] = new CornerRadius(r);
        res["SoftRadiusPill"] = new CornerRadius(Math.Round(r * 2.5));
    }

    /// <summary>
    /// The UI font chain. Han characters look different in Japanese, Korean and Chinese fonts, so the UI language's
    /// CJK fonts come first, embedded and system ones alike; other languages keep Korean first.
    /// </summary>
    public static string FontFamilies(string language)
    {
        (string Embedded, string System)[] cjk =
            language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? [Japanese, Korean, Chinese]
            : language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? [Chinese, Korean, Japanese]
            : [Korean, Chinese, Japanese];
        var traditional = AppLanguages.IsTraditionalChinese(language) ? $"{TraditionalChineseSystem}, " : "";
        // Noto Sans comes first: the first face also draws the trimming ellipsis, which Noto Sans Arabic lacks.
        return $"{Latin}, {Arabic}, {Devanagari}, {Thai}, {traditional}{string.Join(", ", cjk.Select(f => f.Embedded))}, Segoe UI, Nirmala UI, Leelawadee UI, "
            + $"{string.Join(", ", cjk.Select(f => f.System))}, Kohinoor Devanagari, Geeza Pro, Thonburi, sans-serif";
    }

    /// <summary>
    /// The font chain from Windows' own UI fonts only (no embedded fonts), with the UI language's CJK font first: what
    /// the installer uses. Every Windows 10 1809+ has these, with bold faces.
    /// </summary>
    public static string SystemFontFamilies(string language)
    {
        (string Embedded, string System)[] cjk =
            language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? [Japanese, Korean, Chinese]
            : language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? [Chinese, Korean, Japanese]
            : [Korean, Chinese, Japanese];
        var traditional = AppLanguages.IsTraditionalChinese(language) ? $"{TraditionalChineseSystem}, " : "";
        return $"Segoe UI, Nirmala UI, Leelawadee UI, {traditional}{string.Join(", ", cjk.Select(f => f.System))}, sans-serif";
    }

    /// <summary>The resource Soft.axaml's Window style takes <see cref="Visual.FlowDirection"/> from.</summary>
    public const string FlowDirectionKey = "AppFlowDirection";

    /// <summary>"→" between a name and what it becomes, "←" in a right-to-left language (arrows don't mirror).</summary>
    public const string ForwardArrowKey = "AppForwardArrow";

    /// <summary>
    /// Arabic reads right to left: every window mirrors its layout (Soft.axaml's Window style). Controls that draw a
    /// picture or a circle (the donut, the crop canvas, animated images) keep left to right themselves.
    /// </summary>
    public static void ApplyFlowDirection(IResourceDictionary res, string language)
    {
        var rightToLeft = IsRightToLeft(language);
        res[FlowDirectionKey] = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        res[ForwardArrowKey] = rightToLeft ? "←" : "→";
    }

    /// <summary>True for a language written right to left.</summary>
    public static bool IsRightToLeft(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language).TextInfo.IsRightToLeft;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The UI font for <paramref name="language"/>.</summary>
    public static FontFamily FontFamilyFor(string language) => FontFamily.Parse(FontFamilies(language));

    /// <summary>Lightens (amount &gt; 0) or darkens (amount &lt; 0) a colour.</summary>
    public static Color Shade(Color c, double amount)
    {
        byte Mix(byte v) => amount >= 0
            ? (byte)Math.Round(v + (255 - v) * amount)
            : (byte)Math.Round(v * (1 + amount));
        return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
    }

    /// <summary>Relative luminance 0..1 (sRGB approximation).</summary>
    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>WCAG 2.x relative luminance (gamma-corrected), used for contrast ratios.</summary>
    public static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG 2.x contrast ratio between two colours (1 to 21).</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
