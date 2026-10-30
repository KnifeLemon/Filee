// Lays out a paragraph into lines (LineBox) for a column of a given width.
//
// 1. Itemise: the text (ParagraphText) splits into items that each shape in one go: one style (the character slot's
//    font, size, spacing ...), one font (FontResolver's fallback for characters the font lacks), one script, one
//    direction. Tabs and line breaks are items of their own.
// 2. Measure: each item is shaped (Shaper) and every grapheme cluster gets its advance, letter spacing included.
// 3. Break: lines fill greedily up to the column width, breaking at the last opportunity LineBreaker allows (한글 and
//    Word break the same way); spaces and tabs at a line end hang past it. A word wider than the line breaks between
//    clusters. 한글's 최소 공백 (condense) lets a line keep a word that fits once its spaces shrink. With lineWrap
//    KEEP or SQUEEZE the paragraph stays on one line, squeezed to fit with SQUEEZE.
// 4. Place: each line's part of every item is shaped again with its context, so a break inside an Arabic word or
//    after an Indic cluster is shaped as it ends; tabs get their width from the stops (left, right, centre, decimal,
//    with leaders); alignment adds the remaining width before the line (right, centre) or between its spaces
//    (justify; between clusters where a line has no spaces) or its clusters (distribute, 한글 배분, the last line too).
// 5. Line height: by the rule the paragraph carries. 한글's rules (percent, fixed, at least, between lines) measure
//    from the font size, the text taking 85% of it above the baseline as 한글 lays out (its stored line segments put
//    the baseline at 0.85 × the text height); Word's multiple, and 한글 "글꼴에 어울리는 줄 높이", measure from the
//    font's own ascent and descent (OS/2 win metrics, as Word on Windows).
//
// 한글's own line layout: an HWPX that 한글 saved stores where each line of a paragraph starts and how high it is
// (HLineLayout), and 한글 draws from it when it opens the file instead of breaking the lines again. Where the stored
// starts convert to the model's text and every font of the paragraph is installed (by its name or another of its names),
// the lines start where 한글's started and take their stored height and baseline; a stored line wider than the line
// now (a font a little wider than where the file was saved) is squeezed between its characters, as 한글 squeezes it.
// Lines Filee estimated are never used; otherwise the paragraph is broken again and ParagraphBox says why.
//
// Bidirectional text gets a simplified UBA: strong characters and numbers set the levels, neutrals between two runs of
// one direction take it and the paragraph's otherwise; parts are reordered by level (rule L2). The full algorithm
// (explicit embeddings, isolates, brackets) is a later milestone, as is dictionary breaking for Thai and its kin.
// Tab positions are measured from the paragraph's left indent, as 한글 measures them; a hanging indent is a tab stop
// on the first line (the text after a list number starts there).
//
// 본 제품은 한글과컴퓨터의 한/글 문서 파일(.hwp) 공개 문서를 참고하여 개발하였습니다.

using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>Lays out paragraphs; one per layout job, sharing its font resolver and shaper (not thread-safe).</summary>
internal sealed class ParagraphLayouter
{
    /// <summary>Distance between the default tab stops, in points (한글's 40 pt).</summary>
    private const float DefaultTabInterval = 40;

    /// <summary>Part of the text height above the baseline in 한글's line layout.</summary>
    private const float HwpAscent = 0.85f;

    private const float Epsilon = 0.01f;

    private readonly FontResolver _fonts;
    private readonly Shaper _shaper;

    /// <summary>
    /// Lays out the content of a text box laid out like a character (TableLayout.Stack, which the page composer sets);
    /// without it such a box is drawn empty.
    /// </summary>
    public Func<IReadOnlyList<HBlock>, float, List<Piece>> Stack { get; set; } = (_, _) => [];
    private readonly string? _notAtLineStart;
    private readonly string? _notAtLineEnd;
    private readonly Dictionary<(string? Family, FontStyle Style, string Language), ResolvedFont> _resolved = [];

    private readonly bool _storedLines;

    /// <param name="kinsoku">The document's own characters forbidden at a line's start or end (Word settings).</param>
    /// <param name="storedLines">Lay paragraphs out on 한글's stored lines where they can be used.</param>
    public ParagraphLayouter(FontResolver fonts, Shaper shaper, IReadOnlyList<HKinsoku>? kinsoku = null, bool storedLines = true)
    {
        _fonts = fonts;
        _shaper = shaper;
        _storedLines = storedLines;
        if (kinsoku is { Count: > 0 })
        {
            _notAtLineStart = string.Concat(kinsoku.Select(k => k.NotAtLineStart));
            _notAtLineEnd = string.Concat(kinsoku.Select(k => k.NotAtLineEnd));
        }
    }

    /// <summary>Lays out <paramref name="paragraph"/> in a column <paramref name="width"/> points wide.</summary>
    /// <param name="marker">The list item's number or bullet, or null.</param>
    /// <param name="fields">The text of page number and total pages fields; null leaves them out.</param>
    /// <param name="notes">The number a note reference shows; null leaves note references out.</param>
    /// <param name="wrap">
    /// Room objects take beside the lines laid out anew (text wrap): for a line at a height below the first line's top
    /// and its height, how far in from the left and right it starts and ends. Null leaves the lines whole.
    /// </param>
    public ParagraphBox Layout(HParagraph paragraph, float width, string? marker = null, Func<HField, string>? fields = null,
        Func<HNote, string>? notes = null, Func<float, float, (float Left, float Right)>? wrap = null)
    {
        var format = paragraph.Format;
        var source = ParagraphText.From(paragraph, marker, fields, notes);
        var text = source.Text;
        var language = FontSubstitution.Language(text);
        var rightToLeft = format.RightToLeft ?? FirstStrongIsRightToLeft(text);
        var items = Itemize(source, language, Levels(text, rightToLeft));
        var (advances, starts) = Measure(source, items);

        var breaking = format.Layout?.Breaking;
        var rules = new LineBreakRules(
            KeepHangulWords: breaking?.NonLatin == HNonLatinBreak.KeepWord,
            BreakLatinWords: breaking?.Latin == HLatinBreak.BreakWord,
            _notAtLineStart,
            _notAtLineEnd);
        var breaks = LineBreaker.Breaks(text, rules);
        var paragraphLayout = new Settings(
            format.Align ?? HAlign.Left,
            rightToLeft,
            (format.Left ?? 0) / 100f,
            (format.Right ?? 0) / 100f,
            (format.FirstLine ?? 0) / 100f,
            format.Tabs ?? [],
            breaking?.LineWrap ?? HLineWrap.Break,
            Math.Clamp(breaking?.Condense ?? 0, 0, 100) / 100f,
            format.Layout?.LineHeight ?? HLineHeight.From(format.LineSpacing ?? new HLineSpacing(HLineSpacingKind.Percent, 160)),
            format.Layout?.FontLineHeight == true);
        var mark = MarkStyle(paragraph, language);
        var before = (format.Before ?? 0) / 100f;
        var after = (format.After ?? 0) / 100f;

        var (stored, ignored) = _storedLines ? StoredStarts(paragraph, source, marker, items) : (null, null);
        if (stored is not null)
        {
            var segments = paragraph.Lines!.Segments;
            var storedLines = new List<LineBox>();
            for (var i = 0; i < stored.Count; i++)
            {
                var end = i + 1 < stored.Count ? stored[i + 1] : text.Length;
                var forced = end == text.Length || text[end - 1] == ParagraphText.LineSeparator;
                storedLines.Add(Place(source, items, stored[i], end, forced, i == 0, width, paragraphLayout, mark, (segments[i], segments[0])));
            }
            return new ParagraphBox(before, after, storedLines) { StoredLines = true, Marker = marker };
        }

        var lines = new List<LineBox>();
        var start = 0;
        var y = 0f;
        do
        {
            var first = lines.Count == 0;
            var settings = paragraphLayout;
            if (wrap?.Invoke(y, lines.Count > 0 ? lines[^1].Height : mark.FontSize * 1.6f) is var (left, right) && left + right > 0)
                settings = settings with { Left = settings.Left + left, Right = settings.Right + right };
            var available = width - settings.Left - settings.Right - (first ? settings.FirstLine : 0);
            var (end, forced) = settings.Wrap == HLineWrap.Break
                ? Fit(text, breaks, advances, starts, start, available, x => NextStop(settings, first, x), settings.Condense)
                : (text.Length, true);
            lines.Add(Place(source, items, start, end, forced, first, width, settings, mark));
            y += lines[^1].Height;
            start = end;
        }
        while (start < text.Length);
        // A line break at the end of a paragraph leaves an empty last line, as in 한글 and Word.
        if (text.Length > 0 && text[^1] == ParagraphText.LineSeparator)
            lines.Add(Place(source, items, text.Length, text.Length, true, false, width, paragraphLayout, mark));

        return new ParagraphBox(before, after, lines) { StoredLinesIgnored = ignored, Marker = marker };
    }

    /// <summary>
    /// Where the stored lines of <paramref name="paragraph"/> start in <paramref name="text"/> (the first at 0), or null
    /// with the reason they can't be used (null too when there are none).
    /// </summary>
    private (List<int>? Starts, string? Ignored) StoredStarts(HParagraph paragraph, ParagraphText source, string? marker, List<Item> items)
    {
        var text = source.Text;
        if (paragraph.Lines is not { } lines)
            return (null, null);
        if (lines.Estimated)
            return (null, "the stored lines were estimated by Filee");
        if (lines.Starts is not { Count: > 0 } positions || positions.Count != lines.Segments.Count || positions[0] != 0)
            return (null, "the stored line starts don't map to the text (a table splits the paragraph, or one falls inside an object)");
        foreach (var item in items)
        {
            if (item.Kind == ItemKind.Text && item.Style.Family is { } family && _fonts.Find(family, item.Style.Style) is null)
                return (null, $"the font {family} is not installed");
        }
        // A list item's number and the tab or space after it come first in the text, not in 한글's; fields' text and
        // note numbers are the layout's own too.
        var shift = marker is { Length: > 0 } ? marker.Length + 1 : 0;
        var starts = new List<int> { 0 };
        foreach (var position in positions.Skip(1))
        {
            var start = source.FromModel(position, shift);
            if (start <= starts[^1] || start > text.Length)
                return (null, "the stored line starts are out of order");
            starts.Add(start);
        }
        return (starts, null);
    }

    /// <summary>The paragraph settings the line steps share (lengths in points).</summary>
    private sealed record Settings(HAlign Align, bool RightToLeft, float Left, float Right, float FirstLine, IReadOnlyList<HTabStop> Tabs,
        HLineWrap Wrap, float Condense, HLineHeight LineHeight, bool FontLineHeight);

    // ───────────────────────── Items ─────────────────────────

    private enum ItemKind
    {
        Text,
        Tab,
        Break,

        /// <summary>A picture laid out like a character.</summary>
        Object,
    }

    /// <summary>Text shaped in one go, or a tab or line break.</summary>
    /// <param name="Level">Bidirectional embedding level: odd is right to left.</param>
    private sealed record Item(int Start, int Length, ItemKind Kind, TextStyle Style, ResolvedFont Font, UnicodeScript Script, int Level, string? Link)
    {
        public int End => Start + Length;

        public bool RightToLeft => (Level & 1) == 1;
    }

    private List<Item> Itemize(ParagraphText source, string language, int[] levels)
    {
        var text = source.Text;
        var items = new List<Item>();
        foreach (var span in source.Spans)
        {
            foreach (var (start, length, style) in source.Styles(span, language))
            {
                var fontLanguage = style.Language.Length > 0 ? style.Language : language;
                var primary = Font(style.Family, style.Style, fontLanguage);
                foreach (var run in _fonts.Fallback(text, start, length, primary, style.Style, fontLanguage))
                    Split(text, run, style, levels, span.Link, items);
            }
        }
        return items;
    }

    /// <summary>Splits a font run into items of one kind, level and script.</summary>
    private static void Split(string text, FontRun run, TextStyle style, int[] levels, string? link, List<Item> items)
    {
        var scripts = new UnicodeScript[run.Length];
        var current = UnicodeScript.Common;
        for (var i = 0; i < run.Length; i++)
        {
            var script = UnicodeData.GetScript(UnicodeData.CodePointAt(text, run.Start + i));
            if (char.IsLowSurrogate(text[run.Start + i]) || script is UnicodeScript.Common or UnicodeScript.Inherited)
                script = current;
            scripts[i] = current = script;
        }
        // Common characters before the run's first script take it.
        var firstScript = scripts.FirstOrDefault(s => s != UnicodeScript.Common);
        for (var i = 0; i < run.Length && scripts[i] == UnicodeScript.Common; i++)
            scripts[i] = firstScript;

        var itemStart = run.Start;
        for (var i = run.Start; i < run.End; i++)
        {
            var kind = KindOf(text[i]);
            var boundary = i + 1 == run.End || kind != ItemKind.Text || KindOf(text[i + 1]) != ItemKind.Text ||
                           levels[i + 1] != levels[i] || scripts[i + 1 - run.Start] != scripts[i - run.Start] && !char.IsLowSurrogate(text[i + 1]);
            if (!boundary)
                continue;
            items.Add(new Item(itemStart, i + 1 - itemStart, kind, style, run.Font, scripts[itemStart - run.Start], levels[itemStart], link));
            itemStart = i + 1;
        }
    }

    private static ItemKind KindOf(char c) => c switch
    {
        '\t' => ItemKind.Tab,
        ParagraphText.LineSeparator => ItemKind.Break,
        ParagraphText.ObjectReplacement => ItemKind.Object,
        _ => ItemKind.Text,
    };

    private ResolvedFont Font(string? family, FontStyle style, string language)
    {
        var key = (family, style, language);
        if (!_resolved.TryGetValue(key, out var font))
        {
            _resolved[key] = font = _fonts.Resolve(family, style, language)
                                    ?? _fonts.Resolve(null, style, language)
                                    ?? throw new InvalidOperationException("No installed font can draw the document's text.");
        }
        return font;
    }

    /// <summary>The style of the paragraph mark, which gives an empty line its height.</summary>
    private static TextStyle MarkStyle(HParagraph paragraph, string language)
    {
        var format = paragraph.Inlines.Count == 0 ? paragraph.MarkFormat : paragraph.Inlines.OfType<HText>().FirstOrDefault()?.Format ?? paragraph.MarkFormat;
        return format.Layout?.WordFonts is { } word
            ? ParagraphText.WordStyle(format, word, WordFontSlot.Ascii, language)
            : ParagraphText.HwpStyle(format, HLanguage.Hangul, language);
    }

    private static readonly FontFeature[] NoKerning = [new("kern", 0)];

    private ShapedRun Shape(string text, Item item, int start, int length) =>
        _shaper.Shape(item.Font.Font, text, start, length, new ShapeOptions(
            item.RightToLeft ? TextDirection.RightToLeft : TextDirection.LeftToRight,
            item.Script == UnicodeScript.Common ? null : item.Script,
            item.Style.Language.Length > 0 ? item.Style.Language : null,
            item.Style.Kerning ? null : NoKerning));

    /// <summary>The space added after a character whose advance is <paramref name="advance"/>.</summary>
    private static float Spacing(TextStyle style, float advance) => style.LetterSpacing + style.SpacingRatio * advance;

    /// <summary>A glyph's advance in points before the style's scale: a space's as the style sets it.</summary>
    private static float Advance(ShapedRun run, int glyph, string text, int start, TextStyle style) =>
        style.SpaceWidth is { } space && text[start + run.Clusters[glyph]] == ' '
            ? space
            : run.XAdvances[glyph] * style.Size / run.UnitsPerEm;

    /// <summary>Per UTF-16 index: the advance of the cluster starting there (0 elsewhere) and whether one starts there.</summary>
    private static (float[] Advances, bool[] Starts) Measure(ParagraphText source, List<Item> items, Func<string, int, int, Item, ShapedRun> shape)
    {
        var text = source.Text;
        var advances = new float[text.Length];
        var starts = new bool[text.Length];
        foreach (var item in items)
        {
            if (item.Kind != ItemKind.Text)
            {
                starts[item.Start] = true;
                if (item.Kind == ItemKind.Object)
                    advances[item.Start] = ObjectSize(source, item.Start).Width;
                continue;
            }
            var run = shape(text, item.Start, item.Length, item);
            for (var g = 0; g < run.Glyphs.Length; g++)
            {
                var index = item.Start + run.Clusters[g];
                starts[index] = true;
                advances[index] += Advance(run, g, text, item.Start, item.Style) * item.Style.Scale;
            }
            for (var i = item.Start; i < item.End; i++)
            {
                if (starts[i])
                    advances[i] += Spacing(item.Style, advances[i]);
            }
        }
        return (advances, starts);
    }

    private (float[] Advances, bool[] Starts) Measure(ParagraphText source, List<Item> items) =>
        Measure(source, items, (text, start, length, item) => Shape(text, item, start, length));

    /// <summary>The size of the object at <paramref name="position"/>; nothing for a picture whose size isn't known.</summary>
    private static (float Width, float Height) ObjectSize(ParagraphText source, int position) =>
        source.Objects.TryGetValue(position, out var item) && FloatingObjects.Size(item) is { } size ? size : (0, 0);

    // ───────────────────────── Breaking ─────────────────────────

    /// <summary>Where the line starting at <paramref name="start"/> ends, and whether it had to (a line break, the end).</summary>
    private static (int End, bool Forced) Fit(string text, LineBreak[] breaks, float[] advances, bool[] starts, int start, float available,
        Func<float, TabStop> nextStop, float condense)
    {
        var pen = new Pen();
        var lastBreak = -1;
        float spaces = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (i > start && breaks[i] == LineBreak.Mandatory)
                return (i, true);
            if (!starts[i])
                continue;
            if (i > start && breaks[i] == LineBreak.Allowed)
                lastBreak = i;
            var c = text[i];
            if (c == '\t')
            {
                pen.Tab(nextStop(pen.X));
                continue;
            }
            var width = advances[i];
            if (IsSpace(c))
            {
                pen.Add(width, c);
                if (IsStretchable(c))
                    spaces += width;
                continue;
            }
            var after = pen.After(width);
            if (i > start && after > available + Epsilon && !(condense > 0 && after - spaces * condense <= available + Epsilon))
                return (lastBreak > start ? lastBreak : i, false);
            pen.Add(width, c);
        }
        return (text.Length, true);
    }

    /// <summary>Spaces that hang at a line end and take no part in its width.</summary>
    private static bool IsSpace(char c) => c is ' ' or (char)0x3000 or ParagraphText.LineSeparator;

    /// <summary>Spaces justification widens and 최소 공백 narrows.</summary>
    private static bool IsStretchable(char c) => c is ' ' or (char)0x3000;

    private readonly record struct TabStop(float X, HTabKind Kind, string Leader);

    /// <summary>The first tab stop after <paramref name="x"/> (line coordinates: 0 is where the line starts).</summary>
    private static TabStop NextStop(Settings settings, bool firstLine, float x)
    {
        // Stops are measured from the left indent; the first line starts FirstLine from it.
        var indent = firstLine ? settings.FirstLine : 0;
        TabStop? best = null;
        foreach (var tab in settings.Tabs)
        {
            var position = tab.Position / 100f - indent;
            if (position > x + Epsilon && (best is null || position < best.Value.X))
                best = new TabStop(position, tab.Kind, tab.Leader);
        }
        if (firstLine && settings.FirstLine < 0 && -settings.FirstLine > x + Epsilon && (best is null || -settings.FirstLine < best.Value.X))
            best = new TabStop(-settings.FirstLine, HTabKind.Left, "NONE");
        if (best is { } found)
            return found;
        var next = (MathF.Floor((x + indent) / DefaultTabInterval + Epsilon) + 1) * DefaultTabInterval - indent;
        return new TabStop(next, HTabKind.Left, "NONE");
    }

    /// <summary>
    /// A line's width as it fills: text after a right, centre or decimal tab moves the tab's end so the text ends at,
    /// centres on or puts its decimal point at the stop, as long as the text after it doesn't reach back past the tab.
    /// </summary>
    private sealed class Pen
    {
        private float _fixed;
        private TabStop? _tab;
        private float _segment;
        private float _beforeDecimal = -1;

        /// <summary>Where the line's content ends.</summary>
        public float X => Gap() + _fixed + _segment;

        public float After(float width)
        {
            _segment += width;
            var x = X;
            _segment -= width;
            return x;
        }

        public void Add(float width, char c)
        {
            if (_tab is { Kind: HTabKind.Decimal } && _beforeDecimal < 0 && c == '.')
                _beforeDecimal = _segment;
            _segment += width;
        }

        /// <summary>Starts the text after a tab; returns the width of the previous pending tab, now known.</summary>
        public float Tab(TabStop stop)
        {
            var gap = Gap();
            var x = X;
            _fixed = stop.Kind == HTabKind.Left ? Math.Max(stop.X, x) : x;
            _tab = stop.Kind == HTabKind.Left ? null : stop;
            _segment = 0;
            _beforeDecimal = -1;
            return gap;
        }

        /// <summary>The width of the pending right, centre or decimal tab (0 when there is none).</summary>
        public float Gap()
        {
            if (_tab is not { } tab)
                return 0;
            var start = tab.Kind switch
            {
                HTabKind.Right => tab.X - _segment,
                HTabKind.Center => tab.X - _segment / 2,
                _ => tab.X - (_beforeDecimal >= 0 ? _beforeDecimal : _segment),
            };
            return Math.Max(0, start - _fixed);
        }
    }

    // ───────────────────────── Placing ─────────────────────────

    /// <summary>A line's piece of an item.</summary>
    private sealed class Part(Item item, int start, int length)
    {
        public Item Item { get; } = item;
        public int Start { get; } = start;
        public int Length { get; } = length;
        public ShapedRun? Run { get; set; }
        public float Width { get; set; }

        /// <summary>A picture's size.</summary>
        public (float Width, float Height) Size { get; set; }
    }

    /// <param name="stored">The stored line this line follows, with the paragraph's first one, or null.</param>
    private LineBox Place(ParagraphText source, List<Item> items, int start, int end, bool forced, bool firstLine, float width, Settings settings,
        TextStyle mark, (HLineSegment Line, HLineSegment First)? stored = null)
    {
        var text = source.Text;
        var contentEnd = end;
        while (contentEnd > start && IsSpace(text[contentEnd - 1]))
            contentEnd--;
        var indent = firstLine ? settings.FirstLine : 0;
        var available = width - settings.Left - settings.Right - indent;
        // A stored line that starts or ends off the paragraph's first one goes around an object.
        var shiftLeft = 0f;
        if (stored is var (line, first))
        {
            shiftLeft = (line.HorizontalPosition - first.HorizontalPosition) / 100f;
            var shiftRight = (first.HorizontalPosition + first.HorizontalSize - line.HorizontalPosition - line.HorizontalSize) / 100f;
            available -= shiftLeft + shiftRight;
        }

        // The line's parts, shaped with their context, and each cluster's advance.
        var parts = new List<Part>();
        var advances = new float[contentEnd - start];
        var spacing = new float[contentEnd - start];
        var starts = new bool[contentEnd - start];
        foreach (var item in items)
        {
            var from = Math.Max(item.Start, start);
            var to = Math.Min(item.End, contentEnd);
            if (from >= to)
                continue;
            var part = new Part(item, from, to - from);
            parts.Add(part);
            if (item.Kind != ItemKind.Text)
            {
                starts[from - start] = true;
                if (item.Kind == ItemKind.Object)
                {
                    part.Size = ObjectSize(source, from);
                    advances[from - start] = part.Size.Width;
                }
                continue;
            }
            part.Run = Shape(text, item, from, to - from);
            for (var g = 0; g < part.Run.Glyphs.Length; g++)
            {
                var index = from - start + part.Run.Clusters[g];
                starts[index] = true;
                advances[index] += Advance(part.Run, g, text, from, item.Style) * item.Style.Scale;
            }
            for (var i = from - start; i < to - start; i++)
            {
                if (!starts[i])
                    continue;
                spacing[i] = Spacing(item.Style, advances[i]);
                advances[i] += spacing[i];
            }
        }

        // Tabs in logical order, then the natural width.
        var pen = new Pen();
        var tabs = new Dictionary<int, (TabStop Stop, float Width)>();
        var pendingTab = -1;
        for (var i = start; i < contentEnd; i++)
        {
            if (!starts[i - start])
                continue;
            if (text[i] == '\t')
            {
                var before = pen.X;
                var stop = NextStop(settings, firstLine, before);
                var gap = pen.Tab(stop);
                if (pendingTab >= 0)
                    tabs[pendingTab] = tabs[pendingTab] with { Width = gap };
                // A left tab's width is known now, the others' once the text after them is.
                tabs[i] = (stop, stop.Kind == HTabKind.Left ? pen.X - before : 0);
                pendingTab = stop.Kind == HTabKind.Left ? -1 : i;
            }
            else
            {
                pen.Add(advances[i - start], text[i]);
            }
        }
        if (pendingTab >= 0)
            tabs[pendingTab] = tabs[pendingTab] with { Width = pen.Gap() };
        var natural = pen.X;

        // Alignment: the space left over goes before the line or into it.
        var extra = available - natural;
        var squeeze = 1f;
        var gaps = new float[contentEnd - start];
        var align = settings.Align;
        if (align == HAlign.Justify && forced)
            align = HAlign.Left;
        var clusterIndices = Enumerable.Range(start, contentEnd - start).Where(i => starts[i - start]).ToList();
        var spaces = clusterIndices.Where(i => IsStretchable(text[i])).ToList();
        if (stored is not null && extra < 0 && clusterIndices.Count > 1)
        {
            // 한글 squeezes a stored line that no longer fits between all of its characters.
            foreach (var i in clusterIndices.Take(clusterIndices.Count - 1))
                gaps[i - start] = extra / (clusterIndices.Count - 1);
            extra = 0;
        }
        else if (settings.Wrap == HLineWrap.Squeeze && extra < 0 && natural > 0)
        {
            squeeze = available / natural;
            extra = 0;
        }
        else if (extra < 0 && settings.Condense > 0 && spaces.Count > 0)
        {
            // Spaces shrink by up to the condense share of their width.
            var shrink = Math.Max(extra, -spaces.Sum(i => advances[i - start]) * settings.Condense);
            foreach (var i in spaces)
                gaps[i - start] = shrink / spaces.Count;
            extra -= shrink;
        }
        else if (extra > 0 && align == HAlign.Justify)
        {
            var stretch = spaces.Count > 0 ? spaces : clusterIndices.Take(clusterIndices.Count - 1).ToList();
            foreach (var i in stretch)
                gaps[i - start] = extra / stretch.Count;
            if (stretch.Count > 0)
                extra = 0;
        }
        else if (extra > 0 && align == HAlign.Distribute && clusterIndices.Count > 1)
        {
            foreach (var i in clusterIndices.Take(clusterIndices.Count - 1))
                gaps[i - start] = extra / (clusterIndices.Count - 1);
            extra = 0;
        }
        var offset = Math.Max(0, (align, settings.RightToLeft) switch
        {
            (HAlign.Center, _) => extra / 2,
            (HAlign.Right, false) or (HAlign.Left or HAlign.Justify or HAlign.Distribute, true) => extra,
            _ => 0f,
        });
        // A right-to-left paragraph's start indent is on the right.
        var lineLeft = (settings.RightToLeft ? settings.Right : settings.Left + indent) + shiftLeft;

        // Glyph positions, parts in visual order; the runs are kept in logical order, so the PDF's text reads in the
        // order it was written whatever the direction.
        var runs = new List<(int Start, PlacedRun Run)>();
        var fills = new List<Decoration>();
        var strokes = new List<Decoration>();
        var links = new List<LineLink>();
        var pictures = new List<LineObject>();
        var x = lineLeft + offset;
        foreach (var part in Visual(parts))
        {
            var x0 = x;
            if (part.Item.Kind == ItemKind.Object)
            {
                var (pictureWidth, pictureHeight) = (part.Size.Width * squeeze, part.Size.Height);
                if (source.Objects.TryGetValue(part.Start, out var item) && pictureWidth > 0)
                    pictures.Add(new LineObject(new SKRect(x, -pictureHeight, x + pictureWidth, 0), item, Stack));
                x += pictureWidth + gaps[part.Start - start] * squeeze;
            }
            else if (part.Item.Kind == ItemKind.Tab)
            {
                var (stop, tabWidth) = tabs.GetValueOrDefault(part.Start);
                tabWidth *= squeeze;
                if (stop.Leader is not (null or "" or "NONE") && tabWidth > 0)
                    strokes.Add(Leader(stop, x, x + tabWidth, part.Item.Style));
                x += tabWidth;
            }
            else if (part.Run is { } run)
            {
                var style = part.Item.Style;
                var scale = style.Size / run.UnitsPerEm;
                var scaleX = style.Scale * squeeze;
                var positions = new SKPoint[run.Glyphs.Length];
                for (var g = 0; g < run.Glyphs.Length; g++)
                {
                    if (g > 0 && run.Clusters[g] != run.Clusters[g - 1])
                        x += (spacing[part.Start - start + run.Clusters[g - 1]] + gaps[part.Start - start + run.Clusters[g - 1]]) * squeeze;
                    positions[g] = new SKPoint(x + run.XOffsets[g] * scale * scaleX, -style.Rise - run.YOffsets[g] * scale);
                    x += Advance(run, g, text, part.Start, style) * scaleX;
                }
                if (run.Glyphs.Length > 0)
                    x += (spacing[part.Start - start + run.Clusters[^1]] + gaps[part.Start - start + run.Clusters[^1]]) * squeeze;
                runs.Add((part.Start, new PlacedRun(part.Item.Font.Font, style.Size, run.Glyphs, positions, text.Substring(part.Start, part.Length), run.Clusters)
                {
                    Color = style.Color,
                    ScaleX = scaleX,
                    Embolden = part.Item.Font.SyntheticBold,
                    Slant = part.Item.Font.SyntheticItalic,
                }));
                Decorate(part, x0, x, fills, strokes);
            }
            if (part.Item.Link is { } target)
            {
                if (links.Count > 0 && links[^1].Target == target && Math.Abs(links[^1].Right - x0) < Epsilon)
                    links[^1] = links[^1] with { Right = x };
                else
                    links.Add(new LineLink(x0, x, target));
            }
        }

        var (height, baseline, textHeight) = stored is var (segment, _)
            ? ((segment.VerticalSize + segment.Spacing) / 100f, segment.Baseline / 100f, segment.TextHeight / 100f)
            : LineHeight(parts, settings, mark);
        var bookmarks = source.Bookmarks.Where(b => b.Position >= start && (b.Position < end || b.Position == end && end == text.Length))
            .Select(b => (lineLeft + offset, b.Name)).ToList();
        return new LineBox
        {
            Start = start,
            End = end,
            Height = height,
            Baseline = baseline,
            TextHeight = textHeight,
            Left = lineLeft + offset,
            Width = x - lineLeft - offset,
            Runs = [.. runs.OrderBy(r => r.Start).Select(r => r.Run)],
            Decorations = [.. fills, .. strokes],
            Links = links,
            Bookmarks = bookmarks,
            Objects = pictures,
            Notes = [.. source.Notes.Where(n => n.Key >= start && n.Key < Math.Max(end, start + 1)).OrderBy(n => n.Key).Select(n => n.Value)],
            StoredTop = stored is var (top, _) ? top.VerticalPosition / 100f : null,
            Below = stored is var (spaced, _) ? spaced.Spacing / 100f : Math.Max(0, height - textHeight),
            StoredPageStart = stored is var (flagged, _) && (flagged.Flags & 1) != 0,
            StoredColumnStart = stored is var (column, _) && (column.Flags & 2) != 0,
        };
    }

    private static Decoration Leader(TabStop stop, float from, float to, TextStyle style)
    {
        var thickness = Math.Max(0.5f, style.FontSize / 14);
        var kind = stop.Leader switch
        {
            "DOT" or "CIRCLE" => DecorationKind.Dotted,
            "DASH" or "LONG_DASH" or "DASH_DOT" or "DASH_DOT_DOT" => DecorationKind.Dashed,
            _ => DecorationKind.Line,
        };
        // A little room on either side, as 한글 and Word leave between the leader and the text; at the middle of the
        // lowercase letters.
        var pad = Math.Min(style.FontSize / 4, (to - from) / 4);
        var middle = -style.FontSize / 4;
        return new Decoration(kind, from + pad, to - pad, middle - thickness / 2, middle + thickness / 2, style.Color);
    }

    /// <summary>Shade, underline and strike-through of a part drawn from <paramref name="x0"/> to <paramref name="x1"/>.</summary>
    private void Decorate(Part part, float x0, float x1, List<Decoration> fills, List<Decoration> strokes)
    {
        var style = part.Item.Style;
        if (style.Shade is null && !style.Underline && !style.Strike)
            return;
        var metrics = _shaper.Metrics(part.Item.Font.Font);
        var scale = style.Size / metrics.UnitsPerEm;
        var thickness = metrics.UnderlineThickness > 0 ? metrics.UnderlineThickness * scale : style.Size / 14;
        if (style.Shade is { } shade)
            fills.Add(new Decoration(DecorationKind.Fill, x0, x1, -metrics.Ascender * scale - style.Rise, -metrics.Descender * scale - style.Rise, shade));
        if (style.Underline)
        {
            var position = metrics.UnderlinePosition != 0 ? -metrics.UnderlinePosition * scale : style.Size / 8;
            strokes.Add(new Decoration(DecorationKind.Line, x0, x1, position - style.Rise, position - style.Rise + thickness, style.Color));
        }
        if (style.Strike)
        {
            var strike = metrics.StrikeoutThickness > 0 ? metrics.StrikeoutThickness * scale : thickness;
            var position = metrics.StrikeoutPosition != 0 ? metrics.StrikeoutPosition * scale : style.Size * 0.3f;
            strokes.Add(new Decoration(DecorationKind.Line, x0, x1, -position - style.Rise, -position - style.Rise + strike, style.Color));
        }
    }

    /// <summary>The line's pitch, the distance from its top to the baseline and the height of its text.</summary>
    private (float Height, float Baseline, float TextHeight) LineHeight(List<Part> parts, Settings settings, TextStyle mark)
    {
        var rule = settings.LineHeight;
        var fromFont = settings.FontLineHeight || rule.Rule == HLineHeightRule.Multiple;
        float ascent = 0, descent = 0;
        var any = false;
        foreach (var part in parts)
        {
            if (part.Item.Kind == ItemKind.Object)
            {
                // A picture stands on the baseline.
                ascent = Math.Max(ascent, part.Size.Height);
                any = true;
                continue;
            }
            if (part.Item.Kind != ItemKind.Text)
                continue;
            // 한글's line height follows the font size alone; raised or lowered text takes room only with font metrics.
            var (a, d) = Extent(part.Item.Style, part.Item.Font, fromFont);
            var rise = fromFont ? part.Item.Style.Rise : 0;
            ascent = Math.Max(ascent, a + rise);
            descent = Math.Max(descent, d - rise);
            any = true;
        }
        if (!any)
        {
            var font = Font(mark.Family, mark.Style, mark.Language);
            (ascent, descent) = Extent(mark, font, fromFont);
        }
        var textHeight = ascent + descent;
        var value = rule.Value / 100f;
        var height = rule.Rule switch
        {
            HLineHeightRule.Percent => textHeight * rule.Value / 100f,
            HLineHeightRule.Multiple => textHeight * rule.Value / 240f,
            HLineHeightRule.Exact => value,
            HLineHeightRule.AtLeast => Math.Max(value, textHeight),
            _ => textHeight + value,
        };
        return (Math.Max(0, height), ascent, textHeight);
    }

    /// <summary>Ascent and descent of a style: 한글's share of the size, or the font's own metrics.</summary>
    private (float Ascent, float Descent) Extent(TextStyle style, ResolvedFont font, bool fromFont)
    {
        if (!fromFont)
            return (style.FontSize * HwpAscent, style.FontSize * (1 - HwpAscent));
        var metrics = _shaper.Metrics(font.Font);
        var scale = style.Size / metrics.UnitsPerEm;
        if (metrics.WinAscent > 0 || metrics.WinDescent > 0)
            return (metrics.WinAscent * scale, metrics.WinDescent * scale);
        return (metrics.Ascender * scale, (metrics.LineGap - metrics.Descender) * scale);
    }

    // ───────────────────────── Direction ─────────────────────────

    /// <summary>True when the first strong character is right to left (UAX #9 rules P2, P3).</summary>
    private static bool FirstStrongIsRightToLeft(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            switch (UnicodeData.GetBidiClass(UnicodeData.CodePointAt(text, i)))
            {
                case BidiClass.L:
                    return false;
                case BidiClass.R or BidiClass.AL:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The embedding level of each UTF-16 index, simplified: left-to-right letters 0 (2 in a right-to-left paragraph),
    /// right-to-left letters 1, numbers 2 after right-to-left text (else as left-to-right letters), neutrals the level of
    /// the strong text on both sides when it agrees, else the paragraph's.
    /// </summary>
    private static int[] Levels(string text, bool rightToLeft)
    {
        var paragraph = rightToLeft ? 1 : 0;
        var left = rightToLeft ? 2 : 0;
        // Per index: 'L', 'R', 'N' (number after right-to-left text), ' ' (neutral), '+' (same as the previous).
        var kinds = new char[text.Length];
        var lastStrong = rightToLeft ? 'R' : 'L';
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLowSurrogate(text[i]) && i > 0)
            {
                kinds[i] = '+';
                continue;
            }
            var bidi = UnicodeData.GetBidiClass(UnicodeData.CodePointAt(text, i));
            kinds[i] = bidi switch
            {
                BidiClass.L => 'L',
                BidiClass.R or BidiClass.AL => 'R',
                BidiClass.EN => lastStrong == 'R' ? 'N' : 'L',
                BidiClass.AN => 'N',
                BidiClass.NSM => '+',
                _ => ' ',
            };
            if (bidi is BidiClass.L or BidiClass.R or BidiClass.AL)
                lastStrong = kinds[i];
        }

        var levels = new int[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            switch (kinds[i])
            {
                case 'L':
                    levels[i] = left;
                    break;
                case 'R':
                    levels[i] = 1;
                    break;
                case 'N':
                    levels[i] = 2;
                    break;
                case '+':
                    levels[i] = i > 0 ? levels[i - 1] : paragraph;
                    break;
                default:
                    // Numbers count as right to left for the neutrals around them (UAX #9 N1).
                    var before = Strong(kinds, i, -1, rightToLeft);
                    var after = Strong(kinds, i, 1, rightToLeft);
                    levels[i] = before == after ? before == 'L' ? left : 1 : paragraph;
                    break;
            }
        }
        return levels;
    }

    private static char Strong(char[] kinds, int index, int step, bool rightToLeft)
    {
        for (var i = index + step; i >= 0 && i < kinds.Length; i += step)
        {
            if (kinds[i] is 'L')
                return 'L';
            if (kinds[i] is 'R' or 'N')
                return 'R';
        }
        return rightToLeft ? 'R' : 'L';
    }

    /// <summary>The parts in visual order (UAX #9 rule L2).</summary>
    private static List<Part> Visual(List<Part> parts)
    {
        if (parts.Count < 2 || parts.All(p => p.Item.Level == 0))
            return parts;
        var order = parts.ToList();
        var highest = order.Max(p => p.Item.Level);
        var lowestOdd = order.Where(p => p.Item.RightToLeft).Select(p => p.Item.Level).DefaultIfEmpty(1).Min();
        for (var level = highest; level >= lowestOdd; level--)
        {
            for (var i = 0; i < order.Count;)
            {
                if (order[i].Item.Level < level)
                {
                    i++;
                    continue;
                }
                var j = i;
                while (j < order.Count && order[j].Item.Level >= level)
                    j++;
                order.Reverse(i, j - i);
                i = j;
            }
        }
        return order;
    }
}
