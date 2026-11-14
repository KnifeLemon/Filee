// End-to-end tests of the "filee" command: real engines, a temporary settings folder, files in a temporary folder.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Filee.Engines.Tests;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.Cli.Tests;

[Collection("Cli")] // shares EngineEnvironment state with EnginesCommandTests
public sealed class CliTests : IDisposable
{
    [Fact]
    public async Task Folder_to_zip_keeps_the_folder_tree_instead_of_converting_its_individual_files()
    {
        Assert.SkipUnless(new Filee.Engines.Archives.ArchiveConverter().GetStatus().IsAvailable, "Bundled 7-Zip is required.");
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "photos.jpg")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        await File.WriteAllTextAsync(Path.Combine(folder, "input.txt"), "folder contents", TestContext.Current.CancellationToken);
        var result = await Run("convert", folder, "--to", "zip", "--output", _folder);
        Assert.True(result.Code == 0, result.Errors + result.Output);
        using var archive = System.IO.Compression.ZipFile.OpenRead(folder + ".zip");
        Assert.NotNull(archive.GetEntry("photos.jpg/empty/"));
        using var reader = new StreamReader(archive.GetEntry("photos.jpg/input.txt")!.Open());
        Assert.Equal("folder contents", reader.ReadToEnd());
    }

    [Fact]
    public async Task Folder_to_iso_uses_the_folder_itself_and_preserves_a_dotted_name()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "photos.jpg")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "input.txt"), "ISO contents", TestContext.Current.CancellationToken);
        var result = await Run("convert", folder, "--to", "iso", "--output", _folder);
        Assert.True(result.Code == 0, result.Errors + result.Output);
        Assert.True(File.Exists(folder + ".iso"));
        Assert.True(File.Exists(Path.Combine(folder, "input.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Drive_root_to_iso_is_refused_clearly_without_writing_an_image(bool json)
    {
        var output = Path.Combine(_folder, "root-output");
        var args = new List<string> { "convert", Path.GetPathRoot(_folder)!, "--to", "iso", "--output", output };
        if (json) args.AddRange(["--json", "--quiet"]);

        var result = await Run(args.ToArray());

        Assert.Equal(1, result.Code);
        Assert.Contains("Select a folder inside", result.Output + result.Errors);
        Assert.False(Directory.Exists(output));
        if (json)
        {
            using var document = JsonDocument.Parse(result.Output);
            Assert.Equal(1, document.RootElement.GetProperty("failed").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("converted").GetInt32());
            Assert.Equal("error.iso_root", document.RootElement.GetProperty("files")[0].GetProperty("errorKey").GetString());
        }
    }

    [Fact]
    public async Task Zip_to_iso_converts_archive_contents_without_manual_extraction()
    {
        var input = Path.Combine(_folder, "backup.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(input, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("contents.txt").Open())) writer.Write("archived contents");
        var result = await Run("convert", input, "--to", "iso", "--output", _folder);
        Assert.True(result.Code == 0, result.Errors + result.Output);
        Assert.True(File.Exists(Path.Combine(_folder, "backup.iso")));
        Assert.True(File.Exists(input));
    }

    private readonly string _folder = Directory.CreateTempSubdirectory("filee-cli-").FullName;
    private readonly string _settings = Directory.CreateTempSubdirectory("filee-cli-settings-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _folder, _settings })
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string Png(string name)
    {
        var path = Path.Combine(_folder, name);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 40, 30);
        image.Write(path, ImageMagick.MagickFormat.Png);
        return path;
    }

    private async Task<(int Code, string Output, string Errors)> Run(params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await Cli.RunAsync(args, output, errors, TestContext.Current.CancellationToken, () => CliHost.Create(_settings));
        return (code, output.ToString(), errors.ToString());
    }

    /// <summary>A one-page PDF whose page width tells it apart in a merge, with the date modified it is given.</summary>
    private string SizedPdf(string name, int width, DateTime modified, int height = 500)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            var page = document.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(width);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(height);
            document.Save(path);
        }
        File.SetLastWriteTimeUtc(path, modified);
        return path;
    }

    /// <summary>L1: saved on/off presets reach the real merger without changing inputs or the saved library.</summary>
    [Fact]
    public async Task Merge_paper_saved_presets_control_all_three_page_sizes()
    {
        var day = new DateTime(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var inputs = new[]
        {
            SizedPdf("sheet.pdf", 842, day, 596),
            SizedPdf("letter.pdf", 612, day, 792),
            SizedPdf("image.pdf", 1200, day, 675),
        };
        AddPresets(
            new Preset
            {
                Id = "merge-paper-a4",
                TargetFormat = "pdf",
                Pdf = { MergeIntoSingle = true, NormalizeMergePaper = true, MergeOrientation = PdfSheetOrientation.Portrait },
            },
            new Preset { Id = "merge-paper-off", TargetFormat = "pdf", Pdf = { MergeIntoSingle = true } });
        var protectedPaths = inputs.Append(Path.Combine(_settings, "presets.json")).ToArray();
        var hashes = protectedPaths.Select(FileHash).ToArray();

        foreach (var enabled in new[] { true, false })
        {
            var mode = enabled ? "a4" : "off";
            var output = Path.Combine(_folder, mode);
            var result = await Run("convert", inputs[0], inputs[1], inputs[2], "--preset", $"merge-paper-{mode}", "-o", output);
            Assert.True(result.Code == 0, result.Errors + result.Output);
            using var merged = PdfReader.Open(Assert.Single(Directory.GetFiles(output, "*.pdf")), PdfDocumentOpenMode.Import);
            Assert.Equal(3, merged.PageCount);
            for (var i = 0; i < inputs.Length; i++)
            {
                var width = enabled ? 595.28 : new[] { 842d, 612, 1200 }[i];
                var height = enabled ? 841.89 : new[] { 596d, 792, 675 }[i];
                AssertMergePage(merged.Pages[i], width, height, enabled);
            }
            Assert.Equal(hashes, protectedPaths.Select(FileHash));
        }
    }

    /// <summary>L2: mixed real conversions are normalized only after their intermediate PDFs have been produced.</summary>
    [Fact]
    public async Task Merge_paper_mixed_inputs_keep_the_sum_of_individually_converted_pages()
    {
        Assert.SkipUnless(new Filee.Engines.Office.LibreOfficeConverter(new EngineEnvironment(_settings)).GetStatus().IsAvailable,
            "LibreOffice is required for the mixed DOCX/XLSX integration case.");
        var docx = OfficeFile("document.docx", spreadsheet: false);
        var xlsx = OfficeFile("workbook.xlsx", spreadsheet: true);
        var store = new UserDataStore(_settings);
        store.Load();
        store.Settings.LibreOfficeForDocuments = true;
        store.Settings.LibreOfficeForSheets = true;
        store.SaveSettings();
        AddPresets(
            new Preset { Id = "merge-paper-individual", TargetFormat = "pdf" },
            new Preset
            {
                Id = "merge-paper-mixed",
                TargetFormat = "pdf",
                Pdf = { MergeIntoSingle = true, NormalizeMergePaper = true, MergeOrientation = PdfSheetOrientation.Portrait },
            });
        var inputs = new[] { SizedPdf("source.pdf", 842, DateTime.UnixEpoch, 596), Png("picture.png"), docx, xlsx };
        var protectedPaths = inputs.Append(Path.Combine(_settings, "presets.json")).ToArray();
        var hashes = protectedPaths.Select(FileHash).ToArray();
        var pages = 0;
        for (var i = 0; i < inputs.Length; i++)
        {
            var individual = Path.Combine(_folder, $"individual-{i}");
            var result = await Run("convert", inputs[i], "--preset", "merge-paper-individual", "-o", individual);
            Assert.True(result.Code == 0, result.Errors + result.Output);
            using var pdf = PdfReader.Open(Assert.Single(Directory.GetFiles(individual, "*.pdf")), PdfDocumentOpenMode.Import);
            Assert.True(pdf.PageCount > 0);
            pages += pdf.PageCount;
        }
        var output = Path.Combine(_folder, "mixed");
        var merge = await Run(["convert", .. inputs, "--preset", "merge-paper-mixed", "-o", output]);
        Assert.True(merge.Code == 0, merge.Errors + merge.Output);
        using var merged = PdfReader.Open(Assert.Single(Directory.GetFiles(output, "*.pdf")), PdfDocumentOpenMode.Import);
        Assert.Equal(pages, merged.PageCount);
        foreach (var page in merged.Pages)
            AssertMergePage(page, 595.28, 841.89, normalized: true);
        Assert.Equal(hashes, protectedPaths.Select(FileHash));
    }

    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string OfficeFile(string name, bool spreadsheet)
    {
        var path = Path.Combine(_folder, name);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Part(string part, string xml)
        {
            var entry = zip.CreateEntry(part);
            entry.LastWriteTime = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(xml);
        }
        const string rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var main = spreadsheet ? "xl/workbook.xml" : "word/document.xml";
        var type = spreadsheet ? "spreadsheetml.sheet" : "wordprocessingml.document";
        Part("[Content_Types].xml", $"""
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/{main}" ContentType="application/vnd.openxmlformats-officedocument.{type}.main+xml"/>
            {(spreadsheet ? "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" : "")}
            </Types>
            """);
        Part("_rels/.rels", $"<Relationships xmlns=\"{rel}\"><Relationship Id=\"rId1\" Type=\"{office}/officeDocument\" Target=\"{main}\"/></Relationships>");
        if (spreadsheet)
        {
            Part(main, $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{office}\"><sheets><sheet name=\"QA\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Part("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{rel}\"><Relationship Id=\"rId1\" Type=\"{office}/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Part("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>Merge paper sheet</t></is></c></row></sheetData></worksheet>
                """);
        }
        else
            Part(main, """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                <w:body><w:p><w:r><w:t>Merge paper document</w:t></w:r></w:p></w:body></w:document>
                """);
        return path;
    }

    private static void AssertMergePage(PdfPage page, double width, double height, bool normalized)
    {
        Assert.InRange(page.MediaBox.X1, -.01, .01);
        Assert.InRange(page.MediaBox.Y1, -.01, .01);
        Assert.InRange(page.MediaBox.Width, width - .01, width + .01);
        Assert.InRange(page.MediaBox.Height, height - .01, height + .01);
        Assert.Equal(0, page.Rotate);
        if (!normalized)
            return;
        Assert.InRange(page.CropBox.X1, -.01, .01);
        Assert.InRange(page.CropBox.Y1, -.01, .01);
        Assert.InRange(page.CropBox.Width, width - .01, width + .01);
        Assert.InRange(page.CropBox.Height, height - .01, height + .01);
        Assert.Equal(1, page.Elements.GetInteger("/UserUnit"));
    }

    [Fact]
    public async Task Source_bookmarks_saved_presets_control_real_merged_outlines()
    {
        var day = new DateTime(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var a = SizedPdf("A.pdf", 200, day);
        var b = SizedPdf("B.pdf", 300, day);
        AddChapter(a, "A chapter", addPage: true);
        AddChapter(b, "B chapter", addPage: false);
        AddPresets(
            new Preset { Id = "bookmarks-on", TargetFormat = "pdf", Pdf = { MergeIntoSingle = true, SourceBookmarks = true } },
            new Preset { Id = "bookmarks-off", TargetFormat = "pdf", Pdf = { MergeIntoSingle = true } });
        var presetsPath = Path.Combine(_settings, "presets.json");
        var saved = File.ReadAllBytes(presetsPath);

        foreach (var enabled in new[] { true, false })
        {
            var mode = enabled ? "on" : "off";
            var output = Path.Combine(_folder, mode);
            var result = await Run("convert", a, b, "--preset", $"bookmarks-{mode}", "-o", output);
            Assert.True(result.Code == 0, result.Errors + result.Output);
            Assert.Equal(saved, File.ReadAllBytes(presetsPath));
            using var merged = PdfReader.Open(Assert.Single(Directory.GetFiles(output, "*.pdf")), PdfDocumentOpenMode.Import);
            Assert.Equal(3, merged.PageCount);
            var outlines = Dictionary(merged.Internals.Catalog.Elements["/Outlines"]);
            var first = Dictionary(outlines.Elements["/First"]);
            var second = Dictionary(first.Elements["/Next"]);
            Assert.Null(second.Elements["/Next"]);
            if (enabled)
            {
                Assert.Equal("A.pdf", first.Elements.GetString("/Title"));
                Assert.Equal("B.pdf", second.Elements.GetString("/Title"));
                PointsAt(first, merged, 0);
                PointsAt(second, merged, 2);
                var aChapter = Dictionary(first.Elements["/First"]);
                var bChapter = Dictionary(second.Elements["/First"]);
                Assert.Equal("A chapter", aChapter.Elements.GetString("/Title"));
                Assert.Equal("B chapter", bChapter.Elements.GetString("/Title"));
                PointsAt(aChapter, merged, 1);
                PointsAt(bChapter, merged, 2);
                Assert.Null(aChapter.Elements["/Next"]);
                Assert.Null(bChapter.Elements["/Next"]);
            }
            else
            {
                Assert.Equal("A chapter", first.Elements.GetString("/Title"));
                Assert.Equal("B chapter", second.Elements.GetString("/Title"));
                PointsAt(first, merged, 1);
                PointsAt(second, merged, 2);
                Assert.Null(first.Elements["/First"]);
                Assert.Null(second.Elements["/First"]);
            }
        }

        var store = new UserDataStore(_settings);
        store.Load();
        Assert.True(store.Presets.Single(preset => preset.Id == "bookmarks-on").Pdf.SourceBookmarks);
        Assert.False(store.Presets.Single(preset => preset.Id == "bookmarks-off").Pdf.SourceBookmarks);

        static PdfDictionary Dictionary(PdfItem? item) =>
            Assert.IsAssignableFrom<PdfDictionary>(item is PdfReference reference ? reference.Value : item);

        static void PointsAt(PdfDictionary outline, PdfDocument document, int page)
        {
            var destination = Assert.IsType<PdfArray>(outline.Elements["/Dest"]);
            Assert.Equal(2, destination.Elements.Count);
            Assert.Equal(document.Pages[page].Reference!.ObjectID, Assert.IsType<PdfReference>(destination.Elements[0]).ObjectID);
            Assert.Equal("/Fit", Assert.IsType<PdfName>(destination.Elements[1]).Value);
        }

        static void AddChapter(string path, string title, bool addPage)
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            if (addPage)
                document.AddPage();
            var root = new PdfDictionary(document);
            var chapter = new PdfDictionary(document);
            document.Internals.AddObject(root);
            document.Internals.AddObject(chapter);
            var destination = new PdfArray(document);
            destination.Elements.Add(document.Pages[addPage ? 1 : 0].Reference!);
            destination.Elements.Add(new PdfName("/Fit"));
            chapter.Elements["/Title"] = new PdfString(title);
            chapter.Elements["/Dest"] = destination;
            chapter.Elements["/Parent"] = root.Reference;
            root.Elements["/Type"] = new PdfName("/Outlines");
            root.Elements["/First"] = chapter.Reference;
            root.Elements["/Last"] = chapter.Reference;
            root.Elements["/Count"] = new PdfInteger(1);
            document.Internals.Catalog.Elements["/Outlines"] = root.Reference;
            document.Save(path);
        }
    }

    [Theory]
    [InlineData("name", new[] { 200d, 300d, 400d })]
    [InlineData("name-desc", new[] { 400d, 300d, 200d })]
    [InlineData("modified", new[] { 300d, 200d, 400d })]
    [InlineData("MODIFIED-DESC", new[] { 400d, 200d, 300d })]
    public async Task Sort_sets_the_order_files_merge_in(string sort, double[] pageWidths)
    {
        var day = new DateTime(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        SizedPdf("page 2.pdf", 200, day.AddHours(2));
        SizedPdf("page 10.pdf", 300, day.AddHours(1));
        SizedPdf("page 30.pdf", 400, day.AddHours(3));
        var output = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;

        var (code, _, errors) = await Run("convert", _folder, "--preset", "merge-pdf", "--sort", sort, "-o", output);

        Assert.True(code == 0, errors);
        using var merged = PdfSharp.Pdf.IO.PdfReader.Open(Assert.Single(Directory.GetFiles(output, "*.pdf")), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(pageWidths, merged.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => p.Width.Point));
    }

    [Theory]
    [InlineData("name", new[] { 200d, 300d, 400d, 500d, 600d, 700d })]
    [InlineData("name-desc", new[] { 700d, 600d, 500d, 400d, 300d, 200d })]
    public async Task Sort_by_name_keeps_each_folders_files_together_when_recursive(string sort, double[] pageWidths)
    {
        // By file name alone these interleave: sub2\a, sub1\b, c, ...
        var day = new DateTime(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        SizedPdf(Path.Combine("sub1", "deep", "z.pdf"), 200, day);
        SizedPdf(Path.Combine("sub1", "b.pdf"), 300, day);
        SizedPdf(Path.Combine("sub1", "page 10.pdf"), 400, day);
        SizedPdf(Path.Combine("sub2", "a.pdf"), 500, day);
        SizedPdf(Path.Combine("sub10", "a.pdf"), 600, day);
        SizedPdf("c.pdf", 700, day);
        var output = Directory.CreateDirectory(Path.Combine(_settings, "out")).FullName;

        var (code, _, errors) = await Run("convert", _folder, "-r", "--preset", "merge-pdf", "--sort", sort, "-o", output);

        Assert.True(code == 0, errors);
        using var merged = PdfSharp.Pdf.IO.PdfReader.Open(Assert.Single(Directory.GetFiles(output, "*.pdf")), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(pageWidths, merged.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => p.Width.Point));
    }

    [Theory]
    [InlineData("--sort", "size", "--sort must be")]
    [InlineData("--sort", null, "--sort needs a value")]
    public void Sort_refuses_an_order_it_does_not_know(string option, string? value, string error)
    {
        string[] args = value is null ? ["convert", "a.pdf", "--to", "pdf", option] : ["convert", "a.pdf", "--to", "pdf", option, value];
        Assert.StartsWith(error, CliOptions.Parse(args).Error);
    }

    /// <summary>--split-every with a length (#1259): parts of a video; a plain number stays pages of a PDF.</summary>
    [Theory]
    [InlineData("50", 50, null)]
    [InlineData("90s", null, 90)]
    [InlineData("10m", null, 600)]
    [InlineData("10min", null, 600)]
    [InlineData("2.5m", null, 150)]
    [InlineData("1h30m", null, 5400)]
    [InlineData("1H", null, 3600)]
    [InlineData("10:00", null, 600)]
    [InlineData("1:30:00", null, 5400)]
    public void Split_every_takes_pages_or_a_length_of_video(string value, int? pages, int? seconds)
    {
        var options = CliOptions.Parse(["convert", "a.mp4", "--to", "mp4", "--split-every", value]);
        Assert.Null(options.Error);
        Assert.Equal((pages, seconds), (options.SplitEvery, options.SplitEverySeconds));
    }

    [Theory]
    [InlineData("10x")]
    [InlineData("m")]
    [InlineData("1:2:3:4")]
    [InlineData("0s")]
    [InlineData("25h")]
    [InlineData("-5m")]
    public void Split_every_refuses_what_is_neither_pages_nor_a_length(string value) =>
        Assert.StartsWith("--split-every must be", CliOptions.Parse(["convert", "a.mp4", "--to", "mp4", "--split-every", value]).Error);

    [Fact]
    public void Sort_is_refused_by_watch_rather_than_ignored() =>
        Assert.Contains("--sort", CliOptions.Parse(["watch", "inbox", "--to", "pdf", "--sort", "name"]).Error);

    [Fact]
    public async Task Converts_to_a_format_of_a_turned_off_field()
    {
        File.WriteAllText(Path.Combine(_settings, "settings.json"), """{"schemaVersion":58,"turnedOffFields":["video-pro"]}""");
        var srt = Path.Combine(_folder, "a.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var (code, output, errors) = await Run("convert", srt, "--to", "ass");
        Assert.True(code == Cli.Success, output + errors);
        var result = Path.Combine(_folder, "a.ass");
        Assert.True(File.Exists(result));
        Assert.True(new FileInfo(result).Length > 0);
        (code, output, errors) = await Run("formats", "srt");
        Assert.True(code == Cli.Success, errors);
        Assert.Contains("ass", output);
    }

    private static readonly ushort[] FitCrcTable =
        [0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400];

    private static ushort FitCrc(IEnumerable<byte> bytes)
    {
        ushort crc = 0;
        foreach (var b in bytes)
        {
            crc = (ushort)((crc >> 4) ^ FitCrcTable[crc & 0xF] ^ FitCrcTable[b & 0xF]);
            crc = (ushort)((crc >> 4) ^ FitCrcTable[crc & 0xF] ^ FitCrcTable[b >> 4]);
        }
        return crc;
    }

    /// <summary>A FIT activity built from the protocol: a 14-byte header, an activity file_id, one record
    /// definition (timestamp, latitude, longitude) with a message per point, and the header and file CRCs.</summary>
    private static byte[] FitActivity(params (uint Time, int Lat, int Lon)[] points)
    {
        var data = new List<byte> { 0x40, 0, 0, 0, 0, 1, 0, 1, 0, 0x00, 4 };
        data.AddRange([0x41, 0, 0, 20, 0, 3, 253, 4, 0x86, 0, 4, 0x85, 1, 4, 0x85]);
        var value = new byte[4];
        foreach (var (time, lat, lon) in points)
        {
            data.Add(0x01);
            BinaryPrimitives.WriteUInt32LittleEndian(value, time);
            data.AddRange(value);
            BinaryPrimitives.WriteInt32LittleEndian(value, lat);
            data.AddRange(value);
            BinaryPrimitives.WriteInt32LittleEndian(value, lon);
            data.AddRange(value);
        }
        var file = new List<byte> { 14, 0x10, 0x34, 0x08 };
        BinaryPrimitives.WriteInt32LittleEndian(value, data.Count);
        file.AddRange(value);
        file.AddRange(".FIT"u8.ToArray());
        var headerCrc = FitCrc(file);
        file.AddRange([(byte)headerCrc, (byte)(headerCrc >> 8)]);
        file.AddRange(data);
        var fileCrc = FitCrc(file);
        file.AddRange([(byte)fileCrc, (byte)(fileCrc >> 8)]);
        return [.. file];
    }

    [Fact]
    public async Task Fit_activity_converts_to_gpx_with_point_times_using_the_builtin_preset()
    {
        var fit = Path.Combine(_folder, "ride.fit");
        File.WriteAllBytes(fit, FitActivity((0x1000003B, 0x20000000, 0x40000000), (0x1000003C, 0x30000000, 0x30000000),
            (0x1000003D, 0x40000000, 0x20000000)));

        var (code, output, errors) = await Run("convert", fit, "--preset", "to-gpx");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Contains("Converted 1 of 1", output);
        var gpx = XDocument.Load(Path.Combine(_folder, "ride.gpx"));
        var points = gpx.Descendants().Where(element => element.Name.LocalName == "trkpt").ToArray();
        Assert.Equal(3, points.Length);
        Assert.All(points, point => Assert.Single(point.Elements(), element => element.Name.LocalName == "time"));
    }

    [Fact]
    public async Task Converts_files_and_wildcards_into_an_output_folder()
    {
        Png("a.png");
        Png("b.png");
        var outFolder = Path.Combine(_folder, "out");

        var (code, output, errors) = await Run("convert", Path.Combine(_folder, "*.png"), "--to", "jpg", "-o", outFolder, "--quality", "70");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Contains("Converted 2 of 2", output);
        Assert.True(File.Exists(Path.Combine(outFolder, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(outFolder, "b.jpg")));
    }

    [Fact]
    public async Task Zip_saves_the_result_of_each_file_as_one_zip()
    {
        var geojson = Path.Combine(_folder, "places.geojson");
        File.WriteAllText(geojson, """{"type":"Feature","properties":{"n":1},"geometry":{"type":"Point","coordinates":[1,2]}}""");

        var (code, _, _) = await Run("convert", geojson, "--to", "shp", "--zip");

        Assert.Equal(Cli.Success, code);
        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(_folder, "places.zip"));
        Assert.Equal(["places.cpg", "places.dbf", "places.prj", "places.shp", "places.shx"], zip.Entries.Select(e => e.Name).Order().ToArray());
        Assert.False(File.Exists(Path.Combine(_folder, "places.shp")));
    }

    [Fact]
    public async Task Keep_dates_copies_the_dates_of_the_originals()
    {
        var source = Png("old.png");
        var modified = new DateTime(2018, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);

        var (code, _, _) = await Run("convert", source, "--to", "jpg", "--keep-dates");

        Assert.Equal(Cli.Success, code);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(_folder, "old.jpg")));
    }

    [Fact]
    public async Task Json_output_lists_every_file()
    {
        var source = Png("a.png");

        var (code, output, _) = await Run("convert", source, "--to", ".webp", "--json");

        Assert.Equal(Cli.Success, code);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("converted").GetInt32());
        var file = json.RootElement.GetProperty("files")[0];
        Assert.Equal("done", file.GetProperty("state").GetString());
        Assert.EndsWith("a.webp", file.GetProperty("outputs")[0].GetString());
    }

    [Fact]
    public async Task Compress_pdf_says_when_a_pdf_cant_get_smaller()
    {
        var source = Path.Combine(_folder, "scan.pdf");
        using (var image = new ImageMagick.MagickImage("gradient:#C04020-#2060C0", 1800, 1800))
        {
            image.AddNoise(ImageMagick.NoiseType.Gaussian, 0.6);
            image.Density = new ImageMagick.Density(600);
            image.Write(source, ImageMagick.MagickFormat.Pdf);
        }

        var (code, output, errors) = await Run("convert", source, "--preset", "compress-pdf", "--json");
        Assert.Equal(Cli.Success, code);
        var compressed = Path.Combine(_folder, "scan_compressed.pdf");
        Assert.True(new FileInfo(compressed).Length < new FileInfo(source).Length / 2, errors);
        using (var json = JsonDocument.Parse(output))
            Assert.False(json.RootElement.GetProperty("files")[0].TryGetProperty("notice", out _));

        // Compressed already: written unchanged, with a note.
        (code, output, _) = await Run("convert", compressed, "--preset", "compress-pdf");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("scan_compressed_compressed.pdf (Couldn't make it smaller", output);
    }

    /// <summary>
    /// --pages (#981): the range on the command line, over the preset's; Split PDF, which asks on the donut in a new
    /// install, takes it here without asking.
    /// </summary>
    [Fact]
    public async Task Pages_takes_the_range_of_a_pdf_over_the_presets()
    {
        var source = Path.Combine(_folder, "book.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            for (var i = 0; i < 6; i++)
                document.AddPage();
            document.Save(source);
        }

        var (code, output, errors) = await Run("convert", source, "--preset", "pdf-split", "--pages", "2\u20133, 5");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Contains("Converted 1 of 1", output);
        Assert.Equal(["book_p2.pdf", "book_p3.pdf", "book_p5.pdf"],
            Directory.EnumerateFiles(Path.Combine(_folder, "book"), "*.pdf").Select(path => Path.GetFileName(path)).Order().ToArray());

        (code, _, errors) = await Run("convert", source, "--to", "pdf", "--pages", "4-", "-o", Path.Combine(_folder, "out"));
        Assert.Equal(Cli.Success, code);
        using var extract = PdfSharp.Pdf.IO.PdfReader.Open(Path.Combine(_folder, "out", "book.pdf"), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(3, extract.PageCount);
    }

    /// <summary>--split-every and --split-parts (#1259): a PDF in files of so many pages, or in equal parts.</summary>
    [Fact]
    public async Task Split_every_and_split_parts_cut_a_pdf_into_files()
    {
        var source = Path.Combine(_folder, "book.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            for (var i = 0; i < 10; i++)
                document.AddPage();
            document.Save(source);
        }

        var (code, _, errors) = await Run("convert", source, "--preset", "pdf-split", "--split-every", "4");
        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Equal(["book_p1-4.pdf", "book_p5-8.pdf", "book_p9-10.pdf"],
            Directory.EnumerateFiles(Path.Combine(_folder, "book"), "*.pdf").Select(path => Path.GetFileName(path)).Order().ToArray());

        (code, _, errors) = await Run("convert", source, "--to", "pdf", "--pages", "2-", "--split-parts", "2", "-o", Path.Combine(_folder, "halves"));
        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Equal(["book_p2-6.pdf", "book_p7-10.pdf"],
            Directory.EnumerateFiles(Path.Combine(_folder, "halves"), "*.pdf").Select(path => Path.GetFileName(path)).Order().ToArray());
    }

    [Fact]
    public async Task A_folder_converts_the_files_filee_knows()
    {
        Png("a.png");
        File.WriteAllText(Path.Combine(_folder, "notes.unknownformat"), "x");
        // Filee reads a program's icon only when the program is named, not every installer in a folder (#1188).
        File.WriteAllBytes(Path.Combine(_folder, "setup.exe"), "MZ"u8.ToArray());

        var (code, output, _) = await Run("convert", _folder, "--preset", BuiltInData.CreatePresets().First(p => p.TargetFormat == "jpg").Id);

        Assert.Equal(Cli.Success, code);
        Assert.Contains("Converted 1 of 1", output);
    }

    [Fact]
    public async Task A_file_that_cannot_be_converted_fails_with_exit_code_1()
    {
        var text = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(text, "hello");

        var (code, output, _) = await Run("convert", text, "--to", "mp3");

        Assert.Equal(Cli.SomeFailed, code);
        Assert.Contains("fail", output);
    }

    [Fact]
    public async Task An_empty_file_says_it_is_empty()
    {
        var empty = Path.Combine(_folder, "new.docx");
        File.WriteAllBytes(empty, []);

        var (code, output, _) = await Run("convert", empty, "--to", "pdf");

        Assert.Equal(Cli.SomeFailed, code);
        Assert.Contains("new.docx: The file is empty (0 bytes).", output);
    }

    [Theory]
    [InlineData(new[] { "convert", "a.png" }, "--to")]
    [InlineData(new[] { "convert", "a.png", "--to", "jpg", "--preset", "x" }, "--to")]
    [InlineData(new[] { "convert", "a.png", "--to", "nosuchformat" }, "unknown format")]
    [InlineData(new[] { "convert", "a.png", "--preset", "nosuchpreset" }, "no preset")]
    [InlineData(new[] { "convert", "a.png", "--preset", "rotate" }, "rotate-left, rotate-right, rotate-180")]
    [InlineData(new[] { "convert", "--to", "jpg" }, "at least one file")]
    [InlineData(new[] { "convert", "a.png", "--to", "jpg", "--quality", "300" }, "--quality")]
    [InlineData(new[] { "convert", "a.png", "--to", "pdf", "--pages", "1-x" }, "--pages")]
    [InlineData(new[] { "convert", "a.png", "--to", "pdf", "--split-every", "0" }, "--split-every")]
    [InlineData(new[] { "convert", "a.png", "--to", "pdf", "--split-parts", "1" }, "--split-parts")]
    [InlineData(new[] { "convert", "a.png", "--to", "pdf", "--split-every", "5", "--split-parts", "2" }, "either --split-every or --split-parts")]
    [InlineData(new[] { "convert", "a.png", "--to", "mp4", "--split-every", "10m", "--split-parts", "2" }, "either --split-every or --split-parts")]
    // Pages for a video, or a length for a PDF, would split nothing (#1259).
    [InlineData(new[] { "convert", "a.png", "--to", "mp4", "--split-every", "10" }, "Give a length, e.g. 10s or 10m")]
    [InlineData(new[] { "convert", "a.png", "--to", "mkv", "--split-every", "3" }, "--split-every 3 is a number of pages, but this makes videos")]
    [InlineData(new[] { "convert", "a.png", "--to", "pdf", "--split-every", "10m" }, "--split-every 10m is a length of video, but this makes PDFs")]
    [InlineData(new[] { "convert", "a.png", "--preset", "pdf-split", "--split-every", "1:30" }, "Give a number of pages")]
    [InlineData(new[] { "frobnicate" }, "Unknown command")]
    public async Task Wrong_arguments_explain_themselves_with_exit_code_2(string[] args, string message)
    {
        Png("a.png");
        var withPaths = args.Select(a => a == "a.png" ? Path.Combine(_folder, a) : a).ToArray();

        var (code, _, errors) = await Run(withPaths);

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains(message, errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_input_is_reported()
    {
        var (code, _, errors) = await Run("convert", Path.Combine(_folder, "missing.png"), "--to", "jpg");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("not found", errors);
    }

    [Fact]
    public async Task Watch_needs_an_existing_folder()
    {
        var (code, _, errors) = await Run("watch", Path.Combine(_folder, "nope"), "--to", "jpg");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("not found", errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watch_converts_files_that_arrive_until_cancelled()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new StringWriter();
        var watching = Cli.RunAsync(["watch", _folder, "--to", "jpg", "--settle", "0.2"], TextWriter.Synchronized(output), TextWriter.Null,
            stop.Token, () => CliHost.Create(_settings));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Png("arrived.png");
        var converted = Path.Combine(_folder, "converted", "arrived.jpg");
        for (var i = 0; i < 100 && !File.Exists(converted); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        stop.Cancel();

        Assert.Equal(Cli.Success, await watching);
        Assert.True(File.Exists(converted), output.ToString());
    }

    [Fact]
    public async Task A_shared_watch_folder_does_not_reconvert_its_outputs_after_restarting_the_command()
    {
        var args = new[] { "watch", _folder, "--to", "jpg", "-o", _folder, "--move-originals", "--settle", "0.1" };
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            var watching = Cli.RunAsync(args, TextWriter.Null, TextWriter.Null, stop.Token, () => CliHost.Create(_settings));
            var input = Path.Combine(_folder, "arrived.webp");
            using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 40, 30))
                image.Write(input, ImageMagick.MagickFormat.WebP);
            var output = Path.Combine(_folder, "arrived.jpg");
            for (var i = 0; i < 100 && !File.Exists(Path.Combine(_folder, "originals", "arrived.webp")); i++)
                await Task.Delay(100, TestContext.Current.CancellationToken);
            stop.Cancel();
            Assert.Equal(Cli.Success, await watching);
            Assert.True(File.Exists(output));
            Assert.True(File.Exists(Path.Combine(_folder, "originals", "arrived.webp")));
            using var decoded = new ImageMagick.MagickImage(output);
            Assert.Equal(ImageMagick.MagickFormat.Jpeg, decoded.Format);
        }

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            var watching = Cli.RunAsync(args, TextWriter.Null, TextWriter.Null, stop.Token, () => CliHost.Create(_settings));
            // Let the initial inbox scan settle and the one-second conversion timer run twice.
            await Task.Delay(2200, TestContext.Current.CancellationToken);
            stop.Cancel();
            Assert.Equal(Cli.Success, await watching);
            Assert.Equal(["arrived.jpg"], Directory.EnumerateFiles(_folder, "*.jpg").Select(Path.GetFileName));
            Assert.False(File.Exists(Path.Combine(_folder, "originals", "arrived.jpg")));
        }
    }

    [Fact]
    public async Task Formats_and_presets_are_listed()
    {
        var (code, output, _) = await Run("formats", "heic");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("jpg", output);

        (code, output, _) = await Run("presets");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("to-png", output);
    }

    [Fact]
    public async Task Archives_list_folder_as_a_target()
    {
        var catalog = ArchiveCatalog();
        var (code, output, _) = await RunWith(catalog, "formats", "zip");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("folder", output);

        (code, output, _) = await RunWith(catalog, "formats", "png");
        Assert.Equal(Cli.Success, code);
        Assert.DoesNotContain("folder", output);
    }

    [Fact]
    public async Task Formats_json_lists_every_format()
    {
        var (code, output, _) = await Run("formats", "--json");
        Assert.Equal(Cli.Success, code);

        using var json = JsonDocument.Parse(output);
        var formats = json.RootElement.GetProperty("formats").EnumerateArray().ToList();
        var png = Assert.Single(formats, f => f.GetProperty("id").GetString() == "png");
        Assert.Equal("image", png.GetProperty("category").GetString());
        Assert.Contains("png", png.GetProperty("extensions").EnumerateArray().Select(e => e.GetString()));
        Assert.True(png.GetProperty("writable").GetBoolean());
        Assert.DoesNotContain(formats, f => f.GetProperty("id").GetString() == "folder");
    }

    [Fact]
    public async Task Formats_json_of_one_format_matches_the_text()
    {
        var catalog = ArchiveCatalog();
        var (code, text, _) = await RunWith(catalog, "formats", "zip");
        Assert.Equal(Cli.Success, code);
        (code, var output, _) = await RunWith(catalog, "formats", "zip", "--json");
        Assert.Equal(Cli.Success, code);

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal("zip", root.GetProperty("id").GetString());
        Assert.Equal("archive", root.GetProperty("category").GetString());
        var targets = root.GetProperty("targets").EnumerateArray().Select(e => e.GetString()!).ToList();
        var withEngines = root.GetProperty("withEngines").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains("folder", targets.Concat(withEngines));
        Assert.Empty(targets.Intersect(withEngines));
        foreach (var id in targets.Concat(withEngines))
            Assert.Contains(id, text);
    }

    [Fact]
    public async Task Jpeg_xr_converts_on_windows_and_is_no_download_away_elsewhere()
    {
        var (code, output, _) = await Run("formats", "jxr");

        Assert.Equal(Cli.Success, code);
        Assert.Equal(OperatingSystem.IsWindows(), output.Contains("png, jpg", StringComparison.Ordinal));
        // Elsewhere no download would help (on Windows, FFmpeg adds HEIC and video).
        Assert.Equal(OperatingSystem.IsWindows(), output.Contains("optional engines", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_picture_under_the_minimum_is_written_with_its_notice_also_after_a_step_on_the_way()
    {
        // #662. A multi-page TIFF and a merge first turn the files into pages or PNGs: the line for the file the result
        // belongs to is printed once, at the end, with the result and the notice.
        AddPresets(
            new Preset { Id = "min-png", Name = "Min PNG", TargetFormat = "png", Image = { MinSizeMb = 1 } },
            new Preset { Id = "min-tiff", Name = "Min TIFF", TargetFormat = "tiff", Image = { MultiPageTiff = true, MinSizeMb = 1 } },
            new Preset
            {
                Id = "min-merge",
                Name = "Min merge",
                TargetFormat = "png",
                Image = { JoinIntoOne = true, MinSizeMb = 1 },
                Output = { FileNamePattern = "{name}_merged" }
            });
        string notice;
        await using (var host = CliHost.Create(_settings))
            notice = host.Texts["notice.under_min_size"];
        var a = Png("a.png");
        var b = Png("b.png");
        var logo = Path.Combine(_folder, "logo.svg");
        File.WriteAllText(logo, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\"><rect width=\"40\" height=\"30\" fill=\"teal\"/></svg>");

        foreach (var (args, first, result) in new (string[], string, string)[]
                 {
                     (["convert", a, "--preset", "min-png", "--output", Path.Combine(_folder, "out")], "a.png", Path.Combine("out", "a.png")),
                     (["convert", a, b, "--preset", "min-tiff"], "a.png", "a.tiff"),
                     (["convert", logo, b, "--preset", "min-merge"], "logo.svg", "logo_merged.png"),
                 })
        {
            var (code, output, errors) = await Run(args);
            Assert.True(code == Cli.Success, errors + output);
            var line = Assert.Single(output.Split('\n'), l => l.StartsWith("ok", StringComparison.Ordinal) && l.Contains(first, StringComparison.Ordinal)
                && l.IndexOf(first, StringComparison.Ordinal) < l.IndexOf("->", StringComparison.Ordinal));
            Assert.Contains(result, line, StringComparison.Ordinal);
            Assert.Contains(notice, line, StringComparison.Ordinal);

            (code, output, _) = await Run([.. args, "--json", "--overwrite"]);
            Assert.Equal(Cli.Success, code);
            using var json = System.Text.Json.JsonDocument.Parse(output);
            Assert.Equal(notice, json.RootElement.GetProperty("files")[0].GetProperty("notice").GetString());
        }
    }

    /// <summary>Adds presets to the settings folder the CLI runs with.</summary>
    private void AddPresets(params Preset[] presets)
    {
        var store = new Filee.Core.Settings.UserDataStore(_settings);
        store.Load();
        store.Presets.AddRange(presets);
        store.SaveLibrary();
    }

    [Fact]
    public async Task The_command_line_never_moves_an_original_to_the_trash()
    {
        // A preset saved in the app with "Move the original to the Recycle Bin / Trash" (#1387) runs here without it.
        AddPresets(new Preset { Id = "trashing", Name = "Trashing", TargetFormat = "jpg", Output = { TrashOriginalAfterConvert = true } });
        var png = Png("a.png");

        var (code, _, errors) = await Run("convert", png, "--preset", "trashing");

        Assert.Equal(Cli.Success, code);
        Assert.True(File.Exists(Path.Combine(_folder, "a.jpg")), errors);
        Assert.True(File.Exists(png));
    }

    [Fact]
    public async Task A_group_is_never_run_listed_or_in_the_way_of_a_conversion_of_the_same_name()
    {
        AddPresets(
            new Preset { Id = "web-group", Name = "Web", Kind = PresetKind.Group, SubPresetIds = ["to-png", "to-webp"] },
            new Preset { Id = "empty-group", Name = "Empty", Kind = PresetKind.Group },
            new Preset { Id = "my-web", Name = "Web", TargetFormat = "jpg" });
        var png = Png("a.png");

        // "Web" is both a group and a conversion: the conversion runs.
        var (code, _, errors) = await Run("convert", png, "--preset", "Web");
        Assert.Equal(Cli.Success, code);
        Assert.True(File.Exists(Path.Combine(_folder, "a.jpg")), errors);

        // A group by its id says what it holds; one with nothing left says so instead of "Use one of: ".
        (code, _, errors) = await Run("convert", png, "--preset", "web-group");
        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("Use one of: to-png, to-webp", errors);
        (code, _, errors) = await Run("convert", png, "--preset", "Empty");
        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("no presets left", errors);

        var (_, list, _) = await Run("presets");
        Assert.Contains("my-web", list);
        Assert.DoesNotContain("web-group", list);
    }

    [Fact]
    public async Task Data_files_convert_to_each_other()
    {
        var json = Path.Combine(_folder, "config.json");
        await File.WriteAllTextAsync(json, """{"name": "Filee", "ports": [80, 443]}""", TestContext.Current.CancellationToken);

        var (code, output, errors) = await Run("convert", json, "--to", "yaml");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Equal("name: Filee\nports:\n  - 80\n  - 443\n", await File.ReadAllTextAsync(Path.Combine(_folder, "config.yaml"), TestContext.Current.CancellationToken));

        (code, output, _) = await Run("formats", "json");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("yaml, toml, xml", output);
    }

    [Fact]
    public async Task Help_and_version_need_no_engines()
    {
        var (code, output, _) = await Run("help", "convert");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("--to", output);

        (code, output, _) = await Run("--version");
        Assert.Equal(Cli.Success, code);
        Assert.StartsWith("filee ", output);
    }

    [Fact]
    public async Task Case_distinct_inputs_are_both_converted_and_exact_duplicates_are_removed()
    {
        var lower = Png("photo.png");
        if (File.Exists(Path.Combine(_folder, "PHOTO.png")))
        {
            Assert.Skip("The temporary volume is case-insensitive.");
            return;
        }
        var upper = Png("PHOTO.png");
        var outputFolder = Path.Combine(_folder, "out");

        var (code, output, errors) = await Run("convert", lower, upper, lower, "--to", "jpg", "-o", outputFolder, "--json");

        Assert.Equal(Cli.Success, code);
        OnlyEvents(errors);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(2, json.RootElement.GetProperty("converted").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("files").GetArrayLength());
        var outputPaths = json.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("outputs")[0].GetString()!).ToArray();
        Assert.Equal(2, outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(outputPaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task Unicode_spaces_and_quotes_stay_in_a_single_input_argument()
    {
        var name = OperatingSystem.IsWindows() ? "한글 report's 100%.png" : "한글 report's \"quoted\" 100%.png";
        var source = Png(name);
        var outputFolder = Path.Combine(_folder, "결과 output's");

        var (code, output, errors) = await Run("convert", source, "--to", "jpg", "-o", outputFolder, "--json");

        Assert.Equal(Cli.Success, code);
        OnlyEvents(errors);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("converted").GetInt32());
        var result = json.RootElement.GetProperty("files")[0].GetProperty("outputs")[0].GetString();
        Assert.NotNull(result);
        Assert.Equal(outputFolder, Path.GetDirectoryName(result));
        Assert.Contains("한글 report's", Path.GetFileName(result));
        Assert.True(File.Exists(result));
        Assert.True(File.Exists(source));
    }

    // ──────────── Windows bitmap fonts (#177): in-memory samples shared with the engine tests ────────────

    private static readonly string[] FontFormats = ["ttf", "otf", "woff", "woff2", "eot"];

    /// <summary>Writes a synthetic sample to <c>Name.fon</c> in this test's folder (or a subfolder).</summary>
    private string Fon(string sample, string? folder = null)
    {
        folder = Path.Combine(_folder, folder ?? "");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Name.fon");
        File.WriteAllBytes(path, FonBuilder.Sample(sample));
        return path;
    }

    private static string[] Outputs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("files")[0].GetProperty("outputs").EnumerateArray().Select(o => o.GetString()!)];
    }

    [Fact]
    public void Generated_fon_samples_are_written_to_cli_input()
    {
        foreach (var sample in new[] { "Fon-v2-fixed.fon", "Fon-multi.fon" })
        {
            var bytes = FonBuilder.Sample(sample);
            Assert.Equal("MZ"u8.ToArray(), bytes[..2]);
            Assert.Equal(bytes, File.ReadAllBytes(Fon(sample, Path.GetFileNameWithoutExtension(sample))));
        }
    }

    [Theory]
    [InlineData("ttf")]
    [InlineData("otf")]
    [InlineData("woff")]
    [InlineData("woff2")]
    [InlineData("eot")]
    public async Task Fon_single_resource_omits_size_suffix_in_all_five_formats(string format)
    {
        var source = Fon("Fon-v2-fixed.fon");
        var json = Path.Combine(_folder, "json");
        var text = Path.Combine(_folder, "text");

        var (code, output, errors) = await Run("convert", source, "--to", format, "-o", json, "--json");
        Assert.Equal(Cli.Success, code);
        OnlyEvents(errors);
        Assert.Equal([Path.Combine(json, "Name." + format)], Outputs(output));
        Assert.Equal(["Name." + format], Directory.EnumerateFiles(json).Select(Path.GetFileName));

        (code, output, _) = await Run("convert", source, "--to", format, "-o", text);
        Assert.Equal(Cli.Success, code);
        Assert.Contains("Name." + format, output);
        Assert.DoesNotContain("13px", output);
        Assert.Equal(["Name." + format], Directory.EnumerateFiles(text).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Fon_reports_each_strike_and_obeys_conflicts()
    {
        var source = Fon("Fon-multi.fon");
        string[] names = ["Name-13px.ttf", "Name-13px-2.ttf", "Name-14px.ttf"];

        // No conflict, in each format: one file per RT_FONT resource, every path in the text and JSON output.
        foreach (var format in FontFormats)
        {
            var clean = Path.Combine(_folder, "clean-" + format);
            var (code, output, _) = await Run("convert", source, "--to", format, "-o", clean, "--json");
            Assert.Equal(Cli.Success, code);
            Assert.Equal(names.Select(n => Path.Combine(clean, Path.ChangeExtension(n, format))), Outputs(output));
        }
        var seed = Path.Combine(_folder, "clean-ttf");
        var (textCode, text, _) = await Run("convert", source, "--to", "ttf", "-o", Path.Combine(_folder, "text"));
        Assert.Equal(Cli.Success, textCode);
        Assert.All(names, n => Assert.Contains(n, text));

        // One size already there: skip writes the other two, rename and overwrite write three.
        string Folder(string name)
        {
            var folder = Path.Combine(_folder, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Name-13px.ttf"), "old");
            return folder;
        }

        var skip = Folder("skip");
        var (skipCode, skipped, _) = await Run("convert", source, "--to", "ttf", "-o", skip, "--skip", "--json");
        Assert.Equal(Cli.Success, skipCode);
        Assert.Equal([Path.Combine(skip, "Name-13px-2.ttf"), Path.Combine(skip, "Name-14px.ttf")], Outputs(skipped));
        Assert.Equal("old", File.ReadAllText(Path.Combine(skip, "Name-13px.ttf")));

        var rename = Folder("rename");
        var (renameCode, renamed, _) = await Run("convert", source, "--to", "ttf", "-o", rename, "--json");
        Assert.Equal(Cli.Success, renameCode);
        Assert.Equal([Path.Combine(rename, "Name-13px (2).ttf"), Path.Combine(rename, "Name-13px-2.ttf"), Path.Combine(rename, "Name-14px.ttf")],
            Outputs(renamed));
        Assert.Equal("old", File.ReadAllText(Path.Combine(rename, "Name-13px.ttf")));

        var overwrite = Folder("overwrite");
        var (overwriteCode, overwritten, _) = await Run("convert", source, "--to", "ttf", "-o", overwrite, "--overwrite", "--json");
        Assert.Equal(Cli.Success, overwriteCode);
        Assert.Equal(names.Select(n => Path.Combine(overwrite, n)), Outputs(overwritten));
        Assert.All(names, n => Assert.Equal(File.ReadAllBytes(Path.Combine(seed, n)), File.ReadAllBytes(Path.Combine(overwrite, n))));

        // Every size there: skipped, with no outputs.
        var (allCode, all, _) = await Run("convert", source, "--to", "ttf", "-o", seed, "--skip", "--json");
        Assert.Equal(Cli.Success, allCode);
        using var json = JsonDocument.Parse(all);
        Assert.Equal("skipped", json.RootElement.GetProperty("files")[0].GetProperty("state").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("files")[0].GetProperty("outputs").GetArrayLength());
    }

    [Fact]
    public async Task Fon_formats_are_read_only_and_no_to_fon()
    {
        var (code, output, _) = await Run("formats");
        Assert.Equal(Cli.Success, code);
        Assert.Contains(output.Split('\n'), line => line.TrimStart().StartsWith("fon ", StringComparison.Ordinal) && line.Contains("(read only)"));

        (code, output, _) = await Run("formats", "fon");
        Assert.Equal(Cli.Success, code);
        var targets = output.Split('\n')[1].Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(FontFormats.Order(), targets.Order());

        var (refused, _, errors) = await Run("convert", Fon("Fon-v2-fixed.fon"), "--to", "fon");
        Assert.Equal(Cli.UsageError, refused);
        Assert.Contains("can't write", errors);
        Assert.Equal(["Name.fon"], Directory.EnumerateFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Refuses_tool_presets()
    {
        var source = Png("photo.png");
        var (code, _, errors) = await Run("convert", source, "--preset", "tool-crop");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("is an interactive tool, not a conversion", errors);
    }

    private static ConverterCatalog ArchiveCatalog() =>
        new([new ControlledConverter("archive", true, [new("zip", "folder")])]);

    private CliHost ControlledHost(ConverterCatalog catalog)
    {
        var store = new UserDataStore(_settings);
        store.Load();
        var joins = new UnexpectedMerger();
        return new CliHost(store, catalog, new JobQueue(catalog, joins, tiffMerger: joins, mediaJoiner: joins));
    }

    private async Task<(int Code, string Output, string Errors)> RunWith(ConverterCatalog catalog, params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await Cli.RunAsync(args, output, errors, TestContext.Current.CancellationToken, () => ControlledHost(catalog));
        return (code, output.ToString(), errors.ToString());
    }

    private string Input(string extension, string content = "input")
    {
        var path = Path.Combine(_folder, "input." + extension);
        File.WriteAllText(path, content);
        return path;
    }

    private static ControlledConverter Package(string package, bool available, params ConversionEdge[] edges) =>
        new(EngineDownloads.Packages.Single(p => p.Id == package).ConverterIds[0], available, edges);

    private static JsonElement Result(string output)
    {
        using var json = JsonDocument.Parse(output);
        return json.RootElement.Clone();
    }

    private static JsonElement FileResultOf(string output) => Result(output).GetProperty("files")[0];

    private static string[] Strings(JsonElement value) => value.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static List<JsonElement> Events(string lines) =>
        lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Result).ToList();

    private static void OnlyEvents(string errors) =>
        Assert.All(Events(errors), e => Assert.Contains(e.GetProperty("event").GetString(), new[] { "progress", "file" }));

    private static (string Name, string Value)[] Fields(IEnumerable<JsonProperty> properties) =>
        properties.Select(p => (p.Name, JsonSerializer.Serialize(p.Value))).ToArray();

    [Fact]
    public async Task Convert_json_writes_progress_and_file_events_to_stderr()
    {
        var errors = new LineWriter();
        var converter = new ControlledConverter("built-in", true, [new("md", "html")])
        {
            BeforeConvert = async (progress, token) =>
            {
                progress?.Report(0.5);
                for (var i = 0; i < 200 && !errors.Contains("\"event\":\"progress\""); i++)
                    await Task.Delay(25, token);
            },
        };
        var source = Input("md");
        var output = new StringWriter();

        var code = await Cli.RunAsync(["convert", source, "--to", "html", "--json"], output, errors,
            TestContext.Current.CancellationToken, () => ControlledHost(new([converter])));

        Assert.Equal(Cli.Success, code);
        var file = FileResultOf(output.ToString());
        var events = Events(errors.ToString());
        Assert.Contains(events, e => e.GetProperty("event").GetString() == "progress"
            && e.GetProperty("source").GetString() == source
            && e.GetProperty("fraction").GetDouble() is > 0 and < 1);
        var last = events[^1];
        Assert.Equal("file", last.GetProperty("event").GetString());
        Assert.Equal(Fields(file.EnumerateObject()), Fields(last.EnumerateObject().Skip(1)));
        Assert.Single(events, e => e.GetProperty("event").GetString() == "file");
    }

    [Fact]
    public async Task Convert_json_with_quiet_writes_no_events()
    {
        var (code, output, errors) = await RunWith(new([new ControlledConverter("built-in", true, [new("md", "html")])]),
            "convert", Input("md"), "--to", "html", "--json", "--quiet");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Equal(1, Result(output).GetProperty("converted").GetInt32());
    }

    [Fact]
    public async Task Watch_json_prints_nothing_but_events()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new LineWriter();
        var watching = Cli.RunAsync(["watch", _folder, "--to", "jpg", "--settle", "0.2", "--json"], output, TextWriter.Null,
            stop.Token, () => CliHost.Create(_settings));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Png("arrived.png");
        for (var i = 0; i < 100 && !output.Contains("\"event\":\"file\""); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        stop.Cancel();

        Assert.Equal(Cli.Success, await watching);
        var events = Events(output.ToString());
        Assert.Equal("started", events[0].GetProperty("event").GetString());
        Assert.Equal(Path.GetFullPath(_folder), events[0].GetProperty("folder").GetString());
        Assert.Equal("jpg", events[0].GetProperty("target").GetString());
        Assert.Equal(Path.Combine(Path.GetFullPath(_folder), "converted"), events[0].GetProperty("outputFolder").GetString());
        var file = Assert.Single(events, e => e.GetProperty("event").GetString() == "file");
        Assert.Equal("done", file.GetProperty("state").GetString());
        Assert.EndsWith("arrived.png", file.GetProperty("source").GetString());
        Assert.EndsWith("arrived.jpg", file.GetProperty("outputs")[0].GetString());
        Assert.Equal("stopped", events[^1].GetProperty("event").GetString());
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task Watch_json_with_quiet_prints_no_events()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new LineWriter();
        var watching = Cli.RunAsync(["watch", _folder, "--to", "jpg", "--settle", "0.2", "--json", "--quiet"], output, TextWriter.Null,
            stop.Token, () => CliHost.Create(_settings));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        var converted = Path.Combine(_folder, "converted", "arrived.jpg");
        Png("arrived.png");
        for (var i = 0; i < 100 && !File.Exists(converted); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        stop.Cancel();

        Assert.Equal(Cli.Success, await watching);
        Assert.True(File.Exists(converted));
        Assert.Equal("", output.ToString());
    }

    private sealed class LineWriter : StringWriter
    {
        private readonly Lock _gate = new();

        public override void Write(char value)
        {
            lock (_gate)
                base.Write(value);
        }

        public override void Write(string? value)
        {
            lock (_gate)
                base.Write(value);
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
                base.WriteLine(value);
        }

        public bool Contains(string text) => ToString().Contains(text, StringComparison.Ordinal);

        public override string ToString()
        {
            lock (_gate)
                return base.ToString();
        }
    }

    private static void Hints(JsonElement file, params string[] packages)
    {
        Assert.Equal("failed", file.GetProperty("state").GetString());
        Assert.Equal("error.no_route", file.GetProperty("errorKey").GetString());
        if (packages.Length == 0)
            Assert.False(file.TryGetProperty("needsEngines", out _));
        else
            Assert.Equal(packages, Strings(file.GetProperty("needsEngines")));
    }

    [Fact]
    public async Task Presets_json_has_one_envelope_and_explicit_fields()
    {
        var (code, output, errors) = await RunWith(new([]), "presets", "--json");
        Assert.Equal(0, code);
        Assert.Equal("", errors);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(["presets"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.NotEmpty(json.RootElement.GetProperty("presets").EnumerateArray());
        foreach (var preset in json.RootElement.GetProperty("presets").EnumerateArray())
        {
            Assert.Equal(["id", "name", "target"], preset.EnumerateObject().Select(p => p.Name));
            Assert.All(preset.EnumerateObject(), p => Assert.Equal(JsonValueKind.String, p.Value.ValueKind));
        }
        Assert.EndsWith(Environment.NewLine, output);
    }

    [Fact]
    public async Task Presets_json_preserves_visible_presets_and_order()
    {
        AddPresets(
            new() { Id = "z-custom", Name = "공백 \"quoted\" name", TargetFormat = "*" },
            new() { Id = "a-key", NameKey = "preset.merge_pdf", TargetFormat = "folder" },
            new() { Id = "unnamed", TargetFormat = "jpg" },
            new() { Id = "hidden-group", Kind = PresetKind.Group },
            new() { Id = "hidden-tool", Kind = PresetKind.Tool });
        var store = new UserDataStore(_settings);
        store.Load();
        var texts = new EnglishTexts();
        var expected = store.Presets.Where(p => !p.IsGroup && !p.IsTool).ToArray();
        var (_, output, _) = await RunWith(new([]), "presets", "--json");
        var actual = Result(output).GetProperty("presets").EnumerateArray().ToArray();
        Assert.Equal(expected.Select(p => p.Id), actual.Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(expected.Select(p => texts.DisplayName(p)), actual.Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(expected.Select(p => p.TargetFormat), actual.Select(p => p.GetProperty("target").GetString()));
        var (_, text, _) = await RunWith(new([]), "presets");
        Assert.Equal(expected.Select(p => p.Id), text.Split('\n').Skip(1).Take(expected.Length)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]));
    }

    [Fact]
    public async Task Convert_json_keeps_existing_fields()
    {
        var converter = new ControlledConverter("built-in", true, [new("md", "html")])
        {
            Convert = (step, _) =>
            {
                step.Notice?.Invoke("notice.pdf_not_smaller");
                step.MissingFonts?.Invoke(["Missing Family"]);
                return ControlledConverter.Write(step);
            },
        };
        var source = Input("md");
        var catalog = new ConverterCatalog([converter]);
        var (code, output, errors) = await RunWith(catalog, "convert", source, "--to", "html", "--json");
        Assert.Equal(0, code);
        OnlyEvents(errors);
        var root = Result(output);
        Assert.Equal(["preset", "target", "converted", "skipped", "failed", "files"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("HTML", root.GetProperty("preset").GetString());
        Assert.Equal("html", root.GetProperty("target").GetString());
        Assert.Equal(1, root.GetProperty("converted").GetInt32());
        Assert.Equal(0, root.GetProperty("skipped").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        var done = FileResultOf(output);
        Assert.Equal(["source", "state", "outputs", "notice", "missingFonts"], done.EnumerateObject().Select(p => p.Name));
        Assert.Equal(source, done.GetProperty("source").GetString());
        Assert.Equal("done", done.GetProperty("state").GetString());
        Assert.Equal([Path.ChangeExtension(source, "html")], Strings(done.GetProperty("outputs")));
        Assert.Equal(new EnglishTexts()["notice.pdf_not_smaller"], done.GetProperty("notice").GetString());
        Assert.Equal(["Missing Family"], Strings(done.GetProperty("missingFonts")));

        // An allocator skip returns no outputs, notice or fonts.
        converter.Convert = null;
        (_, output, _) = await RunWith(catalog, "convert", source, "--to", "html", "--skip", "--json");
        var skipped = FileResultOf(output);
        Assert.Equal(["source", "state", "outputs"], skipped.EnumerateObject().Select(p => p.Name));
        Assert.Equal("skipped", skipped.GetProperty("state").GetString());
        Assert.Empty(Strings(skipped.GetProperty("outputs")));
        (_, output, _) = await RunWith(new([]), "convert", source, "--to", "mp3", "--json");
        var failed = FileResultOf(output);
        Assert.Equal(["source", "state", "outputs", "error", "errorKey"], failed.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new EnglishTexts()["error.no_route"] + " (md → mp3)", failed.GetProperty("error").GetString());
        Assert.Empty(Strings(failed.GetProperty("outputs")));
        // Cancellation's exact old fields and values are asserted by the event-driven cancellation test.
        await Cancelled_files_do_not_invent_failure_metadata();
    }

    [Fact]
    public async Task Convert_exit_codes_remain_compatible()
    {
        var source = Input("md");
        var catalog = new ConverterCatalog([new ControlledConverter("built-in", true, [new("md", "html")])]);
        var (code, output, _) = await RunWith(catalog, "convert", source, "--to", "html", "--json");
        Assert.Equal(0, code);
        Assert.Equal("done", FileResultOf(output).GetProperty("state").GetString());
        (code, output, _) = await RunWith(catalog, "convert", source, "--to", "html", "--skip", "--json");
        Assert.Equal(0, code);
        Assert.Equal(1, Result(output).GetProperty("skipped").GetInt32());
        Assert.Equal("skipped", FileResultOf(output).GetProperty("state").GetString());
        (code, output, _) = await RunWith(catalog, "convert", source, "--to", "mp3", "--json");
        Assert.Equal(1, code);
        Assert.Equal("failed", FileResultOf(output).GetProperty("state").GetString());
        Assert.Equal(2, (await RunWith(catalog, "convert", source + ".missing", "--to", "html")).Code);
        Assert.Equal(2, (await RunWith(catalog, "convert", source, "--wrong")).Code);
        var empty = Directory.CreateDirectory(Path.Combine(_folder, "empty")).FullName;
        Assert.Equal(3, (await RunWith(catalog, "convert", empty, "--to", "html")).Code);
        await Cancelled_files_do_not_invent_failure_metadata();
    }

    [Fact]
    public async Task No_route_distinguishes_missing_packages_from_impossible_targets()
    {
        var catalog = new ConverterCatalog([Package("pandoc", false, new ConversionEdge("md", "pptx"))]);
        var (code, output, errors) = await RunWith(catalog, "convert", Input("md"), "--to", "pptx", "--json");
        Assert.Equal(1, code);
        OnlyEvents(errors);
        Assert.Equal("failed", Assert.Single(Events(errors), e => e.GetProperty("event").GetString() == "file").GetProperty("state").GetString());
        Hints(FileResultOf(output), "pandoc");
        (code, output, _) = await RunWith(catalog, "convert", Input("txt"), "--to", "mp3", "--json");
        Assert.Equal(1, code);
        Hints(FileResultOf(output));
    }

    [Fact]
    public async Task Optional_targets_and_failure_hints_agree()
    {
        var catalog = new ConverterCatalog([
            new ControlledConverter("built-in", true, [new("md", "html")]),
            new ControlledConverter("rhwp", false, [new("md", "pptx", 1), new("md", "png")]),
            Package("pandoc", false, new ConversionEdge("md", "pptx", 30)),
        ])
        { Priority = ["rhwp", "pandoc", "built-in"] };
        var (_, output, _) = await RunWith(catalog, "formats", "md", "--json");
        var formats = Result(output);
        Assert.Equal(["html"], Strings(formats.GetProperty("targets")));
        Assert.Equal(["pptx"], Strings(formats.GetProperty("withEngines")));
        var optional = Strings(formats.GetProperty("withEngines"));
        var source = Input("md");
        foreach (var target in FormatRegistry.Known.Where(t => t.Writable && t.Id != "md"))
        {
            (_, output, _) = await RunWith(catalog, "convert", source, "--to", target.Id, "--json");
            var file = FileResultOf(output);
            var hasHints = file.TryGetProperty("needsEngines", out var hints);
            Assert.Equal(optional.Contains(target.Id), hasHints);
            if (hasHints)
            {
                Assert.NotEmpty(Strings(hints));
                Hints(file, "pandoc");
            }
        }
    }

    [Fact]
    public async Task Multi_step_failure_lists_all_missing_packages_once()
    {
        var pandoc = Package("pandoc", false, new("md", "html"), new("wav", "pptx"));
        var ffmpeg = Package("ffmpeg", false, new ConversionEdge("html", "wav"));
        var source = Input("md");
        var (_, output, _) = await RunWith(new([pandoc, ffmpeg]), "convert", source, "--to", "pptx", "--json");
        Hints(FileResultOf(output), "pandoc", "ffmpeg");
        ffmpeg.Available = true;
        (_, output, _) = await RunWith(new([pandoc, ffmpeg]), "convert", source, "--to", "pptx", "--json");
        Hints(FileResultOf(output), "pandoc");

        var direct = Package("libreoffice", false, new ConversionEdge("md", "pptx", 40));
        var catalog = new ConverterCatalog([pandoc, ffmpeg, direct]) { Priority = ["pandoc", "ffmpeg", "libreoffice"] };
        (_, output, _) = await RunWith(catalog, "convert", source, "--to", "pptx", "--json");
        Hints(FileResultOf(output), "pandoc");
        direct.Edges = [new("md", "pptx", 1)];
        (_, output, _) = await RunWith(catalog, "convert", source, "--to", "pptx", "--json");
        Hints(FileResultOf(output), "libreoffice");

        var a = Package("pandoc", false, new ConversionEdge("md", "pptx"));
        var b = Package("libreoffice", false, new ConversionEdge("md", "pptx"));
        catalog = new([a, b]) { Priority = [b.Id, a.Id] };
        (_, output, _) = await RunWith(catalog, "convert", source, "--to", "pptx", "--json");
        Hints(FileResultOf(output), "libreoffice");
        catalog.Priority = [a.Id, b.Id];
        (_, output, _) = await RunWith(catalog, "convert", source, "--to", "pptx", "--json");
        Hints(FileResultOf(output), "pandoc");
    }

    [Fact]
    public async Task Engine_hints_exclude_unsupported_and_nonpackage_converters()
    {
        var source = Input("md");
        foreach (var id in new[] { "archive", "rhwp", "wic", "vision", "7zip", "h2orestart", "jre", "vcruntime" })
        {
            var catalog = new ConverterCatalog([new ControlledConverter(id, false, [new("md", "pptx")])]);
            var (_, formats, _) = await RunWith(catalog, "formats", "md", "--json");
            Assert.Empty(Strings(Result(formats).GetProperty("withEngines")));
            var (_, output, _) = await RunWith(catalog, "convert", source, "--to", "pptx", "--json");
            Hints(FileResultOf(output));
        }
        var unsupported = Package("pandoc", false, new ConversionEdge("md", "pptx"));
        unsupported.Reason = "engine.reason.unsupported_platform";
        var (_, listing, _) = await RunWith(new([unsupported]), "formats", "md", "--json");
        Assert.Empty(Strings(Result(listing).GetProperty("withEngines")));
        var (_, failed, _) = await RunWith(new([unsupported]), "convert", source, "--to", "pptx", "--json");
        Hints(FileResultOf(failed));

        var bridge = new ControlledConverter("built-in", true, [new("md", "html")]);
        var catalogWithBridge = new ConverterCatalog([bridge, Package("pandoc", false, new ConversionEdge("html", "pptx"))]);
        (_, failed, _) = await RunWith(catalogWithBridge, "convert", source, "--to", "pptx", "--json");
        Hints(FileResultOf(failed), "pandoc");
        // A cheaper missing non-package alternative must be filtered before choosing the route.
        await Optional_targets_and_failure_hints_agree();
    }

    [Fact]
    public async Task Non_route_failures_do_not_suggest_packages()
    {
        var magick = new Filee.Engines.Magick.MagickImageConverter();
        var catalog = new ConverterCatalog([magick, Package("pandoc", false, new ConversionEdge("png", "jpg"))]);
        Assert.NotNull(catalog.CreatePlanner().Plan("png", "jpg"));
        var (_, output, _) = await RunWith(catalog, "convert", Input("png", "not a png"), "--to", "jpg", "--json");
        Assert.Equal("error.conversion_failed", FileResultOf(output).GetProperty("errorKey").GetString());
        Assert.False(FileResultOf(output).TryGetProperty("needsEngines", out _));
        foreach (var (extension, content, key) in new[] {
            ("png", "", "error.empty_file"), ("unknown", "bytes", "error.unsupported_source") })
        {
            (_, output, _) = await RunWith(catalog, "convert", Input(extension, content), "--to", "jpg", "--json");
            var file = FileResultOf(output);
            Assert.Equal(key, file.GetProperty("errorKey").GetString());
            Assert.False(file.TryGetProperty("needsEngines", out _));
        }
        foreach (var key in new[] { "error.no_route", "error.needs_shaping", "error.cannot_turn", "" })
        {
            var refusal = new ControlledConverter("built-in", true, [new("md", "pptx")])
            {
                Convert = (_, _) => throw new ConversionRefusedException(key, "not a route request"),
            };
            (_, output, _) = await RunWith(new([refusal]), "convert", Input("md"), "--to", "pptx", "--json");
            Assert.Equal(key, FileResultOf(output).GetProperty("errorKey").GetString());
            Assert.False(FileResultOf(output).TryGetProperty("needsEngines", out _));
        }
        var unshaped = new ControlledConverter("rhwp", true, [new("md", "pptx", Unshaped: true)]) { NeedsShaping = true };
        (_, output, _) = await RunWith(new([unshaped]), "convert", Input("md"), "--to", "pptx", "--json");
        Assert.Equal("error.needs_shaping", FileResultOf(output).GetProperty("errorKey").GetString());
        Assert.False(FileResultOf(output).TryGetProperty("needsEngines", out _));
        AddPresets(new Preset { Id = "turn", TargetFormat = "pptx", Image = { Rotation = ImageRotation.Right90 } });
        (_, output, _) = await RunWith(new([new ControlledConverter("built-in", true, [new("md", "pptx")])]),
            "convert", Input("md"), "--preset", "turn", "--json");
        Assert.Equal("error.cannot_turn", FileResultOf(output).GetProperty("errorKey").GetString());
        Assert.False(FileResultOf(output).TryGetProperty("needsEngines", out _));
    }

    [Fact]
    public async Task Cancelled_files_do_not_invent_failure_metadata()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var converter = new ControlledConverter("built-in", true, [new("md", "html")])
        {
            Convert = async (_, token) =>
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => cancelled.TrySetCanceled(token));
                started.TrySetResult();
                try { await cancelled.Task; }
                finally { ended.TrySetResult(); }
                return [];
            },
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new StringWriter();
        var errors = new StringWriter();
        var running = Cli.RunAsync(["convert", Input("md"), "--to", "html", "--json"], output, errors, stop.Token,
            () => ControlledHost(new([converter])));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { stop.Cancel(); }
        Assert.Equal(1, await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var root = Result(output.ToString());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("converted").GetInt32());
        OnlyEvents(errors.ToString());
        Assert.Equal("cancelled", Assert.Single(Events(errors.ToString()), e => e.GetProperty("event").GetString() == "file").GetProperty("state").GetString());
        var file = root.GetProperty("files")[0];
        Assert.Equal(["source", "state", "outputs", "error"], file.EnumerateObject().Select(p => p.Name));
        Assert.Equal("cancelled", file.GetProperty("state").GetString());
        Assert.Equal("conversion failed", file.GetProperty("error").GetString());
        Assert.Empty(Strings(file.GetProperty("outputs")));
        Assert.False(file.TryGetProperty("errorKey", out _));
        Assert.False(file.TryGetProperty("needsEngines", out _));
    }

    [Fact]
    public async Task Engine_hints_follow_the_failed_operation()
    {
        var mp3 = Input("mp3");
        var catalog = new ConverterCatalog([Package("ffmpeg", false, new ConversionEdge("mp3", "mp3"))]);
        var (_, output, _) = await RunWith(catalog, "convert", mp3, "--to", "mp3", "--json");
        Hints(FileResultOf(output), "ffmpeg");
        AddPresets(new Preset { Id = "same", TargetFormat = "*" });
        (_, output, _) = await RunWith(catalog, "convert", mp3, "--preset", "same", "--json");
        Hints(FileResultOf(output), "ffmpeg");
        (_, output, _) = await RunWith(catalog, "formats", "mp3", "--json");
        Assert.DoesNotContain("mp3", Strings(Result(output).GetProperty("withEngines")));

        var cases = new (string Source, Preset Preset, ConversionEdge Edge, string Package)[] {
            ("mp4", new() { TargetFormat = "png", Media = { SaveEveryFrame = true } }, new("mp4", "png", FrameSequence: true), "ffmpeg"),
            ("jpg", new() { TargetFormat = "mp4", Media = { ExtractMotionPhoto = true } }, new("jpg", "mp4", MotionPhoto: true), "ffmpeg"),
            ("mp4", new() { TargetFormat = "jpg", Media = { LivePhoto = LivePhotoKind.Android } }, new("mp4", "jpg", LivePhoto: true), "ffmpeg"),
            ("png", new() { TargetFormat = "pdf", Pdf = { Ocr = true } }, new("png", "pdf", Ocr: true), "tesseract"),
        };
        foreach (var (source, preset, edge, package) in cases)
        {
            preset.Id = "special-" + Guid.NewGuid().ToString("N");
            AddPresets(preset);
            var specialCatalog = new ConverterCatalog([
                new ControlledConverter("ordinary", true, [new(edge.From, edge.To)]),
                Package(package, false, edge),
            ]);
            (_, output, _) = await RunWith(specialCatalog, "convert", Input(source), "--preset", preset.Id, "--json");
            Hints(FileResultOf(output), package);
            (_, output, _) = await RunWith(specialCatalog, "convert", Input(source), "--to", preset.TargetFormat, "--json");
            Assert.Equal("done", FileResultOf(output).GetProperty("state").GetString());
            Assert.False(FileResultOf(output).TryGetProperty("needsEngines", out _));
        }

        // Intermediate PDF, TIFF and slideshow PNG requests discard final-output-only flags.
        var intermediateCases = new (Preset Preset, string Target)[] {
            (new() { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true, Ocr = true } }, "pdf"),
            (new() { TargetFormat = "tiff", Image = { MultiPageTiff = true }, Media = { SaveEveryFrame = true } }, "tiff"),
            (new() { TargetFormat = "mp4", Media = { Slideshow = true, ExtractMotionPhoto = true } }, "png"),
        };
        foreach (var (preset, target) in intermediateCases)
        {
            preset.Id = "intermediate-" + target;
            AddPresets(preset);
            var edge = new ConversionEdge("jpg", target, Ocr: preset.Pdf.Ocr);
            var package = preset.Pdf.Ocr ? "tesseract" : "pandoc";
            catalog = new([Package(package, false, edge)]);
            var source = Input("jpg");
            var second = Path.Combine(_folder, "second.jpg");
            File.WriteAllText(second, "input");
            (_, output, _) = await RunWith(catalog, "convert", source, second, "--preset", preset.Id, "--json");
            Assert.All(Result(output).GetProperty("files").EnumerateArray(), file => Hints(file, package));
        }

        var imageJoiner = new ControlledConverter("joiner", true, []) { ReadsImages = false };
        var merge = new Preset { Id = "plain-merge", TargetFormat = "jpg", Image = { JoinIntoOne = true }, Pdf = { Ocr = true } };
        AddPresets(merge);
        catalog = new([imageJoiner, Package("pandoc", false, new ConversionEdge("heic", "png")), Package("tesseract", false, new ConversionEdge("heic", "pdf", Ocr: true))]);
        (_, output, _) = await RunWith(catalog, "convert", Input("heic"), "--preset", merge.Id, "--json");
        Hints(FileResultOf(output), "pandoc");
        // The image joiner cannot write the target. An ordinary package route is irrelevant.
        imageJoiner.ReadsImages = true;
        catalog = new([imageJoiner, Package("pandoc", false, new ConversionEdge("heic", "jpg"))]);
        (_, output, _) = await RunWith(catalog, "convert", Input("heic"), "--preset", merge.Id, "--json");
        Hints(FileResultOf(output));
    }

    [Fact]
    public async Task Engine_less_cli_discovery_is_verified()
    {
        var packageIds = EngineDownloads.Packages.SelectMany(p => p.ConverterIds).ToHashSet();
        if (Environment.GetEnvironmentVariable("FILEE_753_VERIFY_NO_ENGINES") == "1")
        {
            await using var host = CliHost.Create(_settings);
            TestContext.Current.TestOutputHelper!.WriteLine($"BaseDirectory={AppContext.BaseDirectory}; Data={host.Store.Directory}; " +
                $"DownloadRoot={EngineEnvironment.DownloadRoot}; SearchSystem={EngineEnvironment.SearchSystem}; " +
                $"OwnCopies={string.Join(",", EngineEnvironment.OwnCopies)}; CWD={Environment.CurrentDirectory}");
            var converters = host.Catalog.All.Where(c => packageIds.Contains(c.Id)).ToArray();
            Assert.Equal(packageIds.Order(), converters.Select(c => c.Id).Order());
            foreach (var converter in converters)
            {
                var status = host.Catalog.StatusOf(converter);
                TestContext.Current.TestOutputHelper.WriteLine($"{converter.Id}: available={status.IsAvailable}, reason={status.ReasonKey}, detail={status.Detail}");
                Assert.False(status.IsAvailable);
            }
        }
        else
        {
            var catalog = new ConverterCatalog(EngineDownloads.Packages.Select(p => Package(p.Id, false)));
            Assert.Equal(packageIds.Order(), catalog.All.Select(c => c.Id).Order());
            Assert.All(catalog.All, c => Assert.False(catalog.StatusOf(c).IsAvailable));
            var (code, output, errors) = await RunWith(catalog, "formats", "md", "--json");
            Assert.Equal(0, code);
            Assert.Equal("", errors);
            Assert.Empty(Strings(Result(output).GetProperty("withEngines")));
        }
    }

    private sealed class ControlledConverter(string id, bool available, IReadOnlyList<ConversionEdge> edges)
        : IConverter, ITextShapingCheck, IImageJoiner
    {
        public string Id => id;
        public string DisplayName => id;
        public int MaxParallelism => 1;
        public bool Available { get; set; } = available;
        public string Reason { get; set; } = "engine.reason.not_installed";
        public IReadOnlyList<ConversionEdge> Edges { get; set; } = edges;
        public bool NeedsShaping { get; init; }
        public bool ReadsImages { get; set; }
        public Func<ConversionStep, CancellationToken, Task<IReadOnlyList<string>>>? Convert { get; set; }
        public Func<IProgress<double>?, CancellationToken, Task>? BeforeConvert { get; init; }
        public EngineStatus GetStatus() => Available ? EngineStatus.Available() : EngineStatus.Unavailable(Reason);
        public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (BeforeConvert is not null)
                await BeforeConvert(progress, cancellationToken);
            return await (Convert?.Invoke(step, cancellationToken) ?? Write(step));
        }
        public static Task<IReadOnlyList<string>> Write(ConversionStep step)
        {
            if (step.Output.Allocate(step.To) is not { } path)
                return Task.FromResult<IReadOnlyList<string>>([]);
            File.WriteAllText(path, "converted");
            return Task.FromResult<IReadOnlyList<string>>([path]);
        }
        public TextScripts ScanScripts(string sourcePath, string sourceFormat) => new(NeedsShaping ? "Arabic" : null, null);
        public bool CanRead(string format) => ReadsImages;
        public bool CanWrite(string format) => false;
        public Task JoinAsync(IReadOnlyList<(string Path, string Format)> inputs, string outputPath, string format, ImageOptions options,
            IProgress<double>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected image join");
    }

    private sealed class UnexpectedMerger : IPdfMerger, ITiffMerger, IMediaJoiner
    {
        public Task MergeAsync(IReadOnlyList<PdfMergeInput> inputs, string outputPath, bool sourceBookmarks, PdfMergePaper? paper,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected PDF merge");
        public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, ImageOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected TIFF merge");
        public Task JoinAsync(IReadOnlyList<string> inputPaths, string outputPath, string format, Preset preset,
            string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected media join");
    }
}
