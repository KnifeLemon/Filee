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
    public void Each_script_takes_its_width_from_the_slot_rhwp_draws_it_with()
    {
        // rhwp draws Latin Extended from the LATIN slot and Cyrillic and Greek from the HANGUL one (rhwp 0.8.7).
        var hwpx = TextFile("Привет łódź Ωmega");
        var header = Xml(hwpx, "Contents/header.xml");
        foreach (var fontface in header.Descendants(Hh + "fontface"))
        {
            foreach (var font in fontface.Elements(Hh + "font"))
                font.SetAttributeValue("face", (string?)fontface.Attribute("lang") == "LATIN" ? "Latin Font" : "Hangul Font");
        }
        var edited = Path.Combine(fx.NewFolder(), "edited.hwpx");
        File.Copy(hwpx, edited);
        using (var zip = System.IO.Compression.ZipFile.Open(edited, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry("Contents/header.xml")!.Delete();
            using var stream = zip.CreateEntry("Contents/header.xml").Open();
            header.Document!.Save(stream);
        }

        // The LATIN font draws every letter a full em wide, the HANGUL font half an em.
        var copy = RhwpInput.Prepare(edited, Path.GetDirectoryName(edited)!,
            face => new InstalledFont(true, _ => true, _ => face == "Latin Font" ? 1.0 : 0.5));

        Assert.NotNull(copy);
        var spacing = SpacingOf(copy, "mega");
        Assert.Equal(100, spacing['ł']); // LATIN font: a full em
        Assert.Equal(100, spacing['ź']);
        Assert.Equal(0, spacing['П']); // HANGUL font: half an em, rhwp's guess is right
        Assert.Equal(0, spacing['Ω']);
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
