// Fills, shadows and WordArt of OfficeArt shapes ([MS-ODRAW] 2.3.7, 2.3.13, 2.3.22) as DrawingML.
//
// - Fills (fillType): solid; picture and texture (fillBlip); pattern (fillBlip holds the two-colour pattern bitmap: its
//   dark pixels take fillColor and its light ones fillBackColor, written as a tiled picture); the gradients:
//   - shade and shadeScale (4, 7): along a vector that points up at fillAngle 0 and turns clockwise with it; the last
//     colour (fillBackColor) sits at fillFocus percent along it and fillColor at the ends (focus 50: both ends, an axial
//     gradient; a negative focus swaps the colours); fillShadeColors, when there are some, are the colours from
//     fillColor's end to the last colour's;
//   - shadeCenter and shadeShape (5, 6): outwards from the rectangle fillToLeft / fillToTop / fillToRight /
//     fillToBottom (the middle for shadeShape), focus counted from the outside in;
//   - shadeTitle (8) as a linear gradient; background (9) as no fill, the slide's background showing through.
// - Shadows (fShadow): an outer shadow with the offset (shadowOffsetX / Y), colour and opacity, sharp as PowerPoint
//   97–2003 draws them. The slide renderer doesn't draw shadows, so the capability check names them.
// - WordArt (geometry text: MSOSPT 24–31 and 136–175, or fGtext): the text (gtextUNICODE) in its font (gtextFont),
//   bold and italic, centred in the shape and sized to fill it, with the shape's fill and outline as the text's. The
//   shape's warp (arch, wave, ...) is written as the DrawingML warp of the same name; the renderer doesn't warp text, so
//   the check names it ("WordArt text warp") and such a deck goes to LibreOffice when it's installed. Plain WordArt
//   (no warp) is drawn.

using System.Globalization;
using System.Text;
using Filee.Engines.Office.OfficeArt;
using SkiaSharp;

namespace Filee.Engines.Office.Ppt;

internal sealed partial class PptToPptx
{
    private const int FillBackOpacity = 0x0184;
    private const int FillAngle = 0x018B;
    private const int FillFocus = 0x018C;
    private const int FillToLeft = 0x018D;
    private const int FillToTop = 0x018E;
    private const int FillToRight = 0x018F;
    private const int FillToBottom = 0x0190;
    private const int FillShadeColors = 0x0197;

    private const int ShadowType = 0x0200;
    private const int ShadowColor = 0x0201;
    private const int ShadowOpacity = 0x0204;
    private const int ShadowOffsetX = 0x0205;
    private const int ShadowOffsetY = 0x0206;
    private const int ShadowBooleans = 0x023F;

    private const int GtextUnicode = 0x00C0;
    private const int GtextFont = 0x00C5;
    private const int GtextBooleans = 0x00FF;

    /// <summary>The DrawingML warps of WordArt shapes 136–175 (MSOSPT order); 136 (plain text) has none.</summary>
    private static readonly string[] Warps =
    [
        "", "textStop", "textTriangle", "textTriangleInverted", "textChevron", "textChevronInverted", "textRingInside",
        "textRingOutside", "textArchUp", "textArchDown", "textCircle", "textButton", "textArchUpPour", "textArchDownPour",
        "textCirclePour", "textButtonPour", "textCurveUp", "textCurveDown", "textCascadeUp", "textCascadeDown", "textWave1",
        "textWave2", "textDoubleWave1", "textWave4", "textInflate", "textDeflate", "textInflateBottom", "textDeflateBottom",
        "textInflateTop", "textDeflateTop", "textDeflateInflate", "textDeflateInflateDeflate", "textFadeRight", "textFadeLeft",
        "textFadeUp", "textFadeDown", "textSlantUp", "textSlantDown", "textCanUp", "textCanDown",
    ];

    /// <summary>Pattern pictures written, by the BLIP index and the two colours.</summary>
    private readonly Dictionary<(uint Pib, uint Fore, uint Back), string?> _patterns = [];

    // ───────────────────────── Fills ─────────────────────────

    /// <summary>The fill: none, solid, picture, pattern or gradient. Null when nothing can be drawn.</summary>
    private string? FillXml(Dictionary<int, uint> properties, Sheet sheet, OfficeArtShape shape)
    {
        if (!OfficeArtProperties.Flag(properties, OfficeArtProperties.FillBooleans, 4, true))
            return "<a:noFill/>";
        var type = properties.GetValueOrDefault(OfficeArtProperties.FillType);
        var pib = properties.GetValueOrDefault(OfficeArtProperties.FillBlip);
        if (type is 2 or 3 && pib > 0 && MediaPart(pib) is { } media)
        {
            var embed = sheet.Relationship("image", media);
            return type == 2
                ? $"<a:blipFill rotWithShape=\"1\"><a:blip r:embed=\"{embed}\"/><a:tile tx=\"0\" ty=\"0\" sx=\"100000\" sy=\"100000\" algn=\"tl\"/></a:blipFill>"
                : $"<a:blipFill rotWithShape=\"1\"><a:blip r:embed=\"{embed}\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill>";
        }
        if (type == 1 && pib > 0 && PatternPart(pib, properties, sheet) is { } pattern)
        {
            var embed = sheet.Relationship("image", pattern);
            return $"<a:blipFill rotWithShape=\"1\"><a:blip r:embed=\"{embed}\"/><a:tile tx=\"0\" ty=\"0\" sx=\"100000\" sy=\"100000\" algn=\"tl\"/></a:blipFill>";
        }
        if (type == 9)
            return "<a:noFill/>"; // the slide's background shows through
        if (type is >= 4 and <= 8)
            return GradientXml(properties, type, shape);
        return $"<a:solidFill>{ForeColor(properties)}</a:solidFill>";
    }

    private static string ForeColor(Dictionary<int, uint> properties) =>
        ColorRef(properties.GetValueOrDefault(OfficeArtProperties.FillColor, 0xFFFFFFu), properties,
            properties.GetValueOrDefault(OfficeArtProperties.FillOpacity, 0x10000u), 0);

    private static string BackColor(Dictionary<int, uint> properties) =>
        ColorRef(properties.GetValueOrDefault(OfficeArtProperties.FillBackColor, 0xFFFFFFu), properties,
            properties.GetValueOrDefault(FillBackOpacity, 0x10000u), 0);

    /// <summary>A gradient fill of fill type <paramref name="type"/> (4–8).</summary>
    private string GradientXml(Dictionary<int, uint> properties, uint type, OfficeArtShape shape)
    {
        var focus = Math.Clamp((int)properties.GetValueOrDefault(FillFocus, 0u), -100, 100);
        var (fore, back) = (ForeColor(properties), BackColor(properties));
        if (focus < 0)
        {
            (fore, back) = (back, fore);
            focus = -focus;
        }
        // The colours from fillColor's end (0) to the last colour's (1).
        var colors = new List<(double Position, string Color)>();
        if (ComplexShadeColors(properties, shape) is { Count: >= 2 } shades)
            colors.AddRange(shades);
        else
            colors.AddRange([(0, fore), (1, back)]);

        // Along the vector (or from the outside in): the last colour at f, fillColor's end at 0 and 1.
        var f = focus / 100.0;
        var stops = new SortedDictionary<long, string>();
        void Stop(double t, string color) => stops.TryAdd((long)Math.Round(Math.Clamp(t, 0, 1) * 100000), color);
        foreach (var (position, color) in colors)
        {
            if (f > 0)
                Stop(f * position, color);
            if (f < 1)
                Stop(1 - (1 - f) * position, color);
        }

        var radial = type is 5 or 6;
        var xml = new StringBuilder("<a:gradFill rotWithShape=\"1\"><a:gsLst>");
        foreach (var (position, color) in radial ? stops.Select(s => (100000 - s.Key, s.Value)).OrderBy(s => s.Item1) : stops.Select(s => (s.Key, s.Value)))
            xml.Append(CultureInfo.InvariantCulture, $"<a:gs pos=\"{position}\">{color}</a:gs>");
        xml.Append("</a:gsLst>");
        if (radial)
        {
            long Inset(int pid, uint fallback) => (long)Math.Round(Math.Clamp((int)properties.GetValueOrDefault(pid, fallback) / 65536.0, 0, 1) * 100000);
            var (l, t, r, b) = type == 6
                ? (50000L, 50000L, 50000L, 50000L)
                : (Inset(FillToLeft, 0), Inset(FillToTop, 0), 100000 - Inset(FillToRight, 0), 100000 - Inset(FillToBottom, 0));
            xml.Append(CultureInfo.InvariantCulture, $"<a:path path=\"{(type == 6 ? "shape" : "rect")}\"><a:fillToRect l=\"{l}\" t=\"{t}\" r=\"{r}\" b=\"{b}\"/></a:path>");
        }
        else
        {
            // DrawingML angles run clockwise from left-to-right; the vector points up (270°) at fillAngle 0.
            var angle = ((270 + (int)properties.GetValueOrDefault(FillAngle, 0u) / 65536.0) % 360 + 360) % 360;
            xml.Append(CultureInfo.InvariantCulture, $"<a:lin ang=\"{(long)Math.Round(angle * 60000) % 21600000}\" scaled=\"{(type == 7 ? 1 : 0)}\"/>");
        }
        return xml.Append("</a:gradFill>").ToString();
    }

    /// <summary>fillShadeColors: colours (OfficeArtCOLORREF) with their positions (16.16), in order; null when there are none.</summary>
    private List<(double Position, string Color)>? ComplexShadeColors(Dictionary<int, uint> properties, OfficeArtShape shape)
    {
        if (ComplexOf(shape, FillShadeColors) is not { } data)
            return null;
        var opacity = properties.GetValueOrDefault(OfficeArtProperties.FillOpacity, 0x10000u);
        var list = new List<(double, string)>();
        foreach (var element in ArrayElements(data))
        {
            if (element.Count < 8)
                return null;
            var span = element.AsSpan();
            var color = BitConverter.ToUInt32(span);
            var position = Math.Clamp(BitConverter.ToInt32(span[4..]) / 65536.0, 0, 1);
            list.Add((position, ColorRef(color, properties, opacity, 0)));
        }
        return list;
    }

    /// <summary>
    /// A pattern fill's picture: the pattern bitmap of BLIP <paramref name="pib"/> with its dark pixels in fillColor and
    /// its light ones in fillBackColor, as a PNG part; null when the bitmap can't be read (the first colour is used).
    /// </summary>
    private string? PatternPart(uint pib, Dictionary<int, uint> properties, Sheet sheet)
    {
        var fore = Resolved(properties.GetValueOrDefault(OfficeArtProperties.FillColor, 0xFFFFFFu), properties, sheet.Scheme);
        var back = Resolved(properties.GetValueOrDefault(OfficeArtProperties.FillBackColor, 0xFFFFFFu), properties, sheet.Scheme);
        if (fore is null || back is null)
            return null;
        var key = (pib, fore.Value, back.Value);
        if (_patterns.TryGetValue(key, out var cached))
            return cached;
        string? name = null;
        if (_file.Picture(pib) is { } picture)
        {
            using var bitmap = SKBitmap.Decode(picture.Bytes);
            if (bitmap is { Width: > 0 and <= 64, Height: > 0 and <= 64 })
            {
                using var tile = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                static SKColor Of(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                for (var y = 0; y < bitmap.Height; y++)
                {
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        var light = pixel.Red * 299 + pixel.Green * 587 + pixel.Blue * 114 >= 128 * 1000;
                        tile.SetPixel(x, y, Of(light ? back.Value : fore.Value));
                    }
                }
                using var data = tile.Encode(SKEncodedImageFormat.Png, 100);
                name = $"ppt/media/pattern{_patterns.Count + 1}.png";
                _defaults.TryAdd("png", "image/png");
                using var stream = _zip.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression).Open();
                data.SaveTo(stream);
            }
        }
        _patterns[key] = name;
        return name;
    }

    /// <summary>An OfficeArtCOLORREF as RRGGBB: RGB, a scheme entry, or one of the shape's own colours; null for others.</summary>
    private static uint? Resolved(uint value, Dictionary<int, uint> properties, uint[] scheme, int depth = 0)
    {
        var flags = value >> 24;
        if ((flags & 0x08) != 0)
            return scheme[Math.Min(value & 0xFF, (uint)scheme.Length - 1)];
        if ((flags & 0x10) != 0)
        {
            if (depth > 0)
                return null;
            var other = (value & 0xFF) switch
            {
                0xF0 or 0xF7 => OfficeArtProperties.FillColor,
                0xF1 or 0xF2 => OfficeArtProperties.LineColor,
                0xF5 => OfficeArtProperties.FillBackColor,
                0xF6 => LineBackColor,
                _ => -1,
            };
            return other < 0 ? 0 : Resolved(properties.GetValueOrDefault(other), properties, scheme, depth + 1);
        }
        return flags == 0 ? (value & 0xFF) << 16 | (value & 0xFF00) | (value >> 16 & 0xFF) : null;
    }

    // ───────────────────────── Shadows ─────────────────────────

    /// <summary>An a:effectLst with the shape's shadow, or "" when it has none.</summary>
    private static string ShadowXml(Dictionary<int, uint> properties)
    {
        if (!OfficeArtProperties.Flag(properties, ShadowBooleans, 1, false))
            return "";
        var type = properties.GetValueOrDefault(ShadowType);
        if (type > 2)
            return ""; // perspective and emboss shadows: as PowerPoint 2007 does, none
        var dx = (double)(int)properties.GetValueOrDefault(ShadowOffsetX, 25400u);
        var dy = (double)(int)properties.GetValueOrDefault(ShadowOffsetY, 25400u);
        var distance = Math.Min((long)Math.Round(Math.Sqrt(dx * dx + dy * dy)), 20116800);
        var direction = ((Math.Atan2(dy, dx) * 180 / Math.PI) % 360 + 360) % 360;
        var color = ColorRef(properties.GetValueOrDefault(ShadowColor, 0x808080u), properties, properties.GetValueOrDefault(ShadowOpacity, 0x10000u), 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"<a:effectLst><a:outerShdw dist=\"{distance}\" dir=\"{(long)Math.Round(direction * 60000) % 21600000}\" algn=\"ctr\" rotWithShape=\"0\">{color}</a:outerShdw></a:effectLst>");
    }

    // ───────────────────────── WordArt ─────────────────────────

    /// <summary>The geometry text of a WordArt shape; null when it isn't one.</summary>
    private string? WordArtText(OfficeArtShape shape, Dictionary<int, uint> properties)
    {
        var wordArt = shape.Type is (>= 24 and <= 31) or (>= 136 and <= 175)
                      || OfficeArtProperties.Flag(properties, GtextBooleans, 14, false);
        if (!wordArt || ComplexOf(shape, GtextUnicode) is not { } data)
            return null;
        var text = Encoding.Unicode.GetString(data.AsSpan()[..(data.Count & ~1)]).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private void WriteWordArt(StringBuilder xml, OfficeArtShape shape, Box box, Dictionary<int, uint> properties, string text, Sheet sheet)
    {
        var id = sheet.NextId();
        var font = ComplexOf(shape, GtextFont) is { } face ? Encoding.Unicode.GetString(face.AsSpan()[..(face.Count & ~1)]).TrimEnd('\0') : "";
        if (font.Length == 0)
            font = "Arial";
        var bold = OfficeArtProperties.Flag(properties, GtextBooleans, 5, false);
        var italic = OfficeArtProperties.Flag(properties, GtextBooleans, 4, false);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\r', '\n');
        // The text fills the shape: as large as fits its height and, at about half an em a letter, its width.
        var height = box.Height / 8.0; // points
        var width = box.Width / 8.0;
        var longest = Math.Max(1, lines.Max(l => l.Length));
        var size = Math.Clamp(Math.Min(height / lines.Length / 1.2, width / (longest * 0.55)), 1, 4000);
        var warp = shape.Type is >= 137 and <= 175 ? Warps[shape.Type - 136] : shape.Type is >= 25 and <= 31 ? "textArchUp" : "";

        var fill = OfficeArtProperties.Flag(properties, OfficeArtProperties.FillBooleans, 4, true) ? FillXml(properties, sheet, shape) ?? "<a:noFill/>" : "<a:noFill/>";
        var outline = OfficeArtProperties.Flag(properties, OfficeArtProperties.LineBooleans, 3, true) ? LineXml(properties) : "";
        var run = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"<a:rPr lang=\"en-US\" sz=\"{(int)Math.Round(size * 100)}\"{(bold ? " b=\"1\"" : "")}{(italic ? " i=\"1\"" : "")} dirty=\"0\">"));
        // Outline before fill, as CT_TextCharacterProperties orders them.
        run.Append(outline.Contains("<a:noFill/></a:ln>", StringComparison.Ordinal) ? "" : outline).Append(fill)
            .Append("<a:latin typeface=\"").Append(Escape(font)).Append("\"/><a:ea typeface=\"").Append(Escape(font)).Append("\"/><a:cs typeface=\"")
            .Append(Escape(font)).Append("\"/></a:rPr>");

        xml.Append(CultureInfo.InvariantCulture, $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"WordArt {id - 1}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>")
            .Append(Xfrm(box, properties, shape.Flags, "")).Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln><a:noFill/></a:ln>")
            .Append(ShadowXml(properties)).Append("</p:spPr><p:txBody><a:bodyPr wrap=\"none\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" anchor=\"ctr\" rtlCol=\"0\">")
            .Append(warp.Length > 0 ? $"<a:prstTxWarp prst=\"{warp}\"><a:avLst/></a:prstTxWarp>" : "")
            .Append("<a:noAutofit/></a:bodyPr><a:lstStyle/>");
        foreach (var line in lines)
            xml.Append("<a:p><a:pPr algn=\"ctr\"/><a:r>").Append(run).Append("<a:t>").Append(Escape(line)).Append("</a:t></a:r></a:p>");
        xml.Append("</p:txBody></p:sp>");
    }
}
