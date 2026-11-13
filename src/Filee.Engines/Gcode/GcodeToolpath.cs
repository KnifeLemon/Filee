// The moves of a G-code file: what a slicer (PrusaSlicer, Cura, OrcaSlicer, Bambu Studio, SuperSlicer …) writes for a 3D
// printer, or a CAM program (Fusion, FreeCAD, LinuxCNC …) for a CNC machine. Read as polylines in millimetres, each
// point with its height, for GcodeDrawing to draw.
//
// Read: G0 / G1 (moves), G2 / G3 (arcs by I J K centre offsets or by R, in the G17 / G18 / G19 plane, helical when the
// third axis moves too), G20 / G21 (inches, millimetres), G90 / G91 (absolute, relative), M82 / M83 (extruder absolute,
// relative), G92 (set position), G28 (home), modal moves (a line of coordinates goes on with the last G0-G3, as CAM
// programs write it), line numbers (N) and checksums (*), ";" and "( )" comments. A printer file is one that extrudes
// (E grows on a move): its extruding moves are "extrusion", every other move is travel. A file that never extrudes is
// CNC: G1 / G2 / G3 cut, G0 is a rapid (travel). Cutter compensation (G41 / G42), tool length (G43) and work offsets
// (G54 …) aren't applied: the programmed path is drawn. A line with any other G code (G4, G10, G29, G30, G38.x, G53,
// canned cycles G81 …) isn't a move, nor are macros and expressions (Klipper, RepRapFirmware meta commands).

using System.Globalization;
using System.Numerics;
using System.Text;

namespace Filee.Engines.Gcode;

internal static class GcodeToolpath
{
    /// <summary>Largest file read: a big multi-colour print is a few hundred MB.</summary>
    internal const long MaxFileBytes = 1L << 30;

    /// <summary>
    /// Points kept for drawing, after points closer than <see cref="Tolerance"/> are merged. A 300 MB print keeps about
    /// 6 million; a file past this is too detailed to draw as one picture.
    /// </summary>
    internal const int MaxPoints = 12_000_000;

    /// <summary>Points closer than this (mm) to the last point kept on a line are dropped, the line's end kept.</summary>
    private const float Tolerance = 0.02f;

    /// <summary>Characters of a line that are read; the rest of a longer line (a long comment) is skipped.</summary>
    internal const int MaxLineChars = 4096;

    /// <summary>Arcs are split in segments of at most this angle: a full circle is 72 segments.</summary>
    private const double ArcStepRadians = Math.PI / 36;

    /// <summary>A coordinate past this (mm) is not a machine's: a broken or hostile number.</summary>
    private const double MaxCoordinate = 1e6;

    /// <summary>What a polyline does.</summary>
    internal enum MoveKind
    {
        /// <summary>A travel move of a printer, or a CNC rapid (G0).</summary>
        Travel,
        /// <summary>Extrusion on a printer, or a cut (G1 / G2 / G3) on a CNC machine.</summary>
        Work,
    }

    /// <summary>A polyline of one kind of move, in millimetres (X, Y, Z).</summary>
    internal sealed record Line(MoveKind Kind, List<Vector3> Points);

    /// <summary>The toolpath of a file.</summary>
    /// <param name="Lines">The polylines in file order (the order they were made in).</param>
    /// <param name="IsPrinter">Whether the file extrudes (a 3D printer's) rather than cuts (a CNC machine's).</param>
    /// <param name="Layers">The layers of a print (layer change comments, else the heights extruded at); 0 for CNC.</param>
    /// <param name="NozzleDiameter">The nozzle the slicer says it sliced for (mm), when it says so.</param>
    internal sealed record Toolpath(List<Line> Lines, bool IsPrinter, int Layers, float? NozzleDiameter)
    {
        /// <summary>The work moves (extrusion, cuts): what the picture is framed on.</summary>
        public IEnumerable<Line> Work => Lines.Where(l => l.Kind == MoveKind.Work);
    }

    /// <summary>Reads the toolpath of a file.</summary>
    /// <exception cref="InvalidDataException">The file is not text G-code, holds no moves, or is too large.</exception>
    internal static Toolpath Read(string path, CancellationToken ct)
    {
        using var stream = Open(path);
        return Read(stream, ct);
    }

    /// <summary>Opens a G-code file for reading, once its size is checked against <see cref="MaxFileBytes"/>.</summary>
    /// <exception cref="InvalidDataException">The file is larger.</exception>
    internal static FileStream Open(string path)
    {
        var size = new FileInfo(path).Length;
        if (size > MaxFileBytes)
            throw new InvalidDataException($"The G-code file is {size / (1024 * 1024)} MB; Filee draws files up to {MaxFileBytes / (1024 * 1024)} MB.");
        return File.OpenRead(path);
    }

    /// <inheritdoc cref="Read(string, CancellationToken)"/>
    internal static Toolpath Read(Stream stream, CancellationToken ct)
    {
        var machine = new Machine();
        var count = 0;
        foreach (var line in Lines(stream))
        {
            if ((++count & 0xFFFF) == 0)
                ct.ThrowIfCancellationRequested();
            machine.Run(line);
        }
        return machine.Finish();
    }

    // ───────────────────────── Lines ─────────────────────────

    /// <summary>
    /// The lines of a text file, at most <see cref="MaxLineChars"/> characters each. Fails on binary data: binary G-code
    /// (.bgcode) and other files that aren't text.
    /// </summary>
    internal static IEnumerable<string> Lines(Stream stream)
    {
        var head = new byte[4];
        var read = stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        if (read == 4 && head.AsSpan().SequenceEqual("GCDE"u8))
            throw new InvalidDataException("This is binary G-code (.bgcode), not text G-code: rename it to .bgcode.");
        stream.Position = 0;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16, leaveOpen: true);
        var buffer = new char[1 << 16];
        var line = new StringBuilder(256);
        var skipping = false;
        long checkedChars = 0;
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                var c = buffer[i];
                if (c == '\n' || c == '\r')
                {
                    if (line.Length > 0)
                        yield return line.ToString();
                    line.Clear();
                    skipping = false;
                    continue;
                }
                // A NUL in the first 64 KB: not a text file. (Later NULs, as a broken SD card leaves, are skipped.)
                if (c == '\0' && checkedChars + i < 1 << 16)
                    throw new InvalidDataException("The file is not text G-code: it holds binary data.");
                if (skipping)
                    continue;
                if (line.Length >= MaxLineChars)
                {
                    skipping = true;
                    continue;
                }
                line.Append(c);
            }
            checkedChars += n;
        }
        if (line.Length > 0)
            yield return line.ToString();
    }

    /// <summary>
    /// The words of a line (a letter, upper case, and its number) before its checksum, and its comment, if any. A word
    /// whose number doesn't parse (a RepRapFirmware expression in braces) is left out, and the line ends at anything
    /// that isn't a word (M117's message, a quoted macro name). Null when the line is not a G-code block: a Klipper or
    /// RepRapFirmware command such as SET_FAN_SPEED, EXCLUDE_OBJECT_START or "if", whose first letter has no number.
    /// </summary>
    internal static List<(char Letter, double Value)>? Words(string line, out string? comment)
    {
        comment = null;
        var words = new List<(char, double)>();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == ';')
            {
                comment = line[(i + 1)..];
                break;
            }
            if (c == '(')
            {
                var close = line.IndexOf(')', i + 1);
                comment ??= close < 0 ? line[(i + 1)..] : line[(i + 1)..close];
                if (close < 0)
                    break;
                i = close + 1;
                continue;
            }
            if (c is ' ' or '\t' or '%')
            {
                i++;
                continue;
            }
            if (c == '*' || !char.IsAsciiLetter(c))
                break; // a checksum, or text that isn't G-code words
            i++;
            // "X 10": some CAM programs put a space between the letter and its number.
            while (i + 1 < line.Length && line[i] == ' ' && IsNumberChar(line[i + 1]))
                i++;
            var start = i;
            while (i < line.Length && IsNumberChar(line[i]))
                i++;
            if (i == start)
            {
                if (words.Count == 0)
                    return null;
                if (i < line.Length && line[i] == '{')
                {
                    var close = line.IndexOf('}', i);
                    i = close < 0 ? line.Length : close + 1;
                    continue;
                }
                break;
            }
            if (double.TryParse(line.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                words.Add((char.ToUpperInvariant(c), value));
        }
        return words;
    }

    private static bool IsNumberChar(char c) => char.IsAsciiDigit(c) || c is '.' or '-' or '+';

    // ───────────────────────── Machine ─────────────────────────

    /// <summary>The state of the machine while the file runs.</summary>
    private sealed class Machine
    {
        private readonly List<Line> _lines = [];
        /// <summary>Heights extruded at (0.01 mm), to count layers where the file has no layer comments.</summary>
        private readonly HashSet<int> _extrudedHeights = [];
        private const int MaxHeights = 100_000;
        private Line? _current;
        private int _points;
        private bool _relative;
        private bool _relativeE;
        private bool _extruderModeSet;
        private double _unit = 1;
        private int _motion = -1;
        private int _plane = 17;
        private bool _extruded;
        private int _layerComments;
        private int _firstLayerLine = -1;
        private float? _nozzle;
        private Vector3 _at;
        private double _e;
        private bool _knownX;
        private bool _knownY;

        /// <summary>Raw kinds before the file is known as a printer's or a CNC machine's.</summary>
        private enum Raw { Rapid, Feed, Extrude }

        private readonly List<Raw> _raw = [];

        public void Run(string text)
        {
            var words = Words(text, out var comment);
            if (comment is not null)
                Comment(comment.Trim());
            if (words is null || words.Count == 0)
                return;

            var motion = -1;
            bool coordinates = false, otherCommand = false;
            foreach (var (letter, value) in words)
            {
                switch (letter)
                {
                    case 'G' when (int)Math.Round(value * 10) is 0 or 10 or 20 or 30:
                        motion = (int)Math.Round(value);
                        break;
                    case 'M':
                        otherCommand = true;
                        // M82 / M83 decide the extruder's mode from then on. Marlin's G90 / G91 also set it, Klipper's
                        // don't; a slicer that writes M83 never relies on G90 to undo it, so after M82 / M83 they don't.
                        if ((int)value is 82 or 83)
                        {
                            _relativeE = (int)value == 83;
                            _extruderModeSet = true;
                        }
                        break;
                    case 'T':
                        otherCommand = true;
                        break;
                    case 'X' or 'Y' or 'Z' or 'I' or 'J' or 'K' or 'R':
                        coordinates = true;
                        break;
                }
            }

            // Other G codes of the line first: units, distance mode and plane apply to its move.
            foreach (var (letter, value) in words)
            {
                if (letter != 'G')
                    continue;
                var g = (int)Math.Round(value * 10);
                switch (g)
                {
                    case 0 or 10 or 20 or 30: break; // the motion itself (G0–G3), handled below
                    case 170 or 180 or 190: _plane = g / 10; break;
                    case 200: _unit = 25.4; break;
                    case 210: _unit = 1; break;
                    case 900 or 910:
                        _relative = g == 910;
                        if (!_extruderModeSet)
                            _relativeE = _relative;
                        break;
                    case 920: SetPosition(words); return;
                    case 280: Home(words); return;
                    case 800: _motion = -1; return;
                    // Modes that a move may share its line with (the programmed path is drawn: compensation and
                    // offsets aren't applied): cutter compensation, tool length, work offsets, path control, feed modes.
                    case 400 or 410 or 420 or 430 or 490 or >= 540 and <= 599 or 610 or 611 or 640 or 930 or 940:
                        break;
                    // Any other G code carries X / Y / Z words that are not a move to draw: dwell, offsets, bed
                    // levelling (G29), probing (G30, G38.x), machine coordinates (G53), canned cycles (G81 …).
                    default:
                        return;
                }
            }

            // A line of coordinates (also after G90 or G43 H1) goes on with the last move: modal, as CAM programs write.
            if (motion < 0 && coordinates && !otherCommand)
                motion = _motion;
            if (motion < 0)
                return;
            _motion = motion;
            Move(motion, words);
        }

        private void Comment(string comment)
        {
            // PrusaSlicer, OrcaSlicer, Bambu Studio: ";LAYER_CHANGE"; Cura: ";LAYER:12"; Simplify3D: "; layer 3, Z = 0.6".
            if (comment.StartsWith("LAYER_CHANGE", StringComparison.Ordinal) || comment.StartsWith("LAYER:", StringComparison.Ordinal)
                || comment.StartsWith("layer ", StringComparison.Ordinal) && comment.Contains("Z =", StringComparison.Ordinal))
            {
                if (_layerComments++ == 0)
                {
                    EndLine();
                    _firstLayerLine = _lines.Count;
                }
            }
            else if (_nozzle is null && comment.StartsWith("nozzle_diameter = ", StringComparison.Ordinal)
                && float.TryParse(comment.AsSpan(18).TrimEnd().ToString().Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var nozzle)
                && nozzle is > 0.05f and < 5)
                _nozzle = nozzle;
        }

        private double Number(List<(char Letter, double Value)> words, char letter, double fallback)
        {
            foreach (var (l, v) in words)
                if (l == letter)
                    return v;
            return fallback;
        }

        private static bool Has(List<(char Letter, double Value)> words, char letter) => words.Any(w => w.Letter == letter);

        private void SetPosition(List<(char Letter, double Value)> words)
        {
            EndLine();
            var any = words.Any(w => w.Letter is 'X' or 'Y' or 'Z' or 'E');
            _at = new Vector3(
                (float)(any ? Number(words, 'X', _at.X / _unit) * _unit : 0),
                (float)(any ? Number(words, 'Y', _at.Y / _unit) * _unit : 0),
                (float)(any ? Number(words, 'Z', _at.Z / _unit) * _unit : 0));
            _e = any ? Number(words, 'E', _e) : 0;
            _knownX |= !any || Has(words, 'X');
            _knownY |= !any || Has(words, 'Y');
        }

        private void Home(List<(char Letter, double Value)> words)
        {
            EndLine();
            var axes = words.Where(w => w.Letter is 'X' or 'Y' or 'Z').Select(w => w.Letter).ToHashSet();
            var all = axes.Count == 0;
            _at = new Vector3(all || axes.Contains('X') ? 0 : _at.X, all || axes.Contains('Y') ? 0 : _at.Y, all || axes.Contains('Z') ? 0 : _at.Z);
            // Home is wherever the endstops are, often off the bed: the move away from it is not drawn.
            _knownX &= !(all || axes.Contains('X'));
            _knownY &= !(all || axes.Contains('Y'));
        }

        private Vector3 Target(List<(char Letter, double Value)> words)
        {
            double Axis(char letter, float current)
            {
                if (!Has(words, letter))
                    return current;
                var value = Number(words, letter, 0) * _unit;
                return _relative ? current + value : value;
            }
            var target = new Vector3((float)Axis('X', _at.X), (float)Axis('Y', _at.Y), (float)Axis('Z', _at.Z));
            if (Math.Abs(target.X) > MaxCoordinate || Math.Abs(target.Y) > MaxCoordinate || Math.Abs(target.Z) > MaxCoordinate)
                throw new InvalidDataException("The G-code moves to a coordinate no machine reaches; the file is damaged.");
            return target;
        }

        private void Move(int motion, List<(char Letter, double Value)> words)
        {
            var target = Target(words);
            var extrudes = false;
            if (Has(words, 'E'))
            {
                var e = Number(words, 'E', 0);
                var delta = _relativeE ? e : e - _e;
                _e = _relativeE ? _e + e : e;
                extrudes = delta > 1e-6;
            }
            var raw = motion == 0 ? Raw.Rapid : extrudes ? Raw.Extrude : Raw.Feed;
            if (extrudes)
                _extruded = true;
            var knewXY = _knownX && _knownY;
            // An absolute coordinate makes its axis known; a relative one from an unknown position doesn't.
            _knownX |= !_relative && Has(words, 'X');
            _knownY |= !_relative && Has(words, 'Y');

            if (!knewXY)
            {
                // From an unknown position (power-up, after homing): nothing to draw yet.
                EndLine();
                _at = target;
                return;
            }

            if (motion is 2 or 3)
                Arc(motion == 2, words, target, raw);
            else
                LineTo(target, raw);
            if (raw == Raw.Extrude && target.Z == _at.Z && _extrudedHeights.Count < MaxHeights)
                _extrudedHeights.Add((int)MathF.Round(target.Z * 100));
            _at = target;
        }

        private void LineTo(Vector3 to, Raw raw)
        {
            if (_current is null || _raw[^1] != raw)
            {
                EndLine();
                _current = new Line(MoveKind.Work, [_at]);
                _lines.Add(_current);
                _raw.Add(raw);
                _points++;
            }
            var points = _current.Points;
            if (points[^1] == to)
                return;
            // Merge points closer than the tolerance: keep the newest as the line's end, so the line still ends where
            // the moves did.
            if (points.Count >= 2 && Vector3.DistanceSquared(points[^2], points[^1]) < Tolerance * Tolerance)
            {
                points[^1] = to;
                return;
            }
            if (++_points > MaxPoints)
                throw new InvalidDataException($"The G-code file has more than {MaxPoints:N0} moves to draw; it is too detailed to draw as one picture.");
            points.Add(to);
        }

        private void Arc(bool clockwise, List<(char Letter, double Value)> words, Vector3 target, Raw raw)
        {
            // The arc's plane: G17 XY (the usual one), G18 ZX, G19 YZ; the third axis moves linearly (a helix).
            var (a, b, c, ia, ib) = _plane switch
            {
                18 => (2, 0, 1, 'K', 'I'),
                19 => (1, 2, 0, 'J', 'K'),
                _ => (0, 1, 2, 'I', 'J'),
            };
            static double Get(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
            double sa = Get(_at, a), sb = Get(_at, b), ea = Get(target, a), eb = Get(target, b);
            double ca, cb;
            if (Has(words, 'R') && !Has(words, ia) && !Has(words, ib))
            {
                // Radius form: of the two centres, the one on the side the direction asks for; a negative R takes the
                // longer arc.
                var r = Number(words, 'R', 0) * _unit;
                double dx = ea - sa, dy = eb - sb, d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 1e-9 || Math.Abs(r) < d / 2 - 1e-6)
                {
                    LineTo(target, raw); // no such arc: a straight line, as most controllers treat it
                    return;
                }
                var h = Math.Sqrt(Math.Max(0, r * r - d * d / 4)) * ((clockwise ^ r < 0) ? -1 : 1);
                ca = sa + dx / 2 - h * dy / d;
                cb = sb + dy / 2 + h * dx / d;
            }
            else
            {
                ca = sa + Number(words, ia, 0) * _unit;
                cb = sb + Number(words, ib, 0) * _unit;
            }
            var radius = Math.Sqrt((sa - ca) * (sa - ca) + (sb - cb) * (sb - cb));
            if (!(radius >= 1e-6 && radius <= MaxCoordinate)) // NaN too (R1e300 over a short chord)
            {
                LineTo(target, raw);
                return;
            }
            var start = Math.Atan2(sb - cb, sa - ca);
            var end = Math.Atan2(eb - cb, ea - ca);
            var sweep = end - start;
            if (clockwise && sweep >= -1e-9)
                sweep -= 2 * Math.PI;
            else if (!clockwise && sweep <= 1e-9)
                sweep += 2 * Math.PI;
            // Same start and end: a full circle (the sweep above is then a full turn).
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / ArcStepRadians));
            double sc = Get(_at, c), ec = Get(target, c);
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                var angle = start + sweep * t;
                var p = new double[3];
                p[a] = ca + radius * Math.Cos(angle);
                p[b] = cb + radius * Math.Sin(angle);
                p[c] = sc + (ec - sc) * t;
                LineTo(i == steps ? target : new Vector3((float)p[0], (float)p[1], (float)p[2]), raw);
            }
        }

        private void EndLine() => _current = null;

        public Toolpath Finish()
        {
            // Lines of one point only marked where a kind of move started: dropped. In a print with layer comments, what
            // comes before the first layer is the printer's start G-code (a purge line at the edge of the bed, wiping)
            // and so is the travel from there to the first extrusion: not the part, so not drawn and not framed on. The
            // moves after the last extrusion (parking the head) are the end G-code: not drawn either.
            var first = Math.Max(0, _firstLayerLine);
            while (_extruded && _firstLayerLine >= 0 && first < _lines.Count && _raw[first] != Raw.Extrude)
                first++;
            var end = _lines.Count;
            while (_extruded && end > first && _raw[end - 1] != Raw.Extrude)
                end--;
            var lines = new List<Line>(_lines.Count);
            for (var i = first; i < end; i++)
            {
                if (_lines[i].Points.Count < 2)
                    continue;
                var kind = _extruded
                    ? _raw[i] == Raw.Extrude ? MoveKind.Work : MoveKind.Travel
                    : _raw[i] == Raw.Rapid ? MoveKind.Travel : MoveKind.Work;
                lines.Add(_lines[i] with { Kind = kind });
            }
            if (!lines.Any(l => l.Kind == MoveKind.Work))
                throw new InvalidDataException(lines.Count == 0
                    ? "No G-code moves found: the file has no G0 / G1 / G2 / G3 moves (it may not be G-code)."
                    : "The G-code file only moves, it neither extrudes nor cuts: nothing to draw.");
            var layers = _extruded ? _layerComments > 0 ? _layerComments : _extrudedHeights.Count : 0;
            return new Toolpath(lines, _extruded, layers, _nozzle);
        }
    }
}
