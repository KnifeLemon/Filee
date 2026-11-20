// Trendlines and error bar lengths of a series, as Excel and LibreOffice work them out from the points: least squares
// fits of a line, an exponential, a logarithm, a polynomial (order 2 to 6) or a power curve (the exponential and power
// fitted to ln y, as both programs do), optionally through a given intercept; the moving average of a period; R² of
// the fit; the equation and R² as label text with superscripts. Error bars take Excel's statistics: the standard
// deviation of the series' values (bars around their mean) and its standard error.
//
// Hostile input: values are finite and clamped by the readers; a fit whose numbers overflow or whose equations are
// singular is no fit (null), so nothing non-finite reaches the drawing.

using System.Globalization;

namespace Filee.Engines.Charts;

/// <summary>A fitted trendline: its curve, R², and the coefficients its label shows.</summary>
internal sealed record TrendFit(Func<double, double> Curve, double RSquared, IReadOnlyList<double> Coefficients);

internal static class ChartTrend
{
    /// <summary>
    /// Fits <paramref name="trend"/> (not a moving average) to <paramref name="points"/>; null when there are too few
    /// usable points (positive ones for the logarithmic, exponential and power curves) or the fit doesn't work out.
    /// Coefficients: linear (slope, intercept), exponential and power (factor, exponent), logarithmic (factor of ln x,
    /// constant), polynomial (constant, x, x², ...).
    /// </summary>
    public static TrendFit? Fit(ChartTrendline trend, IReadOnlyList<(double X, double Y)> points)
    {
        var intercept = trend.Intercept;
        switch (trend.Kind)
        {
            case TrendKind.Linear:
                {
                    if (Polynomial(points, 1, intercept) is not { } c)
                        return null;
                    return Finished(x => c[0] + c[1] * x, points, intercept, c, [c[1], c[0]]);
                }
            case TrendKind.Polynomial:
                {
                    if (Polynomial(points, trend.Order, intercept) is not { } c)
                        return null;
                    return Finished(x => Evaluate(c, x), points, intercept, c, c);
                }
            case TrendKind.Exponential:
                {
                    // ln y = ln b + a x
                    var logs = points.Where(p => p.Y > 0).Select(p => (p.X, Math.Log(p.Y))).ToList();
                    double? fixedLog = intercept is > 0 and var b0 ? Math.Log(b0) : null;
                    if (Polynomial(logs, 1, fixedLog) is not { } c)
                        return null;
                    var (factor, exponent) = (Math.Exp(c[0]), c[1]);
                    return Finished(x => factor * Math.Exp(exponent * x), logs, fixedLog, c, [factor, exponent]);
                }
            case TrendKind.Logarithmic:
                {
                    // y = a ln x + b
                    var logs = points.Where(p => p.X > 0).Select(p => (Math.Log(p.X), p.Y)).ToList();
                    if (Polynomial(logs, 1, null) is not { } c)
                        return null;
                    return Finished(x => x > 0 ? c[1] * Math.Log(x) + c[0] : double.NaN, logs, null, c, [c[1], c[0]]);
                }
            case TrendKind.Power:
                {
                    // ln y = ln b + a ln x
                    var logs = points.Where(p => p.X > 0 && p.Y > 0).Select(p => (Math.Log(p.X), Math.Log(p.Y))).ToList();
                    if (Polynomial(logs, 1, null) is not { } c)
                        return null;
                    var (factor, exponent) = (Math.Exp(c[0]), c[1]);
                    return Finished(x => x > 0 ? factor * Math.Pow(x, exponent) : double.NaN, logs, null, c, [factor, exponent]);
                }
            default:
                return null;
        }
    }

    /// <summary>The moving average of <paramref name="period"/> points, at the X of each window's last point.</summary>
    public static List<(double X, double Y)> MovingAverage(IReadOnlyList<(double X, double Y)> points, int period)
    {
        var result = new List<(double X, double Y)>();
        if (period < 2)
            return result;
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            sum += points[i].Y;
            if (i >= period)
                sum -= points[i - period].Y;
            if (i >= period - 1 && double.IsFinite(sum))
                result.Add((points[i].X, sum / period));
        }
        return result;
    }

    /// <summary>
    /// The fit with its R²: 1 − SSE / SST over the <paramref name="fitted"/> points in the space the fit was made in
    /// (ln y for the exponential, ln x for the logarithm, both for the power curve) against <paramref name="line"/>
    /// there, SST about the mean, or about the forced intercept as Excel's LINEST without a constant does.
    /// </summary>
    private static TrendFit? Finished(Func<double, double> curve, IReadOnlyList<(double X, double Y)> fitted, double? intercept,
        double[] line, IReadOnlyList<double> coefficients)
    {
        if (coefficients.Any(c => !double.IsFinite(c)))
            return null;
        double sse = 0, sst = 0;
        var centre = intercept ?? (fitted.Count == 0 ? 0 : fitted.Average(p => p.Y));
        foreach (var (x, y) in fitted)
        {
            var estimate = Evaluate(line, x);
            sse += (y - estimate) * (y - estimate);
            sst += (y - centre) * (y - centre);
        }
        var r2 = sst > 0 ? 1 - sse / sst : sse == 0 ? 1 : 0;
        return double.IsFinite(r2) ? new TrendFit(curve, r2, coefficients) : null;
    }

    private static double Evaluate(IReadOnlyList<double> c, double x)
    {
        double y = 0;
        for (var k = c.Count - 1; k >= 0; k--)
            y = y * x + c[k];
        return y;
    }

    /// <summary>
    /// Least squares coefficients (constant first) of a polynomial of <paramref name="order"/> through the points, with
    /// the constant fixed at <paramref name="intercept"/> when given; X is scaled to ±1 to keep the equations well
    /// conditioned. Null with too few points or singular equations.
    /// </summary>
    private static double[]? Polynomial(IReadOnlyList<(double X, double Y)> points, int order, double? intercept)
    {
        var usable = points.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();
        var unknowns = intercept is null ? order + 1 : order;
        if (usable.Count < Math.Max(2, unknowns) || usable.Count < order + (intercept is null ? 1 : 0))
            return null;
        var scale = usable.Max(p => Math.Abs(p.X));
        if (!(scale > 0) || !double.IsFinite(scale))
            scale = 1;
        var first = intercept is null ? 0 : 1;
        var n = order + 1 - first;
        var matrix = new double[n, n + 1];
        var powers = new double[2 * order + 1];
        foreach (var (x, y) in usable)
        {
            var t = x / scale;
            var target = y - (intercept ?? 0);
            powers[0] = 1;
            for (var k = 1; k < powers.Length; k++)
                powers[k] = powers[k - 1] * t;
            for (var r = 0; r < n; r++)
            {
                for (var c = 0; c < n; c++)
                    matrix[r, c] += powers[r + c + 2 * first];
                matrix[r, n] += powers[r + first] * target;
            }
        }
        if (Solve(matrix, n) is not { } solution)
            return null;
        // Terms that are rounding noise next to the largest (X scaled to ±1, so they compare) are zero.
        var largest = solution.Max(Math.Abs);
        for (var i = 0; i < n; i++)
        {
            if (Math.Abs(solution[i]) < largest * 1e-9)
                solution[i] = 0;
        }
        var coefficients = new double[order + 1];
        coefficients[0] = intercept ?? 0;
        for (var k = first; k <= order; k++)
            coefficients[k] = solution[k - first] / Math.Pow(scale, k);
        return coefficients.All(double.IsFinite) ? coefficients : null;
    }

    /// <summary>Gaussian elimination with partial pivoting of an n × (n + 1) system; null when singular or not finite.</summary>
    private static double[]? Solve(double[,] m, int n)
    {
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                    pivot = r;
            }
            if (!(Math.Abs(m[pivot, col]) > 1e-300) || !double.IsFinite(m[pivot, col]))
                return null;
            if (pivot != col)
            {
                for (var c = col; c <= n; c++)
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            }
            for (var r = col + 1; r < n; r++)
            {
                var f = m[r, col] / m[col, col];
                for (var c = col; c <= n; c++)
                    m[r, c] -= f * m[col, c];
            }
        }
        var x = new double[n];
        for (var r = n - 1; r >= 0; r--)
        {
            var sum = m[r, n];
            for (var c = r + 1; c < n; c++)
                sum -= m[r, c] * x[c];
            x[r] = sum / m[r, r];
            if (!double.IsFinite(x[r]))
                return null;
        }
        return x;
    }

    // ───────────────────────── Label ─────────────────────────

    private static readonly string[] Superscripts = ["⁰", "¹", "²", "³", "⁴", "⁵", "⁶"];

    /// <summary>
    /// The trendline's label: the equation ("y = 2.5x + 1.3", powers and exponents raised) and R² on the next line,
    /// numbers through <paramref name="format"/>; empty when it shows neither.
    /// </summary>
    public static List<IReadOnlyList<ChartRun>> Label(ChartTrendline trend, TrendFit fit, ChartTextStyle style, Func<double, string> format)
    {
        var lines = new List<IReadOnlyList<ChartRun>>();
        var raised = style with { Size = style.Size * 2 / 3 };
        if (trend.ShowEquation)
        {
            var runs = new List<ChartRun>();
            var text = new System.Text.StringBuilder("y = ");
            void Flush()
            {
                if (text.Length > 0)
                    runs.Add(new ChartRun(text.ToString(), style));
                text.Clear();
            }
            void Up(string piece)
            {
                Flush();
                runs.Add(new ChartRun(piece, raised, 0.6f));
            }
            var c = fit.Coefficients;
            switch (trend.Kind)
            {
                case TrendKind.Exponential:
                    text.Append(format(c[0])).Append('e');
                    Up(format(c[1]) + "x");
                    break;
                case TrendKind.Power:
                    text.Append(format(c[0])).Append('x');
                    Up(format(c[1]));
                    break;
                case TrendKind.Logarithmic:
                    text.Append(Factor(format(c[0]))).Append("ln(x)");
                    Constant(text, c[1], format);
                    break;
                case TrendKind.Linear:
                    text.Append(Factor(format(c[0]))).Append('x');
                    Constant(text, c[1], format);
                    break;
                default:
                    {
                        // Polynomial: the highest power first, terms whose coefficient shows as 0 left out.
                        var first = true;
                        for (var k = c.Count - 1; k >= 0; k--)
                        {
                            var shown = format(Math.Abs(c[k]));
                            if (shown == format(0) && !(first && k == 0))
                                continue;
                            text.Append(first ? c[k] < 0 ? "-" : "" : c[k] < 0 ? " - " : " + ");
                            text.Append(k == 0 ? shown : Factor(shown));
                            if (k >= 1)
                                text.Append('x');
                            if (k >= 2)
                                Up(k.ToString(CultureInfo.InvariantCulture));
                            first = false;
                        }
                        if (first)
                            text.Append(format(0));
                        break;
                    }
            }
            Flush();
            lines.Add(runs);
        }
        if (trend.ShowRSquared)
            lines.Add([new ChartRun("R", style), new ChartRun("2", raised, 0.6f), new ChartRun(" = " + format(fit.RSquared), style)]);
        return lines;
    }

    /// <summary>A coefficient before x: 1 is left out and −1 is just the sign, as Excel writes them.</summary>
    private static string Factor(string shown) => shown switch
    {
        "1" => "",
        "-1" => "-",
        _ => shown,
    };

    private static void Constant(System.Text.StringBuilder text, double value, Func<double, string> format)
    {
        var shown = format(Math.Abs(value));
        if (shown == format(0))
            return;
        text.Append(value < 0 ? " - " : " + ").Append(shown);
    }

    /// <summary>A trendline number as Excel's label shows it by default: up to four decimals, very large or small ones in E notation.</summary>
    public static string Number(double value)
    {
        var magnitude = Math.Abs(value);
        var text = magnitude >= 1e6 || magnitude > 0 && magnitude < 1e-4
            ? value.ToString("0.####E+0", CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    // ───────────────────────── Error bars ─────────────────────────

    /// <summary>
    /// The mean and standard deviation of some values, and their standard error: Excel's √(Σy² / (n(n − 1))), or
    /// s / √n when not <paramref name="office"/>.
    /// </summary>
    public static (double Mean, double Deviation, double Error) Statistics(IEnumerable<double> values, bool office = true)
    {
        double n = 0, sum = 0, squares = 0;
        foreach (var v in values)
        {
            if (!double.IsFinite(v))
                continue;
            n++;
            sum += v;
            squares += v * v;
        }
        if (n == 0)
            return (0, 0, 0);
        var mean = sum / n;
        if (n < 2)
            return (mean, 0, 0);
        var variance = Math.Max(0, (squares - n * mean * mean) / (n - 1));
        var error = office ? Math.Sqrt(squares / (n * (n - 1))) : Math.Sqrt(variance / n);
        return (mean, Math.Sqrt(variance), double.IsFinite(error) ? error : 0);
    }
}
