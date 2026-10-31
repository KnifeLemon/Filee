// The graphics objects of a metafile's object table, as GDI keeps them ([MS-WMF] 2.2.1 and [MS-EMF] 2.2): pens,
// brushes, fonts, palettes and regions, plus the stock objects records select by number. Objects never change once
// created, so the device context and its saved copies can share them.

using SkiaSharp;

namespace Filee.Engines.Metafiles;

/// <summary>An entry of the object table.</summary>
internal abstract class GdiObject;

/// <summary>A pen: LOGPEN or LOGPEN_EX (style bits as in [MS-WMF] 2.1.1.23 PenStyle).</summary>
internal sealed class GdiPen : GdiObject
{
    public const int Solid = 0, Dash = 1, Dot = 2, DashDot = 3, DashDotDot = 4, Null = 5, InsideFrame = 6, UserStyle = 7, Alternate = 8;
    public const int StyleMask = 0x0F, CapMask = 0x0F00, JoinMask = 0xF000, TypeMask = 0x000F0000;
    public const int CapSquare = 0x0100, CapFlat = 0x0200, JoinBevel = 0x1000, JoinMiter = 0x2000, Geometric = 0x00010000;

    public static readonly GdiPen Black = new(Solid, 0, SKColors.Black);
    public static readonly GdiPen White = new(Solid, 0, SKColors.White);
    public static readonly GdiPen Empty = new(Null, 0, SKColors.Black);

    /// <param name="width">Width in logical units; 0 is one device pixel.</param>
    public GdiPen(int style, float width, SKColor color, float[]? dashes = null, bool extended = false)
    {
        Style = style;
        Width = width;
        Color = color;
        Dashes = dashes;
        // A pen made by CreatePen (LOGPEN) is one pixel wide at width 0 and otherwise as wide as its width in logical
        // units; ExtCreatePen says which in its style.
        IsCosmetic = extended ? (style & TypeMask) != Geometric : width <= 0;
        Extended = extended;
    }

    /// <summary>The whole PenStyle: line style, end cap, join and pen type.</summary>
    public int Style { get; }

    public int LineStyle => Style & StyleMask;

    public float Width { get; }

    /// <summary>Made by ExtCreatePen (LOGPEN_EX), whose geometric pens keep their dashes at any width.</summary>
    public bool Extended { get; }

    public SKColor Color { get; }

    /// <summary>A cosmetic pen is one device pixel wide whatever the transform.</summary>
    public bool IsCosmetic { get; }

    /// <summary>Dash and gap lengths of a PS_USERSTYLE pen.</summary>
    public float[]? Dashes { get; }

    public SKStrokeCap Cap => (Style & CapMask) switch
    {
        CapSquare => SKStrokeCap.Square,
        CapFlat => SKStrokeCap.Butt,
        _ => SKStrokeCap.Round,
    };

    public SKStrokeJoin Join => (Style & JoinMask) switch
    {
        JoinBevel => SKStrokeJoin.Bevel,
        JoinMiter => SKStrokeJoin.Miter,
        _ => SKStrokeJoin.Round,
    };
}

/// <summary>A brush: LOGBRUSH, or a pattern brush with its bitmap.</summary>
internal sealed class GdiBrush : GdiObject
{
    public const int Solid = 0, Null = 1, Hatched = 2, Pattern = 3, DibPattern = 5, DibPatternPt = 6;

    public static readonly GdiBrush White = new(Solid, SKColors.White);
    public static readonly GdiBrush LightGray = new(Solid, new SKColor(0xC0, 0xC0, 0xC0));
    public static readonly GdiBrush Gray = new(Solid, new SKColor(0x80, 0x80, 0x80));
    public static readonly GdiBrush DarkGray = new(Solid, new SKColor(0x40, 0x40, 0x40));
    public static readonly GdiBrush Black = new(Solid, SKColors.Black);
    public static readonly GdiBrush Empty = new(Null, SKColors.Black);

    public GdiBrush(int style, SKColor color, int hatch = 0, SKImage? pattern = null, bool monochrome = false)
    {
        Style = style;
        Color = color;
        Hatch = hatch;
        PatternImage = pattern;
        Monochrome = monochrome;
    }

    public int Style { get; }

    public SKColor Color { get; }

    /// <summary>HS_HORIZONTAL (0) … HS_DIAGCROSS (5).</summary>
    public int Hatch { get; }

    /// <summary>The bitmap of a pattern brush; null for other styles (and patterns that couldn't be read).</summary>
    public SKImage? PatternImage { get; }

    /// <summary>
    /// A monochrome pattern: its black pixels are drawn in the text colour and its white ones in the background colour
    /// (the pattern is stored as black on white).
    /// </summary>
    public bool Monochrome { get; }
}

/// <summary>A logical font (LOGFONT).</summary>
internal sealed class GdiFont(int height, int width, int escapement, int weight, bool italic, bool underline, bool strikeOut,
    int charSet, string faceName) : GdiObject
{
    /// <summary>Logical units: below 0 the em height, above 0 the cell height (ascent + descent), 0 a default size.</summary>
    public int Height { get; } = height;

    /// <summary>Average character width in logical units; 0 = as designed.</summary>
    public int Width { get; } = width;

    /// <summary>Angle of the baseline in tenths of a degree, counter-clockwise.</summary>
    public int Escapement { get; } = escapement;

    public int Weight { get; } = weight;

    public bool Italic { get; } = italic;

    public bool Underline { get; } = underline;

    public bool StrikeOut { get; } = strikeOut;

    public int CharSet { get; } = charSet;

    public string FaceName { get; } = faceName;

    public const int AnsiCharSet = 0, DefaultCharSet = 1, SymbolCharSet = 2, ShiftJisCharSet = 128, HangulCharSet = 129,
        JohabCharSet = 130, Gb2312CharSet = 134, ChineseBig5CharSet = 136, OemCharSet = 255;

    /// <summary>The font a device context starts with (the System font: a sans-serif 16 pixels high).</summary>
    public static readonly GdiFont Default = new(16, 0, 0, 700, false, false, false, DefaultCharSet, "");

    /// <summary>The code page ANSI text in this font is stored in.</summary>
    public int CodePage => CharSet switch
    {
        ShiftJisCharSet => 932,
        HangulCharSet => 949,
        JohabCharSet => 1361,
        Gb2312CharSet => 936,
        ChineseBig5CharSet => 950,
        161 => 1253, // Greek
        162 => 1254, // Turkish
        163 => 1258, // Vietnamese
        177 => 1255, // Hebrew
        178 => 1256, // Arabic
        186 => 1257, // Baltic
        204 => 1251, // Russian
        222 => 874, // Thai
        238 => 1250, // Eastern Europe
        OemCharSet => 437,
        _ => 1252,
    };

    /// <summary>The language the font's character set stands for, to pick a font for its text.</summary>
    public string Language => CharSet switch
    {
        HangulCharSet or JohabCharSet => "ko",
        ShiftJisCharSet => "ja",
        Gb2312CharSet => "zh-Hans",
        ChineseBig5CharSet => "zh-Hant",
        _ => "",
    };
}

/// <summary>A logical palette.</summary>
internal sealed class GdiPalette(SKColor[] entries) : GdiObject
{
    public SKColor[] Entries { get; } = entries;
}

/// <summary>A region (WMF: in logical units; EMF regions are given with each record, in device units).</summary>
internal sealed class GdiRegion(SKPath path) : GdiObject
{
    public SKPath Path { get; } = path;
}

/// <summary>Objects the renderer keeps a place for but doesn't use (colour spaces).</summary>
internal sealed class GdiPlaceholder : GdiObject
{
    public static readonly GdiPlaceholder Instance = new();
}
