// Excel's tint of SpreadsheetML colours (theme="n" tint="x"): the "Lighter 80 %" … "Darker 50 %" shades of Office's
// theme colours, as Excel's colour palette shows them, from the tints Excel writes for them (n/32767). The theme colours
// are Office 2013+ ("Office" theme: white, black, lt2, dk2, the six accents) and Office 2007-2010 (dk2, lt2, accents).

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Tests;

public sealed class OoxmlTintTests
{
    /// <summary>The tint Excel writes for each palette step, by percent (− darker, + lighter).</summary>
    private static readonly Dictionary<int, double> Tints = new()
    {
        [80] = 0.79998168889431442,
        [60] = 0.59999389629810485,
        [50] = 0.499984740745262,
        [40] = 0.39997558519241921,
        [35] = 0.34998626667073579,
        [25] = 0.249977111117893,
        [15] = 0.14999847407452621,
        [5] = 4.9989318521683403E-2,
        [-5] = -4.9989318521683403E-2,
        [-10] = -9.9978637043366805E-2,
        [-15] = -0.14999847407452621,
        [-25] = -0.249977111117893,
        [-35] = -0.34998626667073579,
        [-50] = -0.499984740745262,
        [-75] = -0.749992370372631,
        [-90] = -0.89999084444715716,
    };

    [Theory]
    [InlineData("FFFFFF", "-5:F2F2F2 -15:D9D9D9 -25:BFBFBF -35:A6A6A6 -50:808080")]
    [InlineData("000000", "+50:808080 +35:595959 +25:404040 +15:262626 +5:0D0D0D")]
    [InlineData("E7E6E6", "-10:D0CECE -25:AEAAAA -50:757171 -75:3A3838 -90:161616")]
    [InlineData("44546A", "+80:D6DCE4 +60:ACB9CA +40:8497B0 -25:333F4F -50:222B35")]
    [InlineData("4472C4", "+80:D9E1F2 +60:B4C6E7 +40:8EA9DB -25:305496 -50:203764")]
    [InlineData("ED7D31", "+80:FCE4D6 +60:F8CBAD +40:F4B084 -25:C65911 -50:833C0C")]
    [InlineData("A5A5A5", "+80:EDEDED +60:DBDBDB +40:C9C9C9 -25:7B7B7B -50:525252")]
    [InlineData("FFC000", "+80:FFF2CC +60:FFE699 +40:FFD966 -25:BF8F00 -50:806000")]
    [InlineData("5B9BD5", "+80:DDEBF7 +60:BDD7EE +40:9BC2E6 -25:2F75B5 -50:1F4E78")]
    [InlineData("70AD47", "+80:E2EFDA +60:C6E0B4 +40:A9D08E -25:548235 -50:375623")]
    [InlineData("1F497D", "+80:C5D9F1 +60:8DB4E2 +40:538DD5 -25:16365C -50:0F243E")]
    [InlineData("EEECE1", "-10:DDD9C4 -25:C4BD97 -50:948A54 -75:494529 -90:1D1B10")]
    [InlineData("4F81BD", "+80:DCE6F1 +60:B8CCE4 +40:95B3D7 -25:366092 -50:244062")]
    [InlineData("C0504D", "+80:F2DCDB +60:E6B8B7 +40:DA9694 -25:963634 -50:632523")]
    [InlineData("9BBB59", "+80:EBF1DE +60:D8E4BC +40:C4D79B -25:76933C -50:4F6228")]
    [InlineData("8064A2", "+80:E4DFEC +60:CCC0DA +40:B1A0C7 -25:60497A -50:403151")]
    [InlineData("4BACC6", "+80:DAEEF3 +60:B7DEE8 +40:92CDDC -25:31869B -50:215967")]
    [InlineData("F79646", "+80:FDE9D9 +60:FCD5B4 +40:FABF8F -25:E26B0A -50:974706")]
    public void A_tinted_theme_colour_comes_out_as_excel_shows_it(string theme, string shades)
    {
        foreach (var shade in shades.Split(' '))
        {
            var (percent, expected) = (int.Parse(shade[..shade.IndexOf(':')], CultureInfo.InvariantCulture), "#" + shade[(shade.IndexOf(':') + 1)..]);
            Assert.Equal(expected, OoxmlColors.SpreadsheetTint("#" + theme, Tints[percent]));
        }
    }

    [Fact]
    public void Exact_tints_and_the_ends_of_the_range_stay_in_range()
    {
        // Tints written as exact fractions (other programs) land on the same shades.
        Assert.Equal("#D9E1F2", OoxmlColors.SpreadsheetTint("#4472C4", 0.8));
        Assert.Equal("#305496", OoxmlColors.SpreadsheetTint("#4472C4", -0.25));
        Assert.Equal("#FFFFFF", OoxmlColors.SpreadsheetTint("#4472C4", 1));
        Assert.Equal("#000000", OoxmlColors.SpreadsheetTint("#4472C4", -1));
        Assert.Equal("#FFFFFF", OoxmlColors.SpreadsheetTint("#4472C4", 7));
        Assert.Equal("#FF0000", OoxmlColors.SpreadsheetTint("#FF0000", 1e-9));

        // Through a SpreadsheetML colour element: theme 4 is accent 1 (theme 0 and 1 are lt1 and dk1).
        var colors = OoxmlColors.FromTheme(null);
        Assert.Equal("#D9E1F2", colors.Spreadsheet(new XElement("color", new XAttribute("theme", 4), new XAttribute("tint", "0.79998168889431442"))));
        Assert.Equal("#D9D9D9", colors.Spreadsheet(new XElement("color", new XAttribute("theme", 0), new XAttribute("tint", "-0.14999847407452621"))));
    }
}
