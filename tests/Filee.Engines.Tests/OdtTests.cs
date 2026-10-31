// The built-in ODT reader and writer (Hwp/Hwpx/Odt): an ODT built in code with the parts LibreOffice writes (styles
// and their inheritance, page layouts and master pages with headers and footers, headings, lists, tables, frames,
// shapes, notes, links, bookmarks, fields, text sections) is read into the model, written back and read again to the
// same model; the routes need no optional engine; hostile packages are refused or read within DocumentLimits; and,
// when LibreOffice is installed, it reads what the writer wrote as an independent reader.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Odt;
using ImageMagick;

namespace Filee.Engines.Tests;

public class OdtTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice", "calibre", "ghostscript"];

    /// <summary>Centimetres in HWPUNIT, rounded as the reader rounds each length.</summary>
    private static int Cm(double cm) => (int)Math.Round(cm * 7200 / 2.54);

    private static double Mm(int hwpUnit) => hwpUnit / HwpxUnits.PerMm;

    private string Media() => Path.Combine(fx.NewFolder(), "media");

    // ───────────────────────── The document ─────────────────────────

    private const string NamedStyles = """
        <style:default-style style:family="paragraph">
          <style:paragraph-properties fo:orphans="2" fo:widows="2" style:tab-stop-distance="1.25cm"/>
          <style:text-properties style:font-name="Liberation Serif" fo:font-size="12pt" style:font-name-asian="Noto Serif CJK KR"/>
        </style:default-style>
        <style:default-style style:family="graphic"><style:graphic-properties draw:fill-color="#729fcf" svg:stroke-color="#3465a4"/></style:default-style>
        <style:style style:name="Standard" style:family="paragraph" style:class="text"/>
        <style:style style:name="Text_20_body" style:display-name="Text body" style:family="paragraph" style:parent-style-name="Standard">
          <style:paragraph-properties fo:margin-top="0cm" fo:margin-bottom="0.247cm" fo:line-height="115%"/>
        </style:style>
        <style:style style:name="Heading" style:family="paragraph" style:parent-style-name="Standard">
          <style:paragraph-properties fo:margin-top="0.423cm" fo:margin-bottom="0.212cm" fo:keep-with-next="always"/>
          <style:text-properties style:font-name="Liberation Sans" fo:font-size="14pt"/>
        </style:style>
        <style:style style:name="Heading_20_1" style:display-name="Heading 1" style:family="paragraph" style:parent-style-name="Heading" style:default-outline-level="1">
          <style:text-properties fo:font-size="130%" fo:font-weight="bold"/>
        </style:style>
        <style:style style:name="Emphasis" style:family="text"><style:text-properties fo:font-style="italic"/></style:style>
        <style:style style:name="Internet_20_link" style:family="text">
          <style:text-properties fo:color="#000080" style:text-underline-style="solid" style:text-underline-width="auto" style:text-underline-color="font-color"/>
        </style:style>
        <style:style style:name="Loop1" style:family="paragraph" style:parent-style-name="Loop2"/>
        <style:style style:name="Loop2" style:family="paragraph" style:parent-style-name="Loop1"><style:paragraph-properties fo:text-align="end"/></style:style>
        <text:list-style style:name="Numbering_20_123">
          <text:list-level-style-number text:level="1" style:num-suffix="." style:num-format="1">
            <style:list-level-properties text:list-level-position-and-space-mode="label-alignment">
              <style:list-level-label-alignment text:label-followed-by="listtab" fo:text-indent="-0.635cm" fo:margin-left="1.27cm"/>
            </style:list-level-properties>
          </text:list-level-style-number>
          <text:list-level-style-number text:level="2" style:num-prefix="(" style:num-suffix=")" style:num-format="a">
            <style:list-level-properties text:list-level-position-and-space-mode="label-alignment">
              <style:list-level-label-alignment text:label-followed-by="listtab" fo:text-indent="-0.635cm" fo:margin-left="1.905cm"/>
            </style:list-level-properties>
          </text:list-level-style-number>
          <text:list-level-style-number text:level="3" style:num-suffix="." style:num-format="i" text:display-levels="3"/>
        </text:list-style>
        <text:notes-configuration text:note-class="footnote" style:num-format="1" text:start-value="0" text:footnotes-position="page" text:start-numbering-at="document"/>
        <text:notes-configuration text:note-class="endnote" style:num-format="i" text:start-value="0"/>
        """;

    private const string PageLayouts = """
        <style:page-layout style:name="pm1">
          <style:page-layout-properties fo:page-width="21.001cm" fo:page-height="29.7cm" style:print-orientation="portrait"
            fo:margin-top="2cm" fo:margin-bottom="1.5cm" fo:margin-left="2.5cm" fo:margin-right="2cm"/>
          <style:header-style><style:header-footer-properties fo:min-height="0.6cm" fo:margin-bottom="0.4cm"/></style:header-style>
          <style:footer-style><style:header-footer-properties fo:min-height="0.5cm" fo:margin-top="0.3cm"/></style:footer-style>
        </style:page-layout>
        <style:page-layout style:name="pm2">
          <style:page-layout-properties fo:page-width="29.7cm" fo:page-height="21cm" style:print-orientation="landscape" fo:margin="1.5cm">
            <style:columns fo:column-count="2" fo:column-gap="1cm"><style:column-sep style:width="0.03cm" style:color="#000000" style:height="100%"/></style:columns>
          </style:page-layout-properties>
          <style:header-style/><style:footer-style/>
        </style:page-layout>
        <style:style style:name="P1" style:family="paragraph" style:parent-style-name="Standard"><style:paragraph-properties fo:text-align="center"/></style:style>
        """;

    private const string Masters = """
        <style:master-page style:name="Standard" style:page-layout-name="pm1">
          <style:header><text:p text:style-name="P1">Header text</text:p></style:header>
          <style:header-left><text:p text:style-name="P1">Left header</text:p></style:header-left>
          <style:header-first><text:p/></style:header-first>
          <style:footer><text:p text:style-name="P1">Page <text:page-number text:select-page="current">1</text:page-number> of <text:page-count>3</text:page-count></text:p></style:footer>
        </style:master-page>
        <style:master-page style:name="Landscape" style:page-layout-name="pm2"/>
        """;

    private const string AutomaticStyles = """
        <style:style style:name="P1" style:family="paragraph" style:parent-style-name="Text_20_body">
          <style:paragraph-properties fo:text-align="justify" fo:text-indent="0.5cm" fo:margin-left="1cm">
            <style:tab-stops><style:tab-stop style:position="5cm" style:type="right" style:leader-style="dotted" style:leader-text="."/></style:tab-stops>
          </style:paragraph-properties>
          <style:text-properties fo:color="#336699"/>
        </style:style>
        <style:style style:name="P2" style:family="paragraph" style:parent-style-name="Standard" style:master-page-name="Landscape"><style:paragraph-properties style:page-number="auto"/></style:style>
        <style:style style:name="P3" style:family="paragraph" style:parent-style-name="Standard"><style:paragraph-properties fo:break-before="page" fo:line-height="0.8cm"/></style:style>
        <style:style style:name="P4" style:family="paragraph" style:parent-style-name="Standard" style:list-style-name="L1"/>
        <style:style style:name="T1" style:family="text"><style:text-properties fo:font-weight="bold" fo:font-size="14pt" style:text-underline-style="solid"/></style:style>
        <style:style style:name="T2" style:family="text"><style:text-properties style:text-position="super 58%"/></style:style>
        <style:style style:name="T3" style:family="text" style:parent-style-name="Emphasis">
          <style:text-properties fo:background-color="#ffff00" fo:letter-spacing="0.06cm" style:text-line-through-style="solid"/>
        </style:style>
        <text:list-style style:name="L1">
          <text:list-level-style-bullet text:level="1" text:bullet-char="•">
            <style:list-level-properties text:list-level-position-and-space-mode="label-alignment">
              <style:list-level-label-alignment text:label-followed-by="listtab" fo:text-indent="-0.635cm" fo:margin-left="0.635cm"/>
            </style:list-level-properties>
          </text:list-level-style-bullet>
          <text:list-level-style-bullet text:level="2" text:bullet-char="◦">
            <style:list-level-properties text:space-before="0.635cm" text:min-label-width="0.635cm"/>
          </text:list-level-style-bullet>
        </text:list-style>
        <style:style style:name="Table1" style:family="table"><style:table-properties style:width="10cm" table:align="center"/></style:style>
        <style:style style:name="Table1.A" style:family="table-column"><style:table-column-properties style:column-width="4cm"/></style:style>
        <style:style style:name="Table1.B" style:family="table-column"><style:table-column-properties style:column-width="3cm"/></style:style>
        <style:style style:name="Table1.1" style:family="table-row"><style:table-row-properties style:min-row-height="1cm" fo:keep-together="always"/></style:style>
        <style:style style:name="Table1.A1" style:family="table-cell">
          <style:table-cell-properties fo:background-color="#ffff00" fo:padding="0.1cm" fo:border="0.5pt solid #ff0000" style:vertical-align="middle"/>
        </style:style>
        <style:style style:name="Table1.B2" style:family="table-cell">
          <style:table-cell-properties fo:padding-left="0.2cm" fo:border-left="none" fo:border-bottom="1pt dashed #0000ff"/>
        </style:style>
        <style:style style:name="fr1" style:family="graphic"><style:graphic-properties style:vertical-pos="top" style:vertical-rel="baseline"/></style:style>
        <style:style style:name="fr2" style:family="graphic">
          <style:graphic-properties style:wrap="parallel" style:horizontal-pos="center" style:horizontal-rel="paragraph" style:vertical-pos="from-top"
            style:vertical-rel="paragraph" fo:margin-left="0.2cm" fo:margin-right="0.2cm" fo:border="1pt solid #000000"/>
        </style:style>
        <style:style style:name="fr3" style:family="graphic">
          <style:graphic-properties style:wrap="run-through" style:run-through="background" style:horizontal-pos="from-left" style:horizontal-rel="page"
            style:vertical-pos="from-top" style:vertical-rel="page" draw:fill="solid" draw:fill-color="#ccffcc" fo:padding="0.15cm"
            fo:border="0.06pt solid #000000" draw:textarea-vertical-align="middle"/>
        </style:style>
        <style:style style:name="gr1" style:family="graphic">
          <style:graphic-properties draw:fill="solid" draw:fill-color="#ff8000" svg:stroke-width="0.05cm" svg:stroke-color="#000000" style:wrap="none"
            style:horizontal-pos="from-left" style:horizontal-rel="paragraph" style:vertical-pos="from-top" style:vertical-rel="paragraph"/>
        </style:style>
        <style:style style:name="Sect1" style:family="section"><style:section-properties><style:columns fo:column-count="2" fo:column-gap="0.5cm"/></style:section-properties></style:style>
        """;

    private const string Text = """
        <text:sequence-decls><text:sequence-decl text:display-outline-level="0" text:name="Table"/></text:sequence-decls>
        <text:tracked-changes><text:changed-region text:id="c1"><text:deletion><text:p>deleted text</text:p></text:deletion></text:changed-region></text:tracked-changes>
        <text:h text:style-name="Heading_20_1" text:outline-level="1">Report <text:bookmark text:name="top"/>title</text:h>
        <text:p text:style-name="P1">Plain <text:span text:style-name="T1">bold big</text:span>  and<text:s text:c="2"/>spaces<text:tab/>tab<text:line-break/>next line, E=mc<text:span text:style-name="T2">2</text:span>, <text:span text:style-name="T3">marked</text:span>, <text:a xlink:type="simple" xlink:href="https://example.com/" text:style-name="Internet_20_link">a link</text:a> and <text:a xlink:href="#top">back</text:a>.<text:note text:id="ftn1" text:note-class="footnote"><text:note-citation>1</text:note-citation><text:note-body><text:p text:style-name="Standard">Footnote text.</text:p></text:note-body></text:note> End<office:annotation><dc:creator>Reviewer</dc:creator><text:p>comment</text:p></office:annotation><text:note text:id="edn1" text:note-class="endnote"><text:note-citation>i</text:note-citation><text:note-body><text:p>Endnote text.</text:p></text:note-body></text:note>.<text:soft-page-break/></text:p>
        <text:list xml:id="list1" text:style-name="Numbering_20_123">
          <text:list-item><text:p text:style-name="Standard">One</text:p>
            <text:list><text:list-item><text:p>One a</text:p></text:list-item><text:list-item><text:p>One b</text:p>
              <text:list><text:list-item><text:p>Deep</text:p></text:list-item></text:list></text:list-item></text:list>
            <text:p>One continued</text:p>
          </text:list-item>
          <text:list-item><text:p>Two</text:p></text:list-item>
        </text:list>
        <text:p text:style-name="Standard">Between lists</text:p>
        <text:list text:continue-list="list1" text:style-name="Numbering_20_123"><text:list-item><text:p>Three</text:p></text:list-item></text:list>
        <text:list><text:list-item><text:p text:style-name="P4">Bullet</text:p><text:list><text:list-item><text:p text:style-name="P4">Nested bullet</text:p></text:list-item></text:list></text:list-item></text:list>
        <table:table table:name="Table1" table:style-name="Table1">
          <table:table-column table:style-name="Table1.A"/>
          <table:table-column table:style-name="Table1.B" table:number-columns-repeated="2"/>
          <table:table-header-rows><table:table-row table:style-name="Table1.1">
            <table:table-cell table:style-name="Table1.A1" table:number-columns-spanned="2" office:value-type="string"><text:p>Wide</text:p></table:table-cell>
            <table:covered-table-cell/>
            <table:table-cell table:style-name="Table1.A1" table:number-rows-spanned="2" office:value-type="string"><text:p>Tall</text:p></table:table-cell>
          </table:table-row></table:table-header-rows>
          <table:table-row>
            <table:table-cell office:value-type="string"><text:p>A2</text:p></table:table-cell>
            <table:table-cell table:style-name="Table1.B2" office:value-type="string"><text:p>B2</text:p></table:table-cell>
            <table:covered-table-cell/>
          </table:table-row>
        </table:table>
        <text:p text:style-name="Standard">Picture <draw:frame draw:style-name="fr1" draw:name="Image1" text:anchor-type="as-char" svg:width="2cm" svg:height="1cm" draw:z-index="0"><draw:image xlink:href="Pictures/chart.png" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"/></draw:frame> inline, <draw:frame draw:style-name="fr2" draw:name="Image2" text:anchor-type="paragraph" svg:y="0.5cm" svg:width="3cm" svg:height="1.5cm" draw:z-index="1"><draw:image xlink:href="Pictures/chart.png"/></draw:frame>floating.</text:p>
        <text:p text:style-name="Standard"><draw:frame draw:style-name="fr3" draw:name="Frame1" text:anchor-type="paragraph" svg:x="2cm" svg:y="3cm" svg:width="5cm" draw:z-index="2"><draw:text-box fo:min-height="2cm"><text:p>Boxed text</text:p></draw:text-box></draw:frame><draw:rect draw:style-name="gr1" text:anchor-type="paragraph" svg:x="1cm" svg:y="1cm" svg:width="2cm" svg:height="1cm" draw:corner-radius="0.2cm"/><draw:line draw:style-name="gr1" text:anchor-type="paragraph" svg:x1="4cm" svg:y1="2cm" svg:x2="2cm" svg:y2="3cm"/></text:p>
        <text:table-of-content text:name="Contents"><text:table-of-content-source/><text:index-body><text:index-title text:name="Contents"><text:p>Contents</text:p></text:index-title><text:p>Report title<text:tab/>1</text:p></text:index-body></text:table-of-content>
        <text:section text:style-name="Sect1" text:name="Section1"><text:p text:style-name="Standard">In two columns</text:p></text:section>
        <text:p text:style-name="Standard">After the section</text:p>
        <text:p text:style-name="P3">Fixed line height on a new page</text:p>
        <text:p text:style-name="Loop1">Styles that loop</text:p>
        <text:p text:style-name="P2">Landscape page</text:p>
        <text:p text:style-name="Standard">Still landscape</text:p>
        """;

    private static byte[] Png(uint width = 40, uint height = 20)
    {
        using var image = new MagickImage(MagickColors.Teal, width, height);
        return image.ToByteArray(MagickFormat.Png);
    }

    private string Document()
    {
        var builder = new OdtBuilder
        {
            Title = "Test document",
            Styles = NamedStyles,
            StylesAutomatic = PageLayouts,
            MasterStyles = Masters,
            ContentStyles = AutomaticStyles,
            Body = Text,
        };
        builder.Parts["Pictures/chart.png"] = Png();
        return builder.Save(Path.Combine(fx.NewFolder(), "document.odt"));
    }

    private static List<HParagraph> Paragraphs(HDocument document) => DocxAssert.Paragraphs(document.Sections.SelectMany(s => s.Blocks)).ToList();

    private static HParagraph Find(HDocument document, string start) =>
        Paragraphs(document).First(p => DocxAssert.TextOf(p).StartsWith(start, StringComparison.Ordinal));

    // ───────────────────────── Reading ─────────────────────────

    [Fact]
    public void Reads_styles_pages_lists_tables_frames_and_notes()
    {
        var model = OdtReader.Read(Document(), Media());

        Assert.Equal("Test document", model.Title);
        Assert.Equal(2, model.Sections.Count);
        var first = model.Sections[0];
        Assert.Equal(new HPage(Cm(21.001), Cm(29.7), Cm(2.5), Cm(2), Cm(2), Cm(1.5), Cm(0.6) + Cm(0.4), Cm(0.5) + Cm(0.3), 0), first.Page);
        Assert.Equal([HPageType.Odd, HPageType.Even], first.Headers.Select(h => h.Pages));
        var header = (HParagraph)first.Headers[0].Blocks.Single();
        Assert.Equal("Header text", DocxAssert.TextOf(header));
        Assert.Equal(HAlign.Center, header.Format.Align); // styles.xml's P1, not content.xml's
        Assert.True(first.HideFirstHeader);
        var footer = (HParagraph)Assert.Single(first.Footers, f => f.Pages == HPageType.Both).Blocks.Single();
        Assert.Equal([HFieldKind.PageNumber, HFieldKind.TotalPages], footer.Inlines.OfType<HField>().Select(f => f.Kind));
        Assert.Equal(("DIGIT", "", ""), (first.Footnotes.Format.Type, first.Footnotes.Format.Prefix, first.Footnotes.Format.Suffix));
        Assert.Equal("ROMAN_SMALL", first.Endnotes.Format.Type);

        var landscape = model.Sections[1];
        Assert.True(landscape.Page!.Landscape);
        Assert.Equal((Cm(29.7), Cm(21), Cm(1.5)), (landscape.Page.Width, landscape.Page.Height, landscape.Page.Left));
        Assert.Equal(new HColumns(2, Cm(1), true), landscape.Columns);
        Assert.Empty(landscape.Headers);
        Assert.Equal(["Landscape page", "Still landscape"], landscape.Blocks.OfType<HParagraph>().Select(DocxAssert.TextOf));

        // Headings: inherited formatting (14 pt × 130 %, bold, Heading's font and spacing).
        var heading = Find(model, "Report");
        Assert.Equal(1, heading.HeadingLevel);
        Assert.Equal("Report title", DocxAssert.TextOf(heading));
        Assert.Single(heading.Inlines.OfType<HBookmark>(), b => b.Name == "top");
        var title = heading.Inlines.OfType<HText>().First().Format;
        Assert.Equal((true, 1820, "Liberation Sans"), (title.Bold, title.Size, title.Font));
        Assert.Equal((Cm(0.423), Cm(0.212), true), (heading.Format.Before, heading.Format.After, heading.Format.KeepWithNext));

        // A paragraph: automatic style over "Text body" over the default style.
        var body = Find(model, "Plain");
        Assert.Equal((HAlign.Justify, Cm(1), Cm(0.5), Cm(0.247)), (body.Format.Align, body.Format.Left, body.Format.FirstLine, body.Format.After));
        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 150), body.Format.LineSpacing); // 115 % of single lines
        Assert.Equal(new HTabStop(Cm(5), HTabKind.Right, "DOT"), Assert.Single(body.Format.Tabs!));
        var plain = body.Inlines.OfType<HText>().First();
        Assert.Equal(("Plain ", "#336699", 1200, "Liberation Serif", "Noto Serif CJK KR"), (plain.Text, plain.Format.Color, plain.Format.Size, plain.Format.Font, plain.Format.EastAsianFont));
        var texts = body.Inlines.OfType<HText>().ToList();
        Assert.Equal(" and  spaces", texts[2].Text); // collapsed white space, text:s kept
        Assert.Equal((true, 1400, true), (texts[1].Format.Bold, texts[1].Format.Size, texts[1].Format.Underline));
        Assert.True(texts.Single(t => t.Text == "2").Format.Superscript);
        var marked = texts.Single(t => t.Text == "marked").Format;
        Assert.Equal((true, true, "#FFFF00", 14), (marked.Italic, marked.Strike, marked.Shade, marked.Spacing));
        Assert.Single(body.Inlines.OfType<HTab>());
        Assert.Single(body.Inlines.OfType<HLineBreak>());
        var links = body.Inlines.OfType<HLink>().ToList();
        Assert.Equal(["https://example.com/", "#top"], links.Select(l => l.Target));
        var linkText = Assert.IsType<HText>(Assert.Single(links[0].Content));
        Assert.Equal(("a link", "#000080", true), (linkText.Text, linkText.Format.Color, linkText.Format.Underline));
        var notes = body.Inlines.OfType<HNote>().ToList();
        Assert.Equal([false, true], notes.Select(n => n.Endnote));
        Assert.Equal("Footnote text.", DocxAssert.TextOf((HParagraph)notes[0].Blocks.Single()));
        Assert.DoesNotContain(Paragraphs(model), p => DocxAssert.TextOf(p).Contains("comment", StringComparison.Ordinal) || DocxAssert.TextOf(p).Contains("deleted", StringComparison.Ordinal));
        Assert.EndsWith(" End.", DocxAssert.TextOf(body), StringComparison.Ordinal);

        // Lists: levels, continuation paragraphs, a list continued after other text, a list styled by its paragraphs.
        var one = Find(model, "One");
        Assert.Equal((0, true), (one.List!.Level, one.List.Numbered));
        Assert.Equal(("DIGIT", "^1.", false), (one.List.Numbering.Levels[0].Format, one.List.Numbering.Levels[0].Text, one.List.Numbering.Levels[0].Bullet));
        Assert.Equal(("LATIN_SMALL", "(^2)"), (one.List.Numbering.Levels[1].Format, one.List.Numbering.Levels[1].Text));
        Assert.Equal(("ROMAN_SMALL", "^1.^2.^3."), (one.List.Numbering.Levels[2].Format, one.List.Numbering.Levels[2].Text));
        Assert.Equal((Cm(1.27), -Cm(0.635)), (one.Format.Left, one.Format.FirstLine));
        Assert.Equal((1, true), (Find(model, "One a").List!.Level, Find(model, "One a").List!.Numbered));
        Assert.Equal(2, Find(model, "Deep").List!.Level);
        Assert.Equal((0, false), (Find(model, "One continued").List!.Level, Find(model, "One continued").List!.Numbered));
        Assert.Null(Find(model, "Between").List);
        Assert.Same(one.List.Numbering, Find(model, "Three").List!.Numbering);
        var bullet = Find(model, "Bullet").List!;
        Assert.Equal((true, "•", "◦"), (bullet.Numbering.Levels[0].Bullet, bullet.Numbering.Levels[0].Text, bullet.Numbering.Levels[1].Text));
        Assert.Equal((Cm(0.635) + Cm(0.635), -Cm(0.635)), (Find(model, "Nested bullet").Format.Left, Find(model, "Nested bullet").Format.FirstLine));

        // Table: widths, header row, spans by covered cells, cell fills, borders and padding.
        var table = first.Blocks.OfType<HTable>().Single();
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal([Cm(4), Cm(3), Cm(3)], table.ColumnWidths);
        Assert.Equal(HAlign.Center, table.Align);
        Assert.Equal((true, Cm(1), true), (table.Rows[0].Header, table.Rows[0].Height, table.Rows[0].CantSplit));
        var wide = table.Rows[0].Cells[0];
        Assert.Equal((0, 2, 1, "#FFFF00", HVerticalAlign.Center), (wide.Column, wide.ColSpan, wide.RowSpan, wide.Fill, wide.VerticalAlign));
        Assert.Equal(new HBorder(HBorderStyle.Solid, Mm(50), "#FF0000"), wide.Borders!.Value.Top);
        Assert.Equal(new HInsets(Cm(0.1), Cm(0.1), Cm(0.1), Cm(0.1)), wide.CellMargin);
        Assert.Equal((2, 2), (table.Rows[0].Cells[1].Column, table.Rows[0].Cells[1].RowSpan));
        var b2 = table.Rows[1].Cells[1];
        Assert.Equal((1, HBorderStyle.None, HBorderStyle.Dash, "#0000FF"), (b2.Column, b2.Borders!.Value.Left.Style, b2.Borders.Value.Bottom.Style, b2.Borders.Value.Bottom.Color));
        Assert.Equal(new HInsets(Cm(0.2), 0, 0, 0), b2.CellMargin);

        // Pictures: in the text and floating, one file for both.
        var pictures = Find(model, "Picture").Inlines.OfType<HImage>().ToList();
        Assert.Equal(2, pictures.Count);
        Assert.Equal((null, Cm(2), Cm(1)), (pictures[0].Anchor, pictures[0].Width, pictures[0].Height));
        Assert.Equal(pictures[0].Path, pictures[1].Path);
        Assert.Equal((40, 20), PictureSize.Read(pictures[0].Path));
        Assert.Equal(new HAnchor("COLUMN", "CENTER", 0, "PARA", "TOP", Cm(0.5), "SQUARE", true), pictures[1].Anchor);
        Assert.Equal((HBorderStyle.Solid, Cm(0.2)), (pictures[1].Line.Style, pictures[1].OuterMargin.Left));

        // Text box, rounded rectangle and line.
        var objects = Paragraphs(model).First(p => p.Inlines.OfType<HTextBox>().Any()).Inlines;
        var box = objects.OfType<HTextBox>().Single();
        Assert.Equal((Cm(5), Cm(2), "#CCFFCC", HVerticalAlign.Center), (box.Width, box.Height, box.Fill, box.VerticalAlign));
        Assert.Equal(new HAnchor("PAPER", "LEFT", Cm(2), "PAPER", "TOP", Cm(3), "BEHIND_TEXT", true), box.Anchor);
        Assert.Equal(new HInsets(Cm(0.15), Cm(0.15), Cm(0.15), Cm(0.15)), box.Padding);
        Assert.Equal("Boxed text", DocxAssert.TextOf((HParagraph)box.Blocks.Single()));
        var rectangle = objects.OfType<HShape>().Single(s => s.Kind == HShapeKind.Rectangle);
        Assert.Equal(("#FF8000", 20, "TOP_AND_BOTTOM"), (rectangle.Fill, rectangle.CornerRatio, rectangle.Anchor!.Wrap));
        Assert.Equal(Mm(Cm(0.05)), rectangle.Line.WidthMm);
        var line = objects.OfType<HShape>().Single(s => s.Kind == HShapeKind.Line);
        Assert.Equal((Cm(4) - Cm(2), Cm(3) - Cm(2), true, false), (line.Width, line.Height, line.FlipHorizontal, line.FlipVertical));
        Assert.Equal((Cm(2), Cm(2)), (line.Anchor!.HorizontalOffset, line.Anchor.VerticalOffset));

        // An index keeps its text; a section of two columns; a page break; a style chain that loops.
        Assert.Equal("Contents", DocxAssert.TextOf(Find(model, "Contents")));
        Assert.Equal(new HColumns(2, Cm(0.5), false), Find(model, "In two columns").ColumnsChange);
        Assert.Equal(HColumns.Single, Find(model, "After the section").ColumnsChange);
        var fixedLine = Find(model, "Fixed line");
        Assert.Equal((true, new HLineSpacing(HLineSpacingKind.Fixed, Cm(0.8))), (fixedLine.PageBreakBefore, fixedLine.Format.LineSpacing));
        Assert.Equal(HAlign.Right, Find(model, "Styles that loop").Format.Align);
    }

    // ───────────────────────── Round trip ─────────────────────────

    [Fact]
    public void Written_odt_reads_back_to_the_same_model()
    {
        var model = OdtReader.Read(Document(), Media());
        var folder = fx.NewFolder();
        var written = Path.Combine(folder, "written.odt");
        OdtWriter.Write(model, written);
        ValidPackage(written);
        var again = OdtReader.Read(written, Path.Combine(folder, "media"));
        Assert.Equal(Dump(model), Dump(again));

        // And once more: the writer's own output is stable.
        var twice = Path.Combine(folder, "twice.odt");
        OdtWriter.Write(again, twice);
        Assert.Equal(Dump(again), Dump(OdtReader.Read(twice, Path.Combine(folder, "media2"))));
    }

    /// <summary>The package is what OpenDocument readers expect: mimetype first and stored, well-formed parts, a manifest that lists them.</summary>
    private static void ValidPackage(string odt)
    {
        using var zip = ZipFile.OpenRead(odt);
        var mimetype = zip.Entries[0];
        Assert.Equal(("mimetype", "application/vnd.oasis.opendocument.text"), (mimetype.FullName, new StreamReader(mimetype.Open()).ReadToEnd()));
        Assert.Equal(mimetype.Length, mimetype.CompressedLength); // stored
        var manifest = XDocument.Load(zip.GetEntry("META-INF/manifest.xml")!.Open());
        var listed = manifest.Descendants().Select(e => (string?)e.Attribute(XName.Get("full-path", "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"))).OfType<string>().ToHashSet();
        foreach (var entry in zip.Entries.Where(e => e.FullName is not ("mimetype" or "META-INF/manifest.xml")))
        {
            Assert.Contains(entry.FullName, listed);
            if (entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
                XDocument.Load(entry.Open()); // well-formed
        }
    }

    /// <summary>Everything the model holds that ODT can express, as text: two equal dumps are two equal models.</summary>
    private static string Dump(HDocument document)
    {
        var sb = new StringBuilder();
        var numberings = new Dictionary<HNumbering, int>(ReferenceEqualityComparer.Instance);
        sb.AppendLine($"title {document.Title}");
        foreach (var section in document.Sections)
        {
            sb.AppendLine($"section {section.Page} {section.Columns} hide {section.HideFirstHeader}/{section.HideFirstFooter} start {section.StartPageNumber}");
            sb.AppendLine($"  notes {section.Footnotes.Format} {section.Endnotes.Format}");
            foreach (var header in section.Headers)
                Blocks(sb, $"header {header.Pages}", header.Blocks, 1, numberings);
            foreach (var footer in section.Footers)
                Blocks(sb, $"footer {footer.Pages}", footer.Blocks, 1, numberings);
            Blocks(sb, "body", section.Blocks, 1, numberings);
        }
        return sb.ToString();
    }

    private static void Blocks(StringBuilder sb, string label, IEnumerable<HBlock> blocks, int depth, Dictionary<HNumbering, int> numberings)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine(indent + label);
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph p:
                    var f = p.Format;
                    sb.AppendLine($"{indent}p h{p.HeadingLevel} page {p.PageBreakBefore} column {p.ColumnBreakBefore} columns {p.ColumnsChange} " +
                                  $"{f.Align} {f.Left} {f.Right} {f.FirstLine} {f.Before} {f.After} {f.LineSpacing} {f.KeepWithNext} {f.KeepLines} {f.WidowOrphan} {f.RightToLeft} " +
                                  $"tabs [{string.Join(", ", f.Tabs ?? [])}] mark {p.MarkFormat}");
                    if (p.List is { } list)
                    {
                        if (!numberings.TryGetValue(list.Numbering, out var id))
                        {
                            numberings[list.Numbering] = id = numberings.Count + 1;
                            sb.AppendLine($"{indent}  numbering {id}: {string.Join(" | ", list.Numbering.Levels)}");
                        }
                        sb.AppendLine($"{indent}  list {id} level {list.Level} numbered {list.Numbered}");
                    }
                    Inlines(sb, p.Inlines, depth + 1, numberings);
                    break;
                case HTable t:
                    sb.AppendLine($"{indent}table {t.ColumnCount} [{string.Join(", ", t.ColumnWidths ?? [])}] [{string.Join(", ", t.RelativeWidths ?? [])}] {t.Align} {t.Indent} {t.Borders} {t.CellMargin} page {t.PageBreakBefore}");
                    foreach (var row in t.Rows)
                    {
                        sb.AppendLine($"{indent}  row {row.Height} {row.Header} {row.CantSplit}");
                        foreach (var cell in row.Cells)
                            Blocks(sb, $"cell {cell.Column} {cell.ColSpan}x{cell.RowSpan} {cell.VerticalAlign} {cell.Fill} {cell.Borders} {cell.CellMargin}", cell.Blocks, depth + 2, numberings);
                    }
                    break;
            }
        }
    }

    private static void Inlines(StringBuilder sb, IEnumerable<HInline> inlines, int depth, Dictionary<HNumbering, int> numberings)
    {
        var indent = new string(' ', depth * 2);
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    sb.AppendLine($"{indent}text '{text.Text}' {text.Format}");
                    break;
                case HTab tab:
                    sb.AppendLine($"{indent}tab {tab.Format}");
                    break;
                case HLineBreak lineBreak:
                    sb.AppendLine($"{indent}break {lineBreak.Format}");
                    break;
                case HBookmark bookmark:
                    sb.AppendLine($"{indent}bookmark {bookmark.Name}");
                    break;
                case HField field:
                    sb.AppendLine($"{indent}field {field.Kind} {field.Format}");
                    break;
                case HLink link:
                    sb.AppendLine($"{indent}link {link.Target}");
                    Inlines(sb, link.Content, depth + 1, numberings);
                    break;
                case HNote note:
                    Blocks(sb, $"note {note.Endnote} {note.Number} {note.Format}", note.Blocks, depth, numberings);
                    break;
                case HImage image:
                    sb.AppendLine($"{indent}image {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image.Path)))} {image.Width} {image.Height} {image.Anchor} " +
                                  $"{image.Line} {image.Margin} {image.OuterMargin} {image.ZOrder}");
                    break;
                case HTextBox box:
                    Blocks(sb, $"box {box.Shape} {box.Width} {box.Height} {box.Anchor} {box.Fill} {box.Line} {box.Padding} {box.VerticalAlign} {box.CornerRatio} {box.ZOrder} {box.OuterMargin}",
                        box.Blocks, depth, numberings);
                    break;
                case HShape shape:
                    sb.AppendLine($"{indent}shape {shape.Kind} {shape.Width} {shape.Height} {shape.Anchor} {shape.Fill} {shape.Line} {shape.CornerRatio} {shape.FlipHorizontal} {shape.FlipVertical} {shape.ZOrder}");
                    break;
            }
        }
    }

    // ───────────────────────── Writing other formats ─────────────────────────

    [Fact]
    public async Task Markdown_becomes_an_odt_with_headings_lists_tables_pictures_and_notes()
    {
        var dir = fx.NewFolder();
        using (var chart = new MagickImage(MagickColors.Teal, 160, 90))
            chart.Write(Path.Combine(dir, "chart.png"));
        var md = Path.Combine(dir, "회의록.md");
        await File.WriteAllTextAsync(md, """
            # 주간 회의

            오늘 결정한 것: **배포**, *문서 정리*, [웹 페이지](https://example.com/).

            1. 설치 파일 만들기
               - 서명하기
            2. 문서 고치기

            | 항목 | 담당 |
            |------|------|
            | 설치 | 민지 |

            ![그림](chart.png)

            각주가 있는 문장.[^1]

            [^1]: 각주 내용
            """, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([md], new Preset { TargetFormat = "odt" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var odt = job.Outputs.Single();
        ValidPackage(odt);

        var model = OdtReader.Read(odt, Media());
        Assert.Equal(1, Find(model, "주간 회의").HeadingLevel);
        var sentence = Find(model, "오늘");
        Assert.True(sentence.Inlines.OfType<HText>().Single(t => t.Text == "배포").Format.Bold);
        Assert.True(sentence.Inlines.OfType<HText>().Single(t => t.Text == "문서 정리").Format.Italic);
        Assert.Equal("https://example.com/", sentence.Inlines.OfType<HLink>().Single().Target);
        var first = Find(model, "설치 파일");
        Assert.Equal(("^1.", 0), (first.List!.Numbering.Levels[0].Text, first.List.Level));
        Assert.True(Find(model, "서명하기").List!.Numbering.Levels[1].Bullet);
        Assert.Same(first.List.Numbering, Find(model, "문서 고치기").List!.Numbering);
        Assert.Equal("민지", DocxAssert.TextOf((HParagraph)model.Sections[0].Blocks.OfType<HTable>().Single().Rows[1].Cells[1].Blocks.Single()));
        Assert.Single(Paragraphs(model).SelectMany(p => p.Inlines).OfType<HImage>());
        var note = Paragraphs(model).SelectMany(p => p.Inlines).OfType<HNote>().Single();
        Assert.Equal("각주 내용", string.Concat(note.Blocks.OfType<HParagraph>().Select(DocxAssert.TextOf)).Trim());

        await ConvertibleByLibreOfficeAsync(odt);
    }

    [Fact]
    public void Docx_through_the_model_becomes_an_odt_that_reads_back()
    {
        var source = new DocxBuilder();
        source.Paragraph(DocxBuilder.P(DocxBuilder.R("Word text", "<w:b/>")));
        var docx = source.Save(Path.Combine(fx.NewFolder(), "word.docx"));
        var odt = Path.Combine(fx.NewFolder(), "word.odt");
        OdtWriter.Write(DocxReader.Read(docx, Media()), odt);
        var read = OdtReader.Read(odt, Media());
        Assert.True(Find(read, "Word text").Inlines.OfType<HText>().Single().Format.Bold);
    }

    // ───────────────────────── Routes ─────────────────────────

    private RoutePlanner BuiltInPlanner(params string[] assumeInstalled) =>
        new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }.CreatePlanner(assumeInstalled);

    [Fact]
    public void Odt_routes_need_no_optional_engine()
    {
        var builtIn = BuiltInPlanner("rhwp");
        Assert.Equal("docx-writer", Assert.Single(builtIn.Plan("odt", "docx")!.Steps).Converter.Id);
        Assert.Equal("hwpx-writer", Assert.Single(builtIn.Plan("odt", "hwpx")!.Steps).Converter.Id);
        Assert.Equal(["hwpx-writer", "rhwp"], builtIn.Plan("odt", "pdf")!.Steps.Select(s => s.Converter.Id));
        foreach (var to in new[] { "md", "html", "epub", "txt" })
            Assert.DoesNotContain(builtIn.Plan("odt", to)!.Steps, s => OptionalEngines.Contains(s.Converter.Id));
        foreach (var from in new[] { "docx", "md", "html", "hwpx", "pdf", "xlsx", "pptx" })
            Assert.Equal("odt-writer", Assert.Single(builtIn.Plan(from, "odt")!.Steps).Converter.Id);
        Assert.Null(builtIn.Plan("odt", "odt"));

        // The built-in reader and writer win when Pandoc and LibreOffice are installed too.
        var all = fx.Catalog.CreatePlanner(["pandoc", "libreoffice", "rhwp"]);
        Assert.Equal("docx-writer", Assert.Single(all.Plan("odt", "docx")!.Steps).Converter.Id);
        Assert.Equal("odt-writer", Assert.Single(all.Plan("docx", "odt")!.Steps).Converter.Id);
        Assert.Equal("odt-writer", Assert.Single(all.Plan("md", "odt")!.Steps).Converter.Id);
        Assert.False(DocumentReaders.NeedsPandoc("odt"));
    }

    // ───────────────────────── Hostile input ─────────────────────────

    private static Exception? OnSmallStack(Action action, int seconds = 60)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, 1024 * 1024);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)), $"Still running after {seconds} s");
        return error;
    }

    private static ConversionRefusedException Refused(Action action)
    {
        var refusal = Assert.IsType<ConversionRefusedException>(OnSmallStack(action));
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
        return refusal;
    }

    private string Odt(string body, Action<OdtBuilder>? setup = null)
    {
        var builder = new OdtBuilder { Body = body };
        setup?.Invoke(builder);
        return builder.Save(Path.Combine(fx.NewFolder(), "input.odt"));
    }

    [Fact]
    public void An_encrypted_odt_is_refused_with_a_clear_message()
    {
        var odt = Odt("", builder =>
        {
            builder.RawContent = RandomNumberGenerator.GetBytes(512);
            builder.ManifestEntries = "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\" manifest:size=\"1000\">" +
                "<manifest:encryption-data manifest:checksum-type=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#sha256-1k\" manifest:checksum=\"AAAA\">" +
                "<manifest:algorithm manifest:algorithm-name=\"http://www.w3.org/2001/04/xmlenc#aes256-cbc\" manifest:initialisation-vector=\"AAAA\"/>" +
                "<manifest:key-derivation manifest:key-derivation-name=\"PBKDF2\" manifest:key-size=\"32\" manifest:iteration-count=\"100000\" manifest:salt=\"AAAA\"/>" +
                "</manifest:encryption-data></manifest:file-entry>";
        });
        var error = Assert.IsType<InvalidDataException>(OnSmallStack(() => OdtReader.Read(odt, Media())));
        Assert.Contains("password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_that_are_no_odt_are_invalid_data()
    {
        var spreadsheet = Odt("<text:p>x</text:p>", builder => builder.MimeType = "application/vnd.oasis.opendocument.spreadsheet");
        Assert.Contains("not an OpenDocument text", Assert.IsType<InvalidDataException>(OnSmallStack(() => OdtReader.Read(spreadsheet, Media()))).Message);
        var noZip = Path.Combine(fx.NewFolder(), "plain.odt");
        File.WriteAllText(noZip, "not a package");
        Assert.IsType<InvalidDataException>(OnSmallStack(() => OdtReader.Read(noZip, Media())));
        var damaged = Odt("", builder => builder.RawContent = "<office:document-content><office:body>"u8.ToArray());
        Assert.Contains("content.xml", Assert.IsType<InvalidDataException>(OnSmallStack(() => OdtReader.Read(damaged, Media()))).Message);
    }

    [Fact]
    public void Zip_bombs_and_deep_xml_are_refused()
    {
        var bomb = Odt("<text:p>" + new string(' ', 6 << 20) + "</text:p>");
        Assert.Contains("times its packed size", Refused(() => OdtReader.Read(bomb, Media())).Detail);

        var nested = string.Concat(Enumerable.Repeat("<text:span>", 100_000)) + "x" + string.Concat(Enumerable.Repeat("</text:span>", 100_000));
        Assert.Contains("512 levels deep", Refused(() => OdtReader.Read(Odt($"<text:p>{nested}</text:p>"), Media())).Detail);

        var many = Odt(string.Concat(Enumerable.Repeat("<text:p>x</text:p>", 101)));
        Assert.Contains("more than 100 paragraphs", Refused(() => OdtReader.Read(many, Media(), DocumentLimits.Default with { MaxParagraphs = 100 })).Detail);
    }

    [Fact]
    public void Tables_nested_to_the_limit_are_read_and_one_deeper_refused()
    {
        string Write(int levels)
        {
            HBlock content = new HParagraph { Inlines = { new HText("inside", default) } };
            for (var i = 0; i < levels; i++)
            {
                var table = new HTable { ColumnCount = 1 };
                var cell = new HCell();
                cell.Blocks.Add(content);
                var row = new HRow();
                row.Cells.Add(cell);
                table.Rows.Add(row);
                content = table;
            }
            var document = new HDocument();
            document.Sections.Add(new HSection { Blocks = { content } });
            var path = Path.Combine(fx.NewFolder(), $"nested-{levels}.odt");
            OdtWriter.Write(document, path);
            return path;
        }
        static int Depth(IEnumerable<HBlock> blocks) =>
            blocks.OfType<HTable>().Select(t => 1 + t.Rows.SelectMany(r => r.Cells).Select(c => Depth(c.Blocks)).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
        HDocument? read = null;
        Assert.Null(OnSmallStack(() => read = OdtReader.Read(Write(32), Media())));
        Assert.Equal(32, Depth(read!.Sections[0].Blocks));
        Assert.Contains("nested more than 32 levels deep", Refused(() => OdtReader.Read(Write(33), Media())).Detail);

        var lists = string.Concat(Enumerable.Repeat("<text:list><text:list-item>", 40)) + "<text:p>x</text:p>" + string.Concat(Enumerable.Repeat("</text:list-item></text:list>", 40));
        Refused(() => OdtReader.Read(Odt(lists), Media()));
    }

    [Fact]
    public void Repeats_and_counts_at_the_end_of_the_number_range_stay_within_the_limits()
    {
        var table = "<table:table><table:table-column table:number-columns-repeated=\"2147483647\"/>" +
                    "<table:table-row table:number-rows-repeated=\"2147483647\"><table:table-cell table:number-columns-repeated=\"2147483647\" " +
                    "table:number-columns-spanned=\"2147483647\" table:number-rows-spanned=\"2147483647\"/></table:table-row></table:table>";
        var limits = DocumentLimits.Default with { MaxParagraphs = 10_000 };
        Assert.Contains("table cells", Refused(() => OdtReader.Read(Odt(table), Media(), limits)).Detail);

        var small = "<table:table><table:table-column table:number-columns-repeated=\"2147483647\"/>" +
                    "<table:table-row><table:table-cell table:number-columns-spanned=\"2147483647\" table:number-rows-spanned=\"2147483647\"><text:p>x</text:p></table:table-cell></table:table-row></table:table>" +
                    "<text:p>a<text:s text:c=\"2147483647\"/>b</text:p>";
        HDocument? read = null;
        Assert.Null(OnSmallStack(() => read = OdtReader.Read(Odt(small), Media())));
        var cell = read!.Sections[0].Blocks.OfType<HTable>().Single().Rows[0].Cells[0];
        Assert.Equal((DocumentLimits.Default.MaxTableColumns, 1), (cell.ColSpan, cell.RowSpan));
        Assert.InRange(DocxAssert.TextOf(read.Sections[0].Blocks.OfType<HParagraph>().Last()).Length, 3, 10_002);
    }

    [Fact]
    public void Pictures_outside_the_package_or_claiming_gigapixels_are_left_out()
    {
        var huge = DocumentFuzzTests.TinyPng(4, 3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(16), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(20), 100_000);
        static string Frame(string href) =>
            $"<draw:frame text:anchor-type=\"as-char\" svg:width=\"1cm\" svg:height=\"1cm\"><draw:image xlink:href=\"{href}\"/></draw:frame>";
        var odt = Odt("<text:p>" + Frame("Pictures/huge.png") + Frame("../../outside.png") + Frame("file:///C:/Windows/win.ini") + Frame("Pictures/small.png") +
                      "<draw:frame text:anchor-type=\"as-char\" svg:width=\"1cm\" svg:height=\"1cm\"><draw:image><office:binary-data>" +
                      Convert.ToBase64String(DocumentFuzzTests.TinyPng(2, 2)) + "</office:binary-data></draw:image></draw:frame></text:p>", builder =>
        {
            builder.Parts["Pictures/huge.png"] = huge;
            builder.Parts["Pictures/small.png"] = DocumentFuzzTests.TinyPng(4, 3);
        });
        HDocument? read = null;
        var media = Media();
        Assert.Null(OnSmallStack(() => read = OdtReader.Read(odt, media)));
        var images = read!.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().ToList();
        Assert.Equal([(4, 3), (2, 2)], images.Select(i => PictureSize.Read(i.Path)));
        Assert.Equal(2, Directory.GetFiles(media).Length);
    }

    [Fact]
    public void A_note_inside_a_note_is_read_once()
    {
        var note = "<text:note text:note-class=\"footnote\"><text:note-citation>1</text:note-citation><text:note-body><text:p>outer" +
                   "<text:note text:note-class=\"footnote\"><text:note-citation>2</text:note-citation><text:note-body><text:p>inner</text:p></text:note-body></text:note>" +
                   "</text:p></text:note-body></text:note>";
        HDocument? read = null;
        Assert.Null(OnSmallStack(() => read = OdtReader.Read(Odt($"<text:p>text{note}</text:p>"), Media())));
        var outer = read!.Sections[0].Blocks.OfType<HParagraph>().Single().Inlines.OfType<HNote>().Single();
        Assert.Empty(outer.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HNote>());
    }

    // ───────────────────────── LibreOffice as an independent reader ─────────────────────────

    [Fact]
    public async Task LibreOffice_reads_the_written_odt()
    {
        var model = OdtReader.Read(Document(), Media());
        var odt = Path.Combine(fx.NewFolder(), "written.odt");
        OdtWriter.Write(model, odt);
        if (await ConvertibleByLibreOfficeAsync(odt) is not { } docx)
        {
            Assert.Skip("LibreOffice not found (pwsh build/fetch-engines.ps1 -Only libreoffice)");
            return;
        }
        var read = DocxReader.Read(docx, Media());
        var texts = Paragraphs(read).Select(DocxAssert.TextOf).ToList();
        foreach (var expected in new[] { "Report title", "Between lists", "Wide", "Boxed text", "Landscape page" })
            Assert.Contains(texts, t => t.Contains(expected, StringComparison.Ordinal));
        Assert.Contains(read.Sections, s => s.Page?.Landscape == true);
    }

    /// <summary>
    /// Converts the ODT to PDF and DOCX with LibreOffice when it is installed (an independent ODF reader): the PDF has
    /// pages, the DOCX is returned to be read back. Null when LibreOffice is not available.
    /// </summary>
    private async Task<string?> ConvertibleByLibreOfficeAsync(string odt)
    {
        var libreOffice = fx.Converters.Single(c => c.Id == "libreoffice");
        if (!fx.Catalog.StatusOf(libreOffice).IsAvailable || !OperatingSystem.IsWindows())
            return null;
        var pdf = Path.ChangeExtension(odt, ".lo.pdf");
        var docx = Path.ChangeExtension(odt, ".lo.docx");
        await HwpxAssert.LibreOfficeGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            foreach (var (target, path) in new[] { ("pdf", pdf), ("docx", docx) })
            {
                var written = await libreOffice.ConvertAsync(new ConversionStep(odt, "odt", target, new Preset(), new HwpxAssert.FixedPath(path), Path.GetDirectoryName(odt)!),
                    null, TestContext.Current.CancellationToken);
                Assert.True(new FileInfo(written.Single()).Length > 1000);
            }
        }
        finally
        {
            HwpxAssert.LibreOfficeGate.Release();
        }
        Assert.True(PDFtoImage.Conversion.GetPageCount(await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken)) >= 1);
        return docx;
    }
}
