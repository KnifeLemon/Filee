// What goes on a slide and how much room it takes: the blocks of a slide become items (text, a code box, a table, a
// picture), stacked from the top of the content area. Text that doesn't fit continues on a slide of its own with the
// same title, the way PowerPoint's "Continue on a new slide" splits it, at a paragraph, a line of a long paragraph, a
// line of code or a table row (header rows repeat); a picture shrinks to the room left when that is at least a third
// of the area, else moves on too. Nothing is made smaller to fit, except a title of more than two lines (fontScale).
//
// Heights are estimated, without the fonts: each character takes a share of the font size by its kind (narrow, wide,
// capital, Hangul / CJK), lines wrap at spaces (anywhere between CJK characters), a line is 1.2 times the font size.
// The shares are a little wider than Calibri's, so a slide errs on the side of room to spare.

using System.Globalization;
using Filee.Engines.Documents;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx.Pptx;

internal sealed partial class PptxWriter
{
    // ───────────────────────── Geometry (EMU, 16:9 like PowerPoint's default) ─────────────────────────

    internal const long SlideWidth = 12192000;
    internal const long SlideHeight = 6858000;

    /// <summary>The content area of "Title and Content" (PowerPoint's own layout).</summary>
    internal const long BodyX = 838200, BodyY = 1825625, BodyWidth = 10515600, BodyHeight = 4351338;

    /// <summary>The title of "Title and Content".</summary>
    internal const long TitleX = 838200, TitleY = 365125, TitleWidth = 10515600, TitleHeight = 1325563;

    /// <summary>Space between two items stacked in the content area (9 pt).</summary>
    private const long Gap = 114300;

    /// <summary>Text box insets, PowerPoint's defaults.</summary>
    private const long InsetX = 91440, InsetY = 45720;

    /// <summary>A picture is made smaller to stay on a slide only down to this share of the content area.</summary>
    private const double MinPictureShare = 1.0 / 3;

    /// <summary>Line height in multiples of the font size.</summary>
    private const double LineHeight = 1.2;

    private const double Emu = PresentationPackage.EmuPerPoint;

    /// <summary>Text sizes (1/100 pt) of the list levels, PowerPoint's: 28, 24, 20 and 18 pt below.</summary>
    private static readonly int[] LevelSizes = [2800, 2400, 2000, 1800, 1800, 1800, 1800, 1800, 1800];

    private const int QuoteSize = 2000, CodeSize = 1800, TableSize = 1800, MinTableSize = 1000, EndNoteSize = 1800;
    internal const int TitleSize = 4400;

    /// <summary>Most columns of a table on a slide (PowerPoint's limit is 75); further columns are left out.</summary>
    internal const int MaxColumns = 75;

    /// <summary>Monospace font of code (as Pandoc's default "Courier", in the form every system has).</summary>
    internal const string CodeFont = "Courier New";

    // ───────────────────────── Text model ─────────────────────────

    /// <summary>A run of text with its look; <see cref="Break"/> is a line break.</summary>
    private sealed record Run(string Text, HCharFormat Format, bool Code = false, string? Link = null, bool Break = false);

    private enum BulletKind
    {
        None,
        Char,
        Number,
    }

    /// <summary>A paragraph on a slide.</summary>
    private sealed record Para
    {
        public List<Run> Runs { get; init; } = [];

        /// <summary>List level 0..8 (indent level); 0 for paragraphs outside lists.</summary>
        public int Level { get; init; }

        /// <summary>In a list: the paragraph is indented to its level's text, with a bullet or number when it starts an item.</summary>
        public bool InList { get; init; }

        public BulletKind Bullet { get; init; }

        /// <summary>buAutoNum type and the item's number.</summary>
        public string Scheme { get; init; } = "arabicPeriod";
        public int Number { get; init; } = 1;

        /// <summary>Left margin of a paragraph outside lists (block quotes), EMU.</summary>
        public long Margin { get; init; }

        public HAlign? Align { get; init; }

        /// <summary>Font size, 1/100 pt.</summary>
        public int Size { get; init; } = LevelSizes[0];

        /// <summary>Space before, 1/100 pt.</summary>
        public int Before { get; init; } = 1000;

        /// <summary>Every run is bold (a heading below the slide level, a definition term).</summary>
        public bool Bold { get; init; }

        /// <summary>A line of code: monospace, no space before.</summary>
        public bool Code { get; init; }

        /// <summary>Left edge of the text from the box's inner edge, EMU.</summary>
        public long TextLeft => InList ? 228600 + Level * 457200L : Margin;
    }

    // ───────────────────────── Items ─────────────────────────

    private abstract class Item;

    private sealed class TextItem(List<Para> paragraphs) : Item
    {
        public List<Para> Paragraphs { get; } = paragraphs;
    }

    private sealed class CodeItem(List<Para> lines) : Item
    {
        public List<Para> Lines { get; } = lines;
    }

    private sealed class PictureItem(string path, double width, double height, string name) : Item
    {
        public string Path { get; } = path;

        /// <summary>Natural size in points.</summary>
        public double Width { get; } = width;
        public double Height { get; } = height;
        public string Name { get; } = name;
    }

    /// <summary>One cell of the table grid: the cell that starts there, or a position a span covers.</summary>
    private sealed record GridCell(List<Para>? Paragraphs, int RowSpan, int ColSpan, bool HMerge, bool VMerge, string? Fill, HVerticalAlign Align);

    private sealed class TableItem(List<GridCell[]> rows, long[] columns, int headerRows, int size) : Item
    {
        public List<GridCell[]> Rows { get; } = rows;

        /// <summary>Column widths, EMU, summing to the content width.</summary>
        public long[] Columns { get; } = columns;

        /// <summary>Leading rows that are header rows (repeated on continuation slides).</summary>
        public int HeaderRows { get; } = headerRows;

        public int Size { get; } = size;

        /// <summary>The first data row of this part (for banding on continuation slides).</summary>
        public int FirstRow { get; init; }
    }

    /// <summary>An item placed on a slide at <see cref="Y"/> with its height (EMU).</summary>
    private sealed record Placed(Item Item, long X, long Y, long Width, long Height);

    /// <summary>A slide as written.</summary>
    private sealed class Page(SlideKind kind)
    {
        public SlideKind Kind { get; } = kind;
        public List<Run>? Title { get; init; }
        public List<Placed> Items { get; } = [];
        public List<string> Notes { get; } = [];

        /// <summary>Row heights of the tables on the page, by item.</summary>
        public Dictionary<TableItem, long[]> RowHeights { get; } = new(ReferenceEqualityComparer.Instance);
    }

    // ───────────────────────── Blocks → items ─────────────────────────

    /// <summary>The items of a slide's blocks in reading order.</summary>
    private List<Item> Items(IEnumerable<HBlock> blocks)
    {
        var items = new List<Item>();
        List<Para>? text = null;
        List<Para>? code = null;
        void FlushText()
        {
            if (text is { Count: > 0 })
                items.Add(new TextItem(text));
            text = null;
        }
        void FlushCode()
        {
            if (code is not null)
            {
                // Empty lines at the end of a code block (the reader keeps the final newline) take no room.
                while (code.Count > 0 && code[^1].Runs.All(r => r.Text.Length == 0))
                    code.RemoveAt(code.Count - 1);
                if (code.Count > 0)
                    items.Add(new CodeItem(code));
            }
            code = null;
        }

        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph { Role: HBlockRole.Code } line:
                    FlushText();
                    code ??= [];
                    code.Add(new Para { Runs = Runs(line.Inlines, codeBlock: true), Size = CodeSize, Before = 0, Code = true });
                    break;
                case HParagraph paragraph:
                    FlushCode();
                    Paragraph(paragraph, ref text, items, FlushText);
                    break;
                case HTable table:
                    FlushCode();
                    FlushText();
                    if (Table(table) is { } tableItem)
                        items.Add(tableItem);
                    break;
            }
        }
        FlushCode();
        FlushText();
        return items;
    }

    /// <summary>A paragraph's text (and the pictures and text boxes in it, as items and paragraphs of their own).</summary>
    private void Paragraph(HParagraph paragraph, ref List<Para>? text, List<Item> items, Action flushText)
    {
        var current = new List<HInline>();
        var first = true;
        void Emit(ref List<Para>? sink)
        {
            if (current.Count > 0 && Runs(current, codeBlock: false) is { Count: > 0 } runs && runs.Any(r => r.Break || r.Text.Trim().Length > 0))
            {
                sink ??= [];
                sink.Add(Describe(paragraph, runs, first));
                first = false;
            }
            current.Clear();
        }

        foreach (var inline in paragraph.Inlines)
        {
            switch (inline)
            {
                case HImage image:
                    Emit(ref text);
                    if (Picture(image) is { } picture)
                    {
                        flushText();
                        text = null;
                        items.Add(picture);
                    }
                    break;
                case HTextBox box:
                    Emit(ref text);
                    foreach (var inner in box.Blocks.OfType<HParagraph>())
                    {
                        var runs = Runs(inner.Inlines, codeBlock: false);
                        if (runs.Any(r => r.Text.Trim().Length > 0))
                            (text ??= []).Add(new Para { Runs = runs, Size = LevelSizes[0] });
                    }
                    break;
                default:
                    current.Add(inline);
                    break;
            }
        }
        Emit(ref text);
    }

    /// <summary>How a paragraph of the document shows on a slide: list level, bullet or number, size.</summary>
    private Para Describe(HParagraph paragraph, List<Run> runs, bool startsParagraph)
    {
        var align = paragraph.Format.Align is HAlign.Center or HAlign.Right or HAlign.Justify ? paragraph.Format.Align : null;
        if (paragraph.HeadingLevel > 0)
            return new Para { Runs = runs, Bold = true, Before = 1800, Align = align };
        if (paragraph.List is { } list)
        {
            var level = Math.Clamp(list.Level, 0, 8);
            var definition = list.Numbering.Levels.Count > level ? list.Numbering.Levels[level] : null;
            var numbered = list.Numbered && startsParagraph;
            var bullet = !numbered ? BulletKind.None : definition is { Bullet: false } ? BulletKind.Number : BulletKind.Char;
            var number = bullet == BulletKind.Number ? NextNumber(list.Numbering, level, definition!.Start) : 1;
            return new Para
            {
                Runs = runs,
                Level = level,
                InList = true,
                Bullet = bullet,
                Scheme = definition is null ? "arabicPeriod" : Scheme(definition),
                Number = number,
                Size = LevelSizes[level],
                Before = level == 0 ? 1000 : 500,
                Align = align,
            };
        }
        var margin = Math.Clamp((long)(paragraph.Format.Left ?? 0) * 127, 0, BodyWidth / 2);
        return new Para
        {
            Runs = runs,
            Margin = margin,
            Size = margin > 0 ? QuoteSize : LevelSizes[0],
            Align = align,
        };
    }

    /// <summary>The number of the next item of <paramref name="numbering"/> at <paramref name="level"/>; deeper levels start again.</summary>
    private int NextNumber(HNumbering numbering, int level, int start)
    {
        if (!_counters.TryGetValue(numbering, out var counters))
            _counters[numbering] = counters = new int?[9];
        counters[level] = counters[level] is { } previous ? previous + 1 : Math.Max(0, start);
        for (var deeper = level + 1; deeper < counters.Length; deeper++)
            counters[deeper] = null;
        return counters[level]!.Value;
    }

    /// <summary>DrawingML's auto-number scheme for a list level ("^1." → arabicPeriod, "(^1)" → arabicParenBoth, ...).</summary>
    private static string Scheme(HNumberingLevel level)
    {
        var kind = level.Format switch
        {
            "LATIN_SMALL" => "alphaLc",
            "LATIN_CAPITAL" => "alphaUc",
            "ROMAN_SMALL" => "romanLc",
            "ROMAN_CAPITAL" => "romanUc",
            "CIRCLED_DIGIT" => "circleNumDbPlain",
            _ => "arabic",
        };
        if (kind == "circleNumDbPlain")
            return kind;
        var text = level.Text;
        var suffix = text.StartsWith('(') && text.EndsWith(')') ? "ParenBoth" : text.EndsWith(')') ? "ParenR" : "Period";
        // DrawingML has no plain roman / letter numbers without a suffix; Period is the closest.
        return kind + suffix;
    }

    /// <summary>The runs of a list of inlines (links' content included, notes as their numbers).</summary>
    private List<Run> Runs(List<HInline> inlines, bool codeBlock, string? link = null)
    {
        var runs = new List<Run>();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    runs.Add(new Run(text.Text.Replace('\n', ' ').Replace('\r', ' '), text.Format, codeBlock || IsCode(text.Format), link));
                    break;
                case HTab tab:
                    runs.Add(new Run("\t", tab.Format, codeBlock, link));
                    break;
                case HLineBreak lineBreak:
                    runs.Add(new Run("", lineBreak.Format, codeBlock, Break: true));
                    break;
                case HLink hyperlink:
                    runs.AddRange(Runs(hyperlink.Content, codeBlock, Hyperlink(hyperlink.Target) ?? link));
                    break;
                case HNote note when !_writingEndNotes:
                    // Numbered in reading order; its text goes on the "Notes" slide at the end (notes in notes are dropped).
                    _endNotes.Add(note.Blocks);
                    runs.Add(new Run(_endNotes.Count.ToString(CultureInfo.InvariantCulture), (note.Format ?? default) with { Superscript = true, Subscript = null }));
                    break;
            }
        }
        return runs;
    }

    /// <summary>Inline code (the Markdown and HTML readers shade it) is set in the code font.</summary>
    private static bool IsCode(HCharFormat format) =>
        string.Equals(format.Shade, HtmlReader.CodeShade, StringComparison.OrdinalIgnoreCase)
        || format.Font is { } font && (font.Contains("Courier", StringComparison.OrdinalIgnoreCase) || font.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                                       || font.Contains("Mono", StringComparison.OrdinalIgnoreCase));

    /// <summary>An absolute web or mail address for a hyperlink relationship, or null (bookmarks, broken addresses).</summary>
    private static string? Hyperlink(string target)
    {
        if (target.StartsWith('#') || !Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri))
            return null;
        return uri.Scheme is "http" or "https" or "mailto" or "ftp" ? uri.AbsoluteUri : null;
    }

    /// <summary>A picture item, with its natural size; null for a missing or unreadable picture or one too large.</summary>
    private PictureItem? Picture(HImage image)
    {
        if (!File.Exists(image.Path))
            return null;
        double? pixelWidth = null, pixelHeight = null;
        if (PictureSize.Read(image.Path) is { } size)
            (pixelWidth, pixelHeight) = (size.Width, size.Height);
        else
        {
            try
            {
                var info = new MagickImageInfo(image.Path);
                (pixelWidth, pixelHeight) = (info.Width, info.Height);
            }
            catch (MagickException)
            {
                return null;
            }
        }
        if (pixelWidth is not > 0 || pixelHeight is not > 0 || pixelWidth * pixelHeight > DocumentLimits.Default.MaxPicturePixels)
            return null;

        // The document's display size (HWPUNIT, 1/100 pt), else the pixels at 96 dpi.
        double width = pixelWidth.Value * 0.75, height = pixelHeight.Value * 0.75;
        if (image.Width is > 0 && image.Height is > 0)
            (width, height) = (image.Width.Value / 100.0, image.Height.Value / 100.0);
        else if (image.Width is > 0)
            (width, height) = (image.Width.Value / 100.0, image.Width.Value / 100.0 * pixelHeight.Value / pixelWidth.Value);
        else if (image.Height is > 0)
            (width, height) = (image.Height.Value / 100.0 * pixelWidth.Value / pixelHeight.Value, image.Height.Value / 100.0);
        return new PictureItem(image.Path, width, height, Path.GetFileName(image.Path));
    }

    /// <summary>A table as a grid of cells with column widths across the content area; null when it has no cells.</summary>
    private TableItem? Table(HTable table)
    {
        // Lay the cells out on the grid: row spans of earlier rows hold their columns.
        var placed = new List<List<(int Column, HCell Cell)>>();
        var held = new Dictionary<int, int>(); // column → rows still covered by a row span
        var columns = Math.Clamp(table.ColumnCount, 0, MaxColumns);
        foreach (var row in table.Rows)
        {
            var cells = new List<(int, HCell)>();
            var column = 0;
            foreach (var cell in row.Cells)
            {
                if (cell.Column >= 0)
                    column = Math.Max(column, cell.Column);
                while (held.TryGetValue(column, out var rows) && rows > 0)
                    column++;
                if (column >= MaxColumns)
                    break;
                var span = Math.Clamp(cell.ColSpan, 1, MaxColumns - column);
                cells.Add((column, cell));
                for (var c = column; c < column + span; c++)
                    held[c] = Math.Max(held.GetValueOrDefault(c), Math.Max(1, cell.RowSpan));
                column += span;
                columns = Math.Max(columns, column);
            }
            placed.Add(cells);
            foreach (var key in held.Keys.ToList())
                held[key]--;
        }
        if (columns == 0 || placed.All(r => r.Count == 0))
            return null;

        var size = columns <= 4 ? TableSize : Math.Max(MinTableSize, TableSize - (columns - 4) * 150);
        var grid = new List<GridCell[]>();
        for (var r = 0; r < placed.Count; r++)
            grid.Add(new GridCell[columns]);
        for (var r = 0; r < placed.Count; r++)
        {
            foreach (var (column, cell) in placed[r])
            {
                if (grid[r][column] is not null)
                    continue; // overlapped by a span of a damaged table
                var rowSpan = Math.Clamp(cell.RowSpan, 1, placed.Count - r);
                var colSpan = Math.Clamp(cell.ColSpan, 1, columns - column);
                grid[r][column] = new GridCell(CellParagraphs(cell.Blocks, size), rowSpan, colSpan, false, false, Color(cell.Fill), cell.VerticalAlign);
                for (var rr = r; rr < r + rowSpan; rr++)
                {
                    for (var cc = column; cc < column + colSpan; cc++)
                    {
                        if (rr == r && cc == column)
                            continue;
                        grid[rr][cc] ??= new GridCell(null, 1, 1, HMerge: cc > column, VMerge: rr > r, null, HVerticalAlign.Top);
                    }
                }
            }
        }
        for (var r = 0; r < grid.Count; r++)
            for (var c = 0; c < columns; c++)
                grid[r][c] ??= new GridCell([], 1, 1, false, false, null, HVerticalAlign.Top);

        var headerRows = table.Rows.TakeWhile(r => r.Header).Count();
        return new TableItem(grid, ColumnWidths(table, columns), Math.Min(headerRows, grid.Count), size);
    }

    /// <summary>Column widths across the content area, in the table's own proportions.</summary>
    private static long[] ColumnWidths(HTable table, int columns)
    {
        var shares = new double[columns];
        if (table.ColumnWidths is { } absolute && absolute.Take(columns).Any(w => w > 0))
        {
            for (var c = 0; c < columns; c++)
                shares[c] = c < absolute.Length && absolute[c] > 0 ? absolute[c] : 0;
        }
        else if (table.RelativeWidths is { } relative && relative.Take(columns).Any(w => w > 0))
        {
            for (var c = 0; c < columns; c++)
                shares[c] = c < relative.Length && relative[c] > 0 ? relative[c] : 0;
        }
        var given = shares.Where(s => s > 0).ToList();
        var fallback = given.Count == 0 ? 1 : given.Average();
        for (var c = 0; c < columns; c++)
            if (shares[c] <= 0)
                shares[c] = fallback;
        var total = shares.Sum();
        var widths = shares.Select(s => (long)(BodyWidth * s / total)).ToArray();
        widths[^1] += BodyWidth - widths.Sum();
        return widths;
    }

    /// <summary>The paragraphs of a cell: text of nested tables row by row, lists with their marks as text.</summary>
    private List<Para> CellParagraphs(List<HBlock> blocks, int size)
    {
        var paragraphs = new List<Para>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    {
                        var runs = Runs(paragraph.Inlines, paragraph.Role == HBlockRole.Code);
                        if (runs.Count == 0)
                            continue;
                        if (paragraph.List is { Numbered: true } list && list.Numbering.Levels.ElementAtOrDefault(list.Level) is { } level)
                            runs.Insert(0, new Run(level.Bullet ? "• " : NextNumber(list.Numbering, list.Level, level.Start).ToString(CultureInfo.InvariantCulture) + ". ", default));
                        paragraphs.Add(new Para
                        {
                            Runs = runs,
                            Size = size,
                            Before = 0,
                            Bold = paragraph.HeadingLevel > 0,
                            Align = paragraph.Format.Align is HAlign.Center or HAlign.Right ? paragraph.Format.Align : null,
                            Code = paragraph.Role == HBlockRole.Code,
                        });
                        break;
                    }
                case HTable nested:
                    foreach (var row in nested.Rows)
                    {
                        var runs = new List<Run>();
                        foreach (var cell in row.Cells)
                        {
                            if (runs.Count > 0)
                                runs.Add(new Run(" | ", default));
                            runs.AddRange(CellParagraphs(cell.Blocks, size).SelectMany(p => p.Runs.Append(new Run(" ", default))));
                        }
                        if (runs.Count > 0)
                            paragraphs.Add(new Para { Runs = runs, Size = size, Before = 0 });
                    }
                    break;
            }
        }
        return paragraphs;
    }

    /// <summary>"#RRGGBB" → "RRGGBB", or null.</summary>
    private static string? Color(string? color) =>
        color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit) ? color[1..].ToUpperInvariant() : null;

    // ───────────────────────── Measuring ─────────────────────────

    /// <summary>How wide a character is, in multiples of the font size.</summary>
    private static double Width(char c, bool code)
    {
        if (char.IsLowSurrogate(c))
            return 0;
        if (IsWide(c) || char.IsHighSurrogate(c))
            return 1.0;
        if (code)
            return 0.6;
        if (c == ' ')
            return 0.25;
        if (c == '\t')
            return 1.0;
        if ("il.,;:'|!`()[]{}jtfrI\"/".Contains(c))
            return 0.32;
        if (c is 'm' or 'w' or 'M' or 'W' or '@' or '%')
            return 0.88;
        return char.IsUpper(c) ? 0.64 : 0.52;
    }

    /// <summary>Hangul, kana, CJK ideographs and full-width forms: a full em, and a line may break around them.</summary>
    private static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᇿ' or >= '⺀' and <= '鿿' or >= 'ꥠ' and <= '꥿' or >= '가' and <= '퟿'
            or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦';

    /// <summary>A place in a paragraph's runs.</summary>
    private readonly record struct Position(int Run, int Char);

    /// <summary>
    /// Where each line of <paramref name="paragraph"/> starts when it is set <paramref name="width"/> EMU wide: greedy
    /// line breaking at spaces and around wide characters, a word longer than the line broken where it reaches the end.
    /// </summary>
    private static List<Position> LineStarts(Para paragraph, long width)
    {
        var starts = new List<Position> { new(0, 0) };
        var size = paragraph.Size / 100.0;
        var available = Math.Max(size, (width - paragraph.TextLeft - 2 * InsetX) / Emu);
        double x = 0;
        Position? lastBreak = null; // after the last space (or before a wide character) on this line
        double widthAtBreak = 0;
        for (var r = 0; r < paragraph.Runs.Count; r++)
        {
            var run = paragraph.Runs[r];
            if (run.Break)
            {
                starts.Add(new Position(r + 1, 0));
                x = 0;
                lastBreak = null;
                continue;
            }
            var scale = size * (run.Format.Bold == true || paragraph.Bold ? 1.07 : 1) * (run.Format.Superscript == true || run.Format.Subscript == true ? 0.7 : 1);
            var code = run.Code || paragraph.Code;
            for (var i = 0; i < run.Text.Length; i++)
            {
                var c = run.Text[i];
                var w = Width(c, code) * scale;
                if (IsWide(c) && x > 0)
                {
                    lastBreak = new Position(r, i);
                    widthAtBreak = x;
                }
                if (x + w > available && x > 0 && c != ' ')
                {
                    if (lastBreak is { } at && widthAtBreak > 0)
                    {
                        starts.Add(at);
                        x -= widthAtBreak;
                    }
                    else
                    {
                        starts.Add(new Position(r, i));
                        x = 0;
                    }
                    lastBreak = null;
                    widthAtBreak = 0;
                }
                x += w;
                if (c == ' ')
                {
                    lastBreak = new Position(r, i + 1);
                    widthAtBreak = x;
                }
            }
        }
        return starts;
    }

    /// <summary>Height of one line of a paragraph, EMU.</summary>
    private static long LineEmu(Para paragraph) => (long)(paragraph.Size / 100.0 * LineHeight * Emu);

    /// <summary>Height of a paragraph set <paramref name="width"/> EMU wide, with its space before unless it comes first.</summary>
    private static long Height(Para paragraph, long width, bool first) =>
        LineStarts(paragraph, width).Count * LineEmu(paragraph) + (first ? 0 : (long)(paragraph.Before / 100.0 * Emu));

    /// <summary>
    /// The first <paramref name="lines"/> lines of a paragraph and the rest, which continues the same item (no bullet
    /// or number of its own).
    /// </summary>
    private static (Para Head, Para Tail) SplitLines(Para paragraph, long width, int lines)
    {
        var at = LineStarts(paragraph, width)[lines];
        var head = new List<Run>();
        var tail = new List<Run>();
        for (var r = 0; r < paragraph.Runs.Count; r++)
        {
            var run = paragraph.Runs[r];
            if (r < at.Run)
                head.Add(run);
            else if (r > at.Run || at.Char == 0)
                tail.Add(run);
            else
            {
                head.Add(run with { Text = run.Text[..at.Char].TrimEnd() });
                tail.Add(run with { Text = run.Text[at.Char..] });
            }
        }
        while (tail.Count > 0 && (tail[0].Break || tail[0].Text.TrimStart().Length == 0))
            tail.RemoveAt(0);
        if (tail.Count > 0)
            tail[0] = tail[0] with { Text = tail[0].Text.TrimStart() };
        return (paragraph with { Runs = head }, paragraph with { Runs = tail, Bullet = BulletKind.None, Before = 0 });
    }

    /// <summary>Height of each row of a table (EMU); a spanned cell shares its height among its rows.</summary>
    private static long[] RowHeights(TableItem table)
    {
        var heights = new long[table.Rows.Count];
        for (var r = 0; r < table.Rows.Count; r++)
        {
            long height = (long)(table.Size / 100.0 * LineHeight * Emu) + 2 * InsetY;
            var row = table.Rows[r];
            for (var c = 0; c < row.Length; c++)
            {
                if (row[c] is not { Paragraphs: { } paragraphs } cell)
                    continue;
                var width = table.Columns.Skip(c).Take(cell.ColSpan).Sum();
                var content = 2 * InsetY + paragraphs.Select((p, i) => Height(p, width, i == 0)).Sum();
                height = Math.Max(height, content / cell.RowSpan);
            }
            heights[r] = height;
        }
        return heights;
    }

    /// <summary>Rows that must stay on one slide together: a row with the rows its cells span into.</summary>
    private static List<(int Start, int Count)> RowGroups(TableItem table, int from)
    {
        var groups = new List<(int, int)>();
        var r = from;
        while (r < table.Rows.Count)
        {
            var end = r + 1;
            for (var rr = r; rr < end && rr < table.Rows.Count; rr++)
                foreach (var cell in table.Rows[rr])
                    if (cell.Paragraphs is not null)
                        end = Math.Max(end, rr + cell.RowSpan);
            end = Math.Min(end, table.Rows.Count);
            groups.Add((r, end - r));
            r = end;
        }
        return groups;
    }

    // ───────────────────────── Fitting onto slides ─────────────────────────

    /// <summary>
    /// Places a slide's items onto as many slides as they need (the first one gets the notes, every one the title).
    /// </summary>
    private List<Page> Paginate(SlideKind kind, List<Run>? title, List<Item> items, List<string> notes)
    {
        var pages = new List<Page>();
        Page NewPage()
        {
            var page = new Page(kind) { Title = title };
            if (pages.Count == 0)
                page.Notes.AddRange(notes);
            pages.Add(page);
            CheckSlides(pages.Count);
            return page;
        }
        var current = NewPage();
        long y = 0;
        var queue = new LinkedList<Item>(items);
        while (queue.First is { } node)
        {
            queue.RemoveFirst();
            var item = node.Value;
            var top = current.Items.Count == 0 ? 0 : y + Gap;
            var room = BodyHeight - top;
            var empty = current.Items.Count == 0;
            var (fitted, height, rest) = Fit(item, room, empty);
            if (fitted is null)
            {
                // Nothing of it fits here: on to the next slide (an empty slide always takes something).
                current = NewPage();
                y = 0;
                queue.AddFirst(item);
                continue;
            }
            if (fitted is TableItem table)
                current.RowHeights[table] = RowHeights(table);
            var (x, width) = (BodyX, BodyWidth);
            if (fitted is PictureItem)
            {
                var (pictureWidth, _) = PictureBox((PictureItem)fitted, height);
                x = BodyX + (BodyWidth - pictureWidth) / 2;
                width = pictureWidth;
            }
            current.Items.Add(new Placed(fitted, x, BodyY + top, width, height));
            y = top + height;
            if (rest is not null)
            {
                queue.AddFirst(rest);
                current = NewPage();
                y = 0;
            }
        }

        // A picture alone on its slide is centred in the content area, as Pandoc does.
        foreach (var page in pages)
        {
            if (page.Items is [{ Item: PictureItem } only])
                page.Items[0] = only with { Y = BodyY + (BodyHeight - only.Height) / 2 };
        }
        return pages;
    }

    /// <summary>
    /// What of <paramref name="item"/> fits into <paramref name="room"/> EMU: the part placed now (null for none), its
    /// height, and the rest for the next slide (null when all of it fitted).
    /// </summary>
    private (Item? Fitted, long Height, Item? Remainder) Fit(Item item, long room, bool empty)
    {
        switch (item)
        {
            case TextItem text:
                {
                    var (head, height, tail) = FitParagraphs(text.Paragraphs, room, BodyWidth, empty);
                    return (head is null ? null : new TextItem(head), height, tail is null ? null : new TextItem(tail));
                }
            case CodeItem code:
                {
                    var (head, height, tail) = FitParagraphs(code.Lines, room - 2 * CodePadding + 2 * InsetY, BodyWidth - 2 * CodePadding + 2 * InsetX, empty);
                    var extra = 2 * CodePadding - 2 * InsetY;
                    return (head is null ? null : new CodeItem(head), height + extra, tail is null ? null : new CodeItem(tail));
                }
            case PictureItem picture:
                {
                    var full = PictureBox(picture, BodyHeight).Height;
                    if (full <= room)
                        return (picture, full, null);
                    if (room >= BodyHeight * MinPictureShare || empty)
                        return (picture, Math.Max(1, room), null);
                    return (null, 0, null);
                }
            case TableItem table:
                return FitTable(table, room, empty);
        }
        return (null, 0, null);
    }

    /// <summary>Inner padding of a code box (text box insets included), EMU.</summary>
    private const long CodePadding = 137160;

    /// <summary>The size of a picture fitted into the content width and <paramref name="height"/> EMU, never enlarged.</summary>
    private static (long Width, long Height) PictureBox(PictureItem picture, long height)
    {
        var w = picture.Width * Emu;
        var h = picture.Height * Emu;
        var scale = Math.Min(1, Math.Min(BodyWidth / w, height / h));
        return (Math.Max(1, (long)(w * scale)), Math.Max(1, (long)(h * scale)));
    }

    /// <summary>The paragraphs that fit into <paramref name="room"/> EMU, splitting a paragraph between lines.</summary>
    private static (List<Para>? Head, long Height, List<Para>? Tail) FitParagraphs(List<Para> paragraphs, long room, long width, bool empty)
    {
        var used = 2 * InsetY;
        var head = new List<Para>();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = paragraphs[i];
            var height = Height(paragraph, width, head.Count == 0);
            if (used + height <= room)
            {
                head.Add(paragraph);
                used += height;
                continue;
            }
            // Split the paragraph: as many lines as fit here, the rest on the next slide.
            var before = head.Count == 0 ? 0 : (long)(paragraph.Before / 100.0 * Emu);
            var lines = (int)((room - used - before) / LineEmu(paragraph));
            var total = LineStarts(paragraph, width).Count;
            if (lines <= 0 && head.Count == 0 && empty)
                lines = 1; // a slide always takes at least a line
            if (lines > 0 && lines < total)
            {
                var (top, bottom) = SplitLines(paragraph, width, lines);
                head.Add(top);
                used += before + lines * LineEmu(paragraph);
                var tail = new List<Para> { bottom };
                tail.AddRange(paragraphs.Skip(i + 1));
                return (head, used, tail);
            }
            if (lines >= total)
            {
                head.Add(paragraph);
                used += height;
                continue;
            }
            return head.Count == 0 ? (null, 0, null) : (head, used, paragraphs.Skip(i).ToList());
        }
        return (head, used, null);
    }

    /// <summary>
    /// The rows of a table that fit; the header rows repeat on the slide that takes the rest (unless they fill half of
    /// a slide themselves). Rows joined by a row span stay together; a group taller than a slide stands alone.
    /// </summary>
    private static (Item? Fitted, long Height, Item? Remainder) FitTable(TableItem table, long room, bool empty)
    {
        var heights = RowHeights(table);
        var header = table.HeaderRows;
        var used = heights.Take(header).Sum();
        var rows = new List<GridCell[]>(table.Rows.Take(header));
        var next = header;
        foreach (var (start, count) in RowGroups(table, header))
        {
            var height = heights.Skip(start).Take(count).Sum();
            if (used + height > room)
            {
                if (next > header)
                    break;
                if (!empty)
                    return (null, 0, null);
            }
            rows.AddRange(table.Rows.Skip(start).Take(count));
            used += height;
            next = start + count;
            if (used > room)
                break;
        }
        if (used > room && !empty)
            return (null, 0, null);
        var fitted = new TableItem(rows, table.Columns, header, table.Size) { FirstRow = table.FirstRow };
        if (next >= table.Rows.Count)
            return (fitted, used, null);
        var repeat = header > 0 && heights.Take(header).Sum() < BodyHeight / 2;
        var remaining = (repeat ? table.Rows.Take(header) : []).Concat(table.Rows.Skip(next)).ToList();
        var rest = new TableItem(remaining, table.Columns, repeat ? header : 0, table.Size) { FirstRow = table.FirstRow + next - header };
        return (fitted, used, rest);
    }

    /// <summary>The slides a document may become, as many as the pages the layout engine makes of one.</summary>
    private void CheckSlides(int pages)
    {
        if (_slides + pages > DocumentLimits.Default.MaxPages)
            throw DocumentLimits.Exceeded($"The presentation would have more than {DocumentLimits.Default.MaxPages} slides.");
    }
}
