// RtfReader: Word drawing objects (\shp) holding a picture, and the properties of pictures.
//
// A floating picture is a shape: {\shp{\*\shpinst \shpleft ... \shpbottom, what they are measured from (\shpbx*,
// \shpby*), the wrap (\shpwr, \shpfblwtxt) and properties {\sp{\sn name}{\sv value}}, the picture in the pib
// property's value}{\shprslt a copy for programs without shapes}}. Such a shape becomes a floating HImage (its
// \shprslt is skipped); other shapes (text boxes) are read from their \shprslt as before. A picture's own properties
// (\*\picprop, as Word writes for pictures in the text) give its rotation and mirroring.

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

    /// <summary>A shape with a picture ends: the picture, floating where the shape is.</summary>
    private void EndShape(ShapeContext shape)
    {
        if (shape.Picture is not { } image)
            return;
        // The shape's box is the picture's size.
        var (width, height) = (Math.Clamp((long)shape.Right - shape.Left, 0, 31680 * 4), Math.Clamp((long)shape.Bottom - shape.Top, 0, 31680 * 4));
        if (width > 0 && height > 0)
        {
            image.Width = HwpxUnits.FromTwips((int)width);
            image.Height = HwpxUnits.FromTwips((int)height);
        }
        image.Transform = shape.Transform() ?? image.Transform;
        if (image.Crop is null && Crop(shape) is { } crop)
            image.Crop = crop;
        // fPseudoInline: a picture in the text that Word wrote as a shape.
        if (!shape.Flag("fPseudoInline"))
            image.Anchor = Anchor(shape);
        if (shape.Z is { } z && image.Anchor is not null)
            image.ZOrder = z;
        // The distance text keeps from it (EMU), as far as the shape gives one; Word's own default is 1/8 inch at the sides.
        int Distance(string name, int fallback) => shape.Number(name) is { } emu ? (int)Math.Clamp(emu / 127.0, 0, 31680 * 5) : fallback;
        if (image.Anchor is not null)
        {
            image.OuterMargin = new HInsets(Distance("dxWrapDistLeft", 900), Distance("dxWrapDistRight", 900),
                Distance("dyWrapDistTop", 0), Distance("dyWrapDistBottom", 0));
        }
        AddInline(image);
    }

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
