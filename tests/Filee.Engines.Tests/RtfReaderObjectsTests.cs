// RTF as Word writes it, beyond plain text and simple tables: tables nested in cells (\itap, \nestcell,
// \nesttableprops), cell, row and paragraph borders with their styles, widths and colours, paragraph shading, a
// different first page's header and footer (\titlepg), per-cell padding (Word's swapped \clpadl / \clpadt), floating
// pictures (\shp with a pib property), cropped and mirrored pictures, and the fonts the missing-font notice looks at.
// Hostile nesting is read in bounded depth and time.

using System.Diagnostics;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Office;
using ImageMagick;

namespace Filee.Engines.Tests;

public sealed class RtfReaderObjectsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Head = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}{\colortbl;\red0\green0\blue0;\red255\green0\blue0;\red0\green0\blue255;\red255\green255\blue255;}";

    private HDocument Read(string rtf) => RtfReader.Read(Encoding.Latin1.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default);

    private static string Text(HBlock block) => block is HParagraph p ? string.Concat(p.Inlines.OfType<HText>().Select(t => t.Text)) : "";

    private static string Png()
    {
        using var image = new MagickImage(MagickColors.Teal, 4, 2);
        return Convert.ToHexString(image.ToByteArray(MagickFormat.Png)).ToLowerInvariant();
    }

    [Fact]
    public void Tables_nested_in_cells_are_tables_in_the_cell()
    {
        var document = Read(Head
            + @"\trowd\trgaph108\trleft-108\cellx4000\cellx8000"
            + @"\pard\plain\intbl\itap1 Outer\par"
            + @"\pard\plain\intbl\itap2 In A\nestcell In B\nestcell"
            + @"{\*\nesttableprops\trowd\trgaph108\clbrdrb\brdrdb\brdrw10\cellx2000\cellx3800\nestrow}{\nonesttables\par}"
            + @"\pard\plain\intbl\itap2 In C\nestcell In D\nestcell"
            + @"{\*\nesttableprops\trowd\trgaph108\cellx2000\cellx3800\nestrow}{\nonesttables\par}"
            + @"\pard\plain\intbl\itap1 after\cell Right\cell"
            + @"\pard\plain\intbl{\trowd\trgaph108\trleft-108\cellx4000\cellx8000\row}"
            + @"\pard\plain After\par}");

        var blocks = document.Sections[0].Blocks;
        var outer = Assert.IsType<HTable>(blocks[0]);
        Assert.Equal("After", Text(blocks[1]));
        Assert.Equal(2, blocks.Count);
        var cell = outer.Rows.Single().Cells[0];
        Assert.Equal(3, cell.Blocks.Count);
        Assert.Equal("Outer", Text(cell.Blocks[0]));
        var inner = Assert.IsType<HTable>(cell.Blocks[1]);
        Assert.Equal("after", Text(cell.Blocks[2]));
        Assert.Equal("Right", Text(outer.Rows[0].Cells[1].Blocks.Single()));
        Assert.Equal([["In A", "In B"], ["In C", "In D"]], inner.Rows.Select(r => r.Cells.Select(c => Text(c.Blocks.Single())).ToArray()).ToArray());
        Assert.Equal([HwpxUnits.FromTwips(2000), HwpxUnits.FromTwips(1800)], inner.ColumnWidths!);
        Assert.Equal(HBorderStyle.Double, inner.Rows[0].Cells[0].Borders!.Value.Bottom.Style);
        Assert.Equal(HBorderStyle.None, inner.Rows[1].Cells[0].Borders!.Value.Bottom.Style);
    }

    [Fact]
    public void Hostile_nesting_is_read_in_bounded_depth_and_time()
    {
        var rtf = new StringBuilder(Head);
        // Forty tables in each other's cells: the ones deeper than Nesting.MaxDepth stay in the deepest one's cells.
        for (var d = 1; d <= 40; d++)
            rtf.Append(@$"\pard\plain\intbl\itap{d} L{d}\par");
        for (var d = 40; d >= 2; d--)
            rtf.Append(@$"\pard\plain\intbl\itap{d} \nestcell{{\*\nesttableprops\trowd\cellx1000\nestrow}}");
        rtf.Append(@"\pard\plain\intbl\cell\trowd\cellx1000\row\pard\plain ");
        // Absurd levels, rows and cells without anything around them.
        for (var i = 0; i < 20000; i++)
            rtf.Append(@"\pard\intbl\itap2000000000 x\nestcell{\*\nesttableprops\trowd\cellx100\nestrow}\nestrow\nestcell");
        rtf.Append(@"\pard\plain end\par}");

        var watch = Stopwatch.StartNew();
        var document = Read(rtf.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Reading took {watch.Elapsed}.");

        static int Depth(IEnumerable<HBlock> blocks) =>
            blocks.OfType<HTable>().Select(t => 1 + t.Rows.SelectMany(r => r.Cells).Select(c => Depth(c.Blocks)).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
        static IEnumerable<string> Texts(IEnumerable<HBlock> blocks) => blocks.SelectMany(b => b switch
        {
            HParagraph p => [string.Concat(p.Inlines.OfType<HText>().Select(t => t.Text))],
            HTable t => t.Rows.SelectMany(r => r.Cells).SelectMany(c => Texts(c.Blocks)),
            _ => [],
        });
        var blocks = document.Sections[0].Blocks;
        Assert.InRange(Depth(blocks), 2, Nesting.MaxDepth);
        var texts = Texts(blocks).ToList();
        Assert.All(Enumerable.Range(1, 40), d => Assert.Contains($"L{d}", texts));
        Assert.Equal("end", texts[^1]);
    }

    [Fact]
    public void Cell_row_and_paragraph_borders_keep_their_kind_width_and_colour()
    {
        var document = Read(Head
            + @"\trowd\trgaph108\trbrdrt\brdrs\brdrw30\brdrcf2\trbrdrb\brdrs\brdrw10\trbrdrv\brdrdot\brdrw10\brdrcf3"
            + @"\clbrdrl\brdrdash\brdrw20\clshdng5000\clcfpat1\clcbpat4\cellx3000\clbrdrt\brdrtbl\clbrdrr\brdrnone\cellx6000"
            + @"\pard\intbl One\cell Two\cell\row"
            + @"\pard\plain\brdrt\brdrs\brdrw15\brsp40\brdrb\brdrdb\brdrw10\brdrcf2\cbpat3 Boxed\par"
            + @"\pard\plain\box\brdrth\brdrw10\shading2500\cfpat1 Shaded\par"
            + @"\pard\plain Plain\par}");

        var blocks = document.Sections[0].Blocks;
        var cells = Assert.IsType<HTable>(blocks[0]).Rows[0].Cells;
        var first = cells[0].Borders!.Value;
        var second = cells[1].Borders!.Value;
        // Own sides first, then the row's: top and bottom of the row, \trbrdrv between the cells.
        Assert.Equal((HBorderStyle.Dash, "#000000"), (first.Left.Style, first.Left.Color));
        Assert.Equal(20 / 1440.0 * 25.4, first.Left.WidthMm, 3);
        Assert.Equal((HBorderStyle.Solid, "#FF0000"), (first.Top.Style, first.Top.Color));
        Assert.Equal(30 / 1440.0 * 25.4, first.Top.WidthMm, 3);
        Assert.Equal((HBorderStyle.Dot, "#0000FF"), (first.Right.Style, first.Right.Color));
        Assert.Equal((HBorderStyle.Dot, "#0000FF"), (second.Left.Style, second.Left.Color));
        Assert.Equal(HBorderStyle.None, second.Right.Style);
        Assert.Equal((HBorderStyle.Solid, "#FF0000"), (second.Top.Style, second.Top.Color)); // \brdrtbl: the row's
        Assert.Equal(HBorderStyle.Solid, second.Bottom.Style);
        // Half black over white.
        Assert.Equal("#808080", cells[0].Fill);

        var boxed = Assert.IsType<HParagraph>(blocks[1]);
        Assert.Equal((HBorderStyle.Solid, HBorderStyle.Double, HBorderStyle.None), (boxed.Format.Borders!.Value.Top.Style, boxed.Format.Borders!.Value.Bottom.Style, boxed.Format.Borders!.Value.Left.Style));
        Assert.Equal("#FF0000", boxed.Format.Borders!.Value.Bottom.Color);
        Assert.Equal("#0000FF", boxed.Format.Fill);
        var border = boxed.Format.Layout!.Border!;
        Assert.Equal(HwpxUnits.FromTwips(40), border.Offsets.Top);
        Assert.Equal("#0000FF", border.Fill);
        Assert.True(border.Connect);

        var shaded = Assert.IsType<HParagraph>(blocks[2]);
        var box = shaded.Format.Borders!.Value;
        Assert.All(new[] { box.Left, box.Right, box.Top, box.Bottom }, b => Assert.Equal(20 / 1440.0 * 25.4, b.WidthMm, 3)); // \brdrth: twice as thick
        Assert.Equal("#BFBFBF", shaded.Format.Fill);
        var plain = Assert.IsType<HParagraph>(blocks[3]);
        Assert.Null(plain.Format.Borders);
        Assert.Null(plain.Format.Fill);
        Assert.Null(plain.Format.Layout);
    }

    [Fact]
    public void A_different_first_page_has_its_own_header_or_none()
    {
        var document = Read(Head
            + @"\sectd\titlepg{\header\pard Every page\par}{\headerf\pard First page\par}{\footer\pard Footer\par}"
            + @"\pard Body\par\sect"
            + @"\sectd\titlepg{\header\pard Second\par}\pard Two\par\sect"
            + @"\sectd{\headerf\pard Not used\par}{\header\pard Third\par}\pard Three\par}");

        var (first, second, third) = (document.Sections[0], document.Sections[1], document.Sections[2]);
        Assert.Equal("First page", Text(first.FirstPageHeader!.Single()));
        Assert.False(first.HideFirstHeader);
        Assert.Equal("Every page", Text(first.Headers.Single().Blocks.Single()));
        // No \footerf: the footer is hidden on the first page.
        Assert.True(first.HideFirstFooter);
        Assert.Null(first.FirstPageFooter);
        Assert.True(second.HideFirstHeader);
        Assert.Null(second.FirstPageHeader);
        // Without \titlepg the first page's header doesn't count.
        Assert.False(third.HideFirstHeader);
        Assert.Null(third.FirstPageHeader);
        Assert.Equal("Third", Text(third.Headers.Single().Blocks.Single()));
    }

    [Fact]
    public void Cell_padding_is_read_as_word_reads_it()
    {
        var table = Read(Head + @"\trowd\trgaph108\clpadl60\clpadfl3\clpadt90\clpadft3\clpadr30\clpadfr3\cellx3000\clpadb45\clpadfb0\cellx6000"
                              + @"\pard\intbl One\cell Two\cell\row}")
            .Sections[0].Blocks.OfType<HTable>().Single();

        // \clpadl is the top padding and \clpadt the left one; \clpadfb0 means no bottom padding is given.
        Assert.Equal(new HInsets(HwpxUnits.FromTwips(90), HwpxUnits.FromTwips(30), HwpxUnits.FromTwips(60), 0), table.Rows[0].Cells[0].CellMargin);
        Assert.Null(table.Rows[0].Cells[1].CellMargin);
    }

    [Fact]
    public void Floating_pictures_are_read_from_their_shape()
    {
        var png = Png();
        var document = Read(Head + @"\pard Text "
            + @"{\shp{\*\shpinst\shpleft1000\shptop500\shpright3000\shpbottom2000\shpfhdr0\shpbxmargin\shpbxignore\shpbypara\shpbyignore"
            + @"\shpwr2\shpwrk0\shpfblwtxt0\shpz3\shplid1025{\sp{\sn shapeType}{\sv 75}}{\sp{\sn posrelh}{\sv 1}}{\sp{\sn posv}{\sv 2}}"
            + @"{\sp{\sn rotation}{\sv 5898240}}{\sp{\sn fFlipV}{\sv 1}}{\sp{\sn cropFromLeft}{\sv 16384}}"
            + @"{\sp{\sn pib}{\sv {\pict\pngblip\picw4\pich2\picwgoal2000\pichgoal1000 " + png + @"}}}}"
            + @"{\shprslt\par\pard {\pict\pngblip\picwgoal2000\pichgoal1000 " + png + @"}\par}}"
            + @" after\par}");

        var paragraph = Assert.IsType<HParagraph>(document.Sections[0].Blocks.Single());
        var image = Assert.Single(paragraph.Inlines.OfType<HImage>());
        Assert.Equal((HwpxUnits.FromTwips(2000), HwpxUnits.FromTwips(1500)), (image.Width, image.Height));
        Assert.Equal(new HAnchor("PAPER", "LEFT", HwpxUnits.FromTwips(1000), "PARA", "CENTER", 0, "SQUARE", true), image.Anchor);
        Assert.Equal(new HTransform(90, false, true), image.Transform);
        Assert.Equal(0.25, image.Crop!.Value.Left, 3);
        Assert.Equal(3, image.ZOrder);
        Assert.Equal(new HInsets(900, 900, 0, 0), image.OuterMargin); // Word's distances when the shape gives none
        Assert.Equal("Text  after", string.Concat(paragraph.Inlines.OfType<HText>().Select(t => t.Text)));
    }

    [Fact]
    public void Text_boxes_are_read_from_their_shape_and_their_copy_is_skipped()
    {
        // As Word writes a text box: its paragraphs in \shptxt, and a copy in \shprslt (a framed paragraph) for programs
        // without shapes. The second shape gives no properties: Word's defaults.
        var document = Read(Head + @"\pard Before "
            + @"{\shp{\*\shpinst\shpleft1440\shptop720\shpright4320\shpbottom2160\shpfhdr0\shpbxcolumn\shpbxignore\shpbypara\shpbyignore"
            + @"\shpwr2\shpwrk0\shpfblwtxt0\shpz2\shplid1026{\sp{\sn shapeType}{\sv 202}}{\sp{\sn fillColor}{\sv 13434879}}"
            + @"{\sp{\sn lineColor}{\sv 255}}{\sp{\sn lineWidth}{\sv 25400}}{\sp{\sn lineDashing}{\sv 1}}"
            + @"{\sp{\sn dxTextLeft}{\sv 127000}}{\sp{\sn dyTextTop}{\sv 63500}}{\sp{\sn anchorText}{\sv 1}}{\sp{\sn posrelh}{\sv 2}}{\sp{\sn posrelv}{\sv 2}}"
            + @"{\shptxt \pard\plain\qc\b Inside\b0  the box\par\pard second line\par}}"
            + @"{\shprslt{\*\do\dobxcolumn\dobypara\dodhgt8192\dptxbx{\dptxbxtext\pard\plain copy for old programs\par}}}}"
            + @"{\shp{\*\shpinst\shpleft0\shptop0\shpright1440\shpbottom720\shpwr3{\sp{\sn shapeType}{\sv 3}}"
            + @"{\shptxt \pard\plain round\par}}{\shprslt\pard\plain round copy\par}}"
            + @" after\par}");

        var paragraph = Assert.IsType<HParagraph>(document.Sections[0].Blocks.Single());
        Assert.Equal("Before  after", string.Concat(paragraph.Inlines.OfType<HText>().Select(t => t.Text)));
        var boxes = paragraph.Inlines.OfType<HTextBox>().ToList();
        Assert.Equal(2, boxes.Count);

        var box = boxes[0];
        Assert.Equal((HwpxUnits.FromTwips(2880), HwpxUnits.FromTwips(1440)), (box.Width, box.Height));
        Assert.Equal(new HAnchor("COLUMN", "LEFT", HwpxUnits.FromTwips(1440), "PARA", "TOP", HwpxUnits.FromTwips(720), "SQUARE", true), box.Anchor);
        Assert.Equal(2, box.ZOrder);
        Assert.Equal("#FFFFCC", box.Fill);
        Assert.Equal((HBorderStyle.Dash, "#FF0000"), (box.Line.Style, box.Line.Color));
        Assert.Equal(25400 / 36000.0, box.Line.WidthMm, 3);
        Assert.Equal(new HInsets(1000, 720, 500, 360), box.Padding);
        Assert.Equal(HVerticalAlign.Center, box.VerticalAlign);
        Assert.Equal(HShapeKind.Rectangle, box.Shape);
        Assert.Equal(["Inside the box", "second line"], box.Blocks.Select(Text));
        var first = Assert.IsType<HParagraph>(box.Blocks[0]);
        Assert.Equal(HAlign.Center, first.Format.Align);
        Assert.True(first.Inlines.OfType<HText>().First().Format.Bold);

        var round = boxes[1];
        Assert.Equal(HShapeKind.Ellipse, round.Shape);
        Assert.Equal("#FFFFFF", round.Fill);
        Assert.Equal((HBorderStyle.Solid, "#000000"), (round.Line.Style, round.Line.Color));
        Assert.Equal("IN_FRONT_OF_TEXT", round.Anchor!.Wrap);
        Assert.Equal(["round"], round.Blocks.Select(Text));
    }

    [Fact]
    public void Floating_tables_are_placed_as_their_first_row_says()
    {
        var document = Read(Head
            + @"\trowd\trgaph108\tphmrg\tpvpg\tposx1440\tposy2880\tdfrmtxtLeft180\tdfrmtxtRight360\tdfrmtxtTop90\tdfrmtxtBottom45\cellx3000"
            + @"\pard\plain\intbl Floating\cell\row"
            + @"\pard\plain Between\par"
            + @"\trowd\trgaph108\tphpg\tpvpara\tposxc\tposyb\tposnegx-500\tabsnoovrlp\cellx3000"
            + @"\pard\plain\intbl Second\cell\row"
            + @"\pard\plain\par"
            + @"\trowd\trgaph108\cellx3000\pard\plain\intbl In the text\cell\row}");

        var tables = document.Sections[0].Blocks.OfType<HTable>().ToList();
        Assert.Equal(new HAnchor("PAGE", "LEFT", HwpxUnits.FromTwips(1440), "PAPER", "TOP", HwpxUnits.FromTwips(2880), "SQUARE", true), tables[0].Float);
        Assert.Equal(new HInsets(HwpxUnits.FromTwips(180), HwpxUnits.FromTwips(360), HwpxUnits.FromTwips(90), HwpxUnits.FromTwips(45)), tables[0].FloatDistance);
        // The last word for a side wins: \tposnegx after \tposxc gives an offset again.
        Assert.Equal(new HAnchor("PAPER", "LEFT", HwpxUnits.FromTwips(-500), "PARA", "BOTTOM", 0, "SQUARE", false), tables[1].Float);
        Assert.Null(tables[2].Float);
    }

    [Fact]
    public void A_list_number_takes_the_mark_s_format_with_the_level_s_over_it_and_empty_cells_keep_theirs()
    {
        // #1011 through RTF: bold numbers before plain answers (the number in the paragraph mark's \b, at the level's
        // \fs24), right-aligned with a space after them; an empty cell keeps its paragraph's line spacing and size.
        var document = Read(Head
            + @"{\*\listtable{\list\listtemplateid1{\listlevel\levelnfc0\leveljc2\levelfollow1\levelstartat1{\leveltext\'02\'00.;}{\levelnumbers\'01;}\fs24\i0\fi-360\li720}\listid1}}"
            + @"{\*\listoverridetable{\listoverride\listid1\listoverridecount0\ls1}}"
            + @"\trowd\trgaph108\cellx2000\cellx4000"
            + @"\pard\plain\intbl\ls1\ilvl0\b\fs28{\b0\fs24 B}\cell"
            + @"\pard\plain\intbl\sl-400\slmult0\b\fs28\cell\row}");

        var row = Assert.IsType<HTable>(document.Sections[0].Blocks.Single()).Rows.Single();
        var item = Assert.IsType<HParagraph>(row.Cells[0].Blocks.Single());
        var list = item.List!;
        Assert.Equal(new HCharFormat(Bold: true, Size: 1200, Font: "Arial"), list.MarkerFormat);
        Assert.Equal((HNumberSuffix.Space, HAlign.Right), (list.Numbering.Levels[0].Suffix, list.Numbering.Levels[0].Align));
        Assert.True(item.MarkFormat.Bold);
        var empty = Assert.IsType<HParagraph>(row.Cells[1].Blocks.Single());
        Assert.Empty(empty.Inlines);
        Assert.Equal(new HLineSpacing(HLineSpacingKind.Fixed, HwpxUnits.FromTwips(400)), empty.Format.LineSpacing);
        Assert.Equal(1400, empty.MarkFormat.Size);

        // Pandoc ends every paragraph with \par, the cell's last one too: no empty paragraph after it.
        var pandoc = Read(Head + @"\trowd\cellx2000\cellx4000{\pard\intbl one\par}{\pard\intbl two\par}\cell{\pard\intbl\par}\cell\row}");
        var cells = Assert.IsType<HTable>(pandoc.Sections[0].Blocks.Single()).Rows.Single().Cells;
        Assert.Equal(["one", "two"], cells[0].Blocks.Select(Text));
        Assert.Single(cells[1].Blocks);
    }

    [Fact]
    public void Cropped_and_mirrored_pictures_in_the_text()
    {
        var image = Read(Head + @"\pard {\*\shppict{\pict{\*\picprop\shplid1025{\sp{\sn shapeType}{\sv 75}}{\sp{\sn fFlipH}{\sv 1}}}"
                              + @"\pngblip\picw4\pich2\picwgoal2000\pichgoal1000\piccropl500\piccropr300\piccropt100\piccropb-50 " + Png() + @"}}\par}")
            .Sections[0].Blocks.OfType<HParagraph>().Single().Inlines.OfType<HImage>().Single();

        Assert.Equal((HwpxUnits.FromTwips(1200), HwpxUnits.FromTwips(900)), (image.Width, image.Height));
        Assert.Equal(new HCrop(0.25, 0.1, 0.15, 0), image.Crop);
        Assert.Equal(new HTransform(0, true, false), image.Transform);
        Assert.Null(image.Anchor);
    }

    [Fact]
    public void The_missing_font_notice_looks_at_the_fonts_the_text_is_set_in()
    {
        var path = Path.Combine(fx.NewFolder(), "fonts.rtf");
        File.WriteAllText(path, @"{\rtf1\ansi\ansicpg949\deff0{\fonttbl{\f0\fnil Default Face;}{\f1\fnil Missing Face;}{\f2\fnil Aliased Face{\*\falt Installed Face};}"
                                + @"{\f3\fnil Unused Face;}{\f4\fnil\fcharset129 \'b8\'bc\'c0\'ba \'b0\'ed\'b5\'f1;}{\f5\fnil Style Face;}{\f6 Symbol;}"
                                + @"{\f7\fnil\fcharset134\uc2\u23435\'cb\'ce\u20307\'cc\'e5;}}"
                                + @"{\stylesheet{\s1\f5 Style;}}{\*\listtable{\list{\listlevel{\leveltext\f6\'01\u-3913 ?;}}}}"
                                + @"\pard\plain\f1 missing {\f2 aliased} {\f4 \'c7\'d1} {\f6 x}{\f7\uc2\u23435\'cb\'ce}\par\plain default\par}", Encoding.Latin1);

        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Installed Face", "Default Face" };
        // Names in \u escapes skip their fallback bytes (\uc2): 宋体, not 宋宋体体.
        Assert.Equal(["Missing Face", "宋体", "맑은 고딕"], DocumentFonts.Missing(path, installed.Contains));
        Assert.Equal(["Aliased Face", "Default Face", "Missing Face", "Symbol", "宋体", "맑은 고딕"],
            RtfReader.Fonts(File.ReadAllBytes(path), DocumentLimits.Default).Fonts);
        // Damage ends the scan without an error.
        File.WriteAllText(path, @"{\rtf1{\fonttbl{\f0 Cut Face;}}\f0 text", Encoding.Latin1);
        Assert.Equal(["Cut Face"], DocumentFonts.Missing(path, _ => false));
        File.WriteAllText(path, "not rtf", Encoding.Latin1);
        Assert.Empty(DocumentFonts.Missing(path, _ => false));
    }
}
