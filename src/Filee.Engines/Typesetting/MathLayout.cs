// Equations for the layout engine (GitHub Project 4 item "Layout engine: equations"): the equation tree (MathNode,
// read from 한글's equation script by Hwp/Hwpx/EquationParser or from Word math by Docx/OmmlReader) laid out into a
// box with its width, height above the baseline and depth below it, drawn with the document's fonts.
//
// Text: variables (Latin letters) italic, numbers, function names, Greek letters, symbols and rm text upright, bold
// where asked; in 한글's equation fonts when installed (HancomEQN, HYhwpEQ), else Times New Roman or another serif,
// with the font fallback for characters a font lacks. Relations (=, ≤, ∈, →, …) get 0.13 of the size on each side,
// binary operators (+, −, ×, ∪, …) a tenth, unless they start the row (a sign); - is drawn as the minus sign.
//
// Structure (sizes as fractions of the equation's size): scripts at 0.7 of their base, raised to 0.42 or lowered by
// 0.18 (further for taller bases); fractions on the math axis (0.27 above the baseline) with a rule of 0.05; roots with
// a drawn radical sign over their body; brackets of LEFT … RIGHT drawn to the height of their content; sums, products
// and unions at 1.4 with their limits above and below, integrals at 1.6 with them to the right; matrices, cases and
// piles centred on the axis; accents drawn over (or under) their content.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>
/// Laid out math: <see cref="Paint"/> draws it with its origin (left edge, baseline) at (x, y), scaled across by the
/// last argument (to fit a box 한글 laid the equation out narrower).
/// </summary>
internal sealed class EquationBox(float width, float ascent, float descent, Action<PageContent, float, float, float> paint)
{
    public static readonly EquationBox Empty = new(0, 0, 0, (_, _, _, _) => { });

    public float Width { get; } = width;
    public float Ascent { get; } = ascent;
    public float Descent { get; } = descent;
    public float Height => Ascent + Descent;
    public Action<PageContent, float, float, float> Paint { get; } = paint;
}

internal sealed class MathLayout(FontResolver fonts, Shaper shaper)
{
    private const float ScriptScale = 0.7f;
    private const float Axis = 0.27f;
    private const float Rule = 0.05f;
    private const float Gap = 0.12f;

    /// <summary>한글's equation fonts first, then common serif fonts.</summary>
    private static readonly string[] Families = ["HancomEQN", "HYhwpEQ", "Times New Roman", "Liberation Serif", "Tinos", "Noto Serif", "DejaVu Serif"];

    private static readonly HashSet<string> Relations =
    [
        "=", "<", ">", "≤", "≥", "≠", "≈", "≡", "≅", "∼", "≃", "∝", "≪", "≫", "∈", "∉", "∋", "⊂", "⊃", "⊆", "⊇", "⊄", "⊅",
        "→", "←", "↔", "⇒", "⇐", "⇔", "↦", "⊥", "∥", "≍", "≐", "≺", "≻", ":",
    ];

    private static readonly HashSet<string> Binary = ["+", "-", "−", "±", "∓", "×", "÷", "·", "∘", "∪", "∩", "∧", "∨", "⊕", "⊖", "⊗", "⊙", "∗", "⋆", "⊔", "⊓", "\\", "/"];

    private readonly Dictionary<FontStyle, ResolvedFont?> _primary = [];

    /// <summary>The style text takes: italic Latin letters unless upright (rm), bold when asked.</summary>
    private readonly record struct Style(float Size, SKColor Color, bool Upright, bool Bold)
    {
        public Style Smaller(float scale) => this with { Size = Size * scale };
    }

    /// <summary>Lays out <paramref name="node"/> at <paramref name="size"/> points in <paramref name="color"/>.</summary>
    public EquationBox Layout(MathNode node, float size, SKColor color) => Lay(node, new Style(size, color, false, false));

    private EquationBox Lay(MathNode node, Style style) => node switch
    {
        MathRow row => Row(row.Items, style),
        MathText text => Text(text.Text, style, italic: !style.Upright && text.Text.All(char.IsAsciiLetter)),
        MathNumber number => Text(number.Digits, style, italic: false),
        MathSymbol symbol => Text(symbol.Text == "-" ? "−" : symbol.Text, style, italic: false),
        MathCommand command => Text(command.Glyph, style, italic: false),
        MathQuoted quoted => Text(quoted.Text, style, italic: false),
        MathFunction function => Text(function.Name, style, italic: false),
        MathSpace space => new EquationBox((float)space.Width * 0.5f * style.Size, 0, 0, (_, _, _, _) => { }),
        MathFraction fraction => Fraction(fraction, style),
        MathRoot root => Root(root, style),
        MathScripts scripts => Scripts(Lay(scripts.Base, style), scripts.Sub, scripts.Sup, style, italicBase: scripts.Base is MathText),
        MathBigOperator big => BigOperator(big, style),
        MathLimit limit => Limit(limit, style),
        MathMatrix matrix => Matrix(matrix, style),
        MathCases cases => Cases(cases, style),
        MathPile pile => Pile(pile.Rows, pile.Align, style),
        MathBrackets brackets => Brackets(brackets.Left, Lay(brackets.Body, style), brackets.Right, style),
        MathAccent accent => Accent(accent.Command, Lay(accent.Body, style), style),
        MathStyled styled => Lay(styled.Body, styled.Command switch
        {
            "rm" => style with { Upright = true },
            "it" => style with { Upright = false },
            "bold" => style with { Bold = true },
            _ => style,
        }),
        _ => EquationBox.Empty,
    };

    // ───────────────────────── Text ─────────────────────────

    private ResolvedFont? Primary(FontStyle style)
    {
        if (_primary.TryGetValue(style, out var font))
            return font;
        font = Families.Select(f => fonts.Find(f, style)).FirstOrDefault(f => f is not null) ?? fonts.Resolve("serif", style);
        return _primary[style] = font;
    }

    /// <summary>A run of text in the equation's fonts (with fallback for characters they lack).</summary>
    private EquationBox Text(string text, Style style, bool italic, float scale = 1)
    {
        if (text.Length == 0)
            return EquationBox.Empty;
        var fontStyle = new FontStyle(style.Bold ? 700 : 400, italic);
        if (Primary(fontStyle) is not { } primary)
            return EquationBox.Empty;
        var size = style.Size * scale;
        var runs = new List<PlacedRun>();
        float x = 0;
        foreach (var run in fonts.Fallback(text, 0, text.Length, primary, fontStyle))
        {
            var shaped = shaper.Shape(run.Font.Font, text, run.Start, run.Length);
            var unit = size / shaped.UnitsPerEm;
            var positions = new SKPoint[shaped.Glyphs.Length];
            for (var g = 0; g < shaped.Glyphs.Length; g++)
            {
                positions[g] = new SKPoint(x + shaped.XOffsets[g] * unit, -shaped.YOffsets[g] * unit);
                x += shaped.XAdvances[g] * unit;
            }
            runs.Add(new PlacedRun(run.Font.Font, size, shaped.Glyphs, positions, text.Substring(run.Start, run.Length), shaped.Clusters)
            {
                Color = style.Color,
                Embolden = run.Font.SyntheticBold,
                Slant = run.Font.SyntheticItalic,
            });
        }
        // Italic letters lean past their advance: a little room after them.
        var width = x + (italic ? size * 0.02f : 0);
        return new EquationBox(width, size * 0.75f, size * 0.25f, (page, left, baseline, scale) =>
        {
            foreach (var run in runs)
            {
                page.DrawGlyphs(new GlyphRun(run.Font, run.Size, run.Glyphs, [.. run.Positions.Select(p => new SKPoint(left + p.X * scale, baseline + p.Y))], run.Text, run.Clusters)
                {
                    Color = run.Color,
                    ScaleX = scale,
                    Embolden = run.Embolden,
                    SkewX = run.Slant ? GlyphRun.SyntheticSlant : 0,
                });
            }
        });
    }

    // ───────────────────────── Rows ─────────────────────────

    private EquationBox Row(IReadOnlyList<MathNode> items, Style style)
    {
        var parts = new List<(float X, EquationBox Box)>();
        float x = 0;
        MathNode? previous = null;
        foreach (var item in items)
        {
            var box = Lay(item, style);
            var (before, after) = Spacing(item, previous, style.Size);
            x += before;
            parts.Add((x, box));
            x += box.Width + after;
            previous = item;
        }
        return Combine(parts, x);
    }

    /// <summary>Space around an operator: relations more than binary operators, none for a sign.</summary>
    private static (float Before, float After) Spacing(MathNode item, MathNode? previous, float size)
    {
        var text = item switch
        {
            MathSymbol symbol => symbol.Text,
            MathCommand command => command.Glyph,
            _ => null,
        };
        if (text is null)
            return item is MathFunction ? (0, size * 0.08f) : (0, 0);
        if (Relations.Contains(text))
            return (size * 0.13f, size * 0.13f);
        var sign = previous is null || previous is MathSymbol { Text: "(" or "[" or "{" or "," } || previous is MathSymbol or MathCommand && IsOperator(previous);
        if (Binary.Contains(text) && !sign)
            return (size * 0.1f, size * 0.1f);
        return text == "," ? (0, size * 0.1f) : (0, 0);
    }

    private static bool IsOperator(MathNode node) => node switch
    {
        MathSymbol symbol => Relations.Contains(symbol.Text) || Binary.Contains(symbol.Text),
        MathCommand command => Relations.Contains(command.Glyph) || Binary.Contains(command.Glyph),
        _ => false,
    };

    /// <summary>Boxes side by side on one baseline.</summary>
    private static EquationBox Combine(List<(float X, EquationBox Box)> parts, float width) =>
        parts.Count == 0
            ? EquationBox.Empty
            : new EquationBox(width, parts.Max(p => p.Box.Ascent), parts.Max(p => p.Box.Descent), (page, x, y, scale) =>
            {
                foreach (var (dx, box) in parts)
                    box.Paint(page, x + dx * scale, y, scale);
            });

    /// <summary>Boxes placed at offsets from the origin (y down from the baseline).</summary>
    private static EquationBox Place(float width, float ascent, float descent, params (float X, float Y, EquationBox Box)[] parts) =>
        new(width, ascent, descent, (page, x, y, scale) =>
        {
            foreach (var (dx, dy, box) in parts)
                box.Paint(page, x + dx * scale, y + dy, scale);
        });

    /// <summary>A drawing with its origin at x = 0 and the baseline at y, scaled across with the box.</summary>
    private static Action<PageContent, float, float, float> Drawn(Action<SKCanvas, float, float> draw) =>
        (page, x, y, scale) => page.Draw(canvas =>
        {
            canvas.Translate(x, 0);
            canvas.Scale(scale, 1);
            draw(canvas, 0, y);
        });

    private static SKPaint Pen(SKColor color, float width) =>
        new() { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    // ───────────────────────── Fractions and roots ─────────────────────────

    private EquationBox Fraction(MathFraction fraction, Style style)
    {
        var (top, bottom) = (Lay(fraction.Numerator, style), Lay(fraction.Denominator, style));
        var size = style.Size;
        var (axis, rule, gap) = (size * Axis, fraction.Bar ? Math.Max(0.4f, size * Rule) : 0, size * Gap);
        var width = Math.Max(top.Width, bottom.Width) + size * 0.12f;
        var topY = -(axis + rule / 2 + gap + top.Descent);
        var bottomY = -axis + rule / 2 + gap + bottom.Ascent;
        var parts = new List<(float, float, EquationBox)>
        {
            ((width - top.Width) / 2, topY, top),
            ((width - bottom.Width) / 2, bottomY, bottom),
        };
        if (fraction.Bar)
        {
            parts.Add((0, 0, new EquationBox(width, 0, 0, Drawn((canvas, x, y) =>
            {
                using var pen = Pen(style.Color, rule);
                pen.StrokeCap = SKStrokeCap.Butt;
                canvas.DrawLine(x + size * 0.05f, y - axis, x + width - size * 0.05f, y - axis, pen);
            }))));
        }
        return Place(width, -topY + top.Ascent, bottomY + bottom.Descent, [.. parts]);
    }

    private EquationBox Root(MathRoot root, Style style)
    {
        var body = Lay(root.Body, style);
        var size = style.Size;
        var rule = Math.Max(0.4f, size * Rule);
        var gap = size * 0.1f;
        var ascent = Math.Max(body.Ascent, size * 0.7f) + gap + rule;
        var descent = Math.Max(body.Descent, size * 0.2f);
        var sign = size * 0.55f;
        var index = root.Index is null ? null : Lay(root.Index, style.Smaller(0.6f));
        var shift = index is null ? 0 : Math.Max(0, index.Width - sign * 0.45f);
        var width = shift + sign + body.Width + size * 0.1f;
        var radical = new EquationBox(width, ascent, descent, Drawn((canvas, x, y) =>
        {
            using var pen = Pen(style.Color, rule);
            using var path = new SKPath();
            var (left, top, bottom) = (x + shift, y - ascent + rule / 2, y + descent);
            path.MoveTo(left, y - (ascent - descent) * 0.35f);
            path.LineTo(left + sign * 0.2f, y - (ascent - descent) * 0.45f);
            path.LineTo(left + sign * 0.5f, bottom);
            path.LineTo(left + sign * 0.95f, top);
            path.LineTo(x + width, top);
            canvas.DrawPath(path, pen);
        }));
        var parts = new List<(float, float, EquationBox)> { (0, 0, radical), (shift + sign, 0, body) };
        if (index is not null)
            parts.Add((0, -(ascent - descent) * 0.45f - index.Descent - size * 0.05f, index));
        var height = index is null ? ascent : Math.Max(ascent, (ascent - descent) * 0.45f + size * 0.05f + index.Height);
        return Place(width, height, descent, [.. parts]);
    }

    // ───────────────────────── Scripts and limits ─────────────────────────

    private EquationBox Scripts(EquationBox body, MathNode? subNode, MathNode? supNode, Style style, bool italicBase)
    {
        var size = style.Size;
        var small = style.Smaller(ScriptScale);
        var sup = supNode is null ? null : Lay(supNode, small);
        var sub = subNode is null ? null : Lay(subNode, small);
        var parts = new List<(float, float, EquationBox)> { (0, 0, body) };
        var supShift = sup is null ? 0 : Math.Max(size * 0.42f, body.Ascent - sup.Ascent * 0.6f);
        var subShift = sub is null ? 0 : Math.Max(size * 0.18f, body.Descent - sub.Descent * 0.2f);
        if (sup is not null && sub is not null && supShift - sup.Descent - (sub.Ascent - subShift) < size * 0.15f)
            subShift += size * 0.15f - (supShift - sup.Descent - (sub.Ascent - subShift));
        var width = body.Width;
        if (sup is not null)
        {
            parts.Add((body.Width + (italicBase ? size * 0.03f : 0), -supShift, sup));
            width = Math.Max(width, body.Width + sup.Width);
        }
        if (sub is not null)
        {
            parts.Add((body.Width - (italicBase ? size * 0.03f : 0), subShift, sub));
            width = Math.Max(width, body.Width + sub.Width);
        }
        width += size * 0.02f;
        var ascent = Math.Max(body.Ascent, sup is null ? 0 : supShift + sup.Ascent);
        var descent = Math.Max(body.Descent, sub is null ? 0 : subShift + sub.Descent);
        return Place(width, ascent, descent, [.. parts]);
    }

    /// <summary><paramref name="body"/> with <paramref name="upper"/> centred above and <paramref name="lower"/> below.</summary>
    private static EquationBox Limits(EquationBox body, EquationBox? upper, EquationBox? lower, float gap)
    {
        var width = Math.Max(body.Width, Math.Max(upper?.Width ?? 0, lower?.Width ?? 0));
        var parts = new List<(float, float, EquationBox)> { ((width - body.Width) / 2, 0, body) };
        var (ascent, descent) = (body.Ascent, body.Descent);
        if (upper is not null)
        {
            var y = -(body.Ascent + gap + upper.Descent);
            parts.Add(((width - upper.Width) / 2, y, upper));
            ascent = -y + upper.Ascent;
        }
        if (lower is not null)
        {
            var y = body.Descent + gap + lower.Ascent;
            parts.Add(((width - lower.Width) / 2, y, lower));
            descent = y + lower.Descent;
        }
        return Place(width, ascent, descent, [.. parts]);
    }

    private EquationBox BigOperator(MathBigOperator big, Style style)
    {
        var size = style.Size;
        var integral = big.Glyph is "∫" or "∬" or "∭" or "∮" or "∯" or "∰";
        var glyph = Text(big.Glyph, style, italic: false, integral ? 1.6f : 1.4f);
        // Centred on the math axis.
        var shift = (glyph.Ascent - glyph.Descent) / 2 - size * Axis;
        var symbol = Place(glyph.Width, glyph.Ascent - shift, glyph.Descent + shift, (0, shift, glyph));
        var small = style.Smaller(ScriptScale);
        var lower = big.Lower is null ? null : Lay(big.Lower, small);
        var upper = big.Upper is null ? null : Lay(big.Upper, small);
        var box = integral ? Scripts(symbol, big.Lower, big.Upper, style, italicBase: true) : Limits(symbol, upper, lower, size * 0.08f);
        return Place(box.Width + size * 0.05f, box.Ascent, box.Descent, (0, 0, box));
    }

    private EquationBox Limit(MathLimit limit, Style style)
    {
        var name = Text("lim", style, italic: false);
        var lower = limit.Lower is null ? null : Lay(limit.Lower, style.Smaller(ScriptScale));
        var box = Limits(name, null, lower, style.Size * 0.05f);
        return Place(box.Width + style.Size * 0.12f, box.Ascent, box.Descent, (0, 0, box));
    }

    // ───────────────────────── Grids ─────────────────────────

    /// <summary>Rows of cells in columns: centred (or to one side) and the whole centred on the math axis.</summary>
    private EquationBox Grid(IReadOnlyList<IReadOnlyList<MathNode>> rows, Style style, string align, float columnGap)
    {
        var size = style.Size;
        var cells = rows.Select(r => r.Select(c => Lay(c, style)).ToList()).ToList();
        var columns = cells.Count == 0 ? 0 : cells.Max(r => r.Count);
        var widths = new float[columns];
        foreach (var row in cells)
            for (var c = 0; c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c].Width);
        var rowGap = size * 0.25f;
        var ascents = cells.Select(r => Math.Max(size * 0.75f, r.Count == 0 ? 0 : r.Max(b => b.Ascent))).ToList();
        var descents = cells.Select(r => Math.Max(size * 0.25f, r.Count == 0 ? 0 : r.Max(b => b.Descent))).ToList();
        var height = ascents.Sum() + descents.Sum() + rowGap * Math.Max(0, cells.Count - 1);
        var top = -(height / 2 + size * Axis);
        var parts = new List<(float, float, EquationBox)>();
        var y = top;
        for (var r = 0; r < cells.Count; r++)
        {
            y += ascents[r];
            float x = 0;
            for (var c = 0; c < cells[r].Count; c++)
            {
                var box = cells[r][c];
                var offset = align switch
                {
                    "lpile" => 0,
                    "rpile" => widths[c] - box.Width,
                    _ => (widths[c] - box.Width) / 2,
                };
                parts.Add((x + offset, y, box));
                x += widths[c] + columnGap;
            }
            y += descents[r] + rowGap;
        }
        var width = widths.Sum() + columnGap * Math.Max(0, columns - 1);
        return Place(width, -top, height + top, [.. parts]);
    }

    private EquationBox Matrix(MathMatrix matrix, Style style)
    {
        var grid = Grid(matrix.Rows, style, "pile", style.Size * 0.8f);
        return matrix.Command switch
        {
            "pmatrix" => Brackets("(", grid, ")", style),
            "bmatrix" => Brackets("[", grid, "]", style),
            "dmatrix" or "vmatrix" => Brackets("|", grid, "|", style),
            _ => grid,
        };
    }

    private EquationBox Cases(MathCases cases, Style style) => Brackets("{", Grid(cases.Rows, style, "lpile", style.Size * 0.8f), "", style);

    private EquationBox Pile(IReadOnlyList<MathNode> rows, string align, Style style) =>
        Grid([.. rows.Select(r => (IReadOnlyList<MathNode>)[r])], style, align, 0);

    // ───────────────────────── Brackets and accents ─────────────────────────

    /// <summary>The body between brackets drawn as tall as it is (at least as a letter), centred on the math axis.</summary>
    private EquationBox Brackets(string left, EquationBox body, string right, Style style)
    {
        var size = style.Size;
        var axis = size * Axis;
        var half = Math.Max(Math.Max(body.Ascent - axis, body.Descent + axis), size * 0.5f) + size * 0.05f;
        var (top, bottom) = (-(axis + half), -(axis - half));
        var leftWidth = BracketWidth(left, size, half);
        var rightWidth = BracketWidth(right, size, half);
        var pad = size * 0.05f;
        var width = leftWidth + pad + body.Width + pad + rightWidth;
        var stroke = Math.Max(0.5f, size * 0.06f);
        var parts = new List<(float, float, EquationBox)> { (leftWidth + pad, 0, body) };
        if (left.Length > 0)
            parts.Add((0, 0, new EquationBox(leftWidth, 0, 0, Drawn((canvas, x, y) => Bracket(canvas, left, x, y + top, y + bottom, leftWidth, stroke, style.Color, false)))));
        if (right.Length > 0)
            parts.Add((width - rightWidth, 0, new EquationBox(rightWidth, 0, 0, Drawn((canvas, x, y) => Bracket(canvas, right, x, y + top, y + bottom, rightWidth, stroke, style.Color, true)))));
        return Place(width, Math.Max(body.Ascent, -top), Math.Max(body.Descent, bottom), [.. parts]);
    }

    private static float BracketWidth(string bracket, float size, float half) => bracket switch
    {
        "" => 0,
        "|" or "‖" => size * (bracket == "|" ? 0.2f : 0.35f),
        "{" or "}" => size * 0.4f + half * 0.05f,
        _ => size * 0.3f + half * 0.05f,
    };

    /// <summary>A bracket from <paramref name="top"/> to <paramref name="bottom"/> in a box <paramref name="width"/> wide.</summary>
    private static void Bracket(SKCanvas canvas, string bracket, float x, float top, float bottom, float width, float stroke, SKColor color, bool closing)
    {
        using var pen = Pen(color, stroke);
        using var path = new SKPath();
        var (inner, outer) = closing ? (x + width * 0.25f, x + width * 0.75f) : (x + width * 0.75f, x + width * 0.25f);
        var middle = (top + bottom) / 2;
        switch (bracket)
        {
            case "(" or ")":
                path.MoveTo(inner, top);
                path.QuadTo(outer - (inner - outer) * 0.3f, middle, inner, bottom);
                break;
            case "[" or "]":
                path.MoveTo(inner, top);
                path.LineTo(outer, top);
                path.LineTo(outer, bottom);
                path.LineTo(inner, bottom);
                break;
            case "⌊" or "⌋":
                path.MoveTo(outer, top);
                path.LineTo(outer, bottom);
                path.LineTo(inner, bottom);
                break;
            case "⌈" or "⌉":
                path.MoveTo(inner, top);
                path.LineTo(outer, top);
                path.LineTo(outer, bottom);
                break;
            case "{" or "}":
                {
                    var center = (inner + outer) / 2;
                    var q = (bottom - top) / 4;
                    path.MoveTo(inner, top);
                    path.QuadTo(center, top, center, top + q * 0.5f);
                    path.LineTo(center, middle - q * 0.5f);
                    path.QuadTo(center, middle, outer, middle);
                    path.QuadTo(center, middle, center, middle + q * 0.5f);
                    path.LineTo(center, bottom - q * 0.5f);
                    path.QuadTo(center, bottom, inner, bottom);
                    break;
                }
            case "⟨" or "⟩" or "<" or ">" or "〈" or "〉":
                path.MoveTo(inner, top);
                path.LineTo(outer, middle);
                path.LineTo(inner, bottom);
                break;
            case "‖":
                path.MoveTo(x + width * 0.3f, top);
                path.LineTo(x + width * 0.3f, bottom);
                path.MoveTo(x + width * 0.7f, top);
                path.LineTo(x + width * 0.7f, bottom);
                break;
            default:
                path.MoveTo(x + width / 2, top);
                path.LineTo(x + width / 2, bottom);
                break;
        }
        canvas.DrawPath(path, pen);
    }

    private EquationBox Accent(string command, EquationBox body, Style style)
    {
        var size = style.Size;
        var stroke = Math.Max(0.4f, size * 0.05f);
        var top = Math.Max(body.Ascent, size * 0.7f) + size * 0.08f;
        var mark = size * 0.18f;
        var under = command == "under";
        var strike = command == "not";
        var (ascent, descent) = (under || strike ? body.Ascent : top + mark, under ? body.Descent + size * 0.12f : body.Descent);
        var accent = new EquationBox(body.Width, 0, 0, Drawn((canvas, x, y) =>
        {
            using var pen = Pen(style.Color, stroke);
            using var path = new SKPath();
            var (left, right, middle) = (x + size * 0.05f, x + body.Width - size * 0.02f, x + body.Width / 2 + size * 0.05f);
            var line = y - top - mark / 2;
            switch (command)
            {
                case "bar":
                    path.MoveTo(left, line);
                    path.LineTo(right, line);
                    break;
                case "under":
                    path.MoveTo(left, y + body.Descent + size * 0.06f);
                    path.LineTo(right, y + body.Descent + size * 0.06f);
                    break;
                case "vec" or "dyad":
                    path.MoveTo(left, line);
                    path.LineTo(right, line);
                    path.MoveTo(right - mark, line - mark / 2);
                    path.LineTo(right, line);
                    path.LineTo(right - mark, line + mark / 2);
                    if (command == "dyad")
                    {
                        path.MoveTo(left + mark, line - mark / 2);
                        path.LineTo(left, line);
                        path.LineTo(left + mark, line + mark / 2);
                    }
                    break;
                case "hat":
                    path.MoveTo(middle - Math.Max(mark, body.Width * 0.4f), line + mark / 2);
                    path.LineTo(middle, line - mark / 2);
                    path.LineTo(middle + Math.Max(mark, body.Width * 0.4f), line + mark / 2);
                    break;
                case "check":
                    path.MoveTo(middle - mark, line - mark / 2);
                    path.LineTo(middle, line + mark / 2);
                    path.LineTo(middle + mark, line - mark / 2);
                    break;
                case "tilde":
                    {
                        var w = Math.Max(mark * 2, body.Width * 0.8f);
                        path.MoveTo(middle - w / 2, line + mark / 3);
                        path.CubicTo(middle - w / 4, line - mark, middle + w / 4, line + mark, middle + w / 2, line - mark / 3);
                        break;
                    }
                case "breve" or "arch":
                    {
                        var w = command == "arch" ? body.Width : mark * 2;
                        path.MoveTo(middle - w / 2, line - mark / 2);
                        path.QuadTo(middle, line + (command == "arch" ? -mark : mark), middle + w / 2, line - mark / 2);
                        break;
                    }
                case "acute" or "grave":
                    path.MoveTo(middle - mark / 2, line + (command == "acute" ? mark / 2 : -mark / 2));
                    path.LineTo(middle + mark / 2, line + (command == "acute" ? -mark / 2 : mark / 2));
                    break;
                case "dot" or "ddot":
                    {
                        using var fill = new SKPaint { Color = style.Color, IsAntialias = true };
                        var r = Math.Max(0.5f, size * 0.05f);
                        if (command == "dot")
                        {
                            canvas.DrawCircle(middle, line, r, fill);
                        }
                        else
                        {
                            canvas.DrawCircle(middle - mark / 2, line, r, fill);
                            canvas.DrawCircle(middle + mark / 2, line, r, fill);
                        }
                        break;
                    }
                case "not":
                    path.MoveTo(x + body.Width * 0.75f, y - body.Ascent);
                    path.LineTo(x + body.Width * 0.25f, y + body.Descent);
                    break;
            }
            canvas.DrawPath(path, pen);
        }));
        return Place(body.Width, ascent, descent, (0, 0, body), (0, 0, accent));
    }
}
