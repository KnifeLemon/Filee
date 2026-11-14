// Equations in a paragraph's lines, and Word's equation numbers on Filee's own route.
//
// - An equation is laid out once (MathLayout) at its size, its own or else its text's, in its colour.
// - Word's equation numbers ("E#(1)", a model read from Word): the reader keeps the number of a one-line equation in a
//   text box placed for rhwp (DocxReader.NumberBox), which the page composer hands over instead (Number). The lines of
//   an equation array each with their own number ("a&=b#(1)" in every line) keep them in the equation. On this route
//   each number goes to the right end of its equation's line, its ink centred on the math axis: of the whole equation
//   (a fraction's bar, the middle of an equation array) or of its own line of the array, as Word places it.

using System.Runtime.CompilerServices;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

internal sealed partial class ParagraphLayouter
{
    /// <summary>
    /// Equations are Word's (a model of another format): the numbers in the lines of an equation array ("#(1)") go to
    /// the right end of the line, level with their own line of the array.
    /// </summary>
    public bool WordEquations { get; set; }

    private readonly ConditionalWeakTable<HEquation, HText> _numbers = [];

    /// <summary>
    /// Draws <paramref name="number"/> at the right end of <paramref name="equation"/>'s line, centred on its math axis
    /// (the page composer's, for the number DocxReader keeps in a text box for rhwp).
    /// </summary>
    public void Number(HEquation equation, HText number) => _numbers.AddOrUpdate(equation, number);

    /// <summary>A number's glyphs, from its left edge (x = 0) and the equation's baseline, and its advance.</summary>
    private sealed record EquationNumber(IReadOnlyList<PlacedRun> Runs, float Width);

    /// <summary>An equation laid out, with its numbers.</summary>
    private sealed record LaidEquation(EquationBox Box, IReadOnlyList<EquationNumber> Numbers);

    private readonly Dictionary<HEquation, LaidEquation> _equations = new(ReferenceEqualityComparer.Instance);

    /// <summary>An equation laid out at its size (its own, else its text's) in its colour.</summary>
    private EquationBox Equation(HEquation equation) => Laid(equation).Box;

    private LaidEquation Laid(HEquation equation)
    {
        if (_equations.TryGetValue(equation, out var laid))
            return laid;
        var size = (equation.Size ?? equation.Format.Size ?? 1000) / 100f;
        var color = ParagraphText.Color(equation.Color ?? equation.Format.Color) ?? SKColors.Black;
        var axis = size * MathLayout.Axis;
        if (_equations.Count > 10_000)
            _equations.Clear();
        if (_numbers.TryGetValue(equation, out var number))
        {
            laid = new LaidEquation(_math.Layout(equation.Math, size, color), [Placed(number, -axis)]);
        }
        else if (WordEquations && LineNumbers(equation.Math) is var (pile, numbers))
        {
            var baselines = new List<float>();
            var box = _math.Lines(pile, size, color, baselines);
            var format = equation.Format with { Italic = false };
            var placed = new List<EquationNumber>();
            for (var i = 0; i < numbers.Count && i < baselines.Count; i++)
            {
                if (numbers[i] is { } line)
                    placed.Add(Placed(new HText(EquationScript.Linear(line), format), baselines[i] - axis));
            }
            laid = new LaidEquation(box, placed);
        }
        else
        {
            laid = new LaidEquation(_math.Layout(equation.Math, size, color), []);
        }
        return _equations[equation] = laid;
    }

    /// <summary>
    /// An equation array whose lines have their own numbers ("a&amp;=b#(1)"): the array without them and each line's
    /// number (null for a line without one); null for any other equation.
    /// </summary>
    private static (MathPile Pile, List<MathNode?> Numbers)? LineNumbers(MathNode math)
    {
        var inner = math;
        while (inner is MathRow { Items: [var only] })
            inner = only;
        if (inner is not MathPile { Rows.Count: >= 2 } pile)
            return null;
        var rows = new List<MathNode>(pile.Rows.Count);
        var numbers = new List<MathNode?>(pile.Rows.Count);
        foreach (var row in pile.Rows)
        {
            var (body, number) = OmmlReader.SplitNumber(new MathPile([row]));
            rows.Add(number is null ? row : body);
            numbers.Add(number);
        }
        return numbers.Any(n => n is not null) ? (pile with { Rows = rows }, numbers) : null;
    }

    /// <summary>A number laid out as text, its ink centred <paramref name="middle"/> below the equation's baseline (up when negative).</summary>
    private EquationNumber Placed(HText number, float middle)
    {
        var paragraph = new HParagraph { Format = new HParaFormat(Align: HAlign.Left, Before: 0, After: 0) };
        paragraph.Inlines.Add(new HText(number.Text, number.Format));
        if (Layout(paragraph, 10_000).Lines is not [var line, ..])
            return new EquationNumber([], 0);
        var ink = Ink(line.Runs);
        var down = ink.IsEmpty ? 0 : middle - ink.MidY;
        var runs = line.Runs.Select(run => run with { Positions = [.. run.Positions.Select(p => new SKPoint(p.X - line.Left, p.Y + down))] }).ToList();
        return new EquationNumber(runs, line.Width);
    }

    /// <summary>
    /// The runs of the numbers of an equation on its line, the numbers' right ends at <paramref name="right"/> (from the
    /// column edge); none for an equation without numbers.
    /// </summary>
    private IEnumerable<PlacedRun> Numbers(HEquation equation, float right)
    {
        foreach (var number in Laid(equation).Numbers)
        {
            var across = right - number.Width;
            foreach (var run in number.Runs)
                yield return run with { Positions = [.. run.Positions.Select(p => new SKPoint(p.X + across, p.Y))] };
        }
    }

    /// <summary>Where the outlines of <paramref name="runs"/>' glyphs are, relative to their origin.</summary>
    private static SKRect Ink(IEnumerable<PlacedRun> runs)
    {
        // Measured large and scaled down: Skia rounds glyph bounds out to whole units of the size it measures at.
        const float Large = 1000;
        var ink = SKRect.Empty;
        foreach (var run in runs)
        {
            using var font = new SKFont(run.Font.Typeface, Large) { LinearMetrics = true, Hinting = SKFontHinting.None };
            var widths = new float[run.Glyphs.Length];
            var bounds = new SKRect[run.Glyphs.Length];
            font.GetGlyphWidths(run.Glyphs, widths, bounds);
            var scale = run.Size / Large;
            for (var i = 0; i < bounds.Length; i++)
            {
                if (bounds[i].IsEmpty)
                    continue;
                var (x, y) = (run.Positions[i].X, run.Positions[i].Y);
                var glyph = new SKRect(x + bounds[i].Left * scale * run.ScaleX, y + bounds[i].Top * scale, x + bounds[i].Right * scale * run.ScaleX,
                    y + bounds[i].Bottom * scale);
                ink = ink.IsEmpty ? glyph : SKRect.Union(ink, glyph);
            }
        }
        return ink;
    }
}
