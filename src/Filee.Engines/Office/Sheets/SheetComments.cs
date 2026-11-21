// The notes on cells (Excel's comments, Calc's annotations) as the printer prints them. XLSX and XLSB keep a note's
// text in the sheet's comments part and whether it is shown, and its box, in the sheet's legacy VML drawing: a v:shape
// whose x:ClientData is ObjectType "Note" with x:Row / x:Column (0-based), x:Visible when it is shown, and x:Anchor
// "left column, offset, top row, offset, right column, offset, bottom row, offset" (offsets in pixels at 96 dpi), its
// fill and outline as the shape's fillcolor / strokecolor. A note without a VML shape, or VML that isn't well-formed
// XML, is a hidden note with Excel's default box.
//
// Printed (SheetPrinter, SheetPageRenderer): "as displayed" draws the shown notes' boxes over the cells with a line to
// their cell's corner; "at end" lists every note of the printed cells after the sheet's pages, "Cell: B2" and then
// "Comment: " and the note's text (Excel's, which begins with the author's name), in Excel's words.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

internal static class SheetComments
{
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace X = "urn:schemas-microsoft-com:office:excel";

    /// <summary>Notes of one sheet read at most (Excel's own limit is a note per cell); more are left out.</summary>
    public const int MaxComments = 65_536;

    /// <summary>Characters of one note's text kept at most (Excel's limit for a cell's text).</summary>
    public const int MaxCharacters = 32_767;

    /// <summary>Excel's note box: the colour of ToolTip backgrounds (#FFFFE1).</summary>
    public static readonly SKColor DefaultFill = new(0xFF, 0xFF, 0xE1);

    /// <summary>The words of the list printed at the end, as Excel prints them.</summary>
    public const string CellWord = "Cell:";

    public const string CommentWord = "Comment:";

    /// <summary>
    /// The notes' shapes in the legacy VML drawing <paramref name="vmlPath"/> by cell: shown or not, and the box; an
    /// empty map when the part is missing or isn't well-formed XML.
    /// </summary>
    public static Dictionary<(int Row, int Column), (bool Shown, SheetShape? Box)> Vml(OpcPackage package, string? vmlPath)
    {
        var notes = new Dictionary<(int, int), (bool, SheetShape?)>();
        if (vmlPath is null)
            return notes;
        XDocument? vml;
        try
        {
            vml = package.Part(vmlPath);
        }
        catch (InvalidDataException)
        {
            return notes; // Excel's VML is often not XML: the notes keep their default look
        }
        foreach (var shape in vml?.Root?.Descendants(V + "shape") ?? [])
        {
            if (notes.Count >= MaxComments)
                break;
            var data = shape.Element(X + "ClientData");
            if ((string?)data?.Attribute("ObjectType") != "Note"
                || !int.TryParse(data.Element(X + "Row")?.Value, out var row) || !int.TryParse(data.Element(X + "Column")?.Value, out var column)
                || row is < 0 or > SheetPagination.MaxRow || column is < 0 or > SheetPagination.MaxColumn)
                continue;
            var style = (string?)shape.Attribute("style") ?? "";
            var shown = data.Element(X + "Visible") is not null || style.Replace(" ", "", StringComparison.Ordinal).Contains("visibility:visible", StringComparison.OrdinalIgnoreCase);
            notes[(row, column)] = (shown, Box(shape, data));
        }
        return notes;
    }

    /// <summary>A note's box from its x:Anchor, filled and outlined as its shape says; null without an anchor.</summary>
    private static SheetShape? Box(XElement shape, XElement data)
    {
        var anchor = (data.Element(X + "Anchor")?.Value ?? "").Split(',', StringSplitOptions.TrimEntries);
        var numbers = new int[8];
        if (anchor.Length != 8)
            return null;
        for (var i = 0; i < 8; i++)
        {
            if (!int.TryParse(anchor[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0)
                return null;
        }
        var (left, leftOffset, top, topOffset, right, rightOffset, bottom, bottomOffset) =
            (numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5], numbers[6], numbers[7]);
        if (right < left || bottom < top || right > SheetPagination.MaxColumn || bottom > SheetPagination.MaxRow)
            return null;
        // Pixels at 96 dpi.
        static double Points(int pixels) => Math.Min(pixels, 100_000) * 0.75;
        var stroked = (string?)shape.Attribute("stroked") is not ("f" or "false");
        var filled = (string?)shape.Attribute("filled") is not ("f" or "false");
        return new SheetShape("rect", top, left, Points(leftOffset), Points(topOffset), 0, 0)
        {
            To = (bottom, right, Points(rightOffset), Points(bottomOffset)),
            Fill = filled ? Color((string?)shape.Attribute("fillcolor")) ?? DefaultFill : null,
            Line = stroked ? Color((string?)shape.Attribute("strokecolor")) ?? SKColors.Black : null,
            LineWidth = 0.75,
        };
    }

    /// <summary>A VML colour: "#rrggbb", "#rgb" or "#rrggbb [index]"; null for anything else.</summary>
    private static SKColor? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim().Split(' ')[0];
        if (text.Length == 4 && text[0] == '#')
            text = $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}";
        return text.Length == 7 && text[0] == '#' && SKColor.TryParse(text, out var color) ? color : null;
    }

    /// <summary>
    /// Adds a note to <paramref name="sheet"/> with what the VML says about its cell; past <see cref="MaxComments"/>
    /// notes are left out.
    /// </summary>
    public static void Add(Worksheet sheet, int row, int column, string text, IReadOnlyList<CellRun>? runs,
        Dictionary<(int Row, int Column), (bool Shown, SheetShape? Box)> vml, string? author = null)
    {
        if (sheet.Comments.Count >= MaxComments || row is < 0 or > SheetPagination.MaxRow || column is < 0 or > SheetPagination.MaxColumn)
            return;
        if (text.Length > MaxCharacters)
        {
            text = text[..MaxCharacters];
            runs = null;
        }
        var (shown, box) = vml.TryGetValue((row, column), out var note) ? note : (false, null);
        sheet.Comments.Add(new SheetComment(row, column, text) { Runs = runs, Author = author, Shown = shown, Box = box });
    }

    /// <summary>
    /// Excel's default box of a note: right of its cell, a row above it (on the first row, beside it), 108 × 59.25 pt.
    /// </summary>
    public static SheetShape DefaultBox(SheetComment comment) =>
        new("rect", Math.Max(0, comment.Row - 1), Math.Min(comment.Column + 1, SheetPagination.MaxColumn), 11.25, comment.Row > 0 ? 7.5 : 0, 108, 59.25)
        {
            Fill = DefaultFill,
            Line = SKColors.Black,
            LineWidth = 0.75,
        };

    /// <summary>The text of a note as its box shows it: the note's runs, in Excel's note font (Tahoma 9) where they set none.</summary>
    public static ShapeText BoxText(SheetComment comment)
    {
        var runs = comment.Runs is { Count: > 0 } pieces ? pieces : [new CellRun(comment.Text)];
        var paragraphs = new List<ShapeParagraph>();
        var current = new List<CellRun>();
        foreach (var run in runs)
        {
            var lines = run.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    paragraphs.Add(new ShapeParagraph(current, CellAlign.Left));
                    current = [];
                }
                if (lines[i].Length > 0)
                    current.Add(run with { Text = lines[i] });
            }
        }
        paragraphs.Add(new ShapeParagraph(current, CellAlign.Left));
        return new ShapeText(paragraphs)
        {
            Font = new CellStyle { FontName = "Tahoma", FontSize = 9 },
            Anchor = CellVerticalAlign.Top,
            Insets = (1.5, 1.5, 1.5, 1.5),
        };
    }

    /// <summary>The cell's name as Excel prints it in the list ("B2").</summary>
    public static string CellName(int row, int column) => SheetPageRenderer.ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture);
}
