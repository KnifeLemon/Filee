// DOC tables → w:tbl. A table is a run of paragraphs inside a table (sprmPFInTable, sprmPItap for its depth); a cell
// ends with a cell mark (character 7, or for nested tables a paragraph with sprmPFInnerTableCell) and a row with the
// table terminating paragraph (TTP: sprmPFTtp, or sprmPFInnerTtp), whose PAPX holds the row's table properties (TAP):
// cell boundaries and TC80s (sprmTDefTable), borders, shading, merges, alignment, padding, height and header rows.
// The grid is every cell boundary of every row, as Word builds it.

using System.Buffers.Binary;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Doc;

internal sealed partial class DocToDocx
{
    private static readonly string[] TableBorderNames = ["top", "left", "bottom", "right", "insideH", "insideV"];
    private static readonly string[] CellBorderOrder = ["top", "left", "bottom", "right", "insideH", "insideV", "tl2br", "tr2bl"];
    private static readonly string[] TblPrOrder = ["tblStyle", "tblW", "jc", "tblCellSpacing", "tblInd", "tblBorders", "shd", "tblLayout", "tblCellMar", "tblLook"];
    private static readonly string[] TcPrOrder = ["tcW", "gridSpan", "vMerge", "tcBorders", "shd", "noWrap", "tcMar", "textDirection", "tcFitText", "vAlign"];

    private sealed class Row
    {
        public List<List<XElement>> Cells { get; } = [];
        public List<Sprm> Tap { get; set; } = [];
    }

    private sealed class CellInfo
    {
        public int HorizontalMerge;
        public int VerticalMerge;
        public int VerticalAlign;
        public int TextFlow;
        public Dictionary<string, XElement> Borders { get; } = [];
        public object[]? Shading;
        public Dictionary<string, int> Padding { get; } = [];
    }

    private sealed class Tap
    {
        public int[] Centers = [];
        public CellInfo[] Cells = [];
        public int GapHalf;
        public string? Jc;
        public Dictionary<string, XElement> Borders { get; } = [];
        public int Height;
        public bool Header, CantSplit;
        public int? Istd;
        public Dictionary<string, int> Padding { get; } = [];
    }

    /// <summary>The table at depth <paramref name="depth"/> starting at <paramref name="index"/>, with tables nested in its cells.</summary>
    private XElement Table(List<Para> paragraphs, ref int index, int depth)
    {
        _budget.Object();
        using var level = _budget.Enter();
        var rows = new List<Row>();
        var row = new Row();
        var cell = new List<XElement>();
        while (index < paragraphs.Count)
        {
            var para = paragraphs[index];
            if (para.Itap < depth)
                break;
            if (para.Itap > depth)
            {
                cell.Add(Table(paragraphs, ref index, depth + 1));
                continue;
            }
            index++;
            if (para.RowEnd(depth))
            {
                if (cell.Count > 0)
                {
                    row.Cells.Add(cell);
                    cell = [];
                }
                row.Tap = para.Sprms;
                rows.Add(row);
                row = new Row();
                continue;
            }
            cell.Add(ParagraphXml(para));
            if (para.CellEnd(depth))
            {
                row.Cells.Add(cell);
                cell = [];
            }
        }
        if (cell.Count > 0)
            row.Cells.Add(cell);
        if (row.Cells.Count > 0)
            rows.Add(row);
        return TableXml(rows);
    }

    private Tap ReadTap(IEnumerable<Sprm> sprms)
    {
        var tap = new Tap();
        foreach (var sprm in sprms)
        {
            var o = sprm.Operand;
            switch (sprm.Op)
            {
                case Ops.TDefTable when o.Length >= 1:
                    {
                        var count = o[0];
                        var centers = Math.Min(count + 1, (o.Length - 1) / 2);
                        tap.Centers = new int[centers];
                        for (var i = 0; i < centers; i++)
                            tap.Centers[i] = BinaryPrimitives.ReadInt16LittleEndian(o.AsSpan(1 + i * 2));
                        tap.Cells = new CellInfo[Math.Max(0, centers - 1)];
                        var tcs = 1 + (count + 1) * 2;
                        for (var i = 0; i < tap.Cells.Length; i++)
                        {
                            var info = tap.Cells[i] = new CellInfo();
                            var at = tcs + i * 20;
                            if (at + 20 > o.Length)
                                continue;
                            var flags = BinaryPrimitives.ReadUInt16LittleEndian(o.AsSpan(at));
                            info.HorizontalMerge = flags & 3;
                            info.TextFlow = (flags >> 2) & 7;
                            info.VerticalMerge = (flags >> 5) & 3;
                            info.VerticalAlign = (flags >> 7) & 3;
                            for (var side = 0; side < 4; side++)
                            {
                                var brc = o.AsSpan(at + 4 + side * 4, 4).ToArray();
                                var name = side switch { 0 => "top", 1 => "left", 2 => "bottom", _ => "right" };
                                if (BinaryPrimitives.ReadUInt32LittleEndian(brc) != 0 && Border(name, brc) is { } border)
                                    info.Borders[name] = border;
                            }
                        }
                        break;
                    }
                case Ops.TDxaLeft when tap.Centers.Length > 0:
                    {
                        var shift = sprm.Short - tap.Centers[0];
                        for (var i = 0; i < tap.Centers.Length; i++)
                            tap.Centers[i] += shift;
                        break;
                    }
                case Ops.TDxaGapHalf:
                    tap.GapHalf = sprm.Short;
                    break;
                case Ops.TJc90 or Ops.TJc:
                    tap.Jc = sprm.UShort switch { 1 => "center", 2 => "right", _ => "left" };
                    break;
                case Ops.TTableBorders80 or Ops.TTableBorders:
                    {
                        var size = sprm.Op == Ops.TTableBorders ? 8 : 4;
                        for (var i = 0; i < 6 && (i + 1) * size <= o.Length; i++)
                        {
                            if (Border(TableBorderNames[i], o.AsSpan(i * size, size).ToArray()) is { } border)
                                tap.Borders[TableBorderNames[i]] = border;
                        }
                        break;
                    }
                case Ops.TDyaRowHeight:
                    tap.Height = sprm.Short;
                    break;
                case Ops.TTableHeader:
                    tap.Header = sprm.Byte != 0;
                    break;
                case Ops.TFCantSplit or Ops.TFCantSplit90:
                    tap.CantSplit = sprm.Byte != 0;
                    break;
                case Ops.TIstd:
                    tap.Istd = sprm.UShort;
                    break;
                case Ops.TDefTableShd80:
                    for (var i = 0; i * 2 + 2 <= o.Length && i < tap.Cells.Length; i++)
                        tap.Cells[i].Shading = Shd80(BinaryPrimitives.ReadUInt16LittleEndian(o.AsSpan(i * 2)));
                    break;
                case Ops.TDefTableShd or Ops.TDefTableShdRaw or Ops.TDefTableShd2nd or Ops.TDefTableShdRaw2nd or Ops.TDefTableShd3rd or Ops.TDefTableShdRaw3rd:
                    {
                        var first = sprm.Op is Ops.TDefTableShd2nd or Ops.TDefTableShdRaw2nd ? 22 : sprm.Op is Ops.TDefTableShd3rd or Ops.TDefTableShdRaw3rd ? 44 : 0;
                        for (var i = 0; i * 10 + 10 <= o.Length && first + i < tap.Cells.Length; i++)
                            tap.Cells[first + i].Shading = Shd(o.AsSpan(i * 10, 10).ToArray());
                        break;
                    }
                case Ops.TSetBrc80 or Ops.TSetBrc when o.Length >= 3:
                    {
                        var brc = o.AsSpan(3).ToArray();
                        for (var i = o[0]; i < Math.Min((int)o[1], tap.Cells.Length); i++)
                        {
                            foreach (var (bit, name) in new[] { (1, "top"), (2, "left"), (4, "bottom"), (8, "right") })
                            {
                                if ((o[2] & bit) != 0 && Border(name, brc) is { } border)
                                    tap.Cells[i].Borders[name] = border;
                            }
                        }
                        break;
                    }
                case Ops.TVertMerge when o.Length >= 2:
                    if (o[0] < tap.Cells.Length)
                        tap.Cells[o[0]].VerticalMerge = o[1];
                    break;
                case Ops.TVertAlign when o.Length >= 3:
                    for (var i = o[0]; i < Math.Min((int)o[1], tap.Cells.Length); i++)
                        tap.Cells[i].VerticalAlign = o[2];
                    break;
                case Ops.TCellPadding or Ops.TCellPaddingDefault when o.Length >= 6:
                    {
                        // CSSA: itcFirst, itcLim, the sides (top, left, bottom, right), ftsWidth (3 = twips), wWidth.
                        var width = BinaryPrimitives.ReadUInt16LittleEndian(o.AsSpan(4));
                        if (o[3] is not (3 or 0))
                            break;
                        var targets = sprm.Op == Ops.TCellPaddingDefault
                            ? [tap.Padding]
                            : Enumerable.Range(o[0], Math.Max(0, Math.Min((int)o[1], tap.Cells.Length) - o[0])).Select(i => tap.Cells[i].Padding).ToList();
                        foreach (var padding in targets)
                        {
                            foreach (var (bit, name) in new[] { (1, "top"), (2, "left"), (4, "bottom"), (8, "right") })
                            {
                                if ((o[2] & bit) != 0)
                                    padding[name] = width;
                            }
                        }
                        break;
                    }
            }
        }
        return tap;
    }

    /// <summary>The tblPr of a table style (its TAPX): borders, alignment and cell padding.</summary>
    private XElement? TableStyleProperties(List<Sprm> tapx)
    {
        if (tapx.Count == 0)
            return null;
        var tap = ReadTap(tapx);
        var bag = new Bag(W + "tblPr", TblPrOrder);
        if (tap.Jc is { } jc)
            bag.Value("jc", jc);
        if (tap.Borders.Count > 0)
            bag.Set("tblBorders", TableBorderNames.Where(tap.Borders.ContainsKey).Select(n => new XElement(tap.Borders[n])));
        if (CellMargins(tap) is { } margins)
            bag.Set("tblCellMar", margins);
        return bag.Build();
    }

    private static XElement[]? CellMargins(Tap tap)
    {
        var padding = new Dictionary<string, int>(tap.Padding);
        if (tap.GapHalf > 0)
        {
            padding.TryAdd("left", tap.GapHalf);
            padding.TryAdd("right", tap.GapHalf);
        }
        if (padding.Count == 0)
            return null;
        return new[] { "top", "left", "bottom", "right" }.Where(padding.ContainsKey)
            .Select(side => new XElement(W + side, new XAttribute(W + "w", padding[side]), new XAttribute(W + "type", "dxa"))).ToArray();
    }

    private XElement TableXml(List<Row> rows)
    {
        // Every row with the TAP of its row end, its cell boundaries made to match its cells.
        var taps = rows.Select(r => ReadTap(r.Tap)).ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            var tap = taps[r];
            var cells = rows[r].Cells.Count;
            if (tap.Centers.Length < cells + 1)
            {
                var centers = new int[cells + 1];
                Array.Copy(tap.Centers, centers, tap.Centers.Length);
                for (var i = tap.Centers.Length; i < centers.Length; i++)
                    centers[i] = i == 0 ? 0 : centers[i - 1] + 1440;
                tap.Centers = centers;
            }
            if (tap.Cells.Length < cells)
                tap.Cells = [.. tap.Cells, .. Enumerable.Range(0, cells - tap.Cells.Length).Select(_ => new CellInfo())];
        }
        var boundaries = taps.SelectMany(t => t.Centers).Distinct().Order().Take(_doc.Limits.MaxTableColumns + 1).ToList();
        int Grid(int position)
        {
            var index = boundaries.BinarySearch(position);
            return index >= 0 ? index : Math.Min(~index, boundaries.Count - 1);
        }

        var first = taps.Count > 0 ? taps[0] : new Tap();
        var tblPr = new Bag(W + "tblPr", TblPrOrder);
        if (first.Istd is { } istd && StyleId(istd, StyleKind.Table) is { } style)
            tblPr.Value("tblStyle", style);
        if (first.Jc is { } jc)
            tblPr.Value("jc", jc);
        // Word 97–2003 place the first cell boundary half a gap left of the text, the indent DOCX gives the text.
        var indent = first.Centers.Length > 0 ? first.Centers[0] + first.Padding.GetValueOrDefault("left", first.GapHalf) : 0;
        tblPr.Set("tblInd", new XAttribute(W + "w", indent), new XAttribute(W + "type", "dxa"));
        if (first.Borders.Count > 0)
            tblPr.Set("tblBorders", TableBorderNames.Where(first.Borders.ContainsKey).Select(n => new XElement(first.Borders[n])));
        if (CellMargins(first) is { } margins)
            tblPr.Set("tblCellMar", margins);
        var table = new XElement(W + "tbl", tblPr.Build(),
            new XElement(W + "tblGrid", boundaries.Zip(boundaries.Skip(1), (a, b) => new XElement(W + "gridCol", new XAttribute(W + "w", Math.Max(0, b - a))))));

        for (var r = 0; r < rows.Count; r++)
        {
            var tap = taps[r];
            var trPr = new XElement(W + "trPr");
            if (Grid(tap.Centers[0]) is > 0 and var before)
                trPr.Add(new XElement(W + "gridBefore", new XAttribute(W + "val", before)));
            if (tap.CantSplit)
                trPr.Add(new XElement(W + "cantSplit"));
            if (tap.Height != 0)
                trPr.Add(new XElement(W + "trHeight", new XAttribute(W + "val", Math.Abs(tap.Height)), new XAttribute(W + "hRule", tap.Height < 0 ? "exact" : "atLeast")));
            if (tap.Header)
                trPr.Add(new XElement(W + "tblHeader"));
            var tr = new XElement(W + "tr", trPr.HasElements ? trPr : null);
            // Rows whose table borders differ from the first row's take them per cell (DOCX has one set per table).
            var ownBorders = r > 0 && !SameBorders(tap.Borders, first.Borders);
            var cells = rows[r].Cells;
            for (var i = 0; i < cells.Count; i++)
            {
                var info = tap.Cells[i];
                var last = i;
                // A horizontally merged set (first cell 1, the rest 2 or 3) is one cell; Word leaves the others empty.
                if (info.HorizontalMerge == 1)
                {
                    while (last + 1 < cells.Count && tap.Cells[last + 1].HorizontalMerge is 2 or 3)
                        last++;
                }
                var start = Grid(tap.Centers[i]);
                var end = Grid(tap.Centers[last + 1]);
                var tcPr = new Bag(W + "tcPr", TcPrOrder);
                tcPr.Set("tcW", new XAttribute(W + "w", Math.Max(0, tap.Centers[last + 1] - tap.Centers[i])), new XAttribute(W + "type", "dxa"));
                if (end - start > 1)
                    tcPr.Value("gridSpan", end - start);
                if (info.VerticalMerge == 3)
                    tcPr.Value("vMerge", "restart");
                else if (info.VerticalMerge is 1 or 2)
                    tcPr.Set("vMerge");
                var borders = new Dictionary<string, XElement>();
                if (ownBorders)
                {
                    // Each DOC row has its own table borders: what this row leaves out it has none of.
                    void Edge(string side, string name) => borders[side] = tap.Borders.TryGetValue(name, out var border)
                        ? new XElement(W + side, border.Attributes())
                        : new XElement(W + side, new XAttribute(W + "val", "nil"));
                    Edge("top", r == 0 ? "top" : "insideH");
                    Edge("bottom", r == rows.Count - 1 ? "bottom" : "insideH");
                    Edge("left", start == 0 ? "left" : "insideV");
                    Edge("right", end >= boundaries.Count - 1 ? "right" : "insideV");
                }
                foreach (var (side, border) in info.Borders)
                    borders[side] = border;
                if (borders.Count > 0)
                    tcPr.Set("tcBorders", CellBorderOrder.Where(borders.ContainsKey).Select(n => new XElement(borders[n])));
                if (info.Shading is { } shading)
                    tcPr.Set("shd", shading);
                if (info.Padding.Count > 0)
                {
                    tcPr.Set("tcMar", new[] { "top", "left", "bottom", "right" }.Where(info.Padding.ContainsKey)
                        .Select(side => new XElement(W + side, new XAttribute(W + "w", info.Padding[side]), new XAttribute(W + "type", "dxa"))));
                }
                if (info.TextFlow is 1 or 3 or 5)
                    tcPr.Value("textDirection", info.TextFlow switch { 1 => "tbRl", 3 => "btLr", _ => "tbRlV" });
                if (info.VerticalAlign is 1 or 2)
                    tcPr.Value("vAlign", info.VerticalAlign == 1 ? "center" : "bottom");
                var content = cells[i].Count > 0 && cells[i][^1].Name == W + "p" ? cells[i] : [.. cells[i], new XElement(W + "p")];
                tr.Add(new XElement(W + "tc", tcPr.Build(), content));
                i = last;
            }
            table.Add(tr);
        }
        return table;
    }

    private static bool SameBorders(Dictionary<string, XElement> a, Dictionary<string, XElement> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && XNode.DeepEquals(pair.Value, other));
}
