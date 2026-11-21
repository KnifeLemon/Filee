// The notes on cells (Excel's comments, Calc's annotations) as the printer prints them. XLSX and XLSB keep a note's
// text in the sheet's comments part and whether it is shown, and its box, in the sheet's legacy VML drawing: a v:shape
// whose x:ClientData is ObjectType "Note" with x:Row / x:Column (0-based), x:Visible when it is shown, and x:Anchor
// "left column, offset, top row, offset, right column, offset, bottom row, offset" (offsets in pixels at 96 dpi), its
// fill and outline as the shape's fillcolor / strokecolor. A note without a VML shape, or VML that isn't well-formed
// XML, is a hidden note with Excel's default box.
//
// Threaded comments (Excel 365: a sheet relationship ".../threadedComment" to a ThreadedComments part, the workbook's
// ".../person" part naming their authors; read from the same XML parts in XLSX and XLSB) are read as notes too: the
// first comment of a thread with its author, its replies (parentId) after it in their order. Excel also writes a
// legacy copy of each thread into the comments part (a notice that the thread needs a newer Excel, then the
// messages); the thread replaces that copy. A thread isn't shown on the sheet: Excel prints threaded comments only at
// the end of the sheet.
//
// Printed (SheetPrinter, SheetPageRenderer): "as displayed" draws the shown notes' boxes over the cells with a line to
// their cell's corner; "at end" lists every note of the printed cells after the sheet's pages, "Cell: B2", then
// "Comment: " and the note's text (Excel's begins with the author's name), then "Reply: " and each reply, in the app's
// language (SheetWords).

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

internal static class SheetComments
{
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace X = "urn:schemas-microsoft-com:office:excel";
    private static readonly XNamespace Tc = "http://schemas.microsoft.com/office/spreadsheetml/2018/threadedcomments";

    /// <summary>Notes of one sheet read at most (Excel's own limit is a note per cell); more are left out.</summary>
    public const int MaxComments = 65_536;

    /// <summary>Characters of one note's text kept at most (Excel's limit for a cell's text).</summary>
    public const int MaxCharacters = 32_767;

    /// <summary>Excel's note box: the colour of ToolTip backgrounds (#FFFFE1).</summary>
    public static readonly SKColor DefaultFill = new(0xFF, 0xFF, 0xE1);

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
    /// The authors of threaded comments by person id: the workbook's persons part (".../person" relationship of
    /// <paramref name="workbookPart"/>), displayName by id; empty without one, or when it isn't well-formed XML.
    /// </summary>
    public static Dictionary<string, string> Persons(OpcPackage package, string workbookPart)
    {
        var persons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (package.TargetOfType(workbookPart, "/person") is not { } part)
            return persons;
        XDocument? list;
        try
        {
            list = package.Part(part);
        }
        catch (InvalidDataException)
        {
            return persons;
        }
        foreach (var person in list?.Root?.Elements(Tc + "person") ?? [])
        {
            if (persons.Count >= MaxComments)
                break;
            if ((string?)person.Attribute("id") is { Length: > 0 } id && (string?)person.Attribute("displayName") is { Length: > 0 } name)
                persons[id] = name.Length > 255 ? name[..255] : name;
        }
        return persons;
    }

    /// <summary>
    /// Reads the threaded comments of sheet part <paramref name="sheetPart"/> into <paramref name="sheet"/>'s notes:
    /// each thread replaces the note on its cell (Excel's legacy copy of it) or is added; past <see cref="MaxComments"/>
    /// messages of a sheet the rest are left out. A part that isn't well-formed XML is skipped (the legacy copy stays).
    /// </summary>
    public static void Threaded(OpcPackage package, string sheetPart, Worksheet sheet, Func<IReadOnlyDictionary<string, string>> persons)
    {
        if (package.TargetOfType(sheetPart, "/threadedComment") is not { } part)
            return;
        XDocument? document;
        try
        {
            document = package.Part(part);
        }
        catch (InvalidDataException)
        {
            return;
        }
        if (document?.Root is not { } root)
            return;
        var names = persons();
        string? Author(XElement comment) => (string?)comment.Attribute("personId") is { } id && names.TryGetValue(id, out var name) ? name : null;
        static string Text(XElement comment)
        {
            var text = comment.Element(Tc + "text")?.Value ?? "";
            return text.Length > MaxCharacters ? text[..MaxCharacters] : text;
        }

        // Threads in their order, replies gathered by the id of the comment they answer.
        var threads = new List<(string? Id, int Row, int Column, XElement First)>();
        var replies = new Dictionary<string, List<(string? Author, string Text)>>(StringComparer.OrdinalIgnoreCase);
        var messages = 0;
        foreach (var comment in root.Elements(Tc + "threadedComment"))
        {
            if (++messages > MaxComments)
                break;
            if ((string?)comment.Attribute("parentId") is { Length: > 0 } parent)
            {
                if (!replies.TryGetValue(parent, out var answers))
                    replies[parent] = answers = [];
                answers.Add((Author(comment), Text(comment)));
            }
            else if (CellRange.TryParseCell((string?)comment.Attribute("ref") ?? "", out var row, out var column)
                     && row is >= 0 and <= SheetPagination.MaxRow && column is >= 0 and <= SheetPagination.MaxColumn)
                threads.Add(((string?)comment.Attribute("id"), row, column, comment));
        }
        if (threads.Count == 0)
            return;
        var at = new Dictionary<(int Row, int Column), int>();
        for (var i = 0; i < sheet.Comments.Count; i++)
            at.TryAdd((sheet.Comments[i].Row, sheet.Comments[i].Column), i);
        foreach (var (id, row, column, first) in threads)
        {
            var note = new SheetComment(row, column, Text(first))
            {
                Author = Author(first),
                Replies = id is not null && replies.TryGetValue(id, out var answers) ? answers : [],
                Threaded = true,
            };
            if (at.TryGetValue((row, column), out var index))
                sheet.Comments[index] = note;
            else if (sheet.Comments.Count < MaxComments)
            {
                at[(row, column)] = sheet.Comments.Count;
                sheet.Comments.Add(note);
            }
        }
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
