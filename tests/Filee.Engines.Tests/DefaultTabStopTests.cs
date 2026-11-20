// Default tab stops and fonts' other names as the readers take them from their formats: RTF's \deftab (Word's half
// inch when the file gives none) and \falt, explicit \tx stops, and ODF's style:tab-stop-distance (LibreOffice's
// 1.25 cm when the default paragraph style gives none), counted from the indent unless settings.xml says otherwise.

using System.IO.Compression;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Tests;

public class DefaultTabStopTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private HDocument Rtf(string rtf) => RtfReader.Read(Encoding.Latin1.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default);

    [Fact]
    public void Rtf_default_tabs_are_deftab_or_half_an_inch()
    {
        Assert.Equal(3600, Rtf("{\\rtf1\\ansi Text}").DefaultTabStop);
        Assert.Equal(5670, Rtf("{\\rtf1\\ansi\\deftab1134 Text}").DefaultTabStop);
        Assert.False(Rtf("{\\rtf1\\ansi Text}").DefaultTabStopFromIndent);
    }

    [Fact]
    public void Rtf_tab_stops_are_read_from_the_indent()
    {
        var document = Rtf("{\\rtf1\\ansi\\pard\\li720\\tx1440\\tqr\\tldot\\tx4320\\tqc\\tx2880 a\\tab b\\par}");

        var tabs = Assert.IsType<HParagraph>(Assert.Single(document.Sections[0].Blocks)).Format.Tabs!;
        Assert.Equal([new HTabStop(3600, HTabKind.Left, "NONE"), new HTabStop(10800, HTabKind.Center, "NONE"), new HTabStop(18000, HTabKind.Right, "DOT")], tabs);
    }

    [Fact]
    public void Rtf_fonts_keep_their_other_names()
    {
        var document = Rtf("{\\rtf1\\ansi{\\fonttbl{\\f0\\fnil\\fcharset0 Corporate Sans{\\*\\falt Arial};}{\\f1\\fnil\\fcharset129 \\'b8\\'bc\\'c0\\'ba \\'b0\\'ed\\'b5\\'f1{\\*\\falt Malgun Gothic};}"
                            + "{\\f2\\fnil Same{\\*\\falt Same};}}\\f0 Text}");

        Assert.Equal("Arial", document.FontAlternates["Corporate Sans"]);
        Assert.Equal("Malgun Gothic", document.FontAlternates["맑은 고딕"]);
        Assert.False(document.FontAlternates.ContainsKey("Same"));
        Assert.Equal("Corporate Sans", Assert.IsType<HText>(Assert.IsType<HParagraph>(document.Sections[0].Blocks[0]).Inlines[0]).Format.Font);
    }

    // ───────────────────────── ODT ─────────────────────────

    private string Odt(string? defaultParagraph, string? settings)
    {
        var path = Path.Combine(fx.NewFolder(), "tabs.odt");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Entry(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            Entry("mimetype", Hwp.Hwpx.Odt.Odf.MimeType);
            Entry("content.xml", $"<office:document-content {Hwp.Hwpx.Odt.Odf.Namespaces}><office:body><office:text><text:p>a<text:tab/>b</text:p></office:text></office:body></office:document-content>");
            Entry("styles.xml", $"<office:document-styles {Hwp.Hwpx.Odt.Odf.Namespaces}><office:styles>" +
                                (defaultParagraph is null ? "" : $"<style:default-style style:family=\"paragraph\"><style:paragraph-properties style:tab-stop-distance=\"{defaultParagraph}\"/></style:default-style>") +
                                "</office:styles></office:document-styles>");
            if (settings is not null)
            {
                Entry("settings.xml", "<office:document-settings xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:config=\"urn:oasis:names:tc:opendocument:xmlns:config:1.0\">" +
                                      "<office:settings><config:config-item-set config:name=\"ooo:configuration-settings\">" +
                                      $"<config:config-item config:name=\"TabsRelativeToIndent\" config:type=\"boolean\">{settings}</config:config-item>" +
                                      "</config:config-item-set></office:settings></office:document-settings>");
            }
        }
        return path;
    }

    [Fact]
    public void Odt_default_tabs_come_from_the_default_paragraph_style()
    {
        var document = Hwp.Hwpx.Odt.OdtReader.Read(Odt("2cm", null), fx.NewFolder());

        Assert.Equal((int)Math.Round(2 * 7200 / 2.54), document.DefaultTabStop);
        Assert.True(document.DefaultTabStopFromIndent);
    }

    [Fact]
    public void Odt_without_a_distance_has_libreoffice_s_and_settings_can_count_from_the_margin()
    {
        var document = Hwp.Hwpx.Odt.OdtReader.Read(Odt(null, "false"), fx.NewFolder());

        Assert.Equal((int)Math.Round(1.25 * 7200 / 2.54), document.DefaultTabStop);
        Assert.False(document.DefaultTabStopFromIndent);
        Assert.True(Hwp.Hwpx.Odt.OdtReader.Read(Odt(null, "true"), fx.NewFolder()).DefaultTabStopFromIndent);
    }
}
