// A sheet's page setup as the readers find it (XLSX pageSetup / pageMargins / printOptions / headerFooter, the XLS
// SETUP, margin and HEADER / FOOTER records, an ODS master page and its page layout), for SheetPrinter. Lengths are
// points; headers and footers are kept in Excel's code language ("&LPage &P of &N&R&A"), which the ODS reader
// writes too.

using System.Globalization;

namespace Filee.Engines.Office.Sheets;

internal sealed record SheetPageSetup
{
    /// <summary>Paper width in points, as the paper stands (portrait).</summary>
    public double PaperWidth { get; init; } = Paper.A4.Width;

    /// <summary>Paper height in points, as the paper stands (portrait).</summary>
    public double PaperHeight { get; init; } = Paper.A4.Height;

    public bool Landscape { get; init; }

    // Excel's default margins ("Normal"): 0.7 in left and right, 0.75 in top and bottom, 0.3 in header and footer.
    public double LeftMargin { get; init; } = 0.7 * 72;
    public double RightMargin { get; init; } = 0.7 * 72;
    public double TopMargin { get; init; } = 0.75 * 72;
    public double BottomMargin { get; init; } = 0.75 * 72;
    public double HeaderMargin { get; init; } = 0.3 * 72;
    public double FooterMargin { get; init; } = 0.3 * 72;

    /// <summary>Adjust to this percent of normal size (10–400), when not <see cref="FitToPage"/>.</summary>
    public int Scale { get; init; } = 100;

    /// <summary>Fit to <see cref="FitWidth"/> pages wide by <see cref="FitHeight"/> tall (0 = as many as needed).</summary>
    public bool FitToPage { get; init; }

    public int FitWidth { get; init; } = 1;
    public int FitHeight { get; init; } = 1;

    /// <summary>Page order "over, then down" instead of Excel's default "down, then over".</summary>
    public bool OverThenDown { get; init; }

    public bool CenterHorizontally { get; init; }
    public bool CenterVertically { get; init; }
    public bool PrintGridLines { get; init; }

    /// <summary>Print the row numbers and column letters.</summary>
    public bool PrintHeadings { get; init; }

    /// <summary>The first page's number when the sheet sets one; null = numbering goes on from the sheet before.</summary>
    public int? FirstPageNumber { get; init; }

    public SheetHeaderFooter HeaderFooter { get; init; } = new();

    /// <summary>The page size as printed (landscape swaps the sides).</summary>
    public (double Width, double Height) PageSize => Landscape
        ? (Math.Max(PaperWidth, PaperHeight), Math.Min(PaperWidth, PaperHeight))
        : (Math.Min(PaperWidth, PaperHeight), Math.Max(PaperWidth, PaperHeight));

    /// <summary>The paper a sheet uses when its file names none: A4, or Letter where the region measures in inches.</summary>
    public static SheetPageSetup ForRegion()
    {
        var letter = !RegionInfo.CurrentRegion.IsMetric;
        return letter ? new SheetPageSetup { PaperWidth = Paper.Letter.Width, PaperHeight = Paper.Letter.Height } : new SheetPageSetup();
    }
}

/// <summary>Header and footer texts in Excel's codes ("&amp;C&amp;P"), and the pictures their &amp;G codes draw.</summary>
internal sealed record SheetHeaderFooter
{
    public string? OddHeader { get; init; }
    public string? OddFooter { get; init; }
    public string? EvenHeader { get; init; }
    public string? EvenFooter { get; init; }
    public string? FirstHeader { get; init; }
    public string? FirstFooter { get; init; }
    public bool DifferentOddEven { get; init; }
    public bool DifferentFirst { get; init; }

    /// <summary>The header and footer grow and shrink with the sheet's scale.</summary>
    public bool ScaleWithDocument { get; init; } = true;

    /// <summary>The left and right sections line up with the page margins (else 0.3 in from the paper's edges).</summary>
    public bool AlignWithMargins { get; init; } = true;

    /// <summary>
    /// Pictures by section: "LH", "CH", "RH" (header), "LF", "CF", "RF" (footer), with "FIRST" or "EVEN" appended for
    /// the first and even pages; the size in points.
    /// </summary>
    public Dictionary<string, (byte[] Data, double Width, double Height)> Pictures { get; init; } = [];

    public bool IsEmpty => OddHeader is null or "" && OddFooter is null or "" && (!DifferentOddEven || EvenHeader is null or "" && EvenFooter is null or "")
                           && (!DifferentFirst || FirstHeader is null or "" && FirstFooter is null or "");
}

/// <summary>Paper sizes by their code in SpreadsheetML pageSetup paperSize and the XLS SETUP record (Windows DMPAPER).</summary>
internal static class Paper
{
    public static readonly (double Width, double Height) A4 = Mm(210, 297);
    public static readonly (double Width, double Height) Letter = In(8.5, 11);

    private static (double, double) In(double width, double height) => (width * 72, height * 72);

    private static (double, double) Mm(double width, double height) => (width * 72 / 25.4, height * 72 / 25.4);

    private static readonly Dictionary<int, (double Width, double Height)> Sizes = new()
    {
        [1] = In(8.5, 11),
        [2] = In(8.5, 11),
        [3] = In(11, 17),
        [4] = In(17, 11),
        [5] = In(8.5, 14),
        [6] = In(5.5, 8.5),
        [7] = In(7.25, 10.5),
        [8] = Mm(297, 420),
        [9] = Mm(210, 297),
        [10] = Mm(210, 297),
        [11] = Mm(148, 210),
        [12] = Mm(257, 364),
        [13] = Mm(182, 257),
        [14] = In(8.5, 13),
        [15] = Mm(215, 275),
        [16] = In(10, 14),
        [17] = In(11, 17),
        [18] = In(8.5, 11),
        [19] = In(3.875, 8.875),
        [20] = In(4.125, 9.5),
        [21] = In(4.5, 10.375),
        [22] = In(4.75, 11),
        [23] = In(5, 11.5),
        [24] = In(17, 22),
        [25] = In(22, 34),
        [26] = In(34, 44),
        [27] = Mm(110, 220),
        [28] = Mm(162, 229),
        [29] = Mm(324, 458),
        [30] = Mm(229, 324),
        [31] = Mm(114, 162),
        [32] = Mm(114, 229),
        [33] = Mm(250, 353),
        [34] = Mm(176, 250),
        [35] = Mm(176, 125),
        [36] = Mm(110, 230),
        [37] = In(3.875, 7.5),
        [38] = In(3.625, 6.5),
        [39] = In(14.875, 11),
        [40] = In(8.5, 12),
        [41] = In(8.5, 13),
        [42] = Mm(250, 353),
        [43] = Mm(100, 148),
        [44] = In(9, 11),
        [45] = In(10, 11),
        [46] = In(15, 11),
        [47] = Mm(220, 220),
        [50] = In(9.275, 12),
        [51] = In(9.275, 15),
        [52] = In(11.69, 18),
        [53] = Mm(236, 322),
        [54] = In(8.275, 11),
        [55] = Mm(210, 297),
        [56] = In(9.275, 12),
        [57] = Mm(227, 356),
        [58] = Mm(305, 487),
        [59] = In(8.5, 12.69),
        [60] = Mm(210, 330),
        [61] = Mm(148, 210),
        [62] = Mm(182, 257),
        [63] = Mm(322, 445),
        [64] = Mm(174, 235),
        [65] = Mm(201, 276),
        [66] = Mm(420, 594),
        [67] = Mm(297, 420),
        [68] = Mm(322, 445),
        [69] = Mm(200, 148),
        [70] = Mm(105, 148),
    };

    /// <summary>The paper of a code, portrait; null for codes Filee doesn't know (the sheet keeps its default).</summary>
    public static (double Width, double Height)? FromCode(int code) =>
        Sizes.TryGetValue(code, out var size) ? (Math.Min(size.Width, size.Height), Math.Max(size.Width, size.Height)) : null;

    /// <summary>"297mm", "8.5in", "21cm" (pageSetup paperWidth / paperHeight) in points; null for anything else.</summary>
    public static double? Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var unit = value.Length > 2 ? value[^2..] : "";
        var number = unit is "mm" or "cm" or "in" or "pt" ? value[..^2] : value;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var length) || !double.IsFinite(length) || length <= 0)
            return null;
        var points = unit switch
        {
            "cm" => length * 72 / 2.54,
            "in" => length * 72,
            "pt" => length,
            _ => length * 72 / 25.4,
        };
        return points is >= 36 and <= 14_400 ? points : null;
    }
}
