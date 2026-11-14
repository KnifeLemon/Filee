using System.IO.Compression;
using System.Text;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

public class SpreadsheetPrintSourceBookmarksTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public async Task SpreadsheetPrint_export_disables_source_bookmarks_and_creates_no_temporary_name_entries()
    {
        var folder = Directory.CreateTempSubdirectory("filee-source-bookmark-print-").FullName;
        try
        {
            var data = Path.Combine(folder, "data");
            var libreOffice = new LibreOfficeConverter(new EngineEnvironment(data));
            Assert.SkipUnless(libreOffice.GetStatus().IsAvailable, "LibreOffice not found; real print export not verified");
            var merger = new RecordingMerger(output);
            var service = new SpreadsheetPrintService([libreOffice, merger]);
            var source = Path.Combine(folder, "original.xlsx");
            WriteWorkbook(source);
            var workbook = service.Inspect(source);
            var sheet = Assert.Single(workbook.Sheets);
            var region = SpreadsheetPrintRegion.FromSource(sheet) with
            {
                Range = "A1:A2",
                WritePrintArea = true,
                Paper = SpreadsheetPaper.A4,
                ScaleMode = SpreadsheetScaleMode.FitToPages,
                FitWidth = 1,
                FitHeight = 1,
            };
            var outputPath = Path.Combine(folder, "export.pdf");

            await service.ExportAsync(workbook, [region, region with { Range = "C1:C2" }], outputPath,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(false, merger.SourceBookmarks);
            Assert.Null(merger.Paper);
            Assert.Equal(2, merger.Inputs.Count);
            Assert.All(merger.Inputs, input => Assert.Equal(Path.GetFileName(input.PdfPath), input.SourceName));
            Assert.All(merger.PageCounts, count => Assert.True(count > 0));
            using var document = PdfReader.Open(outputPath, PdfDocumentOpenMode.Import);
            Assert.Equal(merger.PageCounts.Sum(), document.PageCount);
            for (var index = 0; index < document.PageCount; index++)
            {
                var page = document.Pages[index];
                var expected = merger.PageGeometry[index];
                AssertBox(expected.MediaBox, page.MediaBox);
                AssertBox(expected.CropBox, page.CropBox);
                Assert.Equal(expected.Rotate, page.Rotate);
            }
            var outlines = Outlines(document);
            output.WriteLine("Merged outlines: " + string.Join("; ", outlines));
            Assert.Equal(merger.ExpectedOutlines, outlines);
            Assert.All(outlines, outline => Assert.Equal(0, outline.Depth));
            Assert.All(merger.Inputs, input => Assert.DoesNotContain(outlines,
                outline => outline.Title.Contains(Path.GetFileName(input.PdfPath), StringComparison.OrdinalIgnoreCase)));
            Assert.All(merger.Inputs, input => Assert.False(File.Exists(input.PdfPath)));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void AssertBox(PdfRectangle expected, PdfRectangle actual)
    {
        Assert.InRange(Math.Abs(expected.X1 - actual.X1), 0, 0.01);
        Assert.InRange(Math.Abs(expected.Y1 - actual.Y1), 0, 0.01);
        Assert.InRange(Math.Abs(expected.X2 - actual.X2), 0, 0.01);
        Assert.InRange(Math.Abs(expected.Y2 - actual.Y2), 0, 0.01);
    }

    private sealed class RecordingMerger(ITestOutputHelper output) : IConverter, IPdfMerger
    {
        private readonly PdfSharpConverter _inner = new();
        public IReadOnlyList<PdfMergeInput> Inputs { get; private set; } = [];
        public List<int> PageCounts { get; } = [];
        public List<(PdfRectangle MediaBox, PdfRectangle CropBox, int Rotate)> PageGeometry { get; } = [];
        public List<(int Depth, string Title, int Page)> ExpectedOutlines { get; } = [];
        public bool? SourceBookmarks { get; private set; }
        public PdfMergePaper? Paper { get; private set; }
        public string Id => _inner.Id;
        public string DisplayName => _inner.DisplayName;
        public IReadOnlyList<ConversionEdge> Edges => _inner.Edges;
        public int MaxParallelism => _inner.MaxParallelism;
        public EngineStatus GetStatus() => _inner.GetStatus();
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            _inner.ConvertAsync(step, progress, cancellationToken);

        public Task MergeAsync(IReadOnlyList<PdfMergeInput> inputs, string outputPath, bool sourceBookmarks, PdfMergePaper? paper,
            CancellationToken cancellationToken)
        {
            Inputs = inputs.ToArray();
            SourceBookmarks = sourceBookmarks;
            Paper = paper;
            foreach (var input in inputs)
            {
                using var document = PdfReader.Open(input.PdfPath, PdfDocumentOpenMode.Import);
                var outlines = Outlines(document);
                output.WriteLine($"Rendered {Path.GetFileName(input.PdfPath)}: pages={document.PageCount}; outlines={string.Join("; ", outlines)}");
                ExpectedOutlines.AddRange(outlines.Select(outline => (outline.Depth, outline.Title, outline.Page + PageCounts.Sum())));
                PageCounts.Add(document.PageCount);
                foreach (var page in document.Pages)
                    PageGeometry.Add((page.MediaBox, page.CropBox, page.Rotate));
            }
            return _inner.MergeAsync(inputs, outputPath, sourceBookmarks, paper, cancellationToken);
        }
    }

    private static List<(int Depth, string Title, int Page)> Outlines(PdfDocument document)
    {
        var result = new List<(int Depth, string Title, int Page)>();
        PdfItem? Value(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
        void Read(PdfItem? item, int depth)
        {
            while (Value(item) is PdfDictionary outline)
            {
                var destination = Assert.IsType<PdfArray>(Value(outline.Elements["/Dest"]));
                var page = Assert.IsType<PdfReference>(destination.Elements[0]).ObjectID;
                var index = Enumerable.Range(0, document.PageCount)
                    .Single(index => document.Pages[index].Reference!.ObjectID == page);
                result.Add((depth, outline.Elements.GetString("/Title"), index));
                Read(outline.Elements["/First"], depth + 1);
                item = outline.Elements["/Next"];
            }
        }
        if (Value(document.Internals.Catalog.Elements["/Outlines"]) is PdfDictionary root)
            Read(root.Elements["/First"], 0);
        return result;
    }

    private static void WriteWorkbook(string path)
    {
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><bookViews><workbookView activeTab=\"0\"/></bookViews><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\" tabSelected=\"1\"/></sheetViews><sheetData><row r=\"1\"><c r=\"A1\"><v>11</v></c><c r=\"C1\"><v>31</v></c></row><row r=\"2\"><c r=\"A2\"><v>12</v></c><c r=\"C2\"><v>32</v></c></row></sheetData></worksheet>",
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>",
        };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml);
        }
    }
}
