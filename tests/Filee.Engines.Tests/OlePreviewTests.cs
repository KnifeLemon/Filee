// The pictures of embedded OLE objects (Hwp/Binary/OlePreview.cs) and the HWPX reader drawing an OLE object as its
// picture. The objects are compound files built here with OpenMcdf, the way 한글 keeps them in BinData: a 4-byte
// length, then the compound file with the presentation stream its program left.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
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
