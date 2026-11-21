// The notes of a sheet printed after its pages (page setup "at end"): for each note of the printed cells, by row and
// then column, "Cell: B2", then "Comment: " and the note's text, then "Reply: " and each reply of a threaded comment
// (the words in the app's language: SheetWords), a blank line between notes, in the workbook's Normal font inside the
// sheet's margins, broken into lines at the page's width and into pages at its height. The pages get the sheet's
// paper, header and footer and are numbered on with the sheet's.

using System.Globalization;

using Filee.Engines.Documents;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

internal static class SheetCommentList
{
    /// <summary>Lines of one sheet's list laid out at most; the notes after them are left out.</summary>
    private const int MaxLines = 200_000;

    /// <summary>A line on a page: a piece of a text, where its pen starts and its baseline (page points).</summary>
    internal sealed record Line(StyledText Text, int Start, int End, float X, float Baseline);

    /// <summary>The pages of the list; empty when the sheet prints no notes at its end or has none in <paramref name="printed"/>.</summary>
    public static List<List<Line>> Layout(Workbook book, Worksheet sheet, SheetFonts fonts, CellRange printed, LayoutGuard guard)
    {
        var pages = new List<List<Line>>();
        if (sheet.PageSetup.Comments != SheetCommentsPrint.AtEnd || sheet.Comments.Count == 0)
            return pages;
        var comments = sheet.Comments.Where(c => printed.Contains(c.Row, c.Column)).OrderBy(c => c.Row).ThenBy(c => c.Column).ToList();
        if (comments.Count == 0)
            return pages;
        var setup = sheet.PageSetup;
        var (width, height) = setup.PageSize;
        var left = (float)setup.LeftMargin;
        var top = (float)setup.TopMargin;
        var bottom = (float)(height - setup.BottomMargin);
        var room = Math.Max(36, (float)(width - setup.LeftMargin - setup.RightMargin));
        var style = new CellStyle();
        var words = SheetWords.Of(CultureInfo.CurrentUICulture);
        var y = top;
        var lines = 0;
        var page = new List<Line>();
        void Add(string text, bool blank)
        {
            var styled = CellText.Build(fonts, book, new SheetCell(text, style), style, SKColors.Black, wrap: true);
            var (ascent, _, lineHeight) = styled.LineMetrics(0, text.Length);
            if (lineHeight <= 0)
                lineHeight = fonts.Pixels(book.DefaultFontName, book.DefaultFontSize).LineHeight * 0.75f;
            foreach (var (start, end) in blank ? new List<(int Start, int End)> { (0, 0) } : styled.Lines(room, wrap: true))
            {
                guard.Paragraph();
                if (y + lineHeight > bottom + 0.01f && page.Count > 0)
                {
                    pages.Add(page);
                    page = [];
                    y = top;
                }
                if (end > start)
                    page.Add(new Line(styled, start, end, left, y + 0.75f + ascent));
                y += lineHeight;
                lines++;
            }
        }
        foreach (var comment in comments)
        {
            if (lines >= MaxLines)
                break;
            if (comment != comments[0])
                Add(" ", blank: true);
            Add($"{words.Cell} {SheetComments.CellName(comment.Row, comment.Column)}", blank: false);
            Add($"{words.Comment} {Message(comment.Author, comment.Text)}", blank: false);
            foreach (var (author, text) in comment.Replies)
            {
                if (lines >= MaxLines)
                    break;
                Add($"{words.Reply} {Message(author, text)}", blank: false);
            }
        }
        if (page.Count > 0)
            pages.Add(page);
        return pages;
    }

    /// <summary>A note's text after its author's name and a colon, unless the text already begins with the name (Excel's own).</summary>
    private static string Message(string? author, string text) =>
        author is { Length: > 0 } && !text.StartsWith(author, StringComparison.Ordinal) ? $"{author}:\n{text}" : text;
}
