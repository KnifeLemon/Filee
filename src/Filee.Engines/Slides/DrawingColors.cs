// DrawingML colours for the slide renderer: the colour elements (srgbClr, schemeClr, sysClr, prstClr, scrgbClr,
// hslClr) with their transforms applied in order, alpha included (ECMA-376 Part 1, 20.1.2.3). Theme colours come from
// the theme's colour scheme, and the colour map of the master (or a layout's or slide's override) says which of them
// tx1, bg1, tx2 and bg2 are. Tint and shade work in linear RGB, the luminance and saturation transforms in HSL.

using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace Filee.Engines.Slides;

internal sealed class DrawingColors
{
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static readonly string[] Slots = ["dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];

    /// <summary>Office's default theme colours, for a file without a theme.</summary>
    private static readonly Dictionary<string, SKColor> DefaultTheme = new()
    {
        ["dk1"] = SKColors.Black,
        ["lt1"] = SKColors.White,
        ["dk2"] = new SKColor(0x44, 0x54, 0x6A),
        ["lt2"] = new SKColor(0xE7, 0xE6, 0xE6),
        ["accent1"] = new SKColor(0x44, 0x72, 0xC4),
        ["accent2"] = new SKColor(0xED, 0x7D, 0x31),
        ["accent3"] = new SKColor(0xA5, 0xA5, 0xA5),
        ["accent4"] = new SKColor(0xFF, 0xC0, 0x00),
        ["accent5"] = new SKColor(0x5B, 0x9B, 0xD5),
        ["accent6"] = new SKColor(0x70, 0xAD, 0x47),
        ["hlink"] = new SKColor(0x05, 0x63, 0xC1),
        ["folHlink"] = new SKColor(0x95, 0x4F, 0x72),
    };

    private readonly Dictionary<string, SKColor> _theme;
    private readonly IReadOnlyDictionary<string, string> _map;

    private DrawingColors(Dictionary<string, SKColor> theme, IReadOnlyDictionary<string, string> map)
    {
        _theme = theme;
        _map = map;
    }

    /// <summary>The colours of a theme part (null = Office's defaults) with the standard colour map.</summary>
    public static DrawingColors FromTheme(XElement? theme)
    {
        var colors = new Dictionary<string, SKColor>(DefaultTheme);
        var scheme = theme?.Descendants(A + "clrScheme").FirstOrDefault();
        var plain = new DrawingColors(colors, new Dictionary<string, string>());
        foreach (var slot in Slots)
        {
            if (scheme?.Element(A + slot) is { } element && plain.Color(element) is { } color)
                colors[slot] = color;
        }
        return plain;
    }

    /// <summary>The same colours under a colour map (p:clrMap: bg1="lt1" tx1="dk1" ...).</summary>
    public DrawingColors WithMap(XElement? map) =>
        map is null ? this : new DrawingColors(_theme, map.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value));

    /// <summary>A scheme colour by name (accent1, tx1, bg2, ...).</summary>
    public SKColor Scheme(string name)
    {
        var slot = _map.GetValueOrDefault(name) ?? name switch
        {
            "tx1" => "dk1",
            "bg1" => "lt1",
            "tx2" => "dk2",
            "bg2" => "lt2",
            _ => name,
        };
        return _theme.TryGetValue(slot, out var color) ? color : SKColors.Black;
    }

    /// <summary>
    /// The first colour element among the children of <paramref name="parent"/>, transformed; null when there is none.
    /// <paramref name="placeholder"/> stands for schemeClr "phClr" (theme styles referenced with a colour).
    /// </summary>
    public SKColor? Color(XElement? parent, SKColor? placeholder = null)
    {
        foreach (var element in parent?.Elements() ?? [])
        {
            SKColor? color = element.Name.LocalName switch
            {
                "srgbClr" => Hex((string?)element.Attribute("val")),
                "schemeClr" => (string?)element.Attribute("val") is "phClr" ? placeholder ?? SKColors.Black : Scheme((string?)element.Attribute("val") ?? ""),
                "sysClr" => Hex((string?)element.Attribute("lastClr")) ?? System((string?)element.Attribute("val")),
                "prstClr" => Preset((string?)element.Attribute("val")),
                "scrgbClr" => FromLinear(Percent(element, "r"), Percent(element, "g"), Percent(element, "b")),
                "hslClr" => FromHsl(Number(element, "hue") / 21600000.0, Percent(element, "sat"), Percent(element, "lum"), 1),
                _ => null,
            };
            if (element.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr" or "prstClr" or "scrgbClr" or "hslClr")
                return Transform(color ?? SKColors.Black, element);
        }
        return null;
    }

    private static double Number(XElement element, string name) =>
        double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;

    private static double Percent(XElement element, string name) => Number(element, name) / 100000;

    private static SKColor? Hex(string? value) =>
        value is { Length: 6 } && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;

    /// <summary>Applies the transforms of a colour element in document order.</summary>
    private static SKColor Transform(SKColor color, XElement element)
    {
        double r = color.Red / 255.0, g = color.Green / 255.0, b = color.Blue / 255.0, alpha = color.Alpha / 255.0;
        foreach (var transform in element.Elements())
        {
            var value = Percent(transform, "val");
            switch (transform.Name.LocalName)
            {
                case "alpha":
                    alpha = value;
                    break;
                case "alphaMod":
                    alpha *= value;
                    break;
                case "alphaOff":
                    alpha += value;
                    break;
                case "tint":
                    (r, g, b) = Linear(r, g, b, c => 1 - (1 - c) * Math.Clamp(value, 0, 1));
                    break;
                case "shade":
                    (r, g, b) = Linear(r, g, b, c => c * Math.Clamp(value, 0, 1));
                    break;
                case "lumMod" or "lumOff" or "lum" or "satMod" or "satOff" or "sat" or "hueMod" or "hueOff" or "hue":
                    {
                        var (h, s, l) = ToHsl(r, g, b);
                        switch (transform.Name.LocalName)
                        {
                            case "lumMod": l *= value; break;
                            case "lumOff": l += value; break;
                            case "lum": l = value; break;
                            case "satMod": s *= value; break;
                            case "satOff": s += value; break;
                            case "sat": s = value; break;
                            case "hueMod": h *= value; break;
                            case "hueOff": h += Number(transform, "val") / 21600000.0; break;
                            case "hue": h = Number(transform, "val") / 21600000.0; break;
                        }
                        var c = FromHsl(h - Math.Floor(h), Math.Clamp(s, 0, 1), Math.Clamp(l, 0, 1), 1);
                        (r, g, b) = (c.Red / 255.0, c.Green / 255.0, c.Blue / 255.0);
                        break;
                    }
                case "comp":
                    {
                        var (h, s, l) = ToHsl(r, g, b);
                        var c = FromHsl((h + 0.5) % 1, s, l, 1);
                        (r, g, b) = (c.Red / 255.0, c.Green / 255.0, c.Blue / 255.0);
                        break;
                    }
                case "inv":
                    (r, g, b) = (1 - r, 1 - g, 1 - b);
                    break;
                case "gray":
                    r = g = b = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    break;
                case "red": r = value; break;
                case "redMod": r *= value; break;
                case "redOff": r += value; break;
                case "green": g = value; break;
                case "greenMod": g *= value; break;
                case "greenOff": g += value; break;
                case "blue": b = value; break;
                case "blueMod": b *= value; break;
                case "blueOff": b += value; break;
            }
            (r, g, b, alpha) = (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1), Math.Clamp(alpha, 0, 1));
        }
        return new SKColor(Byte(r), Byte(g), Byte(b), Byte(alpha));
    }

    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static (double R, double G, double B) Linear(double r, double g, double b, Func<double, double> change)
    {
        static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double FromLinearChannel(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (FromLinearChannel(change(ToLinear(r))), FromLinearChannel(change(ToLinear(g))), FromLinearChannel(change(ToLinear(b))));
    }

    private static SKColor FromLinear(double r, double g, double b)
    {
        static double Channel(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(Math.Clamp(c, 0, 1), 1 / 2.4) - 0.055;
        return new SKColor(Byte(Channel(r)), Byte(Channel(g)), Byte(Channel(b)));
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-12)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static SKColor FromHsl(double h, double s, double l, double alpha)
    {
        if (s <= 0)
            return new SKColor(Byte(l), Byte(l), Byte(l), Byte(alpha));
        static double Channel(double p, double q, double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return new SKColor(Byte(Channel(p, q, h + 1 / 3.0)), Byte(Channel(p, q, h)), Byte(Channel(p, q, h - 1 / 3.0)), Byte(alpha));
    }

    private static SKColor System(string? name) => name switch
    {
        "window" or "highlightText" or "btnHighlight" or "menu" or "infoBk" => SKColors.White,
        "btnFace" or "3dLight" or "menuBar" => new SKColor(0xF0, 0xF0, 0xF0),
        "grayText" or "btnShadow" => new SKColor(0x80, 0x80, 0x80),
        "highlight" or "hotLight" => new SKColor(0x00, 0x78, 0xD7),
        _ => SKColors.Black,
    };

    /// <summary>DrawingML's preset colour names (ST_PresetColorVal), the common ones; others are black.</summary>
    private static SKColor Preset(string? name) => name switch
    {
        "white" => SKColors.White,
        "red" => new SKColor(0xFF, 0x00, 0x00),
        "green" => new SKColor(0x00, 0x80, 0x00),
        "lime" => new SKColor(0x00, 0xFF, 0x00),
        "blue" => new SKColor(0x00, 0x00, 0xFF),
        "yellow" => new SKColor(0xFF, 0xFF, 0x00),
        "cyan" or "aqua" => new SKColor(0x00, 0xFF, 0xFF),
        "magenta" or "fuchsia" => new SKColor(0xFF, 0x00, 0xFF),
        "gray" or "grey" => new SKColor(0x80, 0x80, 0x80),
        "silver" => new SKColor(0xC0, 0xC0, 0xC0),
        "ltGray" or "lightGray" or "ltGrey" or "lightGrey" => new SKColor(0xD3, 0xD3, 0xD3),
        "dkGray" or "darkGray" or "dkGrey" or "darkGrey" => new SKColor(0xA9, 0xA9, 0xA9),
        "dimGray" or "dimGrey" => new SKColor(0x69, 0x69, 0x69),
        "orange" => new SKColor(0xFF, 0xA5, 0x00),
        "purple" => new SKColor(0x80, 0x00, 0x80),
        "navy" => new SKColor(0x00, 0x00, 0x80),
        "maroon" => new SKColor(0x80, 0x00, 0x00),
        "olive" => new SKColor(0x80, 0x80, 0x00),
        "teal" => new SKColor(0x00, 0x80, 0x80),
        "brown" => new SKColor(0xA5, 0x2A, 0x2A),
        "pink" => new SKColor(0xFF, 0xC0, 0xCB),
        "gold" => new SKColor(0xFF, 0xD7, 0x00),
        "beige" => new SKColor(0xF5, 0xF5, 0xDC),
        "ivory" => new SKColor(0xFF, 0xFF, 0xF0),
        "violet" => new SKColor(0xEE, 0x82, 0xEE),
        "indigo" => new SKColor(0x4B, 0x00, 0x82),
        "tan" => new SKColor(0xD2, 0xB4, 0x8C),
        "skyBlue" => new SKColor(0x87, 0xCE, 0xEB),
        "ltBlue" or "lightBlue" => new SKColor(0xAD, 0xD8, 0xE6),
        "dkBlue" or "darkBlue" => new SKColor(0x00, 0x00, 0x8B),
        "dkRed" or "darkRed" => new SKColor(0x8B, 0x00, 0x00),
        "dkGreen" or "darkGreen" => new SKColor(0x00, 0x64, 0x00),
        "ltGreen" or "lightGreen" => new SKColor(0x90, 0xEE, 0x90),
        "ltYellow" or "lightYellow" => new SKColor(0xFF, 0xFF, 0xE0),
        "coral" => new SKColor(0xFF, 0x7F, 0x50),
        "salmon" => new SKColor(0xFA, 0x80, 0x72),
        "crimson" => new SKColor(0xDC, 0x14, 0x3C),
        "turquoise" => new SKColor(0x40, 0xE0, 0xD0),
        "khaki" => new SKColor(0xF0, 0xE6, 0x8C),
        "lavender" => new SKColor(0xE6, 0xE6, 0xFA),
        "steelBlue" => new SKColor(0x46, 0x82, 0xB4),
        "royalBlue" => new SKColor(0x41, 0x69, 0xE1),
        "forestGreen" => new SKColor(0x22, 0x8B, 0x22),
        "seaGreen" => new SKColor(0x2E, 0x8B, 0x57),
        "chocolate" => new SKColor(0xD2, 0x69, 0x1E),
        "tomato" => new SKColor(0xFF, 0x63, 0x47),
        "orchid" => new SKColor(0xDA, 0x70, 0xD6),
        "plum" => new SKColor(0xDD, 0xA0, 0xDD),
        "wheat" => new SKColor(0xF5, 0xDE, 0xB3),
        _ => SKColors.Black,
    };
}
