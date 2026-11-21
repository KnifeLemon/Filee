// Normal appearances for annotations that have none, for PDF/A (ISO 19005-2 6.3.3: a conforming reader draws an
// annotation only from its appearance stream, so one without would show nothing). Made by PdfAConformance from the
// annotation's own entries, as a form XObject whose bounding box is the annotation's rectangle in page space (so the
// geometry entries, which are in page space, draw as they are):
//
// - Square, Circle (C border, IC fill, BS/Border width and dash), Line (L), PolyLine and Polygon (Vertices), Ink
//   (InkList): their shapes. Highlight (multiply blend), Underline, StrikeOut, Squiggly: from QuadPoints. Caret and
//   Text (a note): small drawings of their own. Stamp: a frame with the stamp's name. FreeText: its Contents in its DA
//   font, wrapped to the box. Link: its border when it has a visible one, else nothing (links need none).
// - Widgets: check boxes and radio buttons get an Off state and an on state (the AS, V or Yes) with the MK caption's
//   mark (check, circle, cross, square, diamond) drawn as a path; push buttons their caption; text fields and combo
//   boxes their value, list boxes their options with the chosen ones marked, in the field's DA font, colour and
//   quadding; signature fields an empty box. Background and border come from MK (BG, BC).
// - PrinterMark, TrapNet, Watermark, Redact: an empty appearance.
//
// Text is drawn with the DA font from the form's default resources when it is a simple font the text can be encoded
// in, else with Helvetica (WinAnsiEncoding): either way the font goes into the appearance's resources, and the font
// embedding (PdfAFonts) then embeds it like any other or refuses the PDF. Text that the font can't encode makes the
// annotation one Filee can't draw (null), and PdfAConformance refuses the PDF as before.

using System.Globalization;
using System.Text;
using Filee.Engines.Pdf.PostScript;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using UglyToad.PdfPig.Fonts.Standard14Fonts;

namespace Filee.Engines.Pdf;

/// <summary>Draws normal appearances for annotations that have none.</summary>
internal static class PdfAAppearances
{
    /// <summary>Characters of text drawn into one appearance at most.</summary>
    internal const int MaxText = 20_000;

    /// <summary>Points of an ink list, polygon or set of quadrilaterals drawn at most.</summary>
    internal const int MaxPoints = 100_000;

    private const double Kappa = 0.5523;

    private static PdfItem? Resolve(PdfItem? item) => PdfCompressor.Resolve(item);

    private static string Name(PdfDictionary? dictionary, string key) =>
        dictionary is not null && Resolve(dictionary.Elements[key]) is PdfName name ? name.Value : "";

    /// <summary>
    /// The normal appearance (a stream, or for buttons a dictionary of states, whose AS it sets) for
    /// <paramref name="annotation"/>; null when Filee can't draw one.
    /// </summary>
    public static PdfItem? Make(PdfDocument document, PdfDictionary annotation, string subtype, PdfDictionary? form)
    {
        if (PdfObjects.Rectangle(annotation.Elements["/Rect"]) is not { } rect)
            return null;
        var box = new Box(rect.X0, rect.Y0, rect.X1, rect.Y1);
        var drawing = new Drawing(document, box);
        switch (subtype)
        {
            case "/Square" or "/Circle":
                drawing.Shape(annotation, subtype == "/Circle");
                break;
            case "/Line":
                drawing.Lines(annotation, PdfObjects.Numbers(annotation, "/L") is { Length: 4 } line ? [line] : [], close: false);
                break;
            case "/PolyLine" or "/Polygon":
                drawing.Lines(annotation, PdfObjects.Numbers(annotation, "/Vertices") is { } vertices ? [vertices] : [], close: subtype == "/Polygon");
                break;
            case "/Ink":
                drawing.Lines(annotation, PdfObjects.Array(annotation, "/InkList")?.Elements.Select(PdfObjects.Numbers).OfType<double[]>().ToList() ?? [],
                    close: false);
                break;
            case "/Highlight" or "/Underline" or "/StrikeOut" or "/Squiggly":
                drawing.Markup(annotation, subtype);
                break;
            case "/Caret":
                drawing.Caret(annotation);
                break;
            case "/Text":
                drawing.Note(annotation);
                break;
            case "/Stamp":
                if (!drawing.Stamp(annotation))
                    return null;
                break;
            case "/FreeText":
                if (!drawing.FreeText(annotation, form))
                    return null;
                break;
            case "/Link":
                if (!drawing.LinkBorder(annotation))
                    return null; // nothing to draw: a link needs no appearance
                break;
            case "/Widget":
                return Widget(document, annotation, form, box);
            case "/PrinterMark" or "/TrapNet" or "/Watermark" or "/Redact":
                break;
            default:
                return null;
        }
        return drawing.Form().Reference;
    }

    // ---- widgets

    private static PdfItem? Widget(PdfDocument document, PdfDictionary widget, PdfDictionary? form, Box box)
    {
        var type = Inherited(widget, "/FT") is PdfName name ? name.Value : "";
        var flags = Inherited(widget, "/Ff") switch
        {
            PdfInteger i => i.Value,
            PdfIntegerObject i => i.Value,
            _ => 0,
        };
        var mk = Resolve(widget.Elements["/MK"]) as PdfDictionary;
        switch (type)
        {
            case "/Btn":
                return Button(document, widget, form, box, flags, mk);
            case "/Tx" or "/Ch":
                {
                    var drawing = new Drawing(document, box);
                    drawing.Frame(mk, widget, round: false);
                    if (!drawing.FieldText(widget, form, type, flags))
                        return null;
                    return drawing.Form().Reference;
                }
            case "/Sig":
                {
                    var drawing = new Drawing(document, box);
                    drawing.Frame(mk, widget, round: false);
                    return drawing.Form().Reference;
                }
            default:
                return null;
        }
    }

    private static PdfDictionary? Button(PdfDocument document, PdfDictionary widget, PdfDictionary? form, Box box, int flags, PdfDictionary? mk)
    {
        var push = (flags & (1 << 16)) != 0;
        var radio = (flags & (1 << 15)) != 0;
        var states = new PdfDictionary(document);
        var off = new Drawing(document, box);
        off.Frame(mk, widget, round: radio && !push);
        if (push)
        {
            if (mk is not null && mk.Elements.GetString("/CA") is { Length: > 0 } caption && !off.Caption(widget, form, caption))
                return null;
            states.Elements["/Off"] = off.Form().Reference;
            widget.Elements.SetName("/AS", "/Off");
            return states;
        }
        var state = Name(widget, "/AS");
        var value = Inherited(widget, "/V") is PdfName v ? v.Value : "";
        var on = state is { Length: > 0 } and not "/Off" ? state : value is { Length: > 0 } and not "/Off" ? value : "/Yes";
        var marked = new Drawing(document, box);
        marked.Frame(mk, widget, round: radio);
        var mark = mk?.Elements.GetString("/CA") is { Length: > 0 } ca ? ca[0] : radio ? 'l' : '4';
        marked.Mark(widget, form, mark);
        states.Elements[on] = marked.Form().Reference;
        states.Elements["/Off"] = off.Form().Reference;
        widget.Elements.SetName("/AS", state.Length > 0 ? state : value == on ? on : "/Off");
        return states;
    }

    /// <summary>A field entry from the widget or the fields it is a kid of.</summary>
    private static PdfItem? Inherited(PdfDictionary widget, string key)
    {
        PdfDictionary? field = widget;
        for (var depth = 0; field is not null && depth < 64; depth++)
        {
            if (Resolve(field.Elements[key]) is { } value)
                return value;
            field = Resolve(field.Elements["/Parent"]) as PdfDictionary;
        }
        return null;
    }

    private readonly record struct Box(double X0, double Y0, double X1, double Y1)
    {
        public double Width => X1 - X0;
        public double Height => Y1 - Y0;
    }

    /// <summary>A font text is drawn with: its codes for characters and its widths.</summary>
    private sealed class TextFont
    {
        private readonly Dictionary<int, byte> _codes = [];
        private readonly double[] _widths = new double[256];

        private TextFont(PdfItem reference, PdfDictionary font)
        {
            Reference = reference;
            var standard = Standard14.GetMappedFontName(PdfAFonts.BaseName(font).Replace(" ", "", StringComparison.Ordinal)) is { Length: > 0 } mapped
                && Standard14.IsFontInStandard14(mapped) ? mapped : null;
            string?[]? builtIn = standard is "Symbol" or "ZapfDingbats" ? SimpleEncodings.Named(standard) : null;
            var names = SimpleEncodings.Resolve(font, builtIn, out _, symbolic: builtIn is not null);
            var metrics = standard is null ? null : Standard14.GetAdobeFontMetrics(standard);
            var first = (int)PdfObjects.Number(font, "/FirstChar", 0);
            var listed = PdfObjects.Numbers(font, "/Widths");
            for (var code = 255; code >= 32; code--)
            {
                if (names[code] is { } glyph && SimpleEncodings.Unicode(glyph) is var u and > 0)
                    _codes[u] = (byte)code;
                _widths[code] = listed is not null && code - first >= 0 && code - first < listed.Length ? listed[code - first]
                    : metrics is not null && names[code] is { } n && metrics.CharacterMetrics.TryGetValue(n, out var metric) ? metric.Width.X
                    : 500;
            }
        }

        public PdfItem Reference { get; }

        public static TextFont? Of(PdfItem? item)
        {
            if (Resolve(item) is not PdfDictionary font || Name(font, "/Subtype") is not ("/Type1" or "/TrueType" or "/MMType1"))
                return null;
            return new TextFont(item is PdfReference reference ? reference : font, font);
        }

        public static TextFont Helvetica(PdfDocument document)
        {
            var font = new PdfDictionary(document);
            font.Elements.SetName("/Type", "/Font");
            font.Elements.SetName("/Subtype", "/Type1");
            font.Elements.SetName("/BaseFont", "/Helvetica");
            font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
            document.Internals.AddObject(font);
            return new TextFont(font.Reference!, font);
        }

        /// <summary>The text as the font's codes (a Latin-1 string of bytes), or null when a character has none.</summary>
        public string? Encode(string text)
        {
            var codes = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value is '\t')
                    codes.Append(' ');
                else if (_codes.TryGetValue(rune.Value, out var code))
                    codes.Append((char)code);
                else if (rune.Value == 0xA0 && _codes.TryGetValue(' ', out var space))
                    codes.Append((char)space);
                else
                    return null;
            }
            return codes.ToString();
        }

        public double Width(string codes, double size) => codes.Sum(c => _widths[c]) * size / 1000;
    }

    /// <summary>The content of one appearance stream and its resources.</summary>
    private sealed class Drawing(PdfDocument document, Box box)
    {
        private readonly StringBuilder _content = new();
        private readonly PdfDictionary _fonts = new(document);
        private PdfDictionary? _states;

        private static string F(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

        private Drawing Op(string text)
        {
            _content.Append(text).Append('\n');
            return this;
        }

        public PdfDictionary Form()
        {
            var form = new PdfDictionary(document);
            form.Elements.SetName("/Type", "/XObject");
            form.Elements.SetName("/Subtype", "/Form");
            var bbox = new PdfArray(document);
            foreach (var value in new[] { box.X0, box.Y0, box.X1, box.Y1 })
                bbox.Elements.Add(new PdfReal(Math.Round(value, 3)));
            form.Elements["/BBox"] = bbox;
            var resources = new PdfDictionary(document);
            if (_fonts.Elements.Count > 0)
                resources.Elements["/Font"] = _fonts;
            if (_states is not null)
                resources.Elements["/ExtGState"] = _states;
            form.Elements["/Resources"] = resources;
            form.CreateStream(Encoding.Latin1.GetBytes(_content.ToString()));
            form.Stream.Zip();
            document.Internals.AddObject(form);
            return form;
        }

        /// <summary>A colour operator from a colour array (gray, RGB or CMYK); null for none or an empty array.</summary>
        private static string? Colour(PdfItem? item, bool stroke)
        {
            var c = PdfObjects.Numbers(item);
            var values = c is null ? "" : string.Join(' ', c.Select(v => F(Math.Clamp(v, 0, 1))));
            return c?.Length switch
            {
                1 => values + (stroke ? " G" : " g"),
                3 => values + (stroke ? " RG" : " rg"),
                4 => values + (stroke ? " K" : " k"),
                _ => null,
            };
        }

        /// <summary>A colour operator for the other kind of painting (fill ↔ stroke).</summary>
        private static string Swap(string colour, bool toStroke)
        {
            var at = colour.LastIndexOf(' ');
            var op = colour[(at + 1)..];
            return colour[..(at + 1)] + (toStroke ? op.ToUpperInvariant() : op.ToLowerInvariant());
        }

        /// <summary>The border width (BS W, else Border's third number, else 1) and the dash operator, if dashed.</summary>
        private static (double Width, string? Dash) Border(PdfDictionary annotation)
        {
            if (PdfObjects.Dictionary(annotation, "/BS") is { } style)
            {
                var width = PdfObjects.Number(style, "/W", 1);
                var dash = PdfObjects.Name(style, "/S") == "D"
                    ? "[" + string.Join(' ', (PdfObjects.Numbers(style, "/D") ?? [3]).Take(8).Select(F)) + "] 0 d"
                    : null;
                return (Math.Max(0, width), dash);
            }
            if (PdfObjects.Numbers(annotation, "/Border") is { Length: >= 3 } border)
                return (Math.Max(0, border[2]), border.Length > 3 && PdfObjects.Numbers(PdfObjects.Array(annotation, "/Border")!.Elements[3]) is { Length: > 0 } d
                    ? "[" + string.Join(' ', d.Take(8).Select(F)) + "] 0 d" : null);
            return (1, null);
        }

        private Drawing Rectangle(double x, double y, double w, double h) => Op($"{F(x)} {F(y)} {F(w)} {F(h)} re");

        private Drawing Ellipse(double x0, double y0, double x1, double y1)
        {
            var (cx, cy, rx, ry) = ((x0 + x1) / 2, (y0 + y1) / 2, (x1 - x0) / 2, (y1 - y0) / 2);
            var (kx, ky) = (rx * Kappa, ry * Kappa);
            Op($"{F(cx + rx)} {F(cy)} m");
            Op($"{F(cx + rx)} {F(cy + ky)} {F(cx + kx)} {F(cy + ry)} {F(cx)} {F(cy + ry)} c");
            Op($"{F(cx - kx)} {F(cy + ry)} {F(cx - rx)} {F(cy + ky)} {F(cx - rx)} {F(cy)} c");
            Op($"{F(cx - rx)} {F(cy - ky)} {F(cx - kx)} {F(cy - ry)} {F(cx)} {F(cy - ry)} c");
            return Op($"{F(cx + kx)} {F(cy - ry)} {F(cx + rx)} {F(cy - ky)} {F(cx + rx)} {F(cy)} c h");
        }

        public void Shape(PdfDictionary annotation, bool ellipse)
        {
            var stroke = Colour(annotation.Elements["/C"], stroke: true);
            var fill = Colour(annotation.Elements["/IC"], stroke: false);
            var (width, dash) = Border(annotation);
            if (stroke is null)
                width = 0;
            if (stroke is null && fill is null)
                return;
            Op("q");
            if (stroke is not null)
                Op(stroke).Op($"{F(width)} w");
            if (dash is not null)
                Op(dash);
            if (fill is not null)
                Op(fill);
            var inset = width / 2;
            var (x0, y0, x1, y1) = (box.X0 + inset, box.Y0 + inset, box.X1 - inset, box.Y1 - inset);
            if (ellipse)
                Ellipse(x0, y0, x1, y1);
            else
                Rectangle(x0, y0, x1 - x0, y1 - y0);
            Op(fill is not null && width > 0 ? "B" : fill is not null ? "f" : "S").Op("Q");
        }

        public void Lines(PdfDictionary annotation, List<double[]> paths, bool close)
        {
            var stroke = Colour(annotation.Elements["/C"], stroke: true) ?? "0 G";
            var fill = close ? Colour(annotation.Elements["/IC"], stroke: false) : null;
            var (width, dash) = Border(annotation);
            if (width <= 0 && fill is null)
                return;
            Op("q").Op(stroke).Op($"{F(width)} w 1 J 1 j");
            if (dash is not null)
                Op(dash);
            if (fill is not null)
                Op(fill);
            var points = 0;
            foreach (var path in paths)
            {
                for (var i = 0; i + 1 < path.Length && points < MaxPoints; i += 2, points++)
                    Op($"{F(path[i])} {F(path[i + 1])} {(i == 0 ? "m" : "l")}");
                if (close)
                    Op("h");
            }
            Op(fill is not null ? (width > 0 ? "B" : "f") : "S").Op("Q");
        }

        public void Markup(PdfDictionary annotation, string subtype)
        {
            var colour = PdfObjects.Numbers(annotation, "/C") is { Length: > 0 } ? annotation.Elements["/C"] : null;
            var quads = PdfObjects.Numbers(annotation, "/QuadPoints") ?? [box.X0, box.Y1, box.X1, box.Y1, box.X0, box.Y0, box.X1, box.Y0];
            Op("q");
            if (subtype == "/Highlight")
            {
                _states = new PdfDictionary(document);
                var multiply = new PdfDictionary(document);
                multiply.Elements.SetName("/Type", "/ExtGState");
                multiply.Elements.SetName("/BM", "/Multiply");
                _states.Elements["/G0"] = multiply;
                Op("/G0 gs").Op(colour is null ? "1 1 0 rg" : Colour(colour, stroke: false) ?? "1 1 0 rg");
            }
            else
            {
                Op(colour is null ? "0 G" : Colour(colour, stroke: true) ?? "0 G");
            }
            for (var i = 0; i + 7 < quads.Length && i < MaxPoints; i += 8)
            {
                // Top left, top right, bottom left, bottom right, as viewers write them.
                var (x1, y1, x2, y2, x3, y3, x4, y4) = (quads[i], quads[i + 1], quads[i + 2], quads[i + 3], quads[i + 4], quads[i + 5], quads[i + 6], quads[i + 7]);
                var height = Math.Abs(y1 - y3);
                switch (subtype)
                {
                    case "/Highlight":
                        Op($"{F(x1)} {F(y1)} m {F(x2)} {F(y2)} l {F(x4)} {F(y4)} l {F(x3)} {F(y3)} l h f");
                        break;
                    case "/Underline":
                        Op($"{F(Math.Max(0.5, height / 14))} w {F(x3)} {F(y3 + height / 14)} m {F(x4)} {F(y4 + height / 14)} l S");
                        break;
                    case "/StrikeOut":
                        Op($"{F(Math.Max(0.5, height / 14))} w {F((x1 + x3) / 2)} {F((y1 + y3) / 2)} m {F((x2 + x4) / 2)} {F((y2 + y4) / 2)} l S");
                        break;
                    default:
                        {
                            var step = Math.Max(1, height / 6);
                            Op($"{F(Math.Max(0.5, height / 18))} w {F(x3)} {F(y3)} m");
                            var up = true;
                            for (var x = x3 + step; x < x4 && x - x3 < 10_000; x += step, up = !up)
                                Op($"{F(x)} {F(y3 + (up ? step : 0))} l");
                            Op("S");
                            break;
                        }
                }
            }
            Op("Q");
        }

        public void Caret(PdfDictionary annotation)
        {
            var fill = Colour(annotation.Elements["/C"], stroke: false) ?? "0 0 1 rg";
            var (x0, y0, w, h) = (box.X0, box.Y0, box.Width, box.Height);
            Op("q").Op(fill).Op($"{F(x0)} {F(y0)} m {F(x0 + w / 2)} {F(y0 + h)} l {F(x0 + w)} {F(y0)} l {F(x0 + w * 0.75)} {F(y0)} l " +
                $"{F(x0 + w / 2)} {F(y0 + h * 0.55)} l {F(x0 + w * 0.25)} {F(y0)} l h f").Op("Q");
        }

        public void Note(PdfDictionary annotation)
        {
            var fill = Colour(annotation.Elements["/C"], stroke: false) ?? "1 1 0 rg";
            var size = Math.Min(20, Math.Min(box.Width, box.Height));
            if (size <= 1)
                return;
            var (x0, y0) = (box.X0, box.Y1 - size);
            Op("q").Op(fill).Op("0 G 0.6 w");
            Rectangle(x0 + 0.5, y0 + 0.5, size - 1, size - 1).Op("B");
            for (var line = 1; line <= 3; line++)
            {
                var y = y0 + size - size * line / 4.5;
                Op($"{F(x0 + size * 0.2)} {F(y)} m {F(x0 + size * 0.8)} {F(y)} l S");
            }
            Op("Q");
        }

        public bool Stamp(PdfDictionary annotation)
        {
            var stroke = Colour(annotation.Elements["/C"], stroke: true) ?? "0.8 0 0 RG";
            var name = Name(annotation, "/Name") is { Length: > 1 } n ? n[1..] : "Draft";
            var text = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToUpperInvariant();
            var font = TextFont.Helvetica(document);
            if (font.Encode(text) is not { } codes)
                return false;
            var width = Math.Max(1, Math.Min(box.Width, box.Height) / 15);
            Op("q").Op(stroke).Op($"{F(width)} w");
            Rectangle(box.X0 + width, box.Y0 + width, box.Width - 2 * width, box.Height - 2 * width).Op("S");
            var size = Math.Max(1, Math.Min(box.Height * 0.5, (box.Width - 6 * width) * 1000 / Math.Max(1, font.Width(codes, 1000))));
            var fill = Swap(stroke, toStroke: false);
            var x = box.X0 + (box.Width - font.Width(codes, size)) / 2;
            var y = box.Y0 + (box.Height - size * 0.7) / 2;
            _fonts.Elements["/F0"] = font.Reference;
            Op($"BT /F0 {F(size)} Tf {fill} {F(x)} {F(y)} Td {Literal(codes)} Tj ET").Op("Q");
            return true;
        }

        public bool LinkBorder(PdfDictionary annotation)
        {
            var (width, dash) = PdfObjects.Dictionary(annotation, "/BS") is not null || PdfObjects.Numbers(annotation, "/Border") is not null
                ? Border(annotation) : (1, null);
            if (width <= 0 || Colour(annotation.Elements["/C"], stroke: true) is not { } stroke)
                return false;
            Op("q").Op(stroke).Op($"{F(width)} w");
            if (dash is not null)
                Op(dash);
            if (PdfObjects.Name(PdfObjects.Dictionary(annotation, "/BS"), "/S") == "U")
                Op($"{F(box.X0)} {F(box.Y0 + width / 2)} m {F(box.X1)} {F(box.Y0 + width / 2)} l S");
            else
                Rectangle(box.X0 + width / 2, box.Y0 + width / 2, box.Width - width, box.Height - width).Op("S");
            Op("Q");
            return true;
        }

        /// <summary>A widget's background and border (MK BG and BC), as a circle for radio buttons.</summary>
        public void Frame(PdfDictionary? mk, PdfDictionary widget, bool round)
        {
            var fill = mk is null ? null : Colour(mk.Elements["/BG"], stroke: false);
            var stroke = mk is null ? null : Colour(mk.Elements["/BC"], stroke: true);
            var (width, dash) = Border(widget);
            if (stroke is null)
                width = 0;
            if (fill is null && stroke is null)
                return;
            Op("q");
            if (fill is not null)
                Op(fill);
            if (stroke is not null)
                Op(stroke).Op($"{F(width)} w");
            if (dash is not null)
                Op(dash);
            var inset = width / 2;
            if (round)
            {
                var size = Math.Min(box.Width, box.Height) - width;
                var (cx, cy) = ((box.X0 + box.X1) / 2, (box.Y0 + box.Y1) / 2);
                Ellipse(cx - size / 2, cy - size / 2, cx + size / 2, cy + size / 2);
            }
            else
            {
                Rectangle(box.X0 + inset, box.Y0 + inset, box.Width - width, box.Height - width);
            }
            Op(fill is not null && width > 0 ? "B" : fill is not null ? "f" : "S").Op("Q");
        }

        /// <summary>A check box's or radio button's mark, by the ZapfDingbats character its caption names.</summary>
        public void Mark(PdfDictionary widget, PdfDictionary? form, char caption)
        {
            var (_, _, colour) = DefaultAppearance(widget, form);
            var size = Math.Min(box.Width, box.Height);
            var (cx, cy) = ((box.X0 + box.X1) / 2, (box.Y0 + box.Y1) / 2);
            var half = size * 0.3;
            Op("q").Op(colour).Op(Swap(colour, toStroke: true));
            switch (caption)
            {
                case 'l':
                    Ellipse(cx - half * 0.8, cy - half * 0.8, cx + half * 0.8, cy + half * 0.8).Op("f");
                    break;
                case '8':
                    Op($"{F(size * 0.1)} w 1 J {F(cx - half)} {F(cy - half)} m {F(cx + half)} {F(cy + half)} l {F(cx - half)} {F(cy + half)} m {F(cx + half)} {F(cy - half)} l S");
                    break;
                case 'n':
                    Rectangle(cx - half, cy - half, 2 * half, 2 * half).Op("f");
                    break;
                case 'u' or 'H':
                    Op($"{F(cx)} {F(cy + half)} m {F(cx + half)} {F(cy)} l {F(cx)} {F(cy - half)} l {F(cx - half)} {F(cy)} l h f");
                    break;
                default:
                    Op($"{F(size * 0.1)} w 1 J 1 j {F(cx - half)} {F(cy)} m {F(cx - half * 0.3)} {F(cy - half * 0.8)} l {F(cx + half)} {F(cy + half)} l S");
                    break;
            }
            Op("Q");
        }

        /// <summary>The font, size and fill colour a field's (or free text's) DA asks for.</summary>
        private static (string? Font, double Size, string Colour) DefaultAppearance(PdfDictionary annotation, PdfDictionary? form)
        {
            var da = (Inherited(annotation, "/DA") as PdfString)?.Value ?? form?.Elements.GetString("/DA") ?? "";
            string? font = null;
            double size = 0;
            var colour = "0 g";
            var operands = new List<object?>();
            try
            {
                var lexer = new PdfContentLexer(Encoding.Latin1.GetBytes(da));
                while (lexer.Next() is { } token)
                {
                    if (token is not PdfContentLexer.Operator op)
                    {
                        operands.Add(token);
                        continue;
                    }
                    var numbers = operands.OfType<double>().ToList();
                    switch (op.Name)
                    {
                        case "Tf" when operands.Count >= 2 && operands[^2] is PdfContentLexer.Name name && operands[^1] is double s:
                            (font, size) = ("/" + name.Value, s);
                            break;
                        case "g" when numbers.Count >= 1:
                            colour = $"{F(numbers[^1])} g";
                            break;
                        case "rg" when numbers.Count >= 3:
                            colour = $"{F(numbers[^3])} {F(numbers[^2])} {F(numbers[^1])} rg";
                            break;
                        case "k" when numbers.Count >= 4:
                            colour = $"{F(numbers[^4])} {F(numbers[^3])} {F(numbers[^2])} {F(numbers[^1])} k";
                            break;
                    }
                    operands.Clear();
                }
            }
            catch (InvalidDataException)
            {
                // A damaged DA: the defaults.
            }
            return (font, Math.Clamp(size, 0, 1000), colour);
        }

        /// <summary>The DA font from the form's default resources when it can draw <paramref name="text"/>, else Helvetica.</summary>
        private (TextFont Font, string Codes)? FontFor(string? name, PdfDictionary? form, string text)
        {
            var listed = name is null ? null : PdfObjects.Dictionary(PdfObjects.Dictionary(form, "/DR"), "/Font")?.Elements[name];
            if (TextFont.Of(listed) is { } own && own.Encode(text) is { } codes)
                return (own, codes);
            var helvetica = TextFont.Helvetica(document);
            return helvetica.Encode(text) is { } fallback ? (helvetica, fallback) : null;
        }

        /// <summary>Lines of text in the DA font, wrapped to the box, aligned by Q; false when the font can't draw them.</summary>
        private bool Text(PdfDictionary annotation, PdfDictionary? form, string text, bool wrap, int quadding, bool centredVertically, double padding)
        {
            if (text.Length > MaxText)
                text = text[..MaxText];
            var (fontName, size, colour) = DefaultAppearance(annotation, form);
            if (FontFor(fontName, form, text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal)) is not var (font, _))
                return false;
            var inner = box.Width - 2 * padding;
            if (size <= 0)
            {
                // Auto size: the box's height, at most 12 points, then narrower until one line fits.
                size = Math.Clamp((box.Height - 2 * padding) / 1.2, 4, 12);
                if (!wrap && font.Encode(text) is { } single)
                    size = Math.Max(4, Math.Min(size, inner * 1000 / Math.Max(1, font.Width(single, 1000))));
            }
            var lines = new List<string>();
            foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\r', '\n'))
            {
                if (font.Encode(paragraph) is not { } codes)
                    return false;
                if (!wrap)
                {
                    lines.Add(codes);
                    continue;
                }
                var line = "";
                foreach (var word in codes.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && font.Width(candidate, size) > inner)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else
                    {
                        line = candidate;
                    }
                }
                lines.Add(line);
            }
            _fonts.Elements["/F0"] = font.Reference;
            var leading = size * 1.15;
            var y = centredVertically && lines.Count == 1 ? box.Y0 + (box.Height - size * 0.7) / 2 : box.Y1 - padding - size;
            Op("/Tx BMC q").Op($"{F(box.X0 + 1)} {F(box.Y0 + 1)} {F(Math.Max(0, box.Width - 2))} {F(Math.Max(0, box.Height - 2))} re W n");
            Op($"BT /F0 {F(size)} Tf {colour}");
            foreach (var line in lines)
            {
                if (y < box.Y0 - size)
                    break;
                var width = font.Width(line, size);
                var x = quadding switch
                {
                    1 => box.X0 + (box.Width - width) / 2,
                    2 => box.X1 - padding - width,
                    _ => box.X0 + padding,
                };
                Op($"1 0 0 1 {F(x)} {F(y)} Tm {Literal(line)} Tj");
                y -= leading;
            }
            Op("ET Q EMC");
            return true;
        }

        public bool FreeText(PdfDictionary annotation, PdfDictionary? form)
        {
            var fill = Colour(annotation.Elements["/C"], stroke: false);
            var (width, dash) = Border(annotation);
            if (fill is not null || width > 0)
            {
                Op("q");
                if (fill is not null)
                    Op(fill);
                Op($"0 G {F(width)} w");
                if (dash is not null)
                    Op(dash);
                Rectangle(box.X0 + width / 2, box.Y0 + width / 2, box.Width - width, box.Height - width);
                Op(fill is not null ? (width > 0 ? "B" : "f") : "S").Op("Q");
            }
            var text = annotation.Elements.GetString("/Contents");
            var quadding = (int)PdfObjects.Number(annotation, "/Q", 0);
            return text.Length == 0 || Text(annotation, form, text, wrap: true, quadding, centredVertically: false, padding: width + 2);
        }

        /// <summary>A text field's or choice field's value.</summary>
        public bool FieldText(PdfDictionary widget, PdfDictionary? form, string type, int flags)
        {
            var quadding = Inherited(widget, "/Q") is PdfInteger q ? q.Value : (int)PdfObjects.Number(form, "/Q", 0);
            var value = Inherited(widget, "/V");
            var (border, _) = Border(widget);
            if (type == "/Tx")
            {
                if ((flags & (1 << 13)) != 0 || value is not PdfString { Value.Length: > 0 } text)
                    return true; // a password, or nothing typed
                var multiline = (flags & (1 << 12)) != 0;
                return Text(widget, form, text.Value, wrap: multiline, quadding, centredVertically: !multiline, padding: border + 2);
            }
            var chosen = value switch
            {
                PdfString one => [one.Value],
                PdfArray many => many.Elements.Select(Resolve).OfType<PdfString>().Select(s => s.Value).ToList(),
                _ => new List<string>(),
            };
            if ((flags & (1 << 17)) != 0)
                return chosen.Count == 0 || Text(widget, form, chosen[0], wrap: false, quadding, centredVertically: true, padding: border + 2);
            // A list box: its options, the chosen ones on a highlight.
            var options = (Inherited(widget, "/Opt") as PdfArray)?.Elements.Select(Resolve).Select(o => o switch
            {
                PdfString s => s.Value,
                PdfArray pair when pair.Elements.Count >= 2 && Resolve(pair.Elements[1]) is PdfString shown => shown.Value,
                _ => "",
            }).Take(1000).ToList() ?? [];
            if (options.Count == 0)
                return true;
            var (_, daSize, _) = DefaultAppearance(widget, form);
            var size = daSize > 0 ? daSize : 12;
            var top = box.Y1 - border - 1;
            foreach (var (option, index) in options.Select((o, i) => (o, i)))
            {
                if (!chosen.Contains(option))
                    continue;
                Op("q 0.6 0.75 0.85 rg");
                Rectangle(box.X0 + border, top - size * 1.15 * (index + 1), box.Width - 2 * border, size * 1.15).Op("f Q");
            }
            return Text(widget, form, string.Join('\n', options), wrap: false, quadding, centredVertically: false, padding: border + 2);
        }

        /// <summary>A push button's caption, centred.</summary>
        public bool Caption(PdfDictionary widget, PdfDictionary? form, string caption)
        {
            var (border, _) = Border(widget);
            return Text(widget, form, caption, wrap: false, quadding: 1, centredVertically: true, padding: border + 2);
        }

        /// <summary>A PDF literal string of bytes (a Latin-1 string), with the characters that need it escaped.</summary>
        private static string Literal(string codes)
        {
            var text = new StringBuilder(codes.Length + 2).Append('(');
            foreach (var c in codes)
            {
                switch (c)
                {
                    case '(' or ')' or '\\':
                        text.Append('\\').Append(c);
                        break;
                    case '\r':
                        text.Append("\\r");
                        break;
                    case '\n':
                        text.Append("\\n");
                        break;
                    default:
                        text.Append(c);
                        break;
                }
            }
            return text.Append(')').ToString();
        }
    }
}
