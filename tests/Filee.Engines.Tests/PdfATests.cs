// PDF/A (#1367): Ghostscript's pdfwrite settings and output intent, which PDFs already count as PDF/A, and real round
// trips (skipped when Ghostscript isn't installed): PDF → PDF/A, a picture → PDF/A, and a PDF/A from Skia kept as it is.
// Annotations without the print flag (#1496): which get it in the copy Ghostscript reads, what else the copy keeps, when
// no copy is made, and that links and document information come through to the PDF/A. Every input is made in the test.

using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using Filee.Engines.Vector;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public class PdfATests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Sentence = "Archived for later";

    private static bool GhostscriptInstalled => GhostscriptConverter.Locate() is not null;

    private static Preset PdfAPreset(string watermark = "") =>
        new() { TargetFormat = "pdf", Document = { PdfA = true }, Pdf = { Watermark = watermark }, Output = { FileNamePattern = "{name}_pdfa" } };

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
    }

    private static string TextPdf(string folder)
    {
        var pdf = new PdfBuilder();
        pdf.NewPage();
        pdf.Line(Sentence, 14, pdf.Font(), 72, 72);
        pdf.Bar(72, 120, 200, 40, (200, 40, 40));
        return pdf.Save(Path.Combine(folder, "letter.pdf"));
    }

    /// <summary>A one-rectangle SVG, which Skia writes as PDF/A itself.</summary>
    private static string Badge(string folder)
    {
        var svg = Path.Combine(folder, "badge.svg");
        File.WriteAllText(svg, """<svg xmlns="http://www.w3.org/2000/svg" width="200" height="100"><rect width="200" height="100" fill="#2060C0"/></svg>""");
        return svg;
    }

    /// <summary>The output intent PDF/A asks for: GTS_PDFA1 with an RGB profile.</summary>
    private static void AssertOutputIntent(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var intents = document.Internals.Catalog.Elements.GetArray("/OutputIntents");
        Assert.NotNull(intents);
        var intent = Assert.IsType<PdfDictionary>(intents.Elements.GetObject(0));
        Assert.Equal("/GTS_PDFA1", intent.Elements.GetName("/S"));
        var profile = Assert.IsType<PdfDictionary>(intent.Elements.GetObject("/DestOutputProfile"));
        Assert.Equal(3, profile.Elements.GetInteger("/N"));
    }

    private static string Words(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    [Fact]
    public void Pdfwrite_makes_pdf_a_2b_in_rgb_and_leaves_out_what_it_must()
    {
        var args = GhostscriptConverter.PdfAArguments();

        Assert.Contains("-sDEVICE=pdfwrite", args);
        Assert.Contains("-dPDFA=2", args);
        Assert.Contains("-dPDFACompatibilityPolicy=1", args); // drop what PDF/A forbids, don't give up or write a plain PDF
        Assert.Contains("-sColorConversionStrategy=RGB", args); // to match the sRGB output intent
        Assert.Contains("-dAutoRotatePages=/None", args);
    }

    [Fact]
    public void The_output_intent_carries_the_srgb_profile_inline()
    {
        var profile = ColorProfiles.SRGB.ToByteArray();
        var script = GhostscriptConverter.OutputIntent(profile);

        Assert.StartsWith("%!PS", script);
        Assert.True(profile.Length < 65535, "A PostScript string holds at most 65535 bytes.");
        Assert.Contains(Convert.ToHexString(profile), string.Concat(script.Where(char.IsAsciiHexDigit)));
        Assert.Contains("/S /GTS_PDFA1", script);
        Assert.Contains("[{Catalog} <</OutputIntents [{OutputIntent_PDFA}]>> /PUT pdfmark", script);
        Assert.All(script.Split('\n'), line => Assert.True(line.Length < 256, line));
        Assert.True(script.All(char.IsAscii));
    }

    [Fact]
    public void Only_a_pdf_whose_xmp_says_pdf_a_counts_as_one()
    {
        var dir = fx.NewFolder();
        var plain = TextPdf(dir);
        var broken = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.7 not really");

        Assert.False(GhostscriptConverter.DeclaresPdfA(plain));
        Assert.False(GhostscriptConverter.DeclaresPdfA(broken));
    }

    [Fact]
    public async Task A_pdf_a_from_skia_is_kept_as_it_is_without_ghostscript()
    {
        var dir = fx.NewFolder();
        var svg = Badge(dir);

        var job = await fx.ConvertAsync([svg], PdfAPreset());

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.True(GhostscriptConverter.DeclaresPdfA(output));
        Assert.Null(job.Files.Single().NoticeKey);
        Assert.DoesNotContain("Ghostscript", Encoding.Latin1.GetString(File.ReadAllBytes(output))); // not rewritten
    }

    [Fact]
    public async Task A_pdf_becomes_pdf_a_with_its_text()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = fx.NewFolder();
        var source = TextPdf(dir);
        Assert.False(GhostscriptConverter.DeclaresPdfA(source));

        var job = await fx.ConvertAsync([source], BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.PdfAId));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("letter_pdfa.pdf", Path.GetFileName(output));
        Assert.Null(job.Files.Single().NoticeKey);
        Assert.True(GhostscriptConverter.DeclaresPdfA(output));
        AssertOutputIntent(output);
        using var pig = PigDocument.Open(output);
        Assert.Equal(Words(Sentence), Words(pig.GetPage(1).Text));
    }

    [Fact]
    public async Task A_picture_made_into_a_pdf_by_pdfsharp_becomes_pdf_a()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = fx.NewFolder();
        var picture = EngineFixture.MakeImage(dir, "photo.png", MagickFormat.Png);

        var job = await fx.ConvertAsync([picture], PdfAPreset());

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.True(GhostscriptConverter.DeclaresPdfA(output));
        AssertOutputIntent(output);
    }

    [Fact]
    public async Task A_pdf_a_given_a_watermark_is_made_pdf_a_again()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = fx.NewFolder();
        var svg = Badge(dir);

        var job = await fx.ConvertAsync([svg], PdfAPreset(watermark: "DRAFT"));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.True(GhostscriptConverter.DeclaresPdfA(output));
        AssertOutputIntent(output); // Skia's PDF/A had none of its own: this one is Ghostscript's
    }

    // ---- Annotations without the print flag (#1496) ----

    private const string GhostscriptMissing = "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PdfItem? V(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static string? Text(PdfItem? item) => V(item) switch
    {
        PdfString text => text.Value,
        PdfStringObject text => text.Value,
        _ => null,
    };

    private static int? Flag(PdfItem? item) => V(item) switch
    {
        PdfInteger number => number.Value,
        PdfIntegerObject number => number.Value,
        _ => null,
    };

    private static double Number(PdfItem? item) => V(item) switch
    {
        PdfInteger number => number.Value,
        PdfIntegerObject number => number.Value,
        PdfReal number => number.Value,
        PdfRealObject number => number.Value,
        _ => throw new InvalidOperationException("Not a PDF number"),
    };

    private static List<PdfDictionary> Annotations(PdfPage page) =>
        V(page.Elements["/Annots"]) is PdfArray annotations
            ? annotations.Elements.Select(item => Assert.IsAssignableFrom<PdfDictionary>(V(item))).ToList()
            : [];

    private static string Annot(string subtype, string name, string extra = "") =>
        $"<< /Type /Annot /Subtype /{subtype} /Rect [100 100 200 120] /NM ({name}) {extra} >>";

    private static string UriLink(string name, string extra = "") =>
        Annot("Link", name, $"/A << /S /URI /URI (https://example.com/{name}) >> {extra}");

    /// <summary>One page with these annotations (objects 5 …); <paramref name="extra"/> objects follow them.</summary>
    private static string OnePage(string path, IReadOnlyList<string> annotations, string catalog = "", IReadOnlyList<string>? extra = null,
        string trailer = "")
    {
        var references = string.Join(' ', annotations.Select((_, i) => $"{i + 5} 0 R"));
        List<string> objects =
        [
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [{references}] >>",
            RawPdf.Stream("", "0 0 1 rg 72 600 200 100 re f"),
            .. annotations,
            .. extra ?? [],
        ];
        return RawPdf.Write(path, objects, trailer);
    }

    /// <summary>
    /// Two pages: a note with its popup and a reply, a highlight with an appearance, ink, a link both pages show and a
    /// link to page 2; page 1 cropped and turned; full document information.
    /// </summary>
    private static string AnnotatedLetter(string folder) =>
        RawPdf.Write(Path.Combine(folder, "annotated.pdf"),
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [10 20 600 780] /Rotate 90 /Contents 5 0 R " +
            "/Annots [7 0 R 8 0 R 9 0 R 10 0 R 11 0 R 12 0 R 13 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Annots [12 0 R] >>",
            RawPdf.Stream("", "0 0 1 rg 72 600 200 100 re f"),
            RawPdf.Stream("", "1 0 0 rg 72 72 100 100 re f"),
            "<< /Type /Annot /Subtype /Text /Rect [100 700 120 720] /Contents (A note) /Popup 8 0 R /NM (text) >>",
            "<< /Type /Annot /Subtype /Popup /Rect [130 650 300 720] /Parent 7 0 R /NM (popup) >>",
            "<< /Type /Annot /Subtype /Text /Rect [100 670 120 690] /Contents (A reply) /IRT 7 0 R /NM (reply) >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [70 590 280 710] /QuadPoints [72 700 272 700 72 600 272 600] /C [1 1 0] " +
            "/AP << /N 14 0 R >> /NM (highlight) >>",
            "<< /Type /Annot /Subtype /Ink /Rect [300 300 400 400] /InkList [[300 300 350 380 400 320]] /NM (ink) >>",
            "<< /Type /Annot /Subtype /Link /Rect [400 100 500 120] /A << /S /URI /URI (https://example.com/shared) >> /NM (shared) >>",
            "<< /Type /Annot /Subtype /Link /Rect [400 140 500 160] /Dest [4 0 R /XYZ 72 500 1.5] /NM (goto) >>",
            RawPdf.Stream("/Type /XObject /Subtype /Form /BBox [70 590 280 710]", "1 1 0 rg 72 600 200 100 re f"),
            "<< /Title (Letter) /Author (Kim) /Subject (Links) /Keywords (pdf, links) /Creator (Writer 1.0) " +
            "/CreationDate (D:20240101120000Z) >>",
        ], "/Info 15 0 R");

    private static int? PageIndex(PdfDocument document, PdfItem? item) =>
        V(item) is PdfDictionary { Reference: { } reference } page && page.Elements.GetName("/Type") == "/Page"
            ? Enumerable.Range(0, document.PageCount).Cast<int?>().FirstOrDefault(i => document.Pages[i!.Value].Reference!.ObjectID == reference.ObjectID)
            : null;

    /// <summary>
    /// The same value in both PDFs: references stay references to the corresponding object (a page by its number),
    /// streams compare by their decoded data, /F is left to the caller.
    /// </summary>
    private static void AssertSameValue(PdfDocument original, PdfDocument copy, PdfItem? expected, PdfItem? actual,
        HashSet<(object, object)> seen, string at)
    {
        if (expected is PdfReference || actual is PdfReference)
        {
            Assert.True(expected is PdfReference && actual is PdfReference, at);
            if (PageIndex(original, expected) is { } page)
            {
                Assert.Equal(page, PageIndex(copy, actual));
                return;
            }
            expected = V(expected);
            actual = V(actual);
            if (!seen.Add((expected!, actual!)))
                return;
        }
        switch (expected)
        {
            case PdfDictionary dictionary:
                var other = Assert.IsAssignableFrom<PdfDictionary>(actual);
                string[] skipped = dictionary.Stream is null ? ["/F"] : ["/F", "/Length", "/Filter", "/DecodeParms"];
                var keys = dictionary.Elements.Keys.Except(skipped).Order().ToList();
                Assert.Equal(keys, other.Elements.Keys.Except(skipped).Order().ToList());
                foreach (var key in keys)
                    AssertSameValue(original, copy, dictionary.Elements[key], other.Elements[key], seen, at + key);
                if (dictionary.Stream is not null)
                    Assert.Equal(dictionary.Stream.UnfilteredValue, other.Stream!.UnfilteredValue);
                break;
            case PdfArray array:
                var items = Assert.IsType<PdfArray>(actual);
                Assert.Equal(array.Elements.Count, items.Elements.Count);
                for (var i = 0; i < array.Elements.Count; i++)
                    AssertSameValue(original, copy, array.Elements[i], items.Elements[i], seen, $"{at}[{i}]");
                break;
            case PdfInteger or PdfIntegerObject or PdfReal or PdfRealObject:
                Assert.Equal(Number(expected), Number(actual));
                break;
            case PdfString or PdfStringObject:
                Assert.Equal(Text(expected), Text(actual));
                break;
            default:
                Assert.Equal(expected?.ToString(), actual?.ToString());
                break;
        }
    }

    private static Dictionary<string, PdfItem?> FlagsByName(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return Enumerable.Range(0, document.PageCount).SelectMany(i => Annotations(document.Pages[i])).ToDictionary(a => Text(a.Elements["/NM"])!, a => V(a.Elements["/F"]));
    }

    [Fact]
    public void Visible_annotations_get_the_print_flag_and_nothing_else()
    {
        var dir = fx.NewFolder();
        var input = OnePage(Path.Combine(dir, "flags.pdf"),
        [
            UriLink("plain"), UriLink("zero", "/F 0"), UriLink("nozoom", "/F 8"),
            Annot("Highlight", "highlight", "/QuadPoints [100 120 200 120 100 100 200 100]"),
            Annot("Ink", "ink", "/InkList [[100 100 150 110 200 120]]"),
            Annot("Line", "line", "/L [100 100 200 120]"),
            UriLink("invisible", "/F 1"), UriLink("hidden", "/F 2"), UriLink("noview", "/F 32"), UriLink("togglenoview", "/F 256"),
            UriLink("invisible-print", "/F 5"), UriLink("hidden-print", "/F 6"), UriLink("noview-print", "/F 36"),
            UriLink("togglenoview-print", "/F 260"),
            Annot("Popup", "popup"), Annot("Popup", "popup-flagged", "/F 0"),
            Annot("Widget", "widget", "/FT /Btn /T (one)"), Annot("Widget", "widget-flagged", "/FT /Btn /T (two) /F 0"),
            UriLink("real", "/F 0.5"),
        ]);

        var copy = PdfPrintableAnnotations.Prepare(input, dir, Ct);

        Assert.NotNull(copy);
        Assert.Equal(Path.Combine(dir, "printable.pdf"), copy);
        var flags = FlagsByName(copy);
        foreach (var name in (string[])["plain", "zero", "highlight", "ink", "line"])
            Assert.True(Flag(flags[name]) == 4, name);
        Assert.Equal(12, Flag(flags["nozoom"])); // NoZoom stays
        foreach (var (name, value) in (ValueTuple<string, int>[])[("invisible", 1), ("hidden", 2), ("noview", 32), ("togglenoview", 256),
                     ("invisible-print", 5), ("hidden-print", 6), ("noview-print", 36), ("togglenoview-print", 260)])
            Assert.True(Flag(flags[name]) == value, name);
        Assert.Null(flags["popup"]);
        Assert.Equal(0, Flag(flags["popup-flagged"]));
        Assert.Null(flags["widget"]);
        Assert.Equal(0, Flag(flags["widget-flagged"]));
        Assert.Equal(0.5, Assert.IsType<PdfReal>(flags["real"]).Value); // not a whole number: left alone
    }

    [Fact]
    public void The_copy_keeps_everything_but_the_print_flags()
    {
        var dir = fx.NewFolder();
        var input = AnnotatedLetter(dir);

        var path = PdfPrintableAnnotations.Prepare(input, fx.NewFolder(), Ct);

        Assert.NotNull(path);
        using var original = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        using var copy = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(original.PageCount, copy.PageCount);
        var seen = new HashSet<(object, object)>(); // reference pairs already compared: popup and note point at each other
        for (var i = 0; i < original.PageCount; i++)
        {
            var (before, after) = (original.Pages[i], copy.Pages[i]);
            foreach (var key in (string[])["/MediaBox", "/CropBox", "/Rotate"])
                AssertSameValue(original, copy, before.Elements[key], after.Elements[key], seen, $"page {i + 1} {key}");
            Assert.Equal(Assert.IsAssignableFrom<PdfDictionary>(V(before.Elements["/Contents"])).Stream.UnfilteredValue,
                Assert.IsAssignableFrom<PdfDictionary>(V(after.Elements["/Contents"])).Stream.UnfilteredValue);

            var beforeItems = Assert.IsType<PdfArray>(V(before.Elements["/Annots"])).Elements;
            var afterItems = Assert.IsType<PdfArray>(V(after.Elements["/Annots"])).Elements;
            Assert.Equal(beforeItems.Count, afterItems.Count);
            for (var j = 0; j < beforeItems.Count; j++)
                AssertSameValue(original, copy, beforeItems[j], afterItems[j], seen, $"page {i + 1} annotation {j}");
        }

        var annotations = Annotations(copy.Pages[0]);
        Assert.Equal(["text", "popup", "reply", "highlight", "ink", "shared", "goto"], annotations.Select(a => Text(a.Elements["/NM"])));
        var byName = annotations.ToDictionary(a => Text(a.Elements["/NM"])!);
        Assert.Equal([4, null, 4, 4, 4, 4, 4], annotations.Select(a => Flag(a.Elements["/F"])));
        Assert.Same(byName["popup"], V(byName["text"].Elements["/Popup"]));
        Assert.Same(byName["text"], V(byName["popup"].Elements["/Parent"]));
        Assert.Same(byName["text"], V(byName["reply"].Elements["/IRT"]));
        Assert.Same(byName["shared"], Assert.Single(Annotations(copy.Pages[1]))); // one object on both pages, as before
        var destination = Assert.IsType<PdfArray>(V(byName["goto"].Elements["/Dest"]));
        Assert.Equal(1, PageIndex(copy, destination.Elements[0]));
        Assert.Equal("/XYZ", destination.Elements[1].ToString());
        Assert.Equal([72, 500, 1.5], destination.Elements.Skip(2).Select(Number));

        foreach (var key in (string[])["/Title", "/Author", "/Subject", "/Keywords", "/Creator", "/CreationDate"])
            Assert.Equal(Text(original.Info.Elements[key]), Text(copy.Info.Elements[key]));
    }

    [Fact]
    public void A_copy_of_a_pdf_without_a_creator_names_none()
    {
        var dir = fx.NewFolder();
        var bare = OnePage(Path.Combine(dir, "bare.pdf"), [UriLink("plain")]);
        var titled = OnePage(Path.Combine(dir, "titled.pdf"), [UriLink("plain")], extra: ["<< /Title (Only a title) >>"], trailer: "/Info 6 0 R");
        Assert.DoesNotContain("/Info", File.ReadAllText(bare, Encoding.Latin1));
        Assert.Contains("/Title", File.ReadAllText(titled, Encoding.Latin1));
        Assert.DoesNotContain("/Creator", File.ReadAllText(titled, Encoding.Latin1));

        foreach (var (input, title) in (ValueTuple<string, string?>[])[(bare, null), (titled, "Only a title")])
        {
            var path = PdfPrintableAnnotations.Prepare(input, fx.NewFolder(), Ct);

            Assert.NotNull(path);
            using var copy = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var info = copy.Info.Elements;
            Assert.Equal("", Assert.IsType<PdfString>(info["/Creator"]).Value); // not PDFsharp
            Assert.Equal(title, Text(info["/Title"]));
            Assert.Equal(title is not null, info.ContainsKey("/Title"));
            foreach (var key in (string[])["/Author", "/Subject", "/Keywords"])
                Assert.False(info.ContainsKey(key), key);
        }
    }

    [Fact]
    public void The_input_is_never_changed_and_without_annotations_to_fix_no_copy_is_made()
    {
        var dir = fx.NewFolder();
        var letter = AnnotatedLetter(dir);
        var bytes = File.ReadAllBytes(letter);
        Assert.NotNull(PdfPrintableAnnotations.Prepare(letter, fx.NewFolder(), Ct));
        Assert.Equal(bytes, File.ReadAllBytes(letter));

        string[] inputs =
        [
            OnePage(Path.Combine(dir, "none.pdf"), []),
            OnePage(Path.Combine(dir, "printing.pdf"), [UriLink("print", "/F 4"), UriLink("print-nozoom", "/F 12")]),
            OnePage(Path.Combine(dir, "hidden.pdf"), [UriLink("hidden", "/F 2"), Annot("Popup", "popup"), Annot("Widget", "widget", "/FT /Btn /T (w)")]),
        ];
        foreach (var input in inputs)
        {
            var before = File.ReadAllBytes(input);
            var folder = fx.NewFolder();

            Assert.Null(PdfPrintableAnnotations.Prepare(input, folder, Ct));
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
            Assert.Equal(before, File.ReadAllBytes(input));
        }
    }

    /// <summary>A PDF with one link to fix, locked with an owner password (and a user password when given).</summary>
    private static string Locked(string folder, string name, string? userPassword)
    {
        var path = Path.Combine(folder, name);
        using var document = new PdfDocument();
        var page = document.AddPage();
        var link = new PdfDictionary(document);
        document.Internals.AddObject(link);
        link.Elements["/Type"] = new PdfName("/Annot");
        link.Elements["/Subtype"] = new PdfName("/Link");
        link.Elements["/Rect"] = new PdfArray(document, new PdfInteger(10), new PdfInteger(10), new PdfInteger(30), new PdfInteger(30));
        page.Elements["/Annots"] = new PdfArray(document, link.Reference!);
        document.SecuritySettings.OwnerPassword = "owner";
        if (userPassword is not null)
            document.SecuritySettings.UserPassword = userPassword;
        document.SecuritySettings.PermitModifyDocument = false;
        document.Save(path);
        return path;
    }

    /// <summary>65 page tree nodes, one inside the other, above a page with a link to fix.</summary>
    private static string DeepTree(string path)
    {
        const int levels = 65;
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        for (var level = 0; level < levels; level++)
        {
            var number = level + 2;
            var parent = level == 0 ? "" : $"/Parent {number - 1} 0 R";
            objects.Add($"<< /Type /Pages /Kids [{number + 1} 0 R] /Count 1 {parent} >>");
        }
        objects.Add($"<< /Type /Page /Parent {levels + 1} 0 R /MediaBox [0 0 612 792] /Annots [{levels + 3} 0 R] >>");
        objects.Add(UriLink("deep"));
        return RawPdf.Write(path, objects);
    }

    [Fact]
    public void A_pdf_that_cannot_be_copied_goes_to_ghostscript_as_it_is()
    {
        var dir = fx.NewFolder();
        var broken = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.7 not really");
        string[] inputs =
        [
            broken,
            Locked(dir, "owner.pdf", userPassword: null),
            Locked(dir, "user.pdf", userPassword: "user"),
            RawPdf.Write(Path.Combine(dir, "loop.pdf"), ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [2 0 R] /Count 1 >>"]),
            DeepTree(Path.Combine(dir, "deep.pdf")),
        ];
        foreach (var input in inputs)
        {
            var before = File.ReadAllBytes(input);
            var folder = fx.NewFolder();

            Assert.Null(PdfPrintableAnnotations.Prepare(input, folder, Ct));
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
            Assert.Equal(before, File.ReadAllBytes(input));
        }
    }

    [Fact]
    public void A_copy_that_cannot_be_saved_is_given_up()
    {
        var dir = fx.NewFolder();
        var input = OnePage(Path.Combine(dir, "link.pdf"), [UriLink("plain")]);
        var folder = fx.NewFolder();
        var blocked = Directory.CreateDirectory(Path.Combine(folder, "printable.pdf")).FullName; // where the copy would go

        Assert.Null(PdfPrintableAnnotations.Prepare(input, folder, Ct));
        Assert.Equal([blocked], Directory.EnumerateFileSystemEntries(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(blocked));
        Assert.Equal(input, GhostscriptConverter.PdfAInput(input, folder, Ct));
    }

    [Fact]
    public void Preparing_the_copy_stops_when_cancelled()
    {
        var dir = fx.NewFolder();
        var input = OnePage(Path.Combine(dir, "link.pdf"), [UriLink("plain")]);

        Assert.ThrowsAny<OperationCanceledException>(() => PdfPrintableAnnotations.Prepare(input, fx.NewFolder(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Ghostscript_reads_the_copy_only_when_one_was_made()
    {
        var dir = fx.NewFolder();
        var printing = OnePage(Path.Combine(dir, "printing.pdf"), [UriLink("print", "/F 4")]);
        var broken = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.7 not really");
        var link = OnePage(Path.Combine(dir, "link.pdf"), [UriLink("plain")]);

        Assert.Equal(printing, GhostscriptConverter.PdfAInput(printing, fx.NewFolder(), Ct));
        Assert.Equal(broken, GhostscriptConverter.PdfAInput(broken, fx.NewFolder(), Ct));
        var folder = fx.NewFolder();
        Assert.Equal(Path.Combine(folder, "printable.pdf"), GhostscriptConverter.PdfAInput(link, folder, Ct));
    }

    /// <summary>Two pages with links of every kind and markup, none of which says it prints.</summary>
    private static string LinkedLetter(string folder) =>
        RawPdf.Write(Path.Combine(folder, "linked.pdf"),
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Annots [7 0 R 8 0 R 9 0 R 10 0 R 11 0 R 12 0 R 13 0 R 14 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Annots [12 0 R] >>",
            RawPdf.Stream("", "0 0 1 rg 72 600 200 100 re f"),
            RawPdf.Stream("", "1 0 0 rg 72 72 100 100 re f"),
            "<< /Type /Annot /Subtype /Link /Rect [72 700 200 720] /A << /S /URI /URI (https://example.com/uri) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 670 200 690] /Dest [4 0 R /XYZ 72 500 1.5] >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 640 200 660] /F 0 /A << /S /URI /URI (https://example.com/zero) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 610 200 630] /F 8 /A << /S /URI /URI (https://example.com/nozoom) >> >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [70 590 280 710] /QuadPoints [72 700 272 700 72 600 272 600] /C [1 1 0] >>",
            "<< /Type /Annot /Subtype /Link /Rect [400 100 500 120] /A << /S /URI /URI (https://example.com/shared) >> >>",
            "<< /Type /Annot /Subtype /Ink /Rect [300 300 400 400] /InkList [[300 300 350 380 400 320]] /C [1 0 0] >>",
            "<< /Type /Annot /Subtype /Line /Rect [300 450 400 500] /L [300 460 400 490] /C [0 0 1] >>",
        ]);

    private static string? Uri(PdfDictionary annotation) =>
        V(annotation.Elements["/A"]) is PdfDictionary action ? Text(action.Elements["/URI"]) : null;

    /// <summary>A link's destination: its /Dest, or the /D of a go-to action.</summary>
    private static PdfArray? Destination(PdfDictionary annotation) =>
        V(annotation.Elements["/Dest"]) as PdfArray
        ?? (V(annotation.Elements["/A"]) is PdfDictionary action ? V(action.Elements["/D"]) as PdfArray : null);

    [Fact]
    public async Task Links_and_markup_without_the_print_flag_stay_in_the_pdf_a()
    {
        Assert.SkipUnless(GhostscriptInstalled, GhostscriptMissing);
        var dir = fx.NewFolder();
        var source = LinkedLetter(dir);
        var bytes = File.ReadAllBytes(source);

        var job = await fx.ConvertAsync([source], BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.PdfAId));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.True(GhostscriptConverter.DeclaresPdfA(output));
        AssertOutputIntent(output);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        using var before = PdfReader.Open(source, PdfDocumentOpenMode.Import);
        using var after = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, after.PageCount);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(Annotations(before.Pages[i]).Select(a => a.Elements.GetName("/Subtype")).Order(),
                Annotations(after.Pages[i]).Select(a => a.Elements.GetName("/Subtype")).Order());
        }
        var annotations = Enumerable.Range(0, after.PageCount).SelectMany(i => Annotations(after.Pages[i])).ToList();
        Assert.All(annotations, a => Assert.True(Flag(a.Elements["/F"]) is { } flags && (flags & 4) != 0, a.Elements.GetName("/Subtype")));
        Assert.Equal(["https://example.com/nozoom", "https://example.com/shared", "https://example.com/shared", "https://example.com/uri", "https://example.com/zero"],
            annotations.Select(Uri).OfType<string>().Order());
        Assert.Equal(12, Flag(annotations.Single(a => Uri(a) == "https://example.com/nozoom").Elements["/F"]));
        var destination = Assert.Single(annotations.Select(Destination).OfType<PdfArray>());
        Assert.Equal(1, PageIndex(after, destination.Elements[0]));
        Assert.Equal("/XYZ", destination.Elements[1].ToString());
        Assert.Equal([72, 500, 1.5], destination.Elements.Skip(2).Select(Number));
    }

    private const string Xmp = """
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
        <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:xmp="http://ns.adobe.com/xap/1.0/"
         xmlns:filee="https://example.com/filee-test/">
        <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Full letter</rdf:li></rdf:Alt></dc:title>
        <dc:creator><rdf:Seq><rdf:li>Lee</rdf:li></rdf:Seq></dc:creator>
        <xmp:CreatorTool>Writer 2.0</xmp:CreatorTool>
        <filee:Project>Archive</filee:Project>
        </rdf:Description></rdf:RDF></x:xmpmeta>
        <?xpacket end="w"?>
        """;

    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace XmpBasic = "http://ns.adobe.com/xap/1.0/";

    /// <summary>An XMP property's text (the items of a list or alternative joined), or null when it is missing.</summary>
    private static string? XmpValue(string xmp, XName name)
    {
        var packet = XDocument.Parse(xmp[xmp.IndexOf('<', StringComparison.Ordinal)..]);
        if (packet.Descendants(name).FirstOrDefault() is { } element)
            return string.Join("|", element.Descendants().Where(e => !e.HasElements).Select(e => e.Value).DefaultIfEmpty(element.Value));
        return packet.Descendants().Attributes(name).FirstOrDefault()?.Value;
    }

    private static (Dictionary<string, string?> Fields, string Everything) DocumentInformation(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var info = document.Info.Elements;
        var xmp = Encoding.UTF8.GetString(Assert.IsAssignableFrom<PdfDictionary>(V(document.Internals.Catalog.Elements["/Metadata"])).Stream.UnfilteredValue);
        var fields = new Dictionary<string, string?>
        {
            ["Title"] = Text(info["/Title"]),
            ["Author"] = Text(info["/Author"]),
            ["Subject"] = Text(info["/Subject"]),
            ["Keywords"] = Text(info["/Keywords"]),
            ["Creator"] = Text(info["/Creator"]),
            ["dc:title"] = XmpValue(xmp, Dc + "title"),
            ["dc:creator"] = XmpValue(xmp, Dc + "creator"),
            ["xmp:CreatorTool"] = XmpValue(xmp, XmpBasic + "CreatorTool"),
        };
        return (fields, string.Join("\n", info.Keys.Select(key => Text(info[key]))) + "\n" + xmp);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("no-creator")]
    [InlineData("no-info")]
    public async Task The_pdf_a_keeps_the_document_information_ghostscript_would_write_without_the_copy(string kind)
    {
        Assert.SkipUnless(GhostscriptInstalled, GhostscriptMissing);
        var gs = GhostscriptConverter.Locate()!;
        var dir = fx.NewFolder();
        string Input(string name, string flags)
        {
            var stream = RawPdf.Stream("/Type /Metadata /Subtype /XML", Xmp);
            return kind switch
            {
                "full" => OnePage(Path.Combine(dir, name), [UriLink("plain", flags)], catalog: "/Metadata 6 0 R",
                    extra: [stream, "<< /Title (Full letter) /Author (Lee) /Subject (Links) /Keywords (pdf, links) /Creator (Writer 2.0) >>"],
                    trailer: "/Info 7 0 R"),
                "no-creator" => OnePage(Path.Combine(dir, name), [UriLink("plain", flags)], extra: ["<< /Title (Half letter) /Author (Lee) >>"],
                    trailer: "/Info 6 0 R"),
                _ => OnePage(Path.Combine(dir, name), [UriLink("plain", flags)]),
            };
        }
        var test = Input("test.pdf", "");
        var control = Input("control.pdf", "/F 4");
        Assert.NotNull(PdfPrintableAnnotations.Prepare(test, fx.NewFolder(), Ct));
        Assert.Null(PdfPrintableAnnotations.Prepare(control, fx.NewFolder(), Ct)); // Ghostscript reads it as it is

        var outputs = new List<(Dictionary<string, string?> Fields, string Everything)>();
        foreach (var input in (string[])[test, control])
        {
            var output = Path.Combine(fx.NewFolder(), "out.pdf");
            await GhostscriptConverter.ConvertToPdfAAsync(gs, input, output, fx.NewFolder(), Ct);
            outputs.Add(DocumentInformation(output));
        }

        var (withCopy, without) = (outputs[0].Fields, outputs[1].Fields);
        if (kind == "full")
        {
            Assert.Equal(without, withCopy);
            Assert.Equal("Writer 2.0", withCopy["xmp:CreatorTool"]);
        }
        else
        {
            // Ghostscript fills in a placeholder for a missing creator; with the copy it stays empty.
            Assert.True(string.IsNullOrEmpty(withCopy["xmp:CreatorTool"]), withCopy["xmp:CreatorTool"]);
            Assert.True(string.IsNullOrEmpty(withCopy["Creator"]), withCopy["Creator"]);
            foreach (var key in without.Keys.Except(["Creator", "xmp:CreatorTool"]))
                Assert.True(without[key] == withCopy[key], $"{key}: {without[key]} / {withCopy[key]}");
        }
        Assert.DoesNotContain("PDFsharp", outputs[0].Everything, StringComparison.OrdinalIgnoreCase);
    }

    private static string DeclaredPdfA(string path) =>
        OnePage(path, [UriLink("plain")], catalog: "/Metadata 6 0 R", extra:
        [
            RawPdf.Stream("/Type /Metadata /Subtype /XML", """
                <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
                <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" pdfaid:part="2" pdfaid:conformance="B"/>
                </rdf:RDF></x:xmpmeta>
                <?xpacket end="w"?>
                """),
        ]);

    [Fact]
    public async Task An_unchanged_pdf_that_says_it_is_pdf_a_is_left_alone_links_and_all()
    {
        var dir = fx.NewFolder();
        var path = DeclaredPdfA(Path.Combine(dir, "declared.pdf"));
        Assert.True(GhostscriptConverter.DeclaresPdfA(path));
        var bytes = File.ReadAllBytes(path);
        var work = fx.NewFolder();

        Assert.True(await new GhostscriptConverter().ArchiveAsync(path, changed: false, work, Ct));

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public async Task A_changed_pdf_that_says_it_is_pdf_a_is_rewritten_with_its_links()
    {
        Assert.SkipUnless(GhostscriptInstalled, GhostscriptMissing);
        var dir = fx.NewFolder();
        var path = DeclaredPdfA(Path.Combine(dir, "declared.pdf"));

        Assert.True(await new GhostscriptConverter().ArchiveAsync(path, changed: true, fx.NewFolder(), Ct));

        AssertOutputIntent(path); // Ghostscript's
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal("https://example.com/plain", Uri(Assert.Single(Annotations(document.Pages[0]))));
    }
}
