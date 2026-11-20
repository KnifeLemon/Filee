// RTF tables as Word writes them: \trgaph and \trpadd* give the cells' padding, \clwWidth each cell's preferred width,
// and \trleft-108 puts the cells' borders in the margin so their text starts at it.

using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

public sealed class RtfTableMarginTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private const string Head = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Noto Sans;}}\paperw11906\paperh16838\margl1440\margr1440\margt1440\margb1440 ";

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private HTable Table(string rtf) =>
        RtfReader.Read(Encoding.Latin1.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default).Sections.SelectMany(s => s.Blocks).OfType<HTable>().Single();

    [Fact]
    public void Word_rows_give_the_padding_and_start_in_the_margin()
    {
        var table = Table(Head + @"\trowd\trgaph108\trleft-108\cellx2000\cellx4000\intbl One\cell Two\cell\row}");

        Assert.Equal(HwpxUnits.FromTwips(-108), table.Indent);
        Assert.Equal(new HInsets(HwpxUnits.FromTwips(108), HwpxUnits.FromTwips(108), 0, 0), table.CellMargin);
        Assert.Equal(new[] { HwpxUnits.FromTwips(2108), HwpxUnits.FromTwips(2000) }, table.ColumnWidths);
    }

    [Fact]
    public void Padding_in_twips_wins_over_the_gap_and_padding_without_its_unit_is_ignored()
    {
        var table = Table(Head + @"\trowd\trgaph108\trpaddl50\trpaddfl3\trpaddr70\trpaddt30\trpaddft3\cellx2000\intbl One\cell\row}");

        // \trpaddr has no \trpaddfr3: Word 97's \trgaph counts on the right.
        Assert.Equal(new HInsets(HwpxUnits.FromTwips(50), HwpxUnits.FromTwips(108), HwpxUnits.FromTwips(30), 0), table.CellMargin);
    }

    [Fact]
    public void A_row_without_gap_or_padding_keeps_the_default_and_rows_after_it_share_the_definition()
    {
        Assert.Null(Table(Head + @"\trowd\cellx2000\intbl One\cell\row}").CellMargin);

        var table = Table(Head + @"\trowd\trgaph60\cellx2000\intbl One\cell\row\intbl Two\cell\row}");
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new HInsets(HwpxUnits.FromTwips(60), HwpxUnits.FromTwips(60), 0, 0), table.CellMargin);
    }

    [Fact]
    public void Cells_keep_their_preferred_widths()
    {
        var table = Table(Head + @"\trowd\trautofit1\clwWidth1500\clftsWidth3\cellx2000\clwWidth2500\clftsWidth2\cellx4000\clftsWidth1\cellx6000" +
                          @"\intbl A\cell B\cell C\cell\row}");
        var cells = table.Rows[0].Cells;

        Assert.Equal(new HPreferredWidth(HWidthType.Absolute, HwpxUnits.FromTwips(1500)), cells[0].Width);
        Assert.Equal(new HPreferredWidth(HWidthType.Percent, 0.5), cells[1].Width);
        Assert.Equal(HPreferredWidth.Auto, cells[2].Width);
    }

    [Fact]
    public async Task The_first_cells_text_starts_at_the_margin()
    {
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin), "Noto Sans not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var path = Path.Combine(fx.NewFolder(), "table.rtf");
        await File.WriteAllBytesAsync(path, Encoding.Latin1.GetBytes(Head + @"\trowd\trgaph108\trleft-108\cellx3000\intbl Inside\cell\row\pard Outside\par}"),
            TestContext.Current.CancellationToken);
        var document = await DocumentReaders.ReadAsync(path, "rtf", fx.NewFolder(), TestContext.Current.CancellationToken);
        var page = new PageComposer(new FontResolver(_fonts), _shaper, document.Kinsoku, StyleResolver.FromTemplate())
            .Compose(document, TestContext.Current.CancellationToken).Pages[0];

        float Left(string text) => page.Items.OfType<GlyphItem>().Select(i => i.Run).First(r => r.Text.Contains(text, StringComparison.Ordinal)).Positions[0].X;
        Assert.Equal(Left("Outside"), Left("Inside"), 0.5f);
    }
}
