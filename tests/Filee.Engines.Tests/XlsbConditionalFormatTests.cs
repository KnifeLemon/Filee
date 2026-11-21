// Conditional formats of Excel binary workbooks (XlsbReader: BrtBeginConditionalFormatting, BrtBeginCFRule, the
// differential formats of styles.bin, colour scales), checked against the same workbook as XLSX (XlsbBuilder writes
// both): the rules read, the cells they colour, and the printed pages.

using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using static Filee.Engines.Tests.XlsbBuilder;

namespace Filee.Engines.Tests;

public sealed class XlsbConditionalFormatTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private (string Xlsb, string Xlsx) Save(XlsbBuilder book)
    {
        var folder = fx.NewFolder();
        return (book.SaveXlsb(Path.Combine(folder, "book.xlsb")), book.SaveXlsx(Path.Combine(folder, "book.xlsx")));
    }

    /// <summary>Six rows of numbers and text with rules of each kind the printer evaluates.</summary>
    private static XlsbBuilder Book()
    {
        var book = new XlsbBuilder();
        book.Dxfs.Add((XlsbColor.Rgb(0xFFC7CE), XlsbColor.Rgb(0x9C0006), true));
        book.Dxfs.Add((XlsbColor.Theme(5, 0.59999389629810485), null, false));
        var sheet = book.Sheet("Data");
        for (var r = 0; r < 6; r++)
            sheet.Number(r, 0, r * 10).Number(r, 1, r).Text(r, 2, r % 2 == 0 ? "apple pie" : "pear").Number(r, 3, r * r);
        sheet.Rules.Add(new XlsbRule((0, 0, 5, 0), 1, 0, 0, 5, "cellIs") { Formulas = ["25"] }); // greater than 25
        sheet.Rules.Add(new XlsbRule((0, 0, 5, 0), 1, 0, 1, 1, "cellIs") { Formulas = ["5", "15"] }); // between 5 and 15
        sheet.Rules.Add(new XlsbRule((0, 2, 5, 2), 2, 8, 1, 0, "containsText") { Text = "apple" });
        sheet.Rules.Add(new XlsbRule((0, 2, 5, 2), 1, 0, 0, 3, "cellIs") { Formulas = ["\"pear\""] }); // equal to "pear"
        sheet.Rules.Add(new XlsbRule((0, 1, 5, 1), 5, 5, 0, 2, "top10") { Bottom = true });
        sheet.Rules.Add(new XlsbRule((0, 1, 5, 1), 2, 25, 1, 0, "aboveAverage"));
        sheet.Rules.Add(new XlsbRule((0, 3, 5, 3), 3, 2, 0, 0, "colorScale") { Scale = (XlsbColor.Rgb(0xF8696B), XlsbColor.Rgb(0x63BE7B)) });
        return book;
    }

    private static string Describe(ConditionalFormat rule) =>
        $"{string.Join(' ', rule.Ranges)} {rule.Type} {rule.Priority} {rule.Operator} [{string.Join('|', rule.Formulas)}] {rule.Text} {rule.Rank} " +
        $"{rule.Percent} {rule.Bottom} {rule.AboveAverage} {rule.EqualAverage} {rule.StdDev} {rule.StopIfTrue} {rule.Style} " +
        string.Join(' ', rule.Scale.Select(p => $"{p.Type}:{p.Value}:{p.Color}"));

    [Fact]
    public void Conditional_formats_read_as_their_xlsx_twin()
    {
        var (xlsb, xlsx) = Save(Book());
        var binary = XlsbReader.Read(xlsb).Sheets[0];
        var twin = XlsxReader.Read(xlsx).Sheets[0];
        Assert.Equal(7, binary.ConditionalFormats.Count);
        Assert.Equal(twin.ConditionalFormats.Select(Describe), binary.ConditionalFormats.Select(Describe));

        var area = new CellRange(0, 0, 5, 3);
        var cells = ConditionalFormatting.Apply(binary, area);
        Assert.Equal(ConditionalFormatting.Apply(twin, area).OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}"),
            cells.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}"));
        // 30, 40, 50 are over 25 (light red, dark red bold text); 10 is between 5 and 15.
        Assert.Equal("#FFC7CE", cells[(3, 0)].Fill);
        Assert.Equal("#9C0006", cells[(3, 0)].Color);
        Assert.True(cells[(3, 0)].Bold);
        Assert.NotNull(cells[(1, 0)].Fill);
        Assert.False(cells.ContainsKey((0, 0)));
        // "apple pie" contains apple; "pear" equals "pear"; the colour scale colours every number.
        Assert.Equal(cells[(1, 0)].Fill, cells[(0, 2)].Fill);
        Assert.Equal("#FFC7CE", cells[(1, 2)].Fill);
        Assert.Equal("#F8696B", cells[(0, 3)].Fill);
        Assert.Equal("#63BE7B", cells[(5, 3)].Fill);
    }

    [Fact]
    public void Conditional_formats_print_as_their_xlsx_twin()
    {
        var (xlsb, xlsx) = Save(Book());
        var source = new SheetPrintSource("book", "C:\\");
        var now = new DateTime(2026, 11, 21);
        using var a = SheetPrinter.Print(XlsbReader.Read(xlsb), source, DocumentLimits.Default, now, null, TestContext.Current.CancellationToken);
        using var b = SheetPrinter.Print(XlsxReader.Read(xlsx), source, DocumentLimits.Default, now, null, TestContext.Current.CancellationToken);
        using var first = a.Pages[0].Render(72);
        using var twin = b.Pages[0].Render(72);
        Assert.Equal(twin.Bytes, first.Bytes);
    }

    [Fact]
    public void Rule_formulas_are_read_when_they_are_constants()
    {
        Assert.Equal("7", XlsbReader.Constant([0x1E, 7, 0]));
        Assert.Equal("-2.5", XlsbReader.Constant([0x1F, .. F64(2.5), 0x13]));
        Assert.Equal("\"a\"\"b\"", XlsbReader.Constant([0x17, 3, 0, (byte)'a', 0, (byte)'"', 0, (byte)'b', 0]));
        Assert.Equal("1", XlsbReader.Constant([0x1D, 1]));
        // A reference, a function, a cut string: not evaluated.
        Assert.Null(XlsbReader.Constant([0x24, 0, 0, 0, 0, 0, 0]));
        Assert.Null(XlsbReader.Constant([0x1E, 1, 0, 0x1E, 2, 0, 0x03]));
        Assert.Null(XlsbReader.Constant([0x17, 200, 0, (byte)'a', 0]));
        Assert.Null(XlsbReader.Constant([0x1F, .. F64(double.NaN)]));
    }

    [Fact]
    public void Hostile_rules_are_bounded_by_their_records()
    {
        var book = new XlsbBuilder();
        book.Dxfs.Add((XlsbColor.Rgb(0xFF0000), null, false));
        var sheet = book.Sheet("Data").Number(0, 0, 1);
        // Four billion ranges claimed in a record holding one; a rule whose formula claims a gigabyte; one of a type
        // and template that don't exist; a differential format index past the list; a rule outside any range record.
        sheet.ExtraRecords.Add(RecordWriter.Encode(461, [.. U32(1), .. U32(0), .. U32(0xFFFFFFFF), .. U32(0), .. U32(0), .. U32(0), .. U32(0)]));
        sheet.ExtraRecords.Add(RecordWriter.Encode(463, [.. U32(1), .. U32(0), .. U32(0), .. U32(1), .. U32(5), .. U32(0), .. U32(0), .. U16(0),
            .. U32(1), .. U32(0), .. U32(0), .. NullString(), .. U32(1_000_000_000)]));
        sheet.ExtraRecords.Add(RecordWriter.Encode(464, []));
        sheet.ExtraRecords.Add(RecordWriter.Encode(463, [.. U32(9), .. U32(99), .. U32(7), .. U32(2), .. U32(0), .. U32(0), .. U32(0), .. U16(0),
            .. U32(0), .. U32(0), .. U32(0), .. NullString()]));
        sheet.ExtraRecords.Add(RecordWriter.Encode(464, []));
        sheet.ExtraRecords.Add(RecordWriter.Encode(463, [.. U32(2), .. U32(9), .. U32(7), .. U32(3), .. U32(0), .. U32(0), .. U32(0), .. U16(0),
            .. U32(0), .. U32(0), .. U32(0), .. NullString()]));
        sheet.ExtraRecords.Add(RecordWriter.Encode(464, []));
        sheet.ExtraRecords.Add(RecordWriter.Encode(462, []));
        sheet.ExtraRecords.Add(RecordWriter.Encode(463, [.. U32(2), .. U32(9), .. U32(0), .. U32(4), .. U32(0), .. U32(0), .. U32(0), .. U16(0),
            .. U32(0), .. U32(0), .. U32(0), .. NullString()]));
        sheet.ExtraRecords.Add(RecordWriter.Encode(464, []));
        var (xlsb, _) = Save(book);
        var read = XlsbReader.Read(xlsb).Sheets[0];
        // Only the blank-cells rule of a known type stands, without a style (its index is past the list).
        var rule = Assert.Single(read.ConditionalFormats);
        Assert.Equal("containsBlanks", rule.Type);
        Assert.Null(rule.Style);
        Assert.Equal(new CellRange(0, 0, 0, 0), Assert.Single(rule.Ranges));
    }
}
