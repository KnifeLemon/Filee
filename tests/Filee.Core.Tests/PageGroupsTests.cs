// Split PDF every N pages or into equal parts (#1259): which pages go into which file, the names they end with, and a
// preset saved before it still making one file per page.

using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PageGroupsTests
{
    private static string[] Suffixes(string range, int pageCount, int every = 0, int parts = 0) =>
        [.. PageGroups.Of(PageRange.Parse(range, pageCount), new PdfOptions { SplitPages = true, SplitEvery = every, SplitParts = parts })
            .Select(PageGroups.Suffix)];

    [Fact]
    public void Every_n_pages_the_last_file_takes_what_is_left()
    {
        Assert.Equal(["_p1-50", "_p51-100", "_p101-120"], Suffixes("", 120, every: 50));
        Assert.Equal(["_p1-60", "_p61-120"], Suffixes("", 120, every: 60));
        // More pages per file than the PDF has: one file, still named for its pages.
        Assert.Equal(["_p1-10"], Suffixes("", 10, every: 50));
        // A last file of one page is named like one file per page.
        Assert.Equal(["_p1-2", "_p3-4", "_p5"], Suffixes("", 5, every: 2));
    }

    [Fact]
    public void Equal_parts_differ_by_a_page_at_most_the_first_ones_longer()
    {
        Assert.Equal(["_p1-4", "_p5-7", "_p8-10"], Suffixes("", 10, parts: 3));
        Assert.Equal(["_p1-60", "_p61-120"], Suffixes("", 120, parts: 2));
        Assert.Equal(["_p1-3", "_p4-6", "_p7-9"], Suffixes("", 9, parts: 3));
        // More parts than pages: a page each, no empty files.
        Assert.Equal(["_p1", "_p2", "_p3"], Suffixes("", 3, parts: 5));
        // Parts go over pages per file.
        Assert.Equal(["_p1-5", "_p6-10"], Suffixes("", 10, every: 3, parts: 2));
    }

    [Fact]
    public void A_range_is_split_as_chosen()
    {
        Assert.Equal(["_p3-6", "_p7-10", "_p11-12"], Suffixes("3-12", 120, every: 4));
        Assert.Equal(["_p101-110", "_p111-120"], Suffixes("101-", 120, parts: 2));
        // Pages left out stay out: the second file has 8, 12 and 13.
        var groups = PageGroups.Of(PageRange.Parse("1-3,8,12-15", 20), new PdfOptions { SplitPages = true, SplitEvery = 3 });
        Assert.Equal([[0, 1, 2], [7, 11, 12], [13, 14]], groups.Select(g => g.ToArray()));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Without_a_number_it_is_one_file_per_page_as_before(int every, int parts) =>
        Assert.Equal(["_p2", "_p3", "_p5"], Suffixes("2-3,5", 6, every, parts));

    [Fact]
    public void The_answer_of_the_window_carries_the_split()
    {
        var split = BuiltInData.CreatePresets().Single(p => p.Id == "pdf-split");
        var every = AskedPages.Apply(split, "", splitPages: true, splitEvery: 50);
        Assert.Equal((true, 50, 0), (every.Pdf.SplitPages, every.Pdf.SplitEvery, every.Pdf.SplitParts));
        var thirds = AskedPages.Apply(every, "", splitPages: true, splitParts: 3);
        Assert.Equal((true, 0, 3), (thirds.Pdf.SplitPages, thirds.Pdf.SplitEvery, thirds.Pdf.SplitParts));
        // One PDF keeps the numbers, unused, for the next window.
        var one = AskedPages.Apply(thirds, "", splitPages: false);
        Assert.Equal((false, 0, 3), (one.Pdf.SplitPages, one.Pdf.SplitEvery, one.Pdf.SplitParts));
        // PDF → PNG: nothing of a split applies.
        var png = AskedPages.Apply(new Preset { TargetFormat = "png" }, "", splitPages: true, splitEvery: 5);
        Assert.Equal((false, 0), (png.Pdf.SplitPages, png.Pdf.SplitEvery));
    }

    [Fact]
    public void A_library_saved_before_reads_as_one_file_per_page_and_keeps_a_split_saved_now()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveLibrary();
        var path = Path.Combine(dir.Path, "presets.json");
        var presets = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        var split = presets.Single(p => p!["id"]!.GetValue<string>() == "pdf-split")!["pdf"]!.AsObject();
        split.Remove("splitEvery");
        split.Remove("splitParts");
        File.WriteAllText(path, presets.ToJsonString());
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), $$"""{"schemaVersion":{{AppSettings.CurrentSchemaVersion}}}""");

        var old = new UserDataStore(dir.Path);
        old.Load();
        var pdf = old.FindPreset("pdf-split")!.Pdf;
        Assert.Equal((true, 0, 0), (pdf.SplitPages, pdf.SplitEvery, pdf.SplitParts));
        Assert.Equal(3, PageGroups.Of([0, 1, 2], pdf).Count);

        pdf.SplitEvery = 50;
        old.SaveLibrary();
        var again = new UserDataStore(dir.Path);
        again.Load();
        Assert.Equal(50, again.FindPreset("pdf-split")!.Pdf.SplitEvery);
    }
}
