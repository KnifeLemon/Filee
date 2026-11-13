// Material Design 3 dynamic tonal palette generator for Filee.
// Generates variant-specific theme resources (surfaces, text, borders, shadows, and accent roles)
// from a seed colour using perceptual HSL tonal mapping. Built strictly on Filee's existing theme resource keys.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Filee.App.Services;

/// <summary>Resolved Material Design 3 theme resources for one variant (Light or Dark).</summary>
public sealed class MaterialYouPalette
{
    public required Color Page { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceAlt { get; init; }
    public required Color SubtleBorder { get; init; }
    public required Color TextPrimary { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color Danger { get; init; }
    public required Color Success { get; init; }
    public required Color Warning { get; init; }
    public required BoxShadows CardShadow { get; init; }

    public required Color Accent { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color AccentFaint { get; init; }
    public required Color AccentStrong { get; init; }
    public required Color OnAccent { get; init; }

    /// <summary>Creates an Avalonia ResourceDictionary mapping standard Filee theme keys to solid brushes and resources.</summary>
    public ResourceDictionary ToResourceDictionary()
    {
        var resources = new ResourceDictionary
        {
            ["PageBrush"] = new SolidColorBrush(Page),
            ["SurfaceBrush"] = new SolidColorBrush(Surface),
            ["SurfaceAltBrush"] = new SolidColorBrush(SurfaceAlt),
            ["SubtleBorderBrush"] = new SolidColorBrush(SubtleBorder),
            ["TextPrimaryBrush"] = new SolidColorBrush(TextPrimary),
            ["TextSecondaryBrush"] = new SolidColorBrush(TextSecondary),
            ["DangerBrush"] = new SolidColorBrush(Danger),
            ["SuccessBrush"] = new SolidColorBrush(Success),
            ["WarningBrush"] = new SolidColorBrush(Warning),
            ["CardShadow"] = CardShadow,

            ["AccentColor"] = Accent,
            ["AccentBrush"] = new SolidColorBrush(Accent),
            ["AccentSoftBrush"] = new SolidColorBrush(AccentSoft),
            ["AccentFaintBrush"] = new SolidColorBrush(AccentFaint),
            ["AccentStrongBrush"] = new SolidColorBrush(AccentStrong),
            ["SystemAccentColor"] = Accent,
            ["SystemAccentColorLight1"] = SoftPalette.Shade(Accent, 0.15),
            ["SystemAccentColorLight2"] = SoftPalette.Shade(Accent, 0.30),
            ["SystemAccentColorLight3"] = SoftPalette.Shade(Accent, 0.45),
            ["SystemAccentColorDark1"] = SoftPalette.Shade(Accent, -0.15),
            ["SystemAccentColorDark2"] = SoftPalette.Shade(Accent, -0.30),
            ["SystemAccentColorDark3"] = SoftPalette.Shade(Accent, -0.45),

            // Fluent's own templates read these keys for sliders and focused text boxes (filled buttons, checkboxes and the
            // like: ApplyAccentStates below). Pointing them at the Material You accent keeps those controls on the chosen colour.
            // The slider thumb is a slightly darker shade of the accent so it stays visible against the accent-filled track.
            ["AccentThumbBrush"] = new SolidColorBrush(SoftPalette.Shade(Accent, -0.2)),
            ["SliderThumbBackground"] = new SolidColorBrush(SoftPalette.Shade(Accent, -0.2)),
            ["SliderThumbBackgroundPointerOver"] = new SolidColorBrush(SoftPalette.Shade(Accent, -0.26)),
            ["SliderThumbBackgroundPressed"] = new SolidColorBrush(SoftPalette.Shade(Accent, -0.32)),
            ["SliderTrackValueFill"] = new SolidColorBrush(Accent),
            ["SliderTrackValueFillPointerOver"] = new SolidColorBrush(Accent),
            ["SliderTrackValueFillPressed"] = new SolidColorBrush(Accent),
            ["TextControlBorderBrushFocused"] = new SolidColorBrush(Accent),

            // The cross in search boxes turns the accent colour on hover and press, with no background behind it.
            ["TextControlButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent),
            ["TextControlButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent),
            ["TextControlButtonBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent),
            ["TextControlButtonBorderBrushPressed"] = new SolidColorBrush(Colors.Transparent),
            ["TextControlButtonForegroundPointerOver"] = new SolidColorBrush(Accent),
            ["TextControlButtonForegroundPressed"] = new SolidColorBrush(Accent),
        };

        SoftPalette.ApplyAccentStates(resources, Accent, OnAccent, hover: -0.08, pressed: -0.16);

        // Dark mode: Fluent's hover and focus fills for inputs and dropdowns are near-black, a sudden dark box. Keep them a
        // faint step lighter than the resting fill (SurfaceAlt, which the Material You styles already give inputs and
        // dropdowns) instead, so hovering only hints at the change. The resting fills themselves are not set here.
        if (Luminance(Surface) < 0.5)
        {
            var faint = new SolidColorBrush(SoftPalette.Shade(SurfaceAlt, 0.06));
            foreach (var key in new[]
                     {
                         "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                         "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed",
                     })
                resources[key] = faint;
        }

        return resources;
    }

    /// <summary>True if the colour is monochrome (low saturation or black/white/charcoal).</summary>
    public static bool IsMonochrome(Color c)
    {
        var (_, s, _) = RgbToHsl(c);
        return s < 0.08 ||
               (c.R == c.G && c.G == c.B) ||
               (Math.Abs(c.R - c.G) <= 4 && Math.Abs(c.G - c.B) <= 4 && Math.Abs(c.R - c.B) <= 4);
    }

    /// <summary>Calculates standard sRGB relative luminance.</summary>
    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>WCAG 2.x relative luminance (gamma-corrected), used for contrast ratios.</summary>
    public static double RelativeLuminance(Color c) => SoftPalette.RelativeLuminance(c);

    /// <summary>WCAG 2.x contrast ratio between two colours (1 to 21).</summary>
    public static double ContrastRatio(Color a, Color b) => SoftPalette.ContrastRatio(a, b);

    /// <summary>Minimum contrast between filled accent controls (buttons, checkboxes) and the text or glyph on them.</summary>
    public const double MinAccentContrast = SoftPalette.MinAccentContrast;

    private static readonly Color DarkOnAccent = Color.Parse("#1F1E1D");

    /// <summary>
    /// Picks the light-mode accent fill and the colour used on top of it. The fill stays as close to the chosen accent
    /// as readability allows: the old rule jumped any light accent to a fixed 45% lightness, which turned lavender
    /// (#7F77DD) into a dark blue (#392EB8). Now the seed is used as is when white text already reads on it, nudged
    /// only slightly darker when that is enough, and light pastels keep their colour and get dark text instead.
    /// </summary>
    internal static (Color Primary, Color OnPrimary) LightPrimary(Color seed, double h, double sat, double l)
    {
        if (ContrastRatio(seed, Colors.White) >= MinAccentContrast)
            return (seed, Colors.White);

        // Darken in small steps, keeping hue and saturation, until white text is readable.
        var candidate = seed;
        var lightness = l;
        while (lightness > 0.20 && ContrastRatio(candidate, Colors.White) < MinAccentContrast)
        {
            lightness -= 0.005;
            candidate = HslToRgb(h, sat, lightness);
        }

        // A small nudge keeps the accent recognisable; a big one would change its character.
        if (l - lightness <= 0.08)
            return (candidate, Colors.White);

        // Pastels (peach, rose, amber ...): keep the colour itself and put dark text on it.
        if (ContrastRatio(seed, DarkOnAccent) >= MinAccentContrast)
            return (seed, DarkOnAccent);

        return (candidate, Colors.White);
    }

    /// <summary>Generates Material You tonal resources for the specified seed and variant.</summary>
    public static MaterialYouPalette Generate(Color seed, bool isDark)
    {
        if (IsMonochrome(seed))
            return GenerateMonochrome(isDark);

        var (h, s, l) = RgbToHsl(seed);
        var sat = Math.Clamp(s, 0.20, 0.85);

        if (!isDark)
        {
            var (primary, onPrimary) = LightPrimary(seed, h, sat, l);
            var primaryContainer = HslToRgb(h, Math.Min(sat * 0.5, 0.40), 0.90);
            var onPrimaryContainer = HslToRgb(h, Math.Min(sat * 0.7, 0.60), 0.22);

            return new MaterialYouPalette
            {
                Page = HslToRgb(h, Math.Min(sat * 0.18, 0.12), 0.97),
                Surface = Colors.White,
                SurfaceAlt = HslToRgb(h, Math.Min(sat * 0.22, 0.16), 0.92),
                SubtleBorder = Color.FromArgb(28, primary.R, primary.G, primary.B),
                TextPrimary = HslToRgb(h, 0.10, 0.12),
                TextSecondary = HslToRgb(h, 0.15, 0.38),
                Danger = Color.Parse("#BA1A1A"),
                Success = Color.Parse("#1B6D24"),
                Warning = Color.Parse("#825500"),
                CardShadow = BoxShadows.Parse("0 1 3 0 #12000000, 0 6 18 0 #0D000000"),

                Accent = primary,
                OnAccent = onPrimary,
                AccentSoft = primaryContainer,
                AccentFaint = HslToRgb(h, Math.Min(sat * 0.35, 0.25), 0.95),
                AccentStrong = onPrimaryContainer,
            };
        }
        else
        {
            var darkPrimary = l < 0.65 ? HslToRgb(h, Math.Min(sat * 0.75, 0.65), 0.75) : seed;
            var onPrimary = SoftPalette.OnAccent(darkPrimary);
            var primaryContainer = HslToRgb(h, Math.Min(sat * 0.5, 0.38), 0.26);
            var onPrimaryContainer = HslToRgb(h, Math.Min(sat * 0.7, 0.55), 0.88);

            return new MaterialYouPalette
            {
                Page = HslToRgb(h, Math.Min(sat * 0.18, 0.12), 0.08),
                Surface = HslToRgb(h, Math.Min(sat * 0.20, 0.14), 0.13),
                SurfaceAlt = HslToRgb(h, Math.Min(sat * 0.22, 0.16), 0.18),
                SubtleBorder = Color.FromArgb(32, darkPrimary.R, darkPrimary.G, darkPrimary.B),
                TextPrimary = HslToRgb(h, 0.08, 0.92),
                TextSecondary = HslToRgb(h, 0.12, 0.75),
                Danger = Color.Parse("#FFB4AB"),
                Success = Color.Parse("#8CD692"),
                Warning = Color.Parse("#FFBA2F"),
                CardShadow = BoxShadows.Parse("0 1 3 0 #40000000, 0 8 24 0 #33000000"),

                Accent = darkPrimary,
                OnAccent = onPrimary,
                AccentSoft = primaryContainer,
                AccentFaint = HslToRgb(h, Math.Min(sat * 0.30, 0.20), 0.15),
                AccentStrong = onPrimaryContainer,
            };
        }
    }

    private static MaterialYouPalette GenerateMonochrome(bool isDark)
    {
        if (!isDark)
        {
            var accent = Color.Parse("#1F1E1D");
            return new MaterialYouPalette
            {
                Page = Color.Parse("#F7F6F2"),
                Surface = Colors.White,
                SurfaceAlt = Color.Parse("#EEECE5"),
                SubtleBorder = Color.FromArgb(24, 0, 0, 0),
                TextPrimary = Color.Parse("#1F1E1D"),
                TextSecondary = Color.Parse("#6E6C67"),
                Danger = Color.Parse("#C73A39"),
                Success = Color.Parse("#0F7A5A"),
                Warning = Color.Parse("#9A5B0C"),
                CardShadow = BoxShadows.Parse("0 1 3 0 #12000000, 0 6 18 0 #0D000000"),

                Accent = accent,
                OnAccent = Colors.White,
                AccentSoft = Color.FromArgb(32, accent.R, accent.G, accent.B),
                AccentFaint = Color.FromArgb(14, accent.R, accent.G, accent.B),
                AccentStrong = accent,
            };
        }
        else
        {
            var accent = Color.Parse("#F1EFE8");
            return new MaterialYouPalette
            {
                Page = Color.Parse("#161614"),
                Surface = Color.Parse("#222220"),
                SurfaceAlt = Color.Parse("#2C2C29"),
                SubtleBorder = Color.FromArgb(28, 255, 255, 255),
                TextPrimary = Color.Parse("#F1EFE8"),
                TextSecondary = Color.Parse("#AEACA4"),
                Danger = Color.Parse("#F09595"),
                Success = Color.Parse("#5DCAA5"),
                Warning = Color.Parse("#EF9F27"),
                CardShadow = BoxShadows.Parse("0 1 3 0 #40000000, 0 8 24 0 #33000000"),

                Accent = accent,
                OnAccent = Color.Parse("#1F1E1D"),
                AccentSoft = Color.FromArgb(46, accent.R, accent.G, accent.B),
                AccentFaint = Color.FromArgb(20, accent.R, accent.G, accent.B),
                AccentStrong = accent,
            };
        }
    }

    public static (double H, double S, double L) RgbToHsl(Color c)
    {
        double r = c.R / 255.0;
        double g = c.G / 255.0;
        double b = c.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0.0;
        double s = 0.0;
        double l = (max + min) / 2.0;

        if (delta > 1e-5)
        {
            s = l < 0.5 ? delta / (max + min) : delta / (2.0 - max - min);

            if (Math.Abs(max - r) < 1e-5)
                h = ((g - b) / delta + (g < b ? 6.0 : 0.0)) / 6.0;
            else if (Math.Abs(max - g) < 1e-5)
                h = ((b - r) / delta + 2.0) / 6.0;
            else
                h = ((r - g) / delta + 4.0) / 6.0;
        }

        return (h, s, l);
    }

    public static Color HslToRgb(double h, double s, double l)
    {
        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0.0) t += 1.0;
            if (t > 1.0) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        h = (h % 1.0 + 1.0) % 1.0;
        s = Math.Clamp(s, 0.0, 1.0);
        l = Math.Clamp(l, 0.0, 1.0);

        if (s < 1e-5)
        {
            byte grey = (byte)Math.Clamp(Math.Round(l * 255.0), 0, 255);
            return Color.FromRgb(grey, grey, grey);
        }

        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;

        byte r = (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255.0), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h) * 255.0), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255.0), 0, 255);

        return Color.FromRgb(r, g, b);
    }
}
