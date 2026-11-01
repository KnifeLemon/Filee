// Conditional formats that only change how a cell looks (XLSX conditionalFormatting with its dxf styles), for
// SheetPrinter: comparisons with constants, text contains / begins / ends, blanks, errors, top / bottom N (or %),
// above / below average, duplicate and unique values, and two- and three-colour scales. Rules whose condition is a
// formula of its own (type "expression"), data bars and icon sets are not evaluated: their cells keep their own look.
//
// Rules are applied in priority order; a property set by a rule of higher priority wins, and stopIfTrue ends the
// cell's rules, as in Excel.

using System.Globalization;

namespace Filee.Engines.Office.Sheets;

/// <summary>What a matching rule changes (Excel's differential format, dxf); null members are left as they are.</summary>
internal sealed record DifferentialStyle
{
    public string? Fill { get; init; }
    public string? Color { get; init; }
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public bool? Underline { get; init; }
    public bool? Strike { get; init; }
    public CellBorder? Left { get; init; }
    public CellBorder? Right { get; init; }
    public CellBorder? Top { get; init; }
    public CellBorder? Bottom { get; init; }
}

/// <summary>One point of a colour scale: cfvo type ("min", "max", "num", "percent", "percentile"), its value and colour.</summary>
internal readonly record struct ScalePoint(string Type, double Value, string Color);

/// <summary>A rule and the ranges it applies to.</summary>
/// <param name="Type">cellIs, containsText, notContainsText, beginsWith, endsWith, containsBlanks, notContainsBlanks,
/// containsErrors, notContainsErrors, top10, aboveAverage, duplicateValues, uniqueValues, colorScale.</param>
internal sealed record ConditionalFormat(IReadOnlyList<CellRange> Ranges, string Type, int Priority)
{
    public string? Operator { get; init; }

    /// <summary>The constants a cellIs rule compares with (numbers, or text in quotes).</summary>
    public IReadOnlyList<string> Formulas { get; init; } = [];
    public string? Text { get; init; }
    public int Rank { get; init; } = 10;
    public bool Percent { get; init; }
    public bool Bottom { get; init; }
    public bool AboveAverage { get; init; } = true;
    public bool EqualAverage { get; init; }
    public int StdDev { get; init; }
    public bool StopIfTrue { get; init; }
    public DifferentialStyle? Style { get; init; }
    public IReadOnlyList<ScalePoint> Scale { get; init; } = [];
}

internal static class ConditionalFormatting
{
    /// <summary>Cells one sheet's rules look at in all; a sheet whose ranges hold more keeps its own look beyond.</summary>
    private const int MaxCells = 2_000_000;

    /// <summary>The look of every cell a rule changes, by (row, column); the cell's own style with the rules applied.</summary>
    /// <param name="area">The cells that are printed: rules aren't evaluated outside (blank cells especially).</param>
    public static Dictionary<(int Row, int Column), CellStyle> Apply(Worksheet sheet, CellRange area)
    {
        var result = new Dictionary<(int, int), CellStyle>();
        if (sheet.ConditionalFormats.Count == 0)
            return result;
        var decided = new Dictionary<(int, int), Decided>();
        var budget = MaxCells;
        foreach (var rule in sheet.ConditionalFormats.OrderBy(r => r.Priority))
        {
            var cells = Cells(sheet, rule, area, ref budget);
            if (cells.Count == 0)
                continue;
            var matches = Matches(rule, cells);
            foreach (var (position, color) in matches)
            {
                ref var state = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(decided, position, out _);
                if (state.Stopped)
                    continue;
                state.Apply(rule.Style, color);
                if (rule.StopIfTrue)
                    state.Stopped = true;
            }
        }
        foreach (var (position, state) in decided)
        {
            var style = sheet.Get(position.Item1, position.Item2)?.Style ?? CellStyle.Default;
            var changed = state.On(style);
            if (!ReferenceEquals(changed, style))
                result[position] = changed;
        }
        return result;
    }

    /// <summary>The cells of the rule's ranges inside the printed area: the ones with a value, and blanks for blank rules.</summary>
    private static List<((int, int) Position, SheetCell? Cell)> Cells(Worksheet sheet, ConditionalFormat rule, CellRange area, ref int budget)
    {
        var cells = new List<((int, int), SheetCell?)>();
        var blanks = rule.Type is "containsBlanks" or "notContainsBlanks";
        foreach (var range in rule.Ranges)
        {
            var top = Math.Max(range.Top, area.Top);
            var bottom = Math.Min(range.Bottom, area.Bottom);
            var left = Math.Max(range.Left, area.Left);
            var right = Math.Min(range.Right, area.Right);
            if (top > bottom || left > right)
                continue;
            if (blanks)
            {
                if ((long)(bottom - top + 1) * (right - left + 1) > budget)
                    return [];
                budget -= (bottom - top + 1) * (right - left + 1);
                for (var r = top; r <= bottom; r++)
                    for (var c = left; c <= right; c++)
                        cells.Add(((r, c), sheet.Get(r, c)));
                continue;
            }
            foreach (var (r, row) in sheet.Rows)
            {
                if (r < top || r > bottom)
                    continue;
                foreach (var (c, cell) in row)
                {
                    if (c < left || c > right || cell.Text.Length == 0)
                        continue;
                    if (--budget < 0)
                        return [];
                    cells.Add(((r, c), cell));
                }
            }
        }
        return cells;
    }

    private static IEnumerable<((int, int) Position, string? Color)> Matches(ConditionalFormat rule, List<((int, int) Position, SheetCell? Cell)> cells)
    {
        switch (rule.Type)
        {
            case "colorScale":
                foreach (var match in ColorScale(rule, cells))
                    yield return match;
                yield break;
            case "top10":
            case "aboveAverage":
            case "duplicateValues":
            case "uniqueValues":
                foreach (var position in Statistical(rule, cells))
                    yield return (position, null);
                yield break;
        }
        foreach (var (position, cell) in cells)
        {
            if (Test(rule, cell))
                yield return (position, null);
        }
    }

    private static bool Test(ConditionalFormat rule, SheetCell? cell)
    {
        var text = cell?.Text ?? "";
        switch (rule.Type)
        {
            case "containsBlanks":
                return text.Trim().Length == 0;
            case "notContainsBlanks":
                return text.Trim().Length > 0;
            case "containsErrors":
                return cell?.IsError == true;
            case "notContainsErrors":
                return cell?.IsError != true;
            case "containsText":
                return rule.Text is { } t && text.Contains(t, StringComparison.OrdinalIgnoreCase);
            case "notContainsText":
                return rule.Text is { } n && !text.Contains(n, StringComparison.OrdinalIgnoreCase);
            case "beginsWith":
                return rule.Text is { } b && text.StartsWith(b, StringComparison.OrdinalIgnoreCase);
            case "endsWith":
                return rule.Text is { } e && text.EndsWith(e, StringComparison.OrdinalIgnoreCase);
            case "cellIs":
                return CellIs(rule, cell);
            default:
                return false;
        }
    }

    private static bool CellIs(ConditionalFormat rule, SheetCell? cell)
    {
        if (cell is null || rule.Formulas.Count == 0)
            return false;
        var a = Constant(rule.Formulas[0]);
        var b = rule.Formulas.Count > 1 ? Constant(rule.Formulas[1]) : null;
        if (a is null)
            return false;
        int Compare(object constant)
        {
            if (constant is double number)
                return cell.Value is { } value && !cell.IsBoolean ? value.CompareTo(number) : 1; // text sorts after numbers
            return cell.Value is not null && !cell.IsBoolean ? -1 : string.Compare(cell.Text, (string)constant, StringComparison.OrdinalIgnoreCase);
        }
        var first = Compare(a);
        return rule.Operator switch
        {
            "equal" => first == 0,
            "notEqual" => first != 0,
            "greaterThan" => first > 0,
            "lessThan" => first < 0,
            "greaterThanOrEqual" => first >= 0,
            "lessThanOrEqual" => first <= 0,
            "between" => b is not null && Between(first, Compare(b), a, b),
            "notBetween" => b is not null && !Between(first, Compare(b), a, b),
            _ => false,
        };
    }

    /// <summary>Between two bounds in either order.</summary>
    private static bool Between(int toFirst, int toSecond, object first, object second)
    {
        var ascending = first is double x && second is double y ? x <= y : string.Compare(first.ToString(), second.ToString(), StringComparison.OrdinalIgnoreCase) <= 0;
        return ascending ? toFirst >= 0 && toSecond <= 0 : toFirst <= 0 && toSecond >= 0;
    }

    /// <summary>A number, or text in double quotes; null for a formula Filee doesn't evaluate.</summary>
    internal static object? Constant(string formula)
    {
        formula = formula.Trim();
        if (formula.Length >= 2 && formula[0] == '"' && formula[^1] == '"')
            return formula[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        return double.TryParse(formula, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static IEnumerable<(int, int)> Statistical(ConditionalFormat rule, List<((int, int) Position, SheetCell? Cell)> cells)
    {
        if (rule.Type is "duplicateValues" or "uniqueValues")
        {
            var counts = cells.Where(c => c.Cell is { Text.Length: > 0 }).GroupBy(c => c.Cell!.Text, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var (position, cell) in cells)
            {
                if (cell is { Text.Length: > 0 } && counts[cell.Text] > 1 == (rule.Type == "duplicateValues"))
                    yield return position;
            }
            yield break;
        }
        var numbers = cells.Where(c => c.Cell is { Value: not null, IsBoolean: false }).Select(c => (c.Position, Value: c.Cell!.Value!.Value)).ToList();
        if (numbers.Count == 0)
            yield break;
        if (rule.Type == "top10")
        {
            var count = rule.Percent ? Math.Max(1, (int)Math.Floor(numbers.Count * rule.Rank / 100.0)) : rule.Rank;
            var sorted = rule.Bottom ? numbers.OrderBy(n => n.Value).ToList() : numbers.OrderByDescending(n => n.Value).ToList();
            if (count <= 0)
                yield break;
            var limit = sorted[Math.Min(count, sorted.Count) - 1].Value;
            foreach (var (position, value) in numbers)
            {
                if (rule.Bottom ? value <= limit : value >= limit)
                    yield return position;
            }
            yield break;
        }
        var average = numbers.Average(n => n.Value);
        var deviation = rule.StdDev > 0 ? Math.Sqrt(numbers.Sum(n => (n.Value - average) * (n.Value - average)) / Math.Max(1, numbers.Count - 1)) * rule.StdDev : 0;
        foreach (var (position, value) in numbers)
        {
            var bound = rule.AboveAverage ? average + deviation : average - deviation;
            var match = rule.AboveAverage
                ? value > bound || rule.EqualAverage && value == bound
                : value < bound || rule.EqualAverage && value == bound;
            if (match)
                yield return position;
        }
    }

    private static IEnumerable<((int, int), string?)> ColorScale(ConditionalFormat rule, List<((int, int) Position, SheetCell? Cell)> cells)
    {
        var numbers = cells.Where(c => c.Cell is { Value: not null, IsBoolean: false }).Select(c => (c.Position, Value: c.Cell!.Value!.Value)).ToList();
        if (numbers.Count == 0 || rule.Scale.Count < 2)
            yield break;
        var sorted = numbers.Select(n => n.Value).Order().ToArray();
        double Threshold(ScalePoint point) => point.Type switch
        {
            "min" => sorted[0],
            "max" => sorted[^1],
            "percent" => sorted[0] + (sorted[^1] - sorted[0]) * point.Value / 100,
            "percentile" => Percentile(sorted, point.Value / 100),
            _ => point.Value,
        };
        var thresholds = rule.Scale.Select(Threshold).ToArray();
        foreach (var (position, value) in numbers)
        {
            string color;
            if (value <= thresholds[0])
                color = rule.Scale[0].Color;
            else if (value >= thresholds[^1])
                color = rule.Scale[^1].Color;
            else
            {
                var i = 1;
                while (i < thresholds.Length - 1 && value > thresholds[i])
                    i++;
                var span = thresholds[i] - thresholds[i - 1];
                color = Mix(rule.Scale[i - 1].Color, rule.Scale[i].Color, span <= 0 ? 1 : (value - thresholds[i - 1]) / span);
            }
            yield return (position, color);
        }
    }

    private static double Percentile(double[] sorted, double p)
    {
        var rank = Math.Clamp(p, 0, 1) * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = Math.Min(low + 1, sorted.Length - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    /// <summary>"#RRGGBB" between two colours.</summary>
    internal static string Mix(string from, string to, double t)
    {
        static (int, int, int) Rgb(string hex) => hex is { Length: 7 } && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? (v >> 16 & 0xFF, v >> 8 & 0xFF, v & 0xFF) : (255, 255, 255);
        var (r1, g1, b1) = Rgb(from);
        var (r2, g2, b2) = Rgb(to);
        int Lerp(int a, int b) => (int)Math.Round(a + (b - a) * Math.Clamp(t, 0, 1));
        return $"#{Lerp(r1, r2):X2}{Lerp(g1, g2):X2}{Lerp(b1, b2):X2}";
    }

    /// <summary>What the rules decided for one cell so far (the first rule to set a property wins).</summary>
    private struct Decided
    {
        public bool Stopped;
        private string? _fill, _color;
        private bool? _bold, _italic, _underline, _strike;
        private CellBorder? _left, _right, _top, _bottom;

        public void Apply(DifferentialStyle? style, string? scaleColor)
        {
            _fill ??= scaleColor ?? style?.Fill;
            if (style is null)
                return;
            _color ??= style.Color;
            _bold ??= style.Bold;
            _italic ??= style.Italic;
            _underline ??= style.Underline;
            _strike ??= style.Strike;
            _left ??= style.Left;
            _right ??= style.Right;
            _top ??= style.Top;
            _bottom ??= style.Bottom;
        }

        public readonly CellStyle On(CellStyle style)
        {
            if (_fill is null && _color is null && _bold is null && _italic is null && _underline is null && _strike is null
                && _left is null && _right is null && _top is null && _bottom is null)
                return style;
            return style with
            {
                Fill = _fill ?? style.Fill,
                FillPattern = _fill is null ? style.FillPattern : null,
                Gradient = _fill is null ? style.Gradient : null,
                Color = _color ?? style.Color,
                Bold = _bold ?? style.Bold,
                Italic = _italic ?? style.Italic,
                Underline = _underline ?? style.Underline,
                Strike = _strike ?? style.Strike,
                Left = _left ?? style.Left,
                Right = _right ?? style.Right,
                Top = _top ?? style.Top,
                Bottom = _bottom ?? style.Bottom,
            };
        }
    }
}
