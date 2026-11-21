// Mutation fuzzing of the XLSB reader (Office/Sheets/XlsbReader): the seeds are XlsbTests' workbook with every kind of
// cell, format and page setting, a small one, and one with notes, threaded comments and a table with its own formats
// and style (NotesAndTables), and one with a PivotTable in a custom PivotTable style, a table naming cell styles and
// shapes with bullets, autofit, vertical and linked text (PivotsAndShapeText; both also seeds of DocumentFuzzTests' XLSX
// printing), written in code. Most inputs change the BIFF12 records of a binary
// part (a field set to an edge value, a record's size or type changed, records cut, dropped or repeated), some the
// part's bytes, some the package (relationships, ZIP). Every input must be read or refused with InvalidDataException /
// ConversionRefusedException, in time and on a 1 MB stack; what is read is checked for what the printer leaves out and
// written as XLSX and as tables. Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random
// one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using System.Buffers.Binary;
using System.IO.Compression;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class XlsbFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 60;

    [Fact]
    public void XlsbReader_reads_or_refuses_every_mutated_workbook() =>
        new FuzzHarness(fx, QuickIterations, 20261119).Files("xlsb", "xlsb",
            [Seed(XlsbTests.Twin()), Seed(Small()), Seed(NotesAndTables()), Seed(PivotsAndShapeText())], Mutator, Read);

    private static void Read(string path, string media)
    {
        var book = XlsbReader.Read(path);
        SheetCapabilities.FirstUnsupported(path, XlsbReader.Format);
        SheetDocument.Build(book);
        Directory.CreateDirectory(media); // emptied before each input
        XlsxWriter.Write(book, Path.Combine(media, "out.xlsx"));
    }

    private static XlsbBuilder Small()
    {
        var book = new XlsbBuilder();
        book.Sheet("A").Text(0, 0, "a").Rk(0, 1, XlsbBuilder.RkInteger(7)).Merge(1, 0, 2, 1).Width(0, 2, 10);
        return book;
    }

    /// <summary>Notes (one shown), a thread with a reply over its legacy copy, a table with its own formats in a custom style.</summary>
    internal static XlsbBuilder NotesAndTables()
    {
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Notes").Text(0, 0, "Item").Text(0, 1, "Total").Text(1, 0, "Rent").Number(1, 1, 1200).Text(2, 0, "Sum").Number(2, 1, 1200);
        sheet.PageSetup = (9, false, 100, false, 1, 1, false, null);
        sheet.NotesPrint = 2;
        sheet.Notes.Add((1, 1, "Ann", "Check", true));
        sheet.Notes.Add((2, 0, "tc={00000000-0000-0000-0000-000000000001}", "[Threaded comment]", false));
        book.Persons.Add(("{P1}", "Ann"));
        sheet.Threads.Add((2, 0, "{P1}", "Thread", [("{P1}", "Reply")]));
        book.Dxfs.Add((XlsbColor.Rgb(0xC00000), XlsbColor.Rgb(0xFFFFFF), true));
        book.Dxfs.Add((null, null, false));
        book.DxfBorders[1] = (1, XlsbColor.Rgb(0x000000));
        book.TableStyles.Add(("Own", [("headerRow", 0), ("firstRowStripe", 1)]));
        sheet.Tables.Add(((0, 0, 2, 1), "Own", true, true, true));
        sheet.TableFormats[0] = (0, 1, 0, 1, [(1, 0, 1, 0)]);
        return book;
    }

    /// <summary>
    /// A PivotTable (page field, subtotals, column items) in a custom PivotTable style, a table naming cell styles, and
    /// a drawing of shapes with bullets, autofit, vertical text and text linked to a cell (an XML part in both packages).
    /// </summary>
    internal static XlsbBuilder PivotsAndShapeText()
    {
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Pivot").Text(0, 0, "Year").Text(0, 1, "(All)").Text(2, 0, "Row Labels").Text(2, 1, "A").Text(2, 2, "Total")
            .Text(3, 0, "East").Number(3, 1, 1).Number(3, 2, 1).Text(4, 0, "East Total").Number(4, 1, 1).Number(4, 2, 1)
            .Text(5, 0, "Grand Total").Number(5, 1, 1).Number(5, 2, 1).Text(7, 0, "Item").Text(7, 1, "Value").Text(8, 0, "x").Number(8, 1, 2);
        book.Dxfs.Add((XlsbColor.Rgb(0xC00000), XlsbColor.Rgb(0xFFFFFF), true));
        book.PivotStyles.Add(("Own pivot", [("headerRow", 0), ("firstSubtotalRow", 0), ("pageFieldLabels", 0)]));
        sheet.Pivots.Add(new XlsbPivot((2, 0, 5, 2), 1, 1, 1, "Own pivot")
        {
            RowFields = 2,
            ColumnFields = 1,
            PageFields = 1,
            Rows = [(null, 0, 2), ("default", 0, 1), ("grand", 0, 1)],
            Columns = [(null, 0, 1), ("grand", 0, 1)],
        });
        book.CellStyles.Add(("Heading", 1, 2, 1));
        sheet.Tables.Add(((7, 0, 8, 1), "TableStyleMedium2", true, true, false));
        sheet.TableCellStyles[0] = ("Heading", "Heading", null, [(1, "Heading", null, null)]);
        sheet.DrawingId = "rIdD";
        sheet.Relationships.Add(("rIdD", "drawing", "../drawings/drawing1.xml"));
        book.Parts["xl/drawings/drawing1.xml"] =
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
            "<xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"1270000\"/><xdr:ext cx=\"2540000\" cy=\"1270000\"/><xdr:sp textlink=\"$A$1\"><xdr:nvSpPr><xdr:cNvPr id=\"2\" name=\"S\"/><xdr:cNvSpPr/></xdr:nvSpPr>" +
            "<xdr:spPr><a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom></xdr:spPr><xdr:txBody><a:bodyPr vert=\"eaVert\"><a:normAutofit fontScale=\"62500\" lnSpcReduction=\"10000\"/></a:bodyPr><a:lstStyle/>" +
            "<a:p><a:pPr marL=\"171450\" indent=\"-171450\"><a:buFont typeface=\"Wingdings\"/><a:buChar char=\"§\"/></a:pPr><a:r><a:t>세로 text</a:t></a:r></a:p>" +
            "<a:p><a:pPr lvl=\"1\"><a:lnSpc><a:spcPct val=\"150000\"/></a:lnSpc><a:buAutoNum type=\"romanUcPeriod\" startAt=\"4\"/></a:pPr><a:r><a:t>four</a:t></a:r></a:p>" +
            "</xdr:txBody></xdr:sp><xdr:clientData/></xdr:absoluteAnchor></xdr:wsDr>";
        return book;
    }

    private byte[] Seed(XlsbBuilder book) => File.ReadAllBytes(book.SaveXlsb(Path.Combine(fx.NewFolder(), "seed.xlsb")));

    private static Func<byte[]> Mutator(byte[] seed, Random random)
    {
        var parts = new List<(string Name, byte[] Bytes)>();
        using (var zip = new ZipArchive(new MemoryStream(seed), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                parts.Add((entry.FullName, copy.ToArray()));
            }
        }
        var binary = Enumerable.Range(0, parts.Count).Where(i => parts[i].Name.EndsWith(".bin", StringComparison.Ordinal)).ToList();
        var package = FuzzHarness.Packages(seed, random);
        return () =>
        {
            var choice = random.Next(100);
            if (choice < 20)
                return package();
            var mutated = parts.ToList();
            var index = binary[random.Next(binary.Count)];
            mutated[index] = (mutated[index].Name, choice < 35
                ? new BinaryFuzzer(mutated[index].Bytes, random).Next()
                : MutateRecords(mutated[index].Bytes, random));
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, bytes) in mutated)
                {
                    using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                    stream.Write(bytes);
                }
            }
            return output.ToArray();
        };
    }

    private static readonly uint[] Interesting = [0, 1, 0x7F, 0xFF, 0x3FFF, 0x4000, 0xFFFF, 16383, 16384, 1048575, 1048576, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF];

    /// <summary>The part's records with a few changed: fields, sizes, types, copies.</summary>
    private static byte[] MutateRecords(byte[] part, Random random)
    {
        var records = Split(part);
        if (records.Count == 0)
            return part;
        for (var changes = 1 + random.Next(3); changes > 0; changes--)
        {
            var i = random.Next(records.Count);
            var (type, data) = records[i];
            switch (random.Next(7))
            {
                case 0 or 1 when data.Length >= 4:
                    {
                        // A 32-bit (or 16-bit) field at a field boundary set to an edge value.
                        var copy = (byte[])data.Clone();
                        var at = random.Next(data.Length - 3) & ~1;
                        var value = Interesting[random.Next(Interesting.Length)];
                        if (random.Next(3) == 0)
                            BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(at), (ushort)value);
                        else
                            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(at), value);
                        records[i] = (type, copy);
                        break;
                    }
                case 2:
                    records[i] = (type, data[..random.Next(data.Length + 1)]); // cut short
                    break;
                case 3:
                    records[i] = (records[random.Next(records.Count)].Type, data); // another record's type
                    break;
                case 4:
                    for (var copies = random.Next(1, 200); copies > 0; copies--)
                        records.Insert(i, (type, data));
                    break;
                case 5:
                    records.RemoveAt(i);
                    if (records.Count == 0)
                        return [];
                    break;
                default:
                    // A size that claims more than the record holds: the rest of the part becomes its data.
                    var join = new List<byte>(RecordWriter.Header(type, data.Length + random.Next(1, 64)));
                    join.AddRange(data);
                    foreach (var (t, d) in records.Skip(i + 1))
                        join.AddRange(RecordWriter.Encode(t, d));
                    return [.. records.Take(i).SelectMany(r => RecordWriter.Encode(r.Type, r.Data)), .. join];
            }
        }
        return [.. records.SelectMany(r => RecordWriter.Encode(r.Type, r.Data))];
    }

    /// <summary>The records of a BIFF12 part (as far as they are whole).</summary>
    private static List<(int Type, byte[] Data)> Split(byte[] part)
    {
        var records = new List<(int, byte[])>();
        var at = 0;
        while (at < part.Length)
        {
            var type = part[at] & 0x7F;
            if ((part[at++] & 0x80) != 0 && at < part.Length)
                type |= (part[at++] & 0x7F) << 7;
            var size = 0;
            for (var i = 0; i < 4 && at < part.Length; i++)
            {
                var b = part[at++];
                size |= (b & 0x7F) << (7 * i);
                if ((b & 0x80) == 0)
                    break;
            }
            if (size > part.Length - at)
                break;
            records.Add((type, part[at..(at + size)]));
            at += size;
        }
        return records;
    }
}
