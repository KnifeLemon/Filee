// Colours of a JSON Canvas card or line: "1" to "6" for the six preset colours, whose values the specification leaves to
// each app, or "#RRGGBB". The presets are Obsidian's of its light theme, so a board looks as it does there; the board is
// drawn on white, as it is printed.

using System.Globalization;

namespace Filee.Engines.Vector.Canvas;

internal static class CanvasColors
{
    /// <summary>Obsidian's light-theme red, orange, yellow, green, cyan and purple.</summary>
    private static readonly (byte R, byte G, byte B)[] Presets =
    [
        (0xE9, 0x31, 0x47), (0xEC, 0x75, 0x00), (0xE0, 0xAC, 0x00), (0x08, 0xB9, 0x4E), (0x00, 0xBF, 0xBC), (0x78, 0x52, 0xEE),
    ];

    /// <summary>The paper the board is drawn on.</summary>
    public const string Background = "#FFFFFF";

    /// <summary>Text, muted text (file names, group labels) and links, as Obsidian's light theme has them.</summary>
    public const string Text = "#222222";

    public const string Muted = "#5C5C5C";

    public const string Faint = "#ABABAB";

    public const string Accent = "#7852EE";

    /// <summary>A card's frame and the lines without a colour of their own.</summary>
    public const string Border = "#D6D6D6";

    public const string Line = "#A6A6A6";

    /// <summary>The shading of code, quotes and highlights.</summary>
    public const string CodeBackground = "#F5F5F5";

    public const string Highlight = "#FFF3A3";

    /// <summary>The colour a card or line was given, or null for none (or one that can't be read).</summary>
    public static (byte R, byte G, byte B)? Parse(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;
        color = color.Trim();
        if (color.Length == 1 && color[0] is >= '1' and <= '6')
            return Presets[color[0] - '1'];
        if (color[0] != '#')
            return null;
        var hex = color[1..];
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length == 8)
            hex = hex[..6]; // #RRGGBBAA: the alpha is left out, the card is shaded by its own rule
        return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? ((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : null;
    }

    /// <summary>The colour as <c>#RRGGBB</c>.</summary>
    public static string Hex((byte R, byte G, byte B) color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// The colour mixed with white, <paramref name="amount"/> of it (0 white, 1 the colour): the shading of a coloured
    /// card or group, worked out here rather than drawn with transparency so a PDF prints the same everywhere.
    /// </summary>
    public static string Tint((byte R, byte G, byte B) color, double amount)
    {
        static byte Mix(byte value, double amount) => (byte)Math.Round(255 + (value - 255) * amount);
        return Hex((Mix(color.R, amount), Mix(color.G, amount), Mix(color.B, amount)));
    }

    /// <summary>The colour made darker for text on its own tint (a yellow group's label stays readable).</summary>
    public static string Darker((byte R, byte G, byte B) color, double amount = 0.25)
    {
        static byte Dim(byte value, double amount) => (byte)Math.Round(value * (1 - amount));
        return Hex((Dim(color.R, amount), Dim(color.G, amount), Dim(color.B, amount)));
    }
}
