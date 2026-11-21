// Character style names, comments and tracked changes in the document model: a model built in code is written as
// DOCX (schema-valid) and RTF and read back into the same model; Word-like DOCX, RTF, ODT and HWPX built in code are
// read into it; the other writers keep showing the document with its changes made. Also the RTF reader's drawings
// without text, \trkeep and \leveljcn, and symbol-font list bullets that keep their codes for the layout.

using System.IO.Compression;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Typesetting;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class CommentRevisionTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static CommentRevisionTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly DateTime Written = new(2026, 3, 14, 9, 26, 0, DateTimeKind.Utc);
    private static readonly HCharFormat Plain = new(Size: 1000);

    /// <summary>
    /// "Kept [styled] [commented] [inserted] [deleted]": a run in the character style "Strong Emphasis", a comment on
    /// "commented", a tracked insertion and a tracked deletion.
    /// </summary>
    private static HDocument Model()
    {
        var document = new HDocument();
        var section = new HSection();
        document.Sections.Add(section);
        var paragraph = new HParagraph { MarkFormat = Plain };
        var comment = new HComment { Author = "Kim Minsu", Initials = "KM", Date = Written };
        var note = new HParagraph { MarkFormat = Plain };
        note.Inlines.Add(new HText("Check this.", Plain));
        comment.Blocks.Add(note);
        var deletion = new HDeletion(new HRevision("Lee", Written));
        deletion.Content.Add(new HText("old", Plain));
        paragraph.Inlines.AddRange([
            new HText("Kept ", Plain),
            new HText("styled", Plain with { Bold = true, Layout = new HCharLayout(Style: "Strong Emphasis") }),
            new HText(" ", Plain),
            new HCommentStart(comment),
            new HText("commented", Plain),
            comment,
            new HText(" ", Plain),
            new HText("inserted", Plain with { Layout = new HCharLayout(Inserted: new HRevision("Park", Written)) }),
            deletion,
        ]);
        section.Blocks.Add(paragraph);
        return document;
    }

    /// <summary>The model <see cref="Model"/> made, as read back.</summary>
    private static void AssertModel(HDocument read)
    {
        var inlines = read.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).ToList();
        var styled = Assert.Single(inlines.OfType<HText>(), t => t.Text == "styled");
        Assert.Equal("Strong Emphasis", styled.Format.Layout?.Style);
        Assert.True(styled.Format.Bold);

        var start = Assert.Single(inlines.OfType<HCommentStart>());
        var comment = Assert.Single(inlines.OfType<HComment>());
        Assert.Same(start.Comment, comment);
        Assert.True(inlines.IndexOf(start) < inlines.FindIndex(i => i is HText { Text: "commented" }));
        Assert.True(inlines.IndexOf(comment) > inlines.FindIndex(i => i is HText { Text: "commented" }));
        Assert.Equal(("Kim Minsu", "KM", Written), (comment.Author, comment.Initials, comment.Date));
        Assert.Equal("Check this.", string.Concat(comment.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text)).Trim());

        var inserted = Assert.Single(inlines.OfType<HText>(), t => t.Text.Contains("inserted", StringComparison.Ordinal));
        Assert.Equal(new HRevision("Park", Written), inserted.Format.Layout?.Inserted);
        Assert.Null(styled.Format.Layout?.Inserted);

        var deletion = Assert.Single(inlines.OfType<HDeletion>());
        Assert.Equal(new HRevision("Lee", Written), deletion.Revision);
        Assert.Equal("old", string.Concat(deletion.Content.OfType<HText>().Select(t => t.Text)));
        // The deleted text is not text of the paragraph.
        Assert.DoesNotContain(inlines.OfType<HText>(), t => t.Text.Contains("old", StringComparison.Ordinal));
    }

    // ───────────────────────── DOCX ─────────────────────────

    [Fact]
    public void Docx_writes_character_styles_comments_and_tracked_changes_and_reads_them_back()
    {
        var path = Path.Combine(fx.NewFolder(), "revisions.docx");
        DocxWriter.Write(Model(), path);

        DocxAssert.ValidPackage(path);
        var body = DocxAssert.Body(path);
        Assert.Contains(body.Descendants(DocxAssert.W + "rStyle"), s => (string?)s.Attribute(DocxAssert.W + "val") == "StrongEmphasis");
        var styles = DocxAssert.Body(path, "word/styles.xml");
        var style = Assert.Single(styles.Elements(DocxAssert.W + "style"), s => (string?)s.Attribute(DocxAssert.W + "styleId") == "StrongEmphasis");
        Assert.Equal("character", (string?)style.Attribute(DocxAssert.W + "type"));
        Assert.Equal("Strong Emphasis", (string?)style.Element(DocxAssert.W + "name")?.Attribute(DocxAssert.W + "val"));

        Assert.Single(body.Descendants(DocxAssert.W + "commentRangeStart"));
        Assert.Single(body.Descendants(DocxAssert.W + "commentRangeEnd"));
        Assert.Single(body.Descendants(DocxAssert.W + "commentReference"));
        var comments = DocxAssert.Body(path, "word/comments.xml");
        var comment = Assert.Single(comments.Elements(DocxAssert.W + "comment"));
        Assert.Equal(("Kim Minsu", "KM", "2026-03-14T09:26:00Z"),
            ((string?)comment.Attribute(DocxAssert.W + "author"), (string?)comment.Attribute(DocxAssert.W + "initials"), (string?)comment.Attribute(DocxAssert.W + "date")));

        var insertion = Assert.Single(body.Descendants(DocxAssert.W + "ins"));
        Assert.Equal("Park", (string?)insertion.Attribute(DocxAssert.W + "author"));
        var deletion = Assert.Single(body.Descendants(DocxAssert.W + "del"));
        Assert.Equal("Lee", (string?)deletion.Attribute(DocxAssert.W + "author"));
        Assert.Equal("old", deletion.Descendants(DocxAssert.W + "delText").Single().Value);
        Assert.DoesNotContain(body.Descendants(DocxAssert.W + "t"), t => t.Value.Contains("old", StringComparison.Ordinal));

        AssertModel(DocxAssert.ReadBack(path));
    }

    [Fact]
    public void Docx_reader_reads_words_comments_moves_and_deleted_fields()
    {
        var builder = new DocxBuilder();
        builder.Styles += "<w:style w:type=\"character\" w:customStyle=\"1\" w:styleId=\"Code\"><w:name w:val=\"Inline Code\"/><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/></w:rPr></w:style>";
        builder.Paragraph(P(
            R("Use ") + R("x()", "<w:rStyle w:val=\"Code\"/>") +
            "<w:commentRangeStart w:id=\"7\"/>" + R("here") + "<w:commentRangeEnd w:id=\"7\"/><w:r><w:commentReference w:id=\"7\"/></w:r>" +
            "<w:moveFrom w:id=\"1\" w:author=\"A\" w:date=\"2026-03-14T09:26:00Z\"><w:r><w:delText>moved</w:delText></w:r></w:moveFrom>" +
            "<w:del w:id=\"2\" w:author=\"B\"><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:delInstrText> PAGE </w:delInstrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:delText>9</w:delText></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
            "<w:r><w:br w:type=\"page\"/></w:r></w:del>" +
            "<w:ins w:id=\"3\" w:author=\"C\"><w:r><w:t>after</w:t></w:r></w:ins>"));
        var path = builder.Save(Path.Combine(fx.NewFolder(), "word.docx"));
        AddComments(path, "<w:comment w:id=\"7\" w:author=\"Reviewer\" w:date=\"2026-03-14T09:26:00Z\"><w:p><w:r><w:annotationRef/></w:r>" +
            // A comment referring to itself is not read again.
            "<w:r><w:t>Why here?</w:t></w:r><w:r><w:commentReference w:id=\"7\"/></w:r></w:p></w:comment>");

        var document = DocxReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media"));

        var paragraph = Assert.Single(document.Sections[0].Blocks.OfType<HParagraph>());
        var inlines = paragraph.Inlines;
        Assert.Equal("Inline Code", inlines.OfType<HText>().Single(t => t.Text == "x()").Format.Layout?.Style);
        Assert.Null(inlines.OfType<HText>().Single(t => t.Text == "Use ").Format.Layout?.Style);
        var comment = Assert.Single(inlines.OfType<HComment>());
        Assert.Equal("Reviewer", comment.Author);
        Assert.Equal("Why here?", HDocumentWalker.Text(((HParagraph)comment.Blocks[0]).Inlines));
        var deletions = inlines.OfType<HDeletion>().ToList();
        Assert.Equal(2, deletions.Count);
        Assert.Equal(("A", "moved"), (deletions[0].Revision.Author, HDocumentWalker.Text(deletions[0].Content)));
        // The deleted field ended inside the deletion, so the text after it shows; its page break went with it.
        Assert.Equal("C", inlines.OfType<HText>().Single(t => t.Text == "after").Format.Layout?.Inserted?.Author);
        Assert.False(paragraph.PageBreakBefore);
        Assert.Equal("Use x()hereafter", HDocumentWalker.Text(inlines));
    }

    /// <summary>Adds word/comments.xml (w:comment elements) to a package DocxBuilder saved.</summary>
    private static void AddComments(string docx, string comments)
    {
        using var zip = ZipFile.Open(docx, ZipArchiveMode.Update);
        void Replace(string name, Func<string, string> change)
        {
            var entry = zip.GetEntry(name)!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(change(xml));
        }
        Replace("[Content_Types].xml", xml => xml.Replace("</Types>",
            "<Override PartName=\"/word/comments.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml\"/></Types>", StringComparison.Ordinal));
        Replace("word/_rels/document.xml.rels", xml => xml.Replace("</Relationships>",
            "<Relationship Id=\"rIdComments\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments\" Target=\"comments.xml\"/></Relationships>", StringComparison.Ordinal));
        using var part = new StreamWriter(zip.CreateEntry("word/comments.xml").Open(), new UTF8Encoding(false));
        part.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:comments {Namespaces}>{comments}</w:comments>");
    }

    // ───────────────────────── RTF ─────────────────────────

    [Fact]
    public void Rtf_writes_character_styles_comments_and_tracked_changes_and_reads_them_back()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "revisions.rtf");
        RtfWriter.Write(Model(), path, TestContext.Current.CancellationToken);
        var bytes = File.ReadAllBytes(path);
        RtfWriterTests.AssertValidRtf(bytes);
        var rtf = Encoding.ASCII.GetString(bytes);
        Assert.Contains("{\\*\\cs20\\additive Strong Emphasis;}", rtf, StringComparison.Ordinal);
        Assert.Contains("{\\*\\revtbl{Unknown;}{Park;}{Lee;}}", rtf, StringComparison.Ordinal);
        Assert.Contains("\\chatn", rtf, StringComparison.Ordinal);
        Assert.Contains("\\deleted\\revauthdel2", rtf, StringComparison.Ordinal);

        AssertModel(RtfReader.Read(bytes, Path.Combine(folder, "media"), DocumentLimits.Default));
    }

    [Fact]
    public void Rtf_reader_reads_words_annotations_and_revisions()
    {
        // As Word writes them: the style sheet's character style, an annotation on "here", an insertion and a deletion.
        var dttm = RtfWriter.Dttm(Written);
        var rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Arial;}}{\\stylesheet{\\s0 Normal;}{\\*\\cs15\\additive\\b Emphasis Strong;}}" +
                  "{\\*\\revtbl {Unknown;}{Kim;}}" +
                  "\\pard\\plain Use {\\cs15\\b this} {\\*\\atrfstart 3}here{\\*\\atrfend 3}{\\*\\atnid K}{\\*\\atnauthor Kim}\\chatn" +
                  $"{{\\*\\annotation{{\\*\\atnref 3}}{{\\*\\atndate {dttm}}}\\pard\\plain {{\\chatn }}Look.}} " +
                  $"{{\\revised\\revauth1\\revdttm{dttm} new}}{{\\deleted\\revauthdel1\\revdttmdel{dttm} gone\\tab}}\\par}}";

        var document = RtfReader.Read(Encoding.ASCII.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default);

        var inlines = document.Sections[0].Blocks.OfType<HParagraph>().First().Inlines;
        Assert.Equal("Emphasis Strong", inlines.OfType<HText>().Single(t => t.Text == "this").Format.Layout?.Style);
        var comment = Assert.Single(inlines.OfType<HComment>());
        Assert.Same(comment, Assert.Single(inlines.OfType<HCommentStart>()).Comment);
        Assert.Equal(("Kim", "K", Written, "Look."), (comment.Author, comment.Initials, comment.Date, HDocumentWalker.Text(((HParagraph)comment.Blocks[0]).Inlines)));
        Assert.Equal(new HRevision("Kim", Written), inlines.OfType<HText>().Single(t => t.Text.Contains("new", StringComparison.Ordinal)).Format.Layout?.Inserted);
        var deletion = Assert.Single(inlines.OfType<HDeletion>());
        Assert.Equal(new HRevision("Kim", Written), deletion.Revision);
        Assert.Equal("gone", ((HText)deletion.Content[0]).Text);
        Assert.IsType<HTab>(deletion.Content[1]);
        Assert.Equal("Use this here new", HDocumentWalker.Text(inlines));
    }

    [Fact]
    public void Rtf_shapes_without_text_are_drawings_and_rows_and_list_levels_keep_their_words()
    {
        static string Shape(string properties, string result = "") =>
            "{\\shp{\\*\\shpinst\\shpleft0\\shptop0\\shpright1440\\shpbottom720\\shpbxcolumn\\shpbypara\\shpwr3" + properties + "}" + result + "}";
        static string Property(string name, string value) => $"{{\\sp{{\\sn {name}}}{{\\sv {value}}}}}";
        var rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}{\\f1\\fcharset2 Symbol;}}" +
                  "{\\*\\listtable{\\list\\listtemplateid1{\\listlevel\\levelnfc23\\leveljc0\\leveljcn2\\levelfollow0{\\leveltext\\'01\\'b7;}{\\levelnumbers;}\\f1}\\listid1}}" +
                  "{\\*\\listoverridetable{\\listoverride\\listid1\\ls1}}" +
                  "\\pard\\plain " +
                  Shape(Property("shapeType", "1") + Property("fillColor", "255"), "{\\shprslt{\\*\\do\\dobxcolumn}}") +
                  Shape(Property("shapeType", "3")) +
                  Shape(Property("shapeType", "20") + Property("fFlipV", "1")) +
                  // A line with an arrowhead keeps the copy for programs without shapes (here a text), which has the arrowhead.
                  Shape(Property("shapeType", "20") + Property("lineEndArrowhead", "1"), "{\\shprslt arrow}") +
                  "\\par\\pard\\plain\\ls1\\ilvl0 item\\par" +
                  "\\trowd\\trkeep\\cellx2000\\pard\\plain\\intbl cell\\cell\\row}";

        var document = RtfReader.Read(Encoding.ASCII.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default);

        var blocks = document.Sections[0].Blocks;
        var shapes = ((HParagraph)blocks[0]).Inlines.OfType<HShape>().ToList();
        Assert.Equal([HShapeKind.Rectangle, HShapeKind.Ellipse, HShapeKind.Line], shapes.Select(s => s.Kind));
        Assert.Equal(("#FF0000", 7200, 3600), (shapes[0].Fill, shapes[0].Width, shapes[0].Height));
        Assert.Equal("#FFFFFF", shapes[1].Fill);
        Assert.True(shapes[2].FlipVertical);
        Assert.Null(shapes[2].Fill);
        Assert.Equal("COLUMN", shapes[0].Anchor?.HorizontalRelativeTo);
        Assert.Contains("arrow", HDocumentWalker.Text(((HParagraph)blocks[0]).Inlines), StringComparison.Ordinal);

        // \leveljcn wins over \leveljc; the Symbol font's bullet is its character, its code kept for the layout.
        var item = (HParagraph)blocks[1];
        var level = item.List!.Numbering.Levels[0];
        Assert.Equal((HAlign?)HAlign.Right, level.Align);
        Assert.Equal("•", level.Text);
        Assert.Equal(new HSymbol("Symbol", ""), item.List.MarkerFormat?.Layout?.Symbol);

        Assert.True(Assert.IsType<HTable>(blocks[2]).Rows[0].CantSplit);
    }

    [Fact]
    public void Rtf_text_in_a_symbol_font_is_its_characters()
    {
        var rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}{\\f1\\fcharset2 Wingdings;}}\\pard\\plain A{\\f1 \\'fe}B\\par}";

        var inlines = RtfReader.Read(Encoding.ASCII.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default).Sections[0].Blocks.OfType<HParagraph>().First().Inlines;

        var box = Assert.Single(inlines.OfType<HText>(), t => t.Text == "☑");
        Assert.Equal(new HSymbol("Wingdings", ""), box.Format.Layout?.Symbol);
        Assert.Equal("A☑B", HDocumentWalker.Text(inlines));
    }

    // ───────────────────────── ODT ─────────────────────────

    [Fact]
    public void Odt_reader_reads_named_text_styles_annotations_and_tracked_changes()
    {
        var builder = new OdtBuilder
        {
            Styles = "<style:style style:name=\"Strong_20_Emphasis\" style:display-name=\"Strong Emphasis\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\"/></style:style>",
            ContentStyles = "<style:style style:name=\"T1\" style:family=\"text\" style:parent-style-name=\"Strong_20_Emphasis\"><style:text-properties fo:font-style=\"italic\"/></style:style>",
            Body = "<text:tracked-changes>" +
                   "<text:changed-region text:id=\"ct1\"><text:insertion><office:change-info><dc:creator>Park</dc:creator><dc:date>2026-03-14T09:26:00</dc:date></office:change-info></text:insertion></text:changed-region>" +
                   "<text:changed-region text:id=\"ct2\"><text:deletion><office:change-info><dc:creator>Lee</dc:creator><dc:date>2026-03-14T09:26:00</dc:date></office:change-info><text:p>old</text:p></text:deletion></text:changed-region>" +
                   "</text:tracked-changes>" +
                   "<text:p>Kept <text:span text:style-name=\"T1\">styled</text:span> " +
                   "<office:annotation office:name=\"c1\"><dc:creator>Kim Minsu</dc:creator><dc:date>2026-03-14T09:26:00</dc:date><text:p>Check this.</text:p></office:annotation>commented<office:annotation-end office:name=\"c1\"/> " +
                   "<text:change-start text:change-id=\"ct1\"/>inserted<text:change-end text:change-id=\"ct1\"/><text:change text:change-id=\"ct2\"/></text:p>",
        };
        var path = builder.Save(Path.Combine(fx.NewFolder(), "revisions.odt"));

        var document = OdtReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media"));

        var inlines = document.Sections[0].Blocks.OfType<HParagraph>().First().Inlines;
        var styled = inlines.OfType<HText>().Single(t => t.Text == "styled");
        Assert.Equal(("Strong Emphasis", true, true), (styled.Format.Layout?.Style, styled.Format.Bold, styled.Format.Italic));
        var comment = Assert.Single(inlines.OfType<HComment>());
        Assert.Same(comment, Assert.Single(inlines.OfType<HCommentStart>()).Comment);
        Assert.Equal(("Kim Minsu", Written), (comment.Author, comment.Date));
        Assert.Equal(new HRevision("Park", Written), inlines.OfType<HText>().Single(t => t.Text == "inserted").Format.Layout?.Inserted);
        var deletion = Assert.Single(inlines.OfType<HDeletion>());
        Assert.Equal(("Lee", "old"), (deletion.Revision.Author, HDocumentWalker.Text(deletion.Content)));
        Assert.Equal("Kept styled commented inserted", HDocumentWalker.Text(inlines));
    }

    // ───────────────────────── HWPX ─────────────────────────

    [Fact]
    public void Hwpx_reader_reads_tracked_changes_and_memos()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "revisions.hwpx");
        var source = new HDocument();
        var section = new HSection();
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText("MARK", default));
        section.Blocks.Add(paragraph);
        source.Sections.Add(section);
        HwpxWriter.Write(source, path, cancellationToken: TestContext.Current.CancellationToken);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            Replace(zip, "Contents/header.xml", xml => xml.Replace("</hh:refList>",
                "<hh:trackChanges itemCnt=\"2\"><hh:trackChange type=\"Insert\" date=\"2026-03-14T09:26:00Z\" authorID=\"1\" id=\"1\"/>" +
                "<hh:trackChange type=\"Delete\" date=\"2026-03-14T09:26:00Z\" authorID=\"1\" id=\"2\"/></hh:trackChanges>" +
                "<hh:trackChangeAuthors itemCnt=\"1\"><hh:trackChangeAuthor name=\"홍길동\" id=\"1\"/></hh:trackChangeAuthors></hh:refList>", StringComparison.Ordinal));
            Replace(zip, "Contents/section0.xml", xml => xml.Replace("<hp:t>MARK</hp:t>",
                "<hp:t>Kept <hp:insertBegin Id=\"1\" TcId=\"1\"/>new<hp:insertEnd Id=\"1\" TcId=\"1\"/> <hp:deleteBegin Id=\"2\" TcId=\"2\"/>gone<hp:deleteEnd Id=\"2\" TcId=\"2\"/></hp:t>" +
                "<hp:ctrl><hp:fieldBegin id=\"9\" type=\"MEMO\" name=\"\" editable=\"1\" dirty=\"0\"><hp:parameters cnt=\"2\" name=\"\">" +
                "<hp:stringParam name=\"Author\">김철수</hp:stringParam><hp:stringParam name=\"CreateDateTime\">2026-03-14T09:26:00Z</hp:stringParam></hp:parameters>" +
                "<hp:subList id=\"\" textDirection=\"HORIZONTAL\" lineWrap=\"BREAK\" vertAlign=\"TOP\"><hp:p id=\"0\" paraPrIDRef=\"0\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\">" +
                "<hp:run charPrIDRef=\"0\"><hp:t>메모 내용</hp:t></hp:run></hp:p></hp:subList></hp:fieldBegin></hp:ctrl><hp:t>noted</hp:t>" +
                "<hp:ctrl><hp:fieldEnd beginIDRef=\"9\" fieldid=\"9\"/></hp:ctrl>", StringComparison.Ordinal));
        }

        var document = HwpxReader.Read(path, Path.Combine(folder, "media"));

        var inlines = document.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).ToList();
        Assert.Equal(new HRevision("홍길동", Written), inlines.OfType<HText>().Single(t => t.Text == "new").Format.Layout?.Inserted);
        Assert.Null(inlines.OfType<HText>().First().Format.Layout?.Inserted);
        var deletion = Assert.Single(inlines.OfType<HDeletion>());
        Assert.Equal(("홍길동", "gone"), (deletion.Revision.Author, HDocumentWalker.Text(deletion.Content)));
        var memo = Assert.Single(inlines.OfType<HComment>());
        Assert.Same(memo, Assert.Single(inlines.OfType<HCommentStart>()).Comment);
        Assert.Equal(("김철수", Written, "메모 내용"), (memo.Author, memo.Date, HDocumentWalker.Text(((HParagraph)memo.Blocks[0]).Inlines)));
        Assert.True(inlines.IndexOf(memo) > inlines.FindIndex(i => i is HText { Text: "noted" }));
        Assert.Equal("Kept new noted", HDocumentWalker.Text(inlines));
    }

    private static void Replace(ZipArchive zip, string name, Func<string, string> change)
    {
        var entry = zip.GetEntry(name)!;
        string xml;
        using (var reader = new StreamReader(entry.Open()))
            xml = reader.ReadToEnd();
        var changed = change(xml);
        Assert.NotEqual(xml, changed);
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(changed);
    }

    // ───────────────────────── Other writers, layout ─────────────────────────

    [Fact]
    public void Writers_without_comments_and_revisions_show_the_changes_made()
    {
        Assert.Equal("Kept styled commented inserted", PlainTextWriter.Write(Model()).Trim());

        var markdown = MarkdownWriter.Write(Model(), _ => null);
        Assert.Contains("inserted", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("old", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Check this", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wingdings_list_bullet_keeps_its_code_for_the_layout()
    {
        var builder = new DocxBuilder
        {
            Numbering = "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"\"/>" +
                        "<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Wingdings\" w:hAnsi=\"Wingdings\" w:hint=\"default\"/></w:rPr></w:lvl></w:abstractNum>" +
                        "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>",
        };
        builder.Paragraph(P(R("item"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
        var path = builder.Save(Path.Combine(fx.NewFolder(), "bullet.docx"));

        var paragraph = DocxReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media")).Sections[0].Blocks.OfType<HParagraph>().Single();

        var level = paragraph.List!.Numbering.Levels[0];
        Assert.Equal("▪", level.Text);
        var marker = paragraph.List.MarkerFormat!.Value;
        Assert.Equal(new HSymbol("Wingdings", ""), marker.Layout?.Symbol);
        Assert.NotEqual("Wingdings", marker.Font);
        // The layout's text draws the code where the bullet is, and the tab after it as it is.
        var text = ParagraphText.From(paragraph, level.Text);
        Assert.StartsWith("▪\t", text.Text, StringComparison.Ordinal);
        Assert.StartsWith("\t", text.Drawn, StringComparison.Ordinal);
    }
}
