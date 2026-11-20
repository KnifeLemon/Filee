// RtfReader: Word drawing objects (\shp) holding a picture or text, and the properties of pictures.
//
// A floating picture is a shape: {\shp{\*\shpinst \shpleft ... \shpbottom, what they are measured from (\shpbx*,
// \shpby*), the wrap (\shpwr, \shpfblwtxt) and properties {\sp{\sn name}{\sv value}}, the picture in the pib
// property's value}{\shprslt a copy for programs without shapes}}. Such a shape becomes a floating HImage. A text box
// is a shape with its paragraphs in {\shptxt ...} inside \shpinst: it becomes an HTextBox with the shape's size,
// place, fill (fillColor), outline (lineColor, lineWidth, lineDashing), padding (dxTextLeft ...) and vertical
// alignment (anchorText). Either way the \shprslt copy is skipped; other shapes (rectangles, lines) are still read
// from their \shprslt. A picture's own properties (\*\picprop, as Word writes for pictures in the text) give its
// rotation and mirroring.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class RtfReader
{
    /// <summary>A drawing object being read, or the properties of a picture (\*\picprop).</summary>
    private sealed class ShapeContext
    {
        /// <summary>Properties a shape keeps; Word writes a few dozen.</summary>
        private const int MaxProperties = 128;

        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }

        /// <summary>What the position is measured from: \shpbxpage / \shpbxmargin / \shpbxcolumn, \shpbypage / \shpbymargin / \shpbypara.</summary>
        public string Horizontal { get; set; } = "COLUMN";
        public string Vertical { get; set; } = "PARA";

        /// <summary>\shpwrN: 1 text above and below, 2 around, 3 none (in front of or behind the text), 4 tight, 5 through.</summary>
        public int Wrap { get; set; } = 3;

        /// <summary>\shpfblwtxt1: behind the text.</summary>
        public bool Behind { get; set; }
        public int? Z { get; set; }

        /// <summary>The name (\sn) and value (\sv) of the property being read.</summary>
        public StringBuilder Name { get; } = new();
        public StringBuilder Value { get; } = new();

        public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

        /// <summary>The picture of the pib property, read from its \pict group.</summary>
        public HImage? Picture { get; set; }

        /// <summary>The value being read is the pib property's: its \pict group is the shape's picture.</summary>
        public bool PictureValue { get; set; }

        /// <summary>The paragraphs of a text box (\shptxt), once read; null for a shape without text.</summary>
        public List<HBlock>? Text { get; set; }

        public void Set(string name, string value)
        {
            if (name.Length > 0 && (Properties.Count < MaxProperties || Properties.ContainsKey(name)))
                Properties[name] = value;
        }

        public int? Number(string name) =>
            Properties.TryGetValue(name, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

        public bool Flag(string name) => Number(name) is { } value && value != 0;

        /// <summary>Rotation (\sv in 1/65536 degree) and mirroring; null for none.</summary>
        public HTransform? Transform()
        {
            var transform = new HTransform((Number("rotation") ?? 0) / 65536.0 % 360, Flag("fFlipH"), Flag("fFlipV"));
            return transform.IsIdentity ? null : transform;
        }
    }

    private void StartShape()
    {
        var shape = _g.Shape = new ShapeContext();
        _g.Closer = () => EndShape(shape);
    }

    private void StartShapeValue(ShapeContext shape)
    {
        _g.Dest = Dest.ShapeValue;
        var name = shape.Name.ToString().Trim();
        shape.Value.Clear();
        shape.PictureValue = name == "pib";
        _g.Closer = () =>
        {
            if (!shape.PictureValue)
                shape.Set(name, shape.Value.ToString().Trim());
            shape.PictureValue = false;
        };
    }

    /// <summary>A word of \*\shpinst (or \*\picprop): the shape's place and wrap.</summary>
    private void ShapeWord(string word, int? parameter)
    {
        if (_g.Shape is not { } shape)
            return;
        var value = parameter ?? 0;
        switch (word)
        {
            case "shpleft": shape.Left = value; break;
            case "shptop": shape.Top = value; break;
            case "shpright": shape.Right = value; break;
            case "shpbottom": shape.Bottom = value; break;
            case "shpbxpage": shape.Horizontal = "PAPER"; break;
            case "shpbxmargin": shape.Horizontal = "PAGE"; break;
            case "shpbxcolumn": shape.Horizontal = "COLUMN"; break;
            case "shpbypage": shape.Vertical = "PAPER"; break;
            case "shpbymargin": shape.Vertical = "PAGE"; break;
            case "shpbypara": shape.Vertical = "PARA"; break;
            case "shpwr": shape.Wrap = value; break;
            case "shpfblwtxt": shape.Behind = value != 0; break;
            case "shpz": shape.Z = value; break;
        }
    }

    /// <summary>
    /// The text of a text box (\shptxt): read like a note's, into a story of its own, and kept for the shape, which
    /// becomes the box when it ends.
    /// </summary>
    private void StartShapeText(ShapeContext shape)
    {
        var story = new Story();
        _g.Dest = Dest.Text;
        _g.Story = story;
        _g.Sink = null;
        _g.Field = null;
        _g.Para = ParaProps.Default;
        // Shape words in the box's text are for shapes in it, not for this one.
        _g.Shape = null;
        _budget.Object();
        var level = _budget.Enter();
        var group = _g;
        _g.Closer = () =>
        {
            var inner = _g;
            _g = group; // end the box's last paragraph with its own properties
            EndParagraph(story, force: false);
            CloseTable(story);
            _g = inner;
            level.Dispose();
            shape.Text = story.Blocks;
        };
    }

    /// <summary>The shape's box (twips), each side kept to a few pages.</summary>
    private static (int Width, int Height) Size(ShapeContext shape) =>
        ((int)Math.Clamp((long)shape.Right - shape.Left, 0, 31680 * 4), (int)Math.Clamp((long)shape.Bottom - shape.Top, 0, 31680 * 4));

    /// <summary>The distance text keeps from a floating shape (EMU), as far as it gives one; Word's own default is 1/8 inch at the sides.</summary>
    private static HInsets WrapDistance(ShapeContext shape)
    {
        int Distance(string name, int fallback) => shape.Number(name) is { } emu ? (int)Math.Clamp(emu / 127.0, 0, 31680 * 5) : fallback;
        return new HInsets(Distance("dxWrapDistLeft", 900), Distance("dxWrapDistRight", 900), Distance("dyWrapDistTop", 0), Distance("dyWrapDistBottom", 0));
    }

    /// <summary>A shape ends: its picture or its text box, floating where the shape is (or in the text, fPseudoInline).</summary>
    private void EndShape(ShapeContext shape)
    {
        if (shape.Picture is not { } image)
        {
            if (shape.Text is { } text)
                AddInline(TextBox(shape, text));
            return;
        }
        // The shape's box is the picture's size.
        var (width, height) = Size(shape);
        if (width > 0 && height > 0)
        {
            image.Width = HwpxUnits.FromTwips(width);
            image.Height = HwpxUnits.FromTwips(height);
        }
        image.Transform = shape.Transform() ?? image.Transform;
        if (image.Crop is null && Crop(shape) is { } crop)
            image.Crop = crop;
        // fPseudoInline: a picture in the text that Word wrote as a shape.
        if (!shape.Flag("fPseudoInline"))
            image.Anchor = Anchor(shape);
        if (shape.Z is { } z && image.Anchor is not null)
            image.ZOrder = z;
        if (image.Anchor is not null)
            image.OuterMargin = WrapDistance(shape);
        AddInline(image);
    }

    /// <summary>
    /// A text box from a shape with text: shapeType 202 (text box), 1 (rectangle), 2 (rounded, adjustValue the corner
    /// in 1/21600 of the shorter side) or 3 (ellipse). Without the properties Word's defaults apply: white fill, a
    /// black 0.75 pt outline, 0.1 inch padding at the sides and 0.05 inch above and below.
    /// </summary>
    private static HTextBox TextBox(ShapeContext shape, List<HBlock> text)
    {
        var (width, height) = Size(shape);
        var box = new HTextBox
        {
            Width = HwpxUnits.FromTwips(Math.Max(1, width)),
            Height = HwpxUnits.FromTwips(Math.Max(1, height)),
            Transform = shape.Transform(),
            Shape = shape.Number("shapeType") == 3 ? HShapeKind.Ellipse : HShapeKind.Rectangle,
            Fill = shape.Number("fFilled") is not 0 ? ShapeColor(shape.Number("fillColor") ?? 0xFFFFFF) : null,
            Line = shape.Number("fLine") is 0 ? HBorder.None : Outline(shape),
            Padding = new HInsets(Emu(shape, "dxTextLeft", 91440), Emu(shape, "dxTextRight", 91440), Emu(shape, "dyTextTop", 45720),
                Emu(shape, "dyTextBottom", 45720)),
            VerticalAlign = shape.Number("anchorText") switch
            {
                1 or 4 => HVerticalAlign.Center,
                2 or 5 => HVerticalAlign.Bottom,
                _ => HVerticalAlign.Top,
            },
        };
        if (shape.Number("shapeType") == 2)
            box.CornerRatio = (int)Math.Round(Math.Clamp(shape.Number("adjustValue") ?? 3600, 0, 10800) * 100.0 / 21600);
        if (!shape.Flag("fPseudoInline"))
        {
            box.Anchor = Anchor(shape);
            box.OuterMargin = WrapDistance(shape);
            if (shape.Z is { } z)
                box.ZOrder = z;
        }
        box.Blocks.AddRange(text);
        return box;

        // A length in EMU as HWPUNIT, kept to a few pages.
        static int Emu(ShapeContext shape, string name, int fallback) => (int)Math.Clamp((shape.Number(name) ?? fallback) / 127.0, 0, 31680 * 5);
    }

    /// <summary>A shape's outline: lineColor, lineWidth (EMU; Word's default 0.75 pt), lineDashing and lineStyle (1 double).</summary>
    private static HBorder Outline(ShapeContext shape)
    {
        var style = shape.Number("lineStyle") is 1 ? HBorderStyle.Double : shape.Number("lineDashing") switch
        {
            1 or 6 or 7 => HBorderStyle.Dash,
            2 or 5 => HBorderStyle.Dot,
            3 or 4 or 8 or 9 or 10 => HBorderStyle.DashDot,
            _ => HBorderStyle.Solid,
        };
        var width = Math.Clamp((shape.Number("lineWidth") ?? 9525) / 36000.0, 0.05, 20);
        return new HBorder(style, width, ShapeColor(shape.Number("lineColor") ?? 0) ?? "#000000");
    }

    /// <summary>A shape property's colour (0x00BBGGRR) as "#RRGGBB"; null for system and scheme colours (higher bits set).</summary>
    private static string? ShapeColor(int value) =>
        value is < 0 or > 0xFFFFFF ? null : $"#{value & 0xFF:X2}{value >> 8 & 0xFF:X2}{value >> 16 & 0xFF:X2}";

    /// <summary>cropFromLeft ... (fractions in 1/65536 of the picture) of a shape; null for none.</summary>
    private static HCrop? Crop(ShapeContext shape)
    {
        double Side(string name) => Math.Clamp((shape.Number(name) ?? 0) / 65536.0, 0, 0.99);
        var crop = new HCrop(Side("cropFromLeft"), Side("cropFromTop"), Side("cropFromRight"), Side("cropFromBottom"));
        return crop.IsEmpty || crop.Left + crop.Right >= 1 || crop.Top + crop.Bottom >= 1 ? null : crop;
    }

    /// <summary>Where a floating shape is: its offsets or alignment (posh, posv) from what they are measured from (posrelh, posrelv).</summary>
    private static HAnchor Anchor(ShapeContext shape)
    {
        var horizontal = shape.Number("posrelh") switch
        {
            0 or 4 or 5 or 6 or 7 => "PAGE",
            1 => "PAPER",
            2 or 3 => "COLUMN",
            _ => shape.Horizontal,
        };
        var vertical = shape.Number("posrelv") switch
        {
            0 or 4 or 5 or 6 or 7 => "PAGE",
            1 => "PAPER",
            2 or 3 => "PARA",
            _ => shape.Vertical,
        };
        var posh = shape.Number("posh") switch
        {
            2 => "CENTER",
            3 => "RIGHT",
            4 => "INSIDE",
            5 => "OUTSIDE",
            1 => "LEFT",
            _ => null,
        };
        var posv = shape.Number("posv") switch
        {
            2 => "CENTER",
            3 => "BOTTOM",
            4 => "INSIDE",
            5 => "OUTSIDE",
            1 => "TOP",
            _ => null,
        };
        var wrap = shape.Wrap switch
        {
            1 => "TOP_AND_BOTTOM",
            2 or 4 or 5 => "SQUARE",
            _ => shape.Behind ? "BEHIND_TEXT" : "IN_FRONT_OF_TEXT",
        };
        static int Offset(int twips) => HwpxUnits.FromTwips(Math.Clamp(twips, -31680 * 4, 31680 * 4));
        return new HAnchor(
            horizontal, posh ?? "LEFT", posh is null ? Offset(shape.Left) : 0,
            vertical, posv ?? "TOP", posv is null ? Offset(shape.Top) : 0,
            wrap, shape.Number("fAllowOverlap") is not 0);
    }
}
