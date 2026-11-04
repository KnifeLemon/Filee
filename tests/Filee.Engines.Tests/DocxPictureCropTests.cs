// A picture cropped in Word or WPS (a:srcRect): the file keeps the whole picture (issue #280).

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using ImageMagick;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class DocxPictureCropTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static byte[] LeftRedRightBlue()
    {
        using var image = new MagickImage(MagickColors.Red, 200, 100);
        using var right = new MagickImage(MagickColors.Blue, 100, 100);
        image.Composite(right, 100, 0, CompositeOperator.Over);
        return image.ToByteArray(MagickFormat.Png);
    }

    private static string Cropped(string picture, string crop) =>
        picture.Replace("/></pic:blipFill>", $"/>{crop}</pic:blipFill>", StringComparison.Ordinal);

    private List<HImage> Pictures(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        return DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"))
            .Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().ToList();
    }

    [Fact]
    public void Only_the_part_shown_in_Word_is_kept()
    {
        var docx = new DocxBuilder();
        var id = docx.Image(LeftRedRightBlue());
        var images = Pictures(docx.Paragraph(P(Cropped(InlinePicture(id, 914400, 914400), "<a:srcRect l=\"50000\" t=\"10000\" b=\"20000\"/>"))));

        using var image = new MagickImage(Assert.Single(images).Path);
        Assert.Equal((100u, 70u), (image.Width, image.Height));
        using var pixels = image.GetPixels();
        var color = pixels.GetPixel(0, 0).ToColor()!;
        Assert.Equal((0, 0), ((int)color.R, (int)color.G)); // the blue half
    }

    [Fact]
    public void Without_a_crop_the_picture_stays_and_one_crop_makes_one_copy()
    {
        var docx = new DocxBuilder();
        var id = docx.Image(LeftRedRightBlue());
        var crop = "<a:srcRect l=\"50000\"/>";
        var images = Pictures(docx
            .Paragraph(P(InlinePicture(id, 914400, 914400)))
            .Paragraph(P(Cropped(InlinePicture(id, 914400, 914400), "<a:srcRect/>")))
            .Paragraph(P(Cropped(InlinePicture(id, 914400, 914400), crop)))
            .Paragraph(P(Cropped(InlinePicture(id, 914400, 914400), crop)))
            .Paragraph(P(Cropped(InlinePicture(id, 914400, 914400), "<a:srcRect l=\"-5000\" r=\"-5000\"/>"))));

        Assert.Equal(images[0].Path, images[1].Path);
        Assert.NotEqual(images[0].Path, images[2].Path);
        Assert.Equal(images[2].Path, images[3].Path);
        Assert.Equal(images[0].Path, images[4].Path); // space around the picture isn't added
        using var whole = new MagickImage(images[0].Path);
        Assert.Equal(200u, whole.Width);
    }

    [Fact]
    public void A_picture_Magick_cannot_read_stays_uncropped()
    {
        var docx = new DocxBuilder();
        var id = docx.Image([1, 2, 3, 4]);
        var images = Pictures(docx.Paragraph(P(Cropped(InlinePicture(id, 914400, 914400), "<a:srcRect l=\"50000\"/>"))));

        Assert.Equal(4, new FileInfo(Assert.Single(images).Path).Length);
    }
}
