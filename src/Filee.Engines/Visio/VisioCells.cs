// Visio ShapeSheets ([MS-VSDX] Shape, StyleSheet, PageSheet): cells, sections and rows read once per element, and the
// lookup of a shape's cells through what it inherits.
//
// - A sheet's cells (Cell N/V), sections (Section N/IX: Geometry, Character, Paragraph, Layer, Field, ...) and their rows
//   (Row T/IX, Del) are indexed once. Only stored values (V) are used, never formulas (F): Visio stores a cell's value
//   with every formula, inherited ones included (F="Inh"), so plain values cover what was drawn. A value Filee can't
//   take (a damaged number) is passed over and the inherited one used; "Themed" is resolved through the theme
//   (VisioTheme) by the readers that ask for it.
// - Inheritance (VisioCellChain): the shape's own cell, then its master shape's (and that master shape's master's, for
//   masters built on masters), then the style sheets of the cell's kind (LineStyle for line cells, FillStyle for fill
//   cells, TextStyle for text block, character and paragraph cells) along their parent chain. A shape whose own style
//   of a kind differs from its master's takes that kind from its style, not from the master. Geometry sections and
//   their rows match by index (IX) across the shape and its masters; a local section or row marked Del is deleted.
//
// Hostile input: sections per sheet and rows per section are capped (refused with error.document_too_complex);
// numbers are clamped and must be finite.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Documents;
using SkiaSharp;

namespace Filee.Engines.Visio;

/// <summary>Which style sheets a cell inherits from.</summary>
internal enum VisioCellKind
{
    /// <summary>Not in any style (position, size, geometry).</summary>
    None,
    Line,
    Fill,
    Text,
}

/// <summary>A row of a section: its type (Geometry rows: MoveTo, LineTo, ...), index and cells.</summary>
internal sealed class VisioRow(string? type, int index, bool deleted, Dictionary<string, XElement> cells)
{
    public string? Type { get; } = type;
    public int Index { get; } = index;
    public bool Deleted { get; } = deleted;
    public Dictionary<string, XElement> Cells { get; } = cells;
}

/// <summary>A section of a sheet: its own cells (Geometry: NoFill, NoLine, NoShow) and rows by index.</summary>
internal sealed class VisioSection(int index, bool deleted)
{
    public int Index { get; } = index;
    public bool Deleted { get; } = deleted;
    public Dictionary<string, XElement> Cells { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<int, VisioRow> Rows { get; } = [];
}

/// <summary>A Shape, StyleSheet or PageSheet with its cells and sections indexed, and the part it was read from.</summary>
internal sealed class VisioSheet
{
    public static readonly XNamespace V = "http://schemas.microsoft.com/office/visio/2012/main";

    /// <summary>Sections in one sheet at most.</summary>
    public const int MaxSections = 10_000;

    /// <summary>Rows in one section at most.</summary>
    public const int MaxRows = 10_000;

    private VisioSheet(XElement element, string part)
    {
        Element = element;
        Part = part;
    }

    public XElement Element { get; }

    /// <summary>The part the sheet is in (a page or a master): relationships of pictures resolve against it.</summary>
    public string Part { get; }

    public Dictionary<string, XElement> Cells { get; } = new(StringComparer.Ordinal);

    /// <summary>Sections by name, then by index (0 for sections without one).</summary>
    public Dictionary<string, SortedDictionary<int, VisioSection>> Sections { get; } = new(StringComparer.Ordinal);

    public string? Attribute(string name) => (string?)Element.Attribute(name);

    public VisioSection? Section(string name, int index = 0) =>
        Sections.TryGetValue(name, out var sections) && sections.TryGetValue(index, out var section) ? section : null;

    /// <summary>Indexes <paramref name="element"/>'s cells and sections.</summary>
    public static VisioSheet Read(XElement element, string part)
    {
        var sheet = new VisioSheet(element, part);
        var sections = 0;
        foreach (var child in element.Elements())
        {
            if (child.Name == V + "Cell")
            {
                if ((string?)child.Attribute("N") is { } name)
                    sheet.Cells.TryAdd(name, child);
                continue;
            }
            if (child.Name != V + "Section" || (string?)child.Attribute("N") is not { } sectionName)
                continue;
            if (++sections > MaxSections)
                throw DocumentLimits.Exceeded($"A Visio shape has more than {DocumentLimits.Count(MaxSections)} sections.");
            var section = new VisioSection(Index(child, 0), Deleted(child));
            var position = 0;
            foreach (var item in child.Elements())
            {
                if (item.Name == V + "Cell")
                {
                    if ((string?)item.Attribute("N") is { } name)
                        section.Cells.TryAdd(name, item);
                    continue;
                }
                if (item.Name != V + "Row")
                    continue;
                if (section.Rows.Count >= MaxRows)
                    throw DocumentLimits.Exceeded($"A Visio shape has a section of more than {DocumentLimits.Count(MaxRows)} rows.");
                var cells = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (var cell in item.Elements(V + "Cell"))
                    if ((string?)cell.Attribute("N") is { } name)
                        cells.TryAdd(name, cell);
                // Named rows (User, Prop) have no index: their place stands in for it.
                var row = new VisioRow((string?)item.Attribute("T"), Index(item, position), Deleted(item), cells);
                section.Rows.TryAdd(row.Index, row);
                position++;
            }
            if (!sheet.Sections.TryGetValue(sectionName, out var byIndex))
                sheet.Sections[sectionName] = byIndex = [];
            byIndex.TryAdd(section.Index, section);
        }
        return sheet;
    }

    private static int Index(XElement element, int fallback) =>
        int.TryParse((string?)element.Attribute("IX"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 ? index : fallback;

    private static bool Deleted(XElement element) => (string?)element.Attribute("Del") is "1" or "true";
}

/// <summary>Values of cells: numbers, colours and flags as Visio stores them.</summary>
internal static class VisioValues
{
    /// <summary>Visio's 24 standard colours, for colour cells that hold an index and documents without a colour table.</summary>
    private static readonly SKColor[] Standard =
    [
        new(0x00, 0x00, 0x00), new(0xFF, 0xFF, 0xFF), new(0xFF, 0x00, 0x00), new(0x00, 0xFF, 0x00), new(0x00, 0x00, 0xFF),
        new(0xFF, 0xFF, 0x00), new(0xFF, 0x00, 0xFF), new(0x00, 0xFF, 0xFF), new(0x80, 0x00, 0x00), new(0x00, 0x80, 0x00),
        new(0x00, 0x00, 0x80), new(0x80, 0x80, 0x00), new(0x80, 0x00, 0x80), new(0x00, 0x80, 0x80), new(0xC0, 0xC0, 0xC0),
        new(0xE6, 0xE6, 0xE6), new(0xCD, 0xCD, 0xCD), new(0xB3, 0xB3, 0xB3), new(0x9A, 0x9A, 0x9A), new(0x80, 0x80, 0x80),
        new(0x66, 0x66, 0x66), new(0x4D, 0x4D, 0x4D), new(0x33, 0x33, 0x33), new(0x1A, 0x1A, 0x1A),
    ];

    /// <summary>The largest magnitude a number keeps (inches: far beyond any page).</summary>
    private const double MaxMagnitude = 1e7;

    public static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? Math.Clamp(number, -MaxMagnitude, MaxMagnitude)
            : null;

    public static bool IsNumber(string value) => Number(value) is not null;

    /// <summary>"1", "TRUE" → true; "0", "FALSE" → false.</summary>
    public static bool? Flag(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "1" or "TRUE" => true,
        "0" or "FALSE" => false,
        _ => Number(value) is { } number ? number != 0 : null,
    };

    public static bool IsFlag(string value) => Flag(value) is not null;

    /// <summary>"#RRGGBB" or an index into the document's colours (Visio's standard ones when it has none).</summary>
    public static SKColor? Color(string? value, IReadOnlyDictionary<int, SKColor> table)
    {
        if (value is null)
            return null;
        value = value.Trim();
        if (value.Length == 7 && value[0] == '#' && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0)
            return table.TryGetValue(index, out var color) ? color : index < Standard.Length ? Standard[index] : null;
        return null;
    }
}

/// <summary>
/// The sheets a shape's cells are looked up in: the shape's own, its master shapes' (level 1, 2, ...), then the style
/// sheets of the cell's kind.
/// </summary>
internal sealed class VisioCellChain
{
    private static readonly HashSet<string> LineCells = new(StringComparer.Ordinal)
    {
        "LineWeight", "LineColor", "LinePattern", "Rounding", "EndArrowSize", "BeginArrow", "EndArrow", "LineCap", "BeginArrowSize",
        "LineColorTrans", "CompoundType", "QuickStyleLineColor", "QuickStyleLineMatrix",
    };

    private static readonly HashSet<string> FillCells = new(StringComparer.Ordinal)
    {
        "FillForegnd", "FillBkgnd", "FillPattern", "ShdwForegnd", "ShdwBkgnd", "ShdwPattern", "FillForegndTrans", "FillBkgndTrans",
        "ShdwForegndTrans", "ShdwBkgndTrans", "ShapeShdwType", "ShapeShdwOffsetX", "ShapeShdwOffsetY", "ShapeShdwObliqueAngle",
        "ShapeShdwScaleFactor", "ShapeShdwBlur", "ShapeShdwShow", "QuickStyleFillColor", "QuickStyleFillMatrix", "QuickStyleShadowColor",
        "QuickStyleEffectsMatrix", "FillGradientEnabled", "FillGradientDir", "FillGradientAngle", "VariationColorIndex", "VariationStyleIndex",
    };

    private static readonly HashSet<string> TextCells = new(StringComparer.Ordinal)
    {
        "LeftMargin", "RightMargin", "TopMargin", "BottomMargin", "VerticalAlign", "TextBkgnd", "DefaultTabStop", "TextDirection",
        "TextBkgndTrans", "QuickStyleFontColor", "QuickStyleFontMatrix",
    };

    private readonly IReadOnlyList<VisioSheet>[] _styles;
    private readonly bool[] _styleBeforeMasters;

    /// <param name="sheets">The shape's own sheet first, then its master shapes.</param>
    /// <param name="styles">The style sheets by <see cref="VisioCellKind"/> (Line, Fill, Text), each from the shape's style up its parents.</param>
    /// <param name="styleBeforeMasters">By kind: the shape's own style differs from its master's, so the style comes before the masters.</param>
    public VisioCellChain(IReadOnlyList<VisioSheet> sheets, IReadOnlyList<VisioSheet>[] styles, bool[] styleBeforeMasters)
    {
        Sheets = sheets;
        _styles = styles;
        _styleBeforeMasters = styleBeforeMasters;
    }

    /// <summary>The shape's own sheet, then its master shapes.</summary>
    public IReadOnlyList<VisioSheet> Sheets { get; }

    public VisioSheet Local => Sheets[0];

    public static VisioCellKind KindOf(string cell) =>
        LineCells.Contains(cell) ? VisioCellKind.Line
        : FillCells.Contains(cell) ? VisioCellKind.Fill
        : TextCells.Contains(cell) ? VisioCellKind.Text
        : VisioCellKind.None;

    public IReadOnlyList<VisioSheet> Styles(VisioCellKind kind) => kind == VisioCellKind.None ? [] : _styles[(int)kind - 1];

    /// <summary>The sheets a cell of <paramref name="kind"/> is looked up in, with their levels (−1 for styles).</summary>
    private IEnumerable<(VisioSheet Sheet, int Level)> Order(VisioCellKind kind, int from)
    {
        if (from == 0)
            yield return (Sheets[0], 0);
        var styleFirst = kind != VisioCellKind.None && _styleBeforeMasters[(int)kind - 1];
        if (!styleFirst)
        {
            for (var i = Math.Max(1, from); i < Sheets.Count; i++)
                yield return (Sheets[i], i);
        }
        foreach (var style in Styles(kind))
            yield return (style, -1);
    }

    /// <summary>The value of shape cell <paramref name="name"/> that <paramref name="valid"/> takes, and the level it came from.</summary>
    /// <param name="from">The level to start at (to look up a master's own value).</param>
    public (string Value, int Level)? Find(string name, Func<string, bool> valid, int from = 0)
    {
        foreach (var (sheet, level) in Order(KindOf(name), from))
        {
            if (sheet.Cells.TryGetValue(name, out var cell) && (string?)cell.Attribute("V") is { } value && valid(value))
                return (value, level);
        }
        return null;
    }

    /// <summary>A number cell, looked up from level <paramref name="from"/> on (a master's own value).</summary>
    public double? NumberFrom(string name, int from) => Find(name, VisioValues.IsNumber, from) is { } found ? VisioValues.Number(found.Value) : null;

    public double Number(string name, double fallback) => NumberFrom(name, 0) ?? fallback;

    public bool Flag(string name, bool fallback = false) => Find(name, VisioValues.IsFlag) is { } found ? VisioValues.Flag(found.Value) ?? fallback : fallback;

    public string? Text(string name) => Find(name, _ => true)?.Value;

    /// <summary>
    /// A row cell of section <paramref name="section"/> (no index: Character, Paragraph) at row <paramref name="row"/>,
    /// through the shape, its masters and its text style (whose row 0 stands for every row).
    /// </summary>
    public string? RowCell(string section, int row, string name, Func<string, bool> valid)
    {
        foreach (var sheet in Sheets)
        {
            if (sheet.Section(section)?.Rows.TryGetValue(row, out var found) == true && found.Cells.TryGetValue(name, out var cell)
                && (string?)cell.Attribute("V") is { } value && valid(value))
                return value;
        }
        foreach (var style in Styles(VisioCellKind.Text))
        {
            var rows = style.Section(section)?.Rows;
            if (rows is null)
                continue;
            if ((rows.TryGetValue(row, out var found) ? found : rows.TryGetValue(0, out var first) ? first : null) is { } styleRow
                && styleRow.Cells.TryGetValue(name, out var cell) && (string?)cell.Attribute("V") is { } value && valid(value))
                return value;
        }
        return null;
    }

    /// <summary>The indexes of the rows of section <paramref name="section"/> across the shape and its masters.</summary>
    public IEnumerable<int> RowIndexes(string section) =>
        Sheets.SelectMany(s => s.Section(section)?.Rows.Keys ?? (IEnumerable<int>)[]).Distinct().Order();
}
