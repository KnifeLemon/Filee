// "Ask every time" for the pages (#981): the range typed in the window checked as it is typed, which drops ask, and
// the preset as it runs once answered; Split PDF asks in a new install only.

using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class AskedPagesTests
{
    [Theory]
    [InlineData("", 120, "", 120)]
    [InlineData("  ", 120, "", 120)]
    [InlineData("13-55", 120, "13-55", 43)]
    [InlineData("1-3,8,12-15", 120, "1-3,8,12-15", 8)]
    [InlineData(" 1 - 3 , 8 ", 120, "1 - 3 , 8", 4)]
    [InlineData("55-13", 120, "55-13", 43)]
    [InlineData("100-", 120, "100-", 21)]
    [InlineData("-3", 120, "-3", 3)]
    [InlineData("3,3,2-4", 120, "3,3,2-4", 3)]
    [InlineData("1-3,", 120, "1-3,", 3)]
    [InlineData("13\u201355", 120, "13-55", 43)] // an en dash, as some keyboards and pasted text give
    [InlineData("\u0967\u0969-\u096B\u096B", 120, "13-55", 43)] // Devanagari digits (Nepali, Hindi keyboards)
    [InlineData("\u0661\u060C\u0663", 120, "1,3", 2)] // Arabic-Indic digits and the Arabic comma
    [InlineData("1\uFF0C3\uFF0D5", 120, "1,3-5", 4)] // full-width comma and hyphen (Chinese, Japanese input)
    [InlineData("120", 120, "120", 1)]
    public void A_range_typed_by_hand_is_used_as_it_is_and_says_how_many_pages(string typed, int pages, string text, int chosen)
    {
        var check = PageRange.Check(typed, pages);
        Assert.True(check.IsValid, typed);
        Assert.Equal(text, check.Text);
        Assert.Equal(chosen, check.Pages);
        // What the converters read takes the same pages.
        Assert.Equal(chosen, PageRange.Parse(check.Text, pages).Count);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("3-x")]
    [InlineData("0")]
    [InlineData("0-4")]
    [InlineData("-")]
    [InlineData("1-3-5")]
    [InlineData(",")]
    [InlineData("1.5")]
    [InlineData("+3")]
    [InlineData("1;3")]
    public void Anything_but_page_numbers_and_spans_from_1_is_invalid(string typed)
    {
        Assert.Equal(PageRangeProblem.Invalid, PageRange.Check(typed, 120).Problem);
        Assert.Equal(PageRangeProblem.Invalid, PageRange.Check(typed, null).Problem);
        Assert.Null(PageRange.Check(typed, 120).Pages);
    }

    [Theory]
    [InlineData("121")]
    [InlineData("13-155")]
    [InlineData("1-3, 200")]
    [InlineData("150-")]
    public void A_page_past_the_last_one_is_said_when_the_count_is_known(string typed)
    {
        Assert.Equal(PageRangeProblem.PastTheEnd, PageRange.Check(typed, 120).Problem);
        // A PDF whose pages couldn't be counted takes it; the converters leave out pages it doesn't have.
        var unknown = PageRange.Check(typed, null);
        Assert.True(unknown.IsValid);
        Assert.Null(unknown.Pages);
    }

    private static Preset Split() => BuiltInData.CreatePresets().Single(p => p.Id == "pdf-split");

    [Fact]
    public void Split_pdf_asks_in_a_new_install_and_other_presets_dont()
    {
        Assert.True(Split().Pdf.AskPageRange);
        Assert.True(AskedPages.Asks(Split()));
        Assert.Equal(["pdf-split"], BuiltInData.CreatePresets().Where(p => p.Pdf.AskPageRange).Select(p => p.Id));
    }

    [Fact]
    public void Merges_groups_and_tools_never_ask()
    {
        var merge = BuiltInData.CreatePresets().Single(p => p.Id == "merge-pdf");
        merge.Pdf.AskPageRange = true;
        Assert.False(AskedPages.Asks(merge));
        var group = BuiltInData.CreatePresets().Single(p => p.Id == "pdf-edit");
        group.Pdf.AskPageRange = true;
        Assert.False(AskedPages.Asks(group));
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == "tool-pdf-pages");
        tool.Pdf.AskPageRange = true;
        Assert.False(AskedPages.Asks(tool));
        // Pages of a PDF become pictures: the window asks for the range, without one PDF / one file per page.
        var png = new Preset { TargetFormat = "png", Pdf = { AskPageRange = true } };
        Assert.True(AskedPages.Asks(png));
        Assert.False(AskedPages.OffersSplit(png));
        Assert.True(AskedPages.OffersSplit(Split()));
        Assert.False(AskedPages.OffersSplit(new Preset { TargetFormat = "pdf", Pdf = { PagesPerSheet = 4 } }));
        Assert.False(AskedPages.OffersSplit(new Preset { TargetFormat = "pdf", Pdf = { Ocr = true } }));
    }

    [Theory]
    [InlineData("book.pdf", true)]
    [InlineData(@"C:\Users\me\Book.PDF", true)]
    [InlineData("scan.png", false)]
    [InlineData("notes.docx", false)]
    public void Only_pdfs_are_asked_about(string path, bool asked) => Assert.Equal(asked, AskedPages.IsPdf(path));

    [Fact]
    public void The_answer_goes_into_a_copy_that_asks_no_more()
    {
        var split = Split();
        var chapter = AskedPages.Apply(split, " 13-55 ", splitPages: false);
        Assert.Equal(("13-55", false, false), (chapter.Pdf.PageRange, chapter.Pdf.SplitPages, chapter.Pdf.AskPageRange));
        // One PDF isn't put alone into Split PDF's folder of pages.
        Assert.True(chapter.Output.SubfolderOnlyForSeveralFiles);
        Assert.Equal(OutputLocation.Subfolder, chapter.Output.Location);

        var pages = AskedPages.Apply(split, "1-3", splitPages: true);
        Assert.Equal(("1-3", true, false), (pages.Pdf.PageRange, pages.Pdf.SplitPages, pages.Pdf.AskPageRange));
        Assert.False(pages.Output.SubfolderOnlyForSeveralFiles);

        // The saved preset is left as it was.
        Assert.Equal(("", true, true), (split.Pdf.PageRange, split.Pdf.SplitPages, split.Pdf.AskPageRange));

        // PDF → PNG: one file per page doesn't apply and stays as saved.
        var png = new Preset { TargetFormat = "png", Pdf = { AskPageRange = true, SplitPages = true } };
        var images = AskedPages.Apply(png, "2", splitPages: false);
        Assert.Equal(("2", true, false), (images.Pdf.PageRange, images.Pdf.SplitPages, images.Pdf.AskPageRange));
    }

    [Fact]
    public void An_existing_library_keeps_its_split_pdf_and_a_new_one_asks()
    {
        // A library saved before #981: Split PDF without the setting. No migration: it reads as off, and stays off.
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveLibrary();
        var path = Path.Combine(dir.Path, "presets.json");
        var presets = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        var split = presets.Single(p => p!["id"]!.GetValue<string>() == "pdf-split")!;
        Assert.True(split["pdf"]!["askPageRange"]!.GetValue<bool>());
        split["pdf"]!.AsObject().Remove("askPageRange");
        File.WriteAllText(path, presets.ToJsonString());
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), $$"""{"schemaVersion":{{AppSettings.CurrentSchemaVersion}}}""");

        var existing = new UserDataStore(dir.Path);
        existing.Load();
        Assert.False(existing.FindPreset("pdf-split")!.Pdf.AskPageRange);
        Assert.False(AskedPages.Asks(existing.FindPreset("pdf-split")!));

        using var fresh = new TempDir();
        var install = new UserDataStore(fresh.Path);
        install.Load();
        Assert.True(install.FindPreset("pdf-split")!.Pdf.AskPageRange);
    }
}
