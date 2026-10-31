// DocBook without Pandoc (Hwp/Hwpx/Markup/DocBookReader, DocBookWriter): an article written here in DocBook 5 (and a
// DocBook 4 one with its OASIS DOCTYPE) reads into the model, round-trips through the writer, converts to DOCX / HWPX
// / PDF / HTML / Markdown with Pandoc absent, and hostile XML (entity expansion, DTDs, nesting) is refused.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using static Filee.Engines.Tests.MarkupTestKit;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the PDF check turns the Filee renderer on, which is process-wide
public class DocBookTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    internal const string Article = """
        <?xml version="1.0" encoding="UTF-8"?>
        <article xmlns="http://docbook.org/ns/docbook" xmlns:xlink="http://www.w3.org/1999/xlink" version="5.0">
          <info><title>User manual</title><author><personname>Someone</personname></author></info>
          <section xml:id="install">
            <title>Installing</title>
            <para>Filee is <emphasis role="bold">fast</emphasis> and <emphasis>small</emphasis>; run <command>filee --help</command>.
              Water is H<subscript>2</subscript>O and e = mc<superscript>2</superscript>. See
              <link xlink:href="https://filee.sh/">the site</link> and <xref linkend="use"/>.<footnote><para>The note's text.</para></footnote></para>
            <itemizedlist>
              <listitem><para>first</para>
                <orderedlist numeration="loweralpha"><listitem><para>nested</para></listitem></orderedlist></listitem>
              <listitem><para>second</para></listitem>
            </itemizedlist>
            <programlisting language="sh">dotnet build
        dotnet test</programlisting>
            <note><para>Back up your files.</para></note>
            <blockquote><para>A quoted line.</para></blockquote>
            <variablelist><varlistentry><term>term</term><listitem><para>Its definition.</para></listitem></varlistentry></variablelist>
            <informaltable>
              <tgroup cols="2">
                <colspec colname="c1"/><colspec colname="c2"/>
                <thead><row><entry>Name</entry><entry>Size</entry></row></thead>
                <tbody>
                  <row><entry morerows="1">Seoul</entry><entry>9.7</entry></row>
                  <row><entry>9.8</entry></row>
                  <row><entry namest="c1" nameend="c2">Both columns</entry></row>
                </tbody>
              </tgroup>
            </informaltable>
            <section xml:id="use">
              <title>Using</title>
              <literallayout>Line one
        line two</literallayout>
              <indexterm><primary>hidden</primary></indexterm>
            </section>
          </section>
        </article>
        """;

    private HDocument Read(string xml, DocumentLimits? limits = null) =>
        DocBookReader.Read(Encoding.UTF8.GetBytes(xml), fx.NewFolder(), limits ?? DocumentLimits.Default);

    [Fact]
    public void An_article_becomes_the_model()
    {
        var document = Read(Article);
        var paragraphs = Paragraphs(document);

        Assert.Equal("User manual", document.Title);
        Assert.Equal([(1, "Installing"), (2, "Using")], paragraphs.Where(p => p.HeadingLevel > 0).Select(p => (p.HeadingLevel, InlineText(p.Inlines))));
        var body = paragraphs[1];
        Assert.Equal("Filee is fast and small; run filee --help. Water is H2O and e = mc2. See the site and use.", InlineText(body.Inlines));
        var runs = body.Inlines.OfType<HText>().ToList();
        Assert.True(runs.Single(r => r.Text == "fast").Format.Bold);
        Assert.True(runs.Single(r => r.Text == "small").Format.Italic);
        Assert.Equal(HtmlReader.CodeShade, runs.Single(r => r.Text == "filee --help").Format.Shade);
        Assert.True(runs.Single(r => r.Text == "2" && r.Format.Subscript is true).Format.Subscript);
        Assert.Equal(["https://filee.sh/", "#use"], body.Inlines.OfType<HLink>().Select(l => l.Target));
        Assert.Equal("The note's text.", InlineText(((HParagraph)body.Inlines.OfType<HNote>().Single().Blocks.Single()).Inlines));
        // The section the cross reference points at keeps its id as a bookmark.
        Assert.Equal("use", paragraphs.Single(p => p.HeadingLevel == 2).Inlines.OfType<HBookmark>().Single().Name);

        Assert.Equal([("first", 0, true), ("nested", 1, false), ("second", 0, true)],
            paragraphs.Where(p => p.List is not null).Select(p => (InlineText(p.Inlines), p.List!.Level, p.List.Numbering.Levels[p.List.Level].Bullet)));
        Assert.Equal("LATIN_SMALL", paragraphs.Single(p => InlineText(p.Inlines) == "nested").List!.Numbering.Levels[1].Format);
        Assert.Equal(["dotnet build", "dotnet test"], paragraphs.Where(MarkupBlocks.IsCode).Select(p => InlineText(p.Inlines)));
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "Note").Inlines.OfType<HText>().Single().Format.Bold);
        Assert.Equal(MarkupBlocks.QuoteIndent, paragraphs.Single(p => InlineText(p.Inlines) == "A quoted line.").Format.Left);
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "term").Inlines.OfType<HText>().Single().Format.Bold);

        var table = document.Sections[0].Blocks.OfType<HTable>().Single();
        Assert.True(table.Rows[0].Header);
        Assert.Equal(2, table.Rows[1].Cells[0].RowSpan);
        Assert.Equal(2, table.Rows[3].Cells.Single().ColSpan);
        Assert.Equal("Line one\nline two", InlineText(paragraphs.Single(p => InlineText(p.Inlines).StartsWith("Line one", StringComparison.Ordinal)).Inlines));
        Assert.DoesNotContain(Texts(document), t => t.Contains("hidden", StringComparison.Ordinal) || t.Contains("Someone", StringComparison.Ordinal));
    }

    [Fact]
    public void An_article_round_trips_through_the_writer()
    {
        var first = Read(Article);
        var written = DocBookWriter.Write(first, _ => null);
        var second = Read(written);

        Assert.Equal(first.Title, second.Title);
        Assert.Equal(MarkupShape.Of(first), MarkupShape.Of(second));
        Assert.Equal(written, DocBookWriter.Write(second, _ => null));
        Assert.Contains("xmlns=\"http://docbook.org/ns/docbook\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void DocBook_4_with_its_oasis_doctype_is_read_without_the_dtd()
    {
        var document = Read("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE article PUBLIC "-//OASIS//DTD DocBook XML V4.5//EN" "http://www.oasis-open.org/docbook/xml/4.5/docbookx.dtd">
            <article><articleinfo><title>Old manual</title></articleinfo>
              <sect1 id="a"><title>Start</title><para>See <ulink url="https://example.invalid/">the page</ulink>.</para></sect1>
            </article>
            """);

        Assert.Equal("Old manual", document.Title);
        Assert.Equal((1, "Start"), (Paragraphs(document)[0].HeadingLevel, InlineText(Paragraphs(document)[0].Inlines)));
        Assert.Equal("https://example.invalid/", Paragraphs(document)[1].Inlines.OfType<HLink>().Single().Target);
    }

    [Fact]
    public async Task Articles_convert_without_pandoc()
    {
        var source = Path.Combine(fx.NewFolder(), "manual.dbk");
        await File.WriteAllTextAsync(source, Article, TestContext.Current.CancellationToken);

        await ConvertsEverywhereWithoutPandocAsync(fx, source, "Installing", "dotnet build", "Seoul");
    }

    [Fact]
    public void Hostile_xml_is_refused()
    {
        // Entities that expand a billion times (an internal subset), and entities only a DTD would define.
        var laughs = "<?xml version=\"1.0\"?><!DOCTYPE article [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">"
                     + "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]>"
                     + "<article><para>&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;</para></article>";
        Assert.Throws<InvalidDataException>(() => Read(laughs));
        Assert.Throws<InvalidDataException>(() => Read("<!DOCTYPE article SYSTEM \"file:///etc/passwd\"><article><para>&xxe;</para></article>"));
        Assert.Throws<InvalidDataException>(() => Read("<article><para>&nbsp;</para></article>"));
        Assert.Throws<InvalidDataException>(() => Read("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body/></html>"));
        Assert.Throws<InvalidDataException>(() => Read("<article><para>cut"));

        // Nested far beyond the XML limit, and lists, quotes and tables beyond the nesting limit.
        Assert.Throws<ConversionRefusedException>(() => Read("<article>" + string.Concat(Enumerable.Repeat("<section>", 100_000)) + string.Concat(Enumerable.Repeat("</section>", 100_000)) + "</article>"));
        var lists = "<article>" + string.Concat(Enumerable.Repeat("<itemizedlist><listitem>", 40)) + "<para>x</para>" + string.Concat(Enumerable.Repeat("</listitem></itemizedlist>", 40)) + "</article>";
        Assert.Throws<ConversionRefusedException>(() => Read(lists));
        var quotes = "<article>" + string.Concat(Enumerable.Repeat("<blockquote>", 40)) + "<para>x</para>" + string.Concat(Enumerable.Repeat("</blockquote>", 40)) + "</article>";
        Assert.Throws<ConversionRefusedException>(() => Read(quotes));

        // A table claiming a million columns is cut; rows past the paragraph limit are refused.
        var wide = Read("<article><informaltable><tgroup cols=\"1000000\"><tbody><row><entry>x</entry></row></tbody></tgroup></informaltable></article>");
        Assert.Equal(DocumentLimits.Default.MaxTableColumns, wide.Sections[0].Blocks.OfType<HTable>().Single().ColumnCount);
        var limits = DocumentLimits.Default with { MaxParagraphs = 100 };
        Assert.Throws<ConversionRefusedException>(() => Read("<article>" + string.Concat(Enumerable.Repeat("<para>x</para>", 200)) + "</article>", limits));
    }
}
