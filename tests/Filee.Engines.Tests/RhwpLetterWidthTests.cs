// Cyrillic, Greek and other letters rhwp has no width for (issue #1262, rhwp#8143): rhwp lays them out at half an em,
// so a Russian text file came out as a PDF with its letters stuck together when LibreOffice wasn't there to take it.
// RhwpInput gives such letters letter spacing up to the advance their font really has.

using System.Text;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class RhwpLetterWidthTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string TextFile(string text)
    {
        var dir = fx.NewFolder();
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(DocumentReaders.TextDocument(Encoding.UTF8.GetBytes(text)), hwpx);
        return hwpx;
    }

    /// <summary>A computer whose font draws ш a full em wide, и 0.4 and every other letter half an em, like rhwp guesses.</summary>
    private static InstalledFont? Computer(string face) =>
        new(true, _ => true, c => c == 'ш' ? 1.0 : c == 'и' ? 0.4 : 0.5);

    /// <summary>The letter spacing each character of the first paragraph ended up with.</summary>
    private static Dictionary<char, int> SpacingOf(string hwpx, string text)
    {
        var result = new Dictionary<char, int>();
        foreach (var run in ParagraphWith(Xml(hwpx), text).Elements(Hp + "run"))
        {
            var spacing = HeadItem(hwpx, "charPr", (string)run.Attribute("charPrIDRef")!).Element(Hh + "spacing");
            var value = (int?)spacing?.Attribute("latin") ?? 0;
            foreach (var c in run.Elements(Hp + "t").SelectMany(t => t.Value))
                result[c] = value;
        }
        return result;
    }

    [Fact]
    public void Cyrillic_letters_get_spacing_up_to_the_width_of_their_font()
    {
        var hwpx = TextFile("Привет, шаг");

        var copy = RhwpInput.Prepare(hwpx, Path.GetDirectoryName(hwpx)!, Computer);

        Assert.NotNull(copy);
        ValidPackage(copy);
        var runs = ParagraphWith(Xml(copy), "Привет").Elements(Hp + "run").Where(r => r.Element(Hp + "t") is not null);
        Assert.Equal("Привет, шаг", string.Concat(runs.Select(r => r.Element(Hp + "t")!.Value))); // the text is the same
        var spacing = SpacingOf(copy, "Привет");
        Assert.Equal(100, spacing['ш']); // a full em where rhwp guessed half of one
        Assert.Equal(-20, spacing['и']); // 0.4 em
        Assert.Equal(0, spacing['П']); // half an em: rhwp's guess is right
        Assert.Equal(0, spacing['а']);
        Assert.Equal(0, spacing[' ']); // measured by rhwp
        Assert.Equal(0, spacing[',']);
    }

    [Fact]
    public void Greek_and_extended_Latin_letters_are_widened_too()
    {
        var hwpx = TextFile("Ωmega łódź");

        var copy = RhwpInput.Prepare(hwpx, Path.GetDirectoryName(hwpx)!, _ => new InstalledFont(true, _ => true, c => c is 'Ω' or 'ł' ? 0.8 : 0.5));

        Assert.NotNull(copy);
        var spacing = SpacingOf(copy, "mega");
        Assert.Equal(60, spacing['Ω']);
        Assert.Equal(60, spacing['ł']);
        Assert.Equal(0, spacing['m']); // ASCII: rhwp has its width
        Assert.Equal(0, spacing['ó']); // Latin-1: so is this one
    }

    [Fact]
    public void Letters_rhwp_measures_are_left_alone()
    {
        var hwpx = TextFile("Hello, déjà vu — 안녕");

        Assert.Null(RhwpInput.Prepare(hwpx, Path.GetDirectoryName(hwpx)!, _ => new InstalledFont(true, _ => true, _ => 1.0)));
    }

    [Fact]
    public void Without_a_font_that_tells_its_widths_nothing_changes()
    {
        var hwpx = TextFile("Привет");

        Assert.Null(RhwpInput.Prepare(hwpx, Path.GetDirectoryName(hwpx)!, _ => new InstalledFont(true, _ => true)));
    }
}
