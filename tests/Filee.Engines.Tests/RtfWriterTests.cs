// The built-in RTF writer (Hwp/Hwpx/RtfWriter): a model built in code with what the RTF reader understands (page
// setup, headers and footers, headings, character and paragraph formatting, tab stops, lists, links, bookmarks,
// fields, notes, tables with merged and shaded cells, pictures) is written as RTF and read back by Filee's own RTF
// reader into the same model; the file is valid RTF (one balanced group starting with \rtf1, ASCII only); text beyond
// ASCII is \uN with a code-page fallback; fonts carry their other names (\falt); anything → RTF takes the writer; and
// models that pass the document limits are refused without leaving a file.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using ImageMagick;

namespace Filee.Engines.Tests;

public class RtfWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static RtfWriterTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice", "calibre", "ghostscript"];

    /// <summary>Writes <paramref name="document"/>, checks it is valid RTF and reads it back with Filee's RTF reader.</summary>
    private (HDocument Document, string Rtf) RoundTrip(HDocument document, DocumentLimits? limits = null)
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "out.rtf");
        RtfWriter.Write(document, path, limits ?? DocumentLimits.Default, TestContext.Current.CancellationToken);
        var bytes = File.ReadAllBytes(path);
        AssertValidRtf(bytes);
        Assert.Single(Directory.GetFiles(folder)); // no temporary file left beside it
        return (RtfReader.Read(bytes, Path.Combine(folder, "media"), DocumentLimits.Default), Encoding.ASCII.GetString(bytes));
    }

    /// <summary>
    /// One group from the first byte to the last, opened by \rtf1, with every brace balanced (escaped ones and \bin
    /// data aside) and nothing but ASCII in the file.
    /// </summary>
    internal static void AssertValidRtf(byte[] rtf)
    {
        Assert.True(rtf.AsSpan().StartsWith("{\\rtf1"u8), "The file doesn't start with {\\rtf1.");
        Assert.All(rtf, b => Assert.True(b < 0x80, $"Byte 0x{b:x2} is not ASCII."));
        var depth = 0;
        var end = -1;
        for (var i = 0; i < rtf.Length; i++)
        {
            switch (rtf[i])
            {
                case (byte)'\\' when i + 1 < rtf.Length:
                    if (rtf[i + 1] is (byte)'{' or (byte)'}' or (byte)'\\')
                    {
                        i++;
                    }
                    else if (rtf.AsSpan(i + 1).StartsWith("bin"u8))
                    {
                        var j = i + 4;
                        var length = 0;
                        while (j < rtf.Length && char.IsAsciiDigit((char)rtf[j]))
                            length = length * 10 + (rtf[j++] - '0');
                        if (j < rtf.Length && rtf[j] == ' ')
                            j++;
                        i = j + length - 1;
                    }
                    break;
                case (byte)'{':
                    Assert.True(end < 0, "Something follows the document's group.");
                    depth++;
                    break;
                case (byte)'}':
                    depth--;
                    Assert.True(depth >= 0, $"A closing brace without a group at byte {i}.");
                    if (depth == 0)
                        end = i;
                    break;
                default:
                    Assert.True(end < 0 || rtf[i] is (byte)'\r' or (byte)'\n', "Something follows the document's group.");
                    break;
            }
        }
        Assert.Equal(0, depth);
        Assert.True(end > 0);
    }

    private string Picture(MagickColor color, uint width = 40, uint height = 30, string extension = "png")
    {
        var path = Path.Combine(fx.NewFolder(), $"picture.{extension}");
        File.WriteAllBytes(path, extension == "png" ? EbookBuilders.Png(width, height, color) : Encode(color, width, height, extension));
        return path;
    }

    private static byte[] Encode(MagickColor color, uint width, uint height, string format)
    {
        using var image = new MagickImage(color, width, height);
        return image.ToByteArray(format == "gif" ? MagickFormat.Gif : MagickFormat.Bmp);
    }

    private static HParagraph Para(params HInline[] inlines)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    private static HParagraph Para(string text, HCharFormat format = default) => Para(new HText(text, format));

    private static HCell Cell(string text, Action<HCell>? setup = null)
    {
        var cell = new HCell();
        cell.Blocks.Add(Para(text));
        setup?.Invoke(cell);
        return cell;
    }

    // ───────────────────────── Round trip ─────────────────────────

    /// <summary>A document with what the RTF reader reads back; lengths are multiples of a twip (5 HWPUNIT).</summary>
    private HDocument Sample()
    {
        var document = new HDocument { Title = "RTF sample" };
        var section = new HSection
        {
            Page = new HPage(59530, 84190, 7200, 6000, 3600, 3000, 2400, 1800, 0),
            StartPageNumber = 3,
        };
        document.Sections.Add(section);
        var header = new HHeaderFooter(HPageType.Both);
        header.Blocks.Add(Para("Header text"));
        section.Headers.Add(header);
        var footer = new HHeaderFooter(HPageType.Both);
        footer.Blocks.Add(Para(new HText("Page ", default), new HField(HFieldKind.PageNumber, default), new HText(" of ", default),
            new HField(HFieldKind.TotalPages, default)));
        section.Footers.Add(footer);

        var blocks = section.Blocks;
        blocks.Add(new HParagraph { HeadingLevel = 1, Inlines = { new HText("Chapter one", default) } });
        blocks.Add(Para(
            new HText("Plain, ", default),
            new HText("bold", new HCharFormat(Bold: true)),
            new HText(" italic", new HCharFormat(Italic: true, Size: 1400)),
            new HText(" under", new HCharFormat(Underline: true, Color: "#C00000")),
            new HText(" struck", new HCharFormat(Strike: true, Shade: "#FFFF00")),
            new HText("up", new HCharFormat(Superscript: true)),
            new HText("down", new HCharFormat(Subscript: true)),
            new HLineBreak(default),
            new HText("after a break", default),
            new HTab(default),
            new HText("after a tab {braces} and \\backslash", default)));
        blocks.Add(new HParagraph
        {
            Format = new HParaFormat(Align: HAlign.Center, Left: 1000, Right: 500, FirstLine: 400, Before: 600, After: 300,
                LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 160), KeepWithNext: true),
            Inlines = { new HText("Centred and indented", default) },
        });
        blocks.Add(new HParagraph
        {
            Format = new HParaFormat(Align: HAlign.Right, LineSpacing: new HLineSpacing(HLineSpacingKind.Fixed, 3000), KeepLines: true),
            PageBreakBefore = true,
            Inlines = { new HText("Right, fixed lines, new page", default) },
        });
        blocks.Add(new HParagraph
        {
            Format = new HParaFormat(Align: HAlign.Justify, Left: 2000, LineSpacing: new HLineSpacing(HLineSpacingKind.AtLeast, 2500),
                Tabs: [new HTabStop(5000, HTabKind.Center, "NONE"), new HTabStop(10000, HTabKind.Right, "DOT"), new HTabStop(15000, HTabKind.Decimal, "NONE")]),
            Inlines = { new HText("Tabs", default), new HTab(default), new HText("centre", default), new HTab(default), new HText("right", default) },
        });

        var numbers = new HNumbering();
        numbers.Levels.Add(new HNumberingLevel("DIGIT", "^1.", 1, Bullet: false));
        numbers.Levels.Add(new HNumberingLevel("LATIN_SMALL", "(^2)", 1, Bullet: false));
        numbers.Levels.Add(new HNumberingLevel("HANGUL_SYLLABLE", "^1.^2.^3", 3, Bullet: false));
        var bullets = new HNumbering();
        bullets.Levels.Add(new HNumberingLevel("DIGIT", "•", 1, Bullet: true));
        blocks.Add(new HParagraph { List = new HListRef(numbers, 0, true), Inlines = { new HText("First item", default) } });
        blocks.Add(new HParagraph { List = new HListRef(numbers, 1, true), Inlines = { new HText("Nested item", default) } });
        blocks.Add(new HParagraph { List = new HListRef(numbers, 2, true), Inlines = { new HText("Third level", default) } });
        blocks.Add(new HParagraph { List = new HListRef(bullets, 0, true), Inlines = { new HText("A bullet", default) } });

        var note = new HNote(false);
        note.Blocks.Add(Para("The footnote's text."));
        var endnote = new HNote(true);
        endnote.Blocks.Add(Para("An endnote."));
        var link = new HLink("https://example.com/a?b=1&c=2");
        link.Content.Add(new HText("a link", new HCharFormat(Underline: true, Color: "#0563C1")));
        var local = new HLink("#target");
        local.Content.Add(new HText("to the bookmark", default));
        blocks.Add(Para(new HBookmark("target"), new HText("Notes", default), note, new HText(", links ", default), link,
            new HText(" and ", default), local, endnote));
        blocks.Add(Para(new HImage(Picture(MagickColors.Teal)) { Width = 4000, Height = 3000 }));

        var table = new HTable { ColumnCount = 3, ColumnWidths = [10000, 15000, 20000] };
        var head = new HRow { Header = true, Height = 1500 };
        head.Cells.Add(Cell("Wide head", c =>
        {
            c.ColSpan = 2;
            c.Fill = "#DDEEFF";
        }));
        head.Cells.Add(Cell("Tall", c =>
        {
            c.RowSpan = 2;
            c.VerticalAlign = HVerticalAlign.Center;
        }));
        table.Rows.Add(head);
        var middle = new HRow();
        middle.Cells.Add(Cell("Block", c =>
        {
            c.RowSpan = 2;
            c.ColSpan = 2;
            c.Borders = HBorders.Empty;
        }));
        table.Rows.Add(middle);
        var last = new HRow();
        var two = new HCell { VerticalAlign = HVerticalAlign.Bottom };
        two.Blocks.Add(Para("Two"));
        two.Blocks.Add(Para("paragraphs"));
        last.Cells.Add(two);
        table.Rows.Add(last);
        blocks.Add(table);
        blocks.Add(Para("After the table"));
        return document;
    }

    [Fact]
    public void A_document_read_back_from_its_rtf_is_the_same_model()
    {
        var document = Sample();
        var (read, rtf) = RoundTrip(document);

        HDocumentAssert.Similar(document, read);
        Assert.Contains("\\pngblip", rtf, StringComparison.Ordinal);
        Assert.Contains("\\clmgf", rtf, StringComparison.Ordinal);
        Assert.Contains("\\clvmrg", rtf, StringComparison.Ordinal);
        Assert.Equal(3, read.Sections[0].StartPageNumber);
    }

    [Fact]
    public void Sections_columns_and_landscape_pages_come_back()
    {
        var document = new HDocument();
        var portrait = new HSection { Page = new HPage(59530, 84190, 7200, 7200, 3600, 3600, 2400, 2400, 0) };
        portrait.Blocks.Add(Para("Portrait"));
        var landscape = new HSection
        {
            Page = new HPage(84190, 59530, 5000, 5000, 3000, 3000, 2000, 2000, 0),
            Columns = new HColumns(2, 1000, false),
        };
        landscape.Blocks.Add(Para("Landscape in two columns"));
        document.Sections.Add(portrait);
        document.Sections.Add(landscape);

        var (read, rtf) = RoundTrip(document);

        HDocumentAssert.Similar(document, read);
        Assert.Contains("\\sect", rtf, StringComparison.Ordinal);
        Assert.True(read.Sections[1].Page!.Landscape);
    }

    [Fact]
    public void Text_beyond_ascii_is_unicode_with_a_code_page_fallback()
    {
        // é is in Windows-1252, 한 in CP949 (two bytes: \uc2), 😀 in no code page (a surrogate pair, "?").
        var text = "Café 한국어 😀 non\u00A0breaking";
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.Add(Para(text));
        document.Sections.Add(section);

        var (read, rtf) = RoundTrip(document);

        var paragraph = Assert.IsType<HParagraph>(Assert.Single(read.Sections[0].Blocks));
        Assert.Equal(text, string.Concat(paragraph.Inlines.OfType<HText>().Select(t => t.Text)));
        Assert.Contains("\\" + "u233\\'e9", rtf, StringComparison.Ordinal);
        Assert.Contains("\\uc2", rtf, StringComparison.Ordinal);
        Assert.Contains("\\" + "u-10916\\'c7\\'d1", rtf, StringComparison.Ordinal); // 한 (U+D55C, past 32767: negative) = C7 D1 in CP949
        Assert.Contains("\\" + "u-10179?", rtf, StringComparison.Ordinal); // the high surrogate of 😀
        Assert.Contains("\\~", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void Fonts_go_by_script_with_their_other_names()
    {
        var document = new HDocument();
        document.FontAlternates["Fancy Sans"] = "Arial";
        var section = new HSection();
        section.Blocks.Add(Para("Hello 안녕하세요", new HCharFormat(Font: "Arial", EastAsianFont: "바탕")));
        section.Blocks.Add(Para("Display", new HCharFormat(Font: "Fancy Sans")));
        document.Sections.Add(section);

        var (read, rtf) = RoundTrip(document);

        var first = Assert.IsType<HParagraph>(read.Sections[0].Blocks[0]).Inlines.OfType<HText>().ToList();
        Assert.Equal(2, first.Count);
        Assert.Equal(("Hello ", "Arial", null), (first[0].Text, first[0].Format.Font, first[0].Format.EastAsianFont));
        Assert.Equal(("안녕하세요", "바탕", "바탕"), (first[1].Text, first[1].Format.Font, first[1].Format.EastAsianFont));
        Assert.Equal("Fancy Sans", Assert.IsType<HText>(Assert.IsType<HParagraph>(read.Sections[0].Blocks[1]).Inlines[0]).Format.Font);
        // The other names: the document's own, and the English name of a Korean font.
        Assert.Equal("Arial", read.FontAlternates["Fancy Sans"]);
        Assert.Equal("Batang", read.FontAlternates["바탕"]);
        Assert.Equal("Carlito", read.FontAlternates["Calibri"]);
        Assert.Contains("\\fcharset129", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_tab_stops_and_equations_are_written()
    {
        var document = new HDocument { DefaultTabStop = 3600 };
        var section = new HSection();
        section.Blocks.Add(Para(new HText("E = ", default), new HEquation(EquationParser.Parse("a over b"), default)));
        document.Sections.Add(section);

        var (read, rtf) = RoundTrip(document);

        Assert.Contains("\\deftab720", rtf, StringComparison.Ordinal);
        Assert.Equal(3600, read.DefaultTabStop);
        var text = string.Concat(Assert.IsType<HParagraph>(read.Sections[0].Blocks[0]).Inlines.OfType<HText>().Select(t => t.Text));
        Assert.StartsWith("E = ", text, StringComparison.Ordinal);
        Assert.Contains('a', text);
        Assert.Contains('b', text);
    }

    [Fact]
    public void Nested_tables_text_boxes_and_other_pictures_make_valid_rtf()
    {
        var inner = new HTable { ColumnCount = 2 };
        var innerRow = new HRow();
        innerRow.Cells.Add(Cell("inner one"));
        innerRow.Cells.Add(Cell("inner two"));
        inner.Rows.Add(innerRow);
        var outerCell = new HCell();
        outerCell.Blocks.Add(Para("outer"));
        outerCell.Blocks.Add(inner);
        var outer = new HTable { ColumnCount = 2 };
        var row = new HRow();
        row.Cells.Add(outerCell);
        row.Cells.Add(Cell("right"));
        outer.Rows.Add(row);

        var box = new HTextBox { Width = 20000, Height = 6000, Fill = "#FFEECC" };
        box.Blocks.Add(Para("Boxed text"));
        var shape = new HShape(HShapeKind.Ellipse) { Width = 5000, Height = 3000, Fill = "#336699" };
        var floating = new HAnchor("PAGE", "CENTER", 0, "PARA", "TOP", 500, "SQUARE", false);
        var gif = new HImage(Picture(MagickColors.Orange, 16, 8, "gif")) { Anchor = floating };
        var bmp = new HImage(Picture(MagickColors.Purple, 12, 6, "bmp"));

        var document = new HDocument();
        var section = new HSection();
        section.Blocks.Add(Para("Before"));
        section.Blocks.Add(outer);
        section.Blocks.Add(Para(new HText("A box ", default), box, shape, gif, bmp));
        document.Sections.Add(section);

        var (read, rtf) = RoundTrip(document);

        Assert.Contains("\\nestcell", rtf, StringComparison.Ordinal);
        Assert.Contains("\\nesttableprops", rtf, StringComparison.Ordinal);
        Assert.Contains("\\shptxt", rtf, StringComparison.Ordinal);
        Assert.DoesNotContain("\\dibitmap", rtf, StringComparison.Ordinal);
        var table = Assert.IsType<HTable>(read.Sections[0].Blocks[1]);
        // The nested table comes back as a table in the outer cell, after its paragraph.
        var cellBlocks = table.Rows[0].Cells[0].Blocks;
        Assert.Equal("outer", string.Concat(Assert.IsType<HParagraph>(cellBlocks[0]).Inlines.OfType<HText>().Select(t => t.Text)));
        var nested = Assert.IsType<HTable>(cellBlocks[1]);
        Assert.Equal(["inner one", "inner two"], nested.Rows.Single().Cells.Select(c => string.Concat(c.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text))));
        var last = Assert.IsType<HParagraph>(read.Sections[0].Blocks[2]);
        Assert.Contains("Boxed text", string.Concat(last.Inlines.OfType<HText>().Select(t => t.Text)), StringComparison.Ordinal);
        // GIF and BMP become PNG.
        Assert.Equal(2, last.Inlines.OfType<HImage>().Count());
        Assert.All(last.Inlines.OfType<HImage>(), image => Assert.Equal(".png", Path.GetExtension(image.Path)));
    }

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void Documents_go_to_rtf_through_the_built_in_writer()
    {
        var builtIn = new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }.CreatePlanner(["rhwp"]);
        var all = fx.Catalog.CreatePlanner(["pandoc", "libreoffice", "rhwp"]);
        foreach (var from in new[] { "docx", "odt", "hwp", "hwpx", "md", "html", "txt", "doc" })
        {
            Assert.Equal("rtf-writer", Assert.Single(builtIn.Plan(from, "rtf")!.Steps).Converter.Id);
            Assert.Equal("rtf-writer", Assert.Single(all.Plan(from, "rtf")!.Steps).Converter.Id);
        }
        Assert.Null(builtIn.Plan("rtf", "rtf"));
    }

    // ───────────────────────── Limits ─────────────────────────

    [Fact]
    public void A_file_beyond_the_size_limit_is_refused_without_leaving_anything()
    {
        var document = new HDocument();
        var section = new HSection();
        for (var i = 0; i < 2000; i++)
            section.Blocks.Add(Para($"Paragraph {i} with some text to fill the file."));
        document.Sections.Add(section);
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "big.rtf");

        var refused = Assert.Throws<ConversionRefusedException>(() =>
            RtfWriter.Write(document, path, new DocumentLimits { MaxPackageBytes = 20_000 }, TestContext.Current.CancellationToken));

        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void Hostile_models_are_refused_or_written_quickly()
    {
        // Tables 5,000 deep are opened up into paragraphs past the nesting limit.
        var deep = new HDocument();
        var section = new HSection();
        deep.Sections.Add(section);
        var blocks = section.Blocks;
        for (var i = 0; i < 5000; i++)
        {
            var table = new HTable { ColumnCount = 1 };
            var row = new HRow();
            var cell = new HCell();
            cell.Blocks.Add(Para($"level {i}"));
            row.Cells.Add(cell);
            table.Rows.Add(row);
            blocks.Add(table);
            blocks = cell.Blocks;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var (read, _) = RoundTrip(deep);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"Took {started.Elapsed}.");
        Assert.NotEmpty(read.Sections[0].Blocks);

        // A cell spanning 16,384 columns over 1,000 rows passes the table cell limit.
        var wide = new HDocument();
        var wideSection = new HSection();
        var huge = new HTable { ColumnCount = 16_384 };
        for (var r = 0; r < 1000; r++)
        {
            var row = new HRow();
            row.Cells.Add(new HCell { ColSpan = 16_384 });
            huge.Rows.Add(row);
        }
        wideSection.Blocks.Add(huge);
        wide.Sections.Add(wideSection);
        var folder = fx.NewFolder();
        Assert.Throws<ConversionRefusedException>(() => RtfWriter.Write(wide, Path.Combine(folder, "wide.rtf"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder));

        // A picture whose header claims ten gigapixels is left out.
        var claiming = DocumentFuzzTests.TinyPng(4, 3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(claiming.AsSpan(16), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(claiming.AsSpan(20), 100_000);
        var picture = Path.Combine(fx.NewFolder(), "huge.png");
        File.WriteAllBytes(picture, claiming);
        var pictured = new HDocument();
        var pictureSection = new HSection();
        pictureSection.Blocks.Add(Para(new HText("x", default), new HImage(picture)));
        pictured.Sections.Add(pictureSection);
        Assert.DoesNotContain("\\pict", RoundTrip(pictured).Rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void Floating_cropped_and_turned_pictures_come_back()
    {
        var path = Picture(MagickColors.Teal, 40, 20);
        var anchor = new HAnchor("PAPER", "LEFT", 5000, "PARA", "TOP", 1500, "SQUARE", true);
        var floating = new HImage(path) { Width = 6000, Height = 3000, Anchor = anchor, Transform = new HTransform(90), Crop = new HCrop(0.25, 0, 0.25, 0), ZOrder = 4, OuterMargin = new HInsets(500, 600, 100, 200) };
        var centred = new HImage(path) { Width = 4000, Height = 2000, Anchor = new HAnchor("PAGE", "CENTER", 0, "PAGE", "BOTTOM", 0, "BEHIND_TEXT", false) };
        var inline = new HImage(path) { Width = 3000, Height = 1500, Crop = new HCrop(0, 0.2, 0, 0.3), Transform = new HTransform(0, true, false) };
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.Add(Para(new HText("Pictures ", default), floating, centred, inline));
        document.Sections.Add(section);

        var (read, rtf) = RoundTrip(document);

        HDocumentAssert.Similar(document, read);
        Assert.Contains("\\shpinst", rtf, StringComparison.Ordinal);
        Assert.Contains("{\\sn pib}", rtf, StringComparison.Ordinal);
        var images = read.Sections[0].Blocks.OfType<HParagraph>().Single().Inlines.OfType<HImage>().ToList();
        Assert.Equal(3, images.Count);
        Assert.Equal(new HTransform(90), images[0].Transform);
        Assert.Equal(0.25, images[0].Crop!.Value.Left, 2);
        Assert.Equal(0.25, images[0].Crop!.Value.Right, 2);
        Assert.Equal(4, images[0].ZOrder);
        Assert.Equal(new HInsets(500, 600, 100, 200), images[0].OuterMargin);
        Assert.Equal(new HTransform(0, true, false), images[2].Transform);
        Assert.Equal(0.2, images[2].Crop!.Value.Top, 2);
        Assert.Equal(0.3, images[2].Crop!.Value.Bottom, 2);
    }

    [Fact]
    public void Paragraph_boxes_keep_their_distances_lines_between_and_background()
    {
        var line = new HBorder(HBorderStyle.Dot, 0.5, "#336699");
        var borders = new HBorders(line, line, line, line);
        var box = new HParaBorder(borders, "#EEF4E0", new HInsets(400, 400, 100, 100), Connect: true, Between: line);
        HParagraph Boxed(string text) => new()
        {
            Format = new HParaFormat(Borders: borders, Layout: new HParaLayout(Border: box)),
            Inlines = { new HText(text, default) },
        };
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.Add(Boxed("one"));
        section.Blocks.Add(Boxed("two"));
        document.Sections.Add(section);

        var (read, rtf) = RoundTrip(document);

        Assert.Contains("\\brdrbtw", rtf, StringComparison.Ordinal);
        var paragraph = read.Sections[0].Blocks.OfType<HParagraph>().First();
        Assert.Equal("#EEF4E0", paragraph.Format.Fill);
        var border = paragraph.Format.Layout!.Border!;
        Assert.Equal(new HInsets(400, 400, 100, 100), border.Offsets);
        Assert.Equal(HBorderStyle.Dot, border.Between!.Value.Style);
        Assert.Equal((HBorderStyle.Dot, "#336699"), (border.Borders.Left.Style, border.Borders.Left.Color));
    }

    [Fact]
    public void Autofit_tables_stay_autofit_with_their_preferred_widths()
    {
        var table = new HTable
        {
            ColumnCount = 2,
            ColumnWidths = [15000, 20000],
            Sizing = new HTableSizing(false, HPreferredWidth.Auto, [15000, 20000]),
        };
        var row = new HRow();
        row.Cells.Add(Cell("short", c => c.Width = new HPreferredWidth(HWidthType.Absolute, 8000)));
        row.Cells.Add(Cell("auto"));
        table.Rows.Add(row);
        var fixedTable = new HTable { ColumnCount = 1, ColumnWidths = [30000], Sizing = new HTableSizing(true, new HPreferredWidth(HWidthType.Percent, 0.5), [30000]) };
        var fixedRow = new HRow();
        fixedRow.Cells.Add(Cell("fixed"));
        fixedTable.Rows.Add(fixedRow);
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.Add(table);
        section.Blocks.Add(Para("between"));
        section.Blocks.Add(fixedTable);
        document.Sections.Add(section);

        var (read, rtf) = RoundTrip(document);

        Assert.Contains("\\trautofit1\\trftsWidth1", rtf, StringComparison.Ordinal);
        var tables = read.Sections[0].Blocks.OfType<HTable>().ToList();
        Assert.Equal(new HTableSizing(false, HPreferredWidth.Auto, tables[0].ColumnWidths!), tables[0].Sizing);
        Assert.Equal([15000, 20000], tables[0].ColumnWidths!);
        Assert.Equal(new HPreferredWidth(HWidthType.Absolute, 8000), tables[0].Rows[0].Cells[0].Width);
        Assert.Equal(HPreferredWidth.Auto, tables[0].Rows[0].Cells[1].Width);
        // A fixed table keeps its columns (the reader has no sizing for it) and its cells their widths.
        Assert.Null(tables[1].Sizing);
        Assert.Equal([30000], tables[1].ColumnWidths!);
        Assert.Equal(new HPreferredWidth(HWidthType.Absolute, 30000), tables[1].Rows[0].Cells[0].Width);
    }

    [Fact]
    public void A_cancelled_write_leaves_no_file()
    {
        var folder = fx.NewFolder();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => RtfWriter.Write(Sample(), Path.Combine(folder, "out.rtf"), cancelled.Token));

        Assert.Empty(Directory.GetFiles(folder));
    }
}
