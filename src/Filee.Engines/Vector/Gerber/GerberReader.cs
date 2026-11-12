// Reads a Gerber layer (RS-274X, Gerber X2 and X3, "The Gerber Layer Format Specification" by Ucamco) into a
// GerberImage: the photoplot of one PCB layer (copper, solder mask, silkscreen, paste, board outline …) as KiCad,
// Eagle, Altium, EasyEDA and every other PCB program write it for board makers.
//
// A Gerber file is ASCII: word commands ending in '*' (G01, D10, X…Y…D01) and extended commands between '%' signs
// (%FS…*%, %ADD10C,0.5*%, %AM…%). Read: coordinate format (FS, leading or trailing zeros omitted, absolute or
// incremental) and units (MO, G70/G71); standard apertures (C, R, O, P, with holes) and macros (AM); draws, linear and
// circular (G01/G02/G03, single- and multi-quadrant G74/G75), moves and flashes (D01/D02/D03, also the deprecated modal
// form); regions (G36/G37); polarity (LP), aperture mirroring, rotation and scaling (LM/LR/LS); aperture blocks (AB),
// step and repeat (SR); a negative image (IPNEG); the file function attribute (TF.FileFunction, also KiCad's
// "G04 #@! TF…" comment form), which picks the layer's colour. Other attributes and the deprecated image transforms
// (IR, MI, OF, SF) are ignored.
//
// The file is read defensively: a damaged file fails with an InvalidDataException that says what is wrong, and
// everything that costs memory or time is counted before it is made (objects, segments, apertures, block nesting,
// step-and-repeat copies, coordinates). A file that just stops (no M02) keeps what was read.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Filee.Engines.Vector.Gerber;

/// <summary>A Gerber layer as read: its objects, apertures, blocks and file function.</summary>
internal sealed class GerberImage
{
    public List<GerberObject> Objects { get; } = [];

    public List<GerberAperture> Apertures { get; } = [];

    public List<GerberBlock> Blocks { get; } = [];

    /// <summary>The X2 file function ("Copper,L1,Top", "Legend,Top", "Profile,NP" …), null when the file has none.</summary>
    public string? FileFunction { get; set; }

    /// <summary>IPNEG: the image is a negative (clear and dark swapped over the whole layer).</summary>
    public bool Negative { get; set; }
}

/// <summary>Parses Gerber files.</summary>
internal sealed partial class GerberReader
{
    /// <summary>Largest file read. Real layers stay far below; a bigger file would only exhaust memory.</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    private const int MaxObjects = 2_000_000;
    private const long MaxSegments = 10_000_000;
    private const int MaxApertures = 100_000;
    private const int MaxBlockDepth = 8;
    private const int MaxRepeats = 10_000;
    private const int MaxSegmentsPerStroke = 256;

    /// <summary>
    /// Most segments once every block flash and step-and-repeat copy is counted with its contents: the SVG places
    /// copies by reference, but they are all drawn. Blocks flashed inside blocks multiply quickly.
    /// </summary>
    private const long MaxDrawnSegments = 50_000_000;

    /// <summary>Most macro primitives evaluated over all aperture definitions.</summary>
    private const long MaxMacroPrimitives = 500_000;

    /// <summary>Largest coordinate, in millimetres (100 m): anything beyond is a damaged number, not a board.</summary>
    private const double MaxCoordinate = 100_000;

    private readonly GerberImage _image = new();
    private readonly Dictionary<int, GerberAperture> _apertures = [];
    private readonly Dictionary<int, GerberBlock> _blockApertures = [];
    private readonly Dictionary<string, GerberMacro> _macros = new(StringComparer.Ordinal);
    private readonly Stack<Frame> _frames = new();
    private readonly CancellationToken _ct;

    // Coordinate format and units.
    private int _decimals = 4;
    private int _integers = 2;
    private bool _trailingZeros;
    private bool _incremental;
    private double _unit = 25.4; // inches until MO says otherwise, as old files assume
    private bool _sawGerber;

    // Graphics state.
    private GerberPoint _current;
    private int _interpolation = 1;
    private bool _multiQuadrant;
    private int _lastOperation;
    private GerberAperture? _aperture;
    private GerberBlock? _blockAperture;
    private bool _clear;
    private GerberPlacement _placement = GerberPlacement.Identity;
    private bool _inRegion;
    private List<GerberContour> _regionContours = [];
    private GerberContour? _regionContour;
    private GerberStroke? _openStroke;
    private bool _ended;

    private int _objectCount;
    private long _segmentCount;
    private long _drawnSegments;
    private long _macroPrimitives;
    private int _nextBlockId;

    private GerberReader(CancellationToken ct) => _ct = ct;

    /// <summary>A block being collected: an aperture block (AB) or step-and-repeat (SR).</summary>
    private sealed record Frame(GerberBlock Block, int ApertureCode, int RepeatX, int RepeatY, double StepX, double StepY);

    private List<GerberObject> Target => _frames.Count > 0 ? _frames.Peek().Block.Objects : _image.Objects;

    /// <summary>Reads a Gerber file.</summary>
    /// <exception cref="InvalidDataException">The file isn't Gerber, is damaged, or is beyond the reader's limits.</exception>
    public static GerberImage Read(byte[] data, CancellationToken ct = default)
    {
        if (data.Length > MaxFileBytes)
            throw new InvalidDataException($"The Gerber file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
        // Gerber is text; a NUL byte means another format (GIMP brushes also use .gbr).
        if (data.AsSpan(0, Math.Min(data.Length, 65536)).IndexOf((byte)0) >= 0)
            throw new InvalidDataException("The file isn't a Gerber file (it holds binary data; GIMP brushes also use .gbr).");

        var reader = new GerberReader(ct);
        reader.Run(Encoding.UTF8.GetString(data));
        return reader.Finish();
    }

    private void Run(string text)
    {
        var i = 0;
        var words = 0;
        while (i < text.Length && !_ended)
        {
            if ((++words & 0xFFF) == 0)
                _ct.ThrowIfCancellationRequested();
            var c = text[i];
            if (c == '%')
            {
                var end = text.IndexOf('%', i + 1);
                if (end < 0)
                    break; // cut off inside an extended command: keep what was read
                Extended(text.AsSpan(i + 1, end - i - 1));
                i = end + 1;
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else
            {
                var end = text.IndexOf('*', i);
                if (end < 0)
                    break;
                Word(Clean(text.AsSpan(i, end - i)));
                i = end + 1;
            }
        }
    }

    private static string Clean(ReadOnlySpan<char> s)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c is not ('\r' or '\n'))
                builder.Append(c);
        return builder.ToString();
    }

    private GerberImage Finish()
    {
        if (_inRegion)
            EndRegion();
        while (_frames.Count > 0)
            CloseFrame();
        if (_image.Objects.Count == 0)
        {
            throw new InvalidDataException(_sawGerber
                ? "The Gerber file has no drawing (no flashes, draws or regions)."
                : "The file isn't a Gerber file (no format, unit or aperture commands).");
        }
        _image.Apertures.AddRange(_apertures.Values);
        return _image;
    }

    // ───────────────────────── Extended commands ─────────────────────────

    private void Extended(ReadOnlySpan<char> block)
    {
        var words = Clean(block).Split('*').Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
        if (words.Count == 0)
            return;
        if (words[0].StartsWith("AM", StringComparison.Ordinal))
        {
            var macro = GerberMacro.Parse(words);
            _macros[macro.Name] = macro;
            _sawGerber = true;
            return;
        }
        foreach (var word in words)
            ExtendedWord(word);
    }

    private void ExtendedWord(string w)
    {
        if (w.Length < 2)
            return;
        switch (w[..2])
        {
            case "FS":
            {
                var digits = FormatDigits().Match(w);
                if (!digits.Success)
                    throw new InvalidDataException($"The Gerber file has a malformed format statement \"{w}\".");
                _integers = digits.Groups[1].Value[0] - '0';
                _decimals = digits.Groups[2].Value[0] - '0';
                var flags = w[2..digits.Index];
                _trailingZeros = flags.Contains('T');
                _incremental = flags.Contains('I');
                _sawGerber = true;
                break;
            }
            case "MO":
                if (w == "MOMM")
                    _unit = 1;
                else if (w == "MOIN")
                    _unit = 25.4;
                _sawGerber = true;
                break;
            case "AD":
                DefineAperture(w);
                _sawGerber = true;
                break;
            case "LP":
                _clear = w.Length > 2 && w[2] == 'C';
                _openStroke = null;
                break;
            case "LM":
                var mirror = w[2..];
                _placement = _placement with { MirrorX = mirror.Contains('X'), MirrorY = mirror.Contains('Y') };
                _openStroke = null;
                break;
            case "LR":
                _placement = _placement with { Rotation = Number(w[2..], w) };
                _openStroke = null;
                break;
            case "LS":
                var scale = Number(w[2..], w);
                if (scale <= 0 || scale > 1e6)
                    throw new InvalidDataException($"The Gerber file has an invalid aperture scale \"{w}\".");
                _placement = _placement with { Scale = scale };
                _openStroke = null;
                break;
            case "AB":
                ApertureBlock(w);
                break;
            case "SR":
                StepAndRepeat(w);
                break;
            case "TF":
                FileAttribute(w[2..]);
                break;
            case "IP":
                _image.Negative = w == "IPNEG";
                break;
        }
    }

    private void FileAttribute(string attribute)
    {
        const string Function = ".FileFunction,";
        if (attribute.StartsWith(Function, StringComparison.Ordinal))
            _image.FileFunction = attribute[Function.Length..];
    }

    private void DefineAperture(string w)
    {
        var match = ApertureDefinition().Match(w);
        if (!match.Success)
            throw new InvalidDataException($"The Gerber file has a malformed aperture definition \"{w}\".");
        if (_apertures.Count + _blockApertures.Count >= MaxApertures)
            throw new InvalidDataException($"The Gerber file defines more than {MaxApertures} apertures.");
        var code = Count(match.Groups[1].Value, w);
        var template = match.Groups[2].Value;
        double[] p = match.Groups[3].Success && match.Groups[3].Value.Length > 0
            ? [.. match.Groups[3].Value.Split('X').Select(v => Number(v, w))]
            : [];
        double P(int i) => i < p.Length ? p[i] : 0;
        var u = _unit;
        var id = $"a{code}";

        SKPath outline;
        string svg;
        double circle = 0;
        List<(double X, double Y)>? convex = null;
        // A hole: its diameter, or (before 2016) the width and height of a rectangular one.
        double hole, holeHeight;
        switch (template)
        {
            case "C":
                circle = Math.Max(0, P(0) * u);
                (hole, holeHeight) = (P(1) * u, P(2) * u);
                outline = new SKPath();
                if (circle > 0)
                    outline.AddCircle(0, 0, (float)(circle / 2));
                svg = $"<circle id=\"{id}\" r=\"{Svg.N(circle / 2)}\" stroke=\"none\"/>";
                break;
            case "R":
            case "O":
            {
                double width = Math.Max(0, P(0) * u), height = Math.Max(0, P(1) * u);
                (hole, holeHeight) = (P(2) * u, P(3) * u);
                outline = new SKPath();
                var rect = new SKRect((float)(-width / 2), (float)(-height / 2), (float)(width / 2), (float)(height / 2));
                var radius = template == "O" ? Math.Min(width, height) / 2 : 0;
                if (width > 0 && height > 0)
                {
                    if (radius > 0)
                        outline.AddRoundRect(rect, (float)radius, (float)radius);
                    else
                        outline.AddRect(rect);
                }
                svg = $"<rect id=\"{id}\" x=\"{Svg.N(-width / 2)}\" y=\"{Svg.N(-height / 2)}\" width=\"{Svg.N(width)}\" height=\"{Svg.N(height)}\""
                      + (radius > 0 ? $" rx=\"{Svg.N(radius)}\"" : "") + " stroke=\"none\"/>";
                convex = template == "R"
                    ? [(-width / 2, -height / 2), (width / 2, -height / 2), (width / 2, height / 2), (-width / 2, height / 2)]
                    : Obround(width, height);
                break;
            }
            case "P":
            {
                var diameter = Math.Max(0, P(0) * u);
                var vertices = (int)P(1);
                if (vertices is < 3 or > 12)
                    throw new InvalidDataException($"The Gerber polygon aperture D{code} has {vertices} vertices (3 to 12 are allowed).");
                (hole, holeHeight) = (P(3) * u, P(4) * u);
                convex = GerberMacro.RegularPolygon(0, 0, diameter, vertices, P(2));
                outline = diameter > 0 ? GerberMacro.Polygon(convex) : new SKPath();
                svg = "";
                break;
            }
            default:
                if (!_macros.TryGetValue(template, out var macro))
                    throw new InvalidDataException($"The Gerber aperture D{code} uses an undefined aperture macro \"{template}\".");
                _macroPrimitives += macro.StatementCount;
                if (_macroPrimitives > MaxMacroPrimitives)
                    throw new InvalidDataException($"The Gerber file's aperture macros have more than {MaxMacroPrimitives} primitives in all.");
                outline = macro.Evaluate(p);
                outline.Transform(SKMatrix.CreateScale((float)u, (float)u));
                (hole, holeHeight) = (0, 0);
                svg = "";
                break;
        }
        if (hole > 0 && !outline.IsEmpty)
        {
            using var cut = new SKPath();
            if (holeHeight > 0)
                cut.AddRect(new SKRect((float)(-hole / 2), (float)(-holeHeight / 2), (float)(hole / 2), (float)(holeHeight / 2)));
            else
                cut.AddCircle(0, 0, (float)(hole / 2));
            outline.Op(cut, SKPathOp.Difference, outline);
            svg = "";
        }
        if (svg.Length == 0)
        {
            var d = new StringBuilder();
            Svg.AppendPathData(d, outline);
            svg = $"<path id=\"{id}\" d=\"{d}\" stroke=\"none\""
                  + (outline.FillType == SKPathFillType.EvenOdd ? " fill-rule=\"evenodd\"" : "") + "/>";
        }
        _blockApertures.Remove(code);
        _apertures[code] = new GerberAperture(code, outline, svg, circle, convex);
    }

    private static List<(double X, double Y)> Obround(double width, double height)
    {
        // Two half circles at the ends of the long side, 9 points each: close enough for the hull of a swept draw.
        var points = new List<(double, double)>();
        var r = Math.Min(width, height) / 2;
        double dx = width / 2 - r, dy = height / 2 - r;
        for (var half = 0; half < 2; half++)
        {
            double cx = half == 0 ? dx : -dx, cy = half == 0 ? dy : -dy;
            var start = width >= height ? (half == 0 ? -90 : 90) : (half == 0 ? 0 : 180);
            for (var k = 0; k <= 8; k++)
            {
                var a = (start + 180.0 * k / 8) * Math.PI / 180;
                points.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
            }
        }
        return points;
    }

    private void ApertureBlock(string w)
    {
        _openStroke = null;
        if (w == "AB")
        {
            if (_frames.Count == 0 || _frames.Peek().ApertureCode < 0)
                throw new InvalidDataException("The Gerber file closes an aperture block that was never opened.");
            CloseFrame();
            return;
        }
        var match = BlockStart().Match(w);
        if (!match.Success)
            throw new InvalidDataException($"The Gerber file has a malformed aperture block \"{w}\".");
        OpenFrame(new Frame(new GerberBlock(_nextBlockId++), Count(match.Groups[1].Value, w), 1, 1, 0, 0));
    }

    private void StepAndRepeat(string w)
    {
        _openStroke = null;
        if (_frames.Count > 0 && _frames.Peek().ApertureCode < 0)
            CloseFrame(); // a new SR statement ends the previous one
        if (w == "SR")
            return;
        var match = StepRepeat().Match(w);
        if (!match.Success)
            throw new InvalidDataException($"The Gerber file has a malformed step and repeat \"{w}\".");
        var x = Count(match.Groups[1].Value, w);
        var y = Count(match.Groups[2].Value, w);
        if (x < 1 || y < 1 || (long)x * y > MaxRepeats)
            throw new InvalidDataException($"The Gerber step and repeat \"{w}\" makes more than {MaxRepeats} copies.");
        if (x == 1 && y == 1)
            return;
        var stepX = match.Groups[3].Success ? Number(match.Groups[3].Value, w) * _unit : 0;
        var stepY = match.Groups[4].Success ? Number(match.Groups[4].Value, w) * _unit : 0;
        OpenFrame(new Frame(new GerberBlock(_nextBlockId++), -1, x, y, stepX, stepY));
    }

    private void OpenFrame(Frame frame)
    {
        if (_frames.Count >= MaxBlockDepth)
            throw new InvalidDataException($"The Gerber file nests blocks more than {MaxBlockDepth} deep.");
        _frames.Push(frame);
    }

    private void CloseFrame()
    {
        _openStroke = null;
        var frame = _frames.Pop();
        frame.Block.Seal();
        _image.Blocks.Add(frame.Block);
        if (frame.ApertureCode >= 0)
        {
            _apertures.Remove(frame.ApertureCode);
            _blockApertures[frame.ApertureCode] = frame.Block;
            return;
        }
        for (var i = 0; i < frame.RepeatX; i++)
            for (var j = 0; j < frame.RepeatY; j++)
                foreach (var run in frame.Block.Runs)
                    Add(new GerberBlockFlash(run.Clear, run, new GerberPoint(i * frame.StepX, j * frame.StepY), GerberPlacement.Identity));
    }

    // ───────────────────────── Word commands ─────────────────────────

    private void Word(string w)
    {
        if (w.StartsWith("G04", StringComparison.Ordinal) || w.StartsWith("G4 ", StringComparison.Ordinal))
        {
            // KiCad writes X2 attributes as comments unless told to use extended attributes.
            var comment = w[3..].Trim();
            if (comment.StartsWith("#@! TF", StringComparison.Ordinal))
                FileAttribute(comment[6..]);
            return;
        }

        string? x = null, y = null, i = null, j = null;
        var operation = 0;
        var at = 0;
        while (at < w.Length)
        {
            var letter = w[at++];
            var start = at;
            while (at < w.Length && (char.IsAsciiDigit(w[at]) || w[at] is '+' or '-' or '.'))
                at++;
            var value = w[start..at];
            switch (letter)
            {
                case 'X': x = value; break;
                case 'Y': y = value; break;
                case 'I': i = value; break;
                case 'J': j = value; break;
                case 'G': GCode(Code(value, w)); break;
                case 'M':
                    if (Code(value, w) is 0 or 2)
                        _ended = true;
                    break;
                case 'D':
                    var d = Code(value, w);
                    if (d >= 10)
                        SelectAperture(d);
                    else if (d is 1 or 2 or 3)
                        operation = d;
                    break;
                case ' ':
                    break;
                case 'N': // old line numbers
                    break;
                default:
                    return; // something this reader doesn't know: skip the whole word
            }
        }

        if (x is null && y is null && operation == 0)
            return;
        if (operation == 0)
            operation = _lastOperation; // deprecated: coordinates alone repeat the last operation
        if (operation == 0)
            return;
        _lastOperation = operation;

        var target = new GerberPoint(
            x is null ? _current.X : Coordinate(x, _incremental ? _current.X : 0, w),
            y is null ? _current.Y : Coordinate(y, _incremental ? _current.Y : 0, w));
        var offset = new GerberPoint(i is null ? 0 : Coordinate(i, 0, w), j is null ? 0 : Coordinate(j, 0, w));
        switch (operation)
        {
            case 1:
                Interpolate(target, offset);
                break;
            case 2:
                Move(target);
                break;
            case 3:
                _current = target;
                Flash();
                break;
        }
    }

    private void GCode(int g)
    {
        switch (g)
        {
            case 1 or 10 or 11 or 12: _interpolation = 1; break;
            case 2: _interpolation = 2; break;
            case 3: _interpolation = 3; break;
            case 36:
                _inRegion = true;
                _regionContours = [];
                _regionContour = null;
                _openStroke = null;
                break;
            case 37: EndRegion(); break;
            case 74: _multiQuadrant = false; break;
            case 75: _multiQuadrant = true; break;
            case 70: _unit = 25.4; break;
            case 71: _unit = 1; break;
            case 90: _incremental = false; break;
            case 91: _incremental = true; break;
        }
    }

    private void SelectAperture(int code)
    {
        _openStroke = null;
        _aperture = _apertures.GetValueOrDefault(code);
        _blockAperture = _aperture is null ? _blockApertures.GetValueOrDefault(code) : null;
        if (_aperture is null && _blockAperture is null)
            throw new InvalidDataException(_apertures.Count + _blockApertures.Count == 0
                ? "The Gerber file has no aperture definitions: it is RS-274-D, which needs a separate aperture list."
                : $"The Gerber file uses aperture D{code}, which it never defines.");
    }

    private void Move(GerberPoint to)
    {
        if (_inRegion)
        {
            CloseContour();
            _regionContour = new GerberContour(to);
        }
        _current = to;
    }

    private void Interpolate(GerberPoint to, GerberPoint offset)
    {
        var segment = _interpolation == 1 ? new GerberSegment(to) : Arc(_current, to, offset);
        var from = _current;
        _current = to;
        if (_inRegion)
        {
            _regionContour ??= new GerberContour(from);
            _regionContour.Segments.Add(segment);
            CountSegments(1);
            return;
        }
        if (_aperture is null)
        {
            if (_blockAperture is not null)
                return; // a block can only be flashed
            throw new InvalidDataException("The Gerber file draws before selecting an aperture.");
        }
        if (_aperture.IsEmpty)
            return; // zero-size apertures draw nothing

        var width = _aperture.CircleDiameter * _placement.Scale;
        if (width <= 0 && (segment.IsArc || _aperture.ConvexOutline is null))
        {
            // An arc with a rectangle, or a draw with a macro: not allowed, drawn as wide as the shape's short side.
            width = Math.Min(_aperture.Bounds.MaxX - _aperture.Bounds.MinX, _aperture.Bounds.MaxY - _aperture.Bounds.MinY) * _placement.Scale;
        }
        else if (width <= 0)
        {
            Add(Swept(from, to));
            return;
        }

        if (!segment.IsArc && from == to)
        {
            Add(Dot(from, width / 2)); // a zero-length draw is a round dot
            return;
        }
        if (_openStroke is { } open && open.Width == width && open.Clear == _clear && open.SegmentCount < MaxSegmentsPerStroke
            && Target.Count > 0 && ReferenceEquals(Target[^1], open))
        {
            if (open.Contours[^1].End != from)
                open.Contours.Add(new GerberContour(from));
            open.Contours[^1].Segments.Add(segment);
            open.SegmentCount++;
            CountSegments(1);
            return;
        }
        var stroke = new GerberStroke(_clear, width) { SegmentCount = 1 };
        stroke.Contours.Add(new GerberContour(from));
        stroke.Contours[0].Segments.Add(segment);
        Add(stroke);
        _openStroke = stroke;
    }

    /// <summary>The area a convex aperture (rectangle, obround, polygon) covers moving in a straight line.</summary>
    private GerberRegion Swept(GerberPoint from, GerberPoint to)
    {
        var corners = _aperture!.ConvexOutline!.Select(p => _placement.Apply(p.X, p.Y)).ToList();
        var points = corners.Select(p => new GerberPoint(from.X + p.X, from.Y + p.Y))
            .Concat(corners.Select(p => new GerberPoint(to.X + p.X, to.Y + p.Y))).ToList();
        var hull = ConvexHull(points);
        var contour = new GerberContour(hull[0]);
        foreach (var p in hull.Skip(1))
            contour.Segments.Add(new GerberSegment(p));
        contour.Segments.Add(new GerberSegment(hull[0]));
        return new GerberRegion(_clear, [contour]);
    }

    private static List<GerberPoint> ConvexHull(List<GerberPoint> points)
    {
        // Andrew's monotone chain, counter-clockwise.
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3)
            return sorted;
        var hull = new List<GerberPoint>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;

        static double Cross(GerberPoint o, GerberPoint a, GerberPoint b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }

    private GerberRegion Dot(GerberPoint at, double r)
    {
        var contour = new GerberContour(new GerberPoint(at.X + r, at.Y));
        contour.Segments.Add(new GerberSegment(contour.Start, IsArc: true, Center: at, CounterClockwise: true, FullCircle: true));
        return new GerberRegion(_clear, [contour]);
    }

    /// <summary>
    /// A circular segment. In multi-quadrant mode I and J are the signed offset of the centre from the start, and an
    /// arc that ends where it starts is a full circle. In single-quadrant mode they are unsigned and the centre is the
    /// one of the four candidates that gives an arc of at most 90° with the most equal radii.
    /// </summary>
    private GerberSegment Arc(GerberPoint from, GerberPoint to, GerberPoint offset)
    {
        var ccw = _interpolation == 3;
        if (_multiQuadrant)
        {
            var centre = new GerberPoint(from.X + offset.X, from.Y + offset.Y);
            var full = Math.Abs(from.X - to.X) < 1e-9 && Math.Abs(from.Y - to.Y) < 1e-9;
            return new GerberSegment(to, true, centre, ccw, full);
        }
        GerberSegment best = default;
        var bestScore = double.MaxValue;
        foreach (var (sx, sy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
        {
            var candidate = new GerberSegment(to, true, new GerberPoint(from.X + sx * Math.Abs(offset.X), from.Y + sy * Math.Abs(offset.Y)), ccw);
            var r0 = Math.Sqrt((from.X - candidate.Center.X) * (from.X - candidate.Center.X) + (from.Y - candidate.Center.Y) * (from.Y - candidate.Center.Y));
            var r1 = Math.Sqrt((to.X - candidate.Center.X) * (to.X - candidate.Center.X) + (to.Y - candidate.Center.Y) * (to.Y - candidate.Center.Y));
            var score = Math.Abs(r0 - r1) + (GerberContour.Sweep(from, candidate) <= Math.PI / 2 + 1e-6 ? 0 : 1e9);
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    private void Flash()
    {
        if (_inRegion)
            return; // not allowed in a region
        _openStroke = null;
        if (_blockAperture is { } block)
        {
            foreach (var run in block.Runs)
                Add(new GerberBlockFlash(run.Clear ^ _clear, run, _current, _placement));
            return;
        }
        if (_aperture is null)
            throw new InvalidDataException("The Gerber file flashes before selecting an aperture.");
        if (!_aperture.IsEmpty)
            Add(new GerberFlash(_clear, _aperture, _current, _placement));
    }

    private void CloseContour()
    {
        if (_regionContour is not { Segments.Count: > 0 } contour)
            return;
        if (contour.End != contour.Start)
            contour.Segments.Add(new GerberSegment(contour.Start)); // contours must be closed; close a careless one
        _regionContours.Add(contour);
        _regionContour = null;
    }

    private void EndRegion()
    {
        CloseContour();
        _inRegion = false;
        _openStroke = null;
        if (_regionContours.Count > 0)
            Add(new GerberRegion(_clear, _regionContours));
        _regionContours = [];
    }

    private void Add(GerberObject o)
    {
        if (++_objectCount > MaxObjects)
            throw new InvalidDataException($"The Gerber file has more than {MaxObjects} objects.");
        // A placed block costs one <use> in the file, and its whole contents when drawn.
        _segmentCount += o is GerberBlockFlash ? 1 : 0;
        CountSegments(o is GerberBlockFlash ? 0 : o.Weight);
        _drawnSegments += o is GerberBlockFlash ? o.Weight : 0;
        if (_drawnSegments > MaxDrawnSegments)
            throw new InvalidDataException($"The Gerber file draws more than {MaxDrawnSegments} segments once its blocks and copies are placed.");
        Target.Add(o);
        _openStroke = null;
    }

    private void CountSegments(long count)
    {
        _segmentCount += count;
        _drawnSegments += count;
        if (_segmentCount > MaxSegments)
            throw new InvalidDataException($"The Gerber file has more than {MaxSegments} segments.");
    }

    // ───────────────────────── Numbers ─────────────────────────

    /// <summary>A coordinate in millimetres: a decimal number, or digits in the FS format with zeros omitted.</summary>
    private double Coordinate(string s, double origin, string word)
    {
        double value;
        if (s.Contains('.'))
        {
            value = Number(s, word);
        }
        else
        {
            var negative = s.StartsWith('-');
            var digits = s.TrimStart('+', '-');
            if (digits.Length == 0 || digits.Length > 18 || !digits.All(char.IsAsciiDigit))
                throw new InvalidDataException($"The Gerber file has a malformed coordinate in \"{word}\".");
            if (_trailingZeros)
                digits = digits.PadRight(_integers + _decimals, '0');
            value = long.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, _decimals);
            if (negative)
                value = -value;
        }
        value = origin + value * _unit;
        if (!double.IsFinite(value) || Math.Abs(value) > MaxCoordinate)
            throw new InvalidDataException($"The Gerber file has a coordinate out of range in \"{word}\".");
        return value;
    }

    private static double Number(string s, string word) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : throw new InvalidDataException($"The Gerber file has a malformed number in \"{word}\".");

    /// <summary>A count or aperture number; too many digits is a damaged file, not an overflow.</summary>
    private static int Count(string s, string word) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidDataException($"The Gerber file has a number out of range in \"{word}\".");

    private static int Code(string s, string word) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidDataException($"The Gerber file has a malformed command \"{word}\".");

    [GeneratedRegex(@"X(\d)(\d)Y(\d)(\d)")]
    private static partial Regex FormatDigits();

    [GeneratedRegex(@"^ADD(\d+)([^,]+)(?:,(.*))?$")]
    private static partial Regex ApertureDefinition();

    [GeneratedRegex(@"^ABD(\d+)$")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^SRX(\d+)Y(\d+)(?:I([-+\d.]+))?(?:J([-+\d.]+))?$")]
    private static partial Regex StepRepeat();
}
