// PDF/A-2b without Ghostscript (Pdf/PdfAConformance.cs, PdfAMetadata.cs, SrgbProfile.cs): the sRGB profile made in
// code, the XMP and document information that must match, a PDF full of what PDF/A forbids made to conform, the PDFs
// it refuses (and leaves as they are) with why, a CMYK PDF keeping its CMYK intent, the Filee renderer writing PDF/A
// directly, and the job queue without Ghostscript. Conformance is checked by PdfA2bChecks, a structural check of the
// rules implemented: veraPDF isn't run. Every input is built in the test.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using Filee.Engines.Rendering;
using Filee.Engines.Vector;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public class PdfAConformanceTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PdfItem? V(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static Preset PdfAPreset() =>
        new() { TargetFormat = "pdf", Document = { PdfA = true }, Output = { FileNamePattern = "{name}_pdfa" } };

    // ---- inputs

    /// <summary>
    /// LZW data (ISO 32000-1 7.4.4) of nine-bit literal codes only: a clear code, each byte, the end code. Short data
    /// never fills the table far enough for wider codes.
    /// </summary>
    private static byte[] Lzw(byte[] data)
    {
        Assert.True(data.Length < 200);
        var codes = new List<int> { 256 };
        codes.AddRange(data.Select(b => (int)b));
        codes.Add(257);
        var bits = new List<bool>();
        foreach (var code in codes)
        {
            for (var bit = 8; bit >= 0; bit--)
                bits.Add(((code >> bit) & 1) == 1);
        }
        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        return bytes;
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>The header of an ICC profile of <paramref name="space"/> (nothing else: what PDF/A looks at).</summary>
    private static byte[] IccHeader(string deviceClass, string space)
    {
        var icc = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(icc, (uint)icc.Length);
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(8), 0x02100000);
        Encoding.ASCII.GetBytes(deviceClass, icc.AsSpan(12));
        Encoding.ASCII.GetBytes(space, icc.AsSpan(16));
        Encoding.ASCII.GetBytes("Lab ", icc.AsSpan(20));
        Encoding.ASCII.GetBytes("acsp", icc.AsSpan(36));
        return icc;
    }

    private const string Embedded =
        "<< /Type /Font /Subtype /TrueType /BaseFont /Dummy /FirstChar 72 /LastChar 105 /FontDescriptor 6 0 R >>";

    private const string Descriptor =
        "<< /Type /FontDescriptor /FontName /Dummy /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 " +
        "/Descent -200 /CapHeight 700 /StemV 80 /FontFile2 7 0 R >>";

    /// <summary>
    /// One page drawing <paramref name="content"/> with an embedded font (its program is a stand-in: the rules ask
    /// that it be there), objects 5 … 7 the font; <paramref name="extra"/> objects are 8 ….
    /// </summary>
    private static string Simple(string path, string content = "BT /F1 12 Tf 72 700 Td (Hi) Tj ET 0 0 1 rg 72 600 200 100 re f",
        string annotations = "", string catalog = "", string resources = "", IReadOnlyList<string>? extra = null, string font = Embedded) =>
        RawPdf.Write(path,
        [
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> {resources} >> {annotations} >>",
            RawPdf.Stream("", content),
            font,
            Descriptor,
            RawPdf.Stream("/Length1 4", "true"),
            .. extra ?? [],
        ]);

    /// <summary>Everything PDF/A-2b forbids or wants otherwise that Filee can change, in one PDF.</summary>
    private static string Forbidden(string path)
    {
        var picture = Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray();
        return RawPdf.Write(path,
        [
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R /Metadata 26 0 R /Outlines 21 0 R /OpenAction << /S /JavaScript /JS (app.alert\\(1\\)) >> " +
                    "/AA << /WC << /S /JavaScript /JS (x) >> >> /Names << /JavaScript << /Names [(a) << /S /JavaScript /JS (y) >>] >> " +
                    "/EmbeddedFiles << /Names [] >> >> /OCProperties << /OCGs [24 0 R 25 0 R] /D << /Name (Same) " +
                    "/AS [<< /Event /View /OCGs [24 0 R] /Category [/View] >>] /Order [24 0 R] >> /Configs [<< /Name (Same) >>] >> " +
                    "/AcroForm << /Fields [20 0 R] /NeedAppearances true /XFA (xfa) >> >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /AA << /O << /S /JavaScript /JS (z) >> >> " +
                    "/Resources << /Font << /F1 10 0 R /F2 11 0 R >> /ExtGState << /G1 14 0 R >> /XObject << /Im1 15 0 R /Fm1 16 0 R >> >> " +
                    "/Annots [5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 18 0 R 19 0 R 20 0 R 22 0 R] >>",
            /* 4 */ RawPdf.Stream("", "q 0 0 1 rg 72 600 200 100 re f Q BT /F1 12 Tf 72 700 Td (Hi) Tj ET /G1 gs /Im1 Do /Fm1 Do"),
            /* 5 */ "<< /Type /Annot /Subtype /Link /Rect [72 650 200 670] /A << /S /Launch /F (calc.exe) >> /NM (launch) >>",
            /* 6 */ "<< /Type /Annot /Subtype /Link /Rect [72 620 200 640] /A << /S /URI /URI (https://example.com) >> /NM (uri) >>",
            /* 7 */ "<< /Type /Annot /Subtype /Square /Rect [300 600 400 650] /F 2 /AP << /N 12 0 R >> /NM (hidden) >>",
            /* 8 */ "<< /Type /Annot /Subtype /FileAttachment /Rect [300 500 320 520] /FS << /Type /Filespec /F (a.txt) >> /NM (attachment) >>",
            /* 9 */ "<< /Type /Annot /Subtype /Square /Rect [300 400 400 450] /AP << /N 12 0 R /D 12 0 R /R 12 0 R >> /NM (square) >>",
            /* 10 */ "<< /Type /Font /Subtype /TrueType /BaseFont /Dummy /FirstChar 72 /LastChar 105 /FontDescriptor 13 0 R >>",
            /* 11 */ "<< /Type /Font /Subtype /Type0 /BaseFont /DummyCID /Encoding /Identity-H /DescendantFonts [<< /Type /Font " +
                     "/Subtype /CIDFontType2 /BaseFont /DummyCID /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                     "/FontDescriptor 13 0 R >>] >>",
            /* 12 */ RawPdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 50]", "0 g 0 0 100 50 re f"),
            /* 13 */ "<< /Type /FontDescriptor /FontName /Dummy /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 " +
                     "/Descent -200 /CapHeight 700 /StemV 80 /FontFile2 17 0 R >>",
            /* 14 */ "<< /Type /ExtGState /TR /Identity /TR2 /Identity /HTO [0 0] /BM /Weird /RI /Bogus /ca 0.5 >>",
            /* 15 */ RawPdf.Stream("/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 " +
                     "/Interpolate true /Alternates [] /OPI << >> /Filter /LZWDecode", Latin1(Lzw(picture))),
            /* 16 */ RawPdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 10] /Ref << /F (other.pdf) /Page 0 >> /OPI << >>", "0 g 0 0 10 10 re f"),
            /* 17 */ RawPdf.Stream("/Length1 4", "true"),
            /* 18 */ "<< /Type /Annot /Subtype /Popup /Rect [400 700 500 750] /F 2 /NM (popup) >>",
            /* 19 */ "<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] /AS /On /AP << /N << /On 12 0 R /Off 12 0 R >> >> /NM (note) >>",
            /* 20 */ "<< /Type /Annot /Subtype /Widget /FT /Btn /T (ok) /Rect [100 200 200 220] /AP << /N 12 0 R >> " +
                     "/A << /S /JavaScript /JS (q) >> /AA << /D << /S /JavaScript /JS (w) >> >> /NM (button) >>",
            /* 21 */ "<< /Type /Outlines /First 23 0 R /Last 23 0 R /Count 1 >>",
            /* 22 */ "<< /Type /Annot /Subtype /Movie /Rect [100 300 200 350] /NM (movie) >>",
            /* 23 */ "<< /Title (Go) /Parent 21 0 R /A << /S /JavaScript /JS (o) >> >>",
            /* 24 */ "<< /Type /OCG /Name (Layer 1) >>",
            /* 25 */ "<< /Type /OCG /Name (Layer 2) >>",
            /* 26 */ RawPdf.Stream("/Type /Metadata /Subtype /XML", "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"),
            /* 27 */ "<< /Title (Forbidden things) /Author (Kim) /Trapped /True /Creator (Raw) /Producer (Hand) /CreationDate (D:20240101120000Z) >>",
        ], "/Info 27 0 R");
    }

    private static Dictionary<string, PdfDictionary> AnnotationsByName(PdfDocument document) =>
        ((PdfArray)V(document.Pages[0].Elements["/Annots"])!).Elements.Select(V).Cast<PdfDictionary>()
            .ToDictionary(a => a.Elements.GetString("/NM"));

    // ---- the profile and the metadata

    [Fact]
    public void The_srgb_profile_is_a_version_2_display_profile_skia_reads_as_srgb()
    {
        var icc = SrgbProfile.Bytes;

        Assert.Equal((uint)icc.Length, BinaryPrimitives.ReadUInt32BigEndian(icc));
        Assert.Equal(2, icc[8]);
        Assert.Equal("mntr", Encoding.ASCII.GetString(icc, 12, 4));
        Assert.Equal("RGB ", Encoding.ASCII.GetString(icc, 16, 4));
        Assert.Equal("XYZ ", Encoding.ASCII.GetString(icc, 20, 4));
        Assert.Equal("acsp", Encoding.ASCII.GetString(icc, 36, 4));
        Assert.True(icc.Length < 3000, $"{icc.Length} bytes");
        Assert.Equal(icc, SrgbProfile.Bytes); // the same every time

        // skcms (Skia's ICC reader) takes it and finds sRGB's gamut and curve in it.
        using var parsed = SKColorSpace.CreateIcc(icc);
        Assert.NotNull(parsed);
        using var srgb = SKColorSpace.CreateSrgb();
        var (ours, theirs) = (parsed.ToColorSpaceXyz(), srgb.ToColorSpaceXyz());
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
                Assert.Equal(theirs[row, column], ours[row, column], 0.002);
        }
        Assert.True(parsed.GetNumericalTransferFunction(out var curve));
        foreach (var x in new[] { 0.01f, 0.2f, 0.5f, 0.8f, 1f })
            Assert.Equal(SrgbProfile.ToLinear(x), curve.Transform(x), 0.002);
    }

    [Fact]
    public void Xmp_and_document_information_say_the_same_whatever_the_text()
    {
        var dir = fx.NewFolder();
        var path = Path.Combine(dir, "info.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            PdfAConformance.Prepare(document, Ct);
            using var output = File.Create(path);
            PdfAMetadata.Save(document, output, new PdfAInfo
            {
                Title = "Ünïcödé 한글 <&> \"quotes\"\r\nnext line\u0001",
                Author = "Kim & Lee",
                Subject = "  spaced  ",
                Keywords = "pdf, archive",
                Creator = "Writer",
                Producer = "Filee",
                Created = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5.5)),
            }, now: new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(9)));
        }

        PdfA2bChecks.Assert2b(path);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            Assert.Equal("Ünïcödé 한글 <&> \"quotes\"\nnext line", document.Info.Title);
            Assert.Equal("spaced", document.Info.Subject);
            Assert.Equal("(D:20240102030405-05'30')", "(" + document.Info.Elements.GetString("/CreationDate") + ")");
            Assert.True(GhostscriptConverter.DeclaresPdfA(path));
        }
        using var pig = PigDocument.Open(path);
        Assert.Equal(1, pig.NumberOfPages);
        Assert.Equal("Kim & Lee", pig.Information.Author);
    }

    // ---- PDF → PDF/A

    [Fact]
    public void A_pdf_full_of_what_pdf_a_forbids_conforms_afterwards()
    {
        var dir = fx.NewFolder();
        var path = Forbidden(Path.Combine(dir, "forbidden.pdf"));
        Assert.NotEmpty(PdfA2bChecks.Check(File.ReadAllBytes(path)));

        Assert.Equal(PdfArchiveOutcome.Archived, PdfSharpConverter.Archive(path, changed: false, fx.NewFolder(), Ct));

        PdfA2bChecks.Assert2b(path);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var catalog = document.Internals.Catalog;
        Assert.False(catalog.Elements.ContainsKey("/OpenAction"));
        Assert.False(catalog.Elements.ContainsKey("/AA"));

        // Hidden, attachment and movie annotations are gone; the rest print and keep their appearance.
        var annotations = AnnotationsByName(document);
        Assert.Equal(["button", "launch", "note", "popup", "square", "uri"], annotations.Keys.Order());
        Assert.False(annotations["launch"].Elements.ContainsKey("/A"));
        Assert.Equal("https://example.com", ((PdfDictionary)V(annotations["uri"].Elements["/A"])!).Elements.GetString("/URI"));
        Assert.False(annotations["popup"].Elements.ContainsKey("/F"));
        Assert.Equal("/Off", annotations["button"].Elements.GetName("/AS"));
        Assert.NotNull(((PdfDictionary)V(annotations["note"].Elements.GetDictionary("/AP")!.Elements["/N"])!).Stream);

        // The picture is deflated now, its pixels the same.
        var page = document.Pages[0];
        var resources = page.Elements.GetDictionary("/Resources")!;
        var image = (PdfDictionary)V(resources.Elements.GetDictionary("/XObject")!.Elements["/Im1"])!;
        Assert.Equal("/FlateDecode", image.Elements.GetName("/Filter"));
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray(), image.Stream!.UnfilteredValue);

        var cid = (PdfDictionary)V(((PdfArray)V(((PdfDictionary)V(resources.Elements.GetDictionary("/Font")!.Elements["/F2"])!).Elements["/DescendantFonts"])!).Elements[0])!;
        Assert.Equal("/Identity", cid.Elements.GetName("/CIDToGIDMap"));
        var form = catalog.Elements.GetDictionary("/AcroForm")!;
        Assert.False(form.Elements.ContainsKey("/XFA"));
        var outline = (PdfDictionary)V(catalog.Elements.GetDictionary("/Outlines")!.Elements["/First"])!;
        Assert.Equal("Go", outline.Elements.GetString("/Title"));
        Assert.False(outline.Elements.ContainsKey("/A"));

        // Its document information, without what PDF/A has no place for.
        Assert.Equal("Forbidden things", document.Info.Title);
        Assert.Equal("Hand", document.Info.Elements.GetString("/Producer"));
        Assert.False(document.Info.Elements.ContainsKey("/Trapped"));
    }

    public static TheoryData<string, string> Refused() => new()
    {
        { "font", PdfAConformance.FontNotEmbedded },
        { "cmyk", PdfAConformance.DeviceCmyk },
        { "cmyk-and-rgb", PdfAConformance.DeviceCmyk },
        { "appearance", PdfAConformance.NoAppearance },
        { "xfa", PdfAConformance.Unsupported },
        { "postscript", PdfAConformance.Unsupported },
        { "inline-lzw", PdfAConformance.Unsupported },
        { "external", PdfAConformance.Unsupported },
        { "damaged", PdfAConformance.Unreadable },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void What_it_cannot_make_conform_is_refused_with_why_and_left_as_it_is(string kind, string reason)
    {
        var path = Path.Combine(fx.NewFolder(), kind + ".pdf");
        _ = kind switch
        {
            "font" => Simple(path, font: "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            "cmyk" => Simple(path, content: "0 0 0 1 k 72 600 200 100 re f"),
            "cmyk-and-rgb" => Simple(path, content: "0 0 0 1 k 72 600 200 100 re f 1 0 0 rg 0 0 10 10 re f",
                extra: [IccHeaderObject("prtr", "CMYK")], catalog: Intent("GTS_PDFA1", 8)),
            "appearance" => Simple(path, annotations: "/Annots [<< /Type /Annot /Subtype /Square /Rect [10 10 50 50] >>]"),
            "xfa" => Simple(path, catalog: "/NeedsRendering true /AcroForm << /Fields [] /XFA (x) >>"),
            "postscript" => Simple(path, resources: "/XObject << /P 8 0 R >>", content: "/P Do",
                extra: [RawPdf.Stream("/Type /XObject /Subtype /PS", "showpage")]),
            "inline-lzw" => Simple(path, content: $"BI /W 4 /H 4 /CS /G /BPC 8 /F /LZW ID {Latin1(Lzw(new byte[16]))} EI"),
            "external" => Simple(path, resources: "/XObject << /X 8 0 R >>", content: "/X Do",
                extra: [RawPdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 1 1] /FFilter /FlateDecode", "")]),
            _ => Simple(path, content: "BT /F1 12 Tf (never closed"),
        };
        var bytes = File.ReadAllBytes(path);
        var work = fx.NewFolder();

        var refused = Assert.Throws<ConversionRefusedException>(() => PdfSharpConverter.Archive(path, changed: true, work, Ct));

        Assert.Equal(reason, refused.ErrorKey);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    private static string IccHeaderObject(string deviceClass, string space) =>
        RawPdf.Stream($"/N {(space == "CMYK" ? 4 : 3)}", Latin1(IccHeader(deviceClass, space)));

    private static string Intent(string kind, int profile, string identifier = "FOGRA39") =>
        $"/OutputIntents [<< /Type /OutputIntent /S /{kind} /OutputConditionIdentifier ({identifier}) /DestOutputProfile {profile} 0 R >>]";

    [Fact]
    public void A_pdf_locked_with_a_password_is_refused_as_locked()
    {
        var path = Simple(Path.Combine(fx.NewFolder(), "locked.pdf"));
        PdfBuilder.Protect(path, "secret");

        var refused = Assert.Throws<ConversionRefusedException>(() => PdfSharpConverter.Archive(path, changed: false, fx.NewFolder(), Ct));

        Assert.Equal(PdfAConformance.Locked, refused.ErrorKey);
    }

    [Fact]
    public void A_cmyk_pdf_keeps_the_cmyk_intent_it_brings()
    {
        var path = Simple(Path.Combine(fx.NewFolder(), "press.pdf"), content: "0 0 0 1 k 72 600 200 100 re f",
            extra: [IccHeaderObject("prtr", "CMYK")], catalog: Intent("GTS_PDFX", 8));

        Assert.Equal(PdfArchiveOutcome.Archived, PdfSharpConverter.Archive(path, changed: false, fx.NewFolder(), Ct));

        PdfA2bChecks.Assert2b(path);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var intent = (PdfDictionary)V(document.Internals.Catalog.Elements.GetArray("/OutputIntents")!.Elements.Single())!;
        Assert.Equal("/GTS_PDFA1", intent.Elements.GetName("/S"));
        Assert.Equal("FOGRA39", intent.Elements.GetString("/OutputConditionIdentifier"));
        Assert.Equal(4, ((PdfDictionary)V(intent.Elements["/DestOutputProfile"])!).Elements.GetInteger("/N"));
    }

    [Fact]
    public void An_rgb_pdf_gets_the_srgb_intent_and_a_pdf_a_already_is_left_alone()
    {
        var dir = fx.NewFolder();
        var path = Simple(Path.Combine(dir, "plain.pdf"));

        Assert.Equal(PdfArchiveOutcome.Archived, PdfSharpConverter.Archive(path, changed: false, fx.NewFolder(), Ct));

        PdfA2bChecks.Assert2b(path);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            var intent = (PdfDictionary)V(document.Internals.Catalog.Elements.GetArray("/OutputIntents")!.Elements.Single())!;
            Assert.Equal(SrgbProfile.Name, intent.Elements.GetString("/OutputConditionIdentifier"));
            Assert.Equal(SrgbProfile.Bytes, ((PdfDictionary)V(intent.Elements["/DestOutputProfile"])!).Stream!.UnfilteredValue);
        }
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(PdfArchiveOutcome.Archived, PdfSharpConverter.Archive(path, changed: false, fx.NewFolder(), Ct));
        Assert.Equal(bytes, File.ReadAllBytes(path));

        // Changed since (a watermark): made PDF/A again, and still one.
        Assert.Equal(PdfArchiveOutcome.Archived, PdfSharpConverter.Archive(path, changed: true, fx.NewFolder(), Ct));
        PdfA2bChecks.Assert2b(path);
    }

    [Fact]
    public void Preparing_stops_when_cancelled()
    {
        var path = Simple(Path.Combine(fx.NewFolder(), "plain.pdf"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PdfSharpConverter.Archive(path, changed: true, fx.NewFolder(), cancelled.Token));
    }

    // ---- the Filee renderer

    [Fact]
    public void The_renderer_writes_pdf_a_directly_with_embedded_subset_fonts_and_printing_links()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        using var shaper = new TestShaper();
        var page = new PageContent(595.28f, 841.89f); // A4: not whole points, so PDFsharp sets the media box too
        page.DrawGlyphs(shaper.Shape(fonts.Font(RepositoryFonts.Latin), 20, "Archived for later", 50, 100).Run);
        page.LinkToUrl(new SKRect(50, 80, 250, 105), "https://example.com/archive");
        using var output = new MemoryStream();

        PdfPageWriter.Write(output, [page], new PdfDocumentInfo { Title = "Archive", Outline = [new("Start", 0, 80, 0)], PdfA = true },
            cancellationToken: Ct);

        var path = Path.Combine(fx.NewFolder(), "rendered.pdf");
        File.WriteAllBytes(path, output.ToArray());
        PdfA2bChecks.Assert2b(path);
        Assert.True(GhostscriptConverter.DeclaresPdfA(path));
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            Assert.Equal("Archive", document.Info.Title);
            Assert.Equal("Filee", document.Info.Elements.GetString("/Producer"));
            Assert.Equal("Filee", document.Info.Elements.GetString("/Creator"));
            var fontsUsed = document.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/Font")!.Elements.Values
                .Select(V).Cast<PdfDictionary>().ToList();
            Assert.NotEmpty(fontsUsed);
            Assert.All(fontsUsed, f => Assert.True(f.Elements.ContainsKey("/ToUnicode"), f.Elements.GetName("/BaseFont")));
            Assert.All(fontsUsed, f => Assert.Matches("^/[A-Z]{6}\\+", f.Elements.GetName("/BaseFont"))); // subset
            var link = (PdfDictionary)V(document.Pages[0].Elements.GetArray("/Annots")!.Elements.Single())!;
            Assert.Equal(4, link.Elements.GetInteger("/F"));
            Assert.Equal("Start", Assert.Single(document.Outlines).Title);
        }
        using var pig = PigDocument.Open(path);
        Assert.Equal("Archivedforlater", string.Concat(pig.GetPage(1).Text.Where(c => !char.IsWhiteSpace(c))));
    }

    [Fact]
    public void Without_the_box_the_renderer_writes_no_pdf_a()
    {
        var page = new PageContent(200, 100);
        page.Draw(canvas => canvas.DrawRect(10, 10, 50, 50, new SKPaint { Color = SKColors.Teal }));
        using var output = new MemoryStream();

        PdfPageWriter.Write(output, [page], new PdfDocumentInfo(), cancellationToken: Ct);

        Assert.DoesNotContain("pdfaid", Encoding.Latin1.GetString(output.ToArray()));
    }

    [Fact]
    public async Task A_document_the_renderer_draws_comes_out_pdf_a_from_its_own_step()
    {
        var dir = fx.NewFolder();
        var hwpx = await fx.MakeHwpxAsync(dir);
        var renderer = fx.Converters.Single(c => c.Id == "layout");
        var target = Path.Combine(dir, "out.pdf");
        var step = new ConversionStep(hwpx, "hwpx", "pdf", PdfAPreset(), new FixedOutput(target), fx.NewFolder());

        var written = await renderer.ConvertAsync(step, null, Ct);

        Assert.Equal(target, Assert.Single(written));
        PdfA2bChecks.Assert2b(target);
        Assert.True(GhostscriptConverter.DeclaresPdfA(target));
    }

    private sealed class FixedOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }

    // ---- the job queue without Ghostscript

    private ConverterCatalog WithoutGhostscript() =>
        new(fx.Converters.Where(c => c.Id != "ghostscript")) { Priority = fx.Catalog.Priority };

    [Fact]
    public async Task Without_ghostscript_a_pdf_becomes_pdf_a_and_one_it_cannot_says_why()
    {
        var dir = fx.NewFolder();
        var good = Simple(Path.Combine(dir, "good.pdf"));
        var helvetica = Simple(Path.Combine(dir, "helvetica.pdf"), font: "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var job = await fx.ConvertAsync([good, helvetica], BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.PdfAId), WithoutGhostscript());

        Assert.Equal(JobState.Completed, job.State);
        var (first, second) = (job.Files[0], job.Files[1]);
        Assert.Null(first.NoticeKey);
        PdfA2bChecks.Assert2b(first.Outputs.Single());
        Assert.Equal("notice.pdfa_needs_ghostscript", second.NoticeKey);
        Assert.Equal(PdfAConformance.FontNotEmbedded, second.NoticeReasonKey);
        Assert.False(GhostscriptConverter.DeclaresPdfA(second.Outputs.Single()));
    }
}
