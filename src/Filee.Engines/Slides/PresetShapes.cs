// DrawingML's preset shapes (a:prstGeom), written from the definitions in ECMA-376 Part 1, 20.1.10.56 (ST_ShapeType)
// and its presetShapeDefinitions, in Filee's own compact notation (ShapeGeometry.ParseNotation): the same adjust
// values, guide formulas, paths and text rectangles. Stars of 4 to 32 points are made by one rule (Star) with the
// definitions' radii, inner radius adjust value and stretch factors.
//
// Shapes not here (block arcs, no-smoking signs, clouds, ribbons, scrolls, curved and circular arrows, action buttons, border callouts,
// gears, ...) are named by the slide capability check, and drawn as their bounding rectangle when no other route is.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Slides;

internal static class PresetShapes
{
    private static readonly Lazy<Dictionary<string, GeometryDefinition>> All = new(Build);

    /// <summary>The definition of preset <paramref name="name"/>, or null for one Filee doesn't draw.</summary>
    public static GeometryDefinition? Find(string? name) => name is not null && All.Value.TryGetValue(name, out var definition) ? definition : null;

    /// <summary>True for a preset Filee draws.</summary>
    public static bool Known(string name) => All.Value.ContainsKey(name);

    public static IReadOnlyCollection<string> Names => All.Value.Keys;

    private static Dictionary<string, GeometryDefinition> Build()
    {
        var shapes = ShapeGeometry.ParseNotation(Notation);
        foreach (var (points, adjust, hf, vf) in new (int, int, int, int)[]
                 {
                     (4, 12500, 0, 0), (5, 19098, 105146, 110557), (6, 28868, 115470, 0), (7, 34601, 102572, 105210), (8, 38250, 0, 0),
                     (10, 42533, 105146, 0), (12, 37500, 0, 0), (16, 37500, 0, 0), (24, 37500, 0, 0), (32, 37500, 0, 0),
                 })
            shapes[$"star{points}"] = ShapeGeometry.ParseNotation(Star(points, adjust, hf, vf))[$"star{points}"];
        return shapes;
    }

    /// <summary>
    /// An n-pointed star: outer points on the ellipse of the box (stretched by hf / vf as the definitions do, the
    /// centre moved down by vf for stars with a point at the top), inner points at adj / 50,000 of it, between them.
    /// Stars stretched by hf (5, 6, 7, 10 points) have a point at the top, the others at the left.
    /// </summary>
    private static string Star(int points, int adjust, int hf, int vf)
    {
        static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var notation = new StringBuilder($"star{points}\n  av adj {adjust}\n  gd a pin 0 adj 50000\n");
        notation.Append(hf > 0 ? $"  gd swd2 */ wd2 {hf} 100000\n" : "  gd swd2 val wd2\n");
        notation.Append(vf > 0 ? $"  gd shd2 */ hd2 {vf} 100000\n  gd svc */ vc {vf} 100000\n" : "  gd shd2 val hd2\n  gd svc val vc\n");
        notation.Append("  gd iwd2 */ swd2 a 50000\n  gd ihd2 */ shd2 a 50000\n");
        var start = hf > 0 ? -90.0 : 180.0;
        var path = new StringBuilder();
        for (var i = 0; i < points * 2; i++)
        {
            var angle = (start + i * 180.0 / points) * Math.PI / 180;
            var (rx, ry) = i % 2 == 0 ? ("swd2", "shd2") : ("iwd2", "ihd2");
            notation.Append($"  gd x{i} */ {rx} {N(Math.Cos(angle) * 100000)} 100000\n  gd px{i} +- hc x{i} 0\n");
            notation.Append($"  gd y{i} */ {ry} {N(Math.Sin(angle) * 100000)} 100000\n  gd py{i} +- svc y{i} 0\n");
            path.Append(i == 0 ? $"M px{i} py{i} " : $"L px{i} py{i} ");
        }
        // The text sits in the square inscribed in the inner points' ellipse.
        notation.Append("  gd tdx */ iwd2 70711 100000\n  gd tdy */ ihd2 70711 100000\n");
        notation.Append("  gd tl +- hc 0 tdx\n  gd tr +- hc tdx 0\n  gd tt +- svc 0 tdy\n  gd tb +- svc tdy 0\n");
        notation.Append($"  path: {path}Z\n  text tl tt tr tb\n");
        return notation.ToString();
    }

    private const string EllipseText =
        "\n  gd idx cos wd2 2700000\n  gd idy sin hd2 2700000\n  gd il +- hc 0 idx\n  gd ir +- hc idx 0\n  gd it +- vc 0 idy\n  gd ib +- vc idy 0\n";

    private const string Wedge =
        "\n  gd dxPos */ w adj1 100000\n  gd dyPos */ h adj2 100000\n  gd xPos +- hc dxPos 0\n  gd yPos +- vc dyPos 0\n" +
        "  gd dx */ dxPos h 1\n  gd dy */ dyPos w 1\n  gd adx abs dx\n  gd ady abs dy\n  gd dq +- adx 0 ady\n" +
        "  gd xg1 ?: dxPos 7 2\n  gd xg2 ?: dxPos 10 5\n  gd x1 */ w xg1 12\n  gd x2 */ w xg2 12\n" +
        "  gd yg1 ?: dyPos 7 2\n  gd yg2 ?: dyPos 10 5\n  gd y1 */ h yg1 12\n  gd y2 */ h yg2 12\n" +
        "  gd t1 ?: dxPos l xPos\n  gd xl ?: dq t1 l\n  gd t2 ?: dyPos x1 xPos\n  gd xt ?: dq x1 t2\n" +
        "  gd t3 ?: dxPos xPos r\n  gd xr ?: dq t3 r\n  gd t4 ?: dyPos xPos x1\n  gd xb ?: dq x1 t4\n" +
        "  gd t5 ?: dxPos y1 yPos\n  gd yl ?: dq t5 y1\n  gd t6 ?: dyPos t yPos\n  gd yt ?: dq t t6\n" +
        "  gd t7 ?: dxPos yPos y1\n  gd yr ?: dq t7 y1\n  gd t8 ?: dyPos yPos b\n  gd yb ?: dq b t8\n";

    private const string Notation =
"""
rect
  path: M l t L r t L r b L l b Z
  text l t r b

roundRect
  av adj 16667
  gd a pin 0 adj 50000
  gd x1 */ ss a 100000
  gd x2 +- r 0 x1
  gd y2 +- b 0 x1
  gd il */ x1 29289 100000
  gd ir +- r 0 il
  gd ib +- b 0 il
  path: M l x1 A x1 x1 cd2 cd4 L x2 t A x1 x1 3cd4 cd4 L r y2 A x1 x1 0 cd4 L x1 b A x1 x1 cd4 cd4 Z
  text il il ir ib

ellipse
""" + EllipseText + """
  path: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z
  text il it ir ib

flowChartConnector
""" + EllipseText + """
  path: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z
  text il it ir ib

triangle
  av adj 50000
  gd a pin 0 adj 100000
  gd x1 */ w a 200000
  gd x2 */ w a 100000
  gd x3 +- x1 wd2 0
  path: M l b L x2 t L r b Z
  text x1 vc x3 b

rtTriangle
  gd it */ h 7 12
  gd ir */ w 7 12
  gd ib */ h 11 12
  path: M l b L l t L r b Z
  text l it ir ib

parallelogram
  av adj 25000
  gd maxAdj */ 100000 w ss
  gd a pin 0 adj maxAdj
  gd x2 */ ss a 100000
  gd x5 +- r 0 x2
  gd il */ wd3 a maxAdj
  gd it */ hd3 a maxAdj
  gd ir +- r 0 il
  gd ib +- b 0 it
  path: M l b L x2 t L r t L x5 b Z
  text il it ir ib

flowChartInputOutput
  gd x3 */ w 1 5
  gd x4 */ w 4 5
  path w=5 h=5: M 0 5 L 1 0 L 5 0 L 4 5 Z
  text x3 t x4 b

trapezoid
  av adj 25000
  gd maxAdj */ 50000 w ss
  gd a pin 0 adj maxAdj
  gd x2 */ ss a 100000
  gd x3 +- r 0 x2
  gd il */ wd3 a maxAdj
  gd it */ hd3 a maxAdj
  gd ir +- r 0 il
  path: M l b L x2 t L x3 t L r b Z
  text il it ir b

diamond
  gd ir */ w 3 4
  gd ib */ h 3 4
  path: M l vc L hc t L r vc L hc b Z
  text wd4 hd4 ir ib

flowChartDecision
  gd ir */ w 3 4
  gd ib */ h 3 4
  path w=2 h=2: M 0 1 L 1 0 L 2 1 L 1 2 Z
  text wd4 hd4 ir ib

pentagon
  av hf 105146
  av vf 110557
  gd swd2 */ wd2 hf 100000
  gd shd2 */ hd2 vf 100000
  gd svc */ vc vf 100000
  gd dx1 cos swd2 1080000
  gd dx2 cos swd2 18360000
  gd dy1 sin shd2 1080000
  gd dy2 sin shd2 18360000
  gd x1 +- hc 0 dx1
  gd x2 +- hc 0 dx2
  gd x3 +- hc dx2 0
  gd x4 +- hc dx1 0
  gd y1 +- svc 0 dy1
  gd y2 +- svc 0 dy2
  gd it */ y1 dx2 dx1
  path: M x1 y1 L hc t L x4 y1 L x3 y2 L x2 y2 Z
  text x2 it x3 y2

hexagon
  av adj 25000
  av vf 115470
  gd maxAdj */ 50000 w ss
  gd a pin 0 adj maxAdj
  gd shd2 */ hd2 vf 100000
  gd x1 */ ss a 100000
  gd x2 +- r 0 x1
  gd dy1 sin shd2 3600000
  gd y1 +- vc 0 dy1
  gd y2 +- vc dy1 0
  gd q1 */ maxAdj -1 2
  gd q2 +- a q1 0
  gd q3 ?: q2 4 2
  gd q4 ?: q2 3 2
  gd q5 ?: q2 q1 0
  gd q6 +/ a q5 q1
  gd q7 */ q6 q4 -1
  gd q8 +- q3 q7 0
  gd il */ w q8 24
  gd it */ h q8 24
  gd ir +- r 0 il
  gd ib +- b 0 it
  path: M l vc L x1 y1 L x2 y1 L r vc L x2 y2 L x1 y2 Z
  text il it ir ib

heptagon
  av hf 102572
  av vf 105210
  gd swd2 */ wd2 hf 100000
  gd shd2 */ hd2 vf 100000
  gd svc */ vc vf 100000
  gd dx1 */ swd2 97493 100000
  gd dx2 */ swd2 78183 100000
  gd dx3 */ swd2 43388 100000
  gd dy1 */ shd2 62349 100000
  gd dy2 */ shd2 22252 100000
  gd dy3 */ shd2 90097 100000
  gd x1 +- hc 0 dx1
  gd x2 +- hc 0 dx2
  gd x3 +- hc 0 dx3
  gd x4 +- hc dx3 0
  gd x5 +- hc dx2 0
  gd x6 +- hc dx1 0
  gd y1 +- svc 0 dy1
  gd y2 +- svc dy2 0
  gd y3 +- svc dy3 0
  path: M x1 y2 L x2 y1 L hc t L x5 y1 L x6 y2 L x4 y3 L x3 y3 Z
  text x2 y1 x5 y3

octagon
  av adj 29289
  gd a pin 0 adj 50000
  gd x1 */ ss a 100000
  gd x2 +- r 0 x1
  gd y2 +- b 0 x1
  gd il */ x1 1 2
  gd ir +- r 0 il
  gd ib +- b 0 il
  path: M l x1 L x1 t L x2 t L r x1 L r y2 L x2 b L x1 b L l y2 Z
  text il il ir ib

decagon
  av vf 105146
  gd shd2 */ hd2 vf 100000
  gd dx1 cos wd2 2160000
  gd dx2 cos wd2 4320000
  gd x1 +- hc 0 dx1
  gd x2 +- hc 0 dx2
  gd x3 +- hc dx2 0
  gd x4 +- hc dx1 0
  gd dy1 sin shd2 4320000
  gd dy2 sin shd2 2160000
  gd y1 +- vc 0 dy1
  gd y2 +- vc 0 dy2
  gd y3 +- vc dy2 0
  gd y4 +- vc dy1 0
  path: M l vc L x1 y2 L x2 y1 L x3 y1 L x4 y2 L r vc L x4 y3 L x3 y4 L x2 y4 L x1 y3 Z
  text x1 y2 x4 y3

dodecagon
  gd x1 */ w 2894 21600
  gd x2 */ w 7906 21600
  gd x3 */ w 13694 21600
  gd x4 */ w 18706 21600
  gd y1 */ h 2894 21600
  gd y2 */ h 7906 21600
  gd y3 */ h 13694 21600
  gd y4 */ h 18706 21600
  path: M l y2 L x1 y1 L x2 t L x3 t L x4 y1 L r y2 L r y3 L x4 y4 L x3 b L x2 b L x1 y4 L l y3 Z
  text x1 y1 x4 y4

homePlate
  av adj 50000
  gd maxAdj */ 100000 w ss
  gd a pin 0 adj maxAdj
  gd dx1 */ ss a 100000
  gd x1 +- r 0 dx1
  gd ir +/ x1 r 2
  path: M l t L x1 t L r vc L x1 b L l b Z
  text l t ir b

chevron
  av adj 50000
  gd maxAdj */ 100000 w ss
  gd a pin 0 adj maxAdj
  gd x1 */ ss a 100000
  gd x2 +- r 0 x1
  gd dx +- x2 0 x1
  gd il ?: dx x1 l
  gd ir ?: dx x2 r
  path: M l t L x2 t L r vc L x2 b L l b L x1 vc Z
  text il t ir b

plus
  av adj 25000
  gd a pin 0 adj 50000
  gd x1 */ ss a 100000
  gd x2 +- r 0 x1
  gd y2 +- b 0 x1
  gd d +- w 0 h
  gd il ?: d l x1
  gd ir ?: d r x2
  gd it ?: d x1 t
  gd ib ?: d y2 b
  path: M l x1 L x1 x1 L x1 t L x2 t L x2 x1 L r x1 L r y2 L x2 y2 L x2 b L x1 b L x1 y2 L l y2 Z
  text il it ir ib

donut
  av adj 25000
  gd a pin 0 adj 50000
  gd dr */ ss a 100000
  gd iwd2 +- wd2 0 dr
  gd ihd2 +- hd2 0 dr
""" + EllipseText + """
  path: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z M dr vc A iwd2 ihd2 cd2 -5400000 A iwd2 ihd2 cd4 -5400000 A iwd2 ihd2 0 -5400000 A iwd2 ihd2 3cd4 -5400000 Z
  text il it ir ib

arc
  av adj1 16200000
  av adj2 0
  gd stAng pin 0 adj1 21599999
  gd enAng pin 0 adj2 21599999
  gd sw11 +- enAng 0 stAng
  gd sw12 +- sw11 21600000 0
  gd swAng ?: sw11 sw11 sw12
  gd wt1 sin wd2 stAng
  gd ht1 cos hd2 stAng
  gd dx1 cat2 wd2 ht1 wt1
  gd dy1 sat2 hd2 ht1 wt1
  gd x1 +- hc dx1 0
  gd y1 +- vc dy1 0
  path stroke=0: M x1 y1 A wd2 hd2 stAng swAng L hc vc Z
  path fill=none: M x1 y1 A wd2 hd2 stAng swAng
  text l t r b

pie
  av adj1 0
  av adj2 16200000
  gd stAng pin 0 adj1 21599999
  gd enAng pin 0 adj2 21599999
  gd sw11 +- enAng 0 stAng
  gd sw12 +- sw11 21600000 0
  gd swAng ?: sw11 sw11 sw12
  gd wt1 sin wd2 stAng
  gd ht1 cos hd2 stAng
  gd dx1 cat2 wd2 ht1 wt1
  gd dy1 sat2 hd2 ht1 wt1
  gd x1 +- hc dx1 0
  gd y1 +- vc dy1 0
""" + EllipseText + """
  path: M x1 y1 A wd2 hd2 stAng swAng L hc vc Z
  text il it ir ib

chord
  av adj1 2700000
  av adj2 16200000
  gd stAng pin 0 adj1 21599999
  gd enAng pin 0 adj2 21599999
  gd sw11 +- enAng 0 stAng
  gd sw12 +- sw11 21600000 0
  gd swAng ?: sw11 sw11 sw12
  gd wt1 sin wd2 stAng
  gd ht1 cos hd2 stAng
  gd dx1 cat2 wd2 ht1 wt1
  gd dy1 sat2 hd2 ht1 wt1
  gd x1 +- hc dx1 0
  gd y1 +- vc dy1 0
""" + EllipseText + """
  path: M x1 y1 A wd2 hd2 stAng swAng Z
  text il it ir ib

frame
  av adj1 12500
  gd a1 pin 0 adj1 50000
  gd x1 */ ss a1 100000
  gd x4 +- r 0 x1
  gd y4 +- b 0 x1
  path: M l t L r t L r b L l b Z M x1 x1 L x1 y4 L x4 y4 L x4 x1 Z
  text x1 x1 x4 y4

halfFrame
  av adj1 33333
  av adj2 33333
  gd maxAdj2 */ 100000 w ss
  gd a2 pin 0 adj2 maxAdj2
  gd x1 */ ss a2 100000
  gd g1 */ h x1 w
  gd g2 +- h 0 g1
  gd maxAdj1 */ 100000 g2 ss
  gd a1 pin 0 adj1 maxAdj1
  gd y1 */ ss a1 100000
  gd dx2 */ y1 w h
  gd x2 +- r 0 dx2
  gd dy2 */ x1 h w
  gd y2 +- b 0 dy2
  path: M l t L r t L x2 y1 L x1 y1 L x1 y2 L l b Z
  text l t r b

corner
  av adj1 50000
  av adj2 50000
  gd maxAdj1 */ 100000 h ss
  gd maxAdj2 */ 100000 w ss
  gd a1 pin 0 adj1 maxAdj1
  gd a2 pin 0 adj2 maxAdj2
  gd x1 */ ss a2 100000
  gd dy1 */ ss a1 100000
  gd y1 +- b 0 dy1
  gd d +- w 0 h
  gd it ?: d y1 t
  gd ir ?: d r x1
  path: M l t L x1 t L x1 y1 L r y1 L r b L l b Z
  text l it ir b

diagStripe
  av adj 50000
  gd a pin 0 adj 100000
  gd x2 */ w a 100000
  gd x3 +/ x2 r 2
  gd y2 */ h a 100000
  gd y3 +/ y2 b 2
  path: M l y2 L x2 t L r t L l b Z
  text l t x3 y3

can
  av adj 25000
  gd maxAdj */ 50000 h ss
  gd a pin 0 adj maxAdj
  gd y1 */ ss a 200000
  gd y2 +- y1 y1 0
  gd y3 +- b 0 y1
  path stroke=0: M l y1 A wd2 y1 cd2 -10800000 L r y3 A wd2 y1 0 cd2 Z
  path stroke=0 fill=lighten: M l y1 A wd2 y1 cd2 cd2 A wd2 y1 0 cd2 Z
  path fill=none: M r y1 A wd2 y1 0 cd2 A wd2 y1 cd2 cd2 L r y3 A wd2 y1 0 cd2 L l y1
  text l y2 r y3

cube
  av adj 25000
  gd a pin 0 adj 100000
  gd y1 */ ss a 100000
  gd y4 +- b 0 y1
  gd x4 +- r 0 y1
  path stroke=0: M l y1 L x4 y1 L x4 b L l b Z
  path stroke=0 fill=darkenLess: M x4 y1 L r t L r y4 L x4 b Z
  path stroke=0 fill=lightenLess: M l y1 L y1 t L r t L x4 y1 Z
  path fill=none: M l y1 L y1 t L r t L r y4 L x4 b L l b Z M l y1 L x4 y1 L r t M x4 y1 L x4 b
  text l y1 x4 b

bevel
  av adj 12500
  gd a pin 0 adj 50000
  gd x1 */ ss a 100000
  gd x2 +- r 0 x1
  gd y2 +- b 0 x1
  path stroke=0: M x1 x1 L x2 x1 L x2 y2 L x1 y2 Z
  path stroke=0 fill=lightenLess: M l t L r t L x2 x1 L x1 x1 Z
  path stroke=0 fill=darkenLess: M l b L x1 y2 L x2 y2 L r b Z
  path stroke=0 fill=lighten: M l t L x1 x1 L x1 y2 L l b Z
  path stroke=0 fill=darken: M r t L r b L x2 y2 L x2 x1 Z
  path fill=none: M l t L r t L r b L l b Z M x1 x1 L x2 x1 L x2 y2 L x1 y2 Z M l t L x1 x1 M l b L x1 y2 M r t L x2 x1 M r b L x2 y2
  text x1 x1 x2 y2

foldedCorner
  av adj 16667
  gd a pin 0 adj 50000
  gd dy2 */ ss a 100000
  gd dy1 */ dy2 1 5
  gd x1 +- r 0 dy2
  gd x2 +- x1 dy1 0
  gd y2 +- b 0 dy2
  gd y1 +- y2 dy1 0
  path stroke=0: M l t L r t L r y2 L x1 b L l b Z
  path stroke=0 fill=darkenLess: M x1 b L x2 y1 L r y2 Z
  path fill=none: M x1 b L x2 y1 L r y2 L x1 b L l b L l t L r t L r y2
  text l t r y2

snip1Rect
  av adj 16667
  gd a pin 0 adj 50000
  gd dx1 */ ss a 100000
  gd x1 +- r 0 dx1
  gd it */ dx1 1 2
  gd ir +/ x1 r 2
  path: M l t L x1 t L r dx1 L r b L l b Z
  text l it ir b

snip2SameRect
  av adj1 16667
  av adj2 0
  gd a1 pin 0 adj1 50000
  gd a2 pin 0 adj2 50000
  gd tx1 */ ss a1 100000
  gd tx2 +- r 0 tx1
  gd bx1 */ ss a2 100000
  gd bx2 +- r 0 bx1
  gd by1 +- b 0 bx1
  gd d +- tx1 0 bx1
  gd dx ?: d tx1 bx1
  gd il */ dx 1 2
  gd ir +- r 0 il
  gd it */ tx1 1 2
  gd ib +/ by1 b 2
  path: M tx1 t L tx2 t L r tx1 L r by1 L bx2 b L bx1 b L l by1 L l tx1 Z
  text il it ir ib

snip2DiagRect
  av adj1 0
  av adj2 16667
  gd a1 pin 0 adj1 50000
  gd a2 pin 0 adj2 50000
  gd lx1 */ ss a1 100000
  gd lx2 +- r 0 lx1
  gd ly1 +- b 0 lx1
  gd rx1 */ ss a2 100000
  gd rx2 +- r 0 rx1
  gd ry1 +- b 0 rx1
  gd d +- lx1 0 rx1
  gd dx ?: d lx1 rx1
  gd il */ dx 1 2
  gd ir +- r 0 il
  gd ib +- b 0 il
  path: M lx1 t L rx2 t L r rx1 L r ly1 L lx2 b L rx1 b L l ry1 L l lx1 Z
  text il il ir ib

snipRoundRect
  av adj1 16667
  av adj2 16667
  gd a1 pin 0 adj1 50000
  gd a2 pin 0 adj2 50000
  gd x1 */ ss a1 100000
  gd dx2 */ ss a2 100000
  gd x2 +- r 0 dx2
  gd il */ x1 29289 100000
  gd ir +/ x2 r 2
  path: M x1 t L x2 t L r dx2 L r b L l b L l x1 A x1 x1 cd2 cd4 Z
  text il il ir b

round1Rect
  av adj 16667
  gd a pin 0 adj 50000
  gd dx1 */ ss a 100000
  gd x1 +- r 0 dx1
  gd idx */ dx1 29289 100000
  gd ir +- r 0 idx
  path: M l t L x1 t A dx1 dx1 3cd4 cd4 L r b L l b Z
  text l t ir b

round2SameRect
  av adj1 16667
  av adj2 0
  gd a1 pin 0 adj1 50000
  gd a2 pin 0 adj2 50000
  gd tx1 */ ss a1 100000
  gd tx2 +- r 0 tx1
  gd bx1 */ ss a2 100000
  gd bx2 +- r 0 bx1
  gd by1 +- b 0 bx1
  gd d +- tx1 0 bx1
  gd tdx */ tx1 29289 100000
  gd bdx */ bx1 29289 100000
  gd il ?: d tdx bdx
  gd ir +- r 0 il
  gd ib +- b 0 bdx
  path: M tx1 t L tx2 t A tx1 tx1 3cd4 cd4 L r by1 A bx1 bx1 0 cd4 L bx1 b A bx1 bx1 cd4 cd4 L l tx1 A tx1 tx1 cd2 cd4 Z
  text il tdx ir ib

round2DiagRect
  av adj1 16667
  av adj2 0
  gd a1 pin 0 adj1 50000
  gd a2 pin 0 adj2 50000
  gd x1 */ ss a1 100000
  gd y1 +- b 0 x1
  gd a */ ss a2 100000
  gd x2 +- r 0 a
  gd y2 +- b 0 a
  gd dx1 */ x1 29289 100000
  gd dx2 */ a 29289 100000
  gd d +- dx1 0 dx2
  gd dx ?: d dx1 dx2
  gd ir +- r 0 dx
  gd ib +- b 0 dx
  path: M x1 t L x2 t A a a 3cd4 cd4 L r y1 A x1 x1 0 cd4 L a b A a a cd4 cd4 L l x1 A x1 x1 cd2 cd4 Z
  text dx dx ir ib

flowChartAlternateProcess
  gd x2 +- r 0 ssd6
  gd y2 +- b 0 ssd6
  gd il */ ssd6 29289 100000
  gd ir +- r 0 il
  gd ib +- b 0 il
  path: M l ssd6 A ssd6 ssd6 cd2 cd4 L x2 t A ssd6 ssd6 3cd4 cd4 L r y2 A ssd6 ssd6 0 cd4 L ssd6 b A ssd6 ssd6 cd4 cd4 Z
  text il il ir ib

plaque
  av adj 16667
  gd a pin 0 adj 50000
  gd x1 */ ss a 100000
  gd x2 +- r 0 x1
  gd y2 +- b 0 x1
  gd il */ x1 70711 100000
  gd ir +- r 0 il
  gd ib +- b 0 il
  path: M l x1 A x1 x1 cd4 -5400000 L x2 t A x1 x1 cd2 -5400000 L r y2 A x1 x1 3cd4 -5400000 L x1 b A x1 x1 0 -5400000 Z
  text il il ir ib

teardrop
  av adj 100000
  gd a pin 0 adj 200000
  gd r2 sqrt 2
  gd tw */ r2 wd2 1
  gd th */ r2 hd2 1
  gd sw */ tw a 100000
  gd sh */ th a 100000
  gd dx1 cos sw 2700000
  gd dy1 sin sh 2700000
  gd x1 +- hc dx1 0
  gd y1 +- vc 0 dy1
  gd x2 +/ hc x1 2
  gd y2 +/ vc y1 2
""" + EllipseText + """
  path: M l vc A wd2 hd2 cd2 cd4 Q x2 t x1 y1 Q r y2 r vc A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z
  text il it ir ib

heart
  gd dx1 */ w 49 48
  gd dx2 */ w 10 48
  gd x1 +- hc 0 dx1
  gd x2 +- hc 0 dx2
  gd x3 +- hc dx2 0
  gd x4 +- hc dx1 0
  gd y1 +- t 0 hd3
  gd il */ w 1 6
  gd ir */ w 5 6
  gd ib */ h 2 3
  path: M hc hd4 C x3 y1 x4 hd4 hc b C x1 hd4 x2 y1 hc hd4 Z
  text il hd4 ir ib

smileyFace
  av adj 4653
  gd a pin -4653 adj 4653
  gd x1 */ w 4969 21699
  gd x2 */ w 6215 21600
  gd x3 */ w 13135 21600
  gd x4 */ w 16640 21600
  gd y1 */ h 7570 21600
  gd y3 */ h 16515 21600
  gd dy2 */ h a 100000
  gd y2 +- y3 0 dy2
  gd y4 +- y3 dy2 0
  gd dy3 */ h a 50000
  gd y5 +- y4 dy3 0
  gd wR */ w 1125 21600
  gd hR */ h 1125 21600
""" + EllipseText + """
  path stroke=0: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z
  path fill=darkenLess: M x2 y1 A wR hR cd2 21600000 Z M x3 y1 A wR hR cd2 21600000 Z
  path fill=none: M x1 y2 Q hc y5 x4 y2
  path fill=none: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z
  text il it ir ib

rightArrow
  av adj1 50000
  av adj2 50000
  gd maxAdj2 */ 100000 w ss
  gd a1 pin 0 adj1 100000
  gd a2 pin 0 adj2 maxAdj2
  gd dx1 */ ss a2 100000
  gd x1 +- r 0 dx1
  gd dy1 */ h a1 200000
  gd y1 +- vc 0 dy1
  gd y2 +- vc dy1 0
  gd dx2 */ y1 dx1 hd2
  gd x2 +- x1 dx2 0
  path: M l y1 L x1 y1 L x1 t L r vc L x1 b L x1 y2 L l y2 Z
  text l y1 x2 y2

leftArrow
  av adj1 50000
  av adj2 50000
  gd maxAdj2 */ 100000 w ss
  gd a1 pin 0 adj1 100000
  gd a2 pin 0 adj2 maxAdj2
  gd dx2 */ ss a2 100000
  gd x2 +- l dx2 0
  gd dy1 */ h a1 200000
  gd y1 +- vc 0 dy1
  gd y2 +- vc dy1 0
  gd dx1 */ y1 dx2 hd2
  gd x1 +- x2 0 dx1
  path: M l vc L x2 t L x2 y1 L r y1 L r y2 L x2 y2 L x2 b Z
  text x1 y1 r y2

upArrow
  av adj1 50000
  av adj2 50000
  gd maxAdj2 */ 100000 h ss
  gd a1 pin 0 adj1 100000
  gd a2 pin 0 adj2 maxAdj2
  gd dy2 */ ss a2 100000
  gd y2 +- t dy2 0
  gd dx1 */ w a1 200000
  gd x1 +- hc 0 dx1
  gd x2 +- hc dx1 0
  gd dy1 */ x1 dy2 wd2
  gd y1 +- y2 0 dy1
  path: M l y2 L hc t L r y2 L x2 y2 L x2 b L x1 b L x1 y2 Z
  text x1 y1 x2 b

downArrow
  av adj1 50000
  av adj2 50000
  gd maxAdj2 */ 100000 h ss
  gd a1 pin 0 adj1 100000
  gd a2 pin 0 adj2 maxAdj2
  gd dy1 */ ss a2 100000
  gd y1 +- b 0 dy1
  gd dx1 */ w a1 200000
  gd x1 +- hc 0 dx1
  gd x2 +- hc dx1 0
  gd dy2 */ x1 dy1 wd2
  gd y2 +- y1 dy2 0
  path: M l y1 L x1 y1 L x1 t L x2 t L x2 y1 L r y1 L hc b Z
  text x1 t x2 y2

leftRightArrow
  av adj1 50000
  av adj2 50000
  gd maxAdj2 */ 50000 w ss
  gd a1 pin 0 adj1 100000
  gd a2 pin 0 adj2 maxAdj2
  gd x2 */ ss a2 100000
  gd x3 +- r 0 x2
  gd dy */ h a1 200000
  gd y1 +- vc 0 dy
  gd y2 +- vc dy 0
  gd dx1 */ y1 x2 hd2
  gd x1 +- x2 0 dx1
  gd x4 +- x3 dx1 0
  path: M l vc L x2 t L x2 y1 L x3 y1 L x3 t L r vc L x3 b L x3 y2 L x2 y2 L x2 b Z
  text x1 y1 x4 y2

upDownArrow
  av adj1 50000
  av adj2 50000
  gd maxAdj2 */ 50000 h ss
  gd a1 pin 0 adj1 100000
  gd a2 pin 0 adj2 maxAdj2
  gd y2 */ ss a2 100000
  gd y3 +- b 0 y2
  gd dx1 */ w a1 200000
  gd x1 +- hc 0 dx1
  gd x2 +- hc dx1 0
  gd dy1 */ x1 y2 wd2
  gd y1 +- y2 0 dy1
  gd y4 +- y3 dy1 0
  path: M l y2 L hc t L r y2 L x2 y2 L x2 y3 L r y3 L hc b L l y3 L x1 y3 L x1 y2 Z
  text x1 y1 x2 y4

notchedRightArrow
  av adj1 50000
  av adj2 50000
  gd maxAdj2 */ 100000 w ss
  gd a1 pin 0 adj1 100000
  gd a2 pin 0 adj2 maxAdj2
  gd dx2 */ ss a2 100000
  gd x2 +- r 0 dx2
  gd dy1 */ h a1 200000
  gd y1 +- vc 0 dy1
  gd y2 +- vc dy1 0
  gd x1 */ dy1 dx2 hd2
  gd x3 +- r 0 x1
  path: M l y1 L x2 y1 L x2 t L r vc L x2 b L x2 y2 L l y2 L x1 vc Z
  text x1 y1 x3 y2

stripedRightArrow
  av adj1 50000
  av adj2 50000
  gd maxAdj2 */ 84375 w ss
  gd a1 pin 0 adj1 100000
  gd a2 pin 0 adj2 maxAdj2
  gd x4 */ ss 5 32
  gd dx5 */ ss a2 100000
  gd x5 +- r 0 dx5
  gd dy1 */ h a1 200000
  gd y1 +- vc 0 dy1
  gd y2 +- vc dy1 0
  gd dx6 */ dy1 dx5 hd2
  gd x6 +- r 0 dx6
  path: M l y1 L ssd32 y1 L ssd32 y2 L l y2 Z M ssd16 y1 L ssd8 y1 L ssd8 y2 L ssd16 y2 Z M x4 y1 L x5 y1 L x5 t L r vc L x5 b L x5 y2 L x4 y2 Z
  text x4 y1 x6 y2

quadArrow
  av adj1 22500
  av adj2 22500
  av adj3 22500
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
  gd y2 +- vc 0 dx2
  gd y5 +- vc dx2 0
  gd y3 +- vc 0 dx3
  gd y4 +- vc dx3 0
  gd y6 +- b 0 x1
  gd il */ dx3 x1 dx2
  gd ir +- r 0 il
  path: M l vc L x1 y2 L x1 y3 L x3 y3 L x3 x1 L x2 x1 L hc t L x5 x1 L x4 x1 L x4 y3 L x6 y3 L x6 y2 L r vc L x6 y5 L x6 y4 L x4 y4 L x4 y6 L x5 y6 L hc b L x2 y6 L x3 y6 L x3 y4 L x1 y4 L x1 y5 Z
  text il y3 ir y4

wedgeRectCallout
  av adj1 -20833
  av adj2 62500
""" + Wedge + """
  path: M l t L x1 t L xt yt L x2 t L r t L r y1 L xr yr L r y2 L r b L x2 b L xb yb L x1 b L l b L l y2 L xl yl L l y1 Z
  text l t r b

wedgeRoundRectCallout
  av adj1 -20833
  av adj2 62500
  av adj3 16667
""" + Wedge + """
  gd u1 */ ss adj3 100000
  gd u2 +- r 0 u1
  gd v2 +- b 0 u1
  gd il */ u1 29289 100000
  gd ir +- r 0 il
  gd ib +- b 0 il
  path: M l u1 A u1 u1 cd2 cd4 L x1 t L xt yt L x2 t L u2 t A u1 u1 3cd4 cd4 L r y1 L xr yr L r y2 L r v2 A u1 u1 0 cd4 L x2 b L xb yb L x1 b L u1 b A u1 u1 cd4 cd4 L l y2 L xl yl L l y1 Z
  text il il ir ib

wedgeEllipseCallout
  av adj1 -20833
  av adj2 62500
  gd dxPos */ w adj1 100000
  gd dyPos */ h adj2 100000
  gd xPos +- hc dxPos 0
  gd yPos +- vc dyPos 0
  gd sdx */ dxPos h 1
  gd sdy */ dyPos w 1
  gd pang at2 sdx sdy
  gd stAng +- pang 660000 0
  gd enAng +- pang 0 660000
  gd dx1 cos wd2 stAng
  gd dy1 sin hd2 stAng
  gd x1 +- hc dx1 0
  gd y1 +- vc dy1 0
  gd dx2 cos wd2 enAng
  gd dy2 sin hd2 enAng
  gd stAng1 at2 dx1 dy1
  gd enAng1 at2 dx2 dy2
  gd swAng1 +- enAng1 0 stAng1
  gd swAng2 +- swAng1 21600000 0
  gd swAng ?: swAng1 swAng1 swAng2
""" + EllipseText + """
  path: M xPos yPos L x1 y1 A wd2 hd2 stAng1 swAng Z
  text il it ir ib

leftBracket
  av adj 8333
  gd maxAdj */ 50000 h ss
  gd a pin 0 adj maxAdj
  gd y1 */ ss a 100000
  gd dx1 cos w 2700000
  gd dy1 sin y1 2700000
  gd il +- r 0 dx1
  gd it +- y1 0 dy1
  gd ib +- b dy1 y1
  path stroke=0: M r b A w y1 cd4 cd4 L l y1 A w y1 cd2 cd4 Z
  path fill=none: M r b A w y1 cd4 cd4 L l y1 A w y1 cd2 cd4
  text il it r ib

rightBracket
  av adj 8333
  gd maxAdj */ 50000 h ss
  gd a pin 0 adj maxAdj
  gd y1 */ ss a 100000
  gd y2 +- b 0 y1
  gd dx1 cos w 2700000
  gd dy1 sin y1 2700000
  gd ir +- l dx1 0
  gd it +- y1 0 dy1
  gd ib +- b dy1 y1
  path stroke=0: M l t A w y1 3cd4 cd4 L r y2 A w y1 0 cd4 Z
  path fill=none: M l t A w y1 3cd4 cd4 L r y2 A w y1 0 cd4
  text l it ir ib

leftBrace
  av adj1 8333
  av adj2 50000
  gd a2 pin 0 adj2 100000
  gd q1 +- 100000 0 a2
  gd q2 min q1 a2
  gd q3 */ q2 1 2
  gd maxAdj1 */ q3 h ss
  gd a1 pin 0 adj1 maxAdj1
  gd y1 */ ss a1 100000
  gd y3 */ h a2 100000
  gd y4 +- y3 y1 0
  gd dx1 cos wd2 2700000
  gd dy1 sin y1 2700000
  gd il +- r 0 dx1
  gd it +- y1 0 dy1
  gd ib +- b dy1 y1
  path stroke=0: M r b A wd2 y1 cd4 cd4 L hc y4 A wd2 y1 0 -5400000 A wd2 y1 cd4 -5400000 L hc y1 A wd2 y1 cd2 cd4 Z
  path fill=none: M r b A wd2 y1 cd4 cd4 L hc y4 A wd2 y1 0 -5400000 A wd2 y1 cd4 -5400000 L hc y1 A wd2 y1 cd2 cd4
  text il it r ib

rightBrace
  av adj1 8333
  av adj2 50000
  gd a2 pin 0 adj2 100000
  gd q1 +- 100000 0 a2
  gd q2 min q1 a2
  gd q3 */ q2 1 2
  gd maxAdj1 */ q3 h ss
  gd a1 pin 0 adj1 maxAdj1
  gd y1 */ ss a1 100000
  gd y3 */ h a2 100000
  gd y2 +- y3 0 y1
  gd y4 +- b 0 y1
  gd dx1 cos wd2 2700000
  gd dy1 sin y1 2700000
  gd ir +- l dx1 0
  gd it +- y1 0 dy1
  gd ib +- b dy1 y1
  path stroke=0: M l t A wd2 y1 3cd4 cd4 L hc y2 A wd2 y1 cd2 -5400000 A wd2 y1 3cd4 -5400000 L hc y4 A wd2 y1 0 cd4 Z
  path fill=none: M l t A wd2 y1 3cd4 cd4 L hc y2 A wd2 y1 cd2 -5400000 A wd2 y1 3cd4 -5400000 L hc y4 A wd2 y1 0 cd4
  text l it ir ib

bracketPair
  av adj 16667
  gd a pin 0 adj 50000
  gd x1 */ ss a 100000
  gd x2 +- r 0 x1
  gd y2 +- b 0 x1
  gd il */ x1 29289 100000
  gd ir +- r 0 il
  gd ib +- b 0 il
  path stroke=0: M l x1 A x1 x1 cd2 cd4 L x2 t A x1 x1 3cd4 cd4 L r y2 A x1 x1 0 cd4 L x1 b A x1 x1 cd4 cd4 Z
  path fill=none: M x1 b A x1 x1 cd4 cd4 L l x1 A x1 x1 cd2 cd4 M x2 t A x1 x1 3cd4 cd4 L r y2 A x1 x1 0 cd4
  text il il ir ib

bracePair
  av adj 8333
  gd a pin 0 adj 25000
  gd x1 */ ss a 100000
  gd x2 */ ss a 50000
  gd x3 +- r 0 x2
  gd x4 +- r 0 x1
  gd y2 +- vc 0 x1
  gd y3 +- vc x1 0
  gd y4 +- b 0 x1
  gd it */ x1 29289 100000
  gd il +- x1 it 0
  gd ir +- r 0 il
  gd ib +- b 0 it
  path fill=none: M x2 b A x1 x1 cd4 cd4 L x1 y3 A x1 x1 0 -5400000 A x1 x1 cd4 -5400000 L x1 x1 A x1 x1 cd2 cd4 M x3 t A x1 x1 3cd4 cd4 L x4 y2 A x1 x1 cd2 -5400000 A x1 x1 3cd4 -5400000 L x4 y4 A x1 x1 0 cd4
  text il il ir ib

line
  path fill=none: M l t L r b
  text l t r b

straightConnector1
  path fill=none: M l t L r b
  text l t r b

bentConnector2
  path fill=none: M l t L r t L r b
  text l t r b

bentConnector3
  av adj1 50000
  gd x1 */ w adj1 100000
  path fill=none: M l t L x1 t L x1 b L r b
  text l t r b

bentConnector4
  av adj1 50000
  av adj2 50000
  gd x1 */ w adj1 100000
  gd y2 */ h adj2 100000
  path fill=none: M l t L x1 t L x1 y2 L r y2 L r b
  text l t r b

bentConnector5
  av adj1 50000
  av adj2 50000
  av adj3 50000
  gd x1 */ w adj1 100000
  gd x3 */ w adj3 100000
  gd y2 */ h adj2 100000
  path fill=none: M l t L x1 t L x1 y2 L x3 y2 L x3 b L r b
  text l t r b

curvedConnector2
  path fill=none: M l t C wd2 t r hd2 r b
  text l t r b

curvedConnector3
  av adj1 50000
  gd x2 */ w adj1 100000
  gd x1 +/ l x2 2
  gd x3 +/ r x2 2
  gd y3 */ h 3 4
  path fill=none: M l t C x1 t x2 hd4 x2 vc C x2 y3 x3 b r b
  text l t r b

curvedConnector4
  av adj1 50000
  av adj2 50000
  gd x2 */ w adj1 100000
  gd x1 +/ l x2 2
  gd x3 +/ r x2 2
  gd x4 +/ x2 x3 2
  gd x5 +/ x3 r 2
  gd y4 */ h adj2 100000
  gd y1 +/ t y4 2
  gd y2 +/ t y1 2
  gd y3 +/ y1 y4 2
  gd y5 +/ b y4 2
  path fill=none: M l t C x1 t x2 y2 x2 y1 C x2 y3 x4 y4 x3 y4 C x5 y4 r y5 r b
  text l t r b

flowChartProcess
  path w=1 h=1: M 0 0 L 1 0 L 1 1 L 0 1 Z
  text l t r b

flowChartPredefinedProcess
  gd x2 */ w 7 8
  path w=1 h=1 stroke=0: M 0 0 L 1 0 L 1 1 L 0 1 Z
  path w=8 h=8 fill=none: M 1 0 L 1 8 M 7 0 L 7 8
  path w=1 h=1 fill=none: M 0 0 L 1 0 L 1 1 L 0 1 Z
  text wd8 t x2 b

flowChartInternalStorage
  path w=1 h=1 stroke=0: M 0 0 L 1 0 L 1 1 L 0 1 Z
  path w=4 h=4 fill=none: M 1 0 L 1 4 M 0 1 L 4 1
  path w=1 h=1 fill=none: M 0 0 L 1 0 L 1 1 L 0 1 Z
  text wd8 hd8 r b

flowChartDocument
  gd y2 */ h 17322 21600
  path w=21600 h=21600: M 0 0 L 21600 0 L 21600 17322 C 10800 17322 10800 23922 0 20172 Z
  text l t r y2

flowChartTerminator
  gd il */ w 1018 21600
  gd ir */ w 20582 21600
  gd it */ h 3163 21600
  gd ib */ h 18437 21600
  path w=21600 h=21600: M 3475 0 L 18125 0 A 3475 10800 3cd4 cd2 L 3475 21600 A 3475 10800 cd4 cd2 Z
  text il it ir ib

flowChartPreparation
  gd x2 */ w 4 5
  path w=10 h=10: M 0 5 L 2 0 L 8 0 L 10 5 L 8 10 L 2 10 Z
  text wd5 t x2 b

flowChartManualInput
  path w=5 h=5: M 0 1 L 5 0 L 5 5 L 0 5 Z
  text l hd5 r b

flowChartManualOperation
  gd x3 */ w 4 5
  path w=5 h=5: M 0 0 L 5 0 L 4 5 L 1 5 Z
  text wd5 t x3 b

flowChartOffpageConnector
  gd y1 */ h 4 5
  path w=10 h=10: M 0 0 L 10 0 L 10 8 L 5 10 L 0 8 Z
  text l t r y1

flowChartPunchedCard
  path w=5 h=5: M 0 1 L 1 0 L 5 0 L 5 5 L 0 5 Z
  text l hd5 r b

flowChartPunchedTape
  gd y2 */ h 9 10
  path w=20 h=20: M 0 2 A 5 2 cd2 -10800000 A 5 2 cd2 cd2 L 20 18 A 5 2 0 -10800000 A 5 2 0 cd2 Z
  text l hd5 r y2

flowChartOr
""" + EllipseText + """
  path stroke=0: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z
  path fill=none: M hc t L hc b M l vc L r vc
  path fill=none: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z
  text il it ir ib

flowChartSummingJunction
""" + EllipseText + """
  path stroke=0: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z
  path fill=none: M il it L ir ib M ir it L il ib
  path fill=none: M l vc A wd2 hd2 cd2 cd4 A wd2 hd2 3cd4 cd4 A wd2 hd2 0 cd4 A wd2 hd2 cd4 cd4 Z
  text il it ir ib

flowChartCollate
  gd x2 */ w 3 4
  gd y2 */ h 3 4
  path w=2 h=2: M 0 0 L 2 0 L 1 1 L 2 2 L 0 2 L 1 1 Z
  text wd4 hd4 x2 y2

flowChartSort
  gd ir */ w 3 4
  gd ib */ h 3 4
  path w=2 h=2 stroke=0: M 0 1 L 1 0 L 2 1 L 1 2 Z
  path w=2 h=2 fill=none: M 0 1 L 2 1
  path w=2 h=2 fill=none: M 0 1 L 1 0 L 2 1 L 1 2 Z
  text wd4 hd4 ir ib

flowChartExtract
  gd x2 */ w 3 4
  path w=2 h=2: M 0 2 L 1 0 L 2 2 Z
  text wd4 vc x2 b

flowChartMerge
  gd x2 */ w 3 4
  path w=2 h=2: M 0 0 L 2 0 L 1 2 Z
  text wd4 t x2 vc

flowChartOnlineStorage
  gd x2 */ w 5 6
  path w=6 h=6: M 1 0 L 6 0 A 1 3 3cd4 -10800000 L 1 6 A 1 3 cd4 cd2 Z
  text wd6 t x2 b

flowChartDelay
  gd idx cos wd2 2700000
  gd idy sin hd2 2700000
  gd ir +- hc idx 0
  gd it +- vc 0 idy
  gd ib +- vc idy 0
  path: M l t L hc t A wd2 hd2 3cd4 cd2 L l b Z
  text l it ir ib

flowChartMagneticDisk
  gd y3 */ h 5 6
  path w=6 h=6 stroke=0: M 0 1 A 3 1 cd2 cd2 L 6 5 A 3 1 0 cd2 Z
  path w=6 h=6 fill=none: M 6 1 A 3 1 0 cd2
  path w=6 h=6 fill=none: M 0 1 A 3 1 cd2 cd2 L 6 5 A 3 1 0 cd2 Z
  text l hd3 r y3

flowChartMagneticDrum
  gd x2 */ w 2 3
  path w=6 h=6 stroke=0: M 1 0 L 5 0 A 1 3 3cd4 cd2 L 1 6 A 1 3 cd4 cd2 Z
  path w=6 h=6 fill=none: M 5 6 A 1 3 cd4 cd2
  path w=6 h=6 fill=none: M 1 0 L 5 0 A 1 3 3cd4 cd2 L 1 6 A 1 3 cd4 cd2 Z
  text wd6 t x2 b

flowChartDisplay
  gd x2 */ w 5 6
  path w=6 h=6: M 0 3 L 1 0 L 5 0 A 1 3 3cd4 cd2 L 1 6 Z
  text wd6 t x2 b

mathPlus
  av adj1 23520
  gd a1 pin 0 adj1 73490
  gd dx1 */ w 73490 200000
  gd dy1 */ h 73490 200000
  gd dx2 */ ss a1 200000
  gd x1 +- hc 0 dx1
  gd x2 +- hc 0 dx2
  gd x3 +- hc dx2 0
  gd x4 +- hc dx1 0
  gd y1 +- vc 0 dy1
  gd y2 +- vc 0 dx2
  gd y3 +- vc dx2 0
  gd y4 +- vc dy1 0
  path: M x1 y2 L x2 y2 L x2 y1 L x3 y1 L x3 y2 L x4 y2 L x4 y3 L x3 y3 L x3 y4 L x2 y4 L x2 y3 L x1 y3 Z
  text x1 y2 x4 y3

mathMinus
  av adj1 23520
  gd a1 pin 0 adj1 100000
  gd dy1 */ h a1 200000
  gd dx1 */ w 73490 200000
  gd y1 +- vc 0 dy1
  gd y2 +- vc dy1 0
  gd x1 +- hc 0 dx1
  gd x2 +- hc dx1 0
  path: M x1 y1 L x2 y1 L x2 y2 L x1 y2 Z
  text x1 y1 x2 y2

mathEqual
  av adj1 23520
  av adj2 11760
  gd a1 pin 0 adj1 36745
  gd 2a1 */ a1 2 1
  gd mAdj2 +- 100000 0 2a1
  gd a2 pin 0 adj2 mAdj2
  gd dy1 */ h a1 100000
  gd dy2 */ h a2 200000
  gd dx1 */ w 73490 200000
  gd y2 +- vc 0 dy2
  gd y3 +- vc dy2 0
  gd y1 +- y2 0 dy1
  gd y4 +- y3 dy1 0
  gd x1 +- hc 0 dx1
  gd x2 +- hc dx1 0
  path: M x1 y1 L x2 y1 L x2 y2 L x1 y2 Z M x1 y3 L x2 y3 L x2 y4 L x1 y4 Z
  text x1 y1 x2 y4

wave
  av adj1 12500
  av adj2 0
  gd a1 pin 0 adj1 20000
  gd a2 pin -10000 adj2 10000
  gd y1 */ h a1 100000
  gd dy2 */ y1 10 3
  gd y2 +- y1 0 dy2
  gd y3 +- y1 dy2 0
  gd y4 +- b 0 y1
  gd y5 +- y4 0 dy2
  gd y6 +- y4 dy2 0
  gd dx1 */ w a2 100000
  gd of2 */ w a2 50000
  gd dx2 ?: of2 0 of2
  gd x2 +- l 0 dx2
  gd dx5 ?: of2 of2 0
  gd x5 +- r 0 dx5
  gd dx3 +/ dx2 x5 3
  gd x3 +- x2 dx3 0
  gd x4 +/ x3 x5 2
  gd x6 +- l dx5 0
  gd x10 +- r dx2 0
  gd x7 +- x6 dx3 0
  gd x8 +/ x7 x10 2
  gd il max x2 x6
  gd ir min x5 x10
  gd it */ h a1 50000
  gd ib +- b 0 it
  path: M x2 y1 C x3 y2 x4 y3 x5 y1 L x10 y4 C x8 y6 x7 y5 x6 y4 Z
  text il it ir ib
""";
}
