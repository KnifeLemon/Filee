// Visio 2013+ drawings (.vsdx, [MS-VSDX]) → the slide model (Slides/SlideModel.cs), one slide per foreground page,
// drawn by the slide renderer to PDF, PNG and SVG through the drawing converter (Drawings/). Binary .vsd files (Visio
// 2003–2010, a compound file) are not read: FirstUnsupported names them, so their route keeps LibreOffice Draw.
//
// What is read:
// - Pages in order (pages/pages.xml), each page's size from its PageSheet (PageWidth / PageHeight, scaled by PageScale /
//   DrawingScale), the background page under it (BackPage, and that page's own background), its layers (shapes whose
//   layers are all hidden are left out) and shapes in drawing order. Background pages are not pages of their own.
//   The slide model has one size for a deck: pages of other sizes sit at the top left of the largest page's size.
// - Shapes: transform (PinX / PinY / Width / Height / LocPinX / LocPinY / Angle / FlipX / FlipY, inches with y up, turned
//   about the pin and mirrored about the local pin) resolved to the model's box, rotation and flips; geometry sections
//   (VisioGeometry); line (LineColor / LineWeight / LinePattern as dashes / LineCap / transparency, arrowheads as a
//   few basic kinds by BeginArrow / EndArrow and their size) and fill (FillForegnd, FillBkgnd and FillPattern: solid,
//   the 23 two-colour patterns as PatternFill, the gradient patterns as their foreground colour); text (VisioText) in
//   its text block (TxtPinX / TxtPinY / TxtWidth / TxtHeight / TxtLocPinX / TxtLocPinY / TxtAngle, margins, vertical
//   alignment) with Character and Paragraph rows; groups (their members in the group's local coordinates, the group's
//   own geometry behind them and its text in front); connectors as the geometry they carry; pictures (Foreign shapes,
//   ForeignData through a relationship: PNG, JPEG, GIF, BMP, TIFF, EMF, WMF) in their image rectangle.
// - Inheritance (VisioCellChain): cells, geometry and text from master shapes (Master / MasterShape, masters/*.xml)
//   and from the document's style sheets (document.xml StyleSheets) along their parents; colours by index from the
//   document's colour table; fonts by name, or by index from its face names. Group members a master holds that the
//   page doesn't list are drawn from the master.
// Left for later (VSDX-2): theme colours and effects stored as "Themed" (the inherited or default value is used),
// FillGradient sections, shadows, rounding, the full arrowhead table, layer subtleties (print flags, colours), text
// backgrounds, and formulas beyond stored values.
//
// Hostile input: parts through DocumentPackage (sizes, inflate ratio, XML depth and elements); DocumentBudget for
// shapes (each counted every time it is drawn, a background page's shapes once per page on it), paragraphs and group
// nesting; pages ≤ MaxPages; master and style inheritance ≤ 32 levels with loops cut; background pages ≤ 32 levels
// with loops cut; sections, rows, geometry rows in the drawing and NURBS / polyline points capped (VisioCells,
// VisioGeometry); pictures within MaxPartBytes and MaxPicturePixels; numbers clamped and finite.

using System.Globalization;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Visio;

internal sealed class VisioReader
{
    private static readonly XNamespace V = VisioSheet.V;
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Levels of master shapes, style sheets and background pages followed at most.</summary>
    public const int MaxInheritance = 32;

    /// <summary>The largest page side in points (PDF's user space limit, 200 inches).</summary>
    private const float MaxPagePoints = 14_400;

    /// <summary>The largest side of a text block in points.</summary>
    private const double MaxTextBlock = 100_000;

    /// <summary>The refusal of a binary .vsd when LibreOffice, which converts it, isn't installed.</summary>
    public const string NeedsLibreOffice = "error.vsd_needs_libreoffice";

    /// <summary>The first bytes of a compound file (binary .vsd).</summary>
    private static readonly byte[] CompoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Visio's fill patterns 2–24 as the slide model's pattern names.</summary>
    private static readonly string[] Patterns =
    [
        "pct50", "horz", "vert", "upDiag", "dnDiag", "cross", "diagCross", "ltHorz", "ltVert", "ltUpDiag", "ltDnDiag", "smGrid",
        "pct75", "narHorz", "narVert", "dkUpDiag", "dkDnDiag", "smCheck", "pct25", "dkHorz", "dkVert", "pct10", "pct5",
    ];

    /// <summary>Arrowhead sizes 0–6 (very small to colossal) in points.</summary>
    private static readonly float[] ArrowSizes = [3.5f, 5, 7, 10, 14, 20, 28];

    private readonly VisioPackage _package;
    private readonly DocumentBudget _budget;
    private readonly CancellationToken _cancellationToken;
    private readonly VisioGeometry _geometry = new();
    private readonly Dictionary<int, SKColor> _colors = [];
    private readonly Dictionary<string, string> _faces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VisioSheet> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Id, VisioCellKind Kind), IReadOnlyList<VisioSheet>> _styleChains = [];
    private readonly Dictionary<string, Master?> _masters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _masterParts = new(StringComparer.Ordinal);
    private readonly Dictionary<XElement, VisioSheet> _sheets = new(ReferenceEqualityComparer.Instance);
    private string? _documentPart;

    private VisioReader(VisioPackage package, CancellationToken cancellationToken)
    {
        _package = package;
        _budget = new DocumentBudget(package.Limits);
        _cancellationToken = cancellationToken;
    }

    /// <summary>Reads the drawing at <paramref name="path"/>.</summary>
    public static SlideDeck Read(string path, CancellationToken cancellationToken) => Read(path, null, cancellationToken);

    /// <summary>Reads the drawing at <paramref name="path"/> within <paramref name="limits"/>.</summary>
    public static SlideDeck Read(string path, DocumentLimits? limits, CancellationToken cancellationToken = default)
    {
        // Reached only when no other route is installed (FirstUnsupported sends .vsd to LibreOffice Draw).
        if (IsCompoundFile(path))
            throw new ConversionRefusedException(NeedsLibreOffice);
        using var package = VisioPackage.Open(path, limits);
        return new VisioReader(package, cancellationToken).ReadDrawing();
    }

    /// <summary>"binary Visio (VSD)" for a compound file (Visio 2003–2010), which the reader leaves to LibreOffice; else null.</summary>
    public static string? FirstUnsupported(string path) => IsCompoundFile(path) ? "binary Visio (VSD)" : null;

    private static bool IsCompoundFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            return stream.ReadAtLeast(head, 8, throwOnEndOfStream: false) == 8 && head.SequenceEqual(CompoundFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ───────────────────────── Document and pages ─────────────────────────

    /// <summary>A page of pages.xml with its sheet and part; sizes in drawing inches.</summary>
    private sealed record Page(string Id, VisioSheet Sheet, string? Part, bool Background, string? BackPage, double Width, double Height, double Scale)
    {
        /// <summary>Points per drawing inch.</summary>
        public double Unit => 72 * Scale;
    }

    private SlideDeck ReadDrawing()
    {
        _documentPart = _package.MainPart ?? "visio/document.xml";
        var document = _package.Part(_documentPart);
        if (document is null || document.Name != V + "VisioDocument")
            throw new InvalidDataException("The file is not a Visio drawing (no visio/document.xml).");
        foreach (var entry in document.Element(V + "Colors")?.Elements(V + "ColorEntry") ?? [])
        {
            if (int.TryParse((string?)entry.Attribute("IX"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && VisioValues.Color((string?)entry.Attribute("RGB"), _colors) is { } color)
                _colors.TryAdd(index, color);
        }
        foreach (var face in document.Element(V + "FaceNames")?.Elements(V + "FaceName") ?? [])
        {
            if ((string?)face.Attribute("ID") is { } id && ((string?)face.Attribute("NameU") ?? (string?)face.Attribute("Name")) is { Length: > 0 } name)
                _faces.TryAdd(id, name);
        }
        foreach (var style in document.Element(V + "StyleSheets")?.Elements(V + "StyleSheet") ?? [])
        {
            if ((string?)style.Attribute("ID") is { } id)
                _styles.TryAdd(id, Sheet(style, _documentPart));
        }
        var mastersPart = _package.TargetOfType(_documentPart, "/masters");
        foreach (var master in _package.Part(mastersPart)?.Elements(V + "Master") ?? [])
        {
            if ((string?)master.Attribute("ID") is { } id)
                _masterParts.TryAdd(id, _package.Target(mastersPart!, (string?)master.Element(V + "Rel")?.Attribute(R + "id")));
        }

        var pagesPart = _package.TargetOfType(_documentPart, "/pages");
        var pages = new List<Page>();
        foreach (var element in _package.Part(pagesPart)?.Elements(V + "Page") ?? [])
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (pages.Count >= _package.Limits.MaxPages)
                throw DocumentLimits.Exceeded($"The drawing has more than {DocumentLimits.Count(_package.Limits.MaxPages)} pages.");
            var sheet = Sheet(element.Element(V + "PageSheet") ?? new XElement(V + "PageSheet"), pagesPart!);
            double Cell(string name, double fallback) =>
                sheet.Cells.TryGetValue(name, out var cell) && VisioValues.Number((string?)cell.Attribute("V")) is { } value ? value : fallback;
            var pageScale = Cell("PageScale", 1);
            var drawingScale = Cell("DrawingScale", 1);
            var scale = pageScale > 0 && drawingScale > 0 ? Math.Clamp(pageScale / drawingScale, 1e-4, 1e4) : 1;
            var width = Cell("PageWidth", 8.5);
            var height = Cell("PageHeight", 11);
            pages.Add(new Page((string?)element.Attribute("ID") ?? "", sheet, _package.Target(pagesPart!, (string?)element.Element(V + "Rel")?.Attribute(R + "id")),
                VisioValues.Flag((string?)element.Attribute("Background")) == true, (string?)element.Attribute("BackPage"),
                width > 0 ? width : 8.5, height > 0 ? height : 11, scale));
        }
        var shown = pages.Where(p => !p.Background).ToList();
        float Side(double inches, double unit) => (float)Math.Clamp(inches * unit, 1, MaxPagePoints);
        var deck = new SlideDeck
        {
            Width = shown.Count == 0 ? 612 : shown.Max(p => Side(p.Width, p.Unit)),
            Height = shown.Count == 0 ? 792 : shown.Max(p => Side(p.Height, p.Unit)),
        };
        var byId = new Dictionary<string, Page>(StringComparer.Ordinal);
        foreach (var page in pages)
            byId.TryAdd(page.Id, page);
        foreach (var page in shown)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var slide = new Slide();
            // Background pages, the farthest first.
            var backgrounds = new List<Page>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { page.Id };
            for (var back = page.BackPage; back is not null && byId.TryGetValue(back, out var background) && seen.Add(back); back = background.BackPage)
            {
                if (backgrounds.Count >= MaxInheritance)
                    throw DocumentLimits.Exceeded($"Background pages are nested more than {MaxInheritance} levels deep.");
                backgrounds.Add(background);
            }
            backgrounds.Reverse();
            foreach (var layer in backgrounds.Append(page))
                PageShapes(layer, slide.Elements);
            deck.Slides.Add(slide);
        }
        return deck;
    }

    /// <summary>The shapes of <paramref name="page"/> into <paramref name="into"/>, its top at the top of the deck.</summary>
    private void PageShapes(Page page, List<SlideElement> into)
    {
        if (page.Part is null || _package.Part(page.Part) is not { } contents)
            return;
        var hidden = new HashSet<int>();
        foreach (var row in page.Sheet.Section("Layer")?.Rows.Values ?? Enumerable.Empty<VisioRow>())
        {
            if (row.Cells.TryGetValue("Visible", out var visible) && VisioValues.Flag((string?)visible.Attribute("V")) == false)
                hidden.Add(row.Index);
        }
        // The page's top meets the deck's top: its y = height (inches, y up) is at 0.
        var context = new PageContext(page.Part, page.Unit, hidden);
        var frame = new Parent(0, page.Height);
        foreach (var shape in contents.Element(V + "Shapes")?.Elements(V + "Shape") ?? [])
        {
            if (Element(shape, page.Part, frame, context) is { } element)
                into.Add(element);
        }
    }

    /// <summary>What every shape of a page shares: the part, points per drawing inch and the hidden layers.</summary>
    private sealed record PageContext(string Part, double Unit, HashSet<int> HiddenLayers);

    /// <summary>A parent's coordinates: x from <paramref name="Left"/>, y down from <paramref name="Top"/> (inches, y up).</summary>
    private readonly record struct Parent(double Left, double Top);

    // ───────────────────────── Masters, sheets and styles ─────────────────────────

    /// <summary>A master's part: its shapes by id and its one top-level shape.</summary>
    private sealed class Master(string part, Dictionary<string, XElement> byId, XElement? top)
    {
        public string Part { get; } = part;
        public Dictionary<string, XElement> ById { get; } = byId;
        public XElement? Top { get; } = top;
    }

    private Master? MasterOf(string? id)
    {
        if (id is null)
            return null;
        if (_masters.TryGetValue(id, out var cached))
            return cached;
        Master? master = null;
        if (_masterParts.TryGetValue(id, out var part) && part is not null && _package.Part(part) is { } contents)
        {
            var shapes = contents.Element(V + "Shapes");
            var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var shape in shapes?.Descendants(V + "Shape") ?? [])
                if ((string?)shape.Attribute("ID") is { } shapeId)
                    byId.TryAdd(shapeId, shape);
            var top = shapes?.Elements(V + "Shape").Take(2).ToList();
            master = new Master(part, byId, top is { Count: 1 } ? top[0] : null);
        }
        _masters[id] = master;
        return master;
    }

    /// <summary>The master shape <paramref name="shape"/> inherits from, with its part; null when it has none.</summary>
    private (XElement Shape, string Part)? MasterShape(XElement shape)
    {
        var masterShapeId = (string?)shape.Attribute("MasterShape");
        var masterId = (string?)shape.Attribute("Master")
                       ?? (masterShapeId is null ? null : (string?)shape.Ancestors(V + "Shape").FirstOrDefault(a => a.Attribute("Master") is not null)?.Attribute("Master"));
        if (MasterOf(masterId) is not { } master)
            return null;
        var found = masterShapeId is not null ? master.ById.GetValueOrDefault(masterShapeId) : master.Top;
        return found is null ? null : (found, master.Part);
    }

    private VisioSheet Sheet(XElement element, string part)
    {
        if (!_sheets.TryGetValue(element, out var sheet))
            _sheets[element] = sheet = VisioSheet.Read(element, part);
        return sheet;
    }

    /// <summary>The sheets a shape's cells come from: the shape, its master shapes, and its style sheets by kind.</summary>
    private VisioCellChain Chain(XElement shape, string part)
    {
        var sheets = new List<VisioSheet> { Sheet(shape, part) };
        var visited = new HashSet<XElement>(ReferenceEqualityComparer.Instance) { shape };
        for (var next = MasterShape(shape); next is var (master, masterPart) && visited.Add(master); next = MasterShape(master))
        {
            if (sheets.Count > MaxInheritance)
                throw DocumentLimits.Exceeded($"Visio master shapes inherit from each other more than {MaxInheritance} levels deep.");
            sheets.Add(Sheet(master, masterPart));
        }
        var kinds = new[] { (VisioCellKind.Line, "LineStyle"), (VisioCellKind.Fill, "FillStyle"), (VisioCellKind.Text, "TextStyle") };
        var styles = new IReadOnlyList<VisioSheet>[kinds.Length];
        var styleFirst = new bool[kinds.Length];
        for (var i = 0; i < kinds.Length; i++)
        {
            var (kind, attribute) = kinds[i];
            var own = sheets[0].Attribute(attribute);
            var inherited = sheets.Skip(1).Select(s => s.Attribute(attribute)).FirstOrDefault(a => a is not null);
            styles[i] = StyleChain(own ?? inherited, kind, attribute);
            styleFirst[i] = own is not null && inherited is not null && own != inherited;
        }
        return new VisioCellChain(sheets, styles, styleFirst);
    }

    /// <summary>Style sheet <paramref name="id"/> and its parents of <paramref name="kind"/>, nearest first.</summary>
    private IReadOnlyList<VisioSheet> StyleChain(string? id, VisioCellKind kind, string attribute)
    {
        if (id is null)
            return [];
        if (_styleChains.TryGetValue((id, kind), out var cached))
            return cached;
        var chain = new List<VisioSheet>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var next = id; next is not null && seen.Add(next) && _styles.TryGetValue(next, out var style); next = style.Attribute(attribute))
        {
            if (chain.Count >= MaxInheritance)
                throw DocumentLimits.Exceeded($"Visio styles inherit from each other more than {MaxInheritance} levels deep.");
            chain.Add(style);
        }
        _styleChains[(id, kind)] = chain;
        return chain;
    }

    // ───────────────────────── Shapes ─────────────────────────

    /// <summary>A shape (or group) as an element of its parent; null when it draws nothing.</summary>
    private SlideElement? Element(XElement shape, string part, Parent parent, PageContext page)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _budget.Object();
        var chain = Chain(shape, part);
        var type = (string?)shape.Attribute("Type") ?? chain.Sheets.Skip(1).Select(s => s.Attribute("Type")).FirstOrDefault(t => t is not null);
        if (type == "Guide" || chain.Flag("NonPrinting") || OnHiddenLayers(chain, page.HiddenLayers))
            return null;

        var width = chain.Number("Width", 0);
        var height = chain.Number("Height", 0);
        (double X, double Y) Scale(int level)
        {
            var (w, h) = (chain.NumberFrom("Width", level), chain.NumberFrom("Height", level));
            return (w is { } mw && Math.Abs(mw) > 1e-12 ? width / mw : 1, h is { } mh && Math.Abs(mh) > 1e-12 ? height / mh : 1);
        }
        var frame = new VisioFrame(width, height, page.Unit, Scale);
        double Scaled(string name, bool x, double fallback)
        {
            if (chain.Find(name, VisioValues.IsNumber) is not var (value, level))
                return fallback;
            var number = VisioValues.Number(value)!.Value;
            return level > 0 ? number * (x ? Scale(level).X : Scale(level).Y) : number;
        }

        // The box's centre in the parent: the pin, plus the centre's offset from the local pin, mirrored and turned.
        var angle = chain.Number("Angle", 0);
        var (flipX, flipY) = (chain.Flag("FlipX"), chain.Flag("FlipY"));
        var (vx, vy) = (width / 2 - Scaled("LocPinX", true, width / 2), height / 2 - Scaled("LocPinY", false, height / 2));
        if (flipX)
            vx = -vx;
        if (flipY)
            vy = -vy;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var centreX = chain.Number("PinX", 0) + vx * cos - vy * sin;
        var centreY = chain.Number("PinY", 0) + vx * sin + vy * cos;
        var (x, y) = ((centreX - parent.Left) * page.Unit, (parent.Top - centreY) * page.Unit);
        var (boxWidth, boxHeight) = (frame.BoxWidth, frame.BoxHeight);
        var bounds = new SKRect((float)x - boxWidth / 2, (float)y - boxHeight / 2, (float)x + boxWidth / 2, (float)y + boxHeight / 2);
        var rotation = (float)(-angle * 180 / Math.PI % 360);

        var (children, childPart) = Members(shape, part, chain);
        if (children is not null)
        {
            using var level = _budget.Enter();
            var group = new SlideGroup
            {
                Bounds = bounds,
                Rotation = rotation,
                FlipHorizontal = flipX,
                FlipVertical = flipY,
                ChildBounds = new SKRect(0, 0, boxWidth, boxHeight),
            };
            var box = new SKRect(0, 0, boxWidth, boxHeight);
            if (Shape(chain, frame, type, box, withText: false) is { } own)
                group.Children.Add(own);
            var inner = new Parent(frame.Left, frame.Top);
            foreach (var child in children)
            {
                if (Element(child, childPart!, inner, page) is { } element)
                    group.Children.Add(element);
            }
            if (Shape(chain, frame, null, box, withText: true, withGeometry: false) is { } text)
                group.Children.Add(text);
            return group;
        }

        if (Shape(chain, frame, type, bounds, withText: true) is not { } result)
            return null;
        result.Rotation = rotation;
        result.FlipHorizontal = flipX;
        result.FlipVertical = flipY;
        return result;
    }

    /// <summary>A group's members: its own, else those of the first master shape that has some.</summary>
    private static (IEnumerable<XElement>? Members, string? Part) Members(XElement shape, string part, VisioCellChain chain)
    {
        if (shape.Element(V + "Shapes")?.Elements(V + "Shape").Any() == true)
            return (shape.Element(V + "Shapes")!.Elements(V + "Shape"), part);
        foreach (var sheet in chain.Sheets.Skip(1))
        {
            if (sheet.Element.Element(V + "Shapes")?.Elements(V + "Shape").Any() == true)
                return (sheet.Element.Element(V + "Shapes")!.Elements(V + "Shape"), sheet.Part);
        }
        return (null, null);
    }

    /// <summary>The shape is on layers and all of them are hidden.</summary>
    private static bool OnHiddenLayers(VisioCellChain chain, HashSet<int> hidden)
    {
        if (hidden.Count == 0 || chain.Text("LayerMember") is not { Length: > 0 } members)
            return false;
        var any = false;
        foreach (var member in members.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(member, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layer))
                continue;
            if (!hidden.Contains(layer))
                return false;
            any = true;
        }
        return any;
    }

    /// <summary>A shape's outlines, fill, line, picture and text in <paramref name="bounds"/>; null when it draws nothing.</summary>
    private SlideShape? Shape(VisioCellChain chain, VisioFrame frame, string? type, SKRect bounds, bool withText, bool withGeometry = true)
    {
        var shape = new SlideShape { Bounds = bounds };
        if (withGeometry && type == "Foreign" && Picture(chain, frame) is var (fill, path))
        {
            shape.Paths = [new SlidePath(path)];
            shape.Fill = fill;
            shape.IsPicture = true;
        }
        else if (withGeometry)
        {
            var paths = _geometry.Paths(chain, frame);
            if (paths.Count > 0)
            {
                shape.Paths = paths;
                shape.Fill = paths.Any(p => p.Fill != PathFill.None) ? Fill(chain) : null;
                shape.Line = paths.Any(p => p.Stroke) ? Line(chain) : null;
            }
        }
        if (withText)
            shape.Text = TextFrame(chain, frame);
        var draws = shape.Fill is not null || shape.Line is not null || shape.Text is { IsEmpty: false };
        if (!draws)
        {
            foreach (var outline in shape.Paths)
                outline.Path.Dispose();
            return null;
        }
        return shape;
    }

    // ───────────────────────── Fill and line ─────────────────────────

    private SKColor? Color(VisioCellChain chain, string name) =>
        chain.Find(name, v => VisioValues.Color(v, _colors) is not null) is { } found ? VisioValues.Color(found.Value, _colors) : null;

    private static SKColor Transparent(SKColor color, double transparency) =>
        color.WithAlpha((byte)Math.Round(255 * (1 - Math.Clamp(transparency, 0, 1))));

    private SlideFill? Fill(VisioCellChain chain)
    {
        var pattern = (int)Math.Clamp(chain.Number("FillPattern", 1), -1, 1000);
        if (pattern <= 0)
            return null;
        var foreground = Transparent(Color(chain, "FillForegnd") ?? SKColors.White, chain.Number("FillForegndTrans", 0));
        // Gradients (25–40) and Visio 2013 gradient fills are left for later: their first colour stands for them.
        if (pattern == 1 || pattern > Patterns.Length + 1)
            return new SolidFill(foreground);
        var background = Transparent(Color(chain, "FillBkgnd") ?? SKColors.White, chain.Number("FillBkgndTrans", 0));
        return new PatternFill(Patterns[pattern - 2], foreground, background);
    }

    private SlideLine? Line(VisioCellChain chain)
    {
        var pattern = (int)Math.Clamp(chain.Number("LinePattern", 1), -1, 1000);
        if (pattern <= 0)
            return null;
        var color = Transparent(Color(chain, "LineColor") ?? SKColors.Black, chain.Number("LineColorTrans", 0));
        var width = (float)Math.Clamp(chain.Number("LineWeight", 0.01) * 72, 0, 1000);
        float[]? dash = pattern switch
        {
            1 => null,
            2 => [4, 3],
            3 => [1, 2],
            4 => [4, 2, 1, 2],
            5 => [4, 2, 1, 2, 1, 2],
            6 => [8, 3],
            7 => [8, 3, 1, 3],
            8 => [8, 3, 1, 3, 1, 3],
            9 => [2, 2],
            10 => [1, 1],
            _ => [3, 2],
        };
        var cap = (int)chain.Number("LineCap", 0) switch
        {
            1 => SKStrokeCap.Butt,
            2 => SKStrokeCap.Square,
            _ => SKStrokeCap.Round,
        };
        return new SlideLine(new SolidFill(color), width, dash, cap, SKStrokeJoin.Round,
            Arrow(chain, "BeginArrow", "BeginArrowSize", width), Arrow(chain, "EndArrow", "EndArrowSize", width));
    }

    /// <summary>A few basic arrowheads for Visio's arrow numbers (the full table is left for later).</summary>
    private static LineEnd Arrow(VisioCellChain chain, string cell, string sizeCell, float width)
    {
        var kind = (int)Math.Clamp(chain.Number(cell, 0), 0, 1000) switch
        {
            0 => LineEndKind.None,
            1 => LineEndKind.Arrow,
            10 or 20 => LineEndKind.Oval,
            22 => LineEndKind.Diamond,
            _ => LineEndKind.Triangle,
        };
        if (kind == LineEndKind.None)
            return LineEnd.None;
        var size = ArrowSizes[(int)Math.Clamp(chain.Number(sizeCell, 2), 0, ArrowSizes.Length - 1)] / Math.Max(width, 1);
        return new LineEnd(kind, size, size);
    }

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>A Foreign shape's picture over its image rectangle; null when there is none Filee can draw.</summary>
    private (PictureFill Fill, SKPath Box)? Picture(VisioCellChain chain, VisioFrame frame)
    {
        var holder = chain.Sheets.FirstOrDefault(s => s.Element.Element(V + "ForeignData") is not null);
        var id = (string?)holder?.Element.Element(V + "ForeignData")?.Element(V + "Rel")?.Attribute(R + "id");
        if (holder is null || _package.Bytes(_package.Target(holder.Part, id)) is not { } data || !SlideImages.CanDraw(data, _package.Limits))
            return null;
        var (width, height) = (frame.Width, frame.Height);
        double Cell(string name, double fallback, bool x)
        {
            if (chain.Find(name, VisioValues.IsNumber) is not var (value, level))
                return fallback;
            var number = VisioValues.Number(value)!.Value;
            return level > 0 ? number * (x ? frame.Scale(level).X : frame.Scale(level).Y) : number;
        }
        var (left, bottom) = (Cell("ImgOffsetX", 0, true), Cell("ImgOffsetY", 0, false));
        var (imageWidth, imageHeight) = (Cell("ImgWidth", width, true), Cell("ImgHeight", height, false));
        static float Fraction(double part, double whole) => Math.Abs(whole) > 1e-12 ? (float)Math.Clamp(part / whole, -100, 100) : 0;
        var stretch = new SKRect(Fraction(left, width), Fraction(height - bottom - imageHeight, height),
            Fraction(width - left - imageWidth, width), Fraction(bottom, height));
        var box = new SKPath();
        box.AddRect(new SKRect(0, 0, frame.BoxWidth, frame.BoxHeight));
        return (new PictureFill(new SlideImage(data), SKRect.Empty, stretch), box);
    }

    // ───────────────────────── Text ─────────────────────────

    private SlideTextFrame? TextFrame(VisioCellChain chain, VisioFrame frame)
    {
        if (chain.Flag("HideText"))
            return null;
        var text = chain.Sheets.Select(s => s.Element.Element(V + "Text")).FirstOrDefault(t => t is not null);
        if (text is null)
            return null;
        // Markers naming rows that don't exist take row 0, so each row is resolved once however many markers there are.
        var characterRows = chain.RowIndexes("Character").ToHashSet();
        var paragraphRows = chain.RowIndexes("Paragraph").ToHashSet();
        var runs = new Dictionary<int, VisioRun>();
        var paragraphFormats = new Dictionary<int, VisioParagraph>();
        VisioRun RunAt(int index)
        {
            index = characterRows.Contains(index) ? index : 0;
            if (!runs.TryGetValue(index, out var run))
                runs[index] = run = Run(chain, index);
            return run;
        }
        VisioParagraph ParagraphAt(int index)
        {
            index = paragraphRows.Contains(index) ? index : 0;
            if (!paragraphFormats.TryGetValue(index, out var paragraph))
                paragraphFormats[index] = paragraph = Paragraph(chain, index);
            return paragraph;
        }
        var paragraphs = VisioText.Paragraphs(text, RunAt, ParagraphAt, index => chain.RowCell("Field", index, "Value", _ => true), _budget);
        if (paragraphs.Count == 0)
            return null;

        var (width, height) = (frame.Width, frame.Height);
        double Scaled(string name, bool x, double fallback)
        {
            if (chain.Find(name, VisioValues.IsNumber) is not var (value, level))
                return fallback;
            var number = VisioValues.Number(value)!.Value;
            return level > 0 ? number * (x ? frame.Scale(level).X : frame.Scale(level).Y) : number;
        }
        var textWidth = Scaled("TxtWidth", true, width);
        var textHeight = Scaled("TxtHeight", false, height);
        var angle = chain.Number("TxtAngle", 0);
        var (vx, vy) = (textWidth / 2 - Scaled("TxtLocPinX", true, textWidth / 2), textHeight / 2 - Scaled("TxtLocPinY", false, textHeight / 2));
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var centre = frame.Box(Scaled("TxtPinX", true, width / 2) + vx * cos - vy * sin, Scaled("TxtPinY", false, height / 2) + vx * sin + vy * cos);
        // A text block far larger than any page is cut to a size the layout handles.
        var (areaWidth, areaHeight) = ((float)Math.Min(Math.Abs(textWidth) * frame.Unit, MaxTextBlock), (float)Math.Min(Math.Abs(textHeight) * frame.Unit, MaxTextBlock));
        float Margin(string name) => (float)Math.Clamp(chain.Number(name, 4.0 / 72) * 72, 0, 10_000);
        var result = new SlideTextFrame
        {
            Area = new SKRect(centre.X - areaWidth / 2, centre.Y - areaHeight / 2, centre.X + areaWidth / 2, centre.Y + areaHeight / 2),
            InsetLeft = Margin("LeftMargin"),
            InsetRight = Margin("RightMargin"),
            InsetTop = Margin("TopMargin"),
            InsetBottom = Margin("BottomMargin"),
            Anchor = (int)chain.Number("VerticalAlign", 1) switch
            {
                0 => TextAnchor.Top,
                2 => TextAnchor.Bottom,
                _ => TextAnchor.Middle,
            },
            Direction = (int)chain.Number("TextDirection", 0) == 1 ? TextDirection.Down : TextDirection.Horizontal,
            Rotation = (float)(-angle * 180 / Math.PI % 360),
        };
        result.Paragraphs.AddRange(paragraphs);
        return result;
    }

    /// <summary>"#RRGGBB".</summary>
    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    /// <summary>Character row <paramref name="index"/> as a run format.</summary>
    private VisioRun Run(VisioCellChain chain, int index)
    {
        double Number(string name, double fallback) =>
            chain.RowCell("Character", index, name, VisioValues.IsNumber) is { } value ? VisioValues.Number(value)!.Value : fallback;
        var font = chain.RowCell("Character", index, "Font", v => FontName(v) is not null) is { } fontValue ? FontName(fontValue) : null;
        font ??= "Calibri";
        var color = chain.RowCell("Character", index, "Color", v => VisioValues.Color(v, _colors) is not null) is { } colorValue
            ? VisioValues.Color(colorValue, _colors)!.Value
            : SKColors.Black;
        var style = (int)Math.Clamp(Number("Style", 0), 0, 255);
        var size = (int)Math.Clamp(Math.Round(Number("Size", 1.0 / 6) * 7200), 100, 400_000);
        var position = (int)Number("Pos", 0);
        var capitals = (int)Number("Case", 0) == 1 || (style & 8) != 0;
        var format = new HCharFormat(
            Bold: (style & 1) != 0 ? true : null,
            Italic: (style & 2) != 0 ? true : null,
            Underline: (style & 4) != 0 || Number("DblUnderline", 0) != 0 ? true : null,
            Strike: Number("Strikethru", 0) != 0 ? true : null,
            Superscript: position == 1 ? true : null,
            Subscript: position == 2 ? true : null,
            Size: size,
            Font: font,
            EastAsianFont: font,
            Color: Hex(color),
            Layout: new HCharLayout(WordFonts: new HWordFonts(font, font, font, font)));
        return new VisioRun(format, capitals);
    }

    /// <summary>A Font cell's typeface: a name, or an index into the face names; null for a theme font or nothing.</summary>
    private string? FontName(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Equals("Themed", StringComparison.OrdinalIgnoreCase))
            return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return _faces.GetValueOrDefault(value);
        return value;
    }

    /// <summary>Paragraph row <paramref name="index"/> as a paragraph format.</summary>
    private static VisioParagraph Paragraph(VisioCellChain chain, int index)
    {
        double Number(string name, double fallback) =>
            chain.RowCell("Paragraph", index, name, VisioValues.IsNumber) is { } value ? VisioValues.Number(value)!.Value : fallback;
        static int Hundredths(double inches) => (int)Math.Clamp(Math.Round(inches * 7200), -10_000_000, 10_000_000);
        var align = (int)Number("HorzAlign", 1) switch
        {
            0 => HAlign.Left,
            2 => HAlign.Right,
            3 => HAlign.Justify,
            4 => HAlign.Distribute,
            _ => HAlign.Center,
        };
        // SpLine: positive is an exact height, negative a multiple of single spacing, 0 single.
        var spacing = Number("SpLine", 0);
        var rule = spacing > 0 ? HLineHeightRule.Exact : HLineHeightRule.Multiple;
        var value = spacing > 0 ? Math.Clamp(Hundredths(spacing), 1, 158_400) : spacing < 0 ? (int)Math.Clamp(Math.Round(-spacing * 240), 24, 24_000) : 240;
        var format = new HParaFormat(
            Align: align,
            Left: Hundredths(Number("IndLeft", 0)),
            Right: Hundredths(Number("IndRight", 0)),
            FirstLine: Hundredths(Number("IndFirst", 0)),
            Before: Math.Clamp(Hundredths(Number("SpBefore", 0)), 0, 158_400),
            After: Math.Clamp(Hundredths(Number("SpAfter", 0)), 0, 158_400),
            LineSpacing: new HLineSpacing(rule == HLineHeightRule.Exact ? HLineSpacingKind.Fixed : HLineSpacingKind.Percent,
                rule == HLineHeightRule.Exact ? value : value * 120 / 240),
            Layout: new HParaLayout(LineHeight: new HLineHeight(rule, value)));
        string? bullet = null;
        if (Number("Bullet", 0) > 0)
        {
            var text = chain.RowCell("Paragraph", index, "BulletStr", v => v.Length > 0);
            bullet = string.IsNullOrWhiteSpace(text) ? "•" : text;
        }
        return new VisioParagraph(format, bullet);
    }
}
