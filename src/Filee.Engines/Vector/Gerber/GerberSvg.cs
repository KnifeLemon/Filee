// Draws a Gerber layer as SVG at its real size (millimetres), for the vector engine to render to PDF, PNG and the rest.
//
// Look: KiCad's default colours on its dark board background, picked from the layer's role: top copper red, bottom
// copper blue, inner layers green, orange, …, silkscreen pale yellow, solder mask purple or cyan, paste grey, board
// outline light grey. The role comes from the X2 file function attribute (KiCad, Altium and others write it), else
// from the file name (KiCad's "-F_Cu.gbr", Protel's ".gtl" …). The dark background keeps every role legible (light
// silkscreen and outlines vanish on white), and it is what PCB designers see in KiCad and in Gerber viewers.
//
// Polarity: a clear object erases the dark objects before it. Each dark object that a later clear object overlaps is
// replaced by its outline minus those clear objects, computed with Skia's path operations, so the SVG stays truly
// transparent there and the PDF stays vector (Skia would rasterise an SVG mask in a PDF). The work is bounded; past
// the budget, clear objects are painted in the background colour instead, which looks the same on this background.
// Repeated shapes (apertures, aperture blocks, step-and-repeat copies) are defined once and placed with <use>.

using System.Text;
using SkiaSharp;

namespace Filee.Engines.Vector.Gerber;

/// <summary>Gerber layer → SVG.</summary>
internal static class GerberSvg
{
    /// <summary>KiCad's board background.</summary>
    public const string Background = "#001023";

    /// <summary>Space around the drawing, in millimetres.</summary>
    private const double Margin = 2;

    /// <summary>Most dark–clear overlaps resolved with path operations, and most outline points they may touch.</summary>
    private const int MaxOverlaps = 1_000_000;
    private const long MaxOperationWeight = 4_000_000;

    /// <summary>
    /// Most grid entries looked at while finding the overlaps. Entries that turn out not to overlap cost time too: many
    /// objects piled on one spot would otherwise keep the search going for hours.
    /// </summary>
    private const long MaxGridVisits = 20_000_000;

    /// <summary>Largest drawing, in millimetres (10 m): wider than any board or panel.</summary>
    private const double MaxExtent = 10_000;

    /// <summary>The layer roles, each with KiCad's default colour for it.</summary>
    internal enum Role
    {
        Unknown,
        CopperTop,
        CopperBottom,
        CopperInner,
        SilkscreenTop,
        SilkscreenBottom,
        MaskTop,
        MaskBottom,
        PasteTop,
        PasteBottom,
        Outline,
    }

    private static readonly string[] InnerCopper = ["#7FC87F", "#CE7D2C", "#4FCBCB", "#DB628B", "#A7A7A7", "#7D7DFF"];

    /// <summary>Reads <paramref name="data"/> and draws it; <paramref name="fileName"/> helps tell the layer's role.</summary>
    /// <param name="background">
    /// Whether to fill the page with the board background. Off on the way to DXF / DWG, where the full-page rectangle
    /// would become a closed outline around the board.
    /// </param>
    /// <exception cref="InvalidDataException">The file isn't a readable Gerber layer.</exception>
    public static string ToSvg(byte[] data, string fileName, bool background = true, CancellationToken ct = default)
    {
        var image = GerberReader.Read(data, ct);
        var (role, inner) = RoleOf(image.FileFunction, fileName);
        return Write(image, Color(role, inner), background, ct);
    }

    internal static string Color(Role role, int inner) => role switch
    {
        Role.CopperTop => "#C83434",
        Role.CopperBottom => "#4D7FC4",
        Role.CopperInner => InnerCopper[Math.Max(0, inner - 1) % InnerCopper.Length],
        Role.SilkscreenTop => "#F2EDA1",
        Role.SilkscreenBottom => "#E8B2A7",
        Role.MaskTop => "#D864FF",
        Role.MaskBottom => "#02FFEE",
        Role.PasteTop => "#B4A09A",
        Role.PasteBottom => "#00C2C2",
        _ => "#D0D2CD",
    };

    /// <summary>
    /// The layer's role, and the number of an inner copper layer (1 for the first inner layer), from the X2 file
    /// function ("Copper,L2,Inr", "Legend,Top", "Profile,NP" …) or else the file name.
    /// </summary>
    internal static (Role Role, int Inner) RoleOf(string? fileFunction, string fileName)
    {
        if (fileFunction is not null)
        {
            var parts = fileFunction.Split(',');
            var side = parts.Contains("Bot") ? 1 : parts.Contains("Top") ? 0 : -1;
            switch (parts[0])
            {
                case "Copper":
                    if (parts.Contains("Inr"))
                    {
                        var layer = parts.Length > 1 && parts[1].StartsWith('L') && int.TryParse(parts[1][1..], out var l) ? l - 1 : 1;
                        return (Role.CopperInner, Math.Max(1, layer));
                    }
                    return (side == 1 ? Role.CopperBottom : Role.CopperTop, 0);
                case "Legend":
                    return (side == 1 ? Role.SilkscreenBottom : Role.SilkscreenTop, 0);
                case "Soldermask":
                    return (side == 1 ? Role.MaskBottom : Role.MaskTop, 0);
                case "Paste":
                    return (side == 1 ? Role.PasteBottom : Role.PasteTop, 0);
                case "Profile":
                    return (Role.Outline, 0);
            }
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        // KiCad names the layer before the extension: board-F_Cu.gbr, board-F.SilkS.gto, board-Edge_Cuts.gm1 …
        foreach (var (key, role) in new[]
                 {
                     ("F_Cu", Role.CopperTop), ("B_Cu", Role.CopperBottom), ("F_SilkS", Role.SilkscreenTop), ("B_SilkS", Role.SilkscreenBottom),
                     ("F_Silkscreen", Role.SilkscreenTop), ("B_Silkscreen", Role.SilkscreenBottom), ("F_Mask", Role.MaskTop),
                     ("B_Mask", Role.MaskBottom), ("F_Paste", Role.PasteTop), ("B_Paste", Role.PasteBottom), ("Edge_Cuts", Role.Outline),
                 })
        {
            if (name.EndsWith(key, StringComparison.OrdinalIgnoreCase) || name.EndsWith(key.Replace('_', '.'), StringComparison.OrdinalIgnoreCase))
                return (role, 0);
        }
        var innerName = System.Text.RegularExpressions.Regex.Match(name, @"In(\d+)[_.]Cu$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (innerName.Success)
            return (Role.CopperInner, int.Parse(innerName.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

        return extension switch
        {
            "gtl" => (Role.CopperTop, 0),
            "gbl" => (Role.CopperBottom, 0),
            "gto" => (Role.SilkscreenTop, 0),
            "gbo" => (Role.SilkscreenBottom, 0),
            "gts" => (Role.MaskTop, 0),
            "gbs" => (Role.MaskBottom, 0),
            "gtp" => (Role.PasteTop, 0),
            "gbp" => (Role.PasteBottom, 0),
            "gko" or "gm1" => (Role.Outline, 0),
            _ => (Role.Unknown, 0),
        };
    }

    internal static string Write(GerberImage image, string color, bool background = true, CancellationToken ct = default)
    {
        var objects = image.Objects;
        var bounds = objects.Aggregate(GerberBounds.Empty, (b, o) => b.Union(o.Bounds));
        if (image.Negative && !bounds.IsEmpty)
        {
            // A negative image: the whole area is dark and the objects swap polarity.
            foreach (var o in objects)
                o.Clear = !o.Clear;
            var area = new GerberContour(new GerberPoint(bounds.MinX, bounds.MinY));
            foreach (var p in new[] { (bounds.MaxX, bounds.MinY), (bounds.MaxX, bounds.MaxY), (bounds.MinX, bounds.MaxY), (bounds.MinX, bounds.MinY) })
                area.Segments.Add(new GerberSegment(new GerberPoint(p.Item1, p.Item2)));
            objects = [new GerberRegion(false, [area]), .. objects];
        }

        // Clear objects before the first dark one erase nothing.
        var first = objects.FindIndex(o => !o.Clear);
        if (first < 0)
            throw new InvalidDataException("The Gerber file draws nothing dark (only clear objects).");
        objects = objects.Skip(first).ToList();
        bounds = objects.Where(o => !o.Clear).Aggregate(GerberBounds.Empty, (b, o) => b.Union(o.Bounds));
        // Every number was range-checked, but placements add up (a far flash, a large scale, step-and-repeat): the
        // page itself must stay a sensible size, or the raster route would ask for an enormous bitmap.
        if (!double.IsFinite(bounds.MinX) || !double.IsFinite(bounds.MaxX) || !double.IsFinite(bounds.MinY) || !double.IsFinite(bounds.MaxY)
            || bounds.MaxX - bounds.MinX > MaxExtent || bounds.MaxY - bounds.MinY > MaxExtent)
            throw new InvalidDataException($"The Gerber drawing is larger than {MaxExtent / 1000} m on a side.");

        var resolved = objects.Any(o => o.Clear) ? Subtract(objects, ct) : objects;

        bounds = bounds.Inflate(Margin);
        double width = bounds.MaxX - bounds.MinX, height = bounds.MaxY - bounds.MinY;
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"")
            .Append(Svg.N(width)).Append("mm\" height=\"").Append(Svg.N(height)).Append("mm\" viewBox=\"0 0 ")
            .Append(Svg.N(width)).Append(' ').Append(Svg.N(height)).Append("\">");
        if (background)
            svg.Append("<rect width=\"").Append(Svg.N(width)).Append("\" height=\"").Append(Svg.N(height)).Append("\" fill=\"").Append(Background).Append("\"/>");

        svg.Append("<defs>");
        foreach (var aperture in image.Apertures.Where(a => !a.IsEmpty))
            svg.Append(aperture.SvgElement);
        foreach (var block in image.Blocks)
        {
            foreach (var run in block.Runs)
            {
                svg.Append("<g id=\"").Append(run.SvgId).Append("\">");
                foreach (var o in run.Objects)
                    o.WriteSvg(svg);
                svg.Append("</g>");
            }
        }
        svg.Append("</defs>");

        // Gerber's Y axis points up: flip it about the drawing's top edge.
        svg.Append("<g transform=\"matrix(1 0 0 -1 ").Append(Svg.N(-bounds.MinX)).Append(' ').Append(Svg.N(bounds.MaxY)).Append(")\" fill=\"")
            .Append(color).Append("\" stroke=\"").Append(color).Append("\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
        var painting = false;
        for (var i = 0; i < resolved.Count; i++)
        {
            if ((i & 0xFFF) == 0)
                ct.ThrowIfCancellationRequested();
            var o = resolved[i];
            if (o.Clear != painting)
            {
                // Only past the budget: clear objects painted in the background colour, in order.
                svg.Append(o.Clear ? $"<g fill=\"{Background}\" stroke=\"{Background}\">" : "</g>");
                painting = o.Clear;
            }
            o.WriteSvg(svg);
        }
        if (painting)
            svg.Append("</g>");
        return svg.Append("</g></svg>").ToString();
    }

    /// <summary>
    /// The dark objects with every later clear object that overlaps them subtracted, the clear objects dropped. Only
    /// what stays clear is subtracted: a clear object minus the dark objects drawn after it. A hole that a later object
    /// fills again would otherwise meet it edge to edge, and anti-aliasing would show the background along that edge.
    /// Past the budget (or if Skia can't resolve an outline) the objects are returned as they are, clear ones included.
    /// </summary>
    private static List<GerberObject> Subtract(List<GerberObject> objects, CancellationToken ct)
    {
        List<int> clears = [], darks = [];
        for (var i = 0; i < objects.Count; i++)
            (objects[i].Clear ? clears : darks).Add(i);
        var clearGrid = new Grid(objects, clears);
        var darkGrid = new Grid(objects, darks);

        // Count the work before doing any of it. Looking for the overlaps costs time too (every grid entry touched,
        // overlap or not), so that is budgeted, and cancellation is checked, before any path operation runs.
        var overlaps = new Dictionary<int, List<int>>();
        var refills = new Dictionary<int, List<int>>();
        long pairs = 0, weight = 0;
        var weighed = new HashSet<int>();
        bool OverBudget(int index, List<int> hits)
        {
            pairs += hits.Count;
            foreach (var k in hits.Append(index))
                if (weighed.Add(k))
                    weight += objects[k].Weight;
            return pairs > MaxOverlaps || weight > MaxOperationWeight;
        }
        var search = 0;
        foreach (var (grid, from, into) in new[] { (clearGrid, darks, overlaps), (darkGrid, clears, refills) })
        {
            foreach (var i in from)
            {
                if ((search++ & 0x3FF) == 0)
                    ct.ThrowIfCancellationRequested();
                var hits = grid.After(i, objects[i].Bounds);
                if (clearGrid.Visits + darkGrid.Visits > MaxGridVisits)
                    return objects; // too many objects piled on one spot: paint clears in the background instead
                if (hits.Count > 0 && OverBudget(i, into[i] = hits))
                    return objects;
            }
        }

        var outlines = new Dictionary<int, SKPath?>();
        var result = new List<GerberObject>(objects.Count);
        try
        {
            // What clear object k really erases: its outline minus the dark objects after it; null when nothing.
            SKPath? Erased(int k)
            {
                if (outlines.TryGetValue(k, out var known))
                    return known;
                var path = objects[k].Outline();
                if (refills.TryGetValue(k, out var later))
                {
                    using var builder = new SKPath.OpBuilder();
                    builder.Add(path, SKPathOp.Union);
                    foreach (var j in later)
                    {
                        using var dark = objects[j].Outline();
                        builder.Add(dark, SKPathOp.Difference);
                    }
                    var left = new SKPath();
                    if (builder.Resolve(left))
                    {
                        path.Dispose();
                        path = left;
                    }
                    else
                    {
                        left.Dispose();
                    }
                }
                if (path.IsEmpty)
                {
                    path.Dispose();
                    path = null;
                }
                return outlines[k] = path;
            }

            foreach (var i in darks)
            {
                var o = objects[i];
                var erasing = overlaps.TryGetValue(i, out var hits)
                    ? hits.Select(k => Erased(k)).Where(p => p is not null && GerberBounds.Of(p.Bounds).Intersects(o.Bounds)).ToList()
                    : [];
                if (erasing.Count == 0)
                {
                    result.Add(o);
                    continue;
                }
                ct.ThrowIfCancellationRequested();
                using var builder = new SKPath.OpBuilder();
                using (var outline = o.Outline())
                    builder.Add(outline, SKPathOp.Union);
                foreach (var clear in erasing)
                    builder.Add(clear!, SKPathOp.Difference);
                var cut = new SKPath();
                if (!builder.Resolve(cut))
                    return objects;
                if (!cut.IsEmpty)
                    result.Add(new GerberCutOut(cut));
            }
        }
        finally
        {
            foreach (var path in outlines.Values)
                path?.Dispose();
        }
        return result;
    }

    /// <summary>A coarse grid over some of the objects, to find those after a given object that overlap a box.</summary>
    private sealed class Grid
    {
        private const int Size = 128;
        private const int MaxCellsPerObject = 1024;

        private readonly List<GerberObject> _objects;
        private readonly GerberBounds _area;
        private readonly double _cellWidth;
        private readonly double _cellHeight;
        private readonly List<int>?[] _cells = new List<int>?[Size * Size];
        private readonly List<int> _large = [];

        /// <summary>Grid entries looked at across every After call, overlap or not.</summary>
        public long Visits { get; private set; }

        public Grid(List<GerberObject> objects, List<int> members)
        {
            _objects = objects;
            _area = objects.Aggregate(GerberBounds.Empty, (b, o) => b.Union(o.Bounds));
            _cellWidth = Math.Max(1e-9, (_area.MaxX - _area.MinX) / Size);
            _cellHeight = Math.Max(1e-9, (_area.MaxY - _area.MinY) / Size);
            foreach (var k in members)
            {
                var (x0, y0, x1, y1) = Cells(objects[k].Bounds);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerObject)
                {
                    _large.Add(k);
                    continue;
                }
                for (var x = x0; x <= x1; x++)
                    for (var y = y0; y <= y1; y++)
                        (_cells[y * Size + x] ??= []).Add(k);
            }
        }

        private (int, int, int, int) Cells(GerberBounds b) =>
            (Cell(b.MinX, _area.MinX, _cellWidth), Cell(b.MinY, _area.MinY, _cellHeight), Cell(b.MaxX, _area.MinX, _cellWidth), Cell(b.MaxY, _area.MinY, _cellHeight));

        private static int Cell(double v, double origin, double size) => Math.Clamp((int)((v - origin) / size), 0, Size - 1);

        /// <summary>The members after index <paramref name="index"/> whose boxes overlap <paramref name="bounds"/>, in order.</summary>
        public List<int> After(int index, GerberBounds bounds)
        {
            var found = new HashSet<int>();
            var (x0, y0, x1, y1) = Cells(bounds);
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                    if (_cells[y * Size + x] is { } cell)
                        Scan(cell, index, bounds, found);
            Scan(_large, index, bounds, found);
            var list = found.ToList();
            list.Sort();
            return list;
        }

        /// <summary>Adds the members of <paramref name="sorted"/> after <paramref name="index"/> that overlap. The lists
        /// are filled in ascending order, so a binary search skips the k ≤ index without visiting them.</summary>
        private void Scan(List<int> sorted, int index, GerberBounds bounds, HashSet<int> found)
        {
            var start = FirstAfter(sorted, index);
            Visits += sorted.Count - start;
            for (var i = start; i < sorted.Count; i++)
                if (_objects[sorted[i]].Bounds.Intersects(bounds))
                    found.Add(sorted[i]);
        }

        /// <summary>The first position in the ascending list whose value is greater than <paramref name="index"/>.</summary>
        private static int FirstAfter(List<int> sorted, int index)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (sorted[mid] <= index)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }
    }
}
