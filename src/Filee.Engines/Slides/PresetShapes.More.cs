// The rest of DrawingML's preset shapes (ECMA-376 Part 1, 20.1.10.56 ST_ShapeType), in the same notation as
// PresetShapes.cs and with the standard's names, adjust values and defaults, so any reader that hands a preset name and
// its avLst to ShapeGeometry gets them drawn.
//
// Written from the guide formulas of the standard's presetShapeDefinitions. Where a family repeats one rule (callouts
// with 1 to 3 leader segments, gears of 6 and 9 teeth, the circular arrows, action buttons sharing their frame) the
// notation is made by a small generator. Shapes that are another one turned (curvedLeftArrow, curvedDownArrow and
// curvedUpArrow from curvedRightArrow, ribbon2 and ellipseRibbon2 upside down, verticalScroll as horizontalScroll
// transposed) are made by mirroring or transposing the definition: guides stay as they are, each path point, arc angle
// and the text rectangle are mapped (Mirrored).

using System.Globalization;
using System.Text;

namespace Filee.Engines.Slides;

internal static partial class PresetShapes
{
    private static void AddMore(Dictionary<string, GeometryDefinition> shapes)
    {
        foreach (var (name, definition) in ShapeGeometry.ParseNotation(MoreNotation()))
            shapes[name] = definition;

        shapes["curvedLeftArrow"] = Mirrored(shapes["curvedRightArrow"], Mirror.Horizontal);
        shapes["curvedDownArrow"] = Mirrored(shapes["curvedRightArrow"], Mirror.Transpose);
        shapes["curvedUpArrow"] = Mirrored(shapes["curvedDownArrow"], Mirror.Vertical);
        shapes["ribbon2"] = Mirrored(shapes["ribbon"], Mirror.Vertical);
        shapes["ellipseRibbon2"] = Mirrored(shapes["ellipseRibbon"], Mirror.Vertical);
        shapes["verticalScroll"] = Mirrored(shapes["horizontalScroll"], Mirror.Transpose);
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ───────────────────────── Mirroring ─────────────────────────

    internal enum Mirror
    {
        Horizontal,
        Vertical,
        Transpose,
    }

    private static readonly Dictionary<string, string> Transposed = BuildTransposed();

    private static Dictionary<string, string> BuildTransposed()
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["w"] = "h",
            ["l"] = "t",
            ["r"] = "b",
            ["hc"] = "vc",
        };
        foreach (var d in new[] { 2, 3, 4, 5, 6, 8, 10, 12, 32 })
            pairs[$"wd{d}"] = $"hd{d}";
        foreach (var (a, b) in pairs.ToList())
            pairs[b] = a;
        return pairs;
    }

    /// <summary>
    /// <paramref name="source"/> mirrored left to right, upside down, or transposed (x and y swapped, with w and h and
    /// the other built-ins swapped in its formulas, so it is the source shape drawn for the swapped box, turned over).
    /// </summary>
    internal static GeometryDefinition Mirrored(GeometryDefinition source, Mirror mirror)
    {
        var result = new GeometryDefinition();
        string Rename(string operand) => mirror == Mirror.Transpose && Transposed.TryGetValue(operand, out var other) ? other : operand;
        string Formula(string formula)
        {
            var parts = formula.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', parts.Select((part, i) => i == 0 ? part : Rename(part)));
        }
        result.Adjust.AddRange(source.Adjust.Select(a => (a.Name, Formula(a.Formula))));
        result.Guides.AddRange(source.Guides.Select(g => (g.Name, Formula(g.Formula))));
        string Guide(string formula)
        {
            // Named by position, so a mirrored definition mirrored again doesn't reuse the first mirroring's names.
            var name = $"mirror.{result.Guides.Count}";
            result.Guides.Add((name, formula));
            return name;
        }

        foreach (var path in source.Paths)
        {
            var width = path.Width > 0 ? N(path.Width) : "w";
            var height = path.Height > 0 ? N(path.Height) : "h";
            var commands = new List<GeometryCommand>();
            foreach (var command in path.Commands)
            {
                var a = command.Arguments.Select(Rename).ToList();
                if (command.Op == 'A' && a.Count >= 4)
                {
                    a = mirror switch
                    {
                        Mirror.Horizontal => [a[0], a[1], Guide($"+- cd2 0 {a[2]}"), Guide($"+- 0 0 {a[3]}")],
                        Mirror.Vertical => [a[0], a[1], Guide($"+- 0 0 {a[2]}"), Guide($"+- 0 0 {a[3]}")],
                        _ => [a[1], a[0], Guide($"+- cd4 0 {a[2]}"), Guide($"+- 0 0 {a[3]}")],
                    };
                }
                else if (command.Op != 'Z')
                {
                    for (var i = 0; i + 1 < a.Count; i += 2)
                    {
                        (a[i], a[i + 1]) = mirror switch
                        {
                            Mirror.Horizontal => (Guide($"+- {width} 0 {a[i]}"), a[i + 1]),
                            Mirror.Vertical => (a[i], Guide($"+- {height} 0 {a[i + 1]}")),
                            _ => (a[i + 1], a[i]),
                        };
                    }
                }
                commands.Add(new GeometryCommand(command.Op, a));
            }
            var (pathWidth, pathHeight) = mirror == Mirror.Transpose ? (path.Height, path.Width) : (path.Width, path.Height);
            result.Paths.Add(new GeometryPathDefinition(pathWidth, pathHeight, path.Fill, path.Stroke, commands));
        }

        if (source.TextRect is var (l, t, r, b))
        {
            result.TextRect = mirror switch
            {
                Mirror.Horizontal => (Guide($"+- w 0 {r}"), t, Guide($"+- w 0 {l}"), b),
                Mirror.Vertical => (l, Guide($"+- h 0 {b}"), r, Guide($"+- h 0 {t}")),
                _ => (Rename(t), Rename(l), Rename(b), Rename(r)),
            };
        }
        return result;
    }

    // ───────────────────────── Generators ─────────────────────────

    /// <summary>Guides {p}x / {p}y: the point at angle <paramref name="angle"/> on the ellipse rx × ry around the centre.</summary>
    private static string Point(string p, string rx, string ry, string angle) =>
        $"  gd {p}wt sin {rx} {angle}\n  gd {p}ht cos {ry} {angle}\n  gd {p}dx cat2 {rx} {p}ht {p}wt\n  gd {p}dy sat2 {ry} {p}ht {p}wt\n" +
        $"  gd {p}x +- hc {p}dx 0\n  gd {p}y +- vc {p}dy 0\n";

    /// <summary>
    /// Guides {p}l, {p}t, {p}r, {p}b: the box of the points and of an arc of the box's ellipse from <paramref name="start"/>
    /// through <paramref name="sweep"/> (0 to 360°, clockwise), which reaches the box's side wherever it passes 0°, 90°, 180°, 270°.
    /// </summary>
    private static string ArcBox(string p, string start, string sweep, string[] xs, string[] ys)
    {
        var text = new StringBuilder();
        string[] angles = ["0", "cd4", "cd2", "3cd4"];
        for (var k = 0; k < 4; k++)
        {
            text.Append($"  gd {p}d{k} +- {angles[k]} 0 {start}\n  gd {p}n{k} +- 0 0 {p}d{k}\n  gd {p}a{k} +- {p}d{k} 21600000 0\n");
            text.Append($"  gd {p}e{k} ?: {p}n{k} {p}a{k} {p}d{k}\n  gd {p}c{k} +- {sweep} 0 {p}e{k}\n");
        }
        void Fold(string op, string name, string[] values)
        {
            text.Append($"  gd {name} val {values[0]}\n");
            foreach (var value in values.Skip(1))
                text.Append($"  gd {name} {op} {name} {value}\n");
        }
        Fold("max", $"{p}mx", xs);
        Fold("min", $"{p}nx", xs);
        Fold("max", $"{p}my", ys);
        Fold("min", $"{p}ny", ys);
        text.Append($"  gd {p}r ?: {p}c0 r {p}mx\n  gd {p}b ?: {p}c1 b {p}my\n  gd {p}l ?: {p}c2 l {p}nx\n  gd {p}t ?: {p}c3 t {p}ny\n");
        return text.ToString();
    }

    /// <summary>The callouts with a leader of 1 to 3 segments: plain, with an accent bar, with a border, or both.</summary>
    private static string Callouts()
    {
        int[][] defaults =
        [
            [18750, -8333, 112500, -38333],
            [18750, -8333, 18750, -16667, 112500, -46667],
            [18750, -8333, 18750, -16667, 100000, -16667, 112963, -8333],
        ];
        var text = new StringBuilder();
        foreach (var (family, accent, border) in new[] { ("callout", false, false), ("accentCallout", true, false), ("borderCallout", false, true), ("accentBorderCallout", true, true) })
        {
            for (var legs = 1; legs <= 3; legs++)
            {
                text.Append($"{family}{legs}\n");
                var values = defaults[legs - 1];
                for (var i = 0; i < values.Length; i++)
                    text.Append($"  av adj{i + 1} {values[i]}\n");
                var leader = new StringBuilder();
                for (var i = 0; i < values.Length / 2; i++)
                {
                    text.Append($"  gd y{i + 1} */ h adj{2 * i + 1} 100000\n  gd x{i + 1} */ w adj{2 * i + 2} 100000\n");
                    leader.Append(i == 0 ? $"M x{i + 1} y{i + 1}" : $" L x{i + 1} y{i + 1}");
                }
                text.Append(border ? "  path: M l t L r t L r b L l b Z\n" : "  path stroke=0: M l t L r t L r b L l b Z\n");
                if (accent)
                    text.Append("  path fill=none: M x1 t L x1 b\n");
                text.Append($"  path fill=none: {leader}\n  text l t r b\n\n");
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// A gear of <paramref name="teeth"/> teeth, the first centred at <paramref name="first"/> (60,000ths of a degree):
    /// adj1 is the teeth's height, adj2 the width of their flat tops, both of the shorter side; the body is the ellipse
    /// inside the teeth, the teeth trapezoids whose sides lean in by half the height.
    /// </summary>
    private static string Gear(int teeth, int first, int adjust1, int adjust2, int maxAdjust2)
    {
        var step = 21600000 / teeth;
        var text = new StringBuilder($"gear{teeth}\n  av adj1 {adjust1}\n  av adj2 {adjust2}\n");
        text.Append($"  gd a1 pin 0 adj1 20000\n  gd a2 pin 0 adj2 {maxAdjust2}\n  gd th */ ss a1 100000\n  gd lFD */ ss a2 100000\n");
        text.Append("  gd th2 */ th 1 2\n  gd l2 */ lFD 1 2\n  gd l3 +- th2 l2 0\n  gd rh +- hd2 0 th\n  gd rw +- wd2 0 th\n");
        text.Append("  gd dr +- rw 0 rh\n  gd maxr ?: dr rh rw\n  gd ha at2 maxr l3\n  gd mo +- maxr th 0\n  gd hb at2 mo l2\n");
        text.Append($"  gd sw0 +- {step} 0 ha\n  gd sw1 +- sw0 0 ha\n  gd sw max sw1 0\n");
        var path = new StringBuilder();
        for (var k = 0; k < teeth; k++)
        {
            var centre = (first + (long)k * step) % 21600000;
            text.Append($"  gd c{k} val {centre}\n  gd aA{k} +- c{k} 0 ha\n  gd aB{k} +- c{k} 0 hb\n  gd aC{k} +- c{k} hb 0\n  gd aD{k} +- c{k} ha 0\n");
            text.Append(Point($"A{k}", "rw", "rh", $"aA{k}")).Append(Point($"B{k}", "wd2", "hd2", $"aB{k}"));
            text.Append(Point($"C{k}", "wd2", "hd2", $"aC{k}")).Append(Point($"D{k}", "rw", "rh", $"aD{k}"));
            path.Append(k == 0 ? "M A0x A0y" : $" L A{k}x A{k}y");
            path.Append($" L B{k}x B{k}y L C{k}x C{k}y L D{k}x D{k}y A rw rh aD{k} sw");
        }
        text.Append("  gd idx cos rw 2700000\n  gd idy sin rh 2700000\n  gd il +- hc 0 idx\n  gd ir +- hc idx 0\n  gd it +- vc 0 idy\n  gd ib +- vc idy 0\n");
        text.Append($"  path: {path} Z\n  text il it ir ib\n\n");
        return text.ToString();
    }

    /// <summary>
    /// The circular arrows: a band on the ellipse inside the box from stAng (adj4) to enAng (adj3), adj1 thick, its
    /// arrowhead adj5 wider on each side than the band's centre line and adj2 long (an angle); clockwise with the head at
    /// the end (circularArrow), counter-clockwise (leftCircularArrow, adj2 negative), or with a head at each end.
    /// </summary>
    private static string CircularArrow(string name, int[] defaults, bool clockwise, bool bothEnds)
    {
        var text = new StringBuilder($"{name}\n");
        for (var i = 0; i < defaults.Length; i++)
            text.Append($"  av adj{i + 1} {defaults[i]}\n");
        text.Append("  gd a5 pin 0 adj5 25000\n  gd maxAdj1 */ a5 2 1\n  gd a1 pin 0 adj1 maxAdj1\n  gd enAng pin 1 adj3 21599999\n  gd stAng pin 0 adj4 21599999\n");
        text.Append("  gd th */ ss a1 100000\n  gd thh */ ss a5 100000\n  gd th2 */ th 1 2\n  gd rw1 +- wd2 th2 thh\n  gd rh1 +- hd2 th2 thh\n");
        text.Append("  gd rw2 +- rw1 0 th\n  gd rh2 +- rh1 0 th\n  gd rw3 +- rw2 th2 0\n  gd rh3 +- rh2 th2 0\n");
        text.Append("  gd rw4a +- rw3 0 thh\n  gd rh4a +- rh3 0 thh\n  gd rw4 max rw4a 0\n  gd rh4 max rh4a 0\n");
        text.Append("  gd sw1 +- enAng 0 stAng\n");
        if (clockwise)
            text.Append("  gd sw2 +- sw1 21600000 0\n  gd swAng ?: sw1 sw1 sw2\n  gd room +- 21600000 0 swAng\n");
        else
            text.Append("  gd sw2 +- sw1 0 21600000\n  gd swAng ?: sw1 sw2 sw1\n  gd room +- 21600000 swAng 0\n");
        text.Append(bothEnds ? "  gd room2 */ room 1 2\n  gd maxAng min room2 cd2\n" : "  gd maxAng min room cd2\n");
        text.Append(clockwise ? "  gd aAng pin 0 adj2 maxAng\n" : "  gd nMaxAng +- 0 0 maxAng\n  gd aAng pin nMaxAng adj2 0\n");
        text.Append("  gd iswAng +- 0 0 swAng\n  gd ptAng +- enAng aAng 0\n");
        text.Append(Point("E", "rw1", "rh1", "stAng")).Append(Point("G", "wd2", "hd2", "enAng")).Append(Point("A", "rw3", "rh3", "ptAng"));
        text.Append(Point("B", "rw4", "rh4", "enAng")).Append(Point("C", "rw2", "rh2", "enAng"));
        if (bothEnds)
        {
            text.Append("  gd sptAng +- stAng 0 aAng\n").Append(Point("I", "rw2", "rh2", "stAng")).Append(Point("SB", "rw4", "rh4", "stAng"));
            text.Append(Point("SA", "rw3", "rh3", "sptAng")).Append(Point("SG", "wd2", "hd2", "stAng"));
            text.Append("  path: M Ix Iy L SBx SBy L SAx SAy L SGx SGy L Ex Ey A rw1 rh1 stAng swAng L Gx Gy L Ax Ay L Bx By L Cx Cy A rw2 rh2 enAng iswAng Z\n");
        }
        else
            text.Append("  path: M Ex Ey A rw1 rh1 stAng swAng L Gx Gy L Ax Ay L Bx By L Cx Cy A rw2 rh2 enAng iswAng Z\n");
        text.Append("  text l t r b\n\n");
        return text.ToString();
    }

    private const string ButtonFrame = "  gd dx2 */ ss 3 8\n  gd g9 +- vc 0 dx2\n  gd g10 +- vc dx2 0\n  gd g11 +- hc 0 dx2\n  gd g12 +- hc dx2 0\n  gd g13 */ ss 3 4\n";

    /// <summary>
    /// An action button: the frame, then its icon in the middle 3/4 of the shorter side (g9 … g10 down, g11 … g12
    /// across, g13 wide) with darker and lighter parts and the icon's outline.
    /// </summary>
    private static string Button(string name, string guides, string icon, string outline) =>
        $"{name}\n{ButtonFrame}{guides}  path stroke=0: M l t L r t L r b L l b Z\n{icon}" +
        $"  path fill=none: {outline}\n  path fill=none: M l t L r t L r b L l b Z\n  text l t r b\n\n";

    /// <summary>Guides {prefix}{i} = g13 · n / d for each fraction n/d of the icon's size.</summary>
    private static string Fractions(string prefix, params (int Name, int Numerator, int Denominator)[] fractions) =>
        string.Concat(fractions.Select(f => $"  gd {prefix}{f.Name} */ g13 {f.Numerator} {f.Denominator}\n"));

    private static string ActionButtons()
    {
        var text = new StringBuilder();
        text.Append("actionButtonBlank\n  path: M l t L r t L r b L l b Z\n  text l t r b\n\n");

        text.Append(Button("actionButtonHome",
            Fractions("g", (14, 1, 16), (15, 1, 8), (16, 3, 16), (17, 5, 16), (18, 7, 16), (19, 9, 16), (20, 11, 16), (21, 3, 4), (22, 13, 16), (23, 7, 8)) +
            "  gd g24 +- g9 g14 0\n  gd g25 +- g9 g16 0\n  gd g26 +- g9 g17 0\n  gd g27 +- g9 g21 0\n  gd g28 +- g11 g15 0\n" +
            "  gd g29 +- g11 g18 0\n  gd g30 +- g11 g19 0\n  gd g31 +- g11 g20 0\n  gd g32 +- g11 g22 0\n  gd g33 +- g11 g23 0\n",
            "  path stroke=0 fill=darkenLess: M hc g9 L g11 vc L g12 vc Z M g31 g24 L g32 g24 L g32 g26 L g31 g25 Z\n" +
            "  path stroke=0 fill=darken: M g28 vc L g28 g10 L g29 g10 L g29 g27 L g30 g27 L g30 g10 L g33 g10 L g33 vc Z\n",
            "M hc g9 L g31 g25 L g31 g24 L g32 g24 L g32 g26 L g12 vc L g33 vc L g33 g10 L g28 g10 L g28 vc L g11 vc Z " +
            "M g31 g25 L g32 g26 M g33 vc L g28 vc M g29 g10 L g29 g27 L g30 g27 L g30 g10"));

        const string HelpMark = "M g33 g27 A g16 g16 cd2 cd2 A g14 g15 0 cd4 A g41 g42 3cd4 -5400000 L g37 g30 L g36 g30 L g36 g29 " +
                                "A g14 g15 cd2 cd4 A g41 g42 cd4 -5400000 A g14 g14 0 -10800000 Z M hc g31 A g42 g42 3cd4 21600000 Z";
        text.Append(Button("actionButtonHelp",
            Fractions("g", (14, 1, 7), (15, 3, 14), (16, 2, 7), (19, 3, 7), (20, 4, 7), (21, 17, 28), (23, 21, 28), (24, 11, 14), (41, 1, 14), (42, 3, 28)) +
            "  gd g27 +- g9 g16 0\n  gd g29 +- g9 g21 0\n  gd g30 +- g9 g23 0\n  gd g31 +- g9 g24 0\n  gd g33 +- g11 g15 0\n  gd g36 +- g11 g19 0\n  gd g37 +- g11 g20 0\n",
            $"  path stroke=0 fill=darken: {HelpMark}\n", HelpMark));

        const string Info = "M g32 g28 L g35 g28 L g35 g30 L g37 g30 L g37 g31 L g32 g31 L g32 g30 L g34 g30 L g34 g29 L g32 g29 Z M hc g25 A g38 g38 3cd4 21600000 Z";
        text.Append(Button("actionButtonInformation",
            Fractions("g", (14, 1, 32), (17, 5, 16), (18, 3, 8), (19, 13, 32), (20, 19, 32), (22, 11, 16), (23, 13, 16), (24, 7, 8), (38, 3, 32)) +
            "  gd g25 +- g9 g14 0\n  gd g28 +- g9 g17 0\n  gd g29 +- g9 g18 0\n  gd g30 +- g9 g23 0\n  gd g31 +- g9 g24 0\n" +
            "  gd g32 +- g11 g17 0\n  gd g34 +- g11 g19 0\n  gd g35 +- g11 g20 0\n  gd g37 +- g11 g22 0\n",
            $"  path stroke=0 fill=darken: M hc g9 A dx2 dx2 3cd4 21600000 Z\n  path stroke=0 fill=lighten: {Info}\n",
            $"M hc g9 A dx2 dx2 3cd4 21600000 Z {Info}"));

        text.Append(Button("actionButtonForwardNext", "", "  path stroke=0 fill=darken: M g12 vc L g11 g9 L g11 g10 Z\n", "M g12 vc L g11 g9 L g11 g10 Z"));
        text.Append(Button("actionButtonBackPrevious", "", "  path stroke=0 fill=darken: M g11 vc L g12 g9 L g12 g10 Z\n", "M g11 vc L g12 g9 L g12 g10 Z"));

        const string End = "M g16 vc L g11 g9 L g11 g10 Z M g17 g9 L g12 g9 L g12 g10 L g17 g10 Z";
        text.Append(Button("actionButtonEnd", Fractions("g", (14, 3, 4), (15, 7, 8)) + "  gd g16 +- g11 g14 0\n  gd g17 +- g11 g15 0\n",
            $"  path stroke=0 fill=darken: {End}\n", End));
        const string Beginning = "M g17 vc L g12 g9 L g12 g10 Z M g16 g9 L g11 g9 L g11 g10 L g16 g10 Z";
        text.Append(Button("actionButtonBeginning", Fractions("g", (14, 1, 8), (15, 1, 4)) + "  gd g16 +- g11 g14 0\n  gd g17 +- g11 g15 0\n",
            $"  path stroke=0 fill=darken: {Beginning}\n", Beginning));

        const string Return = "M g12 g21 L g23 g9 L hc g21 L g24 g21 L g24 g20 A g27 g27 0 cd4 L g25 g19 A g27 g27 cd4 cd4 L g26 g21 L g11 g21 " +
                              "L g11 g20 A g17 g17 cd2 -5400000 L hc g10 A g17 g17 cd4 -5400000 L g22 g21 Z";
        text.Append(Button("actionButtonReturn",
            Fractions("g", (14, 7, 8), (15, 3, 4), (16, 5, 8), (17, 3, 8), (18, 1, 4), (27, 1, 8)) +
            "  gd g19 +- g9 g15 0\n  gd g20 +- g9 g16 0\n  gd g21 +- g9 g18 0\n  gd g22 +- g11 g14 0\n  gd g23 +- g11 g15 0\n  gd g24 +- g11 g16 0\n" +
            "  gd g25 +- g11 g17 0\n  gd g26 +- g11 g18 0\n",
            $"  path stroke=0 fill=darken: {Return}\n", Return));

        text.Append(Button("actionButtonDocument",
            "  gd dx1 */ ss 9 32\n  gd d11 +- hc 0 dx1\n  gd d12 +- hc dx1 0\n  gd d13 */ ss 3 16\n  gd d14 +- d12 0 d13\n  gd d15 +- g9 d13 0\n",
            "  path stroke=0 fill=darkenLess: M d11 g9 L d14 g9 L d12 d15 L d12 g10 L d11 g10 Z\n  path stroke=0 fill=darken: M d14 g9 L d14 d15 L d12 d15 Z\n",
            "M d11 g9 L d14 g9 L d12 d15 L d12 g10 L d11 g10 Z M d12 d15 L d14 d15 L d14 g9"));

        text.Append(Button("actionButtonSound",
            Fractions("g", (14, 1, 8), (15, 5, 16), (16, 5, 8), (17, 11, 16), (18, 3, 4), (19, 7, 8)) +
            "  gd g20 +- g9 g14 0\n  gd g21 +- g9 g15 0\n  gd g22 +- g9 g17 0\n  gd g23 +- g9 g19 0\n  gd g24 +- g11 g15 0\n  gd g25 +- g11 g16 0\n  gd g26 +- g11 g18 0\n",
            "  path stroke=0 fill=darken: M g11 g21 L g11 g22 L g24 g22 L g25 g10 L g25 g9 L g24 g21 Z\n",
            "M g11 g21 L g11 g22 L g24 g22 L g25 g10 L g25 g9 L g24 g21 Z M g26 g21 L g12 g20 M g26 vc L g12 vc M g26 g22 L g12 g23"));

        const string Movie = "M g11 g39 L g11 g44 L g31 g44 L g32 g43 L g33 g43 L g33 g47 L g35 g47 L g35 g45 L g36 g45 L g38 g46 L g12 g46 " +
                             "L g12 g41 L g38 g41 L g37 g42 L g35 g42 L g35 g41 L g34 g40 L g32 g40 L g31 g39 Z";
        var movie = new StringBuilder();
        foreach (var (index, value) in new[] { (14, 1455), (15, 1905), (16, 2325), (17, 16155), (18, 17010), (19, 19335), (20, 19725), (21, 20595) })
            movie.Append($"  gd g{index} */ g13 {value} 21600\n  gd g{index + 17} +- g11 g{index} 0\n");
        foreach (var (index, value) in new[] { (22, 5280), (23, 5730), (24, 6630), (25, 7492), (26, 9067), (27, 9555), (28, 13342), (29, 14580), (30, 15592) })
            movie.Append($"  gd g{index} */ g13 {value} 21600\n  gd g{index + 17} +- g9 g{index} 0\n");
        text.Append(Button("actionButtonMovie", movie.ToString(), $"  path stroke=0 fill=darken: {Movie}\n", Movie));
        return text.ToString();
    }

    /// <summary>The definitions written out in the notation, generated families included.</summary>
    private static string MoreNotation() =>
        Callouts() + ActionButtons() +
        Gear(6, 19800000, 15000, 3526, 5359) + Gear(9, 18600000, 10000, 1763, 2679) +
        CircularArrow("circularArrow", [12500, 1142319, 20457681, 10800000, 12500], clockwise: true, bothEnds: false) +
        CircularArrow("leftCircularArrow", [12500, -1142319, 1142319, 10800000, 12500], clockwise: false, bothEnds: false) +
        CircularArrow("leftRightCircularArrow", [12500, 1142319, 20457681, 11942319, 12500], clockwise: true, bothEnds: true) +
        $"""
lineInv
  path fill=none: M l b L r t
  text l t r b

nonIsoscelesTrapezoid
  av adj1 25000
  av adj2 25000
  gd maxAdj */ 50000 w ss
  gd a1 pin 0 adj1 maxAdj
  gd a2 pin 0 adj2 maxAdj
  gd x1 */ ss a1 200000
  gd x2 */ ss a1 100000
  gd dx3 */ ss a2 100000
  gd x3 +- r 0 dx3
  gd il */ wd3 a1 maxAdj
  gd adjm max a1 a2
  gd it */ hd3 adjm maxAdj
  gd irt */ wd3 a2 maxAdj
  gd ir +- r 0 irt
  path: M l b L x2 t L x3 t L r b Z
  text il it ir b

pieWedge
  gd g1 cos w 13500000
  gd g2 sin h 13500000
  gd x1 +- r g1 0
  gd y1 +- b g2 0
  path: M l b A w h cd2 cd4 L r b Z
  text x1 y1 r b

blockArc
  av adj1 10800000
  av adj2 0
  av adj3 25000
  gd stAng pin 0 adj1 21599999
  gd istAng pin 0 adj2 21599999
  gd a3 pin 0 adj3 50000
  gd sw11 +- istAng 0 stAng
  gd sw12 +- sw11 21600000 0
  gd swAng ?: sw11 sw11 sw12
  gd iswAng +- 0 0 swAng
  gd dr */ ss a3 100000
  gd iwd2 +- wd2 0 dr
  gd ihd2 +- hd2 0 dr
{Point("o1", "wd2", "hd2", "stAng")}{Point("o2", "wd2", "hd2", "istAng")}{Point("i1", "iwd2", "ihd2", "stAng")}{Point("i2", "iwd2", "ihd2", "istAng")}{ArcBox("box", "stAng", "swAng", ["o1x", "o2x", "i1x", "i2x"], ["o1y", "o2y", "i1y", "i2y"])}  path: M o1x o1y A wd2 hd2 stAng swAng L i2x i2y A iwd2 ihd2 istAng iswAng Z
  text boxl boxt boxr boxb

noSmoking
  av adj 18750
  gd a pin 0 adj 50000
  gd dr */ ss a 100000
  gd iwd2 +- wd2 0 dr
  gd ihd2 +- hd2 0 dr
  gd ang at2 w h
  gd ct cos ihd2 ang
  gd st sin iwd2 ang
  gd m mod ct st 0
  gd n */ iwd2 ihd2 m
  gd drd2 */ dr 1 2
  gd dang at2 n drd2
  gd 2dang */ dang 2 1
  gd swAng +- -10800000 2dang 0
  gd stAng1 +- ang 0 dang
  gd stAng2 +- stAng1 0 cd2
  gd ct1 cos ihd2 stAng1
  gd st1 sin iwd2 stAng1
  gd m1 mod ct1 st1 0
  gd n1 */ iwd2 ihd2 m1
  gd dx1 cos n1 stAng1
  gd dy1 sin n1 stAng1
  gd x1 +- hc dx1 0
  gd y1 +- vc dy1 0
  gd x2 +- hc 0 dx1
  gd y2 +- vc 0 dy1
{EllipseText}  path: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z M x1 y1 A iwd2 ihd2 stAng1 swAng Z M x2 y2 A iwd2 ihd2 stAng2 swAng Z
  text il it ir ib

rightArrowCallout
  av adj1 25000
  av adj2 25000
  av adj3 25000
  av adj4 64977
  gd maxAdj2 */ 50000 h ss
  gd a2 pin 0 adj2 maxAdj2
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd maxAdj3 */ 100000 w ss
  gd a3 pin 0 adj3 maxAdj3
  gd q2 */ a3 ss w
  gd maxAdj4 +- 100000 0 q2
  gd a4 pin 0 adj4 maxAdj4
  gd dy1 */ ss a2 100000
  gd dy2 */ ss a1 200000
  gd y1 +- vc 0 dy1
  gd y2 +- vc 0 dy2
  gd y3 +- vc dy2 0
  gd y4 +- vc dy1 0
  gd dx3 */ ss a3 100000
  gd x3 +- r 0 dx3
  gd x2 */ w a4 100000
  path: M l t L x2 t L x2 y2 L x3 y2 L x3 y1 L r vc L x3 y4 L x3 y3 L x2 y3 L x2 b L l b Z
  text l t x2 b

leftArrowCallout
  av adj1 25000
  av adj2 25000
  av adj3 25000
  av adj4 64977
  gd maxAdj2 */ 50000 h ss
  gd a2 pin 0 adj2 maxAdj2
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd maxAdj3 */ 100000 w ss
  gd a3 pin 0 adj3 maxAdj3
  gd q2 */ a3 ss w
  gd maxAdj4 +- 100000 0 q2
  gd a4 pin 0 adj4 maxAdj4
  gd dy1 */ ss a2 100000
  gd dy2 */ ss a1 200000
  gd y1 +- vc 0 dy1
  gd y2 +- vc 0 dy2
  gd y3 +- vc dy2 0
  gd y4 +- vc dy1 0
  gd x1 */ ss a3 100000
  gd dx2 */ w a4 100000
  gd x2 +- r 0 dx2
  path: M l vc L x1 y1 L x1 y2 L x2 y2 L x2 t L r t L r b L x2 b L x2 y3 L x1 y3 L x1 y4 Z
  text x2 t r b

upArrowCallout
  av adj1 25000
  av adj2 25000
  av adj3 25000
  av adj4 64977
  gd maxAdj2 */ 50000 w ss
  gd a2 pin 0 adj2 maxAdj2
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd maxAdj3 */ 100000 h ss
  gd a3 pin 0 adj3 maxAdj3
  gd q2 */ a3 ss h
  gd maxAdj4 +- 100000 0 q2
  gd a4 pin 0 adj4 maxAdj4
  gd dx1 */ ss a2 100000
  gd dx2 */ ss a1 200000
  gd x1 +- hc 0 dx1
  gd x2 +- hc 0 dx2
  gd x3 +- hc dx2 0
  gd x4 +- hc dx1 0
  gd y1 */ ss a3 100000
  gd dy2 */ h a4 100000
  gd y2 +- b 0 dy2
  path: M l y2 L x2 y2 L x2 y1 L x1 y1 L hc t L x4 y1 L x3 y1 L x3 y2 L r y2 L r b L l b Z
  text l y2 r b

downArrowCallout
  av adj1 25000
  av adj2 25000
  av adj3 25000
  av adj4 64977
  gd maxAdj2 */ 50000 w ss
  gd a2 pin 0 adj2 maxAdj2
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd maxAdj3 */ 100000 h ss
  gd a3 pin 0 adj3 maxAdj3
  gd q2 */ a3 ss h
  gd maxAdj4 +- 100000 0 q2
  gd a4 pin 0 adj4 maxAdj4
  gd dx1 */ ss a2 100000
  gd dx2 */ ss a1 200000
  gd x1 +- hc 0 dx1
  gd x2 +- hc 0 dx2
  gd x3 +- hc dx2 0
  gd x4 +- hc dx1 0
  gd dy3 */ ss a3 100000
  gd y3 +- b 0 dy3
  gd y2 */ h a4 100000
  path: M l t L r t L r y2 L x3 y2 L x3 y3 L x4 y3 L hc b L x1 y3 L x2 y3 L x2 y2 L l y2 Z
  text l t r y2

leftRightArrowCallout
  av adj1 25000
  av adj2 25000
  av adj3 25000
  av adj4 48123
  gd maxAdj2 */ 50000 h ss
  gd a2 pin 0 adj2 maxAdj2
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd maxAdj3 */ 50000 w ss
  gd a3 pin 0 adj3 maxAdj3
  gd q2 */ a3 ss wd2
  gd maxAdj4 +- 100000 0 q2
  gd a4 pin 0 adj4 maxAdj4
  gd dy1 */ ss a2 100000
  gd dy2 */ ss a1 200000
  gd y1 +- vc 0 dy1
  gd y2 +- vc 0 dy2
  gd y3 +- vc dy2 0
  gd y4 +- vc dy1 0
  gd x1 */ ss a3 100000
  gd x4 +- r 0 x1
  gd dx2 */ w a4 200000
  gd x2 +- hc 0 dx2
  gd x3 +- hc dx2 0
  path: M l vc L x1 y1 L x1 y2 L x2 y2 L x2 t L x3 t L x3 y2 L x4 y2 L x4 y1 L r vc L x4 y4 L x4 y3 L x3 y3 L x3 b L x2 b L x2 y3 L x1 y3 L x1 y4 Z
  text x2 t x3 b

upDownArrowCallout
  av adj1 25000
  av adj2 25000
  av adj3 25000
  av adj4 48123
  gd maxAdj2 */ 50000 w ss
  gd a2 pin 0 adj2 maxAdj2
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd maxAdj3 */ 50000 h ss
  gd a3 pin 0 adj3 maxAdj3
  gd q2 */ a3 ss hd2
  gd maxAdj4 +- 100000 0 q2
  gd a4 pin 0 adj4 maxAdj4
  gd dx1 */ ss a2 100000
  gd dx2 */ ss a1 200000
  gd x1 +- hc 0 dx1
  gd x2 +- hc 0 dx2
  gd x3 +- hc dx2 0
  gd x4 +- hc dx1 0
  gd y1 */ ss a3 100000
  gd y4 +- b 0 y1
  gd dy2 */ h a4 200000
  gd y2 +- vc 0 dy2
  gd y3 +- vc dy2 0
  path: M l y2 L x2 y2 L x2 y1 L x1 y1 L hc t L x4 y1 L x3 y1 L x3 y2 L r y2 L r y3 L x3 y3 L x3 y4 L x4 y4 L hc b L x1 y4 L x2 y4 L x2 y3 L l y3 Z
  text l y2 r y3

quadArrowCallout
  av adj1 18515
  av adj2 18515
  av adj3 18515
  av adj4 48123
  gd a2 pin 0 adj2 50000
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd maxAdj3 +- 50000 0 a2
  gd a3 pin 0 adj3 maxAdj3
  gd q2 */ a3 2 1
  gd maxAdj4 +- 100000 0 q2
  gd a4 pin a1 adj4 maxAdj4
  gd dx2 */ ss a2 100000
  gd dx3 */ ss a1 200000
  gd ah */ ss a3 100000
  gd dx1 */ w a4 200000
  gd dy1 */ h a4 200000
  gd x8 +- r 0 ah
  gd x2 +- hc 0 dx1
  gd x7 +- hc dx1 0
  gd x3 +- hc 0 dx2
  gd x6 +- hc dx2 0
  gd x4 +- hc 0 dx3
  gd x5 +- hc dx3 0
  gd y8 +- b 0 ah
  gd y2 +- vc 0 dy1
  gd y7 +- vc dy1 0
  gd y3 +- vc 0 dx2
  gd y6 +- vc dx2 0
  gd y4 +- vc 0 dx3
  gd y5 +- vc dx3 0
  path: M l vc L ah y3 L ah y4 L x2 y4 L x2 y2 L x4 y2 L x4 ah L x3 ah L hc t L x6 ah L x5 ah L x5 y2 L x7 y2 L x7 y4 L x8 y4 L x8 y3 L r vc L x8 y6 L x8 y5 L x7 y5 L x7 y7 L x5 y7 L x5 y8 L x6 y8 L hc b L x3 y8 L x4 y8 L x4 y7 L x2 y7 L x2 y5 L ah y5 L ah y6 Z
  text x2 y2 x7 y7

bentArrow
  av adj1 25000
  av adj2 25000
  av adj3 25000
  av adj4 43750
  gd a2 pin 0 adj2 50000
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd a3 pin 0 adj3 50000
  gd th */ ss a1 100000
  gd aw2 */ ss a2 100000
  gd th2 */ th 1 2
  gd dh2 +- aw2 0 th2
  gd ah */ ss a3 100000
  gd bw +- r 0 ah
  gd bh +- b 0 dh2
  gd bs min bw bh
  gd maxAdj4 */ 100000 bs ss
  gd a4 pin 0 adj4 maxAdj4
  gd bd */ ss a4 100000
  gd bd3 +- bd 0 th
  gd bd2 max bd3 0
  gd x3 +- th bd2 0
  gd x4 +- r 0 ah
  gd y3 +- dh2 th 0
  gd y4 +- y3 dh2 0
  gd y5 +- dh2 bd 0
  path: M l b L l y5 A bd bd cd2 cd4 L x4 dh2 L x4 t L r aw2 L x4 y4 L x4 y3 L x3 y3 A bd2 bd2 3cd4 -5400000 L th b Z
  text l t r b

bentUpArrow
  av adj1 25000
  av adj2 25000
  av adj3 25000
  gd a1 pin 0 adj1 50000
  gd a2 pin 0 adj2 50000
  gd a3 pin 0 adj3 50000
  gd y1 */ ss a3 100000
  gd dx1 */ ss a2 50000
  gd x1 +- r 0 dx1
  gd dx3 */ ss a2 100000
  gd x3 +- r 0 dx3
  gd dx2 */ ss a1 200000
  gd x2 +- x3 0 dx2
  gd x4 +- x3 dx2 0
  gd dy2 */ ss a1 100000
  gd y2 +- b 0 dy2
  path: M l y2 L x2 y2 L x2 y1 L x1 y1 L x3 t L r y1 L x4 y1 L x4 b L l b Z
  text l y2 x4 b

leftUpArrow
  av adj1 25000
  av adj2 25000
  av adj3 25000
  gd a2 pin 0 adj2 50000
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd maxAdj3 +- 100000 0 maxAdj1
  gd a3 pin 0 adj3 maxAdj3
  gd x1 */ ss a3 100000
  gd dx2 */ ss a2 50000
  gd x2 +- r 0 dx2
  gd y2 +- b 0 dx2
  gd dx4 */ ss a2 100000
  gd x4 +- r 0 dx4
  gd y4 +- b 0 dx4
  gd dx3 */ ss a1 200000
  gd x3 +- x4 0 dx3
  gd x5 +- x4 dx3 0
  gd y3 +- y4 0 dx3
  gd y5 +- y4 dx3 0
  gd il */ dx3 x1 dx4
  path: M l y4 L x1 y2 L x1 y3 L x3 y3 L x3 x1 L x2 x1 L x4 t L r x1 L x5 x1 L x5 y5 L x1 y5 L x1 b Z
  text il y3 x4 y5

leftRightUpArrow
  av adj1 25000
  av adj2 25000
  av adj3 25000
  gd a2 pin 0 adj2 50000
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd q1 +- 100000 0 maxAdj1
  gd maxAdj3 */ q1 1 2
  gd a3 pin 0 adj3 maxAdj3
  gd x1 */ ss a3 100000
  gd dx2 */ ss a2 100000
  gd x2 +- hc 0 dx2
  gd x5 +- hc dx2 0
  gd dx3 */ ss a1 200000
  gd x3 +- hc 0 dx3
  gd x4 +- hc dx3 0
  gd x6 +- r 0 x1
  gd dy2 */ ss a2 50000
  gd y2 +- b 0 dy2
  gd y4 +- b 0 dx2
  gd y3 +- y4 0 dx3
  gd y5 +- y4 dx3 0
  gd il */ dx3 x1 dx2
  gd ir +- r 0 il
  path: M l y4 L x1 y2 L x1 y3 L x3 y3 L x3 x1 L x2 x1 L hc t L x5 x1 L x4 x1 L x4 y3 L x6 y3 L x6 y2 L r y4 L x6 b L x6 y5 L x1 y5 L x1 b Z
  text il y3 ir y5

uturnArrow
  av adj1 25000
  av adj2 25000
  av adj3 25000
  av adj4 43750
  av adj5 75000
  gd a2 pin 0 adj2 25000
  gd maxAdj1 */ a2 2 1
  gd a1 pin 0 adj1 maxAdj1
  gd q2 */ a1 ss h
  gd q3 +- 100000 0 q2
  gd maxAdj3 */ q3 h ss
  gd a3 pin 0 adj3 maxAdj3
  gd q1 +- a3 a1 0
  gd minAdj5 */ q1 ss h
  gd a5 pin minAdj5 adj5 100000
  gd th */ ss a1 100000
  gd aw2 */ ss a2 100000
  gd th2 */ th 1 2
  gd dh2 +- aw2 0 th2
  gd y5 */ h a5 100000
  gd ah */ ss a3 100000
  gd y4 +- y5 0 ah
  gd x9 +- r 0 dh2
  gd bw */ x9 1 2
  gd bs min bw y4
  gd maxAdj4 */ bs 100000 ss
  gd a4 pin 0 adj4 maxAdj4
  gd bd */ ss a4 100000
  gd bd3 +- bd 0 th
  gd bd2 max bd3 0
  gd x3 +- th bd2 0
  gd x8 +- r 0 aw2
  gd x6 +- x8 0 aw2
  gd x7 +- x6 dh2 0
  gd x4 +- x9 0 bd
  path: M l b L l bd A bd bd cd2 cd4 L x4 t A bd bd 3cd4 cd4 L x9 y4 L r y4 L x8 y5 L x6 y4 L x7 y4 L x7 x3 A bd2 bd2 0 -5400000 L x3 th A bd2 bd2 3cd4 -5400000 L th b Z
  text l t r b

swooshArrow
  av adj1 25000
  av adj2 16667
  gd a1 pin 1 adj1 75000
  gd maxAdj2 */ 70000 w ss
  gd a2 pin 0 adj2 maxAdj2
  gd ad1 */ h a1 100000
  gd ad2 */ ss a2 100000
  gd xB +- r 0 ad2
  gd yB +- t ssd8 0
  gd alfa */ cd4 1 14
  gd dx0 tan ssd8 alfa
  gd xC +- xB 0 dx0
  gd dx1 tan ad1 alfa
  gd yF +- yB ad1 0
  gd xF +- xB dx1 0
  gd xE +- xF dx0 0
  gd yE +- yF ssd8 0
  gd dy2 +- yE 0 t
  gd dy22 */ dy2 1 2
  gd dy3 */ h 1 20
  gd yD +- t dy22 dy3
  gd yP1 +- hd6 hd6 0
  gd dy5 */ hd6 1 2
  gd yP2 +- yF dy5 0
  path: M l b Q wd6 yP1 xB yB L xC t L r yD L xE yE L xF yF Q wd4 yP2 l b Z
  text l t r b

curvedRightArrow
  av adj1 25000
  av adj2 50000
  av adj3 25000
  gd maxAdj2 */ 50000 h ss
  gd a2 pin 0 adj2 maxAdj2
  gd a1 pin 0 adj1 a2
  gd th */ ss a1 100000
  gd aw */ ss a2 100000
  gd q1 +/ th aw 4
  gd hR +- hd2 0 q1
  gd q7 */ hR 2 1
  gd q8 */ q7 q7 1
  gd q9 */ th th 1
  gd q10 +- q8 0 q9
  gd q11 sqrt q10
  gd idx */ q11 w q7
  gd maxAdj3 */ 100000 idx ss
  gd a3 pin 0 adj3 maxAdj3
  gd ah */ ss a3 100000
  gd y3 +- hR th 0
  gd q2 */ w w 1
  gd q3 */ ah ah 1
  gd q4 +- q2 0 q3
  gd q5 sqrt q4
  gd dy */ q5 hR w
  gd y5 +- hR dy 0
  gd y7 +- y3 dy 0
  gd q6 +- aw 0 th
  gd dh */ q6 1 2
  gd y4 +- y5 0 dh
  gd y8 +- y7 dh 0
  gd aw2 */ aw 1 2
  gd y6 +- b 0 aw2
  gd x1 +- r 0 ah
  gd swAng at2 ah dy
  gd stAng +- cd2 0 swAng
  gd mswAng +- 0 0 swAng
  gd q12 */ th 1 2
  gd dang2 at2 idx q12
  gd swAng2 +- dang2 0 cd4
  gd stAng3 +- cd2 0 dang2
  path stroke=0: M l hR A w hR cd2 mswAng L x1 y4 L r y6 L x1 y8 L x1 y7 A w hR stAng swAng L l hR Z
  path stroke=0 fill=darkenLess: M l hR A w hR cd2 cd4 L r th A w hR 3cd4 swAng2 A w hR stAng3 dang2 Z
  path fill=none: M l hR A w hR cd2 mswAng L x1 y4 L r y6 L x1 y8 L x1 y7 A w hR stAng swAng L l hR A w hR cd2 cd4 L r th A w hR 3cd4 swAng2
  text l t r b

curvedConnector5
  av adj1 50000
  av adj2 50000
  av adj3 50000
  gd x3 */ w adj1 100000
  gd x6 */ w adj3 100000
  gd x1 +/ x3 x6 2
  gd x2 +/ l x3 2
  gd x4 +/ x3 x1 2
  gd x5 +/ x6 x1 2
  gd x7 +/ x6 r 2
  gd y4 */ h adj2 100000
  gd y1 +/ t y4 2
  gd y2 +/ t y1 2
  gd y3 +/ y1 y4 2
  gd y5 +/ b y4 2
  gd y6 +/ y5 y4 2
  gd y7 +/ y5 b 2
  path fill=none: M l t C x2 t x3 y2 x3 y1 C x3 y3 x4 y4 x1 y4 C x5 y4 x6 y6 x6 y5 C x6 y7 x7 b r b
  text l t r b

ribbon
  av adj1 16667
  av adj2 50000
  gd a1 pin 0 adj1 33333
  gd a2 pin 25000 adj2 75000
  gd x10 +- r 0 wd8
  gd dx2 */ w a2 200000
  gd x2 +- hc 0 dx2
  gd x9 +- hc dx2 0
  gd x3 +- x2 wd32 0
  gd x8 +- x9 0 wd32
  gd x5 +- x2 wd8 0
  gd x6 +- x9 0 wd8
  gd x4 +- x5 0 wd32
  gd x7 +- x6 wd32 0
  gd y1 */ h a1 200000
  gd y2 */ h a1 100000
  gd y4 +- b 0 y2
  gd y3 */ y4 1 2
  gd hR */ h a1 400000
  gd y5 +- b 0 hR
  gd y6 +- y2 0 hR
  path stroke=0: M l t L x4 t A wd32 hR 3cd4 cd2 L x3 y1 A wd32 hR 3cd4 -10800000 L x8 y2 A wd32 hR cd4 -10800000 L x7 y1 A wd32 hR cd4 cd2 L r t L x10 y3 L r y4 L x9 y4 L x9 y5 A wd32 hR 0 cd4 L x3 b A wd32 hR cd4 cd4 L x2 y4 L l y4 L wd8 y3 Z
  path stroke=0 fill=darkenLess: M x5 hR A wd32 hR 0 cd4 L x3 y1 A wd32 hR 3cd4 -10800000 L x5 y2 Z M x6 hR A wd32 hR cd2 -5400000 L x8 y1 A wd32 hR 3cd4 cd2 L x6 y2 Z
  path fill=none: M l t L x4 t A wd32 hR 3cd4 cd2 L x3 y1 A wd32 hR 3cd4 -10800000 L x8 y2 A wd32 hR cd4 -10800000 L x7 y1 A wd32 hR cd4 cd2 L r t L x10 y3 L r y4 L x9 y4 L x9 y5 A wd32 hR 0 cd4 L x3 b A wd32 hR cd4 cd4 L x2 y4 L l y4 L wd8 y3 Z M x5 hR L x5 y2 M x6 y2 L x6 hR M x2 y4 L x2 y6 M x9 y6 L x9 y4
  text x2 y2 x9 b

ellipseRibbon
  av adj1 25000
  av adj2 50000
  av adj3 12500
  gd a1 pin 0 adj1 100000
  gd a2 pin 25000 adj2 75000
  gd q10 +- 100000 0 a1
  gd q11 */ q10 1 2
  gd q12 +- a1 0 q11
  gd minAdj3 max 0 q12
  gd a3 pin minAdj3 adj3 a1
  gd dx2 */ w a2 200000
  gd x2 +- hc 0 dx2
  gd x3 +- x2 wd8 0
  gd x4 +- r 0 x3
  gd x5 +- r 0 x2
  gd x6 +- r 0 wd8
  gd dy1 */ h a3 100000
  gd f1 */ 4 dy1 w
  gd q1 */ x3 x3 w
  gd q2 +- x3 0 q1
  gd y1 */ f1 q2 1
  gd cx1 */ x3 1 2
  gd cy1 */ f1 cx1 1
  gd cx2 +- r 0 cx1
  gd bt */ h a1 100000
  gd dy3 +- bt 0 dy1
  gd q3 */ x2 x2 w
  gd q4 +- x2 0 q3
  gd q5 */ f1 q4 1
  gd y3 +- q5 dy3 0
  gd q6 +- dy1 dy3 y3
  gd q7 +- q6 dy1 0
  gd cy3 +- q7 dy3 0
  gd rh +- b 0 bt
  gd q8 */ dy1 14 16
  gd y2 +/ q8 rh 2
  gd y5 +- q5 rh 0
  gd y6 +- y3 rh 0
  gd cx4 */ x2 1 2
  gd q9 */ f1 cx4 1
  gd cy4 +- q9 rh 0
  gd cx5 +- r 0 cx4
  gd cy6 +- cy3 rh 0
  gd y7 +- y1 dy3 0
  gd cy7 +- bt bt y7
  path stroke=0: M l t Q cx1 cy1 x3 y1 L x2 y3 Q hc cy3 x5 y3 L x4 y1 Q cx2 cy1 r t L x6 y2 L r rh Q cx5 cy4 x5 y5 L x5 y6 Q hc cy6 x2 y6 L x2 y5 Q cx4 cy4 l rh L wd8 y2 Z
  path stroke=0 fill=darkenLess: M x3 y7 L x3 y1 L x2 y3 Q hc cy3 x5 y3 L x4 y1 L x4 y7 Q hc cy7 x3 y7 Z
  path fill=none: M l t Q cx1 cy1 x3 y1 L x2 y3 Q hc cy3 x5 y3 L x4 y1 Q cx2 cy1 r t L x6 y2 L r rh Q cx5 cy4 x5 y5 L x5 y6 Q hc cy6 x2 y6 L x2 y5 Q cx4 cy4 l rh L wd8 y2 Z M x2 y5 L x2 y3 M x5 y3 L x5 y5 M x3 y1 L x3 y7 M x4 y7 L x4 y1
  text x2 bt x5 y6

leftRightRibbon
  av adj1 50000
  av adj2 50000
  av adj3 16667
  gd a3 pin 0 adj3 33333
  gd maxAdj1 +- 100000 0 a3
  gd a1 pin 0 adj1 maxAdj1
  gd w1 +- wd2 0 wd32
  gd maxAdj2 */ 100000 w1 ss
  gd a2 pin 0 adj2 maxAdj2
  gd x1 */ ss a2 100000
  gd x4 +- r 0 x1
  gd dy1 */ h a1 200000
  gd dy2 */ h a3 -200000
  gd ly1 +- vc dy2 dy1
  gd ry4 +- vc dy1 dy2
  gd ly2 +- ly1 dy1 0
  gd ry3 +- b 0 ly2
  gd ly4 */ ly2 2 1
  gd ry1 +- b 0 ly4
  gd ly3 +- ly4 0 ly1
  gd ry2 +- b 0 ly3
  gd hR */ a3 ss 400000
  gd x2 +- hc 0 wd32
  gd x3 +- hc wd32 0
  gd y1 +- ly1 hR 0
  gd y2 +- ry2 0 hR
  path stroke=0: M l ly2 L x1 t L x1 ly1 L hc ly1 A wd32 hR 3cd4 cd2 A wd32 hR 3cd4 -10800000 L x4 ry2 L x4 ry1 L r ry3 L x4 b L x4 ry4 L hc ry4 A wd32 hR cd4 cd4 L x2 ly3 L x1 ly3 L x1 ly4 Z
  path stroke=0 fill=darkenLess: M x3 y1 A wd32 hR 0 cd4 A wd32 hR 3cd4 -10800000 L x3 ry2 Z
  path fill=none: M l ly2 L x1 t L x1 ly1 L hc ly1 A wd32 hR 3cd4 cd2 A wd32 hR 3cd4 -10800000 L x4 ry2 L x4 ry1 L r ry3 L x4 b L x4 ry4 L hc ry4 A wd32 hR cd4 cd4 L x2 ly3 L x1 ly3 L x1 ly4 Z M x3 y1 L x3 ry2 M x2 y2 L x2 ly3
  text x1 ly1 x4 ry4

horizontalScroll
  av adj 12500
  gd a pin 0 adj 25000
  gd ch */ ss a 100000
  gd ch2 */ ch 1 2
  gd ch4 */ ch 1 4
  gd y3 +- ch ch2 0
  gd y4 +- ch ch 0
  gd y6 +- b 0 ch
  gd y7 +- b 0 ch2
  gd y5 +- y6 0 ch2
  gd x3 +- r 0 ch
  gd x4 +- r 0 ch2
  path stroke=0: M l y3 A ch2 ch2 cd2 cd4 L x3 ch L x3 ch2 A ch2 ch2 cd2 cd2 L r y5 A ch2 ch2 0 cd4 L ch y6 L ch y7 A ch2 ch2 0 cd2 Z
  path stroke=0 fill=darkenLess: M x4 ch A ch2 ch2 cd4 -16200000 A ch4 ch4 cd2 -10800000 Z M ch2 y4 A ch2 ch2 cd4 -5400000 A ch4 ch4 0 -10800000 Z
  path fill=none: M l y3 A ch2 ch2 cd2 cd4 L x3 ch L x3 ch2 A ch2 ch2 cd2 cd2 L r y5 A ch2 ch2 0 cd4 L ch y6 L ch y7 A ch2 ch2 0 cd2 Z M x3 ch L x4 ch A ch2 ch2 cd4 -5400000 M x4 ch L x4 ch2 A ch4 ch4 0 cd2 M ch2 y4 L ch2 y3 A ch4 ch4 cd2 cd2 A ch2 ch2 0 cd2 M ch y3 L ch y6
  text ch ch x4 y6

cloud
{CloudText}  path w=43200 h=43200: {CloudOutline}
  path w=43200 h=43200 fill=none: {CloudArcs}
  text il it ir ib

cloudCallout
  av adj1 -20833
  av adj2 62500
  gd dxPos */ w adj1 100000
  gd dyPos */ h adj2 100000
  gd xPos +- hc dxPos 0
  gd yPos +- vc dyPos 0
  gd ht cat2 hd2 dxPos dyPos
  gd wt sat2 wd2 dxPos dyPos
  gd g2 cat2 wd2 ht wt
  gd g3 sat2 hd2 ht wt
  gd g4 +- hc g2 0
  gd g5 +- vc g3 0
  gd g6 +- g4 0 xPos
  gd g7 +- g5 0 yPos
  gd g8 mod g6 g7 0
  gd g9 */ ss 6600 21600
  gd g10 +- g8 0 g9
  gd g11 */ g10 1 3
  gd g12 */ ss 1800 21600
  gd g13 +- g11 g12 0
  gd g14 */ g13 g6 g8
  gd g15 */ g13 g7 g8
  gd g16 +- g14 xPos 0
  gd g17 +- g15 yPos 0
  gd g18 */ ss 4800 21600
  gd g19 */ g11 2 1
  gd g20 +- g18 g19 0
  gd g21 */ g20 g6 g8
  gd g22 */ g20 g7 g8
  gd g23 +- g21 xPos 0
  gd g24 +- g22 yPos 0
  gd g25 */ ss 1200 21600
  gd g26 */ ss 600 21600
  gd x23 +- xPos g26 0
  gd x24 +- g16 g25 0
  gd x25 +- g23 g12 0
{CloudText}  path w=43200 h=43200: {CloudOutline}
  path: M x23 yPos A g26 g26 0 21600000 Z M x24 g17 A g25 g25 0 21600000 Z M x25 g24 A g12 g12 0 21600000 Z
  path w=43200 h=43200 fill=none: {CloudArcs}
  text il it ir ib

sun
  av adj 25000
  gd a pin 12500 adj 46875
  gd g0 +- 50000 0 a
  gd g1 */ g0 30274 32768
  gd g2 */ g0 12540 32768
  gd g5 +- 50000 0 g1
  gd g6 +- 50000 0 g2
  gd g7 */ g0 23170 32768
  gd g8 +- 50000 g7 0
  gd g9 +- 50000 0 g7
  gd g10 */ g5 3 4
  gd g11 */ g6 3 4
  gd g12 +- g10 3662 0
  gd g13 +- g11 3662 0
  gd g14 +- g11 12500 0
  gd g15 +- 100000 0 g10
  gd g16 +- 100000 0 g12
  gd g17 +- 100000 0 g13
  gd g18 +- 100000 0 g14
  gd ox1 */ w 18436 21600
  gd oy1 */ h 3163 21600
  gd ox2 */ w 3163 21600
  gd oy2 */ h 18436 21600
  gd x8 */ w g8 100000
  gd x9 */ w g9 100000
  gd x10 */ w g10 100000
  gd x12 */ w g12 100000
  gd x13 */ w g13 100000
  gd x14 */ w g14 100000
  gd x15 */ w g15 100000
  gd x16 */ w g16 100000
  gd x17 */ w g17 100000
  gd x18 */ w g18 100000
  gd x19 */ w a 100000
  gd wR */ w g0 100000
  gd hR */ h g0 100000
  gd y8 */ h g8 100000
  gd y9 */ h g9 100000
  gd y10 */ h g10 100000
  gd y12 */ h g12 100000
  gd y13 */ h g13 100000
  gd y14 */ h g14 100000
  gd y15 */ h g15 100000
  gd y16 */ h g16 100000
  gd y17 */ h g17 100000
  gd y18 */ h g18 100000
  path: M r vc L x15 y18 L x15 y14 Z M ox1 oy1 L x16 y13 L x17 y12 Z M hc t L x18 y10 L x14 y10 Z M ox2 oy1 L x13 y12 L x12 y13 Z M l vc L x10 y14 L x10 y18 Z M ox2 oy2 L x12 y17 L x13 y16 Z M hc b L x14 y15 L x18 y15 Z M ox1 oy2 L x17 y16 L x16 y17 Z M x19 vc A wR hR cd2 21600000 Z
  text x9 y9 x8 y8

moon
  av adj 50000
  gd a pin 0 adj 87500
  gd g0 */ ss a 100000
  gd g0w */ g0 w ss
  gd g1 +- ss 0 g0
  gd g2 */ g0 g0 g1
  gd g3 */ ss ss g1
  gd g4 */ g3 2 1
  gd g5 +- g4 0 g2
  gd g7 */ g5 1 2
  gd g8 +- g7 0 g0
  gd cxw */ g7 w ss
  gd rxw */ g8 w ss
  gd ryh */ g8 hd2 ss
  gd dxb +- w 0 cxw
  gd mhd2 +- 0 0 hd2
  gd stIn at2 dxb mhd2
  gd stIn2 +- stIn 21600000 0
  gd enIn at2 dxb hd2
  gd swIn +- enIn 0 stIn2
  gd g12 */ g0 9598 32768
  gd g12w */ g12 w ss
  gd g13 +- ss 0 g12
  gd q1 */ ss ss 1
  gd q2 */ g13 g13 1
  gd q3 +- q1 0 q2
  gd q4 sqrt q3
  gd dy4 */ q4 hd2 ss
  gd g15h +- vc 0 dy4
  gd g16h +- vc dy4 0
  path: M r b A w hd2 cd4 cd2 A rxw ryh stIn2 swIn Z
  text g12w g15h g0w g16h

lightningBolt
  gd x4 */ w 8757 21600
  gd x9 */ w 13917 21600
  gd y4 */ h 6797 21600
  gd y10 */ h 14915 21600
  path w=21600 h=21600: M 8472 0 L 12860 6080 L 11050 6797 L 16577 12007 L 14767 12877 L 21600 21600 L 10012 14915 L 12222 13987 L 5022 9705 L 7602 8382 L 0 3890 Z
  text x4 y4 x9 y10

irregularSeal1
  gd il */ w 4627 21600
  gd it */ h 6320 21600
  gd ir */ w 16702 21600
  gd ib */ h 13937 21600
  path w=21600 h=21600: M 10800 5800 L 14522 0 L 14155 5325 L 18380 4457 L 16702 7315 L 21097 8137 L 17607 10475 L 21600 13290 L 16837 12942 L 18145 18095 L 14020 14457 L 13247 19737 L 10532 14935 L 8485 21600 L 7715 15627 L 4762 17617 L 5667 13937 L 135 14587 L 3722 11775 L 0 8615 L 4627 7617 L 370 2295 L 7312 6320 L 8352 2295 Z
  text il it ir ib

irregularSeal2
  gd il */ w 5372 21600
  gd it */ h 6382 21600
  gd ir */ w 14640 21600
  gd ib */ h 15370 21600
  path w=21600 h=21600: M 11462 4342 L 14790 0 L 14525 5777 L 18007 3172 L 16380 6532 L 21600 6645 L 16985 9402 L 18270 11290 L 16380 12310 L 18877 15632 L 14640 14350 L 14942 17370 L 12180 15935 L 11612 18842 L 9872 17370 L 8700 19712 L 7527 18125 L 4917 21600 L 4805 18240 L 1285 17825 L 3330 15370 L 0 12877 L 3935 11592 L 1172 8270 L 5372 7817 L 4502 3625 L 8550 6382 L 9722 1887 Z
  text il it ir ib

doubleWave
  av adj1 6250
  av adj2 0
  gd a1 pin 0 adj1 12500
  gd a2 pin -10000 adj2 10000
  gd y1 */ h a1 100000
  gd dy2 */ y1 10 3
  gd y2 +- y1 0 dy2
  gd y3 +- y1 dy2 0
  gd y4 +- b 0 y1
  gd y5 +- y4 0 dy2
  gd y6 +- y4 dy2 0
  gd of2 */ w a2 50000
  gd dx2 ?: of2 0 of2
  gd x2 +- l 0 dx2
  gd dx8 ?: of2 of2 0
  gd x8 +- r 0 dx8
  gd dx3 +/ dx2 x8 6
  gd x3 +- x2 dx3 0
  gd dx4 +/ dx2 x8 3
  gd x4 +- x2 dx4 0
  gd x5 +/ x2 x8 2
  gd x6 +- x5 dx3 0
  gd x7 +/ x6 x8 2
  gd x9 +- l dx8 0
  gd x15 +- r dx2 0
  gd x10 +- x9 dx3 0
  gd x11 +- x9 dx4 0
  gd x12 +/ x9 x15 2
  gd x13 +- x12 dx3 0
  gd x14 +/ x13 x15 2
  gd il max x2 x9
  gd ir min x8 x15
  gd it */ h a1 50000
  gd ib +- b 0 it
  path: M x2 y1 C x3 y2 x4 y3 x5 y1 C x6 y2 x7 y3 x8 y1 L x15 y4 C x14 y6 x13 y5 x12 y4 C x11 y6 x10 y5 x9 y4 Z
  text il it ir ib

funnel
  gd d */ ss 1 20
  gd rw2 +- wd2 0 d
  gd rh2 +- hd4 0 d
  gd t1 cos wd2 480000
  gd t2 sin hd4 480000
  gd da at2 t1 t2
  gd 2da */ da 2 1
  gd stAng1 +- cd2 0 da
  gd swAng1 +- cd2 2da 0
  gd swAng3 +- cd2 0 2da
  gd rw3 */ wd2 1 4
  gd rh3 */ hd4 1 4
  gd ct1 cos hd4 stAng1
  gd st1 sin wd2 stAng1
  gd m1 mod ct1 st1 0
  gd n1 */ wd2 hd4 m1
  gd dx1 cos n1 stAng1
  gd dy1 sin n1 stAng1
  gd x1 +- hc dx1 0
  gd y1 +- hd4 dy1 0
  gd ct3 cos rh3 da
  gd st3 sin rw3 da
  gd m3 mod ct3 st3 0
  gd n3 */ rw3 rh3 m3
  gd dx3 cos n3 da
  gd dy3 sin n3 da
  gd x3 +- hc dx3 0
  gd vc3 +- b 0 rh3
  gd y2 +- vc3 dy3 0
  gd x2 +- wd2 0 rw2
  path: M x1 y1 A wd2 hd4 stAng1 swAng1 L x3 y2 A rw3 rh3 da swAng3 Z M x2 hd4 A rw2 rh2 cd2 -21600000 Z
  text l t r b

flowChartMagneticTape
{EllipseText}  gd ang1 at2 w h
  path: M hc b A wd2 hd2 cd4 cd4 A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 ang1 L r ib L r b Z
  text il it ir ib

flowChartMultidocument
  gd y2 */ h 3675 21600
  gd y8 */ h 17322 21600
  gd x5 */ w 18595 21600
  path w=21600 h=21600: M 0 20782 C 9298 23542 9298 18022 18595 18022 L 18595 3675 L 0 3675 Z M 1532 3675 L 1532 1815 L 20000 1815 L 20000 16252 C 19298 16252 18595 16352 18595 16352 L 18595 3675 Z M 2972 1815 L 2972 0 L 21600 0 L 21600 14392 C 20800 14392 20000 14467 20000 14467 L 20000 1815 Z
  path w=21600 h=21600 fill=none: M 0 3675 L 18595 3675 L 18595 18022 C 9298 18022 9298 23542 0 20782 Z M 1532 3675 L 1532 1815 L 20000 1815 L 20000 16252 C 19298 16252 18595 16352 18595 16352 M 2972 1815 L 2972 0 L 21600 0 L 21600 14392 C 20800 14392 20000 14467 20000 14467
  text l y2 x5 y8

flowChartOfflineStorage
  gd x4 */ w 3 4
  path w=2 h=2: M 0 0 L 2 0 L 1 2 Z
  path w=5 h=5 fill=none: M 2 4 L 3 4
  text wd4 t x4 vc

mathMultiply
  av adj1 23520
  gd a1 pin 0 adj1 51965
  gd th */ ss a1 100000
  gd a at2 w h
  gd sa sin 1 a
  gd ca cos 1 a
  gd ta tan 1 a
  gd dl mod w h 0
  gd rw */ dl 51965 100000
  gd lM +- dl 0 rw
  gd xM */ ca lM 2
  gd yM */ sa lM 2
  gd dxAM */ sa th 2
  gd dyAM */ ca th 2
  gd xA +- xM 0 dxAM
  gd yA +- yM dyAM 0
  gd xB +- xM dxAM 0
  gd yB +- yM 0 dyAM
  gd xBC +- hc 0 xB
  gd yBC */ xBC ta 1
  gd yC +- yBC yB 0
  gd xD +- r 0 xB
  gd xE +- r 0 xA
  gd yFE +- vc 0 yA
  gd xFE */ yFE 1 ta
  gd xF +- xE 0 xFE
  gd xL +- xA xFE 0
  gd yG +- b 0 yA
  gd yH +- b 0 yB
  gd yI +- b 0 yC
  path: M xA yA L xB yB L hc yC L xD yB L xE yA L xF vc L xE yG L xD yH L hc yI L xB yH L xA yG L xL vc Z
  text xA yB xE yH

mathDivide
  av adj1 23520
  av adj2 5880
  av adj3 11760
  gd a1 pin 1000 adj1 36745
  gd ma1 +- 73490 0 a1
  gd ma3h */ ma1 1 4
  gd ma3w */ 36745 w h
  gd maxAdj3 min ma3h ma3w
  gd a3 pin 1000 adj3 maxAdj3
  gd m4a3 */ -4 a3 1
  gd maxAdj2a +- 73490 m4a3 a1
  gd maxAdj2 */ maxAdj2a 1 2
  gd a2 pin 0 adj2 maxAdj2
  gd dy1 */ h a1 200000
  gd yg */ h a2 100000
  gd rad */ h a3 100000
  gd dx1 */ w 73490 200000
  gd y3 +- vc 0 dy1
  gd y4 +- vc dy1 0
  gd a +- yg rad 0
  gd y2 +- y3 0 a
  gd y1 +- y2 0 rad
  gd y5 +- b 0 y1
  gd x1 +- hc 0 dx1
  gd x3 +- hc dx1 0
  path: M hc y1 A rad rad 3cd4 21600000 Z M hc y5 A rad rad cd4 21600000 Z M x1 y3 L x3 y3 L x3 y4 L x1 y4 Z
  text x1 y3 x3 y4

mathNotEqual
  av adj1 23520
  av adj2 6600000
  av adj3 11760
  gd a1 pin 0 adj1 50000
  gd crAng pin 4200000 adj2 6600000
  gd 2a1 */ a1 2 1
  gd maxAdj3 +- 100000 0 2a1
  gd a3 pin 0 adj3 maxAdj3
  gd dy1 */ h a1 100000
  gd dy2 */ h a3 200000
  gd dx1 */ w 73490 200000
  gd x1 +- hc 0 dx1
  gd x8 +- hc dx1 0
  gd y2 +- vc 0 dy2
  gd y3 +- vc dy2 0
  gd y1 +- y2 0 dy1
  gd y4 +- y3 dy1 0
  gd cadj2 +- crAng 0 cd4
  gd xadj2 tan hd2 cadj2
  gd len mod xadj2 hd2 0
  gd bhw */ len dy1 hd2
  gd bhw2 */ bhw 1 2
  gd x7 +- hc xadj2 bhw2
  gd dx67 */ xadj2 y1 hd2
  gd x6 +- x7 0 dx67
  gd dx57 */ xadj2 y2 hd2
  gd x5 +- x7 0 dx57
  gd dx47 */ xadj2 y3 hd2
  gd x4 +- x7 0 dx47
  gd dx37 */ xadj2 y4 hd2
  gd x3 +- x7 0 dx37
  gd rx7 +- x7 bhw 0
  gd rx6 +- x6 bhw 0
  gd rx5 +- x5 bhw 0
  gd rx4 +- x4 bhw 0
  gd rx3 +- x3 bhw 0
  gd dx7 */ dy1 hd2 len
  gd rxt +- x7 dx7 0
  gd lxt +- rx7 0 dx7
  gd rx ?: cadj2 rxt rx7
  gd lx ?: cadj2 x7 lxt
  gd dy3 */ dy1 xadj2 len
  gd dy4 +- 0 0 dy3
  gd ry ?: cadj2 dy3 t
  gd ly ?: cadj2 t dy4
  gd dlx +- w 0 rx
  gd drx +- w 0 lx
  gd dy5 +- h 0 ry
  gd dy6 +- h 0 ly
  path: M x1 y1 L x6 y1 L lx ly L rx ry L rx6 y1 L x8 y1 L x8 y2 L rx5 y2 L rx4 y3 L x8 y3 L x8 y4 L rx3 y4 L drx dy6 L dlx dy5 L x3 y4 L x1 y4 L x1 y3 L x4 y3 L x5 y2 L x1 y2 Z
  text x1 y1 x8 y4

cornerTabs
  gd md mod w h 0
  gd dx */ 1 md 20
  gd y1 +- 0 b dx
  gd x1 +- 0 r dx
  path: M l t L dx t L l dx Z M l y1 L dx b L l b Z M x1 t L r t L r dx Z M r y1 L r b L x1 b Z
  text dx dx x1 y1

squareTabs
  gd md mod w h 0
  gd dx */ 1 md 20
  gd y1 +- 0 b dx
  gd x1 +- 0 r dx
  path: M l t L dx t L dx dx L l dx Z M l y1 L dx y1 L dx b L l b Z M x1 t L r t L r dx L x1 dx Z M x1 y1 L r y1 L r b L x1 b Z
  text dx dx x1 y1

plaqueTabs
  gd md mod w h 0
  gd dx */ 1 md 20
  gd x1 +- 0 r dx
  gd y1 +- 0 b dx
  path: M l t L dx t A dx dx 0 cd4 Z M l y1 A dx dx 3cd4 cd4 L l b Z M r t L r dx A dx dx cd4 cd4 Z M x1 b A dx dx cd2 cd4 L r b Z
  text dx dx x1 y1

chartX
  path w=10 h=10 stroke=0: M 0 0 L 10 0 L 10 10 L 0 10 Z
  path w=10 h=10 fill=none: M 0 0 L 10 10 M 0 10 L 10 0
  text l t r b

chartStar
  path w=10 h=10 stroke=0: M 0 0 L 10 0 L 10 10 L 0 10 Z
  path w=10 h=10 fill=none: M 0 0 L 10 10 M 0 10 L 10 0 M 5 0 L 5 10
  text l t r b

chartPlus
  path w=10 h=10 stroke=0: M 0 0 L 10 0 L 10 10 L 0 10 Z
  path w=10 h=10 fill=none: M 5 0 L 5 10 M 0 5 L 10 5
  text l t r b

""";

    private const string CloudText = "  gd il */ w 2977 21600\n  gd it */ h 3262 21600\n  gd ir */ w 17087 21600\n  gd ib */ h 17337 21600\n";

    // The cloud's outline in its own 43,200 × 43,200 space: eleven arcs, each from where the last one ended.
    private const string CloudOutline =
        "M 3900 14370 A 6753 9190 -11429249 7426832 A 5333 7267 -8646143 5396714 A 4365 5945 -8748475 5983381 " +
        "A 4857 6595 -7859164 7034504 A 5333 7273 -4722533 6541615 A 6775 9220 -2776035 7816140 A 5785 7867 37501 6842000 " +
        "A 6752 9215 1347096 6910353 A 7720 10543 3974558 4542661 A 4360 5918 -16496525 8804134 A 4345 5945 -14809710 9151131 Z";

    // The short arcs inside the cloud where its puffs overlap.
    private const string CloudArcs =
        "M 4693 26177 A 4345 5945 5204520 1585770 M 6928 34899 A 4360 5918 4416628 686848 M 16478 39090 A 6752 9215 8257449 844866 " +
        "M 28827 34751 A 6752 9215 387196 959901 M 34129 22954 A 5785 7867 -4217541 4255042 M 41798 15354 A 5333 7273 1819082 1665090 " +
        "M 38324 5426 A 4857 6595 -824660 891534 M 29078 3952 A 4857 6595 -8950887 1091722 M 22141 4720 A 4365 5945 -9809656 1061181 " +
        "M 14000 5192 A 6753 9190 -4002417 739161 M 4127 15789 A 6753 9190 9459261 711490";
}
