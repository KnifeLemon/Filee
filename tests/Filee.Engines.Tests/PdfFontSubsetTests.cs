// "Compress PDF" re-subsets embedded fonts and packs objects into object streams. The PDFs are built here, with the
// test fonts embedded whole: a Type 0 font with TrueType outlines, a Type 0 font with CID-keyed CFF outlines
// (FontFile3/OpenType) and a simple TrueType font with WinAnsiEncoding. Each draws a few of its glyphs, and the pages
// must look the same after compression while the font programs shrink.

using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Fonts;
using Filee.Engines.Fonts.Cff;
using Filee.Engines.Pdf;
using Filee.Engines.Tests.RenderQuality;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class PdfFontSubsetTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static byte[] FontBytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", name));

    private static Preset Compress() => new()
    {
        TargetFormat = "pdf",
        Pdf = { Compression = PdfCompression.Ebook },
        Output = { FileNamePattern = "{name}_compressed" },
    };

    /// <summary>
    /// A page drawing "Filee Á" with a whole TrueType font twice (as a Type 0 font and as a simple font) and "한글" with a
    /// whole CID-keyed CFF font. With <paramref name="formFont"/>, the simple font is also the interactive form's.
    /// </summary>
    internal static string FontsPdf(string folder, bool formFont = false, byte[]? trueType = null, byte[]? openType = null)
    {
        trueType ??= FontBytes("FileeTest-TrueType.ttf");
        openType ??= FontBytes("FileeTest-CID.otf");
        var ttf = SfntFont.Read(trueType);
        var ttfMap = CmapTable.Read(ttf.Require("cmap"), out _);
        var otf = SfntFont.Read(openType);
        var otfMap = CmapTable.Read(otf.Require("cmap"), out _);
        var charset = CffFont.Parse(otf.Require("CFF ")).Charset!;

        using var document = new PdfDocument();
        var page = document.AddPage();
        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = Type0(document, "/CIDFontType2", "/FontFile2", null, trueType).Reference;
        fonts.Elements["/F2"] = Type0(document, "/CIDFontType0", "/FontFile3", "/OpenType", openType).Reference;
        var simple = Simple(document, trueType);
        fonts.Elements["/F3"] = simple.Reference;
        var resources = new PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = resources;

        var text = "Filee Á";
        var cids = Hex("한글".Select(c => (int)charset[otfMap[c]]));
        var gids = Hex(text.Select(c => ttfMap[c]));
        var content = $"BT /F1 36 Tf 72 700 Td <{gids}> Tj ET\nBT /F2 36 Tf 72 620 Td <{cids}> Tj ET\nBT /F3 36 Tf 72 540 Td ({text}) Tj ET\n";
        var stream = new PdfDictionary(document);
        stream.CreateStream(Encoding.Latin1.GetBytes(content));
        document.Internals.AddObject(stream);
        page.Elements["/Contents"] = stream.Reference;

        if (formFont)
        {
            var form = new PdfDictionary(document);
            var defaults = new PdfDictionary(document);
            var formFonts = new PdfDictionary(document);
            formFonts.Elements["/Helv"] = simple.Reference;
            defaults.Elements["/Font"] = formFonts;
            form.Elements["/DR"] = defaults;
            form.Elements["/Fields"] = new PdfArray(document);
            document.Internals.Catalog.Elements["/AcroForm"] = form;
        }
        var path = Path.Combine(folder, "fonts.pdf");
        document.Save(path);
        return path;

        static string Hex(IEnumerable<int> codes) => string.Concat(codes.Select(c => c.ToString("X4")));
    }

    private static PdfDictionary Type0(PdfDocument document, string cidType, string fileKey, string? fileSubtype, byte[] program)
    {
        var cidFont = Dictionary(document, ("/Type", "/Font"), ("/Subtype", cidType), ("/BaseFont", "/FileeTest"));
        var info = new PdfDictionary(document);
        info.Elements["/Registry"] = new PdfString("Adobe");
        info.Elements["/Ordering"] = new PdfString("Identity");
        info.Elements.SetInteger("/Supplement", 0);
        cidFont.Elements["/CIDSystemInfo"] = info;
        cidFont.Elements["/FontDescriptor"] = Descriptor(document, fileKey, fileSubtype, program, symbolic: true).Reference;
        if (cidType == "/CIDFontType2")
            cidFont.Elements.SetName("/CIDToGIDMap", "/Identity");
        var font = Dictionary(document, ("/Type", "/Font"), ("/Subtype", "/Type0"), ("/BaseFont", "/FileeTest"), ("/Encoding", "/Identity-H"));
        font.Elements["/DescendantFonts"] = new PdfArray(document, cidFont.Reference!);
        return font;
    }

    private static PdfDictionary Simple(PdfDocument document, byte[] program)
    {
        var font = Dictionary(document, ("/Type", "/Font"), ("/Subtype", "/TrueType"), ("/BaseFont", "/FileeTest"), ("/Encoding", "/WinAnsiEncoding"));
        font.Elements.SetInteger("/FirstChar", 32);
        font.Elements.SetInteger("/LastChar", 255);
        var widths = new PdfArray(document);
        for (var c = 32; c <= 255; c++)
            widths.Elements.Add(new PdfInteger(600));
        font.Elements["/Widths"] = widths;
        font.Elements["/FontDescriptor"] = Descriptor(document, "/FontFile2", null, program, symbolic: false).Reference;
        return font;
    }

    private static PdfDictionary Descriptor(PdfDocument document, string fileKey, string? fileSubtype, byte[] program, bool symbolic)
    {
        var file = new PdfDictionary(document);
        file.CreateStream(program);
        if (fileSubtype is not null)
            file.Elements.SetName("/Subtype", fileSubtype);
        else
            file.Elements.SetInteger("/Length1", program.Length);
        document.Internals.AddObject(file);
        var descriptor = Dictionary(document, ("/Type", "/FontDescriptor"), ("/FontName", "/FileeTest"));
        descriptor.Elements.SetInteger("/Flags", symbolic ? 4 : 32);
        descriptor.Elements["/FontBBox"] = new PdfArray(document, new PdfInteger(-500), new PdfInteger(-300), new PdfInteger(1500), new PdfInteger(1100));
        descriptor.Elements.SetInteger("/ItalicAngle", 0);
        descriptor.Elements.SetInteger("/Ascent", 1000);
        descriptor.Elements.SetInteger("/Descent", -250);
        descriptor.Elements.SetInteger("/CapHeight", 700);
        descriptor.Elements.SetInteger("/StemV", 80);
        descriptor.Elements[fileKey] = file.Reference;
        return descriptor;
    }

    private static PdfDictionary Dictionary(PdfDocument document, params (string Key, string Name)[] names)
    {
        var dictionary = new PdfDictionary(document);
        foreach (var (key, name) in names)
            dictionary.Elements.SetName(key, name);
        document.Internals.AddObject(dictionary);
        return dictionary;
    }

    /// <summary>The decoded length of every embedded font program.</summary>
    private static List<int> Programs(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return [.. document.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Elements.GetName("/Type") == "/FontDescriptor")
            .Select(d => d.Elements.GetDictionary("/FontFile2") ?? d.Elements.GetDictionary("/FontFile3"))
            .OfType<PdfDictionary>()
            .Select(f => f.Stream.UnfilteredValue.Length)
            .Order()];
    }

    [Fact]
    public async Task Embedded_fonts_keep_only_the_glyphs_the_page_draws()
    {
        var dir = fx.NewFolder();
        var source = FontsPdf(dir);

        var job = await fx.ConvertAsync([source], Compress());

        var output = job.Outputs.Single();
        var before = Programs(source);
        var after = Programs(output);
        Assert.Equal(3, after.Count);
        Assert.All(after.Zip(before), p => Assert.True(p.First < p.Second * 0.8, $"{p.First} of {p.Second} bytes"));
        var (ssim, off) = Ssim.Compare(GreyImage.Pages(File.ReadAllBytes(source), 72).Single(), GreyImage.Pages(File.ReadAllBytes(output), 72).Single());
        Assert.True(ssim > 0.999 && off == 0, $"SSIM {ssim}, {off:P2} of pixels differ");

        // Fonts embedded whole get a subset tag.
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var names = document.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Elements.GetName("/Type") == "/Font").Select(d => d.Elements.GetName("/BaseFont")).ToList();
        Assert.Equal(5, names.Count); // three fonts, two of them with a descendant
        Assert.All(names, name => Assert.Matches("^/[A-Z]{6}\\+FileeTest$", name));
    }

    [Fact]
    public async Task A_font_the_form_types_with_stays_whole()
    {
        var dir = fx.NewFolder();
        var source = FontsPdf(dir, formFont: true);

        var job = await fx.ConvertAsync([source], Compress());

        var after = Programs(job.Outputs.Single());
        Assert.Contains(FontBytes("FileeTest-TrueType.ttf").Length, after);
    }

    [Fact]
    public void Cff_subsets_keep_glyph_ids_and_empty_the_rest()
    {
        foreach (var name in new[] { "FileeTest-CID.otf", "FileeTest-NameKeyed.otf" })
        {
            var cff = SfntFont.Read(FontBytes(name)).Require("CFF ");
            var original = CffFont.Parse(cff);
            var keep = original.GlyphCount - 1;

            var subset = CffFont.Parse(CffSubsetter.Subset(cff, [keep]));

            Assert.Equal(original.GlyphCount, subset.GlyphCount);
            Assert.Equal(original.IsCid, subset.IsCid);
            Assert.Equal(original.Charset, subset.Charset);
            Assert.Equal(original.CharStrings[keep], subset.CharStrings[keep]);
            Assert.Equal(original.CharStrings[0], subset.CharStrings[0]);
            Assert.All(Enumerable.Range(1, original.GlyphCount - 2), g => Assert.Equal([14], subset.CharStrings[g]));
            var drawn = new Type2Interpreter(subset).Run(keep);
            var expected = new Type2Interpreter(original).Run(keep);
            Assert.Equal(expected.Sum(c => c.Segments.Count), drawn.Sum(c => c.Segments.Count));
        }
    }

    [Fact]
    public void Objects_go_into_object_streams_and_read_back()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "pages.pdf");
        using (var document = new PdfDocument())
        {
            for (var i = 0; i < 40; i++)
            {
                var page = document.AddPage();
                page.Elements["/Annots"] = new PdfArray(document);
            }
            document.Info.Title = "Forty pages";
            document.Save(source);
        }
        var packed = Path.Combine(dir, "packed.pdf");
        File.Copy(source, packed);

        Assert.True(PdfObjectStreams.Rewrite(packed));

        Assert.True(new FileInfo(packed).Length < new FileInfo(source).Length);
        Assert.Contains("/ObjStm", Encoding.Latin1.GetString(File.ReadAllBytes(packed)));
        using (var document = PdfReader.Open(packed, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(40, document.PageCount);
            Assert.Equal("Forty pages", document.Info.Title);
        }
        using var pig = UglyToad.PdfPig.PdfDocument.Open(packed);
        Assert.Equal(40, pig.NumberOfPages);
    }
}
