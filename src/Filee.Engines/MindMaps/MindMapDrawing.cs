// Mind maps drawn (#1543, #1004): FreeMind / Freeplane (.mm) and XMind (.xmind) maps read by MindMapReader
// (ReadForDrawing) and laid out here as the apps show them, a page per sheet sized to its map, for the drawing converter
// (Drawings/DrawingFormats: PDF, SVG, PNG). The outline routes (DOCX, Markdown, ...) keep MindMapReader's outline.
// A .mm that isn't XML is Objective-C++ source (the same extension): its text is laid out by the Filee renderer.
//
// - Layout: the central topic in the middle, its main topics on both sides (XMind's map structures and FreeMind's
//   POSITION; half on the right top to bottom, the rest on the left, bottom to top for XMind's clockwise map), all on
//   the right for XMind's logic / tree / org structures (left for the ".left" ones). Each topic's subtopics are stacked
//   beside it, centred on it, every subtree in a band of its own so no two topics overlap.
// - Topics: their text (rich text keeps its formatting) wrapped at a width per level, markers' symbols before it, an
//   XMind equation below it, a picture above it (XMind images, Freeplane external pictures); boxes rounded, rectangular,
//   elliptic, diamond, hexagon, parallelogram, stadium, underlined (FreeMind's fork) or none, as the file says.
//   Colours, fonts, line kinds and widths come from the file (topic styles and the sheet's theme in XMind, node
//   attributes, font and edge in FreeMind); what it leaves out is the drawing's: a colour per main branch, a tint of it
//   for the main topics' boxes, subtopics underlined in their branch's colour.
// - Branch lines: curves (or straight lines and elbows) from a topic to each subtopic, in the branch's colour; a
//   folded topic (FreeMind FOLDED, XMind branch "folded") hides its subtopics and shows their count in a circle.
// - Relationships (XMind) and arrow links (FreeMind / Freeplane): curves between two topics with their arrowheads and
//   title, bowed away from the central topic (control points the file stores belong to the app's own layout and are
//   not used); boundaries (XMind) around a range of subtopics or a whole branch, with their title; summaries (XMind):
//   a brace beside their range and the summary topic beyond it; clouds (FreeMind / Freeplane) around a topic and its
//   subtopics; floating topics (XMind detached) where the file puts them, or in a row below the map when that
//   would cover it.
// - The page is the map's size plus a margin; a map wider or taller than PDF's 200 inches is scaled down to fit.
//
// Hostile input: MindMapReader's limits (XML / JSON depth and size, packages), then at most MaxTopics topics and
// MaxMarks relationships, boundaries and summaries drawn (DocumentLimits' error); the tree is walked with stacks of
// its own, not by recursion.

using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Drawings;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;
using Topic = Filee.Engines.Hwp.Hwpx.Markup.MindMapReader.Topic;
using TopicKind = Filee.Engines.Hwp.Hwpx.Markup.MindMapReader.TopicKind;

namespace Filee.Engines.MindMaps;

/// <summary>
/// A mind map opened for drawing: a page per sheet. A .mm that isn't XML is Objective-C++ source (the same extension):
/// its text laid out by the document renderer instead, as the outline routes read it.
/// </summary>
internal sealed class MindMapPages : IFixedPages
{
    private readonly Shaper? _shaper;
    private readonly SlideRenderer? _renderer;
    private readonly List<PageContent>? _text;

    private MindMapPages(List<SlideDeck> decks, Shaper shaper, FontResolver fonts, CancellationToken cancellationToken)
    {
        Decks = decks;
        _shaper = shaper;
        _renderer = new SlideRenderer(fonts, shaper, cancellationToken);
    }

    private MindMapPages(List<PageContent> text, string? title)
    {
        Decks = [];
        _text = text;
        Title = title;
    }

    /// <summary>Each sheet as a deck of one slide its map's size.</summary>
    internal IReadOnlyList<SlideDeck> Decks { get; }

    public int Count => _text?.Count ?? Decks.Count;

    public string? Title { get; private init; }

    public IReadOnlyList<LeftOutPart> LeftOut => [];

    /// <summary>Opens mind map <paramref name="path"/> of <paramref name="format"/> (mm or xmind).</summary>
    /// <exception cref="InvalidDataException">The file isn't a mind map.</exception>
    /// <exception cref="ConversionRefusedException">The map is too large, or saved with a password.</exception>
    public static MindMapPages Open(string path, string format, CancellationToken cancellationToken = default)
    {
        if (format == MindMapReader.FreeMind && !MindMapReader.IsFreeMindMap(path))
        {
            var limits = DocumentLimits.Default;
            if (new FileInfo(path).Length > limits.MaxPartBytes)
                throw DocumentLimits.TooLarge($"The file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
            var document = DocumentReaders.TextDocument(File.ReadAllBytes(path));
            return new MindMapPages(FileeRenderer.Pages(document, StyleResolver.FromTemplate(), cancellationToken), document.Title);
        }

        // Pictures are unpacked into a folder of their own while the map is read, and read into memory before it goes.
        var media = Path.Combine(Path.GetTempPath(), $"filee-mindmap-{Guid.NewGuid():N}");
        try
        {
            var map = MindMapReader.ReadForDrawing(path, format, media);
            cancellationToken.ThrowIfCancellationRequested();
            return Draw(map, cancellationToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(media))
                    Directory.Delete(media, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder.
            }
        }
    }

    /// <summary>Lays out and draws the sheets of <paramref name="map"/>.</summary>
    internal static MindMapPages Draw(MindMapReader.Drawn map, CancellationToken cancellationToken = default)
    {
        if (map.Sheets.Count == 0)
            throw new InvalidDataException("The mind map has no topics.");
        var shaper = new Shaper();
        try
        {
            var fonts = new FontResolver();
            var drawing = new MindMapDrawing(fonts, shaper, map, cancellationToken);
            var decks = map.Sheets.Select(drawing.Draw).ToList();
            return new MindMapPages(decks, shaper, fonts, cancellationToken) { Title = decks[0].Title };
        }
        catch
        {
            shaper.Dispose();
            throw;
        }
    }

    public PageContent Page(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _text?[index] ?? _renderer!.Render(Decks[index], Decks[index].Slides[0]);
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _shaper?.Dispose();
    }
}

/// <summary>Lays out the sheets of a mind map and draws each into the slide model.</summary>
internal sealed class MindMapDrawing(FontResolver fonts, Shaper shaper, MindMapReader.Drawn map, CancellationToken cancellationToken)
{
    /// <summary>Topics drawn at most, over all sheets.</summary>
    public const int MaxTopics = 20_000;

    /// <summary>Relationships, arrow links, boundaries, summaries and clouds drawn at most, over all sheets.</summary>
    public const int MaxMarks = 20_000;

    /// <summary>The longest page side, in points: PDF's 200 inches. A larger map is scaled down to it.</summary>
    public const float MaxPageSide = 14_400;

    private const float Margin = 30;
    private const float Unbounded = 100_000;
    private const float CloudPad = 12;
    private const float BoundaryPad = 9;
    private const float BoundaryTitle = 15;
    private const float MaxPicture = 180;

    /// <summary>Branch colours when the file gives none.</summary>
    private static readonly SKColor[] Palette =
    [
        new(0xE0, 0x5A, 0x47), new(0xF0, 0x9A, 0x2E), new(0x3D, 0xA5, 0x7A), new(0x3B, 0x82, 0xC4),
        new(0x8A, 0x5C, 0xC2), new(0xD1, 0x5B, 0x95), new(0x2E, 0xA3, 0xB0), new(0x8C, 0xA0, 0x32),
    ];

    private static readonly SKColor CentralFill = new(0x1F, 0x3B, 0x5C);
    private static readonly SKColor DarkText = new(0x22, 0x22, 0x22);
    private static readonly SKColor Grey = new(0x7A, 0x7A, 0x7A);

    private static readonly HParaFormat Centered = new(Align: HAlign.Center, Before: 0, After: 0,
        LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 120), Layout: new HParaLayout(LineHeight: new HLineHeight(HLineHeightRule.Multiple, 240)));

    private readonly ParagraphLayouter _layouter = new(fonts, shaper, storedLines: false);
    private int _topics;
    private int _marks;

    private enum Role
    {
        Central,
        Main,
        Sub,
        Floating,
        Summary,
        Callout,
    }

    private sealed class Node(Topic topic, Node? parent, Role role)
    {
        public Topic Topic { get; } = topic;
        public Node? Parent { get; } = parent;
        public Role Role { get; } = role;
        public int Side { get; set; } = 1;
        public List<Node> Children { get; } = [];

        /// <summary>Summary topics by id, from the topic's summary children.</summary>
        public Dictionary<string, Node> SummaryTopics { get; } = new(StringComparer.Ordinal);

        public int Hidden { get; set; }

        // Look, resolved.
        public SKColor Branch { get; set; }
        public SKColor Fill { get; set; }
        public SKColor TextColor { get; set; }
        public SKColor? Border { get; set; }
        public float BorderWidth { get; set; }
        public TopicShape Shape { get; set; }
        public BranchLine Line { get; set; }
        public float LineWidth { get; set; }

        /// <summary>Line width and kind the file sets for this branch (FreeMind's edge is inherited down it).</summary>
        public float? FileLineWidth { get; set; }
        public BranchLine FileLine { get; set; }

        // Content and size.
        public HParagraph? Text { get; set; }
        public SKSize TextSize { get; set; }
        public SlideImage? Picture { get; set; }
        public SKSize PictureSize { get; set; }
        public SKSize Size { get; set; }
        public SKPoint Pad { get; set; }

        // Layout.
        public float Band { get; set; }
        public float ExtraTop { get; set; }
        public float ExtraBottom { get; set; }
        public float[] Before { get; set; } = [];
        public float[] After { get; set; } = [];
        public SKRect Box { get; set; }
        public bool Placed { get; set; }

        /// <summary>The boxes of the topic and every subtopic placed under it.</summary>
        public SKRect Subtree { get; set; }

        /// <summary>The attached subtopics (Children before the callouts).</summary>
        public int Attached { get; set; }

        /// <summary>Its place in its parent's Children; -1 for summary and floating topics.</summary>
        public int Index { get; set; } = -1;
    }

    // ───────────────────────── A sheet ─────────────────────────

    public SlideDeck Draw(MindMapReader.Sheet sheet)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = new Dictionary<string, Node>(StringComparer.Ordinal);
        var hiddenLinks = new List<(Node From, MindLink Link)>();
        var (root, floating, all) = Tree(sheet, ids, hiddenLinks);
        Resolve(sheet, all);
        foreach (var node in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Measure(node);
        }
        Sides(sheet, root);
        Bands(all);

        // The central topic in the middle, its main topics beside it, then summaries and floating topics.
        root.Box = Centred(root.Size, SKPoint.Empty);
        root.Placed = true;
        var main = root.Children;
        PlaceChildren(root, 0, [.. main.Where(c => c.Side > 0)]);
        var left = main.Where(c => c.Side < 0).ToList();
        if (sheet.Structure?.Contains("clockwise", StringComparison.OrdinalIgnoreCase) == true
            && sheet.Structure.Contains("anticlockwise", StringComparison.OrdinalIgnoreCase) == false)
            left.Reverse();
        PlaceChildren(root, 0, left);
        Subtrees(all);
        var braces = Summaries(all);
        Floating(root, floating, all);
        // The floating topics' own summaries, now that they are in place.
        Subtrees(all);
        braces.AddRange(Summaries(all));
        Subtrees(all);

        var elements = new List<SlideElement>();
        var bounds = SKRect.Empty;
        void Add(SlideElement element)
        {
            elements.Add(element);
            bounds = bounds.IsEmpty ? element.Bounds : SKRect.Union(bounds, element.Bounds);
        }

        var placed = all.Where(n => n.Placed).ToList();
        foreach (var node in placed.Where(n => n.Topic.Cloud is not null))
            Add(Cloud(node));
        var titles = new List<SlideElement>();
        foreach (var node in placed)
            Boundaries(node, Add, titles);
        foreach (var node in placed)
            Branches(node, Add);
        foreach (var (path, color) in braces)
            Add(Stroke(path, new SlideLine(new SolidFill(color), 1.5f, Cap: SKStrokeCap.Round, Join: SKStrokeJoin.Round)));
        foreach (var node in placed)
            DrawTopic(node, Add);
        titles.ForEach(Add);
        Links(sheet, root, ids, all, hiddenLinks, Add);

        bounds.Inflate(Margin, Margin);
        var scale = Math.Min(1, MaxPageSide / Math.Max(bounds.Width, bounds.Height));
        var page = new SKRect(0, 0, MathF.Max(1, bounds.Width * scale), MathF.Max(1, bounds.Height * scale));
        var group = new SlideGroup { Bounds = page, ChildBounds = bounds };
        group.Children.AddRange(elements);
        var slide = new Slide { Background = sheet.Background is { } background ? new SolidFill(background) : null };
        slide.Elements.Add(group);
        var deck = new SlideDeck { Width = page.Width, Height = page.Height, Title = PlainText(root.Text) };
        deck.Slides.Add(slide);
        return deck;
    }

    /// <summary>The visible tree from the central topic, the floating topics' trees and every node in pre-order.</summary>
    private (Node Root, List<Node> Floating, List<Node> All) Tree(MindMapReader.Sheet sheet, Dictionary<string, Node> ids, List<(Node From, MindLink Link)> hiddenLinks)
    {
        var all = new List<Node>();
        var floating = new List<Node>();
        Node Make(Topic topic, Node? parent, Role role)
        {
            if (++_topics > MaxTopics)
                throw DocumentLimits.Exceeded($"The mind map has more than {DocumentLimits.Count(MaxTopics)} topics to draw.");
            var node = new Node(topic, parent, role);
            if (topic.Id is { Length: > 0 } id)
                ids.TryAdd(id, node);
            all.Add(node);
            return node;
        }

        var root = Make(sheet.Root, null, Role.Central);
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = stack.Pop();
            var topic = node.Topic;
            var folded = topic.Folded && node.Role != Role.Central;
            var attached = topic.Children.Where(c => c.Kind == TopicKind.Attached).ToList();
            if (folded)
            {
                node.Hidden = attached.Count;
                Hide(topic.Children, node, ids, hiddenLinks);
            }
            else
            {
                var role = node.Role == Role.Central ? Role.Main : Role.Sub;
                foreach (var child in attached)
                    node.Children.Add(Make(child, node, role));
                node.Attached = node.Children.Count;
                foreach (var child in topic.Children.Where(c => c.Kind == TopicKind.Callout))
                    node.Children.Add(Make(child, node, Role.Callout));
                for (var i = 0; i < node.Children.Count; i++)
                    node.Children[i].Index = i;
                foreach (var child in topic.Children)
                {
                    if (child.Kind == TopicKind.Summary && child.Id is { Length: > 0 } id && topic.Summaries.Count > 0)
                        node.SummaryTopics.TryAdd(id, Make(child, node, Role.Summary));
                }
            }
            foreach (var child in topic.Children.Where(c => c.Kind == TopicKind.Detached))
            {
                var tree = Make(child, null, Role.Floating);
                floating.Add(tree);
                stack.Push(tree);
            }
            foreach (var summary in node.SummaryTopics.Values)
                stack.Push(summary);
            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
        return (root, floating, all);
    }

    /// <summary>
    /// The topics under a folded one: their ids lead to the folded topic (a relationship to one ends there), and so do
    /// their arrow links.
    /// </summary>
    private static void Hide(List<Topic> hidden, Node folded, Dictionary<string, Node> ids, List<(Node From, MindLink Link)> links)
    {
        var stack = new Stack<Topic>(hidden);
        while (stack.Count > 0)
        {
            var topic = stack.Pop();
            if (topic.Id is { Length: > 0 } id)
                ids.TryAdd(id, folded);
            links.AddRange(topic.Links.Select(l => (folded, l)));
            foreach (var child in topic.Children)
                stack.Push(child);
        }
    }

    // ───────────────────────── Look ─────────────────────────

    private static string ThemeKey(Role role) => role switch
    {
        Role.Central => "centralTopic",
        Role.Main => "mainTopic",
        Role.Floating => "floatingTopic",
        Role.Summary => "summaryTopic",
        Role.Callout => "calloutTopic",
        _ => "subTopic",
    };

    /// <summary>Each node's colours, shape and lines: the file's, then the theme's, then the drawing's.</summary>
    private static void Resolve(MindMapReader.Sheet sheet, List<Node> all)
    {
        var xmind = sheet.Theme.Count > 0 || sheet.Structure is not null;
        var mainIndex = 0;
        foreach (var node in all)
        {
            var theme = sheet.Theme.GetValueOrDefault(ThemeKey(node.Role));
            var own = node.Topic.Style;
            var style = own.Over(theme);
            var parent = node.Parent;

            node.Branch = node.Role switch
            {
                Role.Central => own.LineColor ?? CentralFill,
                Role.Main => own.LineColor
                             ?? (sheet.BranchColors.Count > 0 ? sheet.BranchColors[mainIndex % sheet.BranchColors.Count] : (SKColor?)null)
                             ?? theme?.LineColor ?? parent?.Topic.Style.LineColor ?? Palette[mainIndex % Palette.Length],
                Role.Floating => own.LineColor ?? theme?.LineColor ?? Grey,
                _ => own.LineColor ?? parent?.Branch ?? Grey,
            };
            if (node.Role == Role.Main)
                mainIndex++;

            // XMind styles the lines out of a topic; FreeMind's edge is the line into it, inherited down the branch.
            var lineSource = xmind ? (parent?.Topic.Style.Over(parent is null ? null : sheet.Theme.GetValueOrDefault(ThemeKey(parent.Role))) ?? MindStyle.Empty) : own;
            node.FileLineWidth = lineSource.LineWidth ?? (xmind ? null : parent?.FileLineWidth);
            node.FileLine = lineSource.Line != BranchLine.Default ? lineSource.Line : xmind ? BranchLine.Default : parent?.FileLine ?? BranchLine.Default;
            node.Line = node.FileLine == BranchLine.Default ? BranchLine.Curve : node.FileLine;
            node.LineWidth = Math.Clamp(node.FileLineWidth ?? node.Role switch
            {
                Role.Main => 2.25f,
                Role.Sub or Role.Callout => parent?.Role == Role.Main ? 1.5f : 1.25f,
                _ => 1.25f,
            }, 0.25f, 12);

            node.Shape = style.Shape != TopicShape.Default ? style.Shape : node.Role switch
            {
                Role.Sub => TopicShape.Underline,
                _ => TopicShape.RoundedRect,
            };
            var fill = style.Fill ?? node.Role switch
            {
                Role.Central => CentralFill,
                Role.Main => Tint(node.Branch, 0.82f),
                Role.Floating => new SKColor(0xF2, 0xF2, 0xF2),
                Role.Summary or Role.Callout => SKColors.White,
                _ => SKColors.Transparent,
            };
            node.Fill = fill;
            node.TextColor = style.TextColor is { Alpha: > 0 } text ? text : Luminance(fill.Alpha == 0 ? SKColors.White : fill) > 150 ? DarkText : SKColors.White;
            node.Border = style.BorderColor is { } border ? border : node.Role switch
            {
                Role.Main or Role.Summary or Role.Callout => node.Branch,
                Role.Floating => Grey,
                _ => null,
            };
            node.BorderWidth = Math.Clamp(style.BorderWidth ?? (node.Role == Role.Main ? 1.25f : 1f), 0, 12);
        }
    }

    private static SKColor Tint(SKColor color, float amount) => new(
        (byte)(color.Red + (255 - color.Red) * amount), (byte)(color.Green + (255 - color.Green) * amount), (byte)(color.Blue + (255 - color.Blue) * amount));

    private static float Luminance(SKColor color) => 0.299f * color.Red + 0.587f * color.Green + 0.114f * color.Blue;

    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    // ───────────────────────── Size ─────────────────────────

    private static float DefaultSize(Role role) => role switch
    {
        Role.Central => 18,
        Role.Main => 13,
        Role.Floating => 12,
        _ => 11,
    };

    private static float MaxTextWidth(Role role) => role switch
    {
        Role.Central => 260,
        Role.Main => 220,
        _ => 200,
    };

    /// <summary>The node's text as one paragraph, its picture, and the box they take with the shape around them.</summary>
    private void Measure(Node node)
    {
        var topic = node.Topic;
        var style = node.Topic.Style;
        var size = Math.Clamp(style.FontSize ?? DefaultSize(node.Role), 4, 120);
        var format = new HCharFormat(
            Bold: style.Bold ?? (node.Role is Role.Central or Role.Main ? true : null),
            Italic: style.Italic,
            Size: (int)Math.Round(size * 100),
            Font: style.Font,
            Color: Hex(node.TextColor));

        var (inlines, pictures) = map.Text(topic);
        var paragraph = new HParagraph { Format = Centered, MarkFormat = format };
        if (topic.Symbols.Count > 0)
            paragraph.Inlines.Add(new HText(string.Concat(topic.Symbols) + (inlines.Count > 0 ? " " : ""), format));
        foreach (var inline in inlines)
            paragraph.Inlines.Add(Styled(inline, format));
        if (topic.Equation is { } tex && LatexMath.Parse(tex) is { } math)
        {
            if (paragraph.Inlines.Count > 0)
                paragraph.Inlines.Add(new HLineBreak(format));
            paragraph.Inlines.Add(new HEquation(math, format, tex) { Tex = tex });
        }
        if (paragraph.Inlines.Count > 0)
        {
            var box = _layouter.Layout(paragraph, Unbounded);
            var width = box.Lines.Count == 0 ? 0 : box.Lines.Max(l => l.Width);
            var max = MaxTextWidth(node.Role);
            if (width > max)
            {
                box = _layouter.Layout(paragraph, max);
                width = Math.Min(max, (box.Lines.Count == 0 ? 0 : box.Lines.Max(l => l.Width)) + 2);
            }
            else
            {
                width += 2;
            }
            node.Text = paragraph;
            node.TextSize = new SKSize(width, box.Lines.Sum(l => l.Height));
        }

        if (topic.Pictures.Concat(pictures).FirstOrDefault(File.Exists) is { } picture && Picture(picture, topic.PictureSize) is (var image, var pictureSize))
        {
            node.Picture = image;
            node.PictureSize = pictureSize;
        }

        var pad = node.Role switch
        {
            Role.Central => new SKPoint(16, 10),
            Role.Main => new SKPoint(11, 6),
            _ when node.Shape is TopicShape.Underline or TopicShape.None => new SKPoint(4, 3),
            _ => new SKPoint(9, 5),
        };
        node.Pad = pad;
        var text = node.Text is null ? SKSize.Empty : node.TextSize;
        var lineHeight = size * 1.2f;
        var inner = new SKSize(
            Math.Max(Math.Max(text.Width, node.PictureSize.Width), node.Text is null && node.Picture is null ? size * 3 : 4),
            Math.Max(text.Height + node.PictureSize.Height + (node.Text is not null && node.Picture is not null ? pad.Y : 0), lineHeight));
        var w = inner.Width + 2 * pad.X;
        var h = inner.Height + 2 * pad.Y;
        node.Size = node.Shape switch
        {
            TopicShape.Ellipse => new SKSize(w * 1.25f, h * 1.4f),
            TopicShape.Diamond => new SKSize(w * 1.6f, h * 1.7f),
            TopicShape.Hexagon => new SKSize(w + h * 0.8f, h),
            TopicShape.Parallelogram => new SKSize(w + h * 0.6f, h),
            TopicShape.Stadium => new SKSize(w + h * 0.5f, h),
            _ => new SKSize(w, h),
        };
    }

    /// <summary>An inline of the topic's text with what the topic's format adds where the inline's leaves it open.</summary>
    private static HInline Styled(HInline inline, HCharFormat format)
    {
        switch (inline)
        {
            case HEquation equation:
                return new HEquation(equation.Math, Merge(equation.Format, format), equation.Text) { Tex = equation.Tex };
            case HText text:
                return new HText(text.Text, Merge(text.Format, format));
            case HLineBreak:
                return new HLineBreak(format);
            case HLink link when !link.Target.StartsWith('#'):
                var styled = new HLink(link.Target);
                styled.Content.AddRange(link.Content.Select(i => Styled(i, format)));
                return styled;
            case HLink link:
                // A link to another topic: its bookmark isn't drawn, so the text alone.
                return new HText(string.Concat(link.Content.OfType<HText>().Select(t => t.Text)), format);
            default:
                return inline;
        }
    }

    private static HCharFormat Merge(HCharFormat own, HCharFormat topic) => own with
    {
        Bold = own.Bold ?? topic.Bold,
        Italic = own.Italic ?? topic.Italic,
        Size = own.Size ?? topic.Size,
        Font = own.Font ?? topic.Font,
        Color = own.Color ?? topic.Color,
    };

    /// <summary>A topic's picture and the size it is drawn at (the file's, else its own at 96 dpi, at most MaxPicture).</summary>
    private static (SlideImage Image, SKSize Size)? Picture(string path, SKSize? wanted)
    {
        try
        {
            if (new FileInfo(path).Length > DocumentLimits.Default.MaxPartBytes || PictureSize.Read(path) is not { } pixels
                || (long)pixels.Width * pixels.Height > DocumentLimits.Default.MaxPicturePixels || pixels.Width <= 0 || pixels.Height <= 0)
                return null;
            var size = wanted is { Width: > 0, Height: > 0 } given ? given : new SKSize(pixels.Width * 0.75f, pixels.Height * 0.75f);
            var fit = Math.Min(1, Math.Min(MaxPicture / size.Width, MaxPicture / size.Height));
            return (new SlideImage(File.ReadAllBytes(path)), new SKSize(Math.Max(2, size.Width * fit), Math.Max(2, size.Height * fit)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? PlainText(HParagraph? paragraph)
    {
        if (paragraph is null)
            return null;
        var text = string.Join(' ', paragraph.Inlines.OfType<HText>().Select(t => t.Text.Trim()).Where(t => t.Length > 0));
        return text.Length > 0 ? text : null;
    }

    // ───────────────────────── Layout ─────────────────────────

    /// <summary>Which side of the central topic each main topic goes on; subtopics follow theirs.</summary>
    private static void Sides(MindMapReader.Sheet sheet, Node root)
    {
        var structure = sheet.Structure?.ToLowerInvariant();
        var main = root.Children;
        if (structure is not null && !structure.Contains(".map", StringComparison.Ordinal))
        {
            var side = structure.Contains(".left", StringComparison.Ordinal) ? -1 : 1;
            main.ForEach(n => n.Side = side);
        }
        else
        {
            // Half on the right (the first ones), half on the left; FreeMind's POSITION decides where it is set.
            var free = main.Where(n => n.Topic.Side == 0).ToList();
            var right = (main.Count + 1) / 2 - main.Count(n => n.Topic.Side > 0);
            foreach (var node in main)
                node.Side = node.Topic.Side != 0 ? node.Topic.Side : 0;
            foreach (var node in free)
                node.Side = right-- > 0 ? 1 : -1;
            if (structure?.Contains("anticlockwise", StringComparison.Ordinal) == true)
                main.ForEach(n => n.Side = -n.Side);
        }
        var stack = new Stack<Node>(main);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children.Concat(node.SummaryTopics.Values))
            {
                child.Side = node.Side;
                stack.Push(child);
            }
        }
    }

    private static float HorizontalGap(Node parent) => parent.Role switch
    {
        Role.Central => 48,
        Role.Main => 32,
        _ => 26,
    };

    private static float VerticalGap(Node parent) => parent.Role == Role.Central ? 16 : 7;

    /// <summary>Each subtree's band: the height its topic and subtopics take, with room for clouds and boundaries.</summary>
    private static void Bands(List<Node> all)
    {
        foreach (var node in all)
        {
            node.Before = new float[node.Children.Count];
            node.After = new float[node.Children.Count];
            if (node.Topic.Cloud is not null)
            {
                node.ExtraTop += CloudPad;
                node.ExtraBottom += CloudPad;
            }
            foreach (var boundary in node.Topic.Boundaries)
            {
                var title = boundary.Title is { Length: > 0 } ? BoundaryTitle : 0;
                if (boundary.Master)
                {
                    node.ExtraTop += BoundaryPad + title;
                    node.ExtraBottom += BoundaryPad;
                }
                else if (Range(node, boundary.Start, boundary.End) is (var start, var end))
                {
                    node.Before[start] += BoundaryPad + title;
                    node.After[end] += BoundaryPad;
                }
            }
        }
        // Children before their parents: the list is in pre-order.
        for (var i = all.Count - 1; i >= 0; i--)
        {
            var node = all[i];
            node.Band = Math.Max(node.Size.Height, Span(node, node.Children)) + node.ExtraTop + node.ExtraBottom;
        }
    }

    /// <summary>A range of a node's attached subtopics, clamped to them; null when it holds none of them.</summary>
    private static (int Start, int End)? Range(Node node, int start, int end) =>
        start < node.Attached ? (start, Math.Min(end, node.Attached - 1)) : null;

    /// <summary>The height <paramref name="children"/> of <paramref name="node"/> take stacked with their gaps.</summary>
    private static float Span(Node node, List<Node> children)
    {
        var span = 0f;
        for (var i = 0; i < children.Count; i++)
        {
            var index = children[i].Index;
            span += children[i].Band + (index >= 0 ? node.Before[index] + node.After[index] : 0) + (i > 0 ? VerticalGap(node) : 0);
        }
        return span;
    }

    private static SKRect Centred(SKSize size, SKPoint centre) =>
        SKRect.Create(centre.X - size.Width / 2, centre.Y - size.Height / 2, size.Width, size.Height);

    /// <summary>
    /// Places <paramref name="children"/> of placed <paramref name="parent"/> (and everything under them) beside it,
    /// stacked in their bands, centred on <paramref name="centre"/> (the parent's middle).
    /// </summary>
    private static void PlaceChildren(Node parent, float centre, List<Node> children)
    {
        var stack = new Stack<(Node Parent, float Centre, List<Node> Children)>();
        stack.Push((parent, centre, children));
        while (stack.Count > 0)
        {
            var (owner, middle, kids) = stack.Pop();
            var y = middle - Span(owner, kids) / 2;
            var gap = HorizontalGap(owner);
            foreach (var child in kids)
            {
                var index = child.Index;
                y += index >= 0 ? owner.Before[index] : 0;
                var top = y + child.ExtraTop;
                var bottom = y + child.Band - child.ExtraBottom;
                var cy = (top + bottom) / 2;
                var x = child.Side > 0 ? owner.Box.Right + gap + child.Size.Width / 2 : owner.Box.Left - gap - child.Size.Width / 2;
                child.Box = Centred(child.Size, new SKPoint(x, cy));
                child.Placed = true;
                if (child.Children.Count > 0)
                    stack.Push((child, cy, child.Children));
                y += child.Band + (index >= 0 ? owner.After[index] : 0) + VerticalGap(owner);
            }
        }
    }

    /// <summary>Each placed node's subtree: its box with every placed box under it (summaries' too).</summary>
    private static void Subtrees(List<Node> all)
    {
        for (var i = all.Count - 1; i >= 0; i--)
        {
            var node = all[i];
            if (!node.Placed)
                continue;
            var bounds = node.Box;
            foreach (var child in node.Children.Concat(node.SummaryTopics.Values).Where(c => c.Placed))
                bounds = SKRect.Union(bounds, child.Subtree);
            node.Subtree = bounds;
        }
    }

    /// <summary>Places each summary topic beyond a brace beside its range; the braces to draw.</summary>
    private List<(SKPath Path, SKColor Color)> Summaries(List<Node> all)
    {
        var braces = new List<(SKPath, SKColor)>();
        foreach (var node in all.ToList())
        {
            if (!node.Placed || node.SummaryTopics.Count == 0)
                continue;
            foreach (var summary in node.Topic.Summaries)
            {
                if (Range(node, summary.Start, summary.End) is not (var start, var end)
                    || summary.TopicId is null || !node.SummaryTopics.TryGetValue(summary.TopicId, out var topic) || topic.Placed)
                    continue;
                Mark();
                var range = node.Children[start].Subtree;
                for (var i = start + 1; i <= end; i++)
                    range = SKRect.Union(range, node.Children[i].Subtree);
                var side = node.Side;
                var x = side > 0 ? range.Right + 8 : range.Left - 8;
                braces.Add((Brace(x, range.Top, range.Bottom, side), topic.Branch));
                var cx = x + side * (18 + topic.Size.Width / 2);
                topic.Box = Centred(topic.Size, new SKPoint(cx, range.MidY));
                topic.Placed = true;
                if (topic.Children.Count > 0)
                    PlaceChildren(topic, range.MidY, topic.Children);
            }
        }
        return braces;
    }

    /// <summary>A curly brace from <paramref name="top"/> to <paramref name="bottom"/> at <paramref name="x"/>, its point towards <paramref name="side"/>.</summary>
    private static SKPath Brace(float x, float top, float bottom, int side)
    {
        var w = 8f * side;
        var mid = (top + bottom) / 2;
        var path = new SKPath();
        path.MoveTo(x, top);
        path.QuadTo(x + w, top, x + w, Math.Min(top + 10, mid));
        path.LineTo(x + w, Math.Max(mid - 6, top + 10));
        path.QuadTo(x + w, mid, x + 2 * w, mid);
        path.QuadTo(x + w, mid, x + w, Math.Min(mid + 6, bottom - 10));
        path.LineTo(x + w, Math.Max(bottom - 10, mid));
        path.QuadTo(x + w, bottom, x, bottom);
        return path;
    }

    /// <summary>
    /// Floating topics with their subtopics: where the file puts them (from the central topic's centre) when that
    /// covers nothing placed, else in rows below everything, each row centred under the central topic.
    /// </summary>
    private static void Floating(Node root, List<Node> floating, List<Node> all)
    {
        if (floating.Count == 0)
            return;
        Subtrees(all);
        var map = all.Where(n => n.Placed && n.Role != Role.Floating).Select(n => n.Box).Aggregate(root.Box, SKRect.Union);
        map.Inflate(CloudPad + BoundaryPad, CloudPad + BoundaryPad + BoundaryTitle);
        var taken = new List<SKRect> { map };
        var rest = new List<(List<Node> Nodes, SKRect Bounds)>();
        foreach (var tree in floating)
        {
            tree.Box = Centred(tree.Size, SKPoint.Empty);
            tree.Placed = true;
            if (tree.Children.Count > 0)
                PlaceChildren(tree, 0, tree.Children);
            var nodes = Under(tree);
            var bounds = nodes.Select(n => n.Box).Aggregate(SKRect.Union);
            if (tree.Topic.Position is { } position && Math.Abs(position.X) < 50_000 && Math.Abs(position.Y) < 50_000)
            {
                var shift = new SKPoint(root.Box.MidX + position.X, root.Box.MidY + position.Y);
                var moved = bounds;
                moved.Offset(shift);
                if (!taken.Any(t => t.IntersectsWith(moved)))
                {
                    Move(nodes, shift);
                    moved.Inflate(12, 12);
                    taken.Add(moved);
                    continue;
                }
            }
            rest.Add((nodes, bounds));
        }

        // The others in rows as wide as the map (600 points at least).
        var width = Math.Max(map.Width, 600);
        var y = taken.Max(t => t.Bottom) + 36;
        var row = new List<(List<Node> Nodes, SKRect Bounds)>();
        var rowWidth = 0f;
        void Flush()
        {
            var x = root.Box.MidX - rowWidth / 2;
            foreach (var (nodes, bounds) in row)
            {
                Move(nodes, new SKPoint(x - bounds.Left, y - bounds.Top));
                x += bounds.Width + 30;
            }
            y += row.Max(r => r.Bounds.Height) + 30;
            row.Clear();
            rowWidth = 0;
        }
        foreach (var item in rest)
        {
            if (row.Count > 0 && rowWidth + 30 + item.Bounds.Width > width)
                Flush();
            rowWidth += (row.Count > 0 ? 30 : 0) + item.Bounds.Width;
            row.Add(item);
        }
        if (row.Count > 0)
            Flush();
    }

    /// <summary>A node and every placed node under it.</summary>
    private static List<Node> Under(Node top)
    {
        var nodes = new List<Node>();
        var stack = new Stack<Node>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.Placed)
                continue;
            nodes.Add(node);
            foreach (var child in node.Children.Concat(node.SummaryTopics.Values))
                stack.Push(child);
        }
        return nodes;
    }

    private static void Move(List<Node> nodes, SKPoint shift)
    {
        foreach (var node in nodes)
        {
            var box = node.Box;
            box.Offset(shift);
            node.Box = box;
        }
    }

    // ───────────────────────── Drawing ─────────────────────────

    private void Mark()
    {
        if (++_marks > MaxMarks)
            throw DocumentLimits.Exceeded($"The mind map has more than {DocumentLimits.Count(MaxMarks)} relationships, boundaries and summaries to draw.");
    }

    /// <summary>A stroked open path, given in map coordinates.</summary>
    private static SlideShape Stroke(SKPath path, SlideLine line)
    {
        var bounds = path.Bounds;
        bounds.Inflate(line.Width + 6, line.Width + 6);
        path.Offset(-bounds.Left, -bounds.Top);
        return new SlideShape { Bounds = bounds, Paths = [new SlidePath(path, PathFill.None)], Line = line };
    }

    /// <summary>A filled (and outlined) closed path, given in map coordinates.</summary>
    private static SlideShape Filled(SKPath path, SKColor fill, SlideLine? line)
    {
        var bounds = path.Bounds;
        path.Offset(-bounds.Left, -bounds.Top);
        return new SlideShape
        {
            Bounds = bounds,
            Paths = [new SlidePath(path)],
            Fill = fill.Alpha > 0 ? new SolidFill(fill) : null,
            Line = line,
        };
    }

    /// <summary>A FreeMind cloud around a topic and its subtopics: a rounded box with a scalloped edge.</summary>
    private SlideShape Cloud(Node node)
    {
        Mark();
        var area = node.Subtree;
        area.Inflate(CloudPad - 3, CloudPad - 3);
        var color = node.Topic.Cloud!.Color is { Alpha: > 0 } c ? c : new SKColor(0xF0, 0xF0, 0xF0);
        var path = new SKPath();
        var corners = new[] { new SKPoint(area.Left, area.Top), new SKPoint(area.Right, area.Top), new SKPoint(area.Right, area.Bottom), new SKPoint(area.Left, area.Bottom) };
        path.MoveTo(corners[0]);
        for (var i = 0; i < 4; i++)
        {
            var from = corners[i];
            var to = corners[(i + 1) % 4];
            var length = SKPoint.Distance(from, to);
            var steps = Math.Clamp((int)(length / 20), 1, 2000);
            var direction = new SKPoint((to.X - from.X) / length, (to.Y - from.Y) / length);
            var outward = new SKPoint(direction.Y, -direction.X);
            for (var s = 0; s < steps; s++)
            {
                var a = from + new SKPoint(direction.X * length * s / steps, direction.Y * length * s / steps);
                var b = from + new SKPoint(direction.X * length * (s + 1) / steps, direction.Y * length * (s + 1) / steps);
                var mid = new SKPoint((a.X + b.X) / 2 + outward.X * 6, (a.Y + b.Y) / 2 + outward.Y * 6);
                path.QuadTo(mid, b);
            }
        }
        path.Close();
        var edge = new SKColor((byte)(color.Red * 0.75f), (byte)(color.Green * 0.75f), (byte)(color.Blue * 0.75f));
        return Filled(path, color, new SlideLine(new SolidFill(edge), 1));
    }

    /// <summary>A node's boundaries (around a range of subtopics, or the whole branch) and their titles.</summary>
    private void Boundaries(Node node, Action<SlideElement> add, List<SlideElement> titles)
    {
        var extraTop = node.Topic.Cloud is not null ? CloudPad : 0;
        foreach (var boundary in node.Topic.Boundaries)
        {
            SKRect area;
            var title = boundary.Title is { Length: > 0 } ? BoundaryTitle : 0;
            if (boundary.Master)
            {
                area = node.Subtree;
                area.Inflate(BoundaryPad - 2 + extraTop, BoundaryPad - 2 + extraTop);
                area.Top -= title;
            }
            else if (Range(node, boundary.Start, boundary.End) is (var start, var end))
            {
                area = node.Children[start].Subtree;
                for (var i = start + 1; i <= end; i++)
                    area = SKRect.Union(area, node.Children[i].Subtree);
                area.Inflate(BoundaryPad - 2, BoundaryPad - 2);
                area.Top -= title;
            }
            else
            {
                continue;
            }
            Mark();
            var color = boundary.Style.LineColor ?? boundary.Style.BorderColor ?? node.Children.FirstOrDefault()?.Branch ?? node.Branch;
            var fill = boundary.Style.Fill ?? color.WithAlpha(28);
            var path = new SKPath();
            path.AddRoundRect(area, 8, 8);
            add(Filled(path, fill, new SlideLine(new SolidFill(color), boundary.Style.LineWidth ?? 1.25f, Dash: boundary.Style.Dashed == false ? null : [4, 3],
                Cap: SKStrokeCap.Round)));
            if (title > 0)
                titles.Add(Label(boundary.Title!, new SKPoint(area.Left + 6, area.Top + 2), color, SKColors.White, anchorLeft: true, area.Width - 12));
        }
    }

    /// <summary>The line from a node's parent to it, its underline, and a folded topic's marker stub.</summary>
    private static void Branches(Node node, Action<SlideElement> add)
    {
        var line = new SlideLine(new SolidFill(node.Branch), node.LineWidth, Cap: SKStrokeCap.Round, Join: SKStrokeJoin.Round);
        if (node.Parent is { } parent && node.Role is not (Role.Summary or Role.Floating) && node.Line != BranchLine.None)
        {
            var start = parent.Role == Role.Central ? new SKPoint(parent.Box.MidX, parent.Box.MidY) : Outer(parent, node.Side);
            var end = Inner(node);
            var path = new SKPath();
            path.MoveTo(start);
            switch (node.Line)
            {
                case BranchLine.Straight:
                    path.LineTo(end);
                    break;
                case BranchLine.Elbow:
                    var x = parent.Role == Role.Central ? (Outer(parent, node.Side).X + end.X) / 2 : (start.X + end.X) / 2;
                    path.LineTo(x, start.Y);
                    path.LineTo(x, end.Y);
                    path.LineTo(end);
                    break;
                default:
                    var dx = end.X - start.X;
                    path.CubicTo(start.X + dx * 0.5f, start.Y, end.X - dx * 0.5f, end.Y, end.X, end.Y);
                    break;
            }
            add(Stroke(path, line));
        }
        if (node.Shape == TopicShape.Underline)
        {
            var under = new SKPath();
            under.MoveTo(node.Box.Left, node.Box.Bottom);
            under.LineTo(node.Box.Right, node.Box.Bottom);
            add(Stroke(under, line with { Width = Math.Max(1, node.LineWidth) }));
        }
        if (node.Hidden > 0)
        {
            var from = Outer(node, node.Side);
            var stub = new SKPath();
            stub.MoveTo(from);
            stub.LineTo(from.X + node.Side * 4, from.Y);
            add(Stroke(stub, line with { Width = Math.Max(1, node.LineWidth) }));
        }
    }

    /// <summary>Where lines leave a topic towards <paramref name="side"/>: the middle of its edge, or its underline's end.</summary>
    private static SKPoint Outer(Node node, int side) => new(side > 0 ? node.Box.Right : node.Box.Left,
        node.Shape == TopicShape.Underline ? node.Box.Bottom : node.Box.MidY);

    /// <summary>Where the line from its parent reaches a topic.</summary>
    private static SKPoint Inner(Node node) => new(node.Side > 0 ? node.Box.Left : node.Box.Right,
        node.Shape == TopicShape.Underline ? node.Box.Bottom : node.Box.MidY);

    /// <summary>A topic: its shape with its text and picture, and a folded topic's count of hidden subtopics.</summary>
    private void DrawTopic(Node node, Action<SlideElement> add)
    {
        var box = node.Box;
        var (w, h) = (box.Width, box.Height);
        var outline = node.Shape switch
        {
            TopicShape.Underline or TopicShape.None => null,
            _ => ShapePath(node.Shape, w, h),
        };
        SlideFill? fill = node.Fill.Alpha > 0 ? new SolidFill(node.Fill) : null;
        if (outline is null && fill is not null)
        {
            outline = new SKPath();
            outline.AddRoundRect(new SKRect(0, 0, w, h), 3, 3);
        }
        var border = outline is not null && node.Shape is not (TopicShape.Underline or TopicShape.None) && node.Border is { Alpha: > 0 } color && node.BorderWidth > 0
            ? new SlideLine(new SolidFill(color), node.BorderWidth)
            : null;

        // The picture above the text, both centred in the box.
        var content = new SKSize(Math.Max(node.TextSize.Width, node.PictureSize.Width),
            node.TextSize.Height + node.PictureSize.Height + (node.Text is not null && node.Picture is not null ? node.Pad.Y : 0));
        var top = (h - content.Height) / 2;
        SlideTextFrame? frame = null;
        if (node.Text is { } text)
        {
            var textTop = top + content.Height - node.TextSize.Height;
            frame = new SlideTextFrame
            {
                Area = SKRect.Create((w - node.TextSize.Width) / 2, textTop, node.TextSize.Width, node.TextSize.Height),
                InsetLeft = 0,
                InsetTop = 0,
                InsetRight = 0,
                InsetBottom = 0,
                Anchor = TextAnchor.Middle,
            };
            frame.Paragraphs.Add(text);
        }
        add(new SlideShape
        {
            Bounds = box,
            Paths = outline is null ? [] : [new SlidePath(outline, fill is null ? PathFill.None : PathFill.Normal)],
            Fill = fill,
            Line = border,
            Text = frame,
        });
        if (node.Picture is { } picture)
        {
            var area = SKRect.Create(box.Left + (w - node.PictureSize.Width) / 2, box.Top + top, node.PictureSize.Width, node.PictureSize.Height);
            var rect = new SKPath();
            rect.AddRect(new SKRect(0, 0, area.Width, area.Height));
            add(new SlideShape
            {
                Bounds = area,
                Paths = [new SlidePath(rect, Stroke: false)],
                Fill = new PictureFill(picture, SKRect.Empty, SKRect.Empty),
                IsPicture = true,
            });
        }
        if (node.Hidden > 0)
        {
            var from = Outer(node, node.Side);
            var marker = new SKPath();
            const float radius = 7.5f;
            marker.AddCircle(radius, radius, radius);
            var count = new HParagraph { Format = Centered };
            var format = new HCharFormat(Bold: true, Size: 650, Color: Hex(node.Branch));
            count.Inlines.Add(new HText(node.Hidden > 99 ? "99+" : node.Hidden.ToString(System.Globalization.CultureInfo.InvariantCulture), format));
            var frameCount = new SlideTextFrame { InsetLeft = 0, InsetRight = 0, InsetTop = 0, InsetBottom = 0, Anchor = TextAnchor.Middle, Wrap = false };
            frameCount.Paragraphs.Add(count);
            add(new SlideShape
            {
                Bounds = SKRect.Create(from.X + node.Side * (4 + radius) - radius, from.Y - radius, 2 * radius, 2 * radius),
                Paths = [new SlidePath(marker)],
                Fill = new SolidFill(SKColors.White),
                Line = new SlideLine(new SolidFill(node.Branch), 1.25f),
                Text = frameCount,
            });
        }
    }

    /// <summary>A topic's outline in its box's own coordinates.</summary>
    private static SKPath ShapePath(TopicShape shape, float w, float h)
    {
        var path = new SKPath();
        var box = new SKRect(0, 0, w, h);
        switch (shape)
        {
            case TopicShape.Rect:
                path.AddRect(box);
                break;
            case TopicShape.Ellipse:
                path.AddOval(box);
                break;
            case TopicShape.Diamond:
                path.AddPoly([new SKPoint(w / 2, 0), new SKPoint(w, h / 2), new SKPoint(w / 2, h), new SKPoint(0, h / 2)]);
                break;
            case TopicShape.Hexagon:
                var inset = Math.Min(h * 0.4f, w / 4);
                path.AddPoly([new SKPoint(inset, 0), new SKPoint(w - inset, 0), new SKPoint(w, h / 2), new SKPoint(w - inset, h), new SKPoint(inset, h), new SKPoint(0, h / 2)]);
                break;
            case TopicShape.Parallelogram:
                var slant = Math.Min(h * 0.6f, w / 3);
                path.AddPoly([new SKPoint(slant, 0), new SKPoint(w, 0), new SKPoint(w - slant, h), new SKPoint(0, h)]);
                break;
            case TopicShape.Stadium:
                path.AddRoundRect(box, h / 2, h / 2);
                break;
            default:
                var radius = Math.Min(8, Math.Min(w, h) * 0.3f);
                path.AddRoundRect(box, radius, radius);
                break;
        }
        return path;
    }

    /// <summary>A small text box: <paramref name="text"/> at <paramref name="at"/> (its top-left, or its centre).</summary>
    private SlideShape Label(string text, SKPoint at, SKColor background, SKColor color, bool anchorLeft, float maxWidth = 200)
    {
        var format = new HCharFormat(Size: 850, Color: Hex(color));
        var paragraph = new HParagraph { Format = Centered, MarkFormat = format };
        paragraph.Inlines.Add(new HText(PlainLine(text), format));
        var max = Math.Clamp(maxWidth, 20, 240);
        var box = _layouter.Layout(paragraph, Unbounded);
        var width = box.Lines.Count == 0 ? 0 : box.Lines.Max(l => l.Width);
        if (width > max)
        {
            box = _layouter.Layout(paragraph, max);
            width = Math.Min(max, box.Lines.Max(l => l.Width) + 2);
        }
        else
        {
            width += 2;
        }
        var size = new SKSize(width + 8, box.Lines.Sum(l => l.Height) + 3);
        var bounds = anchorLeft ? SKRect.Create(at.X, at.Y, size.Width, size.Height) : Centred(size, at);
        var path = new SKPath();
        path.AddRoundRect(new SKRect(0, 0, size.Width, size.Height), 3, 3);
        var frame = new SlideTextFrame { InsetLeft = 4, InsetRight = 4, InsetTop = 1.5f, InsetBottom = 1.5f, Anchor = TextAnchor.Middle };
        frame.Paragraphs.Add(paragraph);
        return new SlideShape { Bounds = bounds, Paths = [new SlidePath(path)], Fill = new SolidFill(background), Text = frame };
    }

    private static string PlainLine(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 500 ? line[..500] + "…" : line;
    }

    /// <summary>XMind relationships and FreeMind arrow links: curves between topics, bowed away from the central topic.</summary>
    private void Links(MindMapReader.Sheet sheet, Node root, Dictionary<string, Node> ids, List<Node> all, List<(Node From, MindLink Link)> hidden,
        Action<SlideElement> add)
    {
        var xmind = sheet.Theme.Count > 0 || sheet.Structure is not null;
        var links = new List<(Node From, MindLink Link)>();
        foreach (var relationship in sheet.Relationships)
        {
            if (ids.TryGetValue(relationship.From, out var from))
                links.Add((from, relationship));
        }
        // FreeMind's arrow links hang off their topic, visible or under a folded one.
        foreach (var node in all)
            links.AddRange(node.Topic.Links.Select(l => (node, l)));
        links.AddRange(hidden);
        var theme = sheet.Theme.GetValueOrDefault("relationship");
        var labels = new List<SlideElement>();
        var boxes = all.Where(n => n.Placed).Select(n => n.Box).ToList();
        foreach (var (from, link) in links)
        {
            if (!ids.TryGetValue(link.To, out var to) || ReferenceEquals(from, to) || !from.Placed || !to.Placed)
                continue;
            Mark();
            var style = link.Style.Over(theme);
            var color = style.LineColor is { Alpha: > 0 } c ? c : Grey;
            var width = Math.Clamp(style.LineWidth ?? 1.25f, 0.25f, 12);
            var a = new SKPoint(from.Box.MidX, from.Box.MidY);
            var b = new SKPoint(to.Box.MidX, to.Box.MidY);
            var d = b - a;
            var length = d.Length;
            if (length < 1)
                continue;
            var normal = new SKPoint(-d.Y / length, d.X / length);
            var middle = new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var away = new SKPoint(middle.X - root.Box.MidX, middle.Y - root.Box.MidY);
            if (normal.X * away.X + normal.Y * away.Y < 0)
                normal = new SKPoint(-normal.X, -normal.Y);
            var bow = Math.Clamp(length * 0.3f, 24, 160);
            var c1 = new SKPoint(a.X + d.X * 0.3f + normal.X * bow, a.Y + d.Y * 0.3f + normal.Y * bow);
            var c2 = new SKPoint(a.X + d.X * 0.7f + normal.X * bow, a.Y + d.Y * 0.7f + normal.Y * bow);
            var p0 = Edge(from.Box, c1);
            var p3 = Edge(to.Box, c2);
            var path = new SKPath();
            path.MoveTo(p0);
            path.CubicTo(c1, c2, p3);
            var line = new SlideLine(new SolidFill(color), width, Dash: (style.Dashed ?? xmind) ? [4, 3] : null, Cap: SKStrokeCap.Round,
                Head: new LineEnd(style.ArrowStart ?? LineEndKind.None), Tail: new LineEnd(style.ArrowEnd ?? LineEndKind.Triangle));
            add(Stroke(path, line));
            if (link.Title is { } title && PlainLine(title) is { Length: > 0 })
            {
                // On the curve, at its middle or the nearest place that covers no topic (while that is quick to find).
                SlideShape? label = null;
                foreach (var t in Along)
                {
                    var u = 1 - t;
                    var at = new SKPoint(u * u * u * p0.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * p3.X,
                        u * u * u * p0.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * p3.Y);
                    label = Label(title, at, SKColors.White, color, anchorLeft: false);
                    if ((long)links.Count * boxes.Count > MaxLabelChecks || !boxes.Any(b => b.IntersectsWith(label.Bounds)))
                        break;
                }
                labels.Add(label!);
            }
        }
        labels.ForEach(add);
    }

    /// <summary>Places along a relationship its title is tried at, the middle first.</summary>
    private static readonly float[] Along = [0.5f, 0.4f, 0.6f, 0.3f, 0.7f, 0.2f, 0.8f];

    /// <summary>Relationships × topics past which titles go in the middle without looking for room.</summary>
    private const long MaxLabelChecks = 4_000_000;

    /// <summary>Where the line from the middle of <paramref name="box"/> towards <paramref name="towards"/> leaves it.</summary>
    private static SKPoint Edge(SKRect box, SKPoint towards)
    {
        var centre = new SKPoint(box.MidX, box.MidY);
        var dx = towards.X - centre.X;
        var dy = towards.Y - centre.Y;
        if (Math.Abs(dx) < 0.01f && Math.Abs(dy) < 0.01f)
            return centre;
        var t = Math.Min(Math.Abs(dx) > 0.01f ? box.Width / 2 / Math.Abs(dx) : float.MaxValue, Math.Abs(dy) > 0.01f ? box.Height / 2 / Math.Abs(dy) : float.MaxValue);
        t = Math.Min(t, 1);
        return new SKPoint(centre.X + dx * t, centre.Y + dy * t);
    }
}
