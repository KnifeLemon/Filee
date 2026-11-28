using System.Globalization;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

[Collection(nameof(PdfMergeStructureCollection))]
public class PdfMergePageLabelTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static PdfItem? V(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
    private static PdfDictionary D(PdfItem? item) => Assert.IsAssignableFrom<PdfDictionary>(V(item));
    private static PdfArray A(PdfItem? item) => Assert.IsType<PdfArray>(V(item));
    private static int N(PdfItem? item) => Assert.IsType<PdfInteger>(V(item)).Value;
    private static PdfArray Items(PdfDocument document, params PdfItem[] items)
    {
        var array = new PdfArray(document);
        foreach (var item in items)
            array.Elements.Add(item);
        return array;
    }
    private static PdfDictionary Section(PdfDocument document, string? style = "/D", string? prefix = null,
        long start = 1, PdfStringEncoding encoding = PdfStringEncoding.RawEncoding)
    {
        var section = new PdfDictionary(document);
        if (style is not null)
            section.Elements["/S"] = new PdfName(style);
        if (prefix is not null)
            section.Elements["/P"] = new PdfString(prefix, encoding);
        section.Elements["/St"] = start <= int.MaxValue ? new PdfInteger((int)start) : new PdfLongInteger(start);
        return section;
    }
    private static void Tree(PdfDocument document, params PdfItem[] pairs)
    {
        var tree = new PdfDictionary(document);
        tree.Elements["/Nums"] = Items(document, pairs);
        document.Internals.Catalog.Elements["/PageLabels"] = tree;
    }
    private string Source(int pages = 2, Action<PdfDocument>? configure = null)
    {
        var path = Path.Combine(fx.NewFolder(), "input.pdf");
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
            document.AddPage();
        configure?.Invoke(document);
        document.Save(path);
        return path;
    }
    private string Merge(string[] inputs, PdfMergeStructure.Limits? limits = null, bool bookmarks = false)
    {
        var path = Path.Combine(fx.NewFolder(), "merged.pdf");
        PdfSharpConverter.Merge(inputs.Select(input => new PdfMergeInput(input, "AB")).ToArray(),
            path, bookmarks, null, TestContext.Current.CancellationToken, limits);
        return path;
    }
    private string Save(params PdfPageReference[] pages)
    {
        var path = Path.Combine(fx.NewFolder(), "edited.pdf");
        PdfPageTools.Save(pages, path, TestContext.Current.CancellationToken);
        return path;
    }
    private static PdfArray Nums(PdfDocument document)
    {
        Assert.IsType<PdfReference>(document.Internals.Catalog.Elements["/PageLabels"]);
        var tree = D(document.Internals.Catalog.Elements["/PageLabels"]);
        Assert.Equal(new[] { "/Nums" }, tree.Elements.Keys);
        return A(tree.Elements["/Nums"]);
    }
    private static int[] Keys(PdfDocument document) =>
        Nums(document).Elements.Where((_, index) => index % 2 == 0).Select(N).ToArray();
    private static PdfDictionary At(PdfDocument document, int index) => D(Nums(document).Elements[index * 2 + 1]);
    private static string Prefix(PdfDictionary section)
    {
        if (V(section.Elements["/P"]) is not PdfString text)
            return "";
        return text.Encoding == PdfStringEncoding.Unicode
            ? Regex.Replace(text.Value, "\u001B[^\u001B]*\u001B", "")
            : text.Value.Replace("\u00A0", "\u20AC", StringComparison.Ordinal);
    }
    private static string[] Labels(PdfDocument document)
    {
        var nums = Nums(document);
        var section = 0;
        var result = new string[document.PageCount];
        for (var i = 0; i < result.Length; i++)
        {
            while (section + 2 < nums.Elements.Count && N(nums.Elements[section + 2]) <= i)
                section += 2;
            var dictionary = D(nums.Elements[section + 1]);
            var number = (dictionary.Elements.ContainsKey("/St") ? N(dictionary.Elements["/St"]) : 1)
                + i - N(nums.Elements[section]);
            var style = (V(dictionary.Elements["/S"]) as PdfName)?.Value;
            var suffix = style switch
            {
                "/D" => number.ToString(CultureInfo.InvariantCulture),
                "/r" => Roman(number).ToLowerInvariant(),
                "/R" => Roman(number),
                "/A" => new string((char)('A' + (number - 1) % 26), (number - 1) / 26 + 1),
                "/a" => new string((char)('a' + (number - 1) % 26), (number - 1) / 26 + 1),
                _ => "",
            };
            result[i] = Prefix(dictionary) + suffix;
        }
        return result;
    }
    private static string Roman(int number)
    {
        var result = "";
        foreach (var (value, text) in new (int, string)[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        })
            while (number >= value)
            {
                result += text;
                number -= value;
            }
        return result;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T1_Merge_preserves_labels_and_restarts_unlabelled_sources(bool reverse)
    {
        var labelled = Source(4, d => Tree(d, new PdfInteger(0), Section(d, "/r"), new PdfInteger(2), Section(d)));
        var plain = Source();
        using var result = PdfReader.Open(Merge(reverse ? [plain, labelled] : [labelled, plain]), PdfDocumentOpenMode.Import);
        Assert.Equal(reverse ? ["1", "2", "i", "ii", "1", "2"] : new[] { "i", "ii", "1", "2", "1", "2" }, Labels(result));
        Assert.Equal(new[] { 0, 2, 4 }, Keys(result));
        Assert.Equal(reverse ? new[] { "/D", "/r", "/D" } : ["/r", "/D", "/D"],
            Enumerable.Range(0, 3).Select(i => Assert.IsType<PdfName>(At(result, i).Elements["/S"]).Value));
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(new[] { "/S" }, At(result, i).Elements.Keys));
    }

    [Fact]
    public void T2_Styles_prefixes_and_unnumbered_sections_have_minimal_structure()
    {
        var input = Source(13, d => Tree(d,
            new PdfInteger(0), Section(d, "/R"),
            new PdfInteger(2), Section(d),
            new PdfInteger(4), Section(d, "/A", "A-"),
            new PdfInteger(6), Section(d, "/a", "", 5),
            new PdfInteger(8), Section(d, null, "only", 99),
            new PdfInteger(10), Section(d, null),
            new PdfInteger(11), Section(d, "/D", "\uD55C-", encoding: PdfStringEncoding.Unicode)));
        using var result = PdfReader.Open(Merge([input]), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "I", "II", "1", "2", "A-A", "A-B", "e", "f", "only", "only", "", "\uD55C-1", "\uD55C-2" }, Labels(result));
        Assert.Equal(new[] { 0, 2, 4, 6, 8, 10, 11 }, Keys(result));
        Assert.Equal(5, N(At(result, 3).Elements["/St"]));
        Assert.Equal(new[] { "/P" }, At(result, 4).Elements.Keys);
        Assert.Empty(At(result, 5).Elements.Keys);
        for (var i = 0; i < 7; i++)
        {
            if (i != 3)
                Assert.False(At(result, i).Elements.ContainsKey("/St"));
            if (i is not (2 or 4 or 6))
                Assert.False(At(result, i).Elements.ContainsKey("/P"));
        }
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(3, 2)]
    public void T3_Only_consecutive_numbers_join_across_sources(int start, int sections)
    {
        var first = Source(3, d => Tree(d, new PdfInteger(0), Section(d)));
        var second = Source(2, d => Tree(d, new PdfInteger(0), Section(d, start: start)));
        using var result = PdfReader.Open(Merge([first, second]), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "1", "2", "3", start.ToString(CultureInfo.InvariantCulture), (start + 1).ToString(CultureInfo.InvariantCulture) }, Labels(result));
        Assert.Equal(sections, Keys(result).Length);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("reorder")]
    [InlineData("repeat")]
    [InlineData("mixed")]
    public void T4_Rebuild_preserves_each_page_occurrence(string kind)
    {
        var input = Source(4, d => Tree(d, new PdfInteger(0), Section(d, "/r"), new PdfInteger(2), Section(d, prefix: "A-")));
        var other = Source(2, d => Tree(d, new PdfInteger(0), Section(d, "/A", start: 26)));
        var (pages, expected) = kind switch
        {
            "delete" => (new[] { new PdfPageReference(input, 1), new(input, 3) }, new[] { "ii", "A-2" }),
            "reorder" => ([new(input, 3), new(input, 0), new(input, 2)], new[] { "A-2", "i", "A-1" }),
            "repeat" => ([new(input, 1), new(input, 1), new(input, 2)], new[] { "ii", "ii", "A-1" }),
            _ => ([new(input, 3), new(other, 0), new(input, 0), new(other, 1)], new[] { "A-2", "Z", "i", "AA" }),
        };
        using var result = PdfReader.Open(Save(pages), PdfDocumentOpenMode.Import);
        Assert.Equal(expected, Labels(result));
    }

    [Theory]
    [InlineData("/S", false)]
    [InlineData("/S", true)]
    [InlineData("/P", false)]
    [InlineData("/P", true)]
    [InlineData("/St", false)]
    [InlineData("/St", true)]
    public void T5_Null_entries_are_absent(string key, bool indirect)
    {
        var input = Source(2, d =>
        {
            var section = Section(d, prefix: "A", start: 5);
            var nil = new PdfNullObject(d);
            d.Internals.AddObject(nil);
            section.Elements[key] = indirect ? nil.Reference! : PdfNull.Value;
            Tree(d, new PdfInteger(0), section);
        });
        using var result = PdfReader.Open(Merge([input]), PdfDocumentOpenMode.Import);
        Assert.Equal(key switch { "/S" => ["A", "A"], "/P" => ["5", "6"], _ => new[] { "A1", "A2" } }, Labels(result));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("real-key")]
    [InlineData("string-key")]
    [InlineData("value")]
    [InlineData("style")]
    [InlineData("style-type")]
    [InlineData("prefix")]
    [InlineData("zero-start")]
    [InlineData("negative-start")]
    [InlineData("real-start")]
    [InlineData("string-start")]
    [InlineData("not-dictionary")]
    [InlineData("empty")]
    [InlineData("invalid-out-of-range")]
    public void T6_Invalid_pairs_do_not_make_a_labelled_source(string kind)
    {
        var input = Source(2, d =>
        {
            PdfItem key = kind switch
            {
                "negative" => new PdfInteger(-1),
                "real-key" => new PdfReal(.5),
                "string-key" => new PdfString("0"),
                "invalid-out-of-range" => new PdfInteger(2),
                _ => new PdfInteger(0),
            };
            var section = Section(d, prefix: "A");
            switch (kind)
            {
                case "style": section.Elements["/S"] = new PdfName("/Unknown"); break;
                case "invalid-out-of-range": section.Elements["/S"] = new PdfName("/Unknown"); break;
                case "style-type": section.Elements["/S"] = new PdfString("D"); break;
                case "prefix": section.Elements["/P"] = new PdfInteger(1); break;
                case "zero-start": section.Elements["/St"] = new PdfInteger(0); break;
                case "negative-start": section.Elements["/St"] = new PdfInteger(-1); break;
                case "real-start": section.Elements["/St"] = new PdfReal(1.5); break;
                case "string-start": section.Elements["/St"] = new PdfString("1"); break;
            }
            Tree(d, key, kind == "value" ? new PdfString("bad") : section);
            if (kind == "empty")
                Tree(d);
            if (kind == "not-dictionary")
                d.Internals.Catalog.Elements["/PageLabels"] = new PdfInteger(1);
        });
        using var result = PdfReader.Open(Merge([input]), PdfDocumentOpenMode.Import);
        Assert.False(result.Internals.Catalog.Elements.ContainsKey("/PageLabels"));
    }

    [Fact]
    public void T6_Kids_unsorted_duplicate_keys_cycles_and_limits_use_traversal_order()
    {
        var input = Source(5, d =>
        {
            Tree(d, new PdfInteger(3), Section(d, "/A"), new PdfInteger(1), new PdfInteger(0),
                new PdfInteger(1), Section(d, "/r"), new PdfInteger(1), Section(d, prefix: "wrong"),
                new PdfInteger(2), Section(d, prefix: "B"));
            var root = D(d.Internals.Catalog.Elements["/PageLabels"]);
            var child = new PdfDictionary(d);
            d.Internals.AddObject(root);
            d.Internals.Catalog.Elements["/PageLabels"] = root.Reference!;
            d.Internals.AddObject(child);
            child.Elements["/Nums"] = Items(d, new PdfInteger(4), Section(d, null, "last"));
            child.Elements["/Kids"] = Items(d, root.Reference!);
            root.Elements["/Kids"] = Items(d, child.Reference!);
            root.Elements["/Limits"] = Items(d, new PdfInteger(99), new PdfInteger(100));
        });
        using var result = PdfReader.Open(Merge([input]), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "1", "i", "B1", "A", "last" }, Labels(result));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Keys(result));
    }

    [Fact]
    public void T6_Out_of_range_valid_keys_enable_labels_without_applying_a_section()
    {
        var input = Source(2, d => Tree(d, new PdfInteger(2), Section(d, "/r"),
            new PdfLongInteger(long.MaxValue), Section(d, "/A")));
        using var result = PdfReader.Open(Merge([input, Source()]), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "1", "2", "1", "2" }, Labels(result));
        Assert.Equal(new[] { 0, 2 }, Keys(result));
    }

    [Fact]
    public void T6_Indirect_keys_and_entries_are_resolved()
    {
        var input = Source(2, d =>
        {
            var key = new PdfIntegerObject(d, 0);
            var style = new PdfNameObject(d, "/D");
            var prefix = new PdfStringObject(d, "A");
            var start = new PdfIntegerObject(d, 5);
            foreach (var item in new PdfObject[] { key, style, prefix, start })
                d.Internals.AddObject(item);
            var section = new PdfDictionary(d);
            section.Elements["/S"] = style.Reference!;
            section.Elements["/P"] = prefix.Reference!;
            section.Elements["/St"] = start.Reference!;
            d.Internals.AddObject(section);
            Tree(d, key.Reference!, section.Reference!);
        });
        using var result = PdfReader.Open(Merge([input]), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "A5", "A6" }, Labels(result));
    }

    [Theory]
    [InlineData(false, false, 2147483647L)]
    [InlineData(false, true, 2147483647L)]
    [InlineData(false, false, 2147483648L)]
    [InlineData(false, true, 2147483648L)]
    [InlineData(true, false, 2147483648L)]
    [InlineData(true, true, 2147483648L)]
    [InlineData(true, false, 9223372036854775807L)]
    [InlineData(false, true, 9223372036854775807L)]
    public void T6_Number_overflow_falls_back_only_for_numbered_sections(bool unnumbered, bool indirect, long start)
    {
        var input = Source(2, d =>
        {
            var section = Section(d, unnumbered ? null : "/D", "A", start);
            if (indirect)
            {
                PdfObject number = start <= int.MaxValue ? new PdfIntegerObject(d, (int)start) : new PdfLongIntegerObject(d, start);
                d.Internals.AddObject(number);
                section.Elements["/St"] = number.Reference!;
            }
            Tree(d, new PdfInteger(0), section);
        });
        using var result = PdfReader.Open(Merge([input]), PdfDocumentOpenMode.Import);
        Assert.Equal(unnumbered ? ["A", "A"] : start == int.MaxValue ? ["A2147483647", "2"] : new[] { "1", "2" }, Labels(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T7_Node_and_depth_limits_keep_already_accepted_ranges(bool depth)
    {
        var input = Source(4, d =>
        {
            Tree(d, new PdfInteger(0), Section(d));
            var root = D(d.Internals.Catalog.Elements["/PageLabels"]);
            var first = new PdfDictionary(d);
            var deep = new PdfDictionary(d);
            var sibling = new PdfDictionary(d);
            first.Elements["/Nums"] = Items(d, new PdfInteger(1), Section(d, "/r"));
            deep.Elements["/Nums"] = Items(d, new PdfInteger(2), Section(d, prefix: "deep"));
            sibling.Elements["/Nums"] = Items(d, new PdfInteger(3), Section(d, "/A"));
            first.Elements["/Kids"] = Items(d, deep);
            root.Elements["/Kids"] = Items(d, first, sibling);
        });
        var limits = depth ? new PdfMergeStructure.Limits { NameTreeDepth = 2 } : new() { NameTreeNodes = 2 };
        using var result = PdfReader.Open(Merge([input], limits), PdfDocumentOpenMode.Import);
        Assert.Equal(depth ? ["1", "i", "ii", "A"] : new[] { "1", "i", "ii", "iii" }, Labels(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unicode-empty")]
    public void T7_Empty_prefixes_cost_nothing(string? prefix)
    {
        var first = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: prefix is null ? null : "",
            encoding: prefix == "unicode-empty" ? PdfStringEncoding.Unicode : PdfStringEncoding.RawEncoding)));
        var second = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: "B", start: 2)));
        using var result = PdfReader.Open(Merge([first, second], new() { StringBytes = 1 }), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "1", "B2" }, Labels(result));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 4)]
    [InlineData(true, 3)]
    public void T7_Grouping_uses_display_text_and_the_first_prefix_cost(bool unicodeFirst, int budget)
    {
        var raw = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: "\u00A0", start: unicodeFirst ? 2 : 1)));
        var unicode = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: "\u20AC", start: unicodeFirst ? 1 : 2, encoding: PdfStringEncoding.Unicode)));
        using var result = PdfReader.Open(Merge(unicodeFirst ? [unicode, raw] : [raw, unicode], new() { StringBytes = budget }), PdfDocumentOpenMode.Import);
        Assert.Equal(unicodeFirst && budget < 4 ? ["1", "2"] : new[] { "\u20AC1", "\u20AC2" }, Labels(result));
        Assert.Equal(new[] { 0 }, Keys(result));
        if (!unicodeFirst)
            Assert.Equal("\u00A0", Assert.IsType<PdfString>(At(result, 0).Elements["/P"]).Value);
    }

    [Fact]
    public void T7_Different_display_text_does_not_group()
    {
        var raw = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: "\u00A0")));
        var unicode = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: "\u00A0", start: 2, encoding: PdfStringEncoding.Unicode)));
        using var result = PdfReader.Open(Merge([raw, unicode]), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "\u20AC1", "\u00A02" }, Labels(result));
        Assert.Equal(new[] { 0, 1 }, Keys(result));
    }

    [Theory]
    [InlineData("\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F", "\u02D8\u02C7\u02C6\u02D9\u02DD\u02DB\u02DA\u02DC")]
    [InlineData("\u0080\u0081\u0082\u0083\u0084\u0085\u0086\u0087\u0088\u0089\u008A\u008B\u008C\u008D\u008E\u008F\u0090\u0091\u0092\u0093\u0094\u0095\u0096\u0097\u0098\u0099\u009A\u009B\u009C\u009D\u009E",
        "\u2022\u2020\u2021\u2026\u2014\u2013\u0192\u2044\u2039\u203A\u2212\u2030\u201E\u201C\u201D\u2018\u2019\u201A\u2122\uFB01\uFB02\u0141\u0152\u0160\u0178\u017D\u0131\u0142\u0153\u0161\u017E")]
    [InlineData("\u00A0", "\u20AC")]
    public void T7_PdfDocEncoding_differences_group_with_their_unicode_display_text(string rawText, string displayText)
    {
        var raw = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: rawText)));
        var unicode = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: displayText, start: 2, encoding: PdfStringEncoding.Unicode)));
        using var result = PdfReader.Open(Merge([raw, unicode], new() { StringBytes = rawText.Length }), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { 0 }, Keys(result));
        Assert.Equal(rawText, Assert.IsType<PdfString>(At(result, 0).Elements["/P"]).Value);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 12)]
    [InlineData(true, 11)]
    public void T7_Language_markers_do_not_change_display_text(bool unicodeFirst, int budget)
    {
        const string Marked = "\u001Ben\u001BA";
        var raw = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: "A", start: unicodeFirst ? 2 : 1)));
        var unicode = Source(1, d => Tree(d, new PdfInteger(0), Section(d, prefix: Marked, start: unicodeFirst ? 1 : 2, encoding: PdfStringEncoding.Unicode)));
        using var result = PdfReader.Open(Merge(unicodeFirst ? [unicode, raw] : [raw, unicode], new() { StringBytes = budget }), PdfDocumentOpenMode.Import);
        Assert.Equal(unicodeFirst && budget < 12 ? ["1", "2"] : new[] { "A1", "A2" }, Labels(result));
        Assert.Equal(new[] { 0 }, Keys(result));
        if (unicodeFirst && budget == 12)
            Assert.Equal(Marked, Assert.IsType<PdfString>(At(result, 0).Elements["/P"]).Value);
    }

    [Fact]
    public void T7_Removed_prefixes_regroup_and_do_not_spend_the_remaining_budget()
    {
        var input = Source(4, d => Tree(d,
            new PdfInteger(0), Section(d),
            new PdfInteger(1), Section(d, prefix: "long", start: 2),
            new PdfInteger(2), Section(d, prefix: "A", start: 3),
            new PdfInteger(3), Section(d, prefix: "A", start: 4)));
        using var result = PdfReader.Open(Merge([input], new() { StringBytes = 1 }), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "1", "2", "A3", "A4" }, Labels(result));
        Assert.Equal(new[] { 0, 2 }, Keys(result));
    }

    [Fact]
    public void T7_Prefix_budget_is_independent_of_bookmark_strings()
    {
        var input = Source(2, d => Tree(d, new PdfInteger(0), Section(d, prefix: "AB")));
        using var result = PdfReader.Open(Merge([input], new() { StringBytes = 6 }, bookmarks: true), PdfDocumentOpenMode.Import);
        Assert.True(result.Internals.Catalog.Elements.ContainsKey("/Outlines"));
        Assert.Equal(new[] { "AB1", "AB2" }, Labels(result));
    }

    [Fact]
    public void T7_Repeated_prefixes_in_separate_ranges_each_spend_budget()
    {
        var input = Source(3, d => Tree(d, new PdfInteger(0), Section(d, prefix: "A")));
        var path = Path.Combine(fx.NewFolder(), "edited.pdf");
        PdfPageTools.Save([new(input, 0), new(input, 2), new(input, 0)], path,
            TestContext.Current.CancellationToken, new() { StringBytes = 2 });
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "A1", "A3", "1" }, Labels(result));
        Assert.Equal(new[] { 0, 1, 2 }, Keys(result));
    }

    [Fact]
    public void T7_Unnumbered_sections_regroup_after_prefixes_are_removed()
    {
        var input = Source(3, d => Tree(d,
            new PdfInteger(0), Section(d, null, ""),
            new PdfInteger(1), Section(d, null, "long"),
            new PdfInteger(2), Section(d, null)));
        using var result = PdfReader.Open(Merge([input], new() { StringBytes = 1 }), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "", "", "" }, Labels(result));
        Assert.Equal(new[] { 0 }, Keys(result));
        Assert.Empty(At(result, 0).Elements.Keys);
    }

    [Fact]
    public void T9_Unlabelled_merge_and_rebuild_do_not_write_page_labels()
    {
        var first = Source();
        var second = Source();
        foreach (var path in new[] { Merge([first, second]), Save(new PdfPageReference(first, 1)), Save(new(first, 1), new(second, 0)) })
        {
            using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.False(result.Internals.Catalog.Elements.ContainsKey("/PageLabels"));
        }
    }
}
