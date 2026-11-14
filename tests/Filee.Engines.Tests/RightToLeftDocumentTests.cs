// Right-to-left Word and RTF documents in Filee's own renderer (Arabic, Persian and Hebrew: issues #202, #216, #620).
// The readers keep the paragraph's direction (w:bidi, \rtlpar), right-to-left runs (w:rtl, \rtlch) with Word's
// complex-script size and weight (w:szCs, w:bCs), right-to-left tables (w:bidiVisual, \rtlrow), sections whose columns
// run from the right (sectPr w:bidi, \rtlsect), explicit directions (w:dir, w:bdo) and a note's own number where its
// text has it; the layout starts such paragraphs at the right and orders their text by the Unicode Bidirectional
// Algorithm. Documents are built here; the layout tests draw with the Noto fonts of build/fetch-fonts.ps1 when they have
// the script, else with the installed fonts, and skip when neither has it.

using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

public sealed class RightToLeftDocumentTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Bidi = "<w:bidi/>";
    private const string Rtl = "<w:rtl/>";

    /// <summary>"The program keeps the paragraphs' formatting."</summary>
    private const string Arabic = "يحافظ البرنامج على تنسيق الفقرات";

    /// <summary>"Hello world" in Hebrew.</summary>
    private const string Hebrew = "שלום עולם";

    /// <summary>The body of DocxBuilder's A4 page: 1 inch margins.</summary>
    private const float BodyLeft = 72, BodyRight = 595.3f - 72;

    static RightToLeftDocumentTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // ───────────────────────── Word: reading ─────────────────────────

    [Fact]
    public void Bidi_makes_a_paragraph_right_to_left_and_its_alignment_and_indents_name_start_and_end()
    {
        var builder = new DocxBuilder();
        builder.Styles += """<w:style w:type="paragraph" w:styleId="Arabic"><w:name w:val="Arabic"/><w:basedOn w:val="Normal"/><w:pPr><w:bidi/></w:pPr></w:style>""";
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic, Rtl), Bidi + "<w:jc w:val=\"left\"/><w:ind w:left=\"720\" w:right=\"360\"/>"))
            .Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic, Rtl), Bidi + "<w:jc w:val=\"end\"/>"))
            .Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic, Rtl), "<w:pStyle w:val=\"Arabic\"/>"))
            .Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic, Rtl), "<w:pStyle w:val=\"Arabic\"/><w:bidi w:val=\"0\"/>"))
            .Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic)));

        var paragraphs = Paragraphs(DocxReader.Read(builder.Save(Path.Combine(fx.NewFolder(), "bidi.docx")), fx.NewFolder()));

        // In a w:bidi paragraph Word's "left" is the start side, the right one, as the model's Left is.
        Assert.Equal((true, HAlign.Left, HwpxUnits.FromTwips(720), HwpxUnits.FromTwips(360)),
            (paragraphs[0].Format.RightToLeft, paragraphs[0].Format.Align, paragraphs[0].Format.Left, paragraphs[0].Format.Right));
        Assert.Equal((true, HAlign.Right), (paragraphs[1].Format.RightToLeft, paragraphs[1].Format.Align));
        Assert.True(paragraphs[2].Format.RightToLeft); // from the style
        Assert.False(paragraphs[3].Format.RightToLeft); // turned off over the style
        // Without w:bidi Word lays Arabic out in a left-to-right paragraph; the model leaves it to the (left-to-right) style.
        Assert.Null(paragraphs[4].Format.RightToLeft);
    }

    [Fact]
    public void Right_to_left_runs_keep_their_direction_and_complex_script_size_and_weight()
    {
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(
            DocxBuilder.R(Arabic, Rtl + "<w:b/><w:bCs/><w:sz w:val=\"20\"/><w:szCs w:val=\"28\"/>") +
            DocxBuilder.R(" Filee", "<w:b/>") +
            DocxBuilder.R("نص", "<w:cs/>"), Bidi));

        var runs = Paragraphs(DocxReader.Read(builder.Save(Path.Combine(fx.NewFolder(), "runs.docx")), fx.NewFolder()))[0].Inlines.OfType<HText>().ToList();

        var arabic = runs[0].Format.Layout!;
        Assert.Equal((true, 1400, null), (arabic.RightToLeft, arabic.ComplexSize, arabic.ComplexBold)); // bold as the run
        // w:b alone doesn't make Word's complex-script characters bold.
        var latin = runs[1].Format.Layout!;
        Assert.Equal((null, null, false), (latin.RightToLeft, latin.ComplexSize, latin.ComplexBold));
        Assert.True(runs[2].Format.Layout!.ComplexScript);
    }

    [Fact]
    public void Tables_sections_explicit_directions_and_note_numbers_are_read()
    {
        var builder = new DocxBuilder { SectionProperties = DocxBuilder.DefaultPage + "<w:cols w:num=\"2\" w:space=\"720\"/><w:bidi/>" };
        builder.Paragraph(Table(bidiVisual: true, "واحد", "اثنان"))
            .Paragraph(DocxBuilder.P(DocxBuilder.R("a ") + "<w:dir w:val=\"rtl\">" + DocxBuilder.R("b") + "</w:dir>" +
                                     "<w:bdo w:val=\"ltr\">" + DocxBuilder.R("c") + "</w:bdo>" + DocxBuilder.R(Arabic, Rtl) +
                                     "<w:r><w:footnoteReference w:id=\"1\"/></w:r>", Bidi));
        builder.Footnotes(Bracketed(1));

        var document = DocxReader.Read(builder.Save(Path.Combine(fx.NewFolder(), "parts.docx")), fx.NewFolder());

        var section = Assert.Single(document.Sections);
        Assert.Equal(new HColumns(2, HwpxUnits.FromTwips(720), false, RightToLeft: true), section.Columns);
        Assert.True(section.Blocks.OfType<HTable>().Single().RightToLeft);
        var inlines = section.Blocks.OfType<HParagraph>().Single(p => p.Inlines.OfType<HNote>().Any()).Inlines;
        Assert.Equal(["a ", "+RightToLeft", "b", "+End", "+OverrideLeftToRight", "c", "+End", Arabic, "note"], inlines.Select(i => i switch
        {
            HText text => text.Text,
            HDirectionMark mark => "+" + mark.Kind,
            HNote => "note",
            _ => "?",
        }));
        // The note's own number stays between its brackets.
        var note = inlines.OfType<HNote>().Single().Blocks.OfType<HParagraph>().Single();
        Assert.Equal(["(", "#", ") " + Arabic], note.Inlines.Select(i => i switch { HText text => text.Text, HNoteNumber => "#", _ => "?" }));
    }

    // ───────────────────────── RTF: reading ─────────────────────────

    [Fact]
    public void Rtf_right_to_left_paragraphs_turn_their_left_and_right_into_start_and_end()
    {
        var document = Rtf("""
            {\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}
            \pard\rtlpar\qr\li360\ri720 a\par
            \pard\rtlpar\ql b\par
            \pard\rtlpar\lin1080\ri720 c\par
            \pard\ltrpar\ql\li360 d\par
            \pard{\rtlch\ltrch e}{\ltrch\rtlch f}\par
            }
            """);

        var paragraphs = Paragraphs(document);
        // \qr is the right side, a right-to-left paragraph's start; \ri its start indent, \li its end one.
        Assert.Equal((true, HAlign.Left, HwpxUnits.FromTwips(720), HwpxUnits.FromTwips(360)),
            (paragraphs[0].Format.RightToLeft, paragraphs[0].Format.Align, paragraphs[0].Format.Left, paragraphs[0].Format.Right));
        Assert.Equal(HAlign.Right, paragraphs[1].Format.Align);
        // \lin is the start indent whatever the direction, and wins over \ri.
        Assert.Equal(HwpxUnits.FromTwips(1080), paragraphs[2].Format.Left);
        Assert.Equal((null, HAlign.Left, HwpxUnits.FromTwips(360)), (paragraphs[3].Format.RightToLeft, paragraphs[3].Format.Align, paragraphs[3].Format.Left));
        // The last of \rtlch and \ltrch says the run's direction.
        var runs = paragraphs[4].Inlines.OfType<HText>().ToList();
        Assert.Equal([null, true], runs.Select(r => r.Format.Layout?.RightToLeft));
    }

    [Fact]
    public void Rtf_right_to_left_rows_and_sections_are_read()
    {
        var document = Rtf("""
            {\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\rtlsect\cols2
            \trowd\rtlrow\cellx2000\cellx4000\pard\intbl a\cell b\cell\row
            \pard\par
            \trowd\rtlrow\trqr\cellx2000\pard\intbl c\cell\row
            \pard\par
            \trowd\ltrrow\trqr\cellx2000\pard\intbl d\cell\row
            }
            """);

        Assert.True(document.Sections[0].Columns.RightToLeft);
        var tables = document.Sections.SelectMany(s => s.Blocks).OfType<HTable>().ToList();
        Assert.Equal([(true, HAlign.Left), (true, HAlign.Left), (false, HAlign.Right)], tables.Select(t => (t.RightToLeft, t.Align)));
    }

    [Fact]
    public void Rtf_note_numbers_stay_where_the_note_has_them()
    {
        var document = Rtf("""
            {\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}
            \pard\rtlpar text{\super\chftn}{\footnote\pard\rtlpar ({\super\chftn}) note\par}\par
            }
            """);

        var note = Paragraphs(document)[0].Inlines.OfType<HNote>().Single().Blocks.OfType<HParagraph>().Single();
        Assert.Equal(["(", "#", ") note"], note.Inlines.Select(i => i switch { HText text => text.Text, HNoteNumber => "#", _ => "?" }));
        // Word's notes are numbered without 한글's ")".
        Assert.Equal("", document.Sections[0].Footnotes.Format.Suffix);
    }

    // ───────────────────────── The bidirectional algorithm ─────────────────────────

    [Fact]
    public void Neutrals_of_a_right_to_left_run_read_right_to_left_and_numbers_stay_whole()
    {
        // Latin words typed into a right-to-left run of a right-to-left paragraph follow each other from the right, as
        // Word shows them; in a run of their own they read left to right.
        Assert.Equal("22222122222", string.Concat(BidiAlgorithm.Levels("Hello World", true, Enumerable.Repeat(true, 11).ToArray())));
        Assert.Equal("22222222222", string.Concat(BidiAlgorithm.Levels("Hello World", true)));
        // In a left-to-right paragraph the space between them is right to left, which leaves their order.
        Assert.Equal("00000100000", string.Concat(BidiAlgorithm.Levels("Hello World", false, Enumerable.Repeat(true, 11).ToArray())));
        // A number keeps its separators, and the run's trailing space still falls back to the paragraph's level (L1).
        Assert.Equal("22222221", string.Concat(BidiAlgorithm.Levels("1,234.5 ", true, Enumerable.Repeat(true, 8).ToArray())));
        Assert.Equal("00000000", string.Concat(BidiAlgorithm.Levels("1,234.5 ", false, Enumerable.Repeat(true, 8).ToArray())));
    }

    // ───────────────────────── Layout ─────────────────────────

    [Fact]
    public void An_arabic_paragraph_that_starts_with_a_latin_word_and_a_number_reads_from_the_right()
    {
        var fonts = FontsFor('ي');
        const string text = "Filee 2024 " + Arabic;
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R("Filee 2024 ") + DocxBuilder.R(Arabic, Rtl), Bidi))
            .Paragraph(DocxBuilder.P(DocxBuilder.R("Filee 2024 ") + DocxBuilder.R(Arabic, Rtl)));

        var page = Compose(builder, fonts).Pages[0];
        var lines = Lines(page);
        Assert.Equal(2, lines.Count);

        // Right to left: "Filee 2024" left to right at the right edge, then the Arabic words leftwards.
        var rtl = lines[0];
        var (filee, year, first, last) = (Extent(rtl, "Filee"), Extent(rtl, "2024"), Extent(rtl, "يحافظ"), Extent(rtl, "الفقرات"));
        Assert.True(filee.End < year.Start, $"{filee} {year}");
        Assert.True(year.End < BodyRight + 0.5f && year.End > BodyRight - 15, $"the line ends at {year.End}, not at the right margin");
        Assert.True(first.End < filee.Start && last.End < first.Start, $"{first} {last} {filee}");
        // The runs keep the order the text was written in, so the PDF's text reads as the document does.
        Assert.Equal(text.Replace(" ", ""), RunText(page, rtl[0].Baseline).Replace(" ", ""));

        // Without w:bidi Word starts the same text at the left: Latin first, the Arabic words after it from their right.
        var ltr = lines[1];
        Assert.Equal(BodyLeft, Extent(ltr, "Filee").Start, 1f);
        Assert.True(Extent(ltr, "يحافظ").Start > Extent(ltr, "الفقرات").End);
        Assert.True(Extent(ltr, "الفقرات").Start > Extent(ltr, "2024").End);
    }

    [Fact]
    public void Alignment_and_indents_of_a_right_to_left_paragraph_are_mirrored()
    {
        var fonts = FontsFor('ي');
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic, Rtl), Bidi + "<w:ind w:left=\"720\"/>"))
            .Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic, Rtl), Bidi + "<w:jc w:val=\"right\"/><w:ind w:right=\"1440\"/>"))
            .Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic, Rtl), Bidi + "<w:jc w:val=\"center\"/>"));

        var lines = Lines(Compose(builder, fonts).Pages[0]);

        // The start indent (w:left) is on the right; Word's "right" is the end, the left side, with its indent there.
        Assert.Equal(BodyRight - 36, Right(lines[0]), 1f);
        Assert.Equal(BodyLeft + 72, Left(lines[1]), 1f);
        Assert.Equal((BodyLeft + BodyRight) / 2, (Left(lines[2]) + Right(lines[2])) / 2, 1f);
    }

    [Fact]
    public void A_hebrew_paragraph_starts_at_the_right()
    {
        var fonts = FontsFor('ש');
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R(Hebrew + " 123", Rtl), Bidi));

        var line = Lines(Compose(builder, fonts).Pages[0]).Single();

        var (hello, world, number) = (Extent(line, "שלום"), Extent(line, "עולם"), Extent(line, "123"));
        Assert.True(world.End < hello.Start && number.End < world.Start, $"{hello} {world} {number}");
        Assert.True(hello.End > BodyRight - 15, $"the line ends at {hello.End}, not at the right margin");
        // The digits read left to right.
        Assert.True(Extent(line, "1").Start < Extent(line, "3").Start);
    }

    [Fact]
    public void Complex_script_characters_take_their_own_size()
    {
        var fonts = FontsFor('ي');
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R("Filee", "<w:sz w:val=\"20\"/><w:szCs w:val=\"32\"/>") +
                                        DocxBuilder.R(" " + Arabic, Rtl + "<w:sz w:val=\"20\"/><w:szCs w:val=\"32\"/>"), Bidi));

        var line = Lines(Compose(builder, fonts).Pages[0]).Single();

        Assert.All(line.Where(g => g.Run.Text.Contains("Filee", StringComparison.Ordinal)), g => Assert.Equal(10, g.Run.Size));
        // The right-to-left run is complex-script text throughout, its spaces included.
        Assert.All(line.Where(g => g.Run.Text.Contains("يحافظ", StringComparison.Ordinal)), g => Assert.Equal(16, g.Run.Size));
    }

    [Fact]
    public void A_right_to_left_table_has_its_first_column_on_the_right()
    {
        var fonts = FontsFor('ي');
        var builder = new DocxBuilder();
        builder.Paragraph(Table(bidiVisual: true, "واحد", "اثنان")).Paragraph(DocxBuilder.P(""))
            .Paragraph(Table(bidiVisual: false, "واحد", "اثنان"));

        var rows = Lines(Compose(builder, fonts).Pages[0]);
        Assert.Equal(2, rows.Count);

        // bidiVisual: the first cell is the rightmost, the table starts at the right margin.
        var (one, two) = (Extent(rows[0], "واحد"), Extent(rows[0], "اثنان"));
        Assert.True(one.Start > two.End, $"{one} {two}");
        Assert.True(one.Start > BodyRight - 108, $"the first cell starts at {one.Start}, left of the right margin's 1.5 inch cell");
        // Left to right the first cell is on the left (its right-to-left paragraph at the cell's right edge).
        Assert.True(Extent(rows[1], "واحد").End < Extent(rows[1], "اثنان").Start);
        Assert.True(Extent(rows[1], "واحد").End < BodyLeft + 108, $"the first cell ends at {Extent(rows[1], "واحد").End}");
    }

    [Fact]
    public void A_right_to_left_list_has_its_number_on_the_right()
    {
        var fonts = FontsFor('ي');
        var builder = new DocxBuilder
        {
            Numbering = """
                <w:abstractNum w:abstractNumId="1"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/>
                <w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl></w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
                """,
        };
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic, Rtl), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>" + Bidi));

        var line = Lines(Compose(builder, fonts).Pages[0]).Single();

        var number = line.Single(g => g.Char == '1');
        Assert.All(line.Where(g => ComplexScriptsLetter(g.Char)), g => Assert.True(g.X < number.X, $"{g.Char} at {g.X}, the number at {number.X}"));
        // The number hangs at the start indent less the hanging one (18 pt in from the right), the text starts at 36 pt.
        Assert.True(number.X > BodyRight - 18 - 12 && number.X < BodyRight - 18 + 0.5f, $"the number is at {number.X}");
        Assert.True(Extent(line, "يحافظ").End < BodyRight - 36 + 0.5f);
    }

    [Fact]
    public void A_bracketed_note_number_stays_between_its_brackets()
    {
        // Issue #620: "(1)" in an Arabic footnote came out as "()1".
        var fonts = FontsFor('ي');
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R(Arabic, Rtl) + "<w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:footnoteReference w:id=\"1\"/></w:r>", Bidi));
        builder.Footnotes(Bracketed(1));

        var page = Compose(builder, fonts).Pages[0];

        var note = Glyphs(page).Where(g => g.Y > page.Height / 2).ToList();
        var (open, digit, close) = (note.Single(g => g.Char == '('), note.Single(g => g.Char == '1'), note.Single(g => g.Char == ')'));
        // Right to left: "(" at the right of the number, ")" at its left; the note starts at the right margin.
        Assert.True(open.X > digit.X && digit.X > close.X, $"( at {open.X}, 1 at {digit.X}, ) at {close.X}");
        Assert.True(open.X > BodyRight - 15, $"the note starts at {open.X}");
    }

    [Fact]
    public void An_override_draws_its_letters_in_its_direction()
    {
        var fonts = FontsFor('a');
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R("ab ") + "<w:bdo w:val=\"rtl\">" + DocxBuilder.R("xyz") + "</w:bdo>"));

        var line = Lines(Compose(builder, fonts).Pages[0]).Single();

        Assert.True(Extent(line, "a").Start < Extent(line, "z").Start);
        Assert.True(Extent(line, "z").Start < Extent(line, "y").Start && Extent(line, "y").Start < Extent(line, "x").Start);
    }

    [Fact]
    public void A_right_to_left_section_fills_its_right_column_first()
    {
        var fonts = FontsFor('a');
        var builder = new DocxBuilder { SectionProperties = DocxBuilder.DefaultPage + "<w:cols w:num=\"2\" w:space=\"720\"/><w:bidi/>" };
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R("first")))
            .Paragraph(DocxBuilder.P("<w:r><w:br w:type=\"column\"/></w:r>" + DocxBuilder.R("second")));

        var glyphs = Glyphs(Compose(builder, fonts).Pages[0]);

        var (first, second) = (Extent(glyphs, "first"), Extent(glyphs, "second"));
        Assert.True(first.Start > second.End, $"{first} {second}");
        Assert.True(first.Start > (BodyLeft + BodyRight) / 2);
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>A note in Word's usual layout for Arabic papers: its number in brackets.</summary>
    private static string Bracketed(int id) =>
        $"<w:footnote w:id=\"{id}\">{DocxBuilder.P(DocxBuilder.R("(", Rtl) + $"<w:r><w:rPr>{Rtl}</w:rPr><w:footnoteRef/></w:r>" + DocxBuilder.R(") " + Arabic, Rtl), Bidi)}</w:footnote>";

    /// <summary>A one-row table of two 1.5 inch columns, with Table Grid borders.</summary>
    private static string Table(bool bidiVisual, string first, string second) =>
        $"<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/>{(bidiVisual ? "<w:bidiVisual/>" : "")}<w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr>" +
        "<w:tblGrid><w:gridCol w:w=\"2160\"/><w:gridCol w:w=\"2160\"/></w:tblGrid><w:tr>" +
        $"<w:tc><w:tcPr><w:tcW w:w=\"2160\" w:type=\"dxa\"/></w:tcPr>{DocxBuilder.P(DocxBuilder.R(first, Rtl), Bidi)}</w:tc>" +
        $"<w:tc><w:tcPr><w:tcW w:w=\"2160\" w:type=\"dxa\"/></w:tcPr>{DocxBuilder.P(DocxBuilder.R(second, Rtl), Bidi)}</w:tc></w:tr></w:tbl>";

    private HDocument Rtf(string rtf) => RtfReader.Read(Encoding.Latin1.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default);

    private static List<HParagraph> Paragraphs(HDocument document) => [.. document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>()];

    /// <summary>
    /// The fonts the layout tests draw with: the Noto fonts of build/fetch-fonts.ps1 when they have the character, else
    /// the installed fonts; the test skips when neither has it.
    /// </summary>
    private static IFontCollection FontsFor(char character)
    {
        var repository = RepositoryFonts.Load();
        if (repository.ForCharacter(character, FontStyle.Regular, null) is not null)
            return repository;
        Assert.SkipWhen(SystemFonts.Instance.ForCharacter(character, FontStyle.Regular, null) is null,
            $"No font for U+{(int)character:X4}: neither src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1) nor the installed fonts have one");
        return SystemFonts.Instance;
    }

    /// <summary>Lays a Word document out as the renderer does: its formats resolved against the template's styles.</summary>
    private ComposedDocument Compose(DocxBuilder builder, IFontCollection fonts)
    {
        var document = DocxReader.Read(builder.Save(Path.Combine(fx.NewFolder(), "document.docx")), fx.NewFolder());
        using var shaper = new Shaper();
        return new PageComposer(new FontResolver(fonts), shaper, document.Kinsoku, StyleResolver.FromTemplate())
            .Compose(document, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A drawn glyph: the character its cluster starts with, its origin and advance, its run and the run's
    /// <see cref="Baseline"/>.
    /// </summary>
    private readonly record struct Glyph(char Char, float X, float Y, float Advance, GlyphRun Run, float Baseline);

    private static List<Glyph> Glyphs(PageContent page)
    {
        var glyphs = new List<Glyph>();
        foreach (var run in page.Items.OfType<GlyphItem>().Select(i => i.Run))
        {
            using var font = new SkiaSharp.SKFont(run.Font.Typeface, run.Size) { ScaleX = run.ScaleX, LinearMetrics = true, Subpixel = true };
            var widths = font.GetGlyphWidths(run.Glyphs);
            var baseline = Baseline(run);
            for (var g = 0; g < run.Glyphs.Length; g++)
            {
                var c = run.Text[run.Clusters[g]];
                // The explicit direction controls draw nothing.
                if (c is >= '‪' and <= '‮')
                    continue;
                glyphs.Add(new Glyph(c, run.Positions[g].X, run.Positions[g].Y, widths[g], run, baseline));
            }
        }
        return glyphs;
    }

    /// <summary>
    /// The baseline a run sits on: the y most of its glyphs share. A glyph's own y is not its line's: fonts such as Noto
    /// Sans Arabic draw the dots of ب ت ن ي as mark glyphs of their own, which the font's mark positioning moves above
    /// or below the letter, so their y is a point or two off the baseline.
    /// </summary>
    private static float Baseline(GlyphRun run) =>
        run.Positions.GroupBy(p => p.Y).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;

    /// <summary>The glyphs of each line of a page (by their runs' baselines), top to bottom.</summary>
    private static List<List<Glyph>> Lines(PageContent page) =>
        [.. Glyphs(page).GroupBy(g => MathF.Round(g.Baseline)).OrderBy(g => g.Key).Select(g => g.ToList())];

    /// <summary>The text of the runs drawn on the baseline <paramref name="y"/>, in their (logical) order.</summary>
    private static string RunText(PageContent page, float y) =>
        string.Concat(page.Items.OfType<GlyphItem>().Select(i => i.Run).Where(r => r.Positions.Length > 0 && MathF.Abs(Baseline(r) - y) < 0.5f).Select(r => r.Text))
            .Replace("‪", "").Replace("‫", "").Replace("‬", "").Replace("‭", "").Replace("‮", "");

    /// <summary>Where the glyphs of <paramref name="text"/> lie (the first run among <paramref name="glyphs"/> that holds it).</summary>
    private static (float Start, float End) Extent(List<Glyph> glyphs, string text)
    {
        var run = glyphs.Select(g => g.Run).First(r => r.Text.Contains(text, StringComparison.Ordinal));
        var from = run.Text.IndexOf(text, StringComparison.Ordinal);
        var mine = glyphs.Where(g => g.Run == run).ToList();
        float start = float.MaxValue, end = float.MinValue;
        for (var g = 0; g < run.Glyphs.Length; g++)
        {
            var cluster = run.Clusters[g];
            if (cluster < from || cluster >= from + text.Length)
                continue;
            var glyph = mine.FirstOrDefault(m => m.X == run.Positions[g].X && m.Char == run.Text[cluster]);
            start = Math.Min(start, run.Positions[g].X);
            end = Math.Max(end, run.Positions[g].X + glyph.Advance);
        }
        return (start, end);
    }

    private static float Left(List<Glyph> line) => line.Min(g => g.X);

    private static float Right(List<Glyph> line) => line.Max(g => g.X + g.Advance);

    private static bool ComplexScriptsLetter(char c) => c is >= '؀' and <= 'ۿ' && char.IsLetter(c);
}
