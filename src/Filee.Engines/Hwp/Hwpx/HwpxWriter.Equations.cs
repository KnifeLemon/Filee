// Display equations too wide for their column, broken into lines for 한글: a 한글 equation is one object that never
// wraps, so a Word or TeX display formula wider than the column (which Filee's layout and Word break by themselves) is
// written as one equation per line, each in a centred paragraph of its own, broken before binary and relation
// operators (EquationScript.BreakLines, Word's rule) where both rhwp's estimate and Filee's drawing fit the column.
// Paragraphs of their own rather than lines of one: rhwp draws the first equation of a paragraph split by a page again
// on the next page.

using Filee.Engines.Typesetting;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxWriter
{
    private (FontResolver Fonts, Shaper Shaper, MathLayout Math)? _equationLayout;

    /// <summary>
    /// <paramref name="paragraph"/> as several paragraphs when it holds display equations only (with line breaks
    /// between them) and one is wider than the column; null when it stays as it is.
    /// </summary>
    private List<HParagraph>? EquationLines(HParagraph paragraph, int fallbackSize)
    {
        var shown = paragraph.Inlines.Where(i => i is not HBookmark).ToList();
        var equations = shown.OfType<HEquation>().ToList();
        if (equations.Count == 0 || shown.Any(i => i is not (HEquation or HLineBreak))
            || !equations.All(e => e.Display || (equations.Count == 1 && HEquation.IsDisplay(e, paragraph)))
            || equations.Any(e => e.Width is not null))
            return null;
        var width = _width - (paragraph.Format.Left ?? 0) - (paragraph.Format.Right ?? 0);
        if (width <= 0)
            return null;

        var rows = new List<(HEquation Source, MathNode Row)>();
        var broken = false;
        foreach (var equation in equations)
        {
            var size = Math.Max(100, equation.Format.Size ?? fallbackSize);
            IReadOnlyList<MathNode> lines = [equation.Math];
            if (EquationScript.Measure(equation.Math, size).Width > width / 2)
            {
                // Lines that fit Filee's own drawing too, which is wider than rhwp's estimate (#355).
                if (_equationLayout is null)
                {
                    var (fonts, shaper) = (new FontResolver(), new Shaper());
                    _equationLayout = (fonts, shaper, new MathLayout(fonts, shaper));
                }
                var drawn = _equationLayout.Value.Math;
                lines = EquationScript.BreakLines(equation.Math, width, size, node => drawn.ItemWidth(node, size / 100f) * 100);
            }
            broken |= lines.Count > 1;
            rows.AddRange(lines.Select(line => (equation, line)));
        }
        if (!broken)
            return null;

        var result = new List<HParagraph>();
        for (var i = 0; i < rows.Count; i++)
        {
            var (first, last) = (i == 0, i == rows.Count - 1);
            var line = new HParagraph
            {
                Format = paragraph.Format with
                {
                    Before = first ? paragraph.Format.Before : 0,
                    After = last ? paragraph.Format.After : 0,
                },
                MarkFormat = paragraph.MarkFormat,
                HeadingLevel = paragraph.HeadingLevel,
                List = first ? paragraph.List : paragraph.List is { } list ? list with { Numbered = false } : null,
                PageBreakBefore = first && paragraph.PageBreakBefore,
                ColumnBreakBefore = first && paragraph.ColumnBreakBefore,
                ColumnsChange = first ? paragraph.ColumnsChange : null,
                Role = paragraph.Role,
            };
            if (first)
                line.Inlines.AddRange(paragraph.Inlines.OfType<HBookmark>());
            var source = rows[i].Source;
            line.Inlines.Add(new HEquation(rows[i].Row, source.Format) { Display = true, Color = source.Color, Size = source.Size });
            result.Add(line);
        }
        return result;
    }
}
