// RtfReader: tables, borders, lists and pictures.
//
// A table is a run of rows: \trowd starts a row's definition (its cells' right edges \cellxN, merges, shading and
// borders), the paragraphs marked \intbl fill its cells, \cell ends a cell and \row the row. The columns of the
// table are every cell edge of every row, so rows with other divisions keep their cells where they are. A first row
// with \trautofit1 makes the layout size the columns as Word's autofit tables (HTableSizing, with \trwWidth and each
// cell's \clwWidth). The first row's \trgaph and \trpadd* give the cells' padding and its \trleft the table's indent,
// negative into the margin as Word writes it (\trleft-108 for \trgaph108); a cell's own \clpad* (with \clpadf* 3,
// twips) wins, read as Word reads them: \clpadl is the top padding and \clpadt the left one.
//
// A table in a cell is a nested table: its paragraphs say how deep (\itapN, 2 for a table in a cell of a table in the
// text), \nestcell ends its cells, and each row's definition comes at the row's end in {\*\nesttableprops ... \nestrow}.
// Tables nested deeper than Nesting.MaxDepth stay in the cells of the deepest one kept.
//
// Borders: a border word (\clbrdrt, \trbrdrh, \brdrb, \box ...) says what the style (\brdrs, \brdrdb, \brdrdot ...),
// width (\brdrw, twips), colour (\brdrcf) and distance (\brsp) after it are for. A cell side without its own border
// takes the row's (\trbrdr*: outer sides, \trbrdrh / \trbrdrv between cells). Paragraphs keep their borders and their
// background (\cbpat, with \shading of \cfpat over it) with Word's distances, as the DOCX reader has them.
//
// Lists come from \listtable (levels with their number format and \leveltext) through \listoverridetable (\lsN →
// list). Pictures are \pict groups (PNG, JPEG, EMF, WMF) as hex or \bin data, sized by \picwgoal / \pichgoal, their
// scale and crop (\piccrop*); see RtfReader.Shapes.cs for pictures in drawing objects.

using System.Text;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class RtfReader
{
    /// <summary>Tables kept inside each other; deeper ones stay in the cells of the deepest one.</summary>
    private const int MaxTableDepth = Nesting.MaxDepth;

    private enum Merge
    {
        None,
        First,
        Continue,
    }

    private sealed class CellDef
    {
        public int Right { get; set; }
        public Merge Horizontal { get; set; }
        public Merge Vertical { get; set; }
        public int Shade { get; set; }

        /// <summary>\clcfpat and \clshdng: a pattern colour over <see cref="Shade"/>, in hundredths of a percent.</summary>
        public int PatternColor { get; set; }
        public int Pattern { get; set; }
        public HVerticalAlign Align { get; set; }

        /// <summary>The cell's own borders (left, right, top, bottom); null where it gives none (the row's apply).</summary>
        public HBorder?[] Borders { get; } = new HBorder?[4];

        /// <summary>The cell's preferred width (\clwWidth) and its unit (\clftsWidth: 2 fiftieths of a percent, 3 twips).</summary>
        public int Width { get; set; }
        public int WidthUnit { get; set; }

        /// <summary>The cell's padding (left, right, top, bottom) and their units (3 twips; else not given).</summary>
        public int[] Padding { get; } = new int[4];
        public int[] PaddingUnits { get; } = new int[4];
    }

    private sealed class RowDef
    {
        public int Left { get; set; }
        public int Height { get; set; }
        public bool Header { get; set; }

        /// <summary>\trkeep: the row isn't split across pages.</summary>
        public bool KeepTogether { get; set; }
        /// <summary>\trql, \trqc, \trqr: the left side, centre, right side; null when the row doesn't say.</summary>
        public HAlign? Align { get; set; }

        /// <summary>\rtlrow: the cells run from the right.</summary>
        public bool RightToLeft { get; set; }

        /// <summary>\trautofit1: the columns fit their content (Word's autofit); else they keep their \cellx widths.</summary>
        public bool AutoFit { get; set; }

        /// <summary>The row's preferred width (\trwWidth) and its unit (\trftsWidth: 2 fiftieths of a percent, 3 twips).</summary>
        public int Width { get; set; }
        public int WidthUnit { get; set; }

        /// <summary>\trgaph: half the space between cells, the cells' left and right padding unless \trpaddl / \trpaddr say.</summary>
        public int? Gap { get; set; }

        /// <summary>
        /// \trpaddl, \trpaddr, \trpaddt, \trpaddb: the cells' padding (left, right, top, bottom), counted when its unit
        /// (\trpaddfl, \trpaddfr, \trpaddft, \trpaddfb) is 3, twips; 0 or none ignores it (\trgaph's, Word 97).
        /// </summary>
        public int[] Padding { get; private init; } = new int[4];
        public int[] PaddingUnits { get; private init; } = new int[4];

        /// <summary>The row's borders: left, right, top, bottom, between rows (\trbrdrh) and between cells (\trbrdrv).</summary>
        public HBorder?[] Borders { get; private init; } = new HBorder?[6];

        /// <summary>A floating table's place (\tpos*, \tph*, \tpv*); null for a table in the text.</summary>
        public TablePlace? Place { get; set; }

        /// <summary>The cells, shared with the rows before that kept this definition; copied when it changes.</summary>
        public List<CellDef> Cells { get; private set; } = [];
        private bool _shared;
        public CellDef Pending { get; set; } = new();

        public void Add(CellDef cell)
        {
            if (_shared)
            {
                Cells = [.. Cells];
                _shared = false;
            }
            Cells.Add(cell);
        }

        /// <summary>The definition of the next row when it doesn't give its own: this one's, sharing its cells.</summary>
        public RowDef Next() => new()
        {
            Left = Left,
            Height = Height,
            KeepTogether = KeepTogether,
            Align = Align,
            RightToLeft = RightToLeft,
            AutoFit = AutoFit,
            Width = Width,
            WidthUnit = WidthUnit,
            Gap = Gap,
            Padding = [.. Padding],
            PaddingUnits = [.. PaddingUnits],
            Borders = [.. Borders],
            Place = Place,
            Cells = Cells,
            _shared = true,
        };
    }

    /// <summary>
    /// Where a floating table is: what it is measured from (\tphcol / \tphmrg / \tphpg across, \tpvmrg / \tpvpara /
    /// \tpvpg down; Word's defaults are the column and the margin), its offsets in twips or alignment, the distance text
    /// keeps from it (\tdfrmtxt*) and \tabsnoovrlp.
    /// </summary>
    private sealed record TablePlace(string Across = "COLUMN", string Down = "PAGE", int X = 0, int Y = 0, string AlignX = "LEFT",
        string AlignY = "TOP", int Left = 0, int Right = 0, int Top = 0, int Bottom = 0, bool NoOverlap = false);

    private sealed class TableBuilder
    {
        public RowDef Def { get; set; } = new();
        public List<HBlock> Cell { get; set; } = [];
        public List<List<HBlock>> Cells { get; } = [];
        public List<(RowDef Def, List<List<HBlock>> Cells)> Rows { get; } = [];
    }

    /// <summary>The level of the table a paragraph is in: \itap, else 1 for \intbl, else 0; at most <see cref="MaxTableDepth"/>.</summary>
    private static int TableDepth(ParaProps p) => Math.Min(RawTableDepth(p), MaxTableDepth);

    private static int RawTableDepth(ParaProps p) => p.Itap > 0 ? p.Itap : p.InTable ? 1 : 0;

    /// <summary>
    /// The level of the table row definition words are for: 1 (the table in the text), or in \*\nesttableprops the
    /// nested table of the paragraph; 0 for one nested deeper than kept, whose words are ignored.
    /// </summary>
    private int RowLevel()
    {
        if (!_g.NestedRow)
            return 1;
        var depth = Math.Max(2, RawTableDepth(_g.Para));
        return depth > MaxTableDepth ? 0 : depth;
    }

    /// <summary>The open table at <paramref name="depth"/> (1 = in the text) of <paramref name="story"/>, opening it and those around it.</summary>
    private static TableBuilder TableAt(Story story, int depth)
    {
        while (story.Tables.Count < depth)
            story.Tables.Add(new TableBuilder());
        return story.Tables[depth - 1];
    }

    /// <summary>Closes the tables of <paramref name="story"/> nested deeper than <paramref name="depth"/>, each into the cell around it.</summary>
    private void CloseTables(Story story, int depth)
    {
        while (story.Tables.Count > Math.Max(0, depth))
        {
            var builder = story.Tables[^1];
            story.Tables.RemoveAt(story.Tables.Count - 1);
            BuildTable(builder, story.Tables.Count > 0 ? story.Tables[^1].Cell : story.Blocks);
        }
    }

    /// <summary>Adds the open tables of <paramref name="story"/> to its blocks.</summary>
    private void CloseTable(Story story) => CloseTables(story, 0);

    private bool Table(string word, int? parameter)
    {
        if (_g.Dest != Dest.Text)
            return word is "trowd" or "cell" or "row" or "cellx" or "nestcell" or "nestrow";
        var value = parameter ?? 0;
        var story = _g.Story;
        switch (word)
        {
            case "cell":
                EndCell(story, 1);
                return true;
            case "nestcell":
                // A cell of a nested table ends; one nested deeper than kept (or a paragraph not nested) ends a paragraph.
                if (RawTableDepth(_g.Para) is var depth and >= 2 and <= MaxTableDepth)
                    EndCell(story, depth);
                else
                    EndParagraph(story, force: false);
                return true;
            case "row":
                EndTableRow(story, 1);
                return true;
            case "nestrow":
                // A nested table's row ends (in \*\nesttableprops, or after its cells); one nested deeper than kept doesn't.
                var level = _g.NestedRow ? RowLevel() : RawTableDepth(_g.Para) is var nested and >= 2 and <= MaxTableDepth ? nested : 0;
                if (level >= 2)
                    EndTableRow(story, level);
                return true;
        }

        if (word is not ("trowd" or "trleft" or "trrh" or "trhdr" or "trkeep" or "trql" or "trqc" or "trqr" or "rtlrow" or "ltrrow" or "trgaph"
            or "trpaddl" or "trpaddr" or "trpaddt" or "trpaddb" or "trpaddfl" or "trpaddfr" or "trpaddft" or "trpaddfb" or "clwWidth"
            or "clftsWidth" or "trautofit" or "trwWidth" or "trftsWidth" or "clmgf" or "clmrg" or "clvmgf" or "clvmrg" or "clcbpat"
            or "clcfpat" or "clshdng" or "clvertalc" or "clvertalb" or "clvertalt" or "clpadl" or "clpadr" or "clpadt" or "clpadb"
            or "clpadfl" or "clpadfr" or "clpadft" or "clpadfb" or "cellx" or "tphcol" or "tphmrg" or "tphpg" or "tpvmrg" or "tpvpara"
            or "tpvpg" or "tposx" or "tposnegx" or "tposxc" or "tposxi" or "tposxo" or "tposxr" or "tposxl" or "tposy" or "tposnegy"
            or "tposyt" or "tposyil" or "tposyc" or "tposyb" or "tposyin" or "tposyout" or "tposyoutv" or "tdfrmtxtLeft" or "tdfrmtxtRight"
            or "tdfrmtxtTop" or "tdfrmtxtBottom" or "tabsnoovrlp"))
            return false;
        if (RowLevel() is not (> 0 and var rowLevel))
            return true; // a row of a table nested deeper than kept
        var def = TableAt(story, rowLevel).Def;
        if (word.StartsWith("tp", StringComparison.Ordinal) || word.StartsWith("tdfrmtxt", StringComparison.Ordinal) || word == "tabsnoovrlp")
        {
            def.Place = TablePosition(def.Place ?? new TablePlace(), word, parameter);
            return true;
        }
        switch (word)
        {
            case "trowd": TableAt(story, rowLevel).Def = new RowDef(); break;
            case "trleft": def.Left = value; break;
            case "trrh": def.Height = value; break;
            case "trhdr": def.Header = true; break;
            case "trkeep": def.KeepTogether = parameter != 0; break;
            case "trql": def.Align = HAlign.Left; break;
            case "trqc": def.Align = HAlign.Center; break;
            case "trqr": def.Align = HAlign.Right; break;
            case "rtlrow" or "ltrrow": def.RightToLeft = word == "rtlrow"; break;
            case "trgaph": def.Gap = value; break;
            case "trpaddl" or "trpaddr" or "trpaddt" or "trpaddb": def.Padding[Side(word[^1])] = value; break;
            case "trpaddfl" or "trpaddfr" or "trpaddft" or "trpaddfb": def.PaddingUnits[Side(word[^1])] = value; break;
            case "clwWidth": def.Pending.Width = value; break;
            case "clftsWidth": def.Pending.WidthUnit = value; break;
            case "trautofit": def.AutoFit = (parameter ?? 1) != 0; break;
            case "trwWidth": def.Width = value; break;
            case "trftsWidth": def.WidthUnit = value; break;
            case "clmgf": def.Pending.Horizontal = Merge.First; break;
            case "clmrg": def.Pending.Horizontal = Merge.Continue; break;
            case "clvmgf": def.Pending.Vertical = Merge.First; break;
            case "clvmrg": def.Pending.Vertical = Merge.Continue; break;
            case "clcbpat": def.Pending.Shade = value; break;
            case "clcfpat": def.Pending.PatternColor = value; break;
            case "clshdng": def.Pending.Pattern = Math.Clamp(value, 0, 10000); break;
            case "clvertalc": def.Pending.Align = HVerticalAlign.Center; break;
            case "clvertalb": def.Pending.Align = HVerticalAlign.Bottom; break;
            case "clvertalt": def.Pending.Align = HVerticalAlign.Top; break;
            // Word writes the top padding as \clpadl and the left one as \clpadt, and reads them so.
            case "clpadl" or "clpadr" or "clpadt" or "clpadb": def.Pending.Padding[CellSide(word[^1])] = value; break;
            case "clpadfl" or "clpadfr" or "clpadft" or "clpadfb": def.Pending.PaddingUnits[CellSide(word[^1])] = value; break;
            case "cellx":
                // Cells a row defines past the widest table are cut, as the other readers cut columns past it; their
                // text goes to its last column.
                if (def.Cells.Count < _budget.Limits.MaxTableColumns)
                {
                    def.Pending.Right = value;
                    def.Add(def.Pending);
                }
                def.Pending = new CellDef();
                break;
        }
        return true;
    }

    /// <summary>A floating table's place with one more of its words.</summary>
    private static TablePlace TablePosition(TablePlace place, string word, int? parameter)
    {
        var value = parameter ?? 0;
        // Offsets and distances in twips, kept to a few pages.
        var twips = Math.Clamp(value, -31680 * 4, 31680 * 4);
        var distance = Math.Clamp(value, 0, 31680);
        return word switch
        {
            "tphcol" => place with { Across = "COLUMN" },
            "tphmrg" => place with { Across = "PAGE" },
            "tphpg" => place with { Across = "PAPER" },
            "tpvmrg" => place with { Down = "PAGE" },
            "tpvpara" => place with { Down = "PARA" },
            "tpvpg" => place with { Down = "PAPER" },
            "tposx" or "tposnegx" => place with { X = twips, AlignX = "LEFT" },
            "tposxl" => place with { X = 0, AlignX = "LEFT" },
            "tposxc" => place with { X = 0, AlignX = "CENTER" },
            "tposxr" => place with { X = 0, AlignX = "RIGHT" },
            "tposxi" => place with { X = 0, AlignX = "INSIDE" },
            "tposxo" => place with { X = 0, AlignX = "OUTSIDE" },
            "tposy" or "tposnegy" => place with { Y = twips, AlignY = "TOP" },
            "tposyt" or "tposyil" => place with { Y = 0, AlignY = "TOP" },
            "tposyc" => place with { Y = 0, AlignY = "CENTER" },
            "tposyb" => place with { Y = 0, AlignY = "BOTTOM" },
            "tposyin" => place with { Y = 0, AlignY = "INSIDE" },
            "tposyout" or "tposyoutv" => place with { Y = 0, AlignY = "OUTSIDE" },
            "tdfrmtxtLeft" => place with { Left = distance },
            "tdfrmtxtRight" => place with { Right = distance },
            "tdfrmtxtTop" => place with { Top = distance },
            "tdfrmtxtBottom" => place with { Bottom = distance },
            "tabsnoovrlp" => place with { NoOverlap = parameter != 0 },
            _ => place,
        };
    }

    /// <summary>Ends the cell of the table at <paramref name="depth"/>: its last paragraph, the tables nested in it, the cell.</summary>
    private void EndCell(Story story, int depth)
    {
        if (TableDepth(_g.Para) == 0)
            _g.Para = _g.Para with { InTable = true };
        // \cell ends the cell's last paragraph. An empty cell keeps its paragraph, with its format (line spacing, the
        // mark's font size), which the row's height depends on; after other paragraphs an empty one is left out, as
        // writers that end every paragraph with \par before \cell (Pandoc) mean none.
        EndParagraph(story, force: TableAt(story, depth).Cell.Count == 0);
        CloseTables(story, depth);
        var builder = TableAt(story, depth);
        TableCells(1);
        builder.Cells.Add(builder.Cell);
        builder.Cell = [];
    }

    /// <summary>Ends the row of the table at <paramref name="depth"/>.</summary>
    private void EndTableRow(Story story, int depth)
    {
        EndRun(story.Inlines);
        if (story.Inlines.Count > 0)
        {
            if (TableDepth(_g.Para) == 0)
                _g.Para = _g.Para with { InTable = true };
            EndParagraph(story, force: false);
        }
        CloseTables(story, depth);
        var finished = TableAt(story, depth);
        EndRow(finished);
        finished.Cell = [];
        // The next row repeats its definition, or keeps this one when it doesn't.
        finished.Def = finished.Def.Next();
    }

    /// <summary>The index of a padding side by the last letter of its word: left, right, top, bottom.</summary>
    private static int Side(char letter) => letter switch
    {
        'l' => 0,
        'r' => 1,
        't' => 2,
        _ => 3,
    };

    /// <summary>The side a cell's \clpad* word is for: Word's \clpadl is the top and \clpadt the left.</summary>
    private static int CellSide(char letter) => letter switch
    {
        'l' => 2,
        't' => 0,
        _ => Side(letter),
    };

    /// <summary>
    /// The cells' padding Word gives a row (HWPUNIT): \trgaph left and right unless \trpaddl / \trpaddr in twips say
    /// otherwise, \trpaddt / \trpaddb above and below; null when the row gives none (the layout's default).
    /// </summary>
    private static HInsets? Padding(RowDef def)
    {
        int? Side(int i) => def.PaddingUnits[i] == 3 ? Math.Clamp(def.Padding[i], 0, 31680) : null;
        var (left, right, top, bottom) = (Side(0), Side(1), Side(2), Side(3));
        if (def.Gap is null && left is null && right is null && top is null && bottom is null)
            return null;
        var gap = Math.Clamp(def.Gap ?? 0, 0, 31680);
        return new HInsets(HwpxUnits.FromTwips(left ?? gap), HwpxUnits.FromTwips(right ?? gap), HwpxUnits.FromTwips(top ?? 0),
            HwpxUnits.FromTwips(bottom ?? 0));
    }

    /// <summary>Word's cell padding where a table gives none: 108 twips at the sides.</summary>
    private static readonly HInsets WordCellPadding = new(HwpxUnits.FromTwips(108), HwpxUnits.FromTwips(108), 0, 0);

    /// <summary>A cell's own padding (\clpad* in twips) over the table's; null when it gives none.</summary>
    private static HInsets? Padding(CellDef cell, HInsets? table)
    {
        if (!cell.PaddingUnits.Contains(3))
            return null;
        var around = table ?? WordCellPadding;
        int Side(int i, int fallback) => cell.PaddingUnits[i] == 3 ? HwpxUnits.FromTwips(Math.Clamp(cell.Padding[i], 0, 31680)) : fallback;
        return new HInsets(Side(0, around.Left), Side(1, around.Right), Side(2, around.Top), Side(3, around.Bottom));
    }

    /// <summary>A cell's preferred width (\clwWidth with \clftsWidth 3, twips, or 2, fiftieths of a percent); null for none.</summary>
    private static HPreferredWidth? Preferred(CellDef cell) => cell switch
    {
        { WidthUnit: 3, Width: > 0 } => new HPreferredWidth(HWidthType.Absolute, HwpxUnits.FromTwips(Math.Min(cell.Width, 31680))),
        { WidthUnit: 2, Width: > 0 } => new HPreferredWidth(HWidthType.Percent, Math.Min(10, cell.Width / 5000.0)),
        { WidthUnit: 1 } => HPreferredWidth.Auto,
        _ => null,
    };

    /// <summary>Table cells read so far, in every table of the document; cells a row defines without content count too.</summary>
    private long _tableCells;

    private void TableCells(int count)
    {
        _tableCells += count;
        if (_tableCells > _budget.Limits.MaxParagraphs)
            throw DocumentLimits.TooLarge($"The tables of the document have more than {DocumentLimits.Count(_budget.Limits.MaxParagraphs)} cells.");
    }

    /// <summary>Ends the row of <paramref name="builder"/>, if it has cells (counted at \cell; its other defined cells here).</summary>
    private void EndRow(TableBuilder builder)
    {
        if (builder.Cells.Count == 0)
            return;
        TableCells(Math.Max(0, builder.Def.Cells.Count - builder.Cells.Count));
        builder.Rows.Add((builder.Def, [.. builder.Cells]));
        builder.Cells.Clear();
    }

    /// <summary>Adds the table <paramref name="builder"/> read to <paramref name="target"/>: the blocks around it or the cell it is in.</summary>
    private void BuildTable(TableBuilder builder, List<HBlock> target)
    {
        EndRow(builder);
        if (builder.Rows.Count == 0)
        {
            // Paragraphs marked \intbl without any row: plain paragraphs.
            target.AddRange(builder.Cell);
            return;
        }
        _budget.Object();

        // The grid: every cell edge of every row (rows that share their cells add them once).
        var edges = new SortedSet<int>();
        var seen = new HashSet<List<CellDef>>(ReferenceEqualityComparer.Instance);
        foreach (var (def, _) in builder.Rows)
        {
            edges.Add(def.Left);
            if (!seen.Add(def.Cells))
                continue;
            foreach (var cell in def.Cells)
                edges.Add(cell.Right);
        }
        // Edges past the widest table are cut there: cells beyond it go to its last column.
        var grid = edges.Take(_budget.Limits.MaxTableColumns + 1).ToList();
        var table = new HTable
        {
            ColumnCount = Math.Max(1, grid.Count - 1),
            ColumnWidths = grid.Count > 1 ? [.. grid.Zip(grid.Skip(1), (a, b) => HwpxUnits.FromTwips(Math.Max(1, b - a)))] : null,
            Align = builder.Rows[0].Def.Align ?? HAlign.Left,
            // Word's rows start \trgaph before the text (\trleft-108 with \trgaph108): in the margin.
            Indent = HwpxUnits.FromTwips(Math.Max(-31680, builder.Rows[0].Def.Left)),
            CellMargin = Padding(builder.Rows[0].Def),
            Borders = HBorders.Empty,
            // The cells of a right-to-left row run from the right; the model's Align then names the start side, so
            // \trqr (the right side) is the start, and a row that doesn't say starts at the right too.
            RightToLeft = builder.Rows[0].Def.RightToLeft,
        };
        // A floating table (\tpos* on its first row): placed where the row says, text going around it.
        if (builder.Rows[0].Def.Place is { } place)
        {
            table.Float = new HAnchor(place.Across, place.AlignX, HwpxUnits.FromTwips(place.X), place.Down, place.AlignY,
                HwpxUnits.FromTwips(place.Y), "SQUARE", !place.NoOverlap);
            table.FloatDistance = new HInsets(HwpxUnits.FromTwips(place.Left), HwpxUnits.FromTwips(place.Right),
                HwpxUnits.FromTwips(place.Top), HwpxUnits.FromTwips(place.Bottom));
        }
        if (table.RightToLeft && builder.Rows[0].Def.Align is HAlign.Left or HAlign.Right)
            table.Align = builder.Rows[0].Def.Align == HAlign.Left ? HAlign.Right : HAlign.Left;
        // \trautofit1: Word sizes the columns as its autofit tables, from the \cellx widths and the cells' content (and
        // the preferred width); without it they keep their widths, as the other formats' tables.
        if (builder.Rows[0].Def is { AutoFit: true } autoFit)
        {
            var width = autoFit switch
            {
                { WidthUnit: 3, Width: > 0 } => new HPreferredWidth(HWidthType.Absolute, HwpxUnits.FromTwips(Math.Min(autoFit.Width, 31680))),
                { WidthUnit: 2, Width: > 0 } => new HPreferredWidth(HWidthType.Percent, Math.Min(10, autoFit.Width / 5000.0)),
                _ => HPreferredWidth.Auto,
            };
            table.Sizing = new HTableSizing(false, width, table.ColumnWidths ?? []);
        }
        var openVertical = new Dictionary<int, HCell>();
        for (var r = 0; r < builder.Rows.Count; r++)
        {
            var (def, contents) = builder.Rows[r];
            var row = new HRow
            {
                Height = def.Height == 0 ? null : HwpxUnits.FromTwips(Math.Abs(def.Height)),
                Header = def.Header,
                CantSplit = def.KeepTogether,
            };
            var left = def.Left;
            HCell? previous = null;
            // The cell before was below a cell spanning rows (\clvmrg): \clmrg cells after it belong to that cell too.
            var continuing = false;
            for (var i = 0; i < contents.Count; i++)
            {
                var cellDef = i < def.Cells.Count ? def.Cells[i] : new CellDef { Right = left + 1440 };
                var l = grid.BinarySearch(left);
                var from = Math.Max(0, l >= 0 ? l : ~l);
                var right = grid.BinarySearch(cellDef.Right);
                var to = right >= 0 ? right : Math.Max(from + 1, ~right);
                left = cellDef.Right;
                var span = Math.Max(1, to - from);
                if (cellDef.Horizontal == Merge.Continue && previous is not null)
                {
                    previous.ColSpan += span;
                    // The merged cell ends where the last of its parts does: that part's right border is its.
                    if (previous.Borders is { } merged)
                        previous.Borders = merged with { Right = CellBorders(cellDef, def, r, builder.Rows.Count, i, contents.Count).Right };
                    continue;
                }
                if (cellDef.Horizontal == Merge.Continue && continuing)
                    continue;
                if (cellDef.Vertical == Merge.Continue && openVertical.TryGetValue(from, out var above))
                {
                    above.RowSpan++;
                    if (above.Borders is { } spanning)
                        above.Borders = spanning with { Bottom = CellBorders(cellDef, def, r, builder.Rows.Count, i, contents.Count).Bottom };
                    previous = null;
                    continuing = true;
                    continue;
                }
                continuing = false;
                var cell = new HCell
                {
                    Column = from,
                    ColSpan = span,
                    VerticalAlign = cellDef.Align,
                    Fill = Fill(new Shading(cellDef.Shade, cellDef.PatternColor, cellDef.Pattern)),
                    Borders = CellBorders(cellDef, def, r, builder.Rows.Count, i, contents.Count),
                    Width = Preferred(cellDef),
                    CellMargin = Padding(cellDef, table.CellMargin),
                };
                cell.Blocks.AddRange(contents[i].Count > 0 ? contents[i] : [new HParagraph()]);
                if (cellDef.Vertical == Merge.First)
                    openVertical[from] = cell;
                else
                    openVertical.Remove(from);
                row.Cells.Add(cell);
                previous = cell;
            }
            table.Rows.Add(row);
        }
        target.Add(table);
        // Paragraphs after the last row that no \cell ended keep their text after the table.
        target.AddRange(builder.Cell);
    }

    /// <summary>
    /// A cell's borders: its own sides, else the row's: \trbrdrl / \trbrdrr at the row's ends and \trbrdrv between
    /// cells, \trbrdrt / \trbrdrb in the first and last rows and \trbrdrh between rows.
    /// </summary>
    private static HBorders CellBorders(CellDef cell, RowDef row, int rowIndex, int rows, int cellIndex, int cells)
    {
        var own = cell.Borders;
        var around = row.Borders;
        return new HBorders(
            own[0] ?? (cellIndex == 0 ? around[0] : around[5]) ?? HBorder.None,
            own[1] ?? (cellIndex == cells - 1 ? around[1] : around[5]) ?? HBorder.None,
            own[2] ?? (rowIndex == 0 ? around[2] : around[4]) ?? HBorder.None,
            own[3] ?? (rowIndex == rows - 1 ? around[3] : around[4]) ?? HBorder.None);
    }

    // ───────────────────────── Borders and shading ─────────────────────────

    /// <summary>What the border words after a border's side word are for.</summary>
    private enum BorderOwner
    {
        None,
        Paragraph,
        Cell,
        Row,
    }

    /// <summary>The side of the border being read (as <see cref="ParaBox.With"/> numbers them) and the words read for it so far.</summary>
    private BorderOwner _borderOwner;
    private int _borderSide;
    private HBorderStyle _borderStyle;
    private bool _borderThick;

    /// <summary>\brdrtbl: no border of its own; a cell side then takes the row's.</summary>
    private bool _borderUnset;
    private int _borderWidth = -1;
    private int _borderColor;
    private int _borderSpace;

    /// <summary>A paragraph's borders and their distances from the text (HWPUNIT).</summary>
    private sealed record ParaBox(HBorder Left, HBorder Right, HBorder Top, HBorder Bottom, HBorder Between, HInsets Space)
    {
        public static readonly ParaBox Empty = new(HBorder.None, HBorder.None, HBorder.None, HBorder.None, HBorder.None, default);

        public HBorders Borders => new(Left, Right, Top, Bottom);

        public bool Visible => new[] { Left, Right, Top, Bottom, Between }.Any(b => b.Style != HBorderStyle.None);

        /// <summary>This box with <paramref name="side"/> (0 left, 1 right, 2 top, 3 bottom, 4 between, 5 all four) set.</summary>
        public ParaBox With(int side, HBorder border, int space) => side switch
        {
            0 => this with { Left = border, Space = Space with { Left = space } },
            1 => this with { Right = border, Space = Space with { Right = space } },
            2 => this with { Top = border, Space = Space with { Top = space } },
            3 => this with { Bottom = border, Space = Space with { Bottom = space } },
            4 => this with { Between = border },
            _ => new ParaBox(border, border, border, border, Between, new HInsets(space, space, space, space)),
        };
    }

    /// <summary>A background: a colour (\cbpat), with a pattern colour (\cfpat) over it at \shading hundredths of a percent.</summary>
    private readonly record struct Shading(int Back, int Fore, int Percent);

    /// <summary>The colour a background shows ("#RRGGBB"), or null for none.</summary>
    private string? Fill(Shading shading)
    {
        var back = shading.Back > 0 && shading.Back < _colors.Count ? _colors[shading.Back] : null;
        if (shading.Percent <= 0)
            return back;
        var fore = shading.Fore > 0 && shading.Fore < _colors.Count ? _colors[shading.Fore] ?? "#000000" : "#000000";
        var under = back ?? "#FFFFFF";
        var share = shading.Percent / 10000.0;
        int Channel(string color, int shift) => Convert.ToInt32(color[1..], 16) >> shift & 0xFF;
        int Mix(int shift) => (int)Math.Round(Channel(under, shift) * (1 - share) + Channel(fore, shift) * share);
        return $"#{Mix(16):X2}{Mix(8):X2}{Mix(0):X2}";
    }

    /// <summary>The paragraph's border and background as the layout draws Word's (each line its distance outside the text).</summary>
    private HParaLayout? ParagraphBox(ParaProps p)
    {
        var fill = Fill(p.Shading);
        if (p.Box is not { Visible: true } box)
            return fill is null ? null : new HParaLayout(Border: new HParaBorder(HBorders.Empty, fill, default, Connect: true));
        return new HParaLayout(Border: new HParaBorder(box.Borders, fill, box.Space, Connect: true,
            Between: box.Between.Style == HBorderStyle.None ? null : box.Between));
    }

    /// <summary>Border words: a side (\clbrdrt, \trbrdrh, \brdrb, \box ...) and the style, width, colour and distance of its line.</summary>
    private bool Borders(string word, int? parameter)
    {
        var (owner, side) = word switch
        {
            "brdrl" => (BorderOwner.Paragraph, 0),
            "brdrr" => (BorderOwner.Paragraph, 1),
            "brdrt" => (BorderOwner.Paragraph, 2),
            "brdrb" => (BorderOwner.Paragraph, 3),
            "brdrbtw" => (BorderOwner.Paragraph, 4),
            "box" => (BorderOwner.Paragraph, 5),
            "clbrdrl" => (BorderOwner.Cell, 0),
            "clbrdrr" => (BorderOwner.Cell, 1),
            "clbrdrt" => (BorderOwner.Cell, 2),
            "clbrdrb" => (BorderOwner.Cell, 3),
            "trbrdrl" => (BorderOwner.Row, 0),
            "trbrdrr" => (BorderOwner.Row, 1),
            "trbrdrt" => (BorderOwner.Row, 2),
            "trbrdrb" => (BorderOwner.Row, 3),
            "trbrdrh" => (BorderOwner.Row, 4),
            "trbrdrv" => (BorderOwner.Row, 5),
            _ => (BorderOwner.None, -1),
        };
        if (owner != BorderOwner.None)
        {
            (_borderOwner, _borderSide) = (owner, side);
            (_borderStyle, _borderThick, _borderWidth, _borderColor, _borderSpace, _borderUnset) = (HBorderStyle.None, false, -1, 0, 0, false);
            return true;
        }
        var value = parameter ?? 0;
        switch (word)
        {
            case "brdrw":
                _borderWidth = Math.Clamp(value, 0, 255);
                break;
            case "brdrcf":
                _borderColor = value;
                break;
            case "brsp":
                _borderSpace = Math.Clamp(value, 0, 31680);
                break;
            default:
                if (BorderStyle(word) is not { } style)
                    return word is "brdrbar" or "brdrframe"; // a bar outside the paragraph: not drawn
                (_borderStyle, _borderThick) = style;
                _borderUnset = word == "brdrtbl";
                break;
        }
        SetBorder();
        return true;
    }

    /// <summary>The model's style of a border style word, and whether the line is twice as thick (\brdrth); null for others.</summary>
    private static (HBorderStyle, bool)? BorderStyle(string word) => word switch
    {
        "brdrnone" or "brdrnil" or "brdrtbl" => (HBorderStyle.None, false),
        "brdrs" or "brdrsh" or "brdrhair" or "brdrinset" or "brdroutset" or "brdrwavy" or "brdremboss" or "brdrengrave" => (HBorderStyle.Solid, false),
        "brdrth" => (HBorderStyle.Solid, true),
        "brdrdot" => (HBorderStyle.Dot, false),
        "brdrdash" or "brdrdashsm" => (HBorderStyle.Dash, false),
        "brdrdashd" or "brdrdashdd" or "brdrdashdotstr" => (HBorderStyle.DashDot, false),
        "brdrdb" or "brdrtriple" or "brdrwavydb" => (HBorderStyle.Double, false),
        _ when word.StartsWith("brdrtnth", StringComparison.Ordinal) || word.StartsWith("brdrthtn", StringComparison.Ordinal) => (HBorderStyle.Double, false),
        _ => null,
    };

    /// <summary>The border read so far into what its side word was for.</summary>
    private void SetBorder()
    {
        // Paragraph borders in the text and in paragraph styles; cell and row ones in the text.
        if (_borderOwner == BorderOwner.None || _g.Dest != Dest.Text && !(_g.Dest == Dest.StyleSheet && _borderOwner == BorderOwner.Paragraph))
            return;
        var border = HBorder.None;
        if (_borderStyle != HBorderStyle.None)
        {
            // \brdrw is in twips; without it, the thin line other cells have.
            var mm = _borderWidth < 0 ? HBorder.Thin.WidthMm : Math.Max(0.05, _borderWidth / 1440.0 * 25.4);
            var color = _borderColor > 0 && _borderColor < _colors.Count ? _colors[_borderColor] ?? "#000000" : "#000000";
            border = new HBorder(_borderStyle, _borderThick ? mm * 2 : mm, color);
        }
        switch (_borderOwner)
        {
            case BorderOwner.Paragraph:
                _g.Para = _g.Para with { Box = (_g.Para.Box ?? ParaBox.Empty).With(_borderSide, border, HwpxUnits.FromTwips(_borderSpace)) };
                break;
            case BorderOwner.Cell when RowLevel() is > 0 and var level:
                TableAt(_g.Story, level).Def.Pending.Borders[_borderSide] = _borderUnset ? null : border;
                break;
            case BorderOwner.Row when RowLevel() is > 0 and var level:
                TableAt(_g.Story, level).Def.Borders[_borderSide] = border;
                break;
        }
    }

    // ───────────────────────── Lists ─────────────────────────

    private sealed class ListDef
    {
        public int Id { get; set; }
        public List<LevelDef> Levels { get; } = [];
        public HNumbering? Numbering { get; set; }
    }

    private sealed class LevelDef
    {
        public int Format { get; set; }
        public int Start { get; set; } = 1;
        public string Text { get; set; } = "";

        /// <summary>\leveljc: 0 left, 1 centre, 2 right; \leveljcn (the same at the start and end side) wins.</summary>
        public int Align { get; set; }
        public bool NaturalAlign { get; set; }

        /// <summary>A bullet of a symbol font: the font and its codes, which the number's format keeps (SymbolFonts.Drawn).</summary>
        public (string Font, string Codes)? Symbol { get; set; }

        /// <summary>\levelfollow: 0 a tab, 1 a space, 2 nothing.</summary>
        public int Follow { get; set; }

        /// <summary>The number's own character formatting (\b, \fs ... in \listlevel), over the paragraph mark's; null members unset.</summary>
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }
        public bool? Underline { get; set; }
        public int? HalfPoints { get; set; }
        public int? Font { get; set; }
        public int? Color { get; set; }

        /// <summary>The paragraph mark's formatting <paramref name="mark"/> with this level's over it.</summary>
        public CharProps Over(CharProps mark) => mark with
        {
            Bold = Bold ?? mark.Bold,
            Italic = Italic ?? mark.Italic,
            Underline = Underline ?? mark.Underline,
            HalfPoints = HalfPoints ?? mark.HalfPoints,
            Font = Font ?? mark.Font,
            Color = Color ?? mark.Color,
        };
    }

    /// <summary>Levels a list keeps (the model's numberings have nine); the words of further levels are read and dropped.</summary>
    private const int MaxListLevels = 9;

    private readonly List<ListDef> _lists = [];

    /// <summary>The first list with each \listid.</summary>
    private readonly Dictionary<int, ListDef> _listsById = [];
    private LevelDef? _level;
    private readonly Dictionary<int, int> _overrides = [];
    private int _overrideList;

    private bool Lists(string word, int? parameter)
    {
        var value = parameter ?? 0;
        if (_g.Dest == Dest.ListTable)
        {
            switch (word)
            {
                case "list":
                    Definitions(_lists.Count, "lists");
                    var list = new ListDef();
                    _lists.Add(list);
                    _listsById.TryAdd(list.Id, list);
                    _level = null;
                    return true;
                case "listlevel" when _lists.Count > 0:
                    _level = new LevelDef();
                    if (_lists[^1].Levels.Count < MaxListLevels)
                        _lists[^1].Levels.Add(_level);
                    return true;
                case "listid" when _lists.Count > 0:
                    var identified = _lists[^1];
                    if (_listsById.TryGetValue(identified.Id, out var known) && known == identified)
                        _listsById.Remove(identified.Id);
                    identified.Id = value;
                    _listsById.TryAdd(value, identified);
                    return true;
                case "levelnfc" or "levelnfcn" when _level is { } level:
                    level.Format = value;
                    return true;
                case "levelstartat" when _level is { } level:
                    level.Start = value;
                    return true;
                case "leveljc" when _level is { NaturalAlign: false } level:
                    level.Align = value;
                    return true;
                case "leveljcn" when _level is { } level:
                    level.Align = value;
                    level.NaturalAlign = true;
                    return true;
                case "levelfollow" when _level is { } level:
                    level.Follow = value;
                    return true;
                case "b" when _level is { } level:
                    level.Bold = parameter != 0;
                    return true;
                case "i" when _level is { } level:
                    level.Italic = parameter != 0;
                    return true;
                case "ul" when _level is { } level:
                    level.Underline = parameter != 0;
                    return true;
                case "ulnone" when _level is { } level:
                    level.Underline = false;
                    return true;
                case "fs" when _level is { } level:
                    level.HalfPoints = Math.Clamp(parameter ?? 24, 2, 3276);
                    return true;
                case "f" when _level is { } level:
                    level.Font = value;
                    return true;
                case "cf" when _level is { } level:
                    level.Color = value;
                    return true;
            }
            return true;
        }
        if (_g.Dest == Dest.ListOverrideTable)
        {
            switch (word)
            {
                case "listid":
                    _overrideList = value;
                    return true;
                case "ls":
                    if (!_overrides.ContainsKey(value))
                        Definitions(_overrides.Count, "list overrides");
                    _overrides[value] = _overrideList;
                    return true;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// \leveltext: its first character is the length, then the text, where characters 0–8 stand for the numbers of
    /// those levels.
    /// </summary>
    private void EndLevelText()
    {
        if (_level is not { } level)
            return;
        var raw = _collected.ToString();
        _collected.Clear();
        if (raw.Length == 0)
            return;
        var length = Math.Min(raw[0], raw.Length - 1);
        var text = new StringBuilder();
        foreach (var c in raw.AsSpan(1, length))
        {
            if (c < 9)
                text.Append('^').Append((int)c + 1);
            else
                text.Append(c);
        }
        level.Text = text.ToString();
    }

    /// <summary>
    /// The list the paragraph is an item of, or null. Its number is drawn as Word draws it: in the paragraph mark's
    /// formatting (<paramref name="mark"/>, a bold "1." before plain text) with the level's own over it.
    /// </summary>
    private HListRef? ListOf(ParaProps p, CharProps mark)
    {
        if (p.ListOverride <= 0 || !_overrides.TryGetValue(p.ListOverride, out var id) || !_listsById.TryGetValue(id, out var list))
            return null;
        list.Numbering ??= Numbering(list);
        if (list.Numbering.Levels.Count == 0)
            return null;
        var level = Math.Min(p.ListLevel, list.Numbering.Levels.Count - 1);
        var format = Format(list.Levels[level].Over(mark));
        // A symbol font's bullet: its Unicode character in the text font, and the code for the layout to draw in the font.
        if (list.Levels[level].Symbol is var (font, codes))
            format = SymbolFonts.Drawn(format, font, codes);
        return new HListRef(list.Numbering, level, Numbered: true) { MarkerFormat = format };
    }

    /// <summary>
    /// The symbol font code a character of a bullet's \leveltext stands for: F0xx and bytes as they are, a character
    /// the document's code page decoded (Windows-1252 makes 0x95 "•") as its byte again.
    /// </summary>
    private char? SymbolCode(char c)
    {
        if (c is >= '\uf000' and <= '\uf0ff' || c <= '\u00ff')
            return c;
        var bytes = CodePageEncoding(_ansiCodePage).GetBytes([c]);
        return bytes.Length == 1 ? (char)bytes[0] : null;
    }

    private static HNumberSuffix Suffix(LevelDef level) => level.Follow switch
    {
        1 => HNumberSuffix.Space,
        2 => HNumberSuffix.Nothing,
        _ => HNumberSuffix.Tab,
    };

    private static HAlign? Align(LevelDef level) => level.Align switch
    {
        1 => HAlign.Center,
        2 => HAlign.Right,
        _ => null,
    };

    private HNumbering Numbering(ListDef list)
    {
        var numbering = new HNumbering();
        foreach (var level in list.Levels.Take(9))
        {
            if (level.Format == 23)
            {
                var bullet = level.Text.Trim();
                // A bullet in Symbol, Wingdings & co.: the character its code shows; other symbol-font bullets come as
                // private-use characters.
                var font = level.Font is { } index && _fonts.TryGetValue(index, out var entry) ? entry.Name : null;
                var code = bullet.Length > 0 ? SymbolCode(bullet[0]) : (char?)null;
                var symbol = code is { } c && SymbolFonts.Map(font, c) is { Length: 1 } mapped ? mapped : null;
                if (symbol is not null)
                    level.Symbol = (font!, code!.Value.ToString());
                numbering.Levels.Add(new HNumberingLevel("DIGIT", symbol ?? (bullet.Length == 0 || bullet[0] >= '' && bullet[0] <= '' ? "•" : bullet[..1]), 1, Bullet: true)
                {
                    Suffix = Suffix(level),
                    Align = Align(level),
                });
                continue;
            }
            var format = level.Format switch
            {
                1 => "ROMAN_CAPITAL",
                2 => "ROMAN_SMALL",
                3 => "LATIN_CAPITAL",
                4 => "LATIN_SMALL",
                10 => "IDEOGRAPH",
                18 => "CIRCLED_DIGIT",
                24 => "HANGUL_SYLLABLE",
                25 => "HANGUL_JAMO",
                40 => "HANGUL_PHONETIC",
                _ => "DIGIT",
            };
            numbering.Levels.Add(new HNumberingLevel(format, level.Text.Length > 0 ? level.Text : $"^{numbering.Levels.Count + 1}.", level.Start, Bullet: false)
            {
                Suffix = Suffix(level),
                Align = Align(level),
            });
        }
        return numbering;
    }

    // ───────────────────────── Pictures ─────────────────────────

    private sealed class PictureContext
    {
        public string? Extension { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int GoalWidth { get; set; }
        public int GoalHeight { get; set; }
        public int ScaleX { get; set; } = 100;
        public int ScaleY { get; set; } = 100;

        /// <summary>\piccropl, \piccropt, \piccropr, \piccropb: twips cut off each side of \picwgoal × \pichgoal.</summary>
        public int CropLeft { get; set; }
        public int CropTop { get; set; }
        public int CropRight { get; set; }
        public int CropBottom { get; set; }

        /// <summary>The picture's own properties (\*\picprop): rotation and mirroring.</summary>
        public ShapeContext Properties { get; } = new();

        /// <summary>The shape whose picture this is (its pib property), or null for a picture in the text.</summary>
        public ShapeContext? Owner { get; init; }
        public MemoryStream Data { get; } = new();
        public int HighNibble { get; set; } = -1;
    }

    private PictureContext? _picture;

    private void StartPicture()
    {
        var owner = _g.Dest == Dest.ShapeValue && _g.Shape is { PictureValue: true } shape ? shape : null;
        if (_g.Dest != Dest.Text && owner is null)
        {
            _g.Dest = Dest.Skip;
            return;
        }
        _g.Dest = Dest.Picture;
        var picture = _picture = new PictureContext { Owner = owner };
        _g.Closer = () =>
        {
            _picture = null;
            EndPicture(picture);
        };
    }

    private bool Picture(string word, int? parameter)
    {
        if (_g.Dest != Dest.Picture || _picture is not { } picture)
            return false;
        var value = parameter ?? 0;
        switch (word)
        {
            case "pngblip": picture.Extension = ".png"; break;
            case "jpegblip": picture.Extension = ".jpg"; break;
            case "emfblip": picture.Extension = ".emf"; break;
            case "wmetafile": picture.Extension = ".wmf"; break;
            case "picw": picture.Width = value; break;
            case "pich": picture.Height = value; break;
            case "picwgoal": picture.GoalWidth = value; break;
            case "pichgoal": picture.GoalHeight = value; break;
            case "picscalex": picture.ScaleX = value; break;
            case "picscaley": picture.ScaleY = value; break;
            case "piccropl": picture.CropLeft = value; break;
            case "piccropt": picture.CropTop = value; break;
            case "piccropr": picture.CropRight = value; break;
            case "piccropb": picture.CropBottom = value; break;
        }
        return true;
    }

    private void PictureHex(ReadOnlySpan<byte> text)
    {
        if (_picture is not { } picture)
            return;
        foreach (var c in text)
        {
            var nibble = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => -1,
            };
            if (nibble < 0)
                continue;
            if (picture.HighNibble < 0)
            {
                picture.HighNibble = nibble;
                continue;
            }
            PictureByte((byte)(picture.HighNibble << 4 | nibble));
            picture.HighNibble = -1;
        }
    }

    private void PictureByte(byte value)
    {
        if (_picture is not { } picture)
            return;
        if (picture.Data.Length >= _budget.Limits.MaxPartBytes)
            throw DocumentLimits.TooLarge($"A picture in the RTF file is larger than {_budget.Limits.MaxPartBytes / (1024 * 1024)} MB.");
        picture.Data.WriteByte(value);
    }

    private void PictureBinary(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            PictureByte(b);
    }

    private void EndPicture(PictureContext picture)
    {
        if (picture.Extension is not { } extension || picture.Data.Length == 0)
            return;
        // One whose header claims more pixels than any picture should have is left out before anything decodes it.
        picture.Data.Position = 0;
        if (PictureSize.Read(picture.Data) is { } size && (long)size.Width * size.Height > _budget.Limits.MaxPicturePixels)
            return;
        _budget.Object();
        Directory.CreateDirectory(_mediaFolder);
        var path = Path.Combine(_mediaFolder, $"rtf-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, picture.Data.GetBuffer().AsSpan(0, (int)picture.Data.Length));
        // \picwgoal is in twips; \picw is in pixels for bitmaps and 0.01 mm for metafiles. The crop (in twips of the
        // goal size, cutting toward the centre; negative values add space, which is left out) makes it smaller.
        var width = picture.GoalWidth > 0 ? picture.GoalWidth : 0;
        var height = picture.GoalHeight > 0 ? picture.GoalHeight : 0;
        var image = new HImage(path);
        if (width > 0 && height > 0)
        {
            var (left, right) = (Math.Max(0, picture.CropLeft), Math.Max(0, picture.CropRight));
            var (top, bottom) = (Math.Max(0, picture.CropTop), Math.Max(0, picture.CropBottom));
            if ((long)left + right >= width || (long)top + bottom >= height)
                (left, right, top, bottom) = (0, 0, 0, 0);
            if (left + right + top + bottom > 0)
                image.Crop = new HCrop((double)left / width, (double)top / height, (double)right / width, (double)bottom / height);
            image.Width = HwpxUnits.FromTwips((width - left - right) * Math.Max(1, picture.ScaleX) / 100.0);
            image.Height = HwpxUnits.FromTwips((height - top - bottom) * Math.Max(1, picture.ScaleY) / 100.0);
        }
        image.Transform = picture.Properties.Transform();
        if (picture.Owner is { } shape)
        {
            shape.Picture = image; // placed when the shape ends
            return;
        }
        AddInline(image);
    }
}
