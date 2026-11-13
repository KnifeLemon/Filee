// A JSON Canvas board drawn as SVG, in the look of Obsidian's light theme, for the vector engine to turn into PDF, PNG
// and the other images. Groups are drawn first, as frames with their label above them; then the lines between cards,
// as curves leaving and reaching the sides of the cards with their arrow heads; then the cards in the file's order (its
// z-order): text cards with their Markdown, file cards with the note or picture beside the canvas (the file's name
// otherwise), link cards with their address, never fetched. Line labels come last, on top. The drawing is the size of
// the board plus a margin, in canvas pixels (CSS pixels, 96 dpi).

using Filee.Engines.Cad;

namespace Filee.Engines.Vector.Canvas;

internal static class CanvasRenderer
{
    private const double Margin = 48;
    private const double Radius = 8;
    private const double BorderWidth = 2;
    private const double Padding = 20;
    private const double TextSize = 16;
    private const double LabelSize = 14;
    private const double GroupLabelSize = 18;
    private const double EdgeWidth = 2.5;
    private const double ArrowLength = 14;
    private const double ArrowHalfWidth = 7;

    /// <summary>How strongly a coloured card or group is shaded with its colour.</summary>
    private const double CardTint = 0.08;
    private const double GroupTint = 0.06;

    /// <summary>Most tiles of a repeated group background (each a few dozen bytes: they share one picture).</summary>
    private const int MaxTiles = 10_000;

    /// <summary>Draws the canvas at <paramref name="path"/> as SVG text.</summary>
    public static string Render(string path, CancellationToken ct)
    {
        var canvas = JsonCanvas.Read(path, ct);
        return Render(canvas, new CanvasFiles(Path.GetDirectoryName(Path.GetFullPath(path))!), ct);
    }

    public static string Render(JsonCanvas canvas, CanvasFiles files, CancellationToken ct)
    {
        var edges = canvas.Edges.Select(Geometry).ToList();
        var (left, top, right, bottom) = Bounds(canvas, edges);
        double width = right - left + 2 * Margin, height = bottom - top + 2 * Margin;
        var svg = new CanvasSvg();
        var N = CanvasSvg.N;
        svg.Raw($"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{N(width)}" height="{N(height)}" viewBox="{N(left - Margin)} {N(top - Margin)} {N(width)} {N(height)}">""");
        svg.Rect(left - Margin, top - Margin, width, height, 0, CanvasColors.Background);

        foreach (var group in canvas.Groups)
        {
            ct.ThrowIfCancellationRequested();
            Group(svg, group, files, ct);
        }
        foreach (var edge in edges)
            Edge(svg, edge);
        foreach (var node in canvas.Cards)
        {
            ct.ThrowIfCancellationRequested();
            switch (node.Kind)
            {
                case CanvasNodeKind.Text:
                    TextCard(svg, node);
                    break;
                case CanvasNodeKind.File:
                    FileCard(svg, node, files, ct);
                    break;
                case CanvasNodeKind.Link:
                    LinkCard(svg, node);
                    break;
            }
        }
        foreach (var edge in edges.Where(e => e.Edge.Label is not null))
            EdgeLabel(svg, edge);
        svg.Raw("</svg>");
        return svg.ToString();
    }

    // ───────────────────────── Cards ─────────────────────────

    private static (string Fill, string Stroke) Colors(CanvasNode node, double tint)
    {
        var color = CanvasColors.Parse(node.Color);
        return color is { } c ? (CanvasColors.Tint(c, tint), CanvasColors.Hex(c)) : (CanvasColors.Background, CanvasColors.Border);
    }

    private static void Frame(CanvasSvg svg, CanvasNode node)
    {
        var (fill, stroke) = Colors(node, CardTint);
        svg.Rect(node.X, node.Y, node.Width, node.Height, Radius, fill, stroke, BorderWidth);
    }

    private static void TextCard(CanvasSvg svg, CanvasNode node)
    {
        Frame(svg, node);
        Content(svg, node, n => n.Markdown(node.Content, node.X + Padding, node.Width - 2 * Padding));
    }

    /// <summary>Lays out a card's content inside it, clipped to the card as Obsidian clips a card that overflows.</summary>
    private static void Content(CanvasSvg svg, CanvasNode node, Action<CanvasText> layout)
    {
        var inset = BorderWidth;
        svg.BeginClip(node.X + inset, node.Y + inset, node.Width - 2 * inset, node.Height - 2 * inset, Radius - inset / 2);
        var text = new CanvasText(svg, node.Y + Math.Min(Padding * 0.7, node.Height / 4), node.Bottom, TextSize);
        layout(text);
        svg.EndClip();
    }

    private static void FileCard(CanvasSvg svg, CanvasNode node, CanvasFiles files, CancellationToken ct)
    {
        var name = Path.GetFileName(node.Content.Replace('\\', '/'));
        var title = CanvasFiles.IsNote(name) ? name[..^3] : name;
        if (node.Subpath is { Length: > 1 } subpath)
            title += " > " + subpath.TrimStart('#').Replace("#", " > ", StringComparison.Ordinal);
        AboveLabel(svg, node.X, node.Y, node.Width, title, LabelSize, CanvasColors.Muted, false);

        var local = files.Find(node.Content);
        if (local is not null && CanvasFiles.IsPicture(local)
            && files.Picture(local, node.Width, node.Height, ct) is { } picture)
        {
            // A picture card is the picture, with rounded corners and the card's frame only when it has a colour.
            svg.BeginClip(node.X, node.Y, node.Width, node.Height, Radius);
            svg.Rect(node.X, node.Y, node.Width, node.Height, 0, CanvasColors.Background);
            svg.Picture(picture, node.X, node.Y, node.Width, node.Height, CanvasSvg.Fit.Contain);
            svg.EndClip();
            if (node.Color is not null)
                svg.Rect(node.X, node.Y, node.Width, node.Height, Radius, null, Colors(node, CardTint).Stroke, BorderWidth);
            return;
        }

        Frame(svg, node);
        if (local is not null && CanvasFiles.IsNote(local) && CanvasFiles.Note(local) is { } note)
        {
            Content(svg, node, n => n.Markdown(Section(note, node.Subpath), node.X + Padding, node.Width - 2 * Padding));
            return;
        }
        // Not beside the canvas, or a file a card can't show here (PDF, video, ...): its name and type, centred.
        var extension = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        var middle = node.CenterY;
        Content(svg, node, n =>
        {
            Centered(svg, node, Path.GetFileNameWithoutExtension(name), middle - (extension.Length > 0 ? 4 : -6), 16, CanvasColors.Text, true);
            if (extension.Length > 0)
                Centered(svg, node, extension, middle + 18, 12, CanvasColors.Muted, false);
        });
    }

    private static void LinkCard(CanvasSvg svg, CanvasNode node)
    {
        Frame(svg, node);
        var url = node.Content.Trim();
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : url;
        Content(svg, node, n =>
        {
            n.Plain(host, node.X + Padding, node.Width - 2 * Padding, bold: true);
            n.Plain(url, node.X + Padding, node.Width - 2 * Padding, size: 13, color: CanvasColors.Accent);
        });
    }

    private static void Group(CanvasSvg svg, CanvasNode group, CanvasFiles files, CancellationToken ct)
    {
        var color = CanvasColors.Parse(group.Color);
        var fill = color is { } c ? CanvasColors.Tint(c, GroupTint) : "#FAFAFA";
        var stroke = color is { } s ? CanvasColors.Hex(s) : CanvasColors.Border;
        svg.Rect(group.X, group.Y, group.Width, group.Height, Radius, fill, stroke, BorderWidth);
        if (files.Find(group.Background) is { } local && CanvasFiles.IsPicture(local)
            && files.Picture(local, group.Width, group.Height, ct) is { } picture)
        {
            svg.BeginClip(group.X + BorderWidth, group.Y + BorderWidth, group.Width - 2 * BorderWidth, group.Height - 2 * BorderWidth, Radius);
            if (group.BackgroundStyle == "repeat")
            {
                // Tiled at the picture's own size, as Obsidian repeats it, every tile drawing the one embedded copy;
                // a texture smaller than 16 px is tiled at 16 px, and at most MaxTiles tiles are drawn.
                double tileWidth = Math.Max(16, picture.NaturalWidth), tileHeight = Math.Max(16, picture.NaturalHeight);
                var tiles = 0;
                for (var y = group.Y; y < group.Bottom && tiles < MaxTiles; y += tileHeight)
                    for (var x = group.X; x < group.Right && tiles < MaxTiles; x += tileWidth, tiles++)
                        svg.Picture(picture, x, y, tileWidth, tileHeight);
            }
            else
            {
                svg.Picture(picture, group.X, group.Y, group.Width, group.Height,
                    group.BackgroundStyle == "ratio" ? CanvasSvg.Fit.Contain : CanvasSvg.Fit.Cover);
            }
            svg.EndClip();
        }
        if (group.Content.Length > 0)
            AboveLabel(svg, group.X, group.Y, group.Width, group.Content, GroupLabelSize,
                color is { } l ? CanvasColors.Darker(l) : CanvasColors.Muted, true);
    }

    /// <summary>A label just above a card or group's top left corner (a file's name, a group's label), on one line.</summary>
    private static void AboveLabel(CanvasSvg svg, double x, double y, double width, string text, double size, string color, bool bold)
    {
        var line = text.Replace('\n', ' ').Replace('\r', ' ');
        var face = CadFonts.Resolve(null, bold, false);
        var runs = CanvasFonts.Layout(line, face);
        var limit = Math.Max(width, 200);
        // Cut with an ellipsis where it would run past the card, as Obsidian cuts it.
        double advance = 0;
        var fitted = new List<GlyphRun>();
        foreach (var run in runs)
        {
            if ((advance + run.Advance) * size > limit)
                break;
            fitted.Add(run);
            advance += run.Advance;
        }
        if (fitted.Count < runs.Count)
        {
            var rest = runs[fitted.Count];
            var room = limit / size - advance;
            var letters = (int)Math.Floor(rest.Text.Length * Math.Max(0, room) / Math.Max(rest.Advance, 1e-6)) - 1;
            if (letters > 0)
                fitted.Add(new GlyphRun(rest.Text[..Math.Min(rest.Text.Length, letters)], rest.Typeface, rest.Advance * letters / rest.Text.Length));
            fitted.Add(new GlyphRun("…", face, 0.5));
        }
        var cursor = x + 2;
        foreach (var run in fitted)
        {
            svg.Text(cursor, y - size * 0.45, run.Text, run.Typeface, size, bold, false, color);
            cursor += run.Advance * size;
        }
    }

    private static void Centered(CanvasSvg svg, CanvasNode node, string text, double baseline, double size, string color, bool bold)
    {
        var face = CadFonts.Resolve(null, bold, false);
        var runs = CanvasFonts.Layout(text, face);
        var width = runs.Sum(r => r.Advance) * size;
        var x = node.CenterX - Math.Min(width, node.Width - 2 * Padding) / 2;
        foreach (var run in runs)
        {
            svg.Text(x, baseline, run.Text, run.Typeface, size, bold, false, color);
            x += run.Advance * size;
        }
    }

    /// <summary>The part of a note a file card shows: the heading its subpath names, or the note without its properties.</summary>
    private static string Section(string note, string? subpath)
    {
        var text = note.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.StartsWith("---\n", StringComparison.Ordinal) && text.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and > 0)
        {
            var after = text.IndexOf('\n', end + 4);
            text = after < 0 ? "" : text[(after + 1)..];
        }
        if (subpath is not { Length: > 1 } || subpath.StartsWith("#^", StringComparison.Ordinal))
            return text;
        var heading = subpath.TrimStart('#').Split('#')[^1].Trim();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            var level = trimmed.TakeWhile(c => c == '#').Count();
            if (level is 0 or > 6 || !trimmed[level..].Trim().Equals(heading, StringComparison.OrdinalIgnoreCase))
                continue;
            var stop = i + 1;
            while (stop < lines.Length && !(lines[stop].TrimStart() is var next && next.TakeWhile(c => c == '#').Count() is var l
                       && l > 0 && l <= level && next.Length > l && next[l] == ' '))
                stop++;
            return string.Join('\n', lines[i..stop]);
        }
        return text;
    }

    // ───────────────────────── Lines ─────────────────────────

    private sealed record EdgeGeometry(CanvasEdge Edge, (double X, double Y)[] Curve, (double X, double Y) Start, (double X, double Y) End,
        (double X, double Y) StartNormal, (double X, double Y) EndNormal);

    /// <summary>
    /// The curve of a line: from the middle of one side to the middle of another, leaving and reaching each side at right
    /// angles (a cubic Bézier, as Obsidian draws them). Sides the file leaves out are the facing ones.
    /// </summary>
    private static EdgeGeometry Geometry(CanvasEdge edge)
    {
        var (fromSide, toSide) = Sides(edge);
        var start = Anchor(edge.From, fromSide);
        var end = Anchor(edge.To, toSide);
        var n0 = Normal(fromSide);
        var n1 = Normal(toSide);
        // An arrow head takes the end of the line: the curve stops at its base.
        var p0 = edge.FromArrow ? Add(start, n0, ArrowLength) : start;
        var p3 = edge.ToArrow ? Add(end, n1, ArrowLength) : end;
        var distance = Math.Sqrt(Math.Pow(p3.X - p0.X, 2) + Math.Pow(p3.Y - p0.Y, 2));
        var reach = Math.Clamp(distance / 2, 30, 250);
        var curve = new[] { p0, Add(p0, n0, reach), Add(p3, n1, reach), p3 };
        return new EdgeGeometry(edge, curve, start, end, n0, n1);
    }

    private static (CanvasSide From, CanvasSide To) Sides(CanvasEdge edge)
    {
        var dx = edge.To.CenterX - edge.From.CenterX;
        var dy = edge.To.CenterY - edge.From.CenterY;
        var horizontal = Math.Abs(dx) >= Math.Abs(dy);
        var from = edge.FromSide != CanvasSide.Auto ? edge.FromSide
            : horizontal ? (dx >= 0 ? CanvasSide.Right : CanvasSide.Left) : (dy >= 0 ? CanvasSide.Bottom : CanvasSide.Top);
        var to = edge.ToSide != CanvasSide.Auto ? edge.ToSide
            : horizontal ? (dx >= 0 ? CanvasSide.Left : CanvasSide.Right) : (dy >= 0 ? CanvasSide.Top : CanvasSide.Bottom);
        return (from, to);
    }

    private static (double X, double Y) Anchor(CanvasNode node, CanvasSide side) => side switch
    {
        CanvasSide.Top => (node.CenterX, node.Y),
        CanvasSide.Bottom => (node.CenterX, node.Bottom),
        CanvasSide.Left => (node.X, node.CenterY),
        _ => (node.Right, node.CenterY),
    };

    private static (double X, double Y) Normal(CanvasSide side) => side switch
    {
        CanvasSide.Top => (0, -1),
        CanvasSide.Bottom => (0, 1),
        CanvasSide.Left => (-1, 0),
        _ => (1, 0),
    };

    private static (double X, double Y) Add((double X, double Y) point, (double X, double Y) direction, double length) =>
        (point.X + direction.X * length, point.Y + direction.Y * length);

    private static string EdgeColor(CanvasEdge edge) =>
        CanvasColors.Parse(edge.Color) is { } color ? CanvasColors.Hex(color) : CanvasColors.Line;

    private static void Edge(CanvasSvg svg, EdgeGeometry geometry)
    {
        var N = CanvasSvg.N;
        var c = geometry.Curve;
        var color = EdgeColor(geometry.Edge);
        svg.Path($"M{N(c[0].X)} {N(c[0].Y)} C{N(c[1].X)} {N(c[1].Y)} {N(c[2].X)} {N(c[2].Y)} {N(c[3].X)} {N(c[3].Y)}", null, color, EdgeWidth);
        if (geometry.Edge.ToArrow)
            Arrow(svg, geometry.End, geometry.EndNormal, color);
        if (geometry.Edge.FromArrow)
            Arrow(svg, geometry.Start, geometry.StartNormal, color);
    }

    /// <summary>An arrow head whose tip touches the card's side, pointing into it.</summary>
    private static void Arrow(CanvasSvg svg, (double X, double Y) tip, (double X, double Y) normal, string color)
    {
        var N = CanvasSvg.N;
        var basePoint = Add(tip, normal, ArrowLength);
        var side = (X: -normal.Y, Y: normal.X);
        var a = Add(basePoint, side, ArrowHalfWidth);
        var b = Add(basePoint, side, -ArrowHalfWidth);
        svg.Path($"M{N(tip.X)} {N(tip.Y)} L{N(a.X)} {N(a.Y)} L{N(b.X)} {N(b.Y)} Z", color, color, 1);
    }

    private static (double X, double Y) Point(EdgeGeometry geometry, double t)
    {
        var c = geometry.Curve;
        var u = 1 - t;
        return (u * u * u * c[0].X + 3 * u * u * t * c[1].X + 3 * u * t * t * c[2].X + t * t * t * c[3].X,
            u * u * u * c[0].Y + 3 * u * u * t * c[1].Y + 3 * u * t * t * c[2].Y + t * t * t * c[3].Y);
    }

    private const double EdgeLabelSize = 14;
    private const double EdgeLabelMaxWidth = 280;

    private static (double Width, double Height, List<(string Text, List<GlyphRun> Runs, double Width)> Lines) LabelLines(string label)
    {
        var face = CadFonts.Resolve(null, false, false);
        var lines = label.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Take(20)
            .Select(line =>
            {
                var runs = CanvasFonts.Layout(line, face);
                return (line, runs, Math.Min(EdgeLabelMaxWidth, runs.Sum(r => r.Advance) * EdgeLabelSize));
            }).ToList();
        return (lines.Max(l => l.Item3), lines.Count * EdgeLabelSize * 1.4, lines);
    }

    /// <summary>A line's label at the middle of the curve, on a patch of paper so the line doesn't run through it.</summary>
    private static void EdgeLabel(CanvasSvg svg, EdgeGeometry geometry)
    {
        var (width, height, lines) = LabelLines(geometry.Edge.Label!);
        var (cx, cy) = Point(geometry, 0.5);
        var pad = 6.0;
        svg.Rect(cx - width / 2 - pad, cy - height / 2 - pad / 2, width + 2 * pad, height + pad, 4, CanvasColors.Background);
        var y = cy - height / 2;
        foreach (var (_, runs, lineWidth) in lines)
        {
            var x = cx - lineWidth / 2;
            foreach (var run in runs)
            {
                svg.Text(x, y + EdgeLabelSize * 1.05, run.Text, run.Typeface, EdgeLabelSize, false, false, CanvasColors.Muted);
                x += run.Advance * EdgeLabelSize;
            }
            y += EdgeLabelSize * 1.4;
        }
    }

    // ───────────────────────── Size ─────────────────────────

    /// <summary>The box the drawing needs: cards, the labels above them, the lines and their labels.</summary>
    private static (double Left, double Top, double Right, double Bottom) Bounds(JsonCanvas canvas, List<EdgeGeometry> edges)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        void Include(double x, double y)
        {
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
        foreach (var node in canvas.Nodes)
        {
            Include(node.X, node.Y);
            Include(node.Right, node.Bottom);
            if (node.Kind == CanvasNodeKind.Group && node.Content.Length > 0)
                Include(node.X, node.Y - GroupLabelSize * 1.6);
            else if (node.Kind == CanvasNodeKind.File)
                Include(node.X, node.Y - LabelSize * 1.6);
        }
        foreach (var edge in edges)
        {
            for (var t = 0.0; t <= 1.0; t += 0.0625)
            {
                var (x, y) = Point(edge, t);
                Include(x, y);
            }
            if (edge.Edge.Label is { } label)
            {
                var (width, height, _) = LabelLines(label);
                var (cx, cy) = Point(edge, 0.5);
                Include(cx - width / 2 - 8, cy - height / 2 - 8);
                Include(cx + width / 2 + 8, cy + height / 2 + 8);
            }
        }
        return (left, top, right, bottom);
    }
}
