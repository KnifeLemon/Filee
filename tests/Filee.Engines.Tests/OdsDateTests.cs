// Dates of an OpenDocument spreadsheet without a data style of their own (OdsReader): shown in the standard short date
// (or date and time) of the document's language, as Calc shows them, or of the user's when the document names none.

using System.Globalization;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Tests;

public sealed class OdsDateTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private Worksheet Read(string? language, string? country)
    {
        var properties = language is null ? "" : $"<style:text-properties fo:language=\"{language}\"" + (country is null ? "" : $" fo:country=\"{country}\"") + "/>";
        var styles = $"<style:default-style style:family=\"table-cell\">{properties}</style:default-style>";
        const string Tables = "<table:table table:name=\"Sheet1\"><table:table-row>" +
                              "<table:table-cell office:value-type=\"date\" office:date-value=\"2026-03-05\"/>" +
                              "<table:table-cell office:value-type=\"date\" office:date-value=\"2026-03-05T14:30:00\"/>" +
                              "</table:table-row></table:table>";
        return OdsReader.Read(OdsBuilder.Save(Path.Combine(fx.NewFolder(), "book.ods"), "", Tables, styles)).Sheets[0];
    }

    [Theory]
    [InlineData("en", "US", "3/5/26", "03/05/26 02:30 PM", "m/d/yy")]
    [InlineData("en", null, "3/5/26", "03/05/26 02:30 PM", "m/d/yy")]
    [InlineData("en", "GB", "05/03/26", "05/03/26 14:30", "dd/mm/yy")]
    [InlineData("de", "DE", "05.03.26", "05.03.26 14:30", "dd.mm.yy")]
    [InlineData("fr", "FR", "05/03/2026", "05/03/26 14:30", "dd/mm/yyyy")]
    [InlineData("ko", "KR", "2026-03-05", "2026-03-05 14:30:00", "yyyy-mm-dd")]
    public void A_date_without_a_style_shows_in_the_documents_language(string language, string? country, string date, string dateTime, string format)
    {
        var sheet = Read(language, country);
        Assert.Equal((date, format), (sheet.Get(0, 0)!.Text, sheet.Get(0, 0)!.NumberFormat));
        Assert.Equal(dateTime, sheet.Get(0, 1)!.Text);
        Assert.Equal(46086, sheet.Get(0, 0)!.Value);
    }

    [Fact]
    public void Without_a_language_the_users_is_taken()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("05.03.26", Read(null, null).Get(0, 0)!.Text);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Equal("2026-03-05", Read(null, null).Get(0, 0)!.Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
