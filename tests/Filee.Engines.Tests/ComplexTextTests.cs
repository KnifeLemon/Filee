// The complex-script word builder (PdfDocumentReader.ComplexText): the Tesseract OCR layer regression. Tesseract
// places right-to-left words correctly but draws them with a mirrored matrix, so every glyph advances right to left
// (its end stands before its start) and PdfPig's TextOrientation is not Horizontal for any of them — the page then
// came out with no words at all (a scanned page with a picture in the DOCX) or, word by word, garbled.

using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Filee.Engines.Tests;

public class ComplexTextTests
{
    /// <summary>A letter standing at <paramref name="start"/>, advancing by <paramref name="advance"/> (negative for right-to-left drawn text), on a level baseline.</summary>
    private static Letter At(string value, double start, double advance, double y = 100) =>
        At(value, start, advance, y, TextRenderingMode.Neither);

    /// <summary>A letter with a chosen rendering mode: <see cref="TextRenderingMode.Neither"/> is an OCR layer, <see cref="TextRenderingMode.Fill"/> visible text.</summary>
    private static Letter At(string value, double start, double advance, double y, TextRenderingMode mode) =>
        new(value, Box(start, advance, y), Box(start, advance, y),
            new PdfPoint(start, y), new PdfPoint(start + advance, y), Math.Abs(advance),
            fontSize: 29, fontDetails: null, renderingMode: mode,
            strokeColor: null, fillColor: null, pointSize: 29, textSequence: 0);

    private static PdfRectangle Box(double start, double advance, double y) =>
        advance >= 0 ? new PdfRectangle(start, y - 9, start + advance, y + 9) : new PdfRectangle(start + advance, y - 9, start, y + 9);

    private static List<PdfDocumentReader.PageWord> Words(params Letter[] letters) => PdfDocumentReader.ComplexWords([.. letters]);

    [Fact]
    public void Right_to_left_text_drawn_with_a_mirrored_matrix_reads_in_order()
    {
        // Tesseract's OCR layer of an Arabic page (the probed stream: every word advanced leftwards, glyphs in
        // logical order in the stream, a space at the end of every word's run):
        // "السوق المغربي"
        List<Letter> letters =
        [
            At("ا", 894.0, -9.9), At("ل", 884.1, -9.9), At("س", 874.2, -9.9), At("و", 864.3, -9.9), At("ق", 854.4, -9.9),
            At(" ", 844.5, -9.9),
            At("ا", 831.0, -9.1), At("ل", 821.9, -9.1), At("م", 812.8, -9.1), At("غ", 803.7, -9.1),
            At("ر", 794.6, -9.1), At("ب", 785.5, -9.1), At("ي", 776.4, -9.1),
        ];

        var words = Words([.. letters]);
        // The words of one row come out in visual order (left to right), like they do for every page: the line
        // layout (PdfDocumentReader.Layout) reads a right-to-left line from its TextLine.RightToLeft flag.
        Assert.Equal(["المغربي", "السوق"], words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void Left_to_right_text_still_reads_in_order()
    {
        List<Letter> letters =
        [
            At("h", 100, 9.9), At("e", 109.9, 9.9), At("l", 119.8, 9.9), At("l", 129.7, 9.9), At("o", 139.6, 9.9),
            At(" ", 149.5, 9.9),
            At("w", 160, 9.9), At("o", 169.9, 9.9), At("r", 179.8, 9.9), At("l", 189.7, 9.9), At("d", 199.6, 9.9),
        ];

        var words = Words([.. letters]);
        Assert.Equal(["hello", "world"], words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void A_word_drawn_in_pieces_that_touch_joins_again()
    {
        // "test" drawn as "te" + "st" (a run per style): the pieces touch, so one word.
        List<Letter> letters =
        [
            At("t", 100, 9.9), At("e", 109.9, 9.9), At("s", 119.8, 9.9), At("t", 129.7, 9.9),
            At(" ", 139.6, 9.9),
            At("t", 150, 9.9), At("e", 159.9, 9.9),
            At("s", 169.8, 9.9), At("t", 179.7, 9.9),
        ];

        var words = Words([.. letters]);
        Assert.Equal(["test", "test"], words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void An_ocr_layer_reads_a_date_inside_arabic_in_stream_order()
    {
        // Tesseract writes the line logically but places every glyph monotonically right to left, bidi runs included:
        // the digits of "التاريخ:2026/8/4م" stand in stream order while their positions go leftwards, so deriving the
        // text from the positions reversed the already-logical date ("4/8/6202"). The stream order is the text.
        List<Letter> letters =
        [
            At("ا", 500, -9.9), At("ل", 490.1, -9.9), At("ت", 480.2, -9.9), At("ا", 470.3, -9.9),
            At("ر", 460.4, -9.9), At("ي", 450.5, -9.9), At("خ", 440.6, -9.9), At(":", 430.7, -9.9),
            At("2", 420.8, -9.9), At("0", 410.9, -9.9), At("2", 401.0, -9.9), At("6", 391.1, -9.9),
            At("/", 381.2, -9.9), At("8", 371.3, -9.9), At("/", 361.4, -9.9), At("4", 351.5, -9.9),
            At("م", 341.6, -9.9),
        ];

        var words = Words([.. letters]);
        var word = Assert.Single(words);
        Assert.Equal("التاريخ:2026/8/4م", word.Text);
    }

    [Fact]
    public void An_ocr_layer_keeps_a_widely_spaced_word_together()
    {
        // Tesseract's glyphless font spaces a word's letters over the word's width: ligature-heavy Arabic (few glyphs,
        // wide word) stands wider apart than a quarter of the size, which used to split the word — the pieces then
        // came out in visual order, garbled. The layer's stream order and its spaces carry the words instead.
        // "السوق" (stream: right to left, one wide word) and "المغربي" after a space letter.
        List<Letter> letters =
        [
            At("ا", 894.0, -13.2), At("ل", 880.8, -13.2), At("س", 867.6, -13.2), At("و", 854.4, -13.2), At("ق", 841.2, -13.2),
            At(" ", 828.0, -13.2),
            At("ا", 815.0, -10.5), At("ل", 804.5, -10.5), At("م", 794.0, -10.5), At("غ", 783.5, -10.5),
            At("ر", 773.0, -10.5), At("ب", 762.5, -10.5), At("ي", 752.0, -10.5),
        ];
        foreach (var letter in letters)
            Assert.Equal(TextRenderingMode.Neither, letter.RenderingMode); // the layer itself, not a styled page

        var words = Words([.. letters]);
        Assert.Equal(["المغربي", "السوق"], words.Select(w => w.Text).ToArray()); // one row, in visual order
    }

    [Fact]
    public void Visible_text_still_splits_at_a_wide_gap()
    {
        // A visible line with a real gap (a column seam): two words, not one. Visible letters keep the quarter-size
        // limit that the OCR layer relaxes.
        List<Letter> letters =
        [
            At("h", 100, 9.9, 100, TextRenderingMode.Fill), At("i", 109.9, 9.9, 100, TextRenderingMode.Fill),
            At("o", 160, 9.9, 100, TextRenderingMode.Fill), At("k", 169.9, 9.9, 100, TextRenderingMode.Fill),
        ];

        var words = Words([.. letters]);
        Assert.Equal(["hi", "ok"], words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void Really_turned_text_is_not_read_as_horizontal()
    {
        // A sideways letter (advancing along the height): the OCR-layer reader must not see it as a line of text.
        List<Letter> letters =
        [
            At("ا", 100, 0, y: 100), At("ل", 100, 0, y: 110), At("س", 100, 0, y: 120),
        ];
        // No letter advances horizontally: each pair breaks apart by baseline, and nothing reads as a line.
        Assert.All(Words([.. letters]), w => Assert.True(w.Text.Length <= 1));
    }
}
