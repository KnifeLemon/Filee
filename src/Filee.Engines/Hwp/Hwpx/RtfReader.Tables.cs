// RtfReader: tables, lists and pictures.
//
// A table is a run of rows: \trowd starts a row's definition (its cells' right edges \cellxN, merges, shading and
// borders), the paragraphs marked \intbl fill its cells, \cell ends a cell and \row the row. The columns of the
// table are every cell edge of every row, so rows with other divisions keep their cells where they are. Nested
// tables (\itap > 1) are read as the paragraphs of their outer cell.
//
// Lists come from \listtable (levels with their number format and \leveltext) through \listoverridetable (\lsN →
// list). Pictures are \pict groups (PNG, JPEG, EMF, WMF) as hex or \bin data, sized by \picwgoal / \pichgoal and
// their scale.

using System.Text;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class RtfReader
{
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
        public HVerticalAlign Align { get; set; }
        public bool Bordered { get; set; }
    }

    private sealed class RowDef
    {
        public int Left { get; set; }
        public int Height { get; set; }
        public bool Header { get; set; }
        /// <summary>\trql, \trqc, \trqr: the left side, centre, right side; null when the row doesn't say.</summary>
        public HAlign? Align { get; set; }

        /// <summary>\rtlrow: the cells run from the right.</summary>
        public bool RightToLeft { get; set; }

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
        public RowDef Next() => new() { Left = Left, Height = Height, Align = Align, RightToLeft = RightToLeft, Cells = Cells, _shared = true };
    }

    private sealed class TableBuilder
    {
        public RowDef Def { get; set; } = new();
        public List<HBlock> Cell { get; set; } = [];
        public List<List<HBlock>> Cells { get; } = [];
        public List<(RowDef Def, List<List<HBlock>> Cells)> Rows { get; } = [];

        /// <summary>The last border word was for a cell edge, so the border style that follows marks it.</summary>
        public bool CellBorder { get; set; }
    }

    private TableBuilder TableOf(Story story) => story.Table ??= new TableBuilder();

    private bool Table(string word, int? parameter)
    {
        if (_g.Dest != Dest.Text)
            return word is "trowd" or "cell" or "row" or "cellx";
        var value = parameter ?? 0;
        var story = _g.Story;
        switch (word)
        {
            case "trowd":
                TableOf(story).Def = new RowDef();
                return true;
            case "trleft":
                TableOf(story).Def.Left = value;
                return true;
            case "trrh":
                TableOf(story).Def.Height = value;
                return true;
            case "trhdr":
                TableOf(story).Def.Header = true;
                return true;
            case "trql":
                TableOf(story).Def.Align = HAlign.Left;
                return true;
            case "trqc":
                TableOf(story).Def.Align = HAlign.Center;
                return true;
            case "trqr":
                TableOf(story).Def.Align = HAlign.Right;
                return true;
            case "rtlrow" or "ltrrow":
                TableOf(story).Def.RightToLeft = word == "rtlrow";
                return true;
            case "clmgf":
                TableOf(story).Def.Pending.Horizontal = Merge.First;
                return true;
            case "clmrg":
                TableOf(story).Def.Pending.Horizontal = Merge.Continue;
                return true;
            case "clvmgf":
                TableOf(story).Def.Pending.Vertical = Merge.First;
                return true;
            case "clvmrg":
                TableOf(story).Def.Pending.Vertical = Merge.Continue;
                return true;
            case "clcbpat":
                TableOf(story).Def.Pending.Shade = value;
                return true;
            case "clvertalc":
                TableOf(story).Def.Pending.Align = HVerticalAlign.Center;
                return true;
            case "clvertalb":
                TableOf(story).Def.Pending.Align = HVerticalAlign.Bottom;
                return true;
            case "clbrdrt" or "clbrdrl" or "clbrdrb" or "clbrdrr":
                TableOf(story).CellBorder = true;
                return true;
            case "brdrnone" or "brdrnil" or "brdrtbl":
                if (story.Table is { } none)
                    none.CellBorder = false;
                return true;
            case "brdrs" or "brdrth" or "brdrsh" or "brdrdb" or "brdrdot" or "brdrdash" or "brdrhair" or "brdrdashsm" or "brdrdashd"
                or "brdrdashdd" or "brdrtriple" or "brdrwavy" or "brdrinset" or "brdroutset":
                if (story.Table is { CellBorder: true } bordered)
                {
                    bordered.Def.Pending.Bordered = true;
                    bordered.CellBorder = false;
                }
                return true;
            case "cellx":
                var table = TableOf(story);
                if (table.Def.Cells.Count >= _budget.Limits.MaxTableColumns)
                    throw DocumentLimits.Exceeded($"A table row has more than {_budget.Limits.MaxTableColumns} cells.");
                table.Def.Pending.Right = value;
                table.Def.Add(table.Def.Pending);
                table.Def.Pending = new CellDef();
                return true;
            case "cell" or "nestcell":
                if (word == "nestcell")
                {
                    EndParagraph(story, force: false);
                    return true;
                }
                var current = TableOf(story);
                _g.Para = _g.Para with { InTable = true };
                EndParagraph(story, force: false);
                TableCells(1);
                current.Cells.Add(current.Cell);
                current.Cell = [];
                return true;
            case "row":
                var finished = TableOf(story);
                EndRun(story.Inlines);
                if (story.Inlines.Count > 0)
                {
                    _g.Para = _g.Para with { InTable = true };
                    EndParagraph(story, force: false);
                }
                EndRow(finished);
                finished.Cell = [];
                // The next row repeats its definition, or keeps this one when it doesn't.
                finished.Def = finished.Def.Next();
                return true;
            case "nestrow" or "nesttableprops" or "itap":
                return true;
        }
        return false;
    }

    /// <summary>Table cells read so far, in every table of the document; cells a row defines without content count too.</summary>
    private long _tableCells;

    private void TableCells(int count)
    {
        _tableCells += count;
        if (_tableCells > _budget.Limits.MaxParagraphs)
            throw DocumentLimits.Exceeded($"The tables of the document have more than {DocumentLimits.Count(_budget.Limits.MaxParagraphs)} cells.");
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

    /// <summary>Adds the open table of <paramref name="story"/> to its blocks.</summary>
    private void CloseTable(Story story)
    {
        if (story.Table is not { } builder)
            return;
        story.Table = null;
        EndRow(builder);
        if (builder.Cell.Count > 0 && builder.Rows.Count == 0)
        {
            // Paragraphs marked \intbl without any row: plain paragraphs.
            story.Blocks.AddRange(builder.Cell);
            return;
        }
        if (builder.Rows.Count == 0)
            return;
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
        if (edges.Count - 1 > _budget.Limits.MaxTableColumns)
            throw DocumentLimits.Exceeded($"A table has more than {DocumentLimits.Count(_budget.Limits.MaxTableColumns)} columns.");
        var grid = edges.ToList();
        var table = new HTable
        {
            ColumnCount = Math.Max(1, grid.Count - 1),
            ColumnWidths = grid.Count > 1 ? [.. grid.Zip(grid.Skip(1), (a, b) => HwpxUnits.FromTwips(Math.Max(1, b - a)))] : null,
            Align = builder.Rows[0].Def.Align ?? HAlign.Left,
            Indent = HwpxUnits.FromTwips(Math.Max(0, builder.Rows[0].Def.Left)),
            Borders = HBorders.Empty,
            // The cells of a right-to-left row run from the right; the model's Align then names the start side, so
            // \trqr (the right side) is the start, and a row that doesn't say starts at the right too.
            RightToLeft = builder.Rows[0].Def.RightToLeft,
        };
        if (table.RightToLeft && builder.Rows[0].Def.Align is HAlign.Left or HAlign.Right)
            table.Align = builder.Rows[0].Def.Align == HAlign.Left ? HAlign.Right : HAlign.Left;
        var openVertical = new Dictionary<int, HCell>();
        foreach (var (def, contents) in builder.Rows)
        {
            var row = new HRow
            {
                Height = def.Height == 0 ? null : HwpxUnits.FromTwips(Math.Abs(def.Height)),
                Header = def.Header,
            };
            var left = def.Left;
            HCell? previous = null;
            for (var i = 0; i < contents.Count; i++)
            {
                var cellDef = i < def.Cells.Count ? def.Cells[i] : new CellDef { Right = left + 1440 };
                var l = grid.BinarySearch(left);
                var from = Math.Max(0, l >= 0 ? l : ~l);
                var r = grid.BinarySearch(cellDef.Right);
                var to = r >= 0 ? r : Math.Max(from + 1, ~r);
                left = cellDef.Right;
                var span = Math.Max(1, to - from);
                if (cellDef.Horizontal == Merge.Continue && previous is not null)
                {
                    previous.ColSpan += span;
                    continue;
                }
                if (cellDef.Vertical == Merge.Continue && openVertical.TryGetValue(from, out var above))
                {
                    above.RowSpan++;
                    previous = null;
                    continue;
                }
                var cell = new HCell
                {
                    Column = from,
                    ColSpan = span,
                    VerticalAlign = cellDef.Align,
                    Fill = cellDef.Shade > 0 && cellDef.Shade < _colors.Count ? _colors[cellDef.Shade] : null,
                    Borders = cellDef.Bordered ? HBorders.Grid : HBorders.Empty,
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
        story.Blocks.Add(table);
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

    private HListRef? ListOf(ParaProps p)
    {
        if (p.ListOverride <= 0 || !_overrides.TryGetValue(p.ListOverride, out var id) || !_listsById.TryGetValue(id, out var list))
            return null;
        list.Numbering ??= Numbering(list);
        return list.Numbering.Levels.Count == 0 ? null : new HListRef(list.Numbering, Math.Min(p.ListLevel, list.Numbering.Levels.Count - 1), Numbered: true);
    }

    private static HNumbering Numbering(ListDef list)
    {
        var numbering = new HNumbering();
        foreach (var level in list.Levels.Take(9))
        {
            if (level.Format == 23)
            {
                var bullet = level.Text.Trim();
                // Symbol-font bullets come as private-use characters.
                numbering.Levels.Add(new HNumberingLevel("DIGIT", bullet.Length == 0 || bullet[0] >= '' && bullet[0] <= '' ? "•" : bullet[..1], 1, Bullet: true));
                continue;
            }
            var format = level.Format switch
            {
                1 => "ROMAN_CAPITAL",
                2 => "ROMAN_SMALL",
                3 => "LATIN_CAPITAL",
                4 => "LATIN_SMALL",
                _ => "DIGIT",
            };
            numbering.Levels.Add(new HNumberingLevel(format, level.Text.Length > 0 ? level.Text : $"^{numbering.Levels.Count + 1}.", level.Start, Bullet: false));
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
        public MemoryStream Data { get; } = new();
        public int HighNibble { get; set; } = -1;
    }

    private PictureContext? _picture;

    private void StartPicture()
    {
        if (_g.Dest != Dest.Text)
        {
            _g.Dest = Dest.Skip;
            return;
        }
        _g.Dest = Dest.Picture;
        var picture = _picture = new PictureContext();
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
            throw DocumentLimits.Exceeded($"A picture in the RTF file is larger than {_budget.Limits.MaxPartBytes / (1024 * 1024)} MB.");
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
        // \picwgoal is in twips; \picw is in pixels for bitmaps and 0.01 mm for metafiles.
        var width = picture.GoalWidth > 0 ? picture.GoalWidth : 0;
        var height = picture.GoalHeight > 0 ? picture.GoalHeight : 0;
        var image = new HImage(path);
        if (width > 0 && height > 0)
        {
            image.Width = HwpxUnits.FromTwips(width * Math.Max(1, picture.ScaleX) / 100.0);
            image.Height = HwpxUnits.FromTwips(height * Math.Max(1, picture.ScaleY) / 100.0);
        }
        AddInline(image);
    }
}
