using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Geometry = Filee.Engines.Pdf.PdfMergePaperFitter;
using Paper = Filee.Core.Conversion.PdfMergePaper;

namespace Filee.Engines.Tests;

[Collection(nameof(PdfMergeStructureCollection))]
public class PdfMergePaperTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Body = "q 1 0 0 rg -10 -10 5 5 re f 0 0 2 2 re f 98 98 2 2 re f 50 50 m 60 60 l S BT /F1 12 Tf (VECTOR) Tj ET Q\n";
    private const string Font = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
    private static readonly Paper Same = new(PdfSheetPaper.SameAsFirstPage, PdfSheetOrientation.Auto);
    private static PdfItem? V(PdfItem? item) => item is PdfReference r ? r.Value : item;
    private static PdfDictionary D(PdfItem? item) => Assert.IsAssignableFrom<PdfDictionary>(V(item));
    private static PdfArray A(PdfItem? item) => Assert.IsType<PdfArray>(V(item));
    private static string S(PdfItem? item) => V(item) switch
    {
        PdfName n => n.Value,
        PdfString s => s.Value,
        _ => throw new InvalidDataException(),
    };
    private static double Num(PdfItem? item) => V(item) switch
    {
        PdfInteger n => n.Value,
        PdfReal n => n.Value,
        _ => throw new InvalidDataException(),
    };
    private static double[] Values(PdfItem? item) => V(item) is PdfRectangle rect
        ? [rect.X1, rect.Y1, rect.X2, rect.Y2] : A(item).Elements.Select(Num).ToArray();
    private static void Near(IEnumerable<double> expected, IEnumerable<double> actual)
    {
        var left = expected.ToArray();
        var right = actual.ToArray();
        Assert.Equal(left.Length, right.Length);
        for (var i = 0; i < left.Length; i++)
            Assert.InRange(right[i], left[i] - .01, left[i] + .01);
    }
    private static string Stream(PdfDictionary dictionary) =>
        Encoding.ASCII.GetString(dictionary.Stream!.UnfilteredValue);
    private static PdfDictionary Annotation(PdfPage page, int index = 0) => D(A(page.Elements["/Annots"]).Elements[index]);
    private static double[] ContentMatrix(PdfPage page) =>
        Stream(D(A(page.Elements["/Contents"]).Elements[0])).Split('\n')[1].Split(' ').Take(6)
            .Select(n => double.Parse(n, CultureInfo.InvariantCulture)).ToArray();
    private static string RawStream(string body, string keys = "") =>
        $"<< /Length {Encoding.ASCII.GetByteCount(body)} {keys} >>\nstream\n{body}endstream";

    private string Raw(params string[] objects)
    {
        var path = Path.Combine(fx.NewFolder(), "input.pdf");
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            text.Append(CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n");
        text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, text.ToString(), Encoding.ASCII);
        return path;
    }

    private string Make(string[] entries, string[]? extra = null, string catalog = "", string parent = "")
    {
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", entries.Select((_, i) => $"{i + 4} 0 R"))}] /Count {entries.Length} {parent} >>",
            RawStream(Body),
        };
        objects.AddRange(entries.Select(entry => $"<< /Type /Page /Parent 2 0 R /Contents 3 0 R {Font} {entry} >>"));
        objects.AddRange(extra ?? []);
        return Raw(objects.ToArray());
    }
    private string Merge(string[] inputs, Paper? paper, Geometry.Limits? limits = null, bool bookmarks = false,
        int appearanceLength = 4096)
    {
        var output = Path.Combine(fx.NewFolder(), "merged.pdf");
        PdfSharpConverter.Merge(inputs.Select(path => new PdfMergeInput(path, Path.GetFileName(path))).ToArray(), output,
            bookmarks, paper, TestContext.Current.CancellationToken, new() { Paper = limits, AppearanceLength = appearanceLength });
        return output;
    }
    private void Refused(string[] inputs, Geometry.Limits limits, string key = "error.merge_paper_limit",
        int appearanceLength = 4096)
    {
        var hashes = inputs.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToArray();
        var output = Path.Combine(fx.NewFolder(), "refused.pdf");
        var error = Assert.Throws<ConversionRefusedException>(() => PdfSharpConverter.Merge(
            inputs.Select(path => new PdfMergeInput(path, "source")).ToArray(), output, false, Same,
            TestContext.Current.CancellationToken, new() { Paper = limits, AppearanceLength = appearanceLength }));
        Assert.Equal(key, error.ErrorKey);
        Assert.False(File.Exists(output));
        for (var i = 0; i < inputs.Length; i++)
            Assert.Equal(hashes[i], SHA256.HashData(File.ReadAllBytes(inputs[i])));
    }
    private static void Page(PdfPage page, double w, double h)
    {
        Near([0, 0, w, h], Values(page.Elements["/MediaBox"]));
        Near([0, 0, w, h], Values(page.Elements["/CropBox"]));
        Assert.Equal(0, page.Rotate);
        Assert.Equal(1, Num(page.Elements["/UserUnit"]));
    }

    [Fact]
    public void E1_Null_matches_the_original_import_and_structure_path()
    {
        var input = Make(["/MediaBox [20 30 220 130] /CropBox [30 40 200 120] /Rotate 90 /Annots [5 0 R]"],
            ["<< /Subtype /Link /Rect [40 50 80 70] /Dest [4 0 R /XYZ 40 null 2] >>"]);
        var baseline = Path.Combine(fx.NewFolder(), "baseline.pdf");
        using (var source = PdfReader.Open(input, PdfDocumentOpenMode.Import))
        using (var output = new PdfDocument())
        {
            var imported = output.AddPage(source.Pages[0]);
            var structure = new PdfMergeStructure(output, new());
            structure.Carry(source, new Dictionary<PdfObjectID, PdfPage> { [source.Pages[0].Reference!.ObjectID] = imported },
                TestContext.Current.CancellationToken);
            structure.Write(TestContext.Current.CancellationToken);
            output.Save(baseline);
        }
        using var expected = PdfReader.Open(baseline, PdfDocumentOpenMode.Import);
        using var actual = PdfReader.Open(Merge([input], null), PdfDocumentOpenMode.Import);
        Assert.Equal(expected.PageCount, actual.PageCount);
        foreach (var key in new[] { "/MediaBox", "/CropBox" })
            Near(Values(expected.Pages[0].Elements[key]), Values(actual.Pages[0].Elements[key]));
        Assert.Equal(expected.Pages[0].Rotate, actual.Pages[0].Rotate);
        Assert.Equal(Stream(D(expected.Pages[0].Elements["/Contents"])), Stream(D(actual.Pages[0].Elements["/Contents"])));
        Near(Values(Annotation(expected.Pages[0]).Elements["/Rect"]), Values(Annotation(actual.Pages[0]).Elements["/Rect"]));
        Assert.IsType<PdfNull>(A(Annotation(actual.Pages[0]).Elements["/Dest"]).Elements[3]);
        Assert.False(actual.Pages[0].Elements.ContainsKey("/UserUnit"));
    }

    [Theory]
    [InlineData(PdfSheetPaper.A4, PdfSheetOrientation.Portrait, 595.28, 841.89)]
    [InlineData(PdfSheetPaper.A4, PdfSheetOrientation.Landscape, 841.89, 595.28)]
    [InlineData(PdfSheetPaper.Letter, PdfSheetOrientation.Portrait, 612, 792)]
    [InlineData(PdfSheetPaper.Letter, PdfSheetOrientation.Landscape, 792, 612)]
    [InlineData(PdfSheetPaper.A3, PdfSheetOrientation.Portrait, 841.89, 1190.55)]
    [InlineData(PdfSheetPaper.A3, PdfSheetOrientation.Landscape, 1190.55, 841.89)]
    public void E2_All_fixed_papers_and_orientations(PdfSheetPaper paper, PdfSheetOrientation orientation, double w, double h)
    {
        var input = Make(["/MediaBox [0 0 842 596]", "/MediaBox [0 0 612 792]", "/MediaBox [0 0 1200 675]"]);
        using var result = PdfReader.Open(Merge([input], new(paper, orientation)), PdfDocumentOpenMode.Import);
        Assert.Equal(3, result.PageCount);
        foreach (var page in result.Pages)
            Page(page, w, h);
    }

    [Theory]
    [InlineData(200, 100, 792, 612)]
    [InlineData(100, 200, 612, 792)]
    [InlineData(100, 100, 612, 792)]
    public void E2_Auto_is_chosen_once_with_portrait_ties(int w, int h, double targetW, double targetH)
    {
        var input = Make([$"/MediaBox [0 0 {w} {h}]", "/MediaBox [0 0 50 400]"]);
        using var result = PdfReader.Open(Merge([input], new(PdfSheetPaper.Letter, PdfSheetOrientation.Auto)), PdfDocumentOpenMode.Import);
        foreach (var page in result.Pages)
            Page(page, targetW, targetH);
    }

    [Theory]
    [InlineData(PdfSheetOrientation.Auto, 300, 100)]
    [InlineData(PdfSheetOrientation.Portrait, 100, 300)]
    [InlineData(PdfSheetOrientation.Landscape, 300, 100)]
    public void E2_First_page_paper_uses_visible_dimensions(PdfSheetOrientation orientation, double w, double h)
    {
        var empty = Raw("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>");
        var input = Make(["/MediaBox [0 0 400 200] /CropBox [20 30 320 130]", "/MediaBox [0 0 600 600]"]);
        using var result = PdfReader.Open(Merge([empty, input], new(PdfSheetPaper.SameAsFirstPage, orientation)), PdfDocumentOpenMode.Import);
        Assert.Equal(2, result.PageCount);
        foreach (var page in result.Pages)
            Page(page, w, h);
    }

    [Theory]
    [InlineData(200, 100, .5, 25)]
    [InlineData(50, 25, 2, 25)]
    [InlineData(100, 100, 1, 0)]
    public void E3_Fit_enlarges_centers_and_clips_without_rewriting_vectors(int w, int h, double scale, double dy)
    {
        var input = Make(["/MediaBox [0 0 100 100]", $"/MediaBox [0 0 {w} {h}]"]);
        using var result = PdfReader.Open(Merge([input], Same), PdfDocumentOpenMode.Import);
        Near([scale, 0, 0, scale, 0, dy], ContentMatrix(result.Pages[1]));
        var contents = A(result.Pages[1].Elements["/Contents"]);
        Assert.Equal(3, contents.Elements.Count);
        Assert.Equal(Body, Stream(D(contents.Elements[1])));
        Assert.Contains($"0 0 {w} {h} re W n", Stream(D(contents.Elements[0])), StringComparison.Ordinal);
        Assert.Equal("\nQ\n", Stream(D(contents.Elements[2])));
        Assert.False(D(result.Pages[1].Elements["/Resources"]).Elements.ContainsKey("/XObject"));
        Assert.Same(V(A(result.Pages[0].Elements["/Contents"]).Elements[1]), V(contents.Elements[1]));
    }

    [Theory]
    [InlineData(0, 1, 0, 0, 1, -30, -50, 80, 160)]
    [InlineData(90, 0, -1, 1, 0, -50, 110, 160, 80)]
    [InlineData(180, -1, 0, 0, -1, 110, 210, 80, 160)]
    [InlineData(270, 0, 1, -1, 0, 210, -30, 160, 80)]
    [InlineData(-90, 0, 1, -1, 0, 210, -30, 160, 80)]
    [InlineData(450, 0, -1, 1, 0, -50, 110, 160, 80)]
    public void E4_Rotation_origin_crop_and_auxiliary_boxes(int rotate, double a, double b, double c, double d,
        double e, double f, double w, double h)
    {
        var input = Make([$"/CropBox [30 50 110 210] /TrimBox [30 50 70 90] /BleedBox [500 500 600 600] /ArtBox [9 9 8 8]"],
            parent: $"/MediaBox [20 30 120 230] /Rotate {rotate}");
        using var result = PdfReader.Open(Merge([input], Same), PdfDocumentOpenMode.Import);
        Page(result.Pages[0], w, h);
        Near([a, b, c, d, e, f], ContentMatrix(result.Pages[0]));
        var trim = rotate switch
        {
            0 => new double[] { 0, 0, 40, 40 },
            90 or 450 => [0, 40, 40, 80],
            180 => [40, 120, 80, 160],
            _ => [120, 0, 160, 40],
        };
        Near(trim, Values(result.Pages[0].Elements["/TrimBox"]));
        Assert.False(result.Pages[0].Elements.ContainsKey("/BleedBox"));
        Assert.False(result.Pages[0].Elements.ContainsKey("/ArtBox"));
    }

    [Fact]
    public void E4_First_import_UserUnit_is_read_before_AddPage_discards_it()
    {
        var input = Make(["/MediaBox [0 0 300 400] /UserUnit 2", "/MediaBox [0 0 100 100]"]);
        using var result = PdfReader.Open(Merge([input], Same), PdfDocumentOpenMode.Import);
        foreach (var page in result.Pages)
            Page(page, 600, 800);
        Near([2, 0, 0, 2, 0, 0], ContentMatrix(result.Pages[0]));
        var m = ContentMatrix(result.Pages[0]);
        Near([200, 400], [m[0] * 100 + m[2] * 200 + m[4], m[1] * 100 + m[3] * 200 + m[5]]);
    }

    [Theory]
    [InlineData("[20 20 10 10]")]
    [InlineData("[20 20 20 20]")]
    [InlineData("[200 200 300 300]")]
    public void E4_Empty_inverted_and_disjoint_crop_fall_back_to_media(string crop)
    {
        using var result = PdfReader.Open(Merge([Make([$"/MediaBox [0 0 100 100] /CropBox {crop}"])], Same), PdfDocumentOpenMode.Import);
        Near([1, 0, 0, 1, 0, 0], ContentMatrix(result.Pages[0]));
    }

    [Theory]
    [InlineData("/MediaBox [0 0 0 100]")]
    [InlineData("/MediaBox [100 0 0 100]")]
    public void E5_Invalid_geometry_refuses_without_output_or_input_changes(string entry)
    {
        var input = Make([entry]);
        var hash = SHA256.HashData(File.ReadAllBytes(input));
        var output = Path.Combine(fx.NewFolder(), "invalid.pdf");
        var error = Assert.Throws<ConversionRefusedException>(() => PdfSharpConverter.Merge([new(input, "input.pdf")], output,
            false, Same, TestContext.Current.CancellationToken));
        Assert.Equal("error.merge_paper_geometry", error.ErrorKey);
        Assert.False(File.Exists(output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(input)));
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void E5_Nonfinite_overflow_and_underflow_are_geometry_errors(double unit)
    {
        using var source = new PdfDocument();
        var page = source.AddPage();
        page.Elements["/MediaBox"] = new PdfArray(source);
        var box = A(page.Elements["/MediaBox"]);
        foreach (var n in new double[] { 0, 0, 300, 400 })
            box.Elements.Add(new PdfReal(n));
        page.Elements["/UserUnit"] = new PdfReal(unit);
        using var output = new PdfDocument();
        var normalizer = new Geometry(output, new(), 4096);
        Assert.Throws<ConversionRefusedException>(() =>
        {
            var geometry = normalizer.Snapshot(page);
            Geometry.Fit(geometry, (double.Epsilon, double.Epsilon));
        });
    }

    [Theory]
    [InlineData("/XYZ 20 40 1.25", "/XYZ", "45 90 1.25")]
    [InlineData("/XYZ null 40 2", "/XYZ", "45 null 2")]
    [InlineData("/XYZ 20 null 0", "/XYZ", "null 90 0")]
    [InlineData("/XYZ", "/XYZ", "null null null")]
    [InlineData("/FitR 20 40 60 80", "/FitR", "45 70 65 90")]
    [InlineData("/FitH 40", "/FitV", "45")]
    [InlineData("/FitBH null", "/FitBV", "null")]
    [InlineData("/FitV 20", "/FitH", "90")]
    [InlineData("/FitBV", "/FitBH", "null")]
    [InlineData("/Fit", "/Fit", "")]
    [InlineData("/FitB", "/FitB", "")]
    public void E6_Direct_action_named_and_outline_use_target_transform(string view, string kind, string values)
    {
        var dest = $"[5 0 R {view}]";
        var input = Make(["/MediaBox [0 0 100 100] /Annots [6 0 R 7 0 R 9 0 R]", "/MediaBox [0 0 200 100] /Rotate 90"],
            [
                $"<< /Subtype /Link /Rect [10 10 30 30] /Dest {dest} >>",
                "<< /Subtype /Link /Rect [10 10 30 30] /A 8 0 R >>",
                $"<< /S /GoTo /D {dest} >>",
                "<< /Subtype /Link /Rect [10 10 30 30] /Dest (target) >>",
                "<< /Type /Outlines /First 11 0 R /Last 11 0 R /Count 1 >>",
                $"<< /Title (Original) /Parent 10 0 R /Dest {dest} >>",
            ], $"/Names << /Dests << /Names [(target) {dest}] >> >> /Outlines 10 0 R");
        using var result = PdfReader.Open(Merge([input], Same), PdfDocumentOpenMode.Import);
        var direct = A(Annotation(result.Pages[0], 0).Elements["/Dest"]);
        var action = A(D(Annotation(result.Pages[0], 1).Elements["/A"]).Elements["/D"]);
        var names = A(D(D(result.Internals.Catalog.Elements["/Names"]).Elements["/Dests"]).Elements["/Names"]);
        var named = A(names.Elements[1]);
        var outline = A(D(D(result.Internals.Catalog.Elements["/Outlines"]).Elements["/First"]).Elements["/Dest"]);
        foreach (var array in new[] { direct, action, named, outline })
        {
            Assert.Same(result.Pages[1], V(array.Elements[0]));
            Assert.Equal(kind, S(array.Elements[1]));
            var expected = values.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(expected.Length + 2, array.Elements.Count);
            for (var i = 0; i < expected.Length; i++)
                if (expected[i] == "null")
                    Assert.IsType<PdfNull>(array.Elements[i + 2]);
                else
                    Near([double.Parse(expected[i], CultureInfo.InvariantCulture)], [Num(array.Elements[i + 2])]);
        }
        Near([10, 10, 30, 30], Values(Annotation(result.Pages[0]).Elements["/Rect"]));
    }

    [Fact]
    public void E6_Shared_destination_arrays_are_transformed_once_per_mapping_without_mutating_the_source()
    {
        var input = Make(["/MediaBox [0 0 100 100] /Annots [6 0 R 7 0 R]", "/MediaBox [0 0 200 100] /Rotate 90"],
            [
                "<< /Subtype /Link /Rect [10 10 30 30] /Dest 8 0 R >>",
                "<< /Subtype /Link /Rect [10 10 30 30] /Dest 8 0 R >>",
                "[5 0 R /XYZ 20 40 1.25]",
            ], "/Names << /Dests << /Names [(target) 8 0 R] >> >>");
        var hash = SHA256.HashData(File.ReadAllBytes(input));
        using var result = PdfReader.Open(Merge([input], Same), PdfDocumentOpenMode.Import);
        foreach (var i in new[] { 0, 1 })
        {
            var dest = A(Annotation(result.Pages[0], i).Elements["/Dest"]);
            Near([45, 90, 1.25], dest.Elements.Skip(2).Select(Num));
            Assert.Same(result.Pages[1], V(dest.Elements[0]));
        }
        var names = A(D(D(result.Internals.Catalog.Elements["/Names"]).Elements["/Dests"]).Elements["/Names"]);
        Near([45, 90, 1.25], A(names.Elements[1]).Elements.Skip(2).Select(Num));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(input)));
    }

    [Theory]
    [InlineData("/QuadPoints [80 20 120 20 80 60 120 60]", "/QuadPoints", "20 20 20 -20 60 20 60 -20")]
    [InlineData("/Vertices [80 20 120 60]", "/Vertices", "20 20 60 -20")]
    [InlineData("/L [80 20 120 60]", "/L", "20 20 60 -20")]
    [InlineData("/InkList [[80 20 120 60]]", "/InkList", "20 20 60 -20")]
    public void E7_Common_coordinates_and_AP_wrapper_preserve_full_rect(string entry, string key, string expected)
    {
        const string appearance = "q 1 1 0 rg 0 0 10 20 re f Q\n";
        var input = Make(["/MediaBox [0 0 100 100] /Rotate 90 /Annots [5 0 R]"],
            [
                $"<< /Subtype /FreeText /Rect [80 20 120 60] /Q 1 {entry} /AP << /N << /On 6 0 R /Off 6 0 R >> /R 6 0 R /D 6 0 R >> /AS /On >>",
                RawStream(appearance, "/Type /XObject /Subtype /Form /BBox [0 0 10 20] /Matrix [2 0 0 1 5 7] /Resources << /ProcSet [/PDF] >>"),
            ]);
        using var result = PdfReader.Open(Merge([input], Same), PdfDocumentOpenMode.Import);
        var annotation = Annotation(result.Pages[0]);
        Near([20, -20, 60, 20], Values(annotation.Elements["/Rect"]));
        Assert.Equal(1, Num(annotation.Elements["/Q"]));
        Assert.Same(result.Pages[0], V(annotation.Elements["/P"]));
        var coordinates = key == "/InkList" ? A(annotation.Elements[key]).Elements[0] : annotation.Elements[key];
        Near(expected.Split(' ').Select(n => double.Parse(n, CultureInfo.InvariantCulture)), Values(coordinates));
        var ap = D(annotation.Elements["/AP"]);
        foreach (var wrapper in new[] { D(D(ap.Elements["/N"]).Elements["/On"]), D(ap.Elements["/R"]), D(ap.Elements["/D"]) })
        {
            Near([20, -20, 60, 20], Values(wrapper.Elements["/BBox"]));
            Near([1, 0, 0, 1, 0, 0], Values(wrapper.Elements["/Matrix"]));
            Near([0, -2, 2, 0, 6, 30], Stream(wrapper).Split('\n')[1].Split(' ').Take(6)
                .Select(n => double.Parse(n, CultureInfo.InvariantCulture)));
            var original = D(D(D(wrapper.Elements["/Resources"]).Elements["/XObject"]).Elements["/Original"]);
            Assert.Equal(appearance, Stream(original));
            Near([2, 0, 0, 1, 5, 7], Values(original.Elements["/Matrix"]));
            Assert.Equal("/PDF", S(A(D(original.Elements["/Resources"]).Elements["/ProcSet"]).Elements[0]));
        }
        Assert.Equal("/On", S(annotation.Elements["/AS"]));
    }

    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(false, 2, 12)]
    [InlineData(true, 1, 12)]
    [InlineData(true, 2, 0)]
    public void E8_Widget_layout_DA_inheritance_and_editability_survive(bool appearance, int q, int size)
    {
        var ap = appearance ? "/AP << /N 8 0 R >>" : "";
        var input = Make(["/MediaBox [0 0 100 100] /Annots [6 0 R]", "/MediaBox [0 0 200 100] /Rotate 90 /Annots [7 0 R]"],
            [
                $"<< /Subtype /Widget /FT /Tx /T (field) /V (one) /Rect [80 20 120 60] /Q {q} /Ff 4096 /DA (/F1 {size} Tf 0 g) {ap} >>",
                $"<< /Subtype /Widget /FT /Tx /T (other) /V (two) /Rect [80 20 120 60] /Q {q} /Ff 4096 /MK << /R 90 >> {ap} >>",
                RawStream("0 0 10 20 re S\n", "/Type /XObject /Subtype /Form /BBox [0 0 10 20] /Resources << >>"),
            ], $"/AcroForm << /Fields [6 0 R 7 0 R] /DA (/F1 {size} Tf 0 g) /NeedAppearances true /DR << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
        var path = Merge([input, input], Same);
        using (var result = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var first = Annotation(result.Pages[0]);
            var rotated = Annotation(result.Pages[1]);
            Near([80, 20, 120, 60], Values(first.Elements["/Rect"]));
            Near([35, 40, 55, 60], Values(rotated.Elements["/Rect"]));
            Assert.Equal(q, Num(first.Elements["/Q"]));
            Assert.Equal(q, Num(rotated.Elements["/Q"]));
            Assert.Equal(4096, Num(first.Elements["/Ff"]));
            Assert.Equal(0, Num(D(rotated.Elements["/MK"]).Elements["/R"]));
            Assert.Equal($"/F1 {size} Tf 0 g", S(first.Elements["/DA"]));
            Assert.Equal($"/F1 {size / 2} Tf 0 g", S(rotated.Elements["/DA"]));
            if (appearance)
            {
                var firstAp = D(D(first.Elements["/AP"]).Elements["/N"]);
                var rotatedAp = D(D(rotated.Elements["/AP"]).Elements["/N"]);
                Assert.NotSame(firstAp, rotatedAp);
                Near([80, 20, 120, 60], Values(firstAp.Elements["/BBox"]));
                Near([35, 40, 55, 60], Values(rotatedAp.Elements["/BBox"]));
                PdfDictionary Original(PdfDictionary wrapper) =>
                    D(D(D(wrapper.Elements["/Resources"]).Elements["/XObject"]).Elements["/Original"]);
                Assert.Same(Original(firstAp), Original(rotatedAp));
                Assert.Equal("0 0 10 20 re S\n", Stream(Original(firstAp)));
            }
            Assert.True(((PdfBoolean)V(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/NeedAppearances"])!).Value);
            first.Elements["/V"] = new PdfString("edited");
            result.Save(path);
        }
        using var reopened = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal("edited", S(Annotation(reopened.Pages[0]).Elements["/V"]));
        Assert.Equal("one", S(Annotation(reopened.Pages[2]).Elements["/V"]));
        Near([80, 20, 120, 60], Values(Annotation(reopened.Pages[0]).Elements["/Rect"]));
        var fields = A(D(reopened.Internals.Catalog.Elements["/AcroForm"]).Elements["/Fields"]);
        Assert.Contains(fields.Elements, item => ReferenceEquals(V(item), Annotation(reopened.Pages[0])));
    }

    [Fact]
    public void E8_DA_lexer_scales_only_real_Tf_operands()
    {
        const string da = "% /Fake 99 Tf\n(Fake \\( /F1 99 Tf) Tj [/F1 99 Tf] /F#31 12 Tf 0 g /F1 0 Tf";
        var result = PdfMergeStructure.ScaleAppearance(new PdfString(da), 2).Value;
        Assert.Equal("% /Fake 99 Tf\n(Fake \\( /F1 99 Tf) Tj [/F1 99 Tf] /F#31 24 Tf 0 g /F1 0 Tf", result);
    }

    [Fact]
    public void E9_Shared_annotations_are_per_occurrence_and_cross_page_widgets_are_unchanged()
    {
        var input = Make(["/MediaBox [0 0 100 100] /Annots [6 0 R 6 0 R 7 0 R]",
            "/MediaBox [0 0 200 200] /Annots [6 0 R 7 0 R]"],
            [
                "<< /Subtype /Link /Rect [20 20 60 60] /Vertices [20 20 60 60] /RD [1 2 3 4] /CL [1 2 3 4] /Border [1 2 3] /F 24 /Private [9 8] >>",
                "<< /Subtype /Widget /FT /Tx /T (shared) /V (value) /Rect [20 20 60 60] /DA (/F1 12 Tf) /MK << /R 90 >> >>",
            ], "/AcroForm << /Fields [7 0 R] >>");
        using var result = PdfReader.Open(Merge([input], Same), PdfDocumentOpenMode.Import);
        var first = Annotation(result.Pages[0]);
        var second = Annotation(result.Pages[1]);
        Assert.Same(first, Annotation(result.Pages[0], 1));
        Assert.NotSame(first, second);
        Near([20, 20, 60, 60], Values(first.Elements["/Rect"]));
        Near([10, 10, 30, 30], Values(second.Elements["/Rect"]));
        Near([10, 10, 30, 30], Values(second.Elements["/Vertices"]));
        foreach (var page in result.Pages)
        {
            var widget = Annotation(page, page == result.Pages[0] ? 2 : 1);
            Near([20, 20, 60, 60], Values(widget.Elements["/Rect"]));
            Assert.Equal("/F1 12 Tf", S(widget.Elements["/DA"]));
            Assert.Equal(90, Num(D(widget.Elements["/MK"]).Elements["/R"]));
        }
        Near([1, 2, 3, 4], Values(second.Elements["/RD"]));
        Near([1, 2, 3, 4], Values(second.Elements["/CL"]));
        Near([1, 2, 3], Values(second.Elements["/Border"]));
        Near([9, 8], Values(second.Elements["/Private"]));
        Assert.Equal(24, Num(second.Elements["/F"]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task E10_Source_bookmarks_and_finishing_keep_the_supported_structure(bool bookmarks, bool normalize)
    {
        var input = Make(["/MediaBox [0 0 200 100] /Annots [5 0 R]"],
            [
                "<< /Subtype /Link /Rect [20 20 60 60] /Dest [4 0 R /XYZ 20 40 2] >>",
                "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
                "<< /Title (Original) /Dest [4 0 R /Fit] /Parent 6 0 R /F 3 /C [1 0 0] >>",
            ], "/Outlines 6 0 R");
        var path = Merge([input], normalize ? new(PdfSheetPaper.Letter, PdfSheetOrientation.Portrait) : null, bookmarks: bookmarks);
        await new PdfSharpConverter().FinishAsync(path, new() { Watermark = "QA", PageNumbers = true, Password = "paper-qa" },
            TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, "paper-qa", PdfDocumentOpenMode.Import);
        if (normalize)
            Page(result.Pages[0], 612, 792);
        var root = D(result.Internals.Catalog.Elements["/Outlines"]);
        var first = D(root.Elements["/First"]);
        if (bookmarks)
        {
            Assert.Equal("input.pdf", S(first.Elements["/Title"]));
            Assert.Equal("/Fit", S(A(first.Elements["/Dest"]).Elements[1]));
            first = D(first.Elements["/First"]);
        }
        Assert.Equal("Original", S(first.Elements["/Title"]));
        Assert.Equal(3, Num(first.Elements["/F"]));
        Near([1, 0, 0], Values(first.Elements["/C"]));
        var dest = A(Annotation(result.Pages[0]).Elements["/Dest"]);
        Near(normalize ? [61.2, 365.4, 2] : [20, 40, 2], dest.Elements.Skip(2).Select(Num));
        Near(normalize ? [61.2, 304.2, 183.6, 426.6] : [20, 20, 60, 60], Values(Annotation(result.Pages[0]).Elements["/Rect"]));
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    public void E11_Annotation_numbers_are_reserved_atomically_and_reset_per_source(int limit, bool changed)
    {
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R 6 0 R]"],
            [
                "<< /Subtype /FreeText /Rect [20 20 60 60] /Vertices [20 20 60 60] /DA (/F1 12 Tf) >>",
                "<< /Subtype /Link /Rect [20 20 60 60] >>",
            ]);
        var first = Make(["/MediaBox [0 0 100 100]"]);
        if (!changed)
        {
            Refused([first, input], new() { AnnotationNumbers = limit, SourceNumbers = 12 });
            return;
        }
        using var result = PdfReader.Open(Merge([first, input, input], Same,
            new() { AnnotationNumbers = limit, SourceNumbers = 12, Objects = 6, Items = 24, DaBytes = 8 }), PdfDocumentOpenMode.Import);
        foreach (var page in result.Pages.Cast<PdfPage>().Skip(1))
        {
            Near(changed ? [10, 10, 30, 30] : [20, 20, 60, 60], Values(Annotation(page).Elements["/Rect"]));
            Near(changed ? [10, 10, 30, 30] : [20, 20, 60, 60], Values(Annotation(page).Elements["/Vertices"]));
            Assert.Equal("/F1 6 Tf", S(Annotation(page).Elements["/DA"]));
            Near([10, 10, 30, 30], Values(Annotation(page, 1).Elements["/Rect"]));
        }
    }

    [Theory]
    [InlineData("slots")]
    [InlineData("numbers")]
    [InlineData("strokes")]
    [InlineData("states")]
    [InlineData("objects")]
    [InlineData("items")]
    [InlineData("streams")]
    [InlineData("da")]
    public void E11_Optional_budget_failure_refuses_the_merge(string budget)
    {
        var limits = budget switch
        {
            "slots" => new Geometry.Limits { AnnotationSlots = 0 },
            "numbers" => new() { SourceNumbers = 3 },
            "strokes" => new() { InkStrokes = 0 },
            "states" => new() { AppearanceStates = 0 },
            "objects" => new() { Objects = 1 },
            "items" => new() { Items = 1 },
            "streams" => new() { StreamBytes = 1 },
            _ => new() { DaLength = 1 },
        };
        var first = Make(["/MediaBox [0 0 100 100]"]);
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R]"],
            [
                "<< /Subtype /Widget /FT /Tx /T (field) /V (value) /Rect [20 20 60 60] /InkList [[20 20 60 60]] /AP << /N 6 0 R >> /DA (/F1 12 Tf) >>",
                RawStream("0 0 1 1 re S\n", "/Subtype /Form /BBox [0 0 1 1] /Resources << >>"),
            ], "/AcroForm << /Fields [5 0 R] >>");
        Refused([first, input], limits);
    }

    public static TheoryData<string, int> BudgetBoundaries => new()
    {
        { "slots", 2 }, { "sourceNumbers", 8 }, { "strokes", 2 }, { "states", 2 },
        { "objects", 5 }, { "items", 18 }, { "daLength", 9 }, { "daBytes", 8 },
        { "streamBytes", Encoding.ASCII.GetByteCount("q\n0.5 0 0 0.5 0 0 cm\n/Original Do\nQ\n") },
    };

    [Theory]
    [MemberData(nameof(BudgetBoundaries))]
    public void E11_Each_budget_refuses_below_and_fits_at_or_above_boundary(string budget, int boundary)
    {
        foreach (var offset in new[] { -1, 0, 1 })
        {
            var limit = boundary + offset;
            var limits = budget switch
            {
                "slots" => new Geometry.Limits { AnnotationSlots = limit },
                "sourceNumbers" => new() { SourceNumbers = limit },
                "strokes" => new() { InkStrokes = limit },
                "states" => new() { AppearanceStates = limit },
                "objects" => new() { Objects = limit },
                "items" => new() { Items = limit },
                "daLength" => new() { DaLength = limit },
                "daBytes" => new() { DaBytes = limit },
                _ => new() { StreamBytes = limit },
            };
            var first = Make(["/MediaBox [0 0 100 100]"]);
            var widget = budget is "daLength" or "daBytes";
            var extra = budget switch
            {
                "strokes" => "/InkList [[20 20] [60 60]]",
                "states" => "/AP << /N << /On 7 0 R /Off 7 0 R >> >>",
                "streamBytes" => "/AP << /N 7 0 R >>",
                "daLength" or "daBytes" => "/FT /Tx /T (field) /DA (/F1 12 Tf)",
                _ => "",
            };
            var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R 6 0 R]"],
                [
                    $"<< /Subtype /{(widget ? "Widget" : "Link")} /Rect [20 20 60 60] {extra} >>",
                    "<< /Subtype /Link /Rect [20 20 60 60] >>",
                    RawStream("0 0 1 1 re S\n", "/Subtype /Form /BBox [0 0 1 1] /Resources << >>"),
                ], widget ? "/AcroForm << /Fields [5 0 R] >>" : "");
            if (offset < 0)
            {
                Refused([first, input], limits);
                continue;
            }
            using var result = PdfReader.Open(Merge([first, input], Same, limits), PdfDocumentOpenMode.Import);
            foreach (var annotation in A(result.Pages[1].Elements["/Annots"]).Elements.Select(D))
                Near([10, 10, 30, 30], Values(annotation.Elements["/Rect"]));
        }
    }

    [Fact]
    public void E11_Slot_overflow_refuses_before_partial_owner_collection()
    {
        var input = Make(["/MediaBox [0 0 100 100] /Annots [6 0 R]", "/MediaBox [0 0 200 200] /Annots [6 0 R 7 0 R]"],
            [
                "<< /Subtype /Widget /FT /Tx /T (field) /Rect [20 20 60 60] /DA (/F1 12 Tf) >>",
                "<< /Subtype /Link /Rect [20 20 60 60] >>",
            ], "/AcroForm << /Fields [6 0 R] >>");
        Refused([input], new() { AnnotationSlots = 2 });
    }

    [Theory]
    [InlineData("/Vertices [20 20 60]")]
    [InlineData("/InkList [[20 20] [[60 60]]]")]
    [InlineData("/AP << /N << /Recursive << /Bad 5 0 R >> >> >>")]
    public void E11_Malformed_optional_geometry_does_not_partially_transform_or_block_neighbors(string entry)
    {
        var first = Make(["/MediaBox [0 0 100 100]"]);
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R 6 0 R]"],
            [$"<< /Subtype /Polygon /Rect [20 20 60 60] {entry} >>", "<< /Subtype /Link /Rect [20 20 60 60] >>"]);
        using var result = PdfReader.Open(Merge([first, input], Same), PdfDocumentOpenMode.Import);
        Near([20, 20, 60, 60], Values(Annotation(result.Pages[1]).Elements["/Rect"]));
        Near([10, 10, 30, 30], Values(Annotation(result.Pages[1], 1).Elements["/Rect"]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void E12_Contents_limits_are_checked_before_wrapping(int limit, bool success)
    {
        var input = Make(["/MediaBox [0 0 100 100]"]);
        if (success)
        {
            using var result = PdfReader.Open(Merge([input], Same, new() { ContentStreams = limit }), PdfDocumentOpenMode.Import);
            Page(result.Pages[0], 100, 100);
        }
        else
        {
            var output = Path.Combine(fx.NewFolder(), "limit.pdf");
            var error = Assert.Throws<ConversionRefusedException>(() => PdfSharpConverter.Merge([new(input, "input")], output,
                false, Same, TestContext.Current.CancellationToken, new() { Paper = new() { ContentStreams = limit } }));
            Assert.Equal("error.merge_paper_limit", error.ErrorKey);
            Assert.False(File.Exists(output));
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void E12_Parent_depth_boundaries(int limit, bool success)
    {
        using var input = new PdfDocument();
        var page = input.AddPage();
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(100, 100));
        var normalizer = new Geometry(input, new() { ParentDepth = limit }, 4096);
        if (success)
            Assert.NotNull(normalizer.Snapshot(page));
        else
            Assert.Equal("error.merge_paper_limit", Assert.Throws<ConversionRefusedException>(() => normalizer.Snapshot(page)).ErrorKey);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void E12_Flat_Contents_array_counts_each_shared_stream_slot(int limit, bool success)
    {
        var source = Raw("<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>", RawStream(Body),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents [3 0 R 3 0 R] >>");
        if (success)
        {
            using var result = PdfReader.Open(Merge([source], Same, new() { ContentStreams = limit }), PdfDocumentOpenMode.Import);
            var contents = A(result.Pages[0].Elements["/Contents"]);
            Assert.Equal(4, contents.Elements.Count);
            Assert.Same(V(contents.Elements[1]), V(contents.Elements[2]));
            Assert.Equal(Body, Stream(D(contents.Elements[1])));
        }
        else
        {
            var output = Path.Combine(fx.NewFolder(), "limit.pdf");
            Assert.Equal("error.merge_paper_limit", Assert.Throws<ConversionRefusedException>(() => PdfSharpConverter.Merge(
                [new(source, "source")], output, false, Same, TestContext.Current.CancellationToken,
                new() { Paper = new() { ContentStreams = limit } })).ErrorKey);
            Assert.False(File.Exists(output));
        }
    }

    [Fact]
    public void E12_Parent_cycles_and_nested_contents_fail_finitely()
    {
        using (var input = new PdfDocument())
        {
            var page = input.AddPage();
            page.Elements["/Parent"] = page.Reference!;
            foreach (var limits in new[] { new Geometry.Limits(), new Geometry.Limits { ParentDepth = 1 } })
            {
                var normalizer = new Geometry(input, limits, 4096);
                var error = Assert.Throws<ConversionRefusedException>(() => normalizer.Snapshot(page));
                Assert.Equal("error.merge_paper_geometry", error.ErrorKey);
            }
        }
        var source = Raw("<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>", RawStream(Body),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents [[3 0 R]] >>");
        var output = Path.Combine(fx.NewFolder(), "nested.pdf");
        Assert.Equal("error.merge_paper_geometry", Assert.Throws<ConversionRefusedException>(() => PdfSharpConverter.Merge(
            [new(source, "input")], output, false, Same, TestContext.Current.CancellationToken)).ErrorKey);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void E13_Page_cancellation_occurs_after_the_hook_and_before_the_next_page()
    {
        var input = Make(Enumerable.Repeat("/MediaBox [0 0 100 100]", 3).ToArray());
        var hash = SHA256.HashData(File.ReadAllBytes(input));
        var output = Path.Combine(fx.NewFolder(), "cancel.pdf");
        using var cts = new CancellationTokenSource();
        var trace = new List<int>();
        var clock = Stopwatch.StartNew();
        TimeSpan? cancelled = null;
        var limits = new PdfMergeStructure.Limits
        {
            Paper = new()
            {
                BeforePage = i =>
                {
                    trace.Add(i);
                    if (i == 2)
                    {
                        cancelled = clock.Elapsed;
                        cts.Cancel();
                    }
                },
            },
        };
        Assert.Throws<OperationCanceledException>(() => PdfSharpConverter.Merge([new(input, "input")], output, false, Same, cts.Token, limits));
        Assert.Equal([1, 2], trace);
        Assert.NotNull(cancelled);
        Assert.True(clock.Elapsed - cancelled.Value < TimeSpan.FromSeconds(1));
        Assert.False(File.Exists(output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(input)));
    }

    [Theory]
    [InlineData("/Rotate 45")]
    [InlineData("/Rotate 90.5")]
    [InlineData("/Rotate (bad)")]
    [InlineData("/UserUnit 0")]
    [InlineData("/UserUnit -1")]
    [InlineData("/UserUnit (bad)")]
    public void E5_Viewer_tolerated_rotation_and_unit_values_use_defaults(string entry)
    {
        var input = Make([$"/MediaBox [0 0 200 100] {entry}"]);
        var hash = SHA256.HashData(File.ReadAllBytes(input));
        using var result = PdfReader.Open(Merge([input], Same), PdfDocumentOpenMode.Import);
        Page(result.Pages[0], 200, 100);
        Near([1, 0, 0, 1, 0, 0], ContentMatrix(result.Pages[0]));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(input)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void E5_Nonfinite_rotation_and_unit_default_in_snapshot(double value)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(200, 100));
        page.Elements["/Rotate"] = new PdfReal(value);
        page.Elements["/UserUnit"] = new PdfReal(value);
        var geometry = new Geometry(document, new(), 4096).Snapshot(page);
        Assert.Equal(0, geometry.Rotate);
        Assert.Equal(1, geometry.Unit);
        Assert.Equal(200, geometry.Width);
        Assert.Equal(100, geometry.Height);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("<< >>")]
    [InlineData("[3 0 R 7]")]
    [InlineData("[3 0 R null]")]
    [InlineData("[[3 0 R]]")]
    public void E12_Non_stream_contents_is_geometry_error(string contents)
    {
        var input = Raw("<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>", RawStream(Body),
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents {contents} >>");
        Refused([input], new(), "error.merge_paper_geometry");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void E11_Default_objects_budget_fits_large_link_and_checkbox_documents(bool widgets)
    {
        var pages = widgets ? 40 : 1000;
        var perPage = widgets ? 15 : 6;
        var firstAnnotation = pages + 4;
        var streamId = firstAnnotation + pages * perPage;
        var entries = Enumerable.Range(0, pages).Select(p => "/MediaBox [0 0 200 200] /Annots [" +
            string.Join(" ", Enumerable.Range(0, perPage).Select(i => $"{firstAnnotation + p * perPage + i} 0 R")) + "]").ToArray();
        var annotations = Enumerable.Range(0, pages * perPage).Select(i => widgets
            ? $"<< /Subtype /Widget /FT /Btn /T (box{i}) /Rect [20 20 60 60] /MK << /R 0 >> /AP << /N << /On {streamId} 0 R /Off {streamId} 0 R >> /D << /On {streamId} 0 R /Off {streamId} 0 R >> >> >>"
            : "<< /Subtype /Link /Rect [20 20 60 60] >>").ToList();
        if (widgets)
            annotations.Add(RawStream("0 0 1 1 re S\n", "/Subtype /Form /BBox [0 0 1 1]"));
        var catalog = widgets ? "/AcroForm << /Fields [" +
            string.Join(" ", Enumerable.Range(0, pages * perPage).Select(i => $"{firstAnnotation + i} 0 R")) + "] >>" : "";
        var input = Make(entries, annotations.ToArray(), catalog);
        var clock = Stopwatch.StartNew();
        var path = Merge([Make(["/MediaBox [0 0 100 100]"]), input], Same);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(pages + 1, result.PageCount);
        foreach (var page in result.Pages.Cast<PdfPage>().Skip(1))
            for (var i = 0; i < perPage; i++)
            {
                var annotation = Annotation(page, i);
                Near([10, 10, 30, 30], Values(annotation.Elements["/Rect"]));
                if (!widgets)
                    continue;
                var ap = D(annotation.Elements["/AP"]);
                foreach (var mode in new[] { "/N", "/D" })
                    foreach (var state in new[] { "/On", "/Off" })
                        Near([10, 10, 30, 30], Values(D(D(ap.Elements[mode]).Elements[state]).Elements["/BBox"]));
            }
        if (widgets)
        {
            var fields = A(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/Fields"]);
            Assert.Contains(fields.Elements, item => ReferenceEquals(V(item), Annotation(result.Pages[^1], perPage - 1)));
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"paper-large widgets={widgets} input={new FileInfo(input).Length} output={new FileInfo(path).Length} ms={clock.ElapsedMilliseconds} rss={Process.GetCurrentProcess().PeakWorkingSet64}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void E9_Text_popup_and_reply_keep_in_annots_identity_and_fitted_coordinates(bool crossPage)
    {
        CheckPopup(crossPage, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void E9_Cross_page_popup_parent_and_reply_keep_identity(bool reverse)
    {
        CheckPopup(true, reverse);
    }

    private void CheckPopup(bool crossPage, bool reverse)
    {
        var first = crossPage ? 6 : 5;
        var rootPage = reverse ? 1 : 0;
        var popupPage = crossPage ? 1 - rootPage : 0;
        var entries = crossPage ? new[] { "/MediaBox [0 0 100 100]", "/MediaBox [0 0 200 200]" }
            : ["/MediaBox [0 0 200 200]"];
        if (crossPage)
            entries[rootPage] += $" /Annots [{first} 0 R]";
        entries[popupPage] += crossPage ? $" /Annots [{first + 1} 0 R {first + 2} 0 R]"
            : $" /Annots [{first} 0 R {first + 1} 0 R {first + 2} 0 R]";
        var input = Make(entries,
            [
                $"<< /Subtype /Text /NM (root) /Rect [20 20 60 60] /Popup {first + 1} 0 R >>",
                $"<< /Subtype /Popup /NM (popup) /Rect [40 40 80 80] /Parent {first} 0 R >>",
                $"<< /Subtype /Text /NM (reply) /Rect [60 60 100 100] /IRT {first} 0 R >>",
            ]);
        using var result = PdfReader.Open(Merge([Make(["/MediaBox [0 0 100 100]"]), input], Same), PdfDocumentOpenMode.Import);
        var root = Annotation(result.Pages[rootPage + 1]);
        var popup = Annotation(result.Pages[popupPage + 1], crossPage ? 0 : 1);
        var reply = Annotation(result.Pages[popupPage + 1], crossPage ? 1 : 2);
        Assert.Same(popup, V(root.Elements["/Popup"]));
        Assert.Same(root, V(popup.Elements["/Parent"]));
        Assert.Same(root, V(reply.Elements["/IRT"]));
        var rootScale = crossPage && rootPage == 0 ? 1d : .5;
        var popupScale = crossPage && popupPage == 0 ? 1d : .5;
        Near(new double[] { 20, 20, 60, 60 }.Select(n => n * rootScale), Values(root.Elements["/Rect"]));
        Near(new double[] { 40, 40, 80, 80 }.Select(n => n * popupScale), Values(popup.Elements["/Rect"]));
        Near(new double[] { 60, 60, 100, 100 }.Select(n => n * popupScale), Values(reply.Elements["/Rect"]));
        Assert.Same(result.Pages[rootPage + 1], V(root.Elements["/P"]));
        Assert.Same(result.Pages[popupPage + 1], V(popup.Elements["/P"]));
        foreach (var name in new[] { "root", "popup", "reply" })
            Assert.Single(result.Internals.GetAllObjects().OfType<PdfDictionary>(),
                d => V(d.Elements["/NM"]) is PdfString text && text.Value == name);
    }

    [Fact]
    public void E9_Repeated_single_page_annotation_is_fitted_once_without_replacement()
    {
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R 5 0 R 6 0 R]"],
            [
                "<< /Subtype /Text /Rect [20 20 60 60] /Popup 6 0 R >>",
                "<< /Subtype /Popup /Rect [40 40 80 80] /Parent 5 0 R >>",
            ]);
        using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        var imported = output.AddPage(source.Pages[0]);
        var captured = Annotation(imported);
        var fitter = new Geometry(output, new(), 4096);
        fitter.CollectAnnotations(source);
        fitter.Annotations(source.Pages[0], imported, Geometry.Fit(fitter.Snapshot(source.Pages[0]), (100, 100)));
        Assert.Same(captured, Annotation(imported));
        Assert.Same(captured, Annotation(imported, 1));
        Near([10, 10, 30, 30], Values(captured.Elements["/Rect"]));
        Assert.Same(Annotation(imported, 2), V(captured.Elements["/Popup"]));
        var saved = Path.Combine(fx.NewFolder(), "repeated.pdf");
        output.Save(saved);
        using var reopened = PdfReader.Open(saved, PdfDocumentOpenMode.Import);
        var root = Annotation(reopened.Pages[0]);
        Assert.Same(root, Annotation(reopened.Pages[0], 1));
        Assert.Same(Annotation(reopened.Pages[0], 2), V(root.Elements["/Popup"]));
        Assert.Same(root, V(Annotation(reopened.Pages[0], 2).Elements["/Parent"]));
        Near([10, 10, 30, 30], Values(root.Elements["/Rect"]));
    }

    [Fact]
    public void E9_Shared_annots_array_is_not_written_in_place()
    {
        var input = Make(["/MediaBox [0 0 100 100] /Annots 6 0 R", "/MediaBox [0 0 200 200] /Annots 6 0 R"],
            ["[7 0 R]", "<< /Subtype /Text /Rect [20 20 60 60] >>"]);
        using var result = PdfReader.Open(Merge([input], Same), PdfDocumentOpenMode.Import);
        Assert.NotSame(Annotation(result.Pages[0]), Annotation(result.Pages[1]));
        Near([20, 20, 60, 60], Values(Annotation(result.Pages[0]).Elements["/Rect"]));
        Near([10, 10, 30, 30], Values(Annotation(result.Pages[1]).Elements["/Rect"]));
    }

    [Theory]
    [InlineData("objects")]
    [InlineData("items")]
    [InlineData("numbers")]
    [InlineData("streams")]
    [InlineData("da")]
    public void E11_Budget_hit_mid_page_refuses_the_whole_merge(string budget)
    {
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R 6 0 R]"],
            [
                "<< /Subtype /FreeText /Rect [20 20 60 60] /DA (/F1 12 Tf) /AP << /N 7 0 R >> >>",
                "<< /Subtype /FreeText /Rect [20 20 60 60] /DA (/F1 12 Tf) /AP << /N 7 0 R >> >>",
                RawStream("0 0 1 1 re S\n", "/Subtype /Form /BBox [0 0 1 1]"),
            ]);
        var limits = budget switch
        {
            "objects" => new Geometry.Limits { Objects = 9 },
            "items" => new() { Items = 40 },
            "numbers" => new() { SourceNumbers = 8 },
            "streams" => new() { StreamBytes = 40 },
            _ => new() { DaBytes = 9 },
        };
        Refused([Make(["/MediaBox [0 0 100 100]"]), input], limits);
    }

    [Theory]
    [InlineData(false, "items")]
    [InlineData(false, "objects")]
    [InlineData(true, "items")]
    [InlineData(true, "objects")]
    public void E11_Repeated_damaged_annotation_consumes_work_budget(bool enough, string budget)
    {
        CheckDamagedWork(false, enough, budget);
    }

    [Theory]
    [InlineData(false, "items")]
    [InlineData(false, "objects")]
    [InlineData(true, "items")]
    [InlineData(true, "objects")]
    public void E11_Distinct_damaged_annotations_charge_shared_AP_copies(bool enough, string budget)
    {
        CheckDamagedWork(true, enough, budget);
    }

    private void CheckDamagedWork(bool sharedAp, bool enough, string budget)
    {
        var keys = string.Join(" ", Enumerable.Range(0, 100).Select(i => $"/Private{i} {i}"));
        var extra = sharedAp
            ? new[] { "<< /Subtype /Text /Rect [20 20 60 60] /AP 8 0 R >>",
                "<< /Subtype /Text /Rect [20 20 60 60] /AP 8 0 R >>",
                "<< /Subtype /Link /Rect [20 20 60 60] >>", $"<< /N 7 {keys} >>" }
            : new[] { $"<< /Subtype /Text /Rect [20 20 60 60] /Vertices [1] {keys} >>",
                "<< /Subtype /Link /Rect [20 20 60 60] >>" };
        var slots = sharedAp ? "5 0 R 6 0 R 7 0 R" : "5 0 R 5 0 R 6 0 R";
        var input = Make([$"/MediaBox [0 0 200 200] /Annots [{slots}]"], extra);
        var limits = budget == "items"
            ? new Geometry.Limits { Items = enough ? 1000 : 150 }
            : new() { Objects = enough ? 100 : sharedAp ? 4 : 3 };
        var clock = Stopwatch.StartNew();
        if (!enough)
            Refused([input], limits);
        else
        {
            using var result = PdfReader.Open(Merge([Make(["/MediaBox [0 0 100 100]"]), input], Same, limits), PdfDocumentOpenMode.Import);
            Near([20, 20, 60, 60], Values(Annotation(result.Pages[1]).Elements["/Rect"]));
            Near([10, 10, 30, 30], Values(Annotation(result.Pages[1], 2).Elements["/Rect"]));
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"paper-damage sharedAp={sharedAp} enough={enough} budget={budget} input={new FileInfo(input).Length} slots=3 keys=100 limit={(budget == "items" ? limits.Items : limits.Objects)} ms={clock.ElapsedMilliseconds} rss={Process.GetCurrentProcess().PeakWorkingSet64}");
    }

    [Fact]
    public void E11_Widget_DA_parent_depth_has_independent_limit_boundaries()
    {
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R]"],
            [
                "<< /Subtype /Widget /FT /Tx /Rect [20 20 60 60] /Parent 6 0 R >>",
                "<< /Kids [5 0 R] /Parent 7 0 R >>",
                "<< /T (root) /Kids [6 0 R] /DA (/F1 12 Tf) >>",
            ]);
        Refused([input], new() { ParentDepth = 2 });
        foreach (var depth in new[] { 2, 3, 4 })
        {
            using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            using var output = new PdfDocument();
            var page = output.AddPage(source.Pages[0]);
            var fitter = new Geometry(output, new() { ParentDepth = depth }, 4096);
            fitter.CollectAnnotations(source);
            var transform = Geometry.Fit(fitter.Snapshot(source.Pages[0]), (100, 100));
            if (depth < 3)
                Assert.Equal("error.merge_paper_limit", Assert.Throws<ConversionRefusedException>(
                    () => fitter.Annotations(source.Pages[0], page, transform)).ErrorKey);
            else
            {
                fitter.Annotations(source.Pages[0], page, transform);
                Near([10, 10, 30, 30], Values(Annotation(page).Elements["/Rect"]));
                Assert.Equal("/F1 6 Tf", S(Annotation(page).Elements["/DA"]));
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void E11_Widget_DA_parent_cycle_precedes_depth_limit(int depth)
    {
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R 7 0 R]"],
            [
                "<< /Subtype /Widget /Rect [20 20 60 60] /Parent 6 0 R /AP << /N 8 0 R >> >>",
                "<< /Parent 5 0 R >>",
                "<< /Subtype /Link /Rect [20 20 60 60] >>",
                RawStream("0 0 1 1 re S\n", "/Subtype /Form /BBox [0 0 1 1]"),
            ]);
        using var result = PdfReader.Open(Merge([Make(["/MediaBox [0 0 100 100]"]), input], Same,
            new() { ParentDepth = depth }), PdfDocumentOpenMode.Import);
        var widget = Annotation(result.Pages[1]);
        Near([20, 20, 60, 60], Values(widget.Elements["/Rect"]));
        Near([0, 0, 1, 1], Values(D(D(widget.Elements["/AP"]).Elements["/N"]).Elements["/BBox"]));
        Assert.Null(widget.Elements["/DA"]);
        Near([10, 10, 30, 30], Values(Annotation(result.Pages[1], 1).Elements["/Rect"]));
    }

    [Theory]
    [InlineData(false, 0, 200, 1)]
    [InlineData(false, 0, 50, 2)]
    [InlineData(false, 12, 200, 1)]
    [InlineData(false, 12, 50, 2)]
    [InlineData(true, 0, 200, 2)]
    [InlineData(true, 0, 50, 1)]
    [InlineData(true, 12, 200, 2)]
    [InlineData(true, 12, 50, 2)]
    public void E8_FreeText_DA_scales_with_or_without_AP(bool appearance, int size, int width, int unit)
    {
        var da = $"/F1 {size} Tf 0.3 g (Fake /F1 99 Tf) Tj /F#31 0 Tf";
        var ap = appearance ? "/AP << /N 6 0 R >>" : "";
        var input = Make([$"/MediaBox [0 0 {width} {width}] /UserUnit {unit} /Annots [5 0 R]"],
            [$"<< /Subtype /FreeText /Rect [20 20 60 60] /DA ({da}) {ap} >>",
                RawStream("0 0 1 1 re S\n", "/Subtype /Form /BBox [0 0 1 1]")]);
        using var result = PdfReader.Open(Merge([Make(["/MediaBox [0 0 100 100]"]), input], Same), PdfDocumentOpenMode.Import);
        var annotation = Annotation(result.Pages[1]);
        var scale = 100d / width;
        Near(new double[] { 20, 20, 60, 60 }.Select(n => n * scale), Values(annotation.Elements["/Rect"]));
        Assert.Equal($"/F1 {size * scale} Tf 0.3 g (Fake /F1 99 Tf) Tj /F#31 0 Tf", S(annotation.Elements["/DA"]));
        if (appearance)
        {
            var original = D(D(D(D(D(annotation.Elements["/AP"]).Elements["/N"]).Elements["/Resources"]).Elements["/XObject"]).Elements["/Original"]);
            Assert.Equal("0 0 1 1 re S\n", Stream(original));
        }
    }

    [Fact]
    public void E8_FreeText_DA_is_local_and_other_subtypes_are_unchanged()
    {
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R 6 0 R 7 0 R]"],
            [
                "<< /Subtype /FreeText /Rect [20 20 60 60] /Parent 8 0 R >>",
                "<< /Subtype /Text /Rect [20 20 60 60] /DA (/F1 12 Tf) >>",
                "<< /Subtype /Link /Rect [20 20 60 60] /DA (/F1 12 Tf) >>",
                "<< /DA (/F1 18 Tf) >>",
            ]);
        using var result = PdfReader.Open(Merge([Make(["/MediaBox [0 0 100 100]"]), input], Same), PdfDocumentOpenMode.Import);
        Assert.Null(Annotation(result.Pages[1]).Elements["/DA"]);
        Assert.Equal("/F1 12 Tf", S(Annotation(result.Pages[1], 1).Elements["/DA"]));
        Assert.Equal("/F1 12 Tf", S(Annotation(result.Pages[1], 2).Elements["/DA"]));
    }

    [Theory]
    [InlineData("length", 9)]
    [InlineData("bytes", 8)]
    public void E11_FreeText_DA_limits_refuse_and_damage_stays_atomic(string budget, int boundary)
    {
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R 6 0 R]"],
            ["<< /Subtype /FreeText /Rect [20 20 60 60] /DA (/F1 12 Tf) >>",
                "<< /Subtype /Link /Rect [20 20 60 60] >>"]);
        var first = Make(["/MediaBox [0 0 100 100]"]);
        foreach (var offset in new[] { -1, 0, 1 })
        {
            var limits = budget == "length" ? new Geometry.Limits { DaLength = boundary + offset }
                : new() { DaBytes = boundary + offset };
            if (offset < 0)
                Refused([first, input], limits);
            else
            {
                using var result = PdfReader.Open(Merge([first, input], Same, limits), PdfDocumentOpenMode.Import);
                Near([10, 10, 30, 30], Values(Annotation(result.Pages[1]).Elements["/Rect"]));
                Assert.Equal("/F1 6 Tf", S(Annotation(result.Pages[1]).Elements["/DA"]));
            }
        }
        var damaged = Make(["/MediaBox [0 0 100 100] /Annots [5 0 R 6 0 R]"],
            [$"<< /Subtype /FreeText /Rect [20 20 60 60] /DA (/F1 {new string('9', 308)} Tf) /AP << /N 7 0 R >> >>",
                "<< /Subtype /Link /Rect [20 20 60 60] >>",
                RawStream("0 0 1 1 re S\n", "/Subtype /Form /BBox [0 0 1 1]")]);
        var target = Make(["/MediaBox [0 0 200 200]"]);
        using var output = PdfReader.Open(Merge([target, damaged], Same), PdfDocumentOpenMode.Import);
        Near([20, 20, 60, 60], Values(Annotation(output.Pages[1]).Elements["/Rect"]));
        Near([0, 0, 1, 1], Values(D(D(Annotation(output.Pages[1]).Elements["/AP"]).Elements["/N"]).Elements["/BBox"]));
        Assert.Equal($"/F1 {new string('9', 308)} Tf", S(Annotation(output.Pages[1]).Elements["/DA"]));
        Near([40, 40, 120, 120], Values(Annotation(output.Pages[1], 1).Elements["/Rect"]));
    }

    [Fact]
    public void E11_FreeText_encoded_appearance_length_has_independent_boundaries()
    {
        const string da = "/F1 12 Tf % \uD55C\uAE00\n";
        var hex = "FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(da));
        var input = Make(["/MediaBox [0 0 200 200] /Annots [5 0 R]"],
            [$"<< /Subtype /FreeText /Rect [20 20 60 60] /DA <{hex}> >>"]);
        var first = Make(["/MediaBox [0 0 100 100]"]);
        var boundary = 2 + Encoding.BigEndianUnicode.GetByteCount(da);
        Assert.True(boundary > da.Length);
        foreach (var offset in new[] { -1, 0, 1 })
        {
            if (offset < 0)
                Refused([first, input], new() { DaLength = 1000, DaBytes = 1000 }, appearanceLength: boundary + offset);
            else
            {
                using var result = PdfReader.Open(Merge([first, input], Same, new() { DaLength = 1000, DaBytes = 1000 },
                    appearanceLength: boundary + offset), PdfDocumentOpenMode.Import);
                Near([10, 10, 30, 30], Values(Annotation(result.Pages[1]).Elements["/Rect"]));
                Assert.Equal("/F1 6 Tf % \uD55C\uAE00\n", S(Annotation(result.Pages[1]).Elements["/DA"]));
            }
        }
    }
}
