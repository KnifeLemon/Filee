// The pictures of embedded OLE objects (Hwp/Binary/OlePreview.cs) and the HWPX reader drawing an OLE object as its
// picture; 한글 charts kept as OLE objects in HWP and HWPX files, drawn from their DrawingML part (OOXMLChartContents)
// or, for 한글 Neo's older chart without it, from their picture and named as a chart drawn as a picture. The objects are compound files built here with OpenMcdf, the way 한글 keeps them in BinData: a 4-byte
// length, then the compound file with the presentation stream its program left.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Metafiles;
using OpenMcdf;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class OlePreviewTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A 96 × 48 unit WMF without the placeable header, as OLE pictures are: a blue rectangle.</summary>
    private static byte[] Wmf() => new WmfBuilder(0, 0)
        .Add(0x0103, 8) // SETMAPMODE MM_ANISOTROPIC
        .Add(0x020C, -48, 96) // SETWINDOWEXT (y, x)
        .Brush(0xFF0000).Select(0)
        .Rectangle(0, 0, 96, -48)
        .ToArray(placeable: false);

    /// <summary>A 2 × 1 pixel 24-bit DIB (BITMAPINFOHEADER and bits): red, blue.</summary>
    private static byte[] Dib()
    {
        var dib = new byte[40 + 8];
        EmfBuilder.Dib(2, 1, 24).CopyTo(dib, 0);
        byte[] bits = [0, 0, 255, 255, 0, 0, 0, 0]; // BGR red, BGR blue, padding to 4 bytes
        bits.CopyTo(dib, 40);
        return dib;
    }

    /// <summary>An OLEPresentationStream: a standard clipboard format, no target device, and the picture.</summary>
    private static byte[] Presentation(uint format, byte[] picture)
    {
        var stream = new byte[8 + 4 + 28 + picture.Length];
        var span = stream.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], format);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 4); // TargetDeviceSize: no device
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 1); // DVASPECT_CONTENT
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], -1); // Lindex
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 2); // Advf
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 2540); // Width (0.01 mm)
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], 1270); // Height
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], (uint)picture.Length);
        picture.CopyTo(stream, 40);
        return stream;
    }

    /// <summary>An OLE object as 한글's BinData holds it: its length, then a compound file with these streams.</summary>
    private string Ole(params (string Name, byte[] Data)[] streams)
    {
        var path = Path.Combine(fx.NewFolder(), "object.cfb");
        using (var root = RootStorage.Create(path, OpenMcdf.Version.V3, StorageModeFlags.None))
        {
            foreach (var (name, data) in streams)
            {
                using var stream = root.CreateStream(name);
                stream.Write(data);
            }
        }
        var cfb = File.ReadAllBytes(path);
        var ole = Path.ChangeExtension(path, ".ole");
        File.WriteAllBytes(ole, [.. BitConverter.GetBytes(cfb.Length), .. cfb]);
        return ole;
    }

    [Fact]
    public void A_metafile_picture_comes_out_as_a_wmf()
    {
        var ole = Ole(("\u0001CompObj", [1, 0]), ("\u0002OlePres000", Presentation(3, Wmf())));

        var preview = OlePreview.Extract(ole, ole + "-preview");

        Assert.EndsWith(".wmf", preview);
        using var drawing = Metafile.Load(preview!, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((72f, 36f), (drawing.Width, drawing.Height)); // 96 × 48 screen pixels
    }

    [Fact]
    public void A_bitmap_picture_or_a_paint_object_comes_out_as_a_bmp()
    {
        var dib = OlePreview.Extract(Ole(("\u0002OlePres000", Presentation(8, Dib()))), Path.Combine(fx.NewFolder(), "dib"));
        // A Paint object without a presentation stream: its native data is the BMP file.
        var bmp = new byte[14 + 48];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54);
        Dib().CopyTo(bmp, 14);
        var native = OlePreview.Extract(Ole(("\u0001Ole10Native", [.. BitConverter.GetBytes(bmp.Length), .. bmp])), Path.Combine(fx.NewFolder(), "native"));

        foreach (var path in new[] { dib, native })
        {
            Assert.EndsWith(".bmp", path);
            using var bitmap = SKBitmap.Decode(path);
            Assert.Equal((2, 1), (bitmap.Width, bitmap.Height));
            Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(1, 0));
        }
    }

    [Fact]
    public void An_object_without_a_picture_or_not_an_object_has_none()
    {
        var folder = fx.NewFolder();
        // A chart of 한글's own (only its "Contents"), a picture in a format named by a string, and plain bytes.
        Assert.Null(OlePreview.Extract(Ole(("Contents", [0, 0, 1, 0])), Path.Combine(folder, "a")));
        var named = Presentation(3, Wmf());
        BinaryPrimitives.WriteUInt32LittleEndian(named, 5); // an ANSI format name of 5 characters instead of 0xFFFFFFFF
        Assert.Null(OlePreview.Extract(Ole(("\u0002OlePres000", named)), Path.Combine(folder, "b")));
        var junk = Path.Combine(folder, "junk.ole");
        File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("not a compound file at all"));
        Assert.Null(OlePreview.Extract(junk, Path.Combine(folder, "c")));
    }

    [Fact]
    public void The_objects_of_a_document_share_one_budget_and_are_read_once()
    {
        var ole = Ole(("\u0002OlePres000", Presentation(3, Wmf())));
        var copy = Path.Combine(fx.NewFolder(), "copy.ole");
        File.Copy(ole, copy);

        // What one object costs: its file and the streams read from it.
        var measure = new OlePreview.Budget(DocumentLimits.Default);
        Assert.NotNull(measure.Extract(ole, Path.Combine(fx.NewFolder(), "measure")));
        var cost = DocumentLimits.Default.MaxPackageBytes - measure.Left;
        Assert.True(cost >= new FileInfo(ole).Length);

        // A budget of exactly one object: the first has its picture, the next none, although each alone would fit.
        var budget = new OlePreview.Budget(DocumentLimits.Default with { MaxPackageBytes = cost });
        var folder = fx.NewFolder();
        var first = budget.Extract(ole, Path.Combine(folder, "first"));
        Assert.NotNull(first);
        Assert.Equal(0, budget.Left);
        Assert.Null(budget.Extract(copy, Path.Combine(folder, "second")));
        Assert.NotNull(OlePreview.Extract(copy, Path.Combine(folder, "alone")));

        // The same object named again gives the same picture without reading the object again.
        File.Delete(ole);
        Assert.Equal(first, budget.Extract(ole, Path.Combine(folder, "again")));
    }

    /// <summary>A CompObj stream (MS-OLEDS 2.3.8): the header, then the user type and program id as ANSI strings.</summary>
    private static byte[] CompObj(string userType, string programId)
    {
        static byte[] Ansi(string text) => [.. BitConverter.GetBytes(text.Length + 1), .. Encoding.ASCII.GetBytes(text), 0];
        return [.. new byte[28], .. Ansi(userType), .. BitConverter.GetBytes(0), .. Ansi(programId)];
    }

    /// <summary>
    /// An HWP whose one paragraph holds an OLE object (a gso control of kind $ole, 240 × 150 pt, treated as a character)
    /// stored in BinData/BIN0001.ole, uncompressed, as 한글 stores it.
    /// </summary>
    private string HwpWithOle(string ole)
    {
        var builder = new HwpBuilder().PlainDocInfo();
        builder.DocInfo.Add(HwpBuilder.Record(18, 1, HwpBuilder.Bytes((ushort)(2 | 2 << 4), (ushort)1, "ole")));
        builder.Streams["BinData/BIN0001.ole"] = File.ReadAllBytes(ole);
        builder.SectionStart();
        var section = builder.Section;
        section.Add(HwpBuilder.Record(66, 0, HwpBuilder.Bytes(9, 0, (ushort)0, (byte)0, (byte)0, (ushort)1, (ushort)0, (ushort)0, 0, (ushort)0)));
        section.Add(HwpBuilder.Record(67, 1, HwpBuilder.Bytes(HwpBuilder.Control(11, "gso "), (ushort)13)));
        section.Add(HwpBuilder.Record(71, 1, HwpBuilder.Bytes(HwpBuilder.Id("gso "), 1u, 0, 0, 24000, 15000, 0, (short)0, (short)0, (short)0, (short)0, 1, 0, "")));
        section.Add(HwpBuilder.Record(76, 2, HwpBuilder.Bytes(HwpBuilder.Id("$ole"), HwpBuilder.Id("$ole"), 0, 0, (ushort)0, (ushort)1, 24000, 15000, 24000, 15000, 0, (short)0,
            0, 0, (ushort)0, new byte[48])));
        section.Add(HwpBuilder.Record(84, 3, HwpBuilder.Bytes(1 | 1 << 16, 24000, 15000, (ushort)1, 0, 0, 0)));
        var path = Path.Combine(fx.NewFolder(), "chart.hwp");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static IEnumerable<HImage> Images(HDocument document) =>
        document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>();

    [Fact]
    public void A_hangul_chart_is_drawn_from_its_drawingml_part()
    {
        var ole = Ole(("\u0001CompObj", CompObj("Hwp Chart", "HwpChart.1")), ("Contents", [0, 0, 1, 0]),
            ("OOXMLChartContents", Encoding.UTF8.GetBytes(ChartBuilder.Columns())), ("\u0002OlePres000", Presentation(3, Wmf())));
        var content = OlePreview.Read(ole, ole + "-preview");
        Assert.True(content.IsChart);
        Assert.NotNull(content.ChartPart);

        var document = HwpReader.ReadForLayout(HwpWithOle(ole), fx.NewFolder());
        var image = Assert.Single(Images(document));
        Assert.NotNull(image.Chart);
        Assert.Equal(["North", "South", "West"], image.Chart!.Plots[0].Series.Select(s => s.Name));
        Assert.Equal((24000, 15000), (image.Width, image.Height));
        Assert.DoesNotContain(document.LeftOut.Kinds, k => k.Kind is HLeftOutKind.ChartPictured or HLeftOutKind.ObjectPictured or HLeftOutKind.Chart);
    }

    [Fact]
    public void A_chart_without_its_drawingml_part_is_drawn_from_its_picture_and_named()
    {
        // 한글 Neo's chart: only its own "Contents" (not read) and the picture it left.
        var neo = Ole(("\u0001CompObj", CompObj("Hwp Chart", "HwpChart.1")), ("Contents", [0, 0, 1, 0]), ("\u0002OlePres000", Presentation(3, Wmf())));
        var document = HwpReader.ReadForLayout(HwpWithOle(neo), fx.NewFolder());
        var image = Assert.Single(Images(document));
        Assert.Null(image.Chart);
        Assert.True(Metafile.IsMetafile(image.Path));
        Assert.Contains(document.LeftOut.Kinds, k => k.Kind == HLeftOutKind.ChartPictured);

        // A part that isn't a chart ChartReader draws: the picture again.
        var broken = Ole(("OOXMLChartContents", Encoding.UTF8.GetBytes("<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"/>")),
            ("\u0002OlePres000", Presentation(3, Wmf())));
        var fallback = HwpReader.ReadForLayout(HwpWithOle(broken), fx.NewFolder());
        Assert.Null(Assert.Single(Images(fallback)).Chart);
        Assert.Contains(fallback.LeftOut.Kinds, k => k.Kind == HLeftOutKind.ChartPictured);

        // Another program's object (no chart named) stays an object drawn as its picture.
        var paint = HwpReader.ReadForLayout(HwpWithOle(Ole(("\u0001CompObj", CompObj("Bitmap Image", "Paint.Picture")), ("\u0002OlePres000", Presentation(3, Wmf())))), fx.NewFolder());
        Assert.Contains(paint.LeftOut.Kinds, k => k.Kind == HLeftOutKind.ObjectPictured);

        // The HWP's OLE object keeps its BinData item in the HWPX it becomes.
        var hwpx = Path.Combine(fx.NewFolder(), "chart.hwpx");
        HwpReader.ToHwpx(HwpWithOle(neo), hwpx);
        using var zip = ZipFile.OpenRead(hwpx);
        using var reader = new StreamReader(zip.GetEntry("Contents/section0.xml")!.Open());
        Assert.Contains("binaryItemIDRef=\"ole1\"", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_renderer_keeps_hwp_ole_objects_it_draws_and_names_the_others()
    {
        var renderer = new Typesetting.FileeRendererConverter();
        // A chart with its DrawingML part, a chart or another object with a picture: drawn, so the renderer keeps the file.
        var chart = Ole(("\u0001CompObj", CompObj("Hwp Chart", "HwpChart.1")), ("OOXMLChartContents", Encoding.UTF8.GetBytes(ChartBuilder.Columns())));
        Assert.Null(renderer.FirstUnsupported(HwpWithOle(chart), "hwp"));
        var neo = Ole(("\u0001CompObj", CompObj("Hwp Chart", "HwpChart.1")), ("Contents", [0, 0, 1, 0]), ("\u0002OlePres000", Presentation(3, Wmf())));
        Assert.Null(renderer.FirstUnsupported(HwpWithOle(neo), "hwp"));
        // Nothing to draw it from (no picture, no chart part ChartReader draws): named, as before.
        var bare = Ole(("\u0001CompObj", CompObj("Hwp Chart", "HwpChart.1")), ("Contents", [0, 0, 1, 0]));
        Assert.Equal("OLE objects and charts", renderer.FirstUnsupported(HwpWithOle(bare), "hwp"));
        var broken = Ole(("OOXMLChartContents", Encoding.UTF8.GetBytes("<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"/>")));
        Assert.Equal("OLE objects and charts", renderer.FirstUnsupported(HwpWithOle(broken), "hwp"));
    }

    [Fact]
    public void A_hostile_chart_part_in_an_ole_object_is_refused_or_left_to_its_picture_quickly()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Points beyond the limit: the document is refused with the usual reason.
        var many = ChartBuilder.Columns().Replace("<c:ptCount val=\"4\"/>", "<c:ptCount val=\"99999999\"/>", StringComparison.Ordinal);
        var ole = Ole(("OOXMLChartContents", Encoding.UTF8.GetBytes(many)), ("\u0002OlePres000", Presentation(3, Wmf())));
        var refused = Assert.Throws<Core.Conversion.ConversionRefusedException>(() => HwpReader.ReadForLayout(HwpWithOle(ole), fx.NewFolder()));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
        // XML nested past the limits: refused by the XML limits, as an HWPX chart part is.
        var deep = string.Concat(Enumerable.Repeat("<a>", 100_000)) + string.Concat(Enumerable.Repeat("</a>", 100_000));
        var nested = Ole(("OOXMLChartContents", Encoding.UTF8.GetBytes(deep)), ("\u0002OlePres000", Presentation(3, Wmf())));
        Assert.Equal(DocumentLimits.ErrorKey,
            Assert.Throws<Core.Conversion.ConversionRefusedException>(() => HwpReader.ReadForLayout(HwpWithOle(nested), fx.NewFolder())).ErrorKey);
        // Not XML at all: the picture stands in.
        var junk = Ole(("OOXMLChartContents", [0xFF, 0xFE, 0x00, 0x3C]), ("\u0002OlePres000", Presentation(3, Wmf())));
        Assert.Null(Assert.Single(Images(HwpReader.ReadForLayout(HwpWithOle(junk), fx.NewFolder()))).Chart);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), clock.Elapsed.ToString());
    }

    [Fact]
    public void An_ole_object_in_an_hwpx_is_laid_out_as_its_picture()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "ole.hwpx");
        File.Copy(HwpxReaderTests.HangulStylePackage(folder), path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            // The package's chart falls back to an OLE object (hp:switch, hp:default) with BinData item ole1.
            using var entry = zip.CreateEntry("BinData/ole1.ole").Open();
            entry.Write(File.ReadAllBytes(Ole(("\u0002OlePres000", Presentation(3, Wmf())))));
        }

        var layout = HwpxReader.ReadForLayout(path, Path.Combine(folder, "media"));
        var read = HwpxReader.Read(path, Path.Combine(folder, "media-read"));

        static IEnumerable<HImage> Pictures(HDocument document) =>
            document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>();
        var picture = Assert.Single(Pictures(layout), p => Metafile.IsMetafile(p.Path));
        Assert.Null(picture.Anchor); // the test object has no position: laid out like a character
        Assert.DoesNotContain(Pictures(read), p => Metafile.IsMetafile(p.Path)); // the converters' model skips it as before
    }
}
