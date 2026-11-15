// Generated low-level PDFs exercise catalog structure independently of PDFsharp's convenience writers.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

/// <summary>Runs alone so other tests do not inflate the limit cases' allocation measurements.</summary>
[CollectionDefinition(nameof(PdfMergeStructureCollection), DisableParallelization = true)]
public sealed class PdfMergeStructureCollection;

[Collection(nameof(PdfMergeStructureCollection))]
public class PdfMergeStructureTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static PdfItem? V(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
    private static PdfDictionary D(PdfItem? item) => Assert.IsAssignableFrom<PdfDictionary>(V(item));
    private static PdfArray A(PdfItem? item) => Assert.IsType<PdfArray>(V(item));
    private static string S(PdfItem? item) => V(item) switch
    {
        PdfString text => text.Value,
        PdfStringObject text => text.Value,
        PdfName name => name.Value,
        _ => throw new InvalidOperationException("Not a PDF string or name"),
    };

    private static PdfDictionary Object(PdfDocument document)
    {
        var dictionary = new PdfDictionary(document);
        document.Internals.AddObject(dictionary);
        return dictionary;
    }

    private static PdfArray Items(PdfDocument document, params PdfItem[] items)
    {
        var array = new PdfArray(document);
        foreach (var item in items)
            array.Elements.Add(item);
        return array;
    }

    private static PdfArray Dest(PdfDocument document, int page, string kind = "/Fit", params PdfItem[] values) =>
        Items(document, [document.Pages[page].Reference!, new PdfName(kind), .. values]);

    private static PdfDictionary Link(PdfDocument document, int page, PdfItem? destination = null, PdfDictionary? action = null)
    {
        var link = Object(document);
        link.Elements["/Type"] = new PdfName("/Annot");
        link.Elements["/Subtype"] = new PdfName("/Link");
        link.Elements["/Rect"] = Items(document, new PdfInteger(10), new PdfInteger(10), new PdfInteger(30), new PdfInteger(30));
        if (destination is not null)
            link.Elements["/Dest"] = destination;
        if (action is not null)
            link.Elements["/A"] = action;
        if (V(document.Pages[page].Elements["/Annots"]) is not PdfArray annotations)
        {
            annotations = new PdfArray(document);
            document.Pages[page].Elements["/Annots"] = annotations;
        }
        annotations.Elements.Add(link.Reference!);
        return link;
    }

    private static PdfDictionary Action(PdfDocument document, string kind, PdfItem destination)
    {
        var action = new PdfDictionary(document);
        action.Elements["/S"] = new PdfName(kind);
        action.Elements[kind == "/URI" ? "/URI" : "/D"] = destination;
        return action;
    }

    private static PdfDictionary Bookmark(PdfDocument document, string title, PdfItem? destination = null)
    {
        var item = Object(document);
        item.Elements["/Title"] = new PdfString(title);
        if (destination is not null)
            item.Elements["/Dest"] = destination;
        return item;
    }

    private static void Children(PdfDictionary parent, params PdfDictionary[] children)
    {
        if (children.Length == 0)
            return;
        parent.Elements["/First"] = children[0].Reference;
        parent.Elements["/Last"] = children[^1].Reference;
        for (var i = 0; i < children.Length; i++)
        {
            children[i].Elements["/Parent"] = parent.Reference;
            if (i > 0)
                children[i].Elements["/Prev"] = children[i - 1].Reference;
            if (i + 1 < children.Length)
                children[i].Elements["/Next"] = children[i + 1].Reference;
        }
    }

    private static PdfDictionary Widget(PdfDocument document, int page, PdfDictionary? parent = null)
    {
        var widget = Object(document);
        widget.Elements["/Type"] = new PdfName("/Annot");
        widget.Elements["/Subtype"] = new PdfName("/Widget");
        widget.Elements["/Rect"] = Items(document, new PdfInteger(40), new PdfInteger(40), new PdfInteger(100), new PdfInteger(60));
        widget.Elements["/P"] = document.Pages[page].Reference;
        if (parent is not null)
            widget.Elements["/Parent"] = parent.Reference;
        if (V(document.Pages[page].Elements["/Annots"]) is not PdfArray annotations)
        {
            annotations = new PdfArray(document);
            document.Pages[page].Elements["/Annots"] = annotations;
        }
        annotations.Elements.Add(widget.Reference!);
        var appearance = Object(document);
        appearance.Elements["/Type"] = new PdfName("/XObject");
        appearance.Elements["/Subtype"] = new PdfName("/Form");
        appearance.Elements["/BBox"] = Items(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(60), new PdfInteger(20));
        appearance.CreateStream("0 0 1 rg 0 0 60 20 re f"u8.ToArray());
        var ap = new PdfDictionary(document);
        ap.Elements["/N"] = appearance.Reference;
        widget.Elements["/AP"] = ap;
        return widget;
    }

    private static PdfDictionary Form(PdfDocument document)
    {
        var form = Object(document);
        form.Elements["/Fields"] = new PdfArray(document);
        document.Internals.Catalog.Elements["/AcroForm"] = form.Reference;
        return form;
    }

    private static PdfDictionary Field(PdfDocument document, PdfDictionary form, string name, string value, bool multiple = false)
    {
        var field = multiple ? Object(document) : Widget(document, 0);
        field.Elements["/T"] = new PdfString(name);
        field.Elements["/FT"] = new PdfName("/Tx");
        field.Elements["/V"] = new PdfString(value);
        if (multiple)
            field.Elements["/Kids"] = Items(document, Widget(document, 0, field).Reference!, Widget(document, 1, field).Reference!);
        A(form.Elements["/Fields"]).Elements.Add(field.Reference!);
        return field;
    }

    private static PdfDictionary Resource(PdfDocument document, string? font = null)
    {
        var dictionary = Object(document);
        if (font is not null)
        {
            dictionary.Elements["/Type"] = new PdfName("/Font");
            dictionary.Elements["/Subtype"] = new PdfName("/Type1");
            dictionary.Elements["/BaseFont"] = new PdfName("/" + font);
        }
        return dictionary;
    }

    private static string Make(string folder, string name, int index = 0, bool? need = null, bool collision = false)
    {
        var path = Path.Combine(folder, name + ".pdf");
        using var document = new PdfDocument();
        document.AddPage();
        document.AddPage().Rotate = 90;
        for (var p = 0; p < 2; p++)
        {
            var content = Object(document);
            content.CreateStream(Encoding.ASCII.GetBytes($"0 0 {index + p + 1} 10 re f"));
            document.Pages[p].Elements["/Contents"] = content.Reference;
        }
        var pairs = Items(document, new PdfString("intro"), Dest(document, 1, "/FitH", new PdfInteger(123)));
        var tree = new PdfDictionary(document);
        tree.Elements["/Names"] = pairs;
        var names = new PdfDictionary(document);
        names.Elements["/Dests"] = tree;
        document.Internals.Catalog.Elements["/Names"] = names;
        var old = new PdfDictionary(document);
        old.Elements["/old"] = Dest(document, 0, "/FitR", new PdfInteger(1), new PdfInteger(2), new PdfInteger(30), new PdfInteger(40));
        document.Internals.Catalog.Elements["/Dests"] = old;
        var xyz = Dest(document, 1, "/XYZ", new PdfInteger(10), PdfNull.Value, new PdfReal(1.5));
        var root = Object(document);
        root.Elements["/Type"] = new PdfName("/Outlines");
        var top = Bookmark(document, $"source-{index}", xyz);
        top.Elements["/C"] = Items(document, new PdfReal(0.2), new PdfInteger(0), new PdfInteger(1));
        top.Elements["/F"] = new PdfInteger(3);
        top.Elements["/Count"] = new PdfInteger(99);
        var child = Bookmark(document, $"child-{index}");
        child.Elements["/A"] = Action(document, "/GoTo", new PdfString("intro"));
        Children(top, child);
        Children(root, top);
        document.Internals.Catalog.Elements["/Outlines"] = root.Reference;
        Link(document, 0, xyz);
        Link(document, 0, action: Action(document, "/GoTo", old.Elements["/old"]!));
        Link(document, 0, new PdfString("intro"));
        Link(document, 0, new PdfName("/old"));
        Link(document, 0, action: Action(document, "/URI", new PdfString("https://example.org/")));
        Link(document, 0, Dest(document, 0));
        var form = Form(document);
        form.Elements["/DA"] = new PdfString($"/F#31 {10 + index * 4} Tf % /F1 stays in a comment\n/CS1 cs /CS1 CS /P1 scn /P1 SCN /GS1 gs /DeviceRGB cs {(index == 0 ? "1 0 0" : "0 0 1")} rg (/F1 /CS1) Tj");
        form.Elements["/Q"] = new PdfInteger(index == 0 ? 0 : 2);
        if (need is { } needValue)
            form.Elements["/NeedAppearances"] = new PdfBoolean(needValue);
        Field(document, form, "name", index == 0 ? "first" : "second", multiple: true);
        var unique = Field(document, form, $"unique-{index}", "unique");
        unique.Elements["/FT"] = new PdfName("/Btn");
        unique.Elements["/V"] = new PdfName("/Yes");
        unique.Elements["/AS"] = new PdfName("/Yes");
        var appearances = D(unique.Elements["/AP"]);
        var off = Object(document);
        off.Elements["/Type"] = new PdfName("/XObject");
        off.Elements["/Subtype"] = new PdfName("/Form");
        off.Elements["/BBox"] = Items(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(60), new PdfInteger(20));
        off.CreateStream([]);
        var checks = new PdfDictionary(document);
        checks.Elements["/Yes"] = appearances.Elements["/N"];
        checks.Elements["/Off"] = off.Reference;
        appearances.Elements["/N"] = checks;
        var alternate = Field(document, form, $"alternate-{index}", "alt");
        alternate.Elements["/DA"] = new PdfString("/F1_2 9 Tf 0 g");
        if (collision)
            Field(document, form, "2", "reserved");
        var resources = new PdfDictionary(document);
        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = Resource(document, index == 0 ? "Helvetica" : "Courier").Reference;
        fonts.Elements["/F1_2"] = Resource(document, index == 0 ? "Times-Roman" : "Times-Bold").Reference;
        resources.Elements["/Font"] = fonts;
        var spaces = new PdfDictionary(document);
        spaces.Elements["/CS1"] = new PdfName(index == 0 ? "/DeviceRGB" : "/DeviceGray");
        resources.Elements["/ColorSpace"] = spaces;
        var patterns = new PdfDictionary(document);
        var pattern = Resource(document);
        pattern.Elements["/Marker"] = new PdfInteger(index);
        patterns.Elements["/P1"] = pattern.Reference;
        resources.Elements["/Pattern"] = patterns;
        var states = new PdfDictionary(document);
        var state = Resource(document);
        state.Elements["/ca"] = new PdfReal(index == 0 ? 0.5 : 0.8);
        states.Elements["/GS1"] = state.Reference;
        resources.Elements["/ExtGState"] = states;
        form.Elements["/DR"] = resources;
        if (index == 1)
        {
            var direct = new PdfDictionary(document);
            foreach (var entry in form.Elements)
                direct.Elements[entry.Key] = entry.Value;
            document.Internals.Catalog.Elements["/AcroForm"] = direct;
        }
        document.Save(path);
        return path;
    }

    private string Merge(PdfMergeStructure.Limits? limits = null, bool collision = false)
    {
        var folder = fx.NewFolder();
        var inputs = new[] { Make(folder, "a", 0), Make(folder, "b", 1, true, collision) };
        var path = Path.Combine(folder, "merged.pdf");
        MergeFiles(inputs, path, TestContext.Current.CancellationToken, limits);
        return path;
    }

    private static void MergeFiles(IReadOnlyList<string> inputs, string output, CancellationToken ct,
        PdfMergeStructure.Limits? limits = null) =>
        PdfSharpConverter.Merge(inputs.Select(path => new PdfMergeInput(path, Path.GetFileName(path))).ToArray(),
            output, false, null, ct, limits);

    private string MergeSources(IReadOnlyList<PdfMergeInput> inputs, bool enabled = true, PdfMergeStructure.Limits? limits = null)
    {
        var output = Path.Combine(fx.NewFolder(), "merged.pdf");
        PdfSharpConverter.Merge(inputs, output, enabled, null, TestContext.Current.CancellationToken, limits);
        return output;
    }

    private static List<PdfDictionary> Siblings(PdfDictionary parent)
    {
        var children = new List<PdfDictionary>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var next = parent.Elements["/First"];
        while (next is not null)
        {
            var child = D(next);
            Assert.True(visited.Add(child));
            Assert.Same(parent, D(child.Elements["/Parent"]));
            if (children.Count == 0)
                Assert.False(child.Elements.ContainsKey("/Prev"));
            else
                Assert.Same(children[^1], D(child.Elements["/Prev"]));
            children.Add(child);
            next = child.Elements["/Next"];
        }
        if (children.Count > 0)
            Assert.Same(children[^1], D(parent.Elements["/Last"]));
        return children;
    }

    private static void AssertSourceTree(PdfDocument document, string secondName = "b.pdf")
    {
        var root = D(document.Internals.Catalog.Elements["/Outlines"]);
        var parents = Siblings(root);
        Assert.Equal(new[] { "a.pdf", secondName }, parents.Select(p => S(p.Elements["/Title"])));
        // Source 0 is open, source 1 is closed; both file parents are open.
        Assert.Equal(5, Assert.IsType<PdfInteger>(root.Elements["/Count"]).Value);
        for (var i = 0; i < 2; i++)
        {
            var parent = parents[i];
            View(document, A(parent.Elements["/Dest"]), i * 2, "/Fit");
            Assert.Equal(i == 0 ? 2 : 1, Assert.IsType<PdfInteger>(parent.Elements["/Count"]).Value);
            var top = Assert.Single(Siblings(parent));
            var child = Assert.Single(Siblings(top));
            Assert.Empty(Siblings(child));
            Assert.Equal($"source-{i}", S(top.Elements["/Title"]));
            Assert.Equal($"child-{i}", S(child.Elements["/Title"]));
            Assert.Equal(i == 0 ? 1 : -1, Assert.IsType<PdfInteger>(top.Elements["/Count"]).Value);
            Assert.False(child.Elements.ContainsKey("/Count"));
            Assert.Equal(new[] { "0.2", "0", "1" }, A(top.Elements["/C"]).Elements.Select(v => v.ToString()));
            Assert.Equal(3, Assert.IsType<PdfInteger>(top.Elements["/F"]).Value);
            View(document, A(top.Elements["/Dest"]), i * 2 + 1, "/XYZ", "10", "null", "1.5");
            View(document, A(child.Elements["/Dest"]), i * 2 + 1, "/FitH", "123");
        }
        Assert.Equal(6, OutlineItems(document).Count);
    }

    private string ClosedSource(string folder)
    {
        var source = Make(folder, "b", 1, true);
        using var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        D(D(document.Internals.Catalog.Elements["/Outlines"]).Elements["/First"]).Elements["/Count"] = new PdfInteger(-1);
        var path = Path.Combine(folder, "closed.pdf");
        document.Save(path);
        return path;
    }

    [Fact]
    public void Source_bookmarks_wrap_each_sources_outline_and_keep_its_view_and_style()
    {
        var folder = fx.NewFolder();
        using var result = PdfReader.Open(MergeSources(
            [new(Make(folder, "a"), "a.pdf"), new(ClosedSource(folder), "b.pdf")]), PdfDocumentOpenMode.Import);
        Assert.Equal(4, result.PageCount);
        AssertSourceTree(result);
        AssertLinks(result);
        AssertForms(result);
    }

    [Fact]
    public void Source_bookmarks_keep_duplicate_names_as_separate_entries()
    {
        var first = Make(fx.NewFolder(), "report");
        var second = Make(fx.NewFolder(), "report", 1);
        using var result = PdfReader.Open(MergeSources(
            [new(first, Path.GetFileName(first)), new(second, Path.GetFileName(second)), new(first, Path.GetFileName(first))]),
            PdfDocumentOpenMode.Import);
        var parents = Siblings(D(result.Internals.Catalog.Elements["/Outlines"]));
        Assert.Equal(new[] { "report.pdf", "report.pdf", "report.pdf" }, parents.Select(p => S(p.Elements["/Title"])));
        Assert.Equal(6, result.PageCount);
        for (var i = 0; i < parents.Count; i++)
            View(result, A(parents[i].Elements["/Dest"]), i * 2, "/Fit");
    }

    [Fact]
    public void Source_bookmarks_point_at_the_first_imported_page_occurrence()
    {
        var source = Source(pdf =>
        {
            var pages = D(pdf.Internals.Catalog.Elements["/Pages"]);
            var kids = A(pages.Elements["/Kids"]);
            kids.Elements[1] = kids.Elements[0];
        });
        using (var input = PdfReader.Open(source, PdfDocumentOpenMode.Import))
            Assert.Equal(input.Pages[0].Reference!.ObjectID, input.Pages[1].Reference!.ObjectID);
        using var result = PdfReader.Open(MergeSources([new(source, "repeat.pdf")]), PdfDocumentOpenMode.Import);
        Assert.Equal(2, result.PageCount);
        Assert.NotEqual(result.Pages[0].Reference!.ObjectID, result.Pages[1].Reference!.ObjectID);
        View(result, A(Assert.Single(OutlineItems(result)).Elements["/Dest"]), 0, "/Fit");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Source_bookmarks_create_leaf_entries_for_sources_without_outlines(int count)
    {
        var inputs = Enumerable.Range(0, count).Select(i => new PdfMergeInput(Source(_ => { }), $"plain-{i}.pdf")).ToArray();
        using var result = PdfReader.Open(MergeSources(inputs), PdfDocumentOpenMode.Import);
        Assert.Equal(count * 2, result.PageCount);
        var parents = Siblings(D(result.Internals.Catalog.Elements["/Outlines"]));
        Assert.Equal(inputs.Select(i => i.SourceName), parents.Select(p => S(p.Elements["/Title"])));
        for (var i = 0; i < count; i++)
        {
            Assert.Empty(Siblings(parents[i]));
            Assert.False(parents[i].Elements.ContainsKey("/Count"));
            View(result, A(parents[i].Elements["/Dest"]), i * 2, "/Fit");
        }
    }

    [Fact]
    public void Source_bookmarks_off_keeps_the_existing_flat_structure()
    {
        var folder = fx.NewFolder();
        using (var result = PdfReader.Open(MergeSources(
            [new(Make(folder, "a"), "a.pdf"), new(Make(folder, "b", 1), "b.pdf")], false), PdfDocumentOpenMode.Import))
            AssertBookmarks(result);
        using var plain = PdfReader.Open(MergeSources([new(Source(_ => { }), "plain.pdf")], false), PdfDocumentOpenMode.Import);
        Assert.Equal(2, plain.PageCount);
        Assert.False(plain.Internals.Catalog.Elements.ContainsKey("/Outlines"));
    }

    [Fact]
    public void Source_bookmarks_skip_zero_page_sources_without_losing_their_neighbor()
    {
        var empty = EmptyPdf(fx.NewFolder());
        using (var input = PdfReader.Open(empty, PdfDocumentOpenMode.Import))
            Assert.Equal(0, input.PageCount);
        using var result = PdfReader.Open(MergeSources(
            [new(empty, "empty.pdf"), new(Source(pdf => Children(OutlineRoot(pdf), Bookmark(pdf, "healthy", Dest(pdf, 1)))), "healthy.pdf")]),
            PdfDocumentOpenMode.Import);
        Assert.Equal(2, result.PageCount);
        var parent = Assert.Single(Siblings(D(result.Internals.Catalog.Elements["/Outlines"])));
        Assert.Equal("healthy.pdf", S(parent.Elements["/Title"]));
        View(result, A(parent.Elements["/Dest"]), 0, "/Fit");
        View(result, A(Assert.Single(Siblings(parent)).Elements["/Dest"]), 1, "/Fit");
    }

    [Fact]
    public void Source_bookmarks_titles_keep_unicode_and_replace_display_controls()
    {
        const string kept = "보고서 e\u0301 👩\u200D💻 (final) \\";
        const string controls = "\0\t\r\n\u007F\u0085\u061C\u200E\u200F\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069";
        var source = Source(_ => { });
        var name = kept + controls + "\uD800x\uDC00.pdf";
        using var result = PdfReader.Open(MergeSources([new(source, name)]), PdfDocumentOpenMode.Import);
        var title = Assert.Single(OutlineItems(result)).Elements["/Title"];
        Assert.Equal(kept + new string(' ', controls.Length) + "\uFFFDx\uFFFD.pdf", S(title));
        Assert.DoesNotContain(Path.GetDirectoryName(source)!, S(title), StringComparison.Ordinal);
        Assert.Equal(PdfStringEncoding.Unicode, Assert.IsType<PdfString>(V(title)).Encoding);
    }

    [Theory]
    [InlineData(1023, false)]
    [InlineData(1024, false)]
    [InlineData(1025, false)]
    [InlineData(1023, true)]
    [InlineData(1024, true)]
    [InlineData(1025, true)]
    public void Source_bookmarks_titles_stop_at_the_utf16_limit_without_splitting_a_pair(int length, bool pair)
    {
        var name = pair ? new string('a', Math.Min(length - 2, 1022)) + "📄" + new string('z', Math.Max(0, length - 1024)) : new string('a', length);
        Assert.Equal(length, name.Length);
        var expected = name.Length <= 1024 ? name : new string('a', pair ? 1022 : 1023) + "\u2026";
        using var result = PdfReader.Open(MergeSources([new(Source(_ => { }), name)]), PdfDocumentOpenMode.Import);
        var title = S(Assert.Single(OutlineItems(result)).Elements["/Title"]);
        Assert.Equal(expected, title);
        Assert.InRange(title.Length, 1, 1024);
        Assert.DoesNotContain('\uFFFD', title);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void Source_bookmarks_count_the_parent_in_item_and_depth_limits(int limit, bool depth)
    {
        var source = Source(pdf =>
        {
            var top = Bookmark(pdf, "top", Dest(pdf, 0));
            var child = Bookmark(pdf, "child", Dest(pdf, 1));
            Children(OutlineRoot(pdf), top);
            Children(top, child);
        });
        using var result = PdfReader.Open(MergeSources([new(source, "a.pdf"), new(source, "b.pdf")], limits:
            depth ? new() { OutlineDepth = limit } : new() { OutlineItems = limit }), PdfDocumentOpenMode.Import);
        var items = OutlineItems(result);
        Assert.Equal(Math.Min(limit, 3) * 2, items.Count);
        Assert.Equal(4, result.PageCount);
        if (limit == 0)
            Assert.False(result.Internals.Catalog.Elements.ContainsKey("/Outlines"));
        else
        {
            var parents = Siblings(D(result.Internals.Catalog.Elements["/Outlines"]));
            Assert.Equal(new[] { "a.pdf", "b.pdf" }, parents.Select(p => S(p.Elements["/Title"])));
            for (var i = 0; i < 2; i++)
            {
                View(result, A(parents[i].Elements["/Dest"]), i * 2, "/Fit");
                var branch = parents[i];
                for (var level = 1; level < Math.Min(limit, 3); level++)
                    branch = Assert.Single(Siblings(branch));
                Assert.Empty(Siblings(branch));
            }
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Source_bookmarks_reserve_unicode_title_bytes_before_destination_names(int bytes)
    {
        var source = Source(pdf =>
        {
            NameTree(pdf).Elements["/Names"] = Items(pdf, new PdfString("n"), Dest(pdf, 1));
            Children(OutlineRoot(pdf), Bookmark(pdf, "x", Dest(pdf, 0)));
        });
        using var result = PdfReader.Open(MergeSources([new(source, "p"), new(source, "q")],
            limits: new() { StringBytes = bytes }), PdfDocumentOpenMode.Import);
        var items = OutlineItems(result);
        Assert.Equal(bytes switch { 3 => 0, 4 => 2, 5 => 3, 6 => 4, _ => 3 }, items.Count);
        var names = result.Internals.Catalog.Elements.ContainsKey("/Names") ? Destinations(result) : new Dictionary<string, PdfArray>();
        Assert.Equal(bytes < 4 ? new[] { "n", "n-2" } : bytes == 4 ? [] : bytes < 7 ? ["n"] : ["n", "n-2"], names.Keys);
        if (bytes >= 4)
        {
            var parents = Siblings(D(result.Internals.Catalog.Elements["/Outlines"]));
            Assert.Equal(new[] { "p", "q" }, parents.Select(p => S(p.Elements["/Title"])));
            View(result, A(parents[1].Elements["/Dest"]), 2, "/Fit");
        }
    }

    [Theory]
    [InlineData("items")]
    [InlineData("depth")]
    [InlineData("strings")]
    public void Source_bookmarks_do_not_fall_back_to_flat_outlines_when_parent_reservation_fails(string kind)
    {
        var source = Source(pdf =>
        {
            Children(OutlineRoot(pdf), Bookmark(pdf, "x", Dest(pdf, 0)));
            Link(pdf, 0, Dest(pdf, 1));
            Field(pdf, Form(pdf), "f", "v");
        });
        var limits = kind switch
        {
            "items" => new PdfMergeStructure.Limits { OutlineItems = 0 },
            "depth" => new PdfMergeStructure.Limits { OutlineDepth = 0 },
            _ => new PdfMergeStructure.Limits { StringBytes = 16 },
        };
        using var result = PdfReader.Open(MergeSources(
            [new(source, "parent-too-long.pdf"), new(source, "another-long-parent.pdf")], limits: limits), PdfDocumentOpenMode.Import);
        Assert.Equal(4, result.PageCount);
        Assert.False(result.Internals.Catalog.Elements.ContainsKey("/Outlines"));
        for (var i = 0; i < 2; i++)
            View(result, A(Assert.Single(Links(result, i * 2)).Elements["/Dest"]), i * 2 + 1, "/Fit");
        var fields = Fields(result);
        Assert.Equal(new[] { "f", "2", "2.f" }, fields.Keys);
        Assert.Equal("v", S(fields["f"].Elements["/V"]));
        Assert.Equal("v", S(fields["2.f"].Elements["/V"]));
        Assert.Same(fields["2"], D(fields["2.f"].Elements["/Parent"]));
    }

    [Fact]
    public void Source_bookmarks_bound_cyclic_and_deep_outlines_with_a_healthy_neighbor()
    {
        var deep = Source(pdf =>
        {
            var parent = OutlineRoot(pdf);
            for (var i = 0; i < 70; i++)
            {
                var child = Bookmark(pdf, $"deep-{i}", Dest(pdf, 0));
                Children(parent, child);
                parent = child;
            }
        });
        var cyclic = Source(pdf =>
        {
            var root = OutlineRoot(pdf);
            var entries = Enumerable.Range(0, 256).Select(i => Bookmark(pdf, $"sibling-{i}", Dest(pdf, 1))).ToArray();
            entries[1].Elements["/Dest"] = Items(pdf, new PdfInteger(-1), new PdfName("/Fit"));
            Children(root, entries);
            entries[^1].Elements["/Next"] = entries[0].Reference;
        });
        var clock = Stopwatch.StartNew();
        var output = MergeSources([new(deep, "deep.pdf"), new(cyclic, "cyclic.pdf")]);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(4, result.PageCount);
        Assert.Equal(321, OutlineItems(result).Count);
        var parents = Siblings(D(result.Internals.Catalog.Elements["/Outlines"]));
        Assert.Equal(new[] { "deep.pdf", "cyclic.pdf" }, parents.Select(p => S(p.Elements["/Title"])));
        View(result, A(parents[0].Elements["/Dest"]), 0, "/Fit");
        View(result, A(parents[1].Elements["/Dest"]), 2, "/Fit");
        var branch = parents[0];
        for (var depth = 1; depth < 64; depth++)
            branch = Assert.Single(Siblings(branch));
        Assert.Empty(Siblings(branch));
        var siblings = Siblings(parents[1]);
        Assert.Equal(256, siblings.Count);
        Assert.False(siblings[1].Elements.ContainsKey("/Dest"));
        View(result, A(siblings[^1].Elements["/Dest"]), 3, "/Fit");
    }

    [Fact]
    public void Source_bookmarks_observe_cancellation_before_carrying_structure()
    {
        var folder = fx.NewFolder();
        var source = Make(folder, "a");
        var output = Path.Combine(folder, "cancelled.pdf");
        using var cts = new CancellationTokenSource();
        TimeSpan? cancelled = null;
        var clock = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => PdfSharpConverter.Merge([new(source, "a.pdf")], output, true, null,
            cts.Token, new() { BeforeCarry = () => { cancelled = clock.Elapsed; cts.Cancel(); } }));
        Assert.NotNull(cancelled);
        Assert.True(clock.Elapsed - cancelled.Value < TimeSpan.FromSeconds(1));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Source_bookmarks_survive_pdf_finishing_with_links_and_forms()
    {
        var folder = fx.NewFolder();
        var job = await fx.ConvertAsync([Make(folder, "a"), ClosedSource(folder)], new Preset
        {
            TargetFormat = "pdf",
            Pdf = { MergeIntoSingle = true, SourceBookmarks = true, Watermark = "DRAFT", PageNumbers = true, Password = "source-test" },
        });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var path = Assert.Single(job.Outputs);
        using (var result = PdfReader.Open(path, "source-test", PdfDocumentOpenMode.Import))
        {
            AssertSourceTree(result, "closed.pdf");
            AssertLinks(result);
            AssertForms(result);
            AssertDefaults(result);
        }
        ChangeIndependently(path, "source-test");
    }

    private static List<PdfDictionary> OutlineItems(PdfDocument document)
    {
        var result = new List<PdfDictionary>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem>();
        if (V(document.Internals.Catalog.Elements["/Outlines"]) is PdfDictionary root && root.Elements["/First"] is { } first)
            stack.Push(first);
        while (stack.TryPop(out var item))
        {
            var dictionary = D(item);
            Assert.True(visited.Add(dictionary), "outline items must be reachable exactly once");
            result.Add(dictionary);
            if (dictionary.Elements["/Next"] is { } next)
                stack.Push(next);
            if (dictionary.Elements["/First"] is { } child)
                stack.Push(child);
        }
        return result;
    }

    private static Dictionary<string, PdfDictionary> Fields(PdfDocument document)
    {
        var result = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
        if (V(document.Internals.Catalog.Elements["/AcroForm"]) is not PdfDictionary form)
            return result;
        var stack = new Stack<(PdfItem Item, string Prefix)>();
        foreach (var item in A(form.Elements["/Fields"]).Elements.Reverse())
            stack.Push((item, ""));
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (stack.TryPop(out var entry))
        {
            var field = D(entry.Item);
            Assert.True(visited.Add(field), "fields and widgets must be reachable exactly once");
            var name = field.Elements["/T"] is { } t ? entry.Prefix + S(t) : entry.Prefix.TrimEnd('.');
            if (field.Elements.ContainsKey("/T"))
                result.Add(name, field);
            if (V(field.Elements["/Kids"]) is PdfArray kids)
                foreach (var kid in kids.Elements.Reverse())
                {
                    Assert.Same(field, D(D(kid).Elements["/Parent"]));
                    stack.Push((kid, name + "."));
                }
        }
        return result;
    }

    private static List<PdfDictionary> Links(PdfDocument document, int page) =>
        A(document.Pages[page].Elements["/Annots"]).Elements.Select(D)
            .Where(d => d.Elements["/Subtype"] is { } item && S(item) == "/Link").ToList();

    private static Dictionary<string, PdfArray> Destinations(PdfDocument document)
    {
        var names = A(D(D(document.Internals.Catalog.Elements["/Names"]).Elements["/Dests"]).Elements["/Names"]);
        var result = new Dictionary<string, PdfArray>(StringComparer.Ordinal);
        for (var i = 0; i < names.Elements.Count; i += 2)
            result.Add(Key(names.Elements[i]), A(names.Elements[i + 1]));
        return result;
    }

    private static string Key(PdfItem item)
    {
        var (value, encoding) = V(item) switch
        {
            PdfString text => (text.Value, text.Encoding),
            PdfStringObject text => (text.Value, text.Encoding),
            _ => throw new InvalidOperationException("Not a PDF string"),
        };
        return encoding == PdfStringEncoding.Unicode
            ? "\xfe\xff" + Encoding.Latin1.GetString(Encoding.BigEndianUnicode.GetBytes(value))
            : value;
    }

    private static PdfArray Resolved(PdfDocument document, PdfItem? item) =>
        V(item) is PdfString or PdfStringObject ? Destinations(document)[Key(item!)] : A(item);

    private static void View(PdfDocument document, PdfArray destination, int page, string kind, params string[] values)
    {
        Assert.Equal(document.Pages[page].Reference!.ObjectID, Assert.IsType<PdfReference>(destination.Elements[0]).ObjectID);
        Assert.Equal(kind, S(destination.Elements[1]));
        Assert.Equal(values, destination.Elements.Skip(2).Select(item => item.ToString()));
    }

    private static void AssertBookmarks(PdfDocument document)
    {
        var items = OutlineItems(document);
        Assert.Equal(new[] { "source-0", "child-0", "source-1", "child-1" }, items.Select(d => S(d.Elements["/Title"])));
        Assert.Equal(4, Assert.IsType<PdfInteger>(D(document.Internals.Catalog.Elements["/Outlines"]).Elements["/Count"]).Value);
        for (var source = 0; source < 2; source++)
        {
            var parent = items[source * 2];
            var child = items[source * 2 + 1];
            Assert.Same(parent, D(child.Elements["/Parent"]));
            Assert.Same(child, D(parent.Elements["/First"]));
            Assert.Equal(1, Assert.IsType<PdfInteger>(parent.Elements["/Count"]).Value);
            Assert.False(child.Elements.ContainsKey("/Count"));
            Assert.Equal(new[] { "0.2", "0", "1" }, A(parent.Elements["/C"]).Elements.Select(v => v.ToString()));
            Assert.Equal(3, Assert.IsType<PdfInteger>(parent.Elements["/F"]).Value);
            View(document, A(parent.Elements["/Dest"]), source * 2 + 1, "/XYZ", "10", "null", "1.5");
            View(document, A(child.Elements["/Dest"]), source * 2 + 1, "/FitH", "123");
        }
    }

    private static void AssertForms(PdfDocument document, string prefix = "2")
    {
        var fields = Fields(document);
        Assert.Equal("first", S(fields["name"].Elements["/V"]));
        Assert.Equal("second", S(fields[prefix + ".name"].Elements["/V"]));
        for (var source = 0; source < 2; source++)
        {
            var parent = fields[source == 0 ? "name" : prefix + ".name"];
            var widgets = A(parent.Elements["/Kids"]);
            Assert.Equal(2, widgets.Elements.Count);
            for (var p = 0; p < 2; p++)
            {
                var widget = D(widgets.Elements[p]);
                Assert.Same(parent, D(widget.Elements["/Parent"]));
                Assert.Contains(A(document.Pages[source * 2 + p].Elements["/Annots"]).Elements,
                    item => ReferenceEquals(V(item), widget));
                Assert.NotNull(D(widget.Elements["/AP"]).Elements["/N"]);
            }
            Assert.Equal("/Yes", S(fields[$"unique-{source}"].Elements["/V"]));
        }
        var reachable = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem>(A(D(document.Internals.Catalog.Elements["/AcroForm"]).Elements["/Fields"]).Elements);
        while (stack.TryPop(out var item))
        {
            var field = D(item);
            Assert.True(reachable.Add(field));
            if (V(field.Elements["/Kids"]) is PdfArray kids)
                foreach (var kid in kids.Elements)
                    stack.Push(kid);
        }
        foreach (var page in document.Pages)
            foreach (var widget in A(page.Elements["/Annots"]).Elements.Select(D).Where(d => S(d.Elements["/Subtype"]) == "/Widget"))
                Assert.Contains(widget, reachable);
    }

    private static void AssertLinks(PdfDocument document)
    {
        var names = Destinations(document);
        Assert.Contains("intro", names.Keys);
        Assert.Contains("intro-2", names.Keys);
        for (var source = 0; source < 2; source++)
        {
            var links = Links(document, source * 2);
            View(document, A(links[0].Elements["/Dest"]), source * 2 + 1, "/XYZ", "10", "null", "1.5");
            View(document, A(D(links[1].Elements["/A"]).Elements["/D"]), source * 2, "/FitR", "1", "2", "30", "40");
            Assert.Equal(source == 0 ? "intro" : "intro-2", S(links[2].Elements["/Dest"]));
            View(document, Resolved(document, links[2].Elements["/Dest"]), source * 2 + 1, "/FitH", "123");
            View(document, Resolved(document, links[3].Elements["/Dest"]), source * 2, "/FitR", "1", "2", "30", "40");
            Assert.Equal("/URI", S(D(links[4].Elements["/A"]).Elements["/S"]));
            Assert.Equal("https://example.org/", S(D(links[4].Elements["/A"]).Elements["/URI"]));
            View(document, A(links[5].Elements["/Dest"]), source * 2, "/Fit");
        }
    }

    private static PdfItem? Inherited(PdfDictionary field, string key)
    {
        while (true)
        {
            if (field.Elements.ContainsKey(key))
                return field.Elements[key];
            if (V(field.Elements["/Parent"]) is not PdfDictionary parent)
                return null;
            field = parent;
        }
    }

    private static void AssertDefaults(PdfDocument document)
    {
        var fields = Fields(document);
        var resources = D(D(document.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]);
        var fonts = D(resources.Elements["/Font"]);
        Assert.Equal(4, fonts.Elements.Count);
        for (var source = 0; source < 2; source++)
        {
            var field = fields[source == 0 ? "name" : "2.name"];
            var da = S(Inherited(field, "/DA"));
            var fontName = da.Split(' ')[0].Replace("#31", "1", StringComparison.Ordinal);
            Assert.Equal(source == 0 ? "/Helvetica" : "/Courier", S(D(fonts.Elements[fontName]).Elements["/BaseFont"]));
            Assert.Contains(source == 0 ? "10 Tf" : "14 Tf", da, StringComparison.Ordinal);
            Assert.Contains(source == 0 ? "1 0 0 rg" : "0 0 1 rg", da, StringComparison.Ordinal);
            Assert.Contains("% /F1 stays in a comment", da, StringComparison.Ordinal);
            Assert.Contains("(/F1 /CS1) Tj", da, StringComparison.Ordinal);
            Assert.Contains("/DeviceRGB cs", da, StringComparison.Ordinal);
            Assert.Equal(source == 0 ? 0 : 2, Assert.IsType<PdfInteger>(Inherited(field, "/Q")).Value);
            var tokens = da.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string Before(string operation) => tokens[System.Array.LastIndexOf(tokens, operation) - 1];
            var spaces = D(resources.Elements["/ColorSpace"]);
            Assert.Equal(source == 0 ? "/DeviceRGB" : "/DeviceGray", S(spaces.Elements[tokens[System.Array.IndexOf(tokens, "cs") - 1]]));
            Assert.Equal(source == 0 ? "/DeviceRGB" : "/DeviceGray", S(spaces.Elements[Before("CS")]));
            var pattern = D(D(resources.Elements["/Pattern"]).Elements[Before("scn")]);
            Assert.Equal(source, Assert.IsType<PdfInteger>(pattern.Elements["/Marker"]).Value);
            Assert.Same(pattern, D(D(resources.Elements["/Pattern"]).Elements[Before("SCN")]));
            var state = D(D(resources.Elements["/ExtGState"]).Elements[Before("gs")]);
            Assert.Equal(source == 0 ? 0.5 : 0.8, Assert.IsType<PdfReal>(state.Elements["/ca"]).Value);
            var alternate = S(fields[$"alternate-{source}"].Elements["/DA"]).Split(' ')[0];
            Assert.Equal(source == 0 ? "/Times-Roman" : "/Times-Bold", S(D(fonts.Elements[alternate]).Elements["/BaseFont"]));
        }
    }

    private static void ChangeIndependently(string path, string? password = null)
    {
        var edited = Path.Combine(Path.GetDirectoryName(path)!, "edited.pdf");
        using (var document = password is null ? PdfReader.Open(path, PdfDocumentOpenMode.Modify) : PdfReader.Open(path, password, PdfDocumentOpenMode.Modify))
        {
            var fields = Fields(document);
            fields["2.name"].Elements["/V"] = new PdfString("changed");
            document.Save(edited);
        }
        using var opened = password is null ? PdfReader.Open(edited, PdfDocumentOpenMode.Import) : PdfReader.Open(edited, password, PdfDocumentOpenMode.Import);
        var result = Fields(opened);
        Assert.Equal("first", S(result["name"].Elements["/V"]));
        Assert.Equal("changed", S(result["2.name"].Elements["/V"]));
        foreach (var name in new[] { "unique-0", "unique-1" })
        {
            Assert.Equal("/Yes", S(result[name].Elements["/V"]));
            Assert.Equal("/Yes", S(result[name].Elements["/AS"]));
        }
        foreach (var widget in A(result["2.name"].Elements["/Kids"]).Elements)
            Assert.Equal("changed", S(D(D(widget).Elements["/Parent"]).Elements["/V"]));
    }

    [Fact]
    public void Merge_keeps_each_sources_bookmarks_pointing_at_its_pages()
    {
        using var document = PdfReader.Open(Merge(), PdfDocumentOpenMode.Import);
        AssertBookmarks(document);
    }

    [Fact]
    public void Merge_keeps_form_fields_and_their_values()
    {
        var folder = fx.NewFolder();
        var inputs = new[] { Make(folder, "a"), Make(folder, "b", 1) };
        using (var second = PdfReader.Open(inputs[1], PdfDocumentOpenMode.Import))
            Assert.IsType<PdfDictionary>(second.Internals.Catalog.Elements["/AcroForm"]);
        var output = Path.Combine(folder, "merged.pdf");
        MergeFiles(inputs, output, TestContext.Current.CancellationToken);
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        AssertForms(document);
    }

    [Fact]
    public void Fields_with_the_same_name_get_the_file_position_as_a_prefix()
    {
        using (var document = PdfReader.Open(Merge(), PdfDocumentOpenMode.Import))
            AssertForms(document);
        using var collision = PdfReader.Open(Merge(collision: true), PdfDocumentOpenMode.Import);
        AssertForms(collision, "2_2");
        Assert.Equal("reserved", S(Fields(collision)["2"].Elements["/V"]));
    }

    [Fact]
    public void Links_and_named_destinations_point_into_the_merged_file()
    {
        using var document = PdfReader.Open(Merge(), PdfDocumentOpenMode.Import);
        AssertLinks(document);
    }

    [Fact]
    public void Merged_pages_are_the_pages_of_the_sources()
    {
        var folder = fx.NewFolder();
        var inputs = new[] { Make(folder, "a"), Make(folder, "b", 1) };
        var path = Path.Combine(folder, "merged.pdf");
        var baseline = Path.Combine(folder, "baseline.pdf");
        using (var old = new PdfDocument())
        {
            foreach (var input in inputs)
            {
                using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
                foreach (var page in source.Pages)
                    old.AddPage(page);
            }
            old.Save(baseline);
        }
        MergeFiles(inputs, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        using var original = PdfReader.Open(baseline, PdfDocumentOpenMode.Import);
        Assert.Equal(original.PageCount, result.PageCount);
        for (var i = 0; i < result.PageCount; i++)
        {
            Assert.Equal(original.Pages[i].MediaBox, result.Pages[i].MediaBox);
            Assert.Equal(original.Pages[i].Rotate, result.Pages[i].Rotate);
            Assert.Equal(original.Pages[i].Contents.CreateSingleContent().Stream.Value, result.Pages[i].Contents.CreateSingleContent().Stream.Value);
        }
        Assert.Equal(result.PageCount, result.Internals.GetAllObjects().OfType<PdfDictionary>().Count(d => d.Elements["/Type"] is { } type && S(type) == "/Page"));
    }

    [Fact]
    public async Task Merge_job_keeps_structure_through_watermark_page_numbers_and_password()
    {
        var folder = fx.NewFolder();
        var job = await fx.ConvertAsync([Make(folder, "a"), Make(folder, "b", 1, true)], new Preset
        {
            TargetFormat = "pdf",
            Pdf = { MergeIntoSingle = true, Watermark = "DRAFT", PageNumbers = true, Password = "merge-test" },
        });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var path = Assert.Single(job.Outputs);
        using (var document = PdfReader.Open(path, "merge-test", PdfDocumentOpenMode.Import))
        {
            AssertBookmarks(document);
            AssertForms(document);
            AssertLinks(document);
            AssertDefaults(document);
            Assert.True(Assert.IsType<PdfBoolean>(D(document.Internals.Catalog.Elements["/AcroForm"]).Elements["/NeedAppearances"]).Value);
        }
        ChangeIndependently(path, "merge-test");
    }

    [Fact]
    public void Sources_without_structure_merge_as_before()
    {
        var folder = fx.NewFolder();
        var source = Path.Combine(folder, "plain.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.Save(source);
        }
        var output = Path.Combine(folder, "out.pdf");
        var second = Source(document => document.Pages[0].Rotate = 90);
        MergeFiles([source, second], output, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(90, result.Pages[1].Rotate);
        foreach (var key in new[] { "/Outlines", "/Names", "/AcroForm" })
            Assert.False(result.Internals.Catalog.Elements.ContainsKey(key));
    }

    private string Source(Action<PdfDocument> build)
    {
        var path = Path.Combine(fx.NewFolder(), "source.pdf");
        using var document = new PdfDocument();
        document.AddPage();
        document.AddPage();
        build(document);
        document.Save(path);
        return path;
    }

    private static string MergeOne(string source, PdfMergeStructure.Limits? limits = null)
    {
        var path = Path.Combine(Path.GetDirectoryName(source)!, "out.pdf");
        MergeFiles([source], path, TestContext.Current.CancellationToken, limits);
        return path;
    }

    private static string EmptyPdf(string folder)
    {
        // PDFsharp cannot save a document without pages; write the empty page tree explicitly.
        var empty = Path.Combine(folder, "empty.pdf");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R /Outlines 5 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Fields [4 0 R] >>",
            "<< /FT /Tx /T (broken) /V (bad) >>",
            "<< /Type /Outlines /First 6 0 R /Last 6 0 R >>",
            "<< /Title (empty bookmark) /Parent 5 0 R >>",
        };
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(empty, pdf.ToString(), Encoding.ASCII);
        return empty;
    }

    [Theory]
    [InlineData("no-widget")]
    [InlineData("outside-page-tree")]
    [InlineData("kid-without-page")]
    [InlineData("direct-field")]
    [InlineData("direct-kid")]
    [InlineData("no-pages")]
    [InlineData("missing-resource-reference")]
    [InlineData("unpaged-kid-with-own-resources")]
    public void Unimported_fields_and_missing_resources_do_not_break_healthy_fields(string kind)
    {
        var source = Source(document =>
        {
            var form = Form(document);
            Field(document, form, "healthy", "kept");
            if (kind == "unpaged-kid-with-own-resources")
            {
                var root = Object(document);
                root.Elements["/T"] = new PdfString("parent");
                root.Elements["/FT"] = new PdfName("/Tx");
                root.Elements["/V"] = new PdfString("kept");
                var paged = Widget(document, 0, root);
                var unpaged = Object(document);
                unpaged.Elements["/Type"] = new PdfName("/Annot");
                unpaged.Elements["/Subtype"] = new PdfName("/Widget");
                unpaged.Elements["/Parent"] = root.Reference;
                unpaged.Elements["/DA"] = new PdfString("/KidFont 10 Tf");
                var local = new PdfDictionary(document);
                var fonts = new PdfDictionary(document);
                fonts.Elements["/KidFont"] = Resource(document, "Helvetica").Reference;
                local.Elements["/Font"] = fonts;
                unpaged.Elements["/DR"] = local;
                root.Elements["/Kids"] = Items(document, paged.Reference!, unpaged.Reference!);
                A(form.Elements["/Fields"]).Elements.Add(root.Reference!);
                return;
            }
            if (kind is "no-pages")
                return;
            if (kind is "missing-resource-reference")
            {
                var font = Resource(document, "Helvetica");
                font.Elements["/ToUnicode"] = new PdfLiteral("777777 0 R");
                // PDFsharp normalizes a missing dictionary value, but leaves a missing array entry as null.
                font.Elements["/MissingArray"] = Items(document, new PdfLiteral("777777 0 R"));
                var fonts = new PdfDictionary(document);
                fonts.Elements["/F1"] = font.Reference;
                var resources = new PdfDictionary(document);
                resources.Elements["/Font"] = fonts;
                form.Elements["/DR"] = resources;
                form.Elements["/DA"] = new PdfString("/F1 10 Tf");
                return;
            }
            var broken = kind == "direct-field" ? new PdfDictionary(document) : Object(document);
            broken.Elements["/T"] = new PdfString("broken");
            broken.Elements["/FT"] = new PdfName("/Tx");
            broken.Elements["/V"] = new PdfString("bad");
            A(form.Elements["/Fields"]).Elements.Add(broken.Reference is { } reference ? reference : broken);
            if (kind == "no-widget")
                return;
            if (kind == "outside-page-tree")
            {
                var page = document.AddPage();
                var widget = Widget(document, 2, broken);
                broken.Elements["/Kids"] = Items(document, widget.Reference!);
                document.Pages.Remove(page);
                return;
            }
            if (kind == "direct-field")
            {
                broken.Elements["/Type"] = new PdfName("/Annot");
                broken.Elements["/Subtype"] = new PdfName("/Widget");
                broken.Elements["/P"] = document.Pages[0].Reference;
                broken.Elements["/Rect"] = Items(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(20), new PdfInteger(20));
                A(document.Pages[0].Elements["/Annots"]).Elements.Add(broken);
                return;
            }
            var kid = kind == "direct-kid" ? new PdfDictionary(document) : Object(document);
            kid.Elements["/Type"] = new PdfName("/Annot");
            kid.Elements["/Subtype"] = new PdfName("/Widget");
            kid.Elements["/Parent"] = broken.Reference;
            kid.Elements["/Rect"] = Items(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(20), new PdfInteger(20));
            kid.Elements["/DA"] = new PdfString("/F1 10 Tf");
            kid.Elements["/DR"] = new PdfDictionary(document);
            broken.Elements["/Kids"] = kind == "direct-kid"
                ? Items(document, Widget(document, 0, broken).Reference!, kid)
                : Items(document, kid.Reference!);
        });
        var output = Path.Combine(Path.GetDirectoryName(source)!, "out.pdf");
        if (kind == "no-pages")
        {
            var empty = EmptyPdf(Path.GetDirectoryName(source)!);
            MergeFiles([empty, source], output, TestContext.Current.CancellationToken);
        }
        else
            MergeFiles([source], output, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, result.PageCount);
        var fields = Fields(result);
        Assert.Equal("kept", S(fields["healthy"].Elements["/V"]));
        if (kind == "unpaged-kid-with-own-resources")
            Assert.Equal("kept", S(fields["parent"].Elements["/V"]));
        if (kind is "no-widget" or "outside-page-tree" or "direct-field" or "direct-kid" or "no-pages")
            Assert.DoesNotContain("broken", fields.Keys);
        if (kind == "missing-resource-reference")
        {
            var font = D(D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]).Elements["/F1"]);
            Assert.IsType<PdfNull>(V(font.Elements["/ToUnicode"]));
            Assert.IsType<PdfNull>(V(A(font.Elements["/MissingArray"]).Elements[0]));
        }
        using var pig = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(2, pig.NumberOfPages);
        Assert.True(pig.TryGetForm(out var parsed));
        Assert.Contains(parsed.Fields, field =>
            field is UglyToad.PdfPig.AcroForms.Fields.AcroTextField { Value: "kept" });
    }

    [Fact]
    public void Page_backed_field_survives_an_unimported_kid_with_local_resources()
    {
        var sourcePath = Source(document =>
        {
            var form = Form(document);
            Field(document, form, "healthy", "kept");
            var root = Object(document);
            root.Elements["/T"] = new PdfString("parent");
            root.Elements["/FT"] = new PdfName("/Tx");
            root.Elements["/V"] = new PdfString("root value");
            var paged = Widget(document, 0, root);
            var unpaged = Object(document);
            unpaged.Elements["/Type"] = new PdfName("/Annot");
            unpaged.Elements["/Subtype"] = new PdfName("/Widget");
            unpaged.Elements["/Parent"] = root.Reference;
            unpaged.Elements["/DA"] = new PdfString("/KidFont 10 Tf");
            var fonts = new PdfDictionary(document);
            fonts.Elements["/KidFont"] = Resource(document, "Helvetica").Reference;
            var local = new PdfDictionary(document);
            local.Elements["/Font"] = fonts;
            unpaged.Elements["/DR"] = local;
            root.Elements["/Kids"] = Items(document, paged.Reference!, unpaged.Reference!);
            A(form.Elements["/Fields"]).Elements.Add(root.Reference!);
        });
        var outputPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "out.pdf");
        using (var source = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import))
        using (var output = new PdfDocument())
        {
            var form = D(source.Internals.Catalog.Elements["/AcroForm"]);
            var root = D(A(form.Elements["/Fields"]).Elements[1]);
            var kids = A(root.Elements["/Kids"]);
            var unpaged = D(kids.Elements[1]);
            var unpagedReference = kids.Elements[1];
            // Exclude the unpaged kid from PDFsharp's page-import closure, then restore the source tree.
            kids.Elements.RemoveAt(1);
            var pages = new Dictionary<PdfObjectID, PdfPage>();
            foreach (var page in source.Pages)
                pages[page.Reference!.ObjectID] = output.AddPage(page);
            kids.Elements.Add(unpagedReference);
            Assert.IsAssignableFrom<PdfDictionary>(output.Internals.MapExternalObject(root));
            Assert.Throws<KeyNotFoundException>(() => output.Internals.MapExternalObject(unpaged));

            var structure = new PdfMergeStructure(output, new());
            structure.Carry(source, pages, TestContext.Current.CancellationToken);
            structure.Write(TestContext.Current.CancellationToken);
            output.Save(outputPath);
        }
        using (var result = PdfReader.Open(outputPath, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(2, result.PageCount);
            var fields = Fields(result);
            Assert.Equal("kept", S(fields["healthy"].Elements["/V"]));
            var root = fields["parent"];
            Assert.Equal("root value", S(root.Elements["/V"]));
            var paged = D(Assert.Single(A(root.Elements["/Kids"]).Elements));
            Assert.Contains(A(result.Pages[0].Elements["/Annots"]).Elements, item => ReferenceEquals(V(item), paged));
        }
        using var pig = UglyToad.PdfPig.PdfDocument.Open(outputPath);
        Assert.Equal(2, pig.NumberOfPages);
        Assert.True(pig.TryGetForm(out var parsed));
        Assert.Contains(parsed.Fields, field => field is UglyToad.PdfPig.AcroForms.Fields.AcroTextField { Value: "kept" });
        Assert.Contains(parsed.Fields, field => field is UglyToad.PdfPig.AcroForms.Fields.AcroTextField { Value: "root value" });
    }

    private static PdfDictionary OutlineRoot(PdfDocument document)
    {
        var root = Object(document);
        root.Elements["/Type"] = new PdfName("/Outlines");
        document.Internals.Catalog.Elements["/Outlines"] = root.Reference;
        return root;
    }

    private static PdfDictionary NameTree(PdfDocument document)
    {
        var names = new PdfDictionary(document);
        var root = Object(document);
        names.Elements["/Dests"] = root.Reference;
        document.Internals.Catalog.Elements["/Names"] = names;
        return root;
    }

    [Theory]
    [InlineData("outline-next")]
    [InlineData("outline-first")]
    [InlineData("destination-count")]
    [InlineData("destination-value")]
    [InlineData("destination-object")]
    [InlineData("destination-integer")]
    [InlineData("name-cycle")]
    [InlineData("name-value")]
    [InlineData("field-cycle")]
    [InlineData("field-parent-self")]
    [InlineData("field-parent-other")]
    [InlineData("field-parent-null")]
    [InlineData("field-shared")]
    [InlineData("field-title")]
    [InlineData("null-outline")]
    [InlineData("null-field")]
    [InlineData("null-resource")]
    [InlineData("resource-value")]
    [InlineData("resource-cycle")]
    [InlineData("resource-shared")]
    [InlineData("signature")]
    public void Broken_items_are_dropped_and_their_neighbours_kept(string kind)
    {
        var source = Source(document =>
        {
            if (kind.StartsWith("outline", StringComparison.Ordinal) || kind == "null-outline" ||
                kind.StartsWith("destination", StringComparison.Ordinal))
            {
                var root = OutlineRoot(document);
                var before = Bookmark(document, "before", Dest(document, 0));
                var broken = Bookmark(document, "broken", Dest(document, 1));
                var after = Bookmark(document, "after", Dest(document, 1));
                Children(root, before, broken, after);
                switch (kind)
                {
                    case "outline-next": after.Elements["/Next"] = before.Reference; break;
                    case "outline-first": broken.Elements["/First"] = before.Reference; break;
                    case "null-outline": broken.Elements["/First"] = PdfNull.Value; break;
                    case "destination-count": broken.Elements["/Dest"] = Dest(document, 1, "/XYZ", new PdfInteger(1)); break;
                    case "destination-value": broken.Elements["/Dest"] = Dest(document, 1, "/FitH", new PdfString("bad")); break;
                    case "destination-object": A(broken.Elements["/Dest"]).Elements[0] = Resource(document).Reference!; break;
                    case "destination-integer": A(broken.Elements["/Dest"]).Elements[0] = new PdfInteger(0); break;
                }
                Link(document, 0, broken.Elements["/Dest"]);
            }
            else if (kind.StartsWith("name", StringComparison.Ordinal))
            {
                var root = NameTree(document);
                var child = Object(document);
                root.Elements["/Names"] = Items(document, new PdfString("before"), Dest(document, 0));
                child.Elements["/Names"] = Items(document, new PdfString("after"), Dest(document, 1),
                    new PdfString("broken"), kind == "name-value" ? new PdfInteger(42) : Dest(document, 0));
                root.Elements["/Kids"] = Items(document, child.Reference!);
                if (kind == "name-cycle")
                    child.Elements["/Kids"] = Items(document, root.Reference!);
            }
            else if (kind.StartsWith("resource", StringComparison.Ordinal) || kind == "null-resource")
            {
                var form = Form(document);
                Field(document, form, "before", "ok");
                Field(document, form, "after", "ok");
                var resources = new PdfDictionary(document);
                var fonts = new PdfDictionary(document);
                fonts.Elements["/Before"] = Resource(document, "Helvetica").Reference;
                var shared = Resource(document);
                if (kind is "null-resource" or "resource-value")
                    fonts.Elements["/Broken"] = kind == "null-resource" ? PdfNull.Value : new PdfInteger(42);
                else
                {
                    using var bytes = new MemoryStream();
                    using (var compressed = new ZLibStream(bytes, CompressionLevel.SmallestSize, leaveOpen: true))
                        compressed.Write("resource"u8);
                    shared.CreateStream(bytes.ToArray());
                    shared.Elements["/Filter"] = new PdfName("/FlateDecode");
                    fonts.Elements["/Broken"] = shared.Reference;
                    shared.Elements["/A"] = shared.Reference;
                    shared.Elements["/B"] = shared.Reference;
                    if (kind == "resource-shared")
                        fonts.Elements["/Again"] = shared.Reference;
                }
                fonts.Elements["/After"] = Resource(document, "Courier").Reference;
                resources.Elements["/Font"] = fonts;
                form.Elements["/DR"] = resources;
            }
            else
            {
                var form = Form(document);
                Field(document, form, "before", "ok");
                var broken = Field(document, form, "broken", "bad", multiple: true);
                var widget = D(A(broken.Elements["/Kids"]).Elements[0]);
                var after = Field(document, form, "after", "ok");
                switch (kind)
                {
                    case "field-cycle": A(broken.Elements["/Kids"]).Elements.Add(broken.Reference!); break;
                    case "field-parent-self": broken.Elements["/Parent"] = broken.Reference; break;
                    case "field-parent-other": widget.Elements["/Parent"] = after.Reference; break;
                    case "field-parent-null": widget.Elements["/Parent"] = PdfNull.Value; break;
                    case "field-shared":
                        var branch = Object(document);
                        branch.Elements["/Parent"] = broken.Reference;
                        branch.Elements["/Kids"] = Items(document, widget.Reference!);
                        A(broken.Elements["/Kids"]).Elements.Add(branch.Reference!);
                        break;
                    case "field-title": broken.Elements["/T"] = new PdfInteger(7); break;
                    case "null-field": A(broken.Elements["/Kids"]).Elements.Add(PdfNull.Value); break;
                    case "signature": broken.Elements["/FT"] = new PdfName("/Sig"); break;
                }
            }
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        Assert.Equal(2, result.PageCount);
        if (kind.StartsWith("outline", StringComparison.Ordinal) || kind == "null-outline" ||
            kind.StartsWith("destination", StringComparison.Ordinal))
        {
            var items = OutlineItems(result);
            Assert.Equal(new[] { "before", "broken", "after" }, items.Select(d => S(d.Elements["/Title"])));
            if (kind == "destination-count")
            {
                View(result, A(items[1].Elements["/Dest"]), 1, "/XYZ", "1", "null", "null");
                View(result, A(Links(result, 0)[0].Elements["/Dest"]), 1, "/XYZ", "1", "null", "null");
            }
            else if (kind.StartsWith("destination", StringComparison.Ordinal))
            {
                Assert.False(items[1].Elements.ContainsKey("/Dest"));
                Assert.False(Links(result, 0)[0].Elements.ContainsKey("/Dest"));
            }
        }
        else if (kind.StartsWith("name", StringComparison.Ordinal))
        {
            var names = Destinations(result);
            Assert.Contains("before", names.Keys);
            Assert.Contains("after", names.Keys);
            Assert.Equal(kind == "name-value" ? 2 : 3, names.Count);
        }
        else
        {
            Assert.Equal(new[] { "before", "after" }, Fields(result).Keys);
            if (kind.StartsWith("resource", StringComparison.Ordinal) || kind == "null-resource")
            {
                var fonts = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]);
                Assert.Contains("/Before", fonts.Elements.Keys);
                Assert.Contains("/After", fonts.Elements.Keys);
                if (kind is "null-resource" or "resource-value")
                    Assert.False(fonts.Elements.ContainsKey("/Broken"));
                else
                {
                    var shared = D(fonts.Elements["/Broken"]);
                    Assert.Equal("/FlateDecode", S(shared.Elements["/Filter"]));
                    Assert.Equal("resource"u8.ToArray(), shared.Stream.UnfilteredValue);
                    Assert.Same(shared, D(shared.Elements["/A"]));
                    Assert.Same(shared, D(shared.Elements["/B"]));
                    if (kind == "resource-shared")
                        Assert.Same(shared, D(fonts.Elements["/Again"]));
                }
            }
        }
        Assert.Equal(result.PageCount, result.Internals.GetAllObjects().OfType<PdfDictionary>().Count(d => d.Elements["/Type"] is { } type && S(type) == "/Page"));
    }

    [Fact]
    public void Named_destinations_keep_their_namespace_and_bytes()
    {
        var source = Source(document =>
        {
            var tree = NameTree(document);
            tree.Elements["/Names"] = Items(document,
                new PdfString("intro"), Dest(document, 0),
                new PdfString("A", PdfStringEncoding.Unicode), Dest(document, 0),
                new PdfString("B", PdfStringEncoding.RawEncoding), Dest(document, 1),
                new PdfString("caf\xe9", PdfStringEncoding.RawEncoding), Dest(document, 1),
                new PdfString("한글", PdfStringEncoding.Unicode), Dest(document, 1));
            var old = new PdfDictionary(document);
            old.Elements["/intro"] = Dest(document, 1);
            old.Elements["/hash#23name"] = Dest(document, 0);
            document.Internals.Catalog.Elements["/Dests"] = old;
            Link(document, 0, new PdfString("intro"));
            Link(document, 0, new PdfName("/intro"));
            Link(document, 0, new PdfString("A", PdfStringEncoding.Unicode));
            Link(document, 0, new PdfString("caf\xe9", PdfStringEncoding.RawEncoding));
            Link(document, 0, new PdfName("/hash#23name"));
            var root = OutlineRoot(document);
            Children(root, Bookmark(document, "string", new PdfString("intro")),
                Bookmark(document, "name", new PdfName("/intro")));
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        var names = Destinations(result);
        Assert.Contains("caf\xe9", names.Keys);
        Assert.Contains("hash#23name", names.Keys);
        Assert.Contains("\xfe\xff\0A", names.Keys);
        Assert.Contains("\xfe\xff" + Encoding.Latin1.GetString(Encoding.BigEndianUnicode.GetBytes("한글")), names.Keys);
        Assert.Equal(names.Keys.Order(StringComparer.Ordinal), names.Keys);
        Assert.True(names.Keys.ToList().IndexOf("B") < names.Keys.ToList().IndexOf("\xfe\xff\0A"));
        var links = Links(result, 0);
        View(result, Resolved(result, links[0].Elements["/Dest"]), 0, "/Fit");
        View(result, Resolved(result, links[1].Elements["/Dest"]), 1, "/Fit");
        View(result, Resolved(result, links[2].Elements["/Dest"]), 0, "/Fit");
        View(result, Resolved(result, links[3].Elements["/Dest"]), 1, "/Fit");
        View(result, Resolved(result, links[4].Elements["/Dest"]), 0, "/Fit");
        var outlines = OutlineItems(result);
        View(result, A(outlines[0].Elements["/Dest"]), 0, "/Fit");
        View(result, A(outlines[1].Elements["/Dest"]), 1, "/Fit");
    }

    private string MergeAfterPrefix(string source, PdfMergeStructure.Limits? limits = null)
    {
        var prefix = Source(_ => { });
        var path = Path.Combine(Path.GetDirectoryName(source)!, "prefixed.pdf");
        MergeFiles([prefix, source], path, TestContext.Current.CancellationToken, limits);
        return path;
    }

    private static Dictionary<string, PdfItem> NameKeys(PdfDocument document)
    {
        var pairs = A(D(D(document.Internals.Catalog.Elements["/Names"]).Elements["/Dests"]).Elements["/Names"]);
        return Enumerable.Range(0, pairs.Elements.Count / 2)
            .ToDictionary(i => Key(pairs.Elements[2 * i]), i => pairs.Elements[2 * i], StringComparer.Ordinal);
    }

    private static void SharedName(PdfDocument document, string bytes, IEnumerable<PdfItem> uses)
    {
        var key = NameKeys(document)[bytes];
        Assert.Equal(bytes, Key(key));
        if (bytes.Length < 32)
        {
            Assert.IsType<PdfString>(key);
            Assert.All(uses, item =>
            {
                Assert.IsType<PdfString>(item);
                Assert.Equal(bytes, Key(item));
            });
            return;
        }
        var reference = Assert.IsType<PdfReference>(key);
        Assert.All(uses, item =>
        {
            Assert.Equal(reference.ObjectID, Assert.IsType<PdfReference>(item).ObjectID);
            Assert.Equal(bytes, Key(item));
        });
        Assert.Single(document.Internals.GetAllObjects().OfType<PdfStringObject>(), item => Key(item) == bytes);
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, false)]
    [InlineData(33, false)]
    [InlineData(128, false)]
    [InlineData(255, false)]
    [InlineData(256, false)]
    [InlineData(14, true)]
    [InlineData(15, true)]
    [InlineData(16, true)]
    public void Named_destinations_share_strings_at_the_byte_boundary(int length, bool unicode)
    {
        var text = new string('k', length);
        var encoding = unicode ? PdfStringEncoding.Unicode : PdfStringEncoding.RawEncoding;
        var bytes = Key(new PdfString(text, encoding));
        var source = Source(pdf =>
        {
            NameTree(pdf).Elements["/Names"] = Items(pdf, new PdfString(text, encoding),
                Dest(pdf, 1, "/XYZ", new PdfInteger(10), PdfNull.Value, new PdfReal(1.5)));
            for (var i = 0; i < 100; i++)
                if (i % 2 == 0)
                    Link(pdf, 0, new PdfString(text, encoding));
                else
                    Link(pdf, 0, action: Action(pdf, "/GoTo", new PdfString(text, encoding)));
        });
        using var result = PdfReader.Open(MergeAfterPrefix(source), PdfDocumentOpenMode.Import);
        Assert.Equal(4, result.PageCount);
        var links = Links(result, 2);
        Assert.Equal(100, links.Count);
        var uses = links.Select(link => link.Elements["/Dest"] ?? D(link.Elements["/A"]).Elements["/D"]!).ToArray();
        SharedName(result, bytes, uses);
        View(result, Assert.Single(Destinations(result)).Value, 3, "/XYZ", "10", "null", "1.5");
        Assert.All(uses, item => View(result, Resolved(result, item), 3, "/XYZ", "10", "null", "1.5"));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    public void Renamed_names_choose_sharing_after_the_suffix(int length)
    {
        var name = new string('k', length);
        string Make() => Source(pdf =>
        {
            NameTree(pdf).Elements["/Names"] = Items(pdf, new PdfString(name), Dest(pdf, 1));
            Link(pdf, 0, new PdfString(name));
            Link(pdf, 0, action: Action(pdf, "/GoTo", new PdfString(name)));
        });
        var first = Make();
        var path = Path.Combine(Path.GetDirectoryName(first)!, "collision.pdf");
        MergeFiles([first, Make()], path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(4, result.PageCount);
        Assert.Equal(new[] { name, name + "-2" }, Destinations(result).Keys);
        for (var i = 0; i < 2; i++)
        {
            var bytes = i == 0 ? name : name + "-2";
            var links = Links(result, i * 2);
            SharedName(result, bytes, [links[0].Elements["/Dest"]!, D(links[1].Elements["/A"]).Elements["/D"]!]);
            View(result, Destinations(result)[bytes], i * 2 + 1, "/Fit");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(255)]
    public void Shared_short_instances_keep_distinct_encodings(int length)
    {
        foreach (var narrow in new[] { false, true })
        {
            using var source = new PdfDocument();
            source.AddPage();
            source.AddPage();
            var shared = length == 0 ? string.Empty : new string('k', length);
            var raw = new PdfString(shared, PdfStringEncoding.RawEncoding);
            var unicode = new PdfString(shared, PdfStringEncoding.Unicode);
            Assert.Same(raw.Value, unicode.Value);
            NameTree(source).Elements["/Names"] = Items(source, raw, Dest(source, 0), unicode, Dest(source, 1),
                new PdfString("z"), Dest(source, 1));
            var rawLink = new PdfString(shared, PdfStringEncoding.RawEncoding);
            var unicodeLink = new PdfString(shared, PdfStringEncoding.Unicode);
            Assert.Same(raw.Value, rawLink.Value);
            Assert.Same(unicode.Value, unicodeLink.Value);
            Link(source, 0, rawLink);
            Link(source, 0, unicodeLink);
            Link(source, 0, new PdfString("z"));

            var folder = fx.NewFolder();
            using var output = new PdfDocument();
            output.AddPage();
            output.AddPage();
            var pages = new Dictionary<PdfObjectID, PdfPage>();
            for (var p = 0; p < 2; p++)
                pages[source.Pages[p].Reference!.ObjectID] = output.AddPage();
            // Carry's positional fallback rewrites these page-backed links without serializing the source.
            for (var i = 0; i < 3; i++)
                Link(output, 2);
            var structure = new PdfMergeStructure(output, new()
            {
                StringBytes = narrow ? length + 2 + 2 * length - 1 : 4096,
            });
            structure.Carry(source, pages, TestContext.Current.CancellationToken);
            structure.Write(TestContext.Current.CancellationToken);
            var path = Path.Combine(folder, "memory.pdf");
            output.Save(path);
            using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var names = Destinations(result);
            Assert.Equal(narrow ? 2 : 3, names.Count);
            View(result, names[Key(raw)], 2, "/Fit");
            View(result, names["z"], 3, "/Fit");
            var links = Links(result, 2);
            View(result, Resolved(result, links[0].Elements["/Dest"]), 2, "/Fit");
            View(result, Resolved(result, links[2].Elements["/Dest"]), 3, "/Fit");
            if (narrow)
            {
                Assert.DoesNotContain(Key(unicode), names.Keys);
                Assert.False(links[1].Elements.ContainsKey("/Dest"));
            }
            else
            {
                View(result, names[Key(unicode)], 3, "/Fit");
                View(result, Resolved(result, links[1].Elements["/Dest"]), 3, "/Fit");
            }
        }
    }

    private void DestinationPaths(Func<PdfDocument, PdfItem> make, string kind, string[] values, bool valid = true)
    {
        var source = Source(pdf =>
        {
            var destination = make(pdf);
            NameTree(pdf).Elements["/Names"] = Items(pdf, new PdfString("a"), Dest(pdf, 0),
                new PdfString("tree"), destination, new PdfString("z"), Dest(pdf, 1));
            var legacy = new PdfDictionary(pdf);
            legacy.Elements["/legacy"] = destination;
            pdf.Internals.Catalog.Elements["/Dests"] = legacy;
            var bookmarks = new List<PdfDictionary> { Bookmark(pdf, "before", Dest(pdf, 0)) };
            Link(pdf, 0, Dest(pdf, 0));
            foreach (var target in new PdfItem[] { destination, new PdfString("tree"), new PdfName("/legacy") })
                foreach (var action in new[] { false, true })
                {
                    var bookmark = Bookmark(pdf, $"route-{bookmarks.Count}");
                    if (action)
                    {
                        bookmark.Elements["/A"] = Action(pdf, "/GoTo", target);
                        Link(pdf, 0, action: Action(pdf, "/GoTo", target));
                    }
                    else
                    {
                        bookmark.Elements["/Dest"] = target;
                        Link(pdf, 0, target);
                    }
                    bookmarks.Add(bookmark);
                }
            bookmarks.Add(Bookmark(pdf, "after", Dest(pdf, 1)));
            Link(pdf, 0, Dest(pdf, 1));
            Children(OutlineRoot(pdf), bookmarks.ToArray());
        });
        using var result = PdfReader.Open(MergeAfterPrefix(source), PdfDocumentOpenMode.Import);
        Assert.Equal(4, result.PageCount);
        var names = Destinations(result);
        Assert.Equal(valid ? 4 : 2, names.Count);
        View(result, names["a"], 2, "/Fit");
        View(result, names["z"], 3, "/Fit");
        var bookmarks = OutlineItems(result);
        Assert.Equal(new[] { "before", "route-1", "route-2", "route-3", "route-4", "route-5", "route-6", "after" },
            bookmarks.Select(item => S(item.Elements["/Title"])));
        var links = Links(result, 2);
        Assert.Equal(8, links.Count);
        View(result, A(bookmarks[0].Elements["/Dest"]), 2, "/Fit");
        View(result, A(bookmarks[^1].Elements["/Dest"]), 3, "/Fit");
        View(result, A(links[0].Elements["/Dest"]), 2, "/Fit");
        View(result, A(links[^1].Elements["/Dest"]), 3, "/Fit");
        if (valid)
        {
            View(result, names["tree"], 3, kind, values);
            View(result, names["legacy"], 3, kind, values);
        }
        else
        {
            Assert.DoesNotContain("tree", names.Keys);
            Assert.DoesNotContain("legacy", names.Keys);
        }
        for (var i = 1; i <= 6; i++)
            if (valid)
            {
                View(result, A(bookmarks[i].Elements["/Dest"]), 3, kind, values);
                var item = links[i].Elements["/Dest"] ?? D(links[i].Elements["/A"]).Elements["/D"];
                View(result, Resolved(result, item), 3, kind, values);
            }
            else
            {
                Assert.False(bookmarks[i].Elements.ContainsKey("/Dest"));
                Assert.False(bookmarks[i].Elements.ContainsKey("/A"));
                Assert.False(links[i].Elements.ContainsKey("/Dest"));
                Assert.False(links[i].Elements.ContainsKey("/A"));
            }
    }

    [Theory]
    [InlineData("/XYZ", "")]
    [InlineData("/XYZ", "10")]
    [InlineData("/XYZ", "null")]
    [InlineData("/XYZ", "10,20")]
    [InlineData("/XYZ", "10,null")]
    [InlineData("/XYZ", "null,20")]
    [InlineData("/FitH", "")]
    [InlineData("/FitV", "")]
    [InlineData("/FitBH", "")]
    [InlineData("/FitBV", "")]
    public void Trailing_destination_values_are_padded_with_null(string kind, string input)
    {
        var values = input.Length == 0 ? [] : input.Split(',');
        var expected = values.Concat(Enumerable.Repeat("null", (kind == "/XYZ" ? 3 : 1) - values.Length)).ToArray();
        DestinationPaths(pdf => Dest(pdf, 1, kind, values.Select(value =>
            value == "null" ? (PdfItem)PdfNull.Value : new PdfInteger(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))).ToArray()),
            kind, expected);
    }

    [Theory]
    [InlineData("/XYZ", "10,20,2")]
    [InlineData("/XYZ", "null,20,2")]
    [InlineData("/XYZ", "10,null,2")]
    [InlineData("/XYZ", "10,20,null")]
    [InlineData("/XYZ", "null,null,2")]
    [InlineData("/XYZ", "null,20,null")]
    [InlineData("/XYZ", "10,null,null")]
    [InlineData("/XYZ", "null,null,null")]
    [InlineData("/Fit", "")]
    [InlineData("/FitB", "")]
    [InlineData("/FitH", "20")]
    [InlineData("/FitH", "null")]
    [InlineData("/FitV", "10")]
    [InlineData("/FitV", "null")]
    [InlineData("/FitBH", "20")]
    [InlineData("/FitBH", "null")]
    [InlineData("/FitBV", "10")]
    [InlineData("/FitBV", "null")]
    [InlineData("/FitR", "1,2,30,40")]
    public void Complete_destinations_keep_each_kind_and_value(string kind, string input)
    {
        var values = input.Length == 0 ? [] : input.Split(',');
        DestinationPaths(pdf => Dest(pdf, 1, kind, values.Select(value =>
            value == "null" ? (PdfItem)PdfNull.Value : new PdfInteger(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))).ToArray()),
            kind, values);
    }

    [Theory]
    [InlineData("fitr-short-0")]
    [InlineData("fitr-short-1")]
    [InlineData("fitr-short-2")]
    [InlineData("fitr-short-3")]
    [InlineData("fitr-null-0")]
    [InlineData("fitr-null-1")]
    [InlineData("fitr-null-2")]
    [InlineData("fitr-null-3")]
    [InlineData("/XYZ")]
    [InlineData("/Fit")]
    [InlineData("/FitB")]
    [InlineData("/FitH")]
    [InlineData("/FitBH")]
    [InlineData("/FitV")]
    [InlineData("/FitBV")]
    [InlineData("/FitR")]
    [InlineData("string")]
    [InlineData("name")]
    [InlineData("array")]
    [InlineData("boolean")]
    [InlineData("missing-page")]
    [InlineData("object-page")]
    [InlineData("integer-page")]
    [InlineData("unknown")]
    [InlineData("missing-kind")]
    [InlineData("empty")]
    [InlineData("not-array")]
    public void Invalid_destinations_stay_rejected_beside_healthy_items(string failure)
    {
        DestinationPaths(pdf =>
        {
            if (failure == "not-array")
                return new PdfInteger(42);
            if (failure == "empty")
                return Items(pdf);
            if (failure == "missing-kind")
                return Items(pdf, pdf.Pages[1].Reference!);
            if (failure.StartsWith("fitr-short-", StringComparison.Ordinal))
                return Dest(pdf, 1, "/FitR", Enumerable.Range(0, failure[^1] - '0').Select(i => (PdfItem)new PdfInteger(i)).ToArray());
            if (failure.StartsWith("fitr-null-", StringComparison.Ordinal))
            {
                var values = Enumerable.Range(0, 4).Select(i => (PdfItem)new PdfInteger(i)).ToArray();
                values[failure[^1] - '0'] = PdfNull.Value;
                return Dest(pdf, 1, "/FitR", values);
            }
            if (failure.StartsWith('/'))
            {
                var length = failure switch
                {
                    "/XYZ" => 3,
                    "/Fit" or "/FitB" => 0,
                    "/FitR" => 4,
                    _ => 1,
                };
                return Dest(pdf, 1, failure, Enumerable.Range(0, length + 1).Select(i => (PdfItem)new PdfInteger(i)).ToArray());
            }
            if (failure == "unknown")
                return Dest(pdf, 1, "/Unknown");
            if (failure is "missing-page" or "object-page" or "integer-page")
            {
                var destination = Dest(pdf, 1);
                destination.Elements[0] = failure switch
                {
                    "missing-page" => pdf.AddPage().Reference!,
                    "object-page" => Resource(pdf).Reference!,
                    _ => new PdfInteger(1),
                };
                // A real page reference outside the source page tree has no entry in the merge map.
                if (failure == "missing-page")
                    pdf.Pages.RemoveAt(2);
                return destination;
            }
            PdfItem bad = failure switch
            {
                "string" => new PdfString("bad"),
                "name" => new PdfName("/bad"),
                "array" => Items(pdf),
                _ => new PdfBoolean(true),
            };
            return Dest(pdf, 1, "/XYZ", new PdfInteger(10), bad);
        }, "/Fit", [], valid: false);
    }

    [Theory]
    [InlineData(31, 1)]
    [InlineData(31, 1000)]
    [InlineData(32, 1)]
    [InlineData(32, 1000)]
    [InlineData(33, 1)]
    [InlineData(33, 1000)]
    public void Names_spend_the_budget_once_and_leave_room_for_neighbours(int length, int repeats)
    {
        var name = new string('k', length);
        var over = new string('x', 80 - 1 - length + 1);
        var source = Source(pdf =>
        {
            NameTree(pdf).Elements["/Names"] = Items(pdf, new PdfString("a"), Dest(pdf, 0),
                new PdfString(name), Dest(pdf, 1), new PdfString(over), Dest(pdf, 0), new PdfString("z"), Dest(pdf, 1));
            Link(pdf, 0, new PdfString("a"));
            for (var i = 0; i < repeats; i++)
                Link(pdf, 0, new PdfString(name));
            for (var i = 0; i < 1000; i++)
                Link(pdf, 0, new PdfString(over));
            Link(pdf, 0, new PdfString("z"));
            Children(OutlineRoot(pdf), Bookmark(pdf, "ok", Dest(pdf, 1)));
        });
        using (var result = PdfReader.Open(MergeAfterPrefix(source, new() { StringBytes = 80 }), PdfDocumentOpenMode.Import))
        {
            Assert.Equal(new[] { "a", name, "z" }, Destinations(result).Keys);
            Assert.Equal("ok", S(Assert.Single(OutlineItems(result)).Elements["/Title"]));
            var links = Links(result, 2);
            Assert.Equal(repeats + 1002, links.Count);
            View(result, Resolved(result, links[0].Elements["/Dest"]), 2, "/Fit");
            View(result, Resolved(result, links[^1].Elements["/Dest"]), 3, "/Fit");
            foreach (var link in links.Skip(1).Take(repeats))
                View(result, Resolved(result, link.Elements["/Dest"]), 3, "/Fit");
            SharedName(result, name, links.Skip(1).Take(repeats).Select(link => link.Elements["/Dest"]!));
            Assert.All(links.Skip(1 + repeats).Take(1000), link => Assert.False(link.Elements.ContainsKey("/Dest")));
        }

        string Make(bool second) => Source(pdf =>
        {
            NameTree(pdf).Elements["/Names"] = Items(pdf, new PdfString(name), Dest(pdf, 1),
                new PdfString(second ? "y" : "z"), Dest(pdf, 0));
            Link(pdf, 0, new PdfString(name));
            Link(pdf, 0, new PdfString(second ? "y" : "z"));
        });
        var first = Make(false);
        var path = Path.Combine(Path.GetDirectoryName(first)!, "suffix-budget.pdf");
        MergeFiles([first, Make(true)], path, TestContext.Current.CancellationToken, new() { StringBytes = length + 1 });
        using var suffixed = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { name, "y", "z" }, Destinations(suffixed).Keys);
        View(suffixed, Resolved(suffixed, Links(suffixed, 0)[0].Elements["/Dest"]), 1, "/Fit");
        Assert.False(Links(suffixed, 2)[0].Elements.ContainsKey("/Dest"));
        View(suffixed, Resolved(suffixed, Links(suffixed, 2)[1].Elements["/Dest"]), 2, "/Fit");
    }

    [Theory]
    [InlineData(false, -2)]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(true, -2)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    public void Default_string_budget_is_exact_and_resets_per_source(bool title, int offset)
    {
        const int budget = 4 * 1024 * 1024;
        Assert.Equal(budget, new PdfMergeStructure.Limits().StringBytes);
        var length = budget + offset;
        var keep = 1 + length <= budget;
        string Make(bool second) => Source(pdf =>
        {
            var normal = second ? "m" : "n";
            var text = new string(second ? 'b' : 'a', length);
            var pairs = Items(pdf, new PdfString(normal), Dest(pdf, 0));
            NameTree(pdf).Elements["/Names"] = pairs;
            for (var i = 0; i < 100; i++)
                Link(pdf, 0, new PdfString(normal));
            var shared = new PdfStringObject(pdf, text) { Encoding = PdfStringEncoding.RawEncoding };
            pdf.Internals.AddObject(shared);
            if (title)
            {
                var bookmark = Bookmark(pdf, "unused", Dest(pdf, 1));
                bookmark.Elements["/Title"] = shared.Reference;
                Children(OutlineRoot(pdf), bookmark);
            }
            else
            {
                pairs.Elements.Add(shared.Reference!);
                pairs.Elements.Add(Dest(pdf, 1));
                for (var i = 0; i < 100; i++)
                    Link(pdf, 0, shared.Reference);
            }
        });
        var first = Make(false);
        var path = Path.Combine(Path.GetDirectoryName(first)!, "default-budget.pdf");
        MergeFiles([first, Make(true)], path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(4, result.PageCount);
        var names = Destinations(result);
        Assert.Equal(!title && keep ? 4 : 2, names.Count);
        var outlines = OutlineItems(result);
        Assert.Equal(title && keep ? 2 : 0, outlines.Count);
        for (var source = 0; source < 2; source++)
        {
            var normal = source == 0 ? "n" : "m";
            var text = new string(source == 0 ? 'a' : 'b', length);
            var links = Links(result, source * 2);
            Assert.Equal(title ? 100 : 200, links.Count);
            View(result, names[normal], source * 2, "/Fit");
            Assert.All(links.Take(100), link => View(result, Resolved(result, link.Elements["/Dest"]), source * 2, "/Fit"));
            if (title && keep)
            {
                Assert.Equal(text, S(outlines[source].Elements["/Title"]));
                View(result, A(outlines[source].Elements["/Dest"]), source * 2 + 1, "/Fit");
            }
            else if (!title && keep)
            {
                View(result, names[text], source * 2 + 1, "/Fit");
                Assert.All(links.Skip(100), link =>
                {
                    Assert.Equal(text, Key(link.Elements["/Dest"]!));
                    View(result, names[text], source * 2 + 1, "/Fit");
                });
            }
            else if (!title)
            {
                Assert.DoesNotContain(text, names.Keys);
                Assert.All(links.Skip(100), link => Assert.False(link.Elements.ContainsKey("/Dest")));
            }
        }
    }

    [Theory]
    [InlineData(256)]
    [InlineData(4096)]
    public void A_repeated_indirect_key_keeps_its_first_valid_destination(int length)
    {
        var name = new string('k', length);
        var source = Source(pdf =>
        {
            var shared = new PdfStringObject(pdf, name) { Encoding = PdfStringEncoding.RawEncoding };
            pdf.Internals.AddObject(shared);
            var pairs = Items(pdf, new PdfString("a"), Dest(pdf, 0));
            for (var i = 0; i < 100; i++)
            {
                pairs.Elements.Add(shared.Reference!);
                pairs.Elements.Add(i == 0 ? new PdfInteger(42) : Dest(pdf, i == 1 ? 1 : 0));
                Link(pdf, 0, shared.Reference);
            }
            pairs.Elements.Add(new PdfString("z"));
            pairs.Elements.Add(Dest(pdf, 1));
            NameTree(pdf).Elements["/Names"] = pairs;
            Link(pdf, 0, new PdfString("a"));
            Link(pdf, 0, new PdfString("z"));
        });
        using var result = PdfReader.Open(MergeAfterPrefix(source), PdfDocumentOpenMode.Import);
        var names = Destinations(result);
        Assert.Equal(new[] { "a", name, "z" }, names.Keys);
        View(result, names["a"], 2, "/Fit");
        View(result, names["z"], 3, "/Fit");
        View(result, names[name], 3, "/Fit");
        var links = Links(result, 2);
        Assert.Equal(102, links.Count);
        SharedName(result, name, links.Take(100).Select(link => link.Elements["/Dest"]!));
        Assert.All(links.Take(100), link => View(result, Resolved(result, link.Elements["/Dest"]), 3, "/Fit"));
        View(result, Resolved(result, links[100].Elements["/Dest"]), 2, "/Fit");
        View(result, Resolved(result, links[101].Elements["/Dest"]), 3, "/Fit");
    }

    [Fact]
    public void A_repeated_long_key_over_budget_after_suffix_stays_unregistered()
    {
        var name = new string('k', 256);
        string Make(bool second) => Source(pdf =>
        {
            var shared = new PdfStringObject(pdf, name) { Encoding = PdfStringEncoding.RawEncoding };
            pdf.Internals.AddObject(shared);
            var pairs = new PdfArray(pdf);
            for (var i = 0; i < (second ? 100 : 1); i++)
            {
                pairs.Elements.Add(shared.Reference!);
                pairs.Elements.Add(Dest(pdf, 1));
                Link(pdf, 0, shared.Reference);
            }
            pairs.Elements.Add(new PdfString(second ? "z" : "a"));
            pairs.Elements.Add(Dest(pdf, 0));
            Link(pdf, 0, new PdfString(second ? "z" : "a"));
            NameTree(pdf).Elements["/Names"] = pairs;
        });
        var first = Make(false);
        var path = Path.Combine(Path.GetDirectoryName(first)!, "repeated-suffix.pdf");
        MergeFiles([first, Make(true)], path, TestContext.Current.CancellationToken, new() { StringBytes = 257 });
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var names = Destinations(result);
        Assert.Equal(new[] { "a", name, "z" }, names.Keys);
        View(result, names[name], 1, "/Fit");
        View(result, names["a"], 0, "/Fit");
        View(result, names["z"], 2, "/Fit");
        var firstLinks = Links(result, 0);
        SharedName(result, name, [firstLinks[0].Elements["/Dest"]!]);
        View(result, Resolved(result, firstLinks[0].Elements["/Dest"]), 1, "/Fit");
        View(result, Resolved(result, firstLinks[1].Elements["/Dest"]), 0, "/Fit");
        var secondLinks = Links(result, 2);
        Assert.Equal(101, secondLinks.Count);
        Assert.All(secondLinks.Take(100), link => Assert.False(link.Elements.ContainsKey("/Dest")));
        View(result, Resolved(result, secondLinks[^1].Elements["/Dest"]), 2, "/Fit");
    }

    [Fact]
    public void Looking_up_registered_names_does_not_spend_the_string_budget_again()
    {
        const int count = 60_000;
        var timer = Stopwatch.StartNew();
        var source = Source(document =>
        {
            var pairs = new PdfArray(document);
            NameTree(document).Elements["/Names"] = pairs;
            var destination = Dest(document, 1);
            for (var i = 0; i < count; i++)
            {
                var name = i.ToString("D42", System.Globalization.CultureInfo.InvariantCulture);
                pairs.Elements.Add(new PdfString(name));
                pairs.Elements.Add(destination);
                Link(document, 0, new PdfString(name));
            }
        });
        var output = MergeOne(source);
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var names = Destinations(result);
        var links = Links(result, 0);
        var kept = links.Count(link => link.Elements.ContainsKey("/Dest"));
        TestContext.Current.TestOutputHelper?.WriteLine($"{names.Count:N0} names, {kept:N0} link destinations, {timer.Elapsed.TotalSeconds:F3} s");
        Assert.Equal(count, names.Count);
        Assert.Equal(count, links.Count);
        Assert.Equal(count, kept);
        for (var i = 0; i < count; i++)
        {
            var name = i.ToString("D42", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(name, Key(links[i].Elements["/Dest"]!));
            View(result, names[name], 1, "/Fit");
        }
    }

    private static void OutlineList(PdfDocument document, int count, bool chain = false, PdfItem? title = null, PdfItem? dest = null)
    {
        var root = OutlineRoot(document);
        var parent = root;
        PdfDictionary? previous = null;
        for (var i = 0; i < count; i++)
        {
            var item = Bookmark(document, i.ToString(System.Globalization.CultureInfo.InvariantCulture), Dest(document, i % 2));
            if (title is not null)
                item.Elements["/Title"] = title;
            if (dest is not null)
                item.Elements["/Dest"] = dest;
            item.Elements["/Count"] = new PdfInteger(i % 2 == 0 ? 999 : -999);
            if (chain || previous is null)
                parent.Elements["/First"] = item.Reference;
            else
                previous.Elements["/Next"] = item.Reference;
            item.Elements["/Parent"] = parent.Reference;
            previous = item;
            if (chain)
                parent = item;
        }
    }

    private static PdfDictionary ResourceChain(PdfDocument document, int count)
    {
        var first = Resource(document);
        var current = first;
        for (var i = 1; i < count; i++)
        {
            var next = Resource(document);
            current.Elements["/Next"] = next.Reference;
            current = next;
        }
        return first;
    }

    [Theory]
    [InlineData("outline-items")]
    [InlineData("outline-depth")]
    [InlineData("name-nodes")]
    [InlineData("resources")]
    [InlineData("appearance")]
    [InlineData("shared-title")]
    [InlineData("long-name")]
    [InlineData("shared-kids")]
    [InlineData("shared-page-resource")]
    [InlineData("shared-field-resources")]
    [InlineData("shared-dest")]
    [InlineData("repeated-key")]
    [InlineData("two-long-dests")]
    [InlineData("shared-action")]
    [InlineData("wide-page-resource")]
    public void Structure_beyond_the_limits_is_cut_in_bounded_time_and_memory(string kind)
    {
        var source = Source(document =>
        {
            switch (kind)
            {
                case "outline-items": OutlineList(document, 150_000); break;
                case "outline-depth": OutlineList(document, 1000, chain: true); break;
                case "shared-title":
                    var shared = new PdfStringObject(document, new string('x', 64 * 1024));
                    document.Internals.AddObject(shared);
                    OutlineList(document, 150_000, title: shared.Reference);
                    break;
                case "name-nodes":
                    var tree = NameTree(document);
                    var kids = new PdfArray(document);
                    tree.Elements["/Kids"] = kids;
                    tree.Elements["/Names"] = Items(document, new PdfString("root"), Dest(document, 0));
                    for (var i = 1; i < 150_000; i++)
                    {
                        var child = Object(document);
                        child.Elements["/Names"] = Items(document, new PdfString($"n{i}"), Dest(document, 0));
                        kids.Elements.Add(child.Reference!);
                    }
                    break;
                case "resources":
                    var form = Form(document);
                    Field(document, form, "name", "value");
                    var resources = new PdfDictionary(document);
                    var fonts = new PdfDictionary(document);
                    fonts.Elements["/Small"] = Resource(document, "Helvetica").Reference;
                    fonts.Elements["/Long"] = ResourceChain(document, 20_000).Reference;
                    resources.Elements["/Font"] = fonts;
                    form.Elements["/DR"] = resources;
                    break;
                case "appearance":
                    var appearanceForm = Form(document);
                    appearanceForm.Elements["/DA"] = new PdfString("/F1 10 Tf " + new string(' ', 4096 - 10));
                    for (var i = 0; i < 1000; i++)
                        Field(document, appearanceForm, $"f{i}", "v");
                    break;
                case "shared-kids":
                    // 50,000 nodes share one /Kids array of 50,000 entries: small to store, 2.5 billion
                    // children if the array were read again for every node.
                    var leaf = Object(document);
                    leaf.Elements["/Names"] = Items(document, new PdfString("leaf"), Dest(document, 0));
                    var sharedKids = new PdfArray(document);
                    document.Internals.AddObject(sharedKids);
                    for (var i = 0; i < 50_000; i++)
                        sharedKids.Elements.Add(leaf.Reference!);
                    var top = new PdfArray(document);
                    for (var i = 0; i < 50_000; i++)
                    {
                        var node = Object(document);
                        node.Elements["/Kids"] = sharedKids.Reference;
                        top.Elements.Add(node.Reference!);
                    }
                    NameTree(document).Elements["/Kids"] = top;
                    break;
                case "shared-page-resource":
                    // 50,000 fonts lead into one chain of 9,000 dictionaries that ends at a page.
                    var pageForm = Form(document);
                    Field(document, pageForm, "name", "v");
                    var chain = Object(document);
                    chain.Elements["/ExamplePage"] = document.Pages[0].Reference;
                    for (var i = 1; i < 9000; i++)
                    {
                        var link = Object(document);
                        link.Elements["/Next"] = chain.Reference;
                        chain = link;
                    }
                    var chainFonts = new PdfDictionary(document);
                    for (var i = 0; i < 50_000; i++)
                    {
                        var font = Resource(document, "Helvetica");
                        font.Elements["/Example"] = chain.Reference;
                        chainFonts.Elements[$"/F{i}"] = font.Reference;
                    }
                    var chainResources = new PdfDictionary(document);
                    chainResources.Elements["/Font"] = chainFonts;
                    pageForm.Elements["/DR"] = chainResources;
                    break;
                case "shared-field-resources":
                    // 50,000 fields share one /DR whose /Font has 50,000 names.
                    var sharedForm = Form(document);
                    var helvetica = Resource(document, "Helvetica").Reference!;
                    var ownFonts = new PdfDictionary(document);
                    for (var i = 0; i < 50_000; i++)
                        ownFonts.Elements[$"/F{i}"] = helvetica;
                    var own = Object(document);
                    own.Elements["/Font"] = ownFonts;
                    for (var i = 0; i < 50_000; i++)
                        Field(document, sharedForm, $"f{i}", "v").Elements["/DR"] = own.Reference;
                    break;
                case "wide-page-resource":
                    // 5,000 fonts lead into one array of a page and 200,000 integers.
                    var wideForm = Form(document);
                    Field(document, wideForm, "name", "v");
                    var wide = new PdfArray(document);
                    document.Internals.AddObject(wide);
                    wide.Elements.Add(document.Pages[0].Reference!);
                    for (var i = 0; i < 200_000; i++)
                        wide.Elements.Add(new PdfInteger(0));
                    var wideFonts = new PdfDictionary(document);
                    for (var i = 0; i < 5000; i++)
                    {
                        var font = Resource(document, "Helvetica");
                        font.Elements["/Example"] = wide.Reference;
                        wideFonts.Elements[$"/F{i}"] = font.Reference;
                    }
                    var wideResources = new PdfDictionary(document);
                    wideResources.Elements["/Font"] = wideFonts;
                    wideForm.Elements["/DR"] = wideResources;
                    break;
                case "shared-dest":
                    // 10,000 bookmarks name one destination through the same 1 MiB Unicode string.
                    var unicodeKey = new PdfStringObject(document, new string('\uD55C', 512 * 1024));
                    document.Internals.AddObject(unicodeKey);
                    NameTree(document).Elements["/Names"] = Items(document, unicodeKey.Reference!, Dest(document, 0));
                    OutlineList(document, 10_000, dest: unicodeKey.Reference);
                    break;
                case "repeated-key":
                    // One /Names array repeats the same 64 KiB key 150,000 times.
                    var repeatedKey = new PdfStringObject(document, new string('k', 64 * 1024));
                    document.Internals.AddObject(repeatedKey);
                    var repeated = new PdfArray(document);
                    for (var i = 0; i < 150_000; i++)
                    {
                        repeated.Elements.Add(repeatedKey.Reference!);
                        repeated.Elements.Add(Dest(document, 0));
                    }
                    NameTree(document).Elements["/Names"] = repeated;
                    break;
                case "two-long-dests":
                    // Two long keys that differ only in the last character, used by alternating bookmarks.
                    var prefix = new string('\uD55C', 256 * 1024);
                    var first = new PdfStringObject(document, prefix + "A");
                    var second = new PdfStringObject(document, prefix + "B");
                    document.Internals.AddObject(first);
                    document.Internals.AddObject(second);
                    NameTree(document).Elements["/Names"] = Items(document, first.Reference!, Dest(document, 0), second.Reference!, Dest(document, 1));
                    OutlineList(document, 5_000, dest: first.Reference);
                    var alternate = OutlineItems(document);
                    for (var i = 1; i < alternate.Count; i += 2)
                        alternate[i].Elements["/Dest"] = second.Reference;
                    break;
                case "shared-action":
                    // 20,000 links share one action whose direct /Next holds 5,000 URI actions.
                    var nextActions = new PdfArray(document);
                    for (var i = 0; i < 5000; i++)
                    {
                        var uri = new PdfDictionary(document);
                        uri.Elements["/S"] = new PdfName("/URI");
                        uri.Elements["/URI"] = new PdfString($"https://example.org/{i}");
                        nextActions.Elements.Add(uri);
                    }
                    var sharedAction = Object(document);
                    sharedAction.Elements["/S"] = new PdfName("/GoTo");
                    sharedAction.Elements["/D"] = Dest(document, 1);
                    sharedAction.Elements["/Next"] = nextActions;
                    for (var i = 0; i < 20_000; i++)
                        Link(document, 0, action: sharedAction);
                    break;
                case "long-name":
                    var longKey = new string('k', 64 * 1024);
                    var key = new PdfStringObject(document, longKey);
                    document.Internals.AddObject(key);
                    NameTree(document).Elements["/Names"] = Items(document, key.Reference!, Dest(document, 0));
                    for (var i = 0; i < 16_384; i++)
                        Link(document, 0, key.Reference!);
                    break;
            }
        });
        var output = Path.Combine(Path.GetDirectoryName(source)!, "out.pdf");
        // Opening and saving a large source already allocates hundreds of MB before any structure is
        // carried, so the bound is relative: the same merge with pages only, as before this change.
        var pagesOnly = GC.GetAllocatedBytesForCurrentThread();
        using (var baseline = new PdfDocument())
        {
            using (var opened = PdfReader.Open(source, PdfDocumentOpenMode.Import))
                foreach (var page in opened.Pages)
                    baseline.AddPage(page);
            baseline.Save(Path.Combine(Path.GetDirectoryName(source)!, "baseline.pdf"));
        }
        pagesOnly = GC.GetAllocatedBytesForCurrentThread() - pagesOnly;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        MergeFiles([source], output, TestContext.Current.CancellationToken);
        timer.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        TestContext.Current.TestOutputHelper?.WriteLine($"{kind}: {timer.Elapsed.TotalSeconds:F3} s, {allocated:N0} bytes, pages only {pagesOnly:N0} bytes");
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, result.PageCount);
        switch (kind)
        {
            case "outline-items": Assert.Equal(100_000, OutlineItems(result).Count); break;
            case "outline-depth": Assert.Equal(64, OutlineItems(result).Count); break;
            case "name-nodes": Assert.Equal(100_000, Destinations(result).Count); break;
            case "shared-kids": Assert.Equal(new[] { "leaf" }, Destinations(result).Keys); break;
            case "shared-page-resource":
                Assert.False(D(D(result.Internals.Catalog.Elements["/AcroForm"])).Elements.ContainsKey("/DR"));
                Assert.Single(Fields(result));
                break;
            case "shared-field-resources": Assert.Equal(50_000, Fields(result).Count); break;
            case "shared-dest":
                var destOutlines = OutlineItems(result);
                Assert.Equal(10_000, destOutlines.Count);
                Assert.All(destOutlines, d => Assert.Equal(result.Pages[0].Reference, A(d.Elements["/Dest"]).Elements[0]));
                break;
            case "repeated-key": Assert.Single(Destinations(result)); break;
            case "shared-action":
                var sharedLinks = Links(result, 0);
                Assert.Equal(20_000, sharedLinks.Count);
                Assert.Single(sharedLinks.Select(l => Assert.IsType<PdfReference>(l.Elements["/A"]).Value).Distinct());
                break;
            case "two-long-dests":
                var alternating = OutlineItems(result);
                Assert.Equal(5_000, alternating.Count);
                for (var i = 0; i < alternating.Count; i++)
                    Assert.Equal(result.Pages[i % 2].Reference, A(alternating[i].Elements["/Dest"]).Elements[0]);
                break;
            case "wide-page-resource":
                Assert.False(D(D(result.Internals.Catalog.Elements["/AcroForm"])).Elements.ContainsKey("/DR"));
                Assert.Single(Fields(result));
                break;
            case "resources":
                var fonts = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]);
                Assert.Equal(new[] { "/Small" }, fonts.Elements.Keys);
                Assert.DoesNotContain(result.Internals.GetAllObjects().OfType<PdfDictionary>(), d => d.Elements.ContainsKey("/Next"));
                break;
            case "appearance":
                var fields = Fields(result);
                Assert.Equal(256, fields.Count);
                Assert.Equal(1024 * 1024, fields.Values.Sum(f => S(f.Elements["/DA"]).Length));
                break;
            case "shared-title":
                var outlines = OutlineItems(result);
                Assert.Equal(64, outlines.Count);
                Assert.All(outlines, d => Assert.Equal(new string('x', 64 * 1024), S(d.Elements["/Title"])));
                Assert.Equal(4 * 1024 * 1024, outlines.Sum(d => S(d.Elements["/Title"]).Length));
                break;
            case "long-name":
                var links = Links(result, 0);
                Assert.Equal(16_384, links.Count);
                var sharedName = Assert.Single(links.Select(d => Assert.IsType<PdfReference>(d.Elements["/Dest"]).Value).Distinct());
                Assert.Equal(new string('k', 64 * 1024), S(sharedName));
                Assert.Equal(S(sharedName), Assert.Single(Destinations(result)).Key);
                Assert.True(new FileInfo(output).Length < 2 * new FileInfo(Path.Combine(Path.GetDirectoryName(source)!, "baseline.pdf")).Length + 1024 * 1024);
                break;
        }
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(60), $"{kind}: {timer.Elapsed}");
        // The allocation bound is the real check; this time limit only catches runaway work.
        Assert.True(allocated < 3 * pagesOnly + 64 * 1024 * 1024, $"{kind}: {allocated} bytes, pages only {pagesOnly} bytes");
    }

    [Theory]
    [InlineData("outline-items")]
    [InlineData("outline-depth")]
    [InlineData("name-nodes")]
    [InlineData("name-depth")]
    [InlineData("names")]
    [InlineData("field-depth")]
    [InlineData("field-items")]
    [InlineData("resources")]
    [InlineData("strings")]
    [InlineData("appearance-length")]
    public void Each_limit_cuts_exactly_at_its_value(string kind)
    {
        if (kind == "appearance-length")
        {
            var seed = Source(document =>
            {
                var form = Form(document);
                Field(document, form, "seed", "v");
                var resources = new PdfDictionary(document);
                var fonts = new PdfDictionary(document);
                fonts.Elements["/F1"] = Resource(document, "Helvetica").Reference;
                resources.Elements["/Font"] = fonts;
                form.Elements["/DR"] = resources;
            });
            var boundary = Source(document =>
            {
                var form = Form(document);
                form.Elements["/DA"] = new PdfString("/F1 10 Tf " + new string(' ', 4096 - 10));
                Field(document, form, "before", "ok");
                var own = Field(document, form, "long", "ok");
                own.Elements["/DA"] = new PdfString("/F1 10 Tf " + new string(' ', 4097 - 10));
                var resources = new PdfDictionary(document);
                var fonts = new PdfDictionary(document);
                fonts.Elements["/F1"] = Resource(document, "Courier").Reference;
                resources.Elements["/Font"] = fonts;
                form.Elements["/DR"] = resources;
            });
            var beyond = Source(document =>
            {
                var form = Form(document);
                form.Elements["/DA"] = new PdfString("/F1 10 Tf " + new string(' ', 4097 - 10));
                Field(document, form, "not-inherited", "ok");
            });
            var path = Path.Combine(Path.GetDirectoryName(seed)!, "length.pdf");
            MergeFiles([seed, boundary, beyond], path, TestContext.Current.CancellationToken);
            using var opened = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var fields = Fields(opened);
            Assert.StartsWith("/F1_2 10 Tf ", S(fields["before"].Elements["/DA"]), StringComparison.Ordinal);
            Assert.Equal(4098, S(fields["before"].Elements["/DA"]).Length);
            Assert.StartsWith("/F1 10 Tf ", S(fields["long"].Elements["/DA"]), StringComparison.Ordinal);
            Assert.Equal(4097, S(fields["long"].Elements["/DA"]).Length);
            Assert.False(fields["not-inherited"].Elements.ContainsKey("/DA"));
            return;
        }
        var limits = new PdfMergeStructure.Limits();
        limits = kind switch
        {
            "outline-items" => limits with { OutlineItems = 3 },
            "outline-depth" => limits with { OutlineDepth = 3 },
            "name-nodes" => limits with { NameTreeNodes = 3 },
            "name-depth" => limits with { NameTreeDepth = 3 },
            "names" => limits with { NamedDestinations = 3 },
            "field-depth" => limits with { FieldDepth = 3 },
            "field-items" => limits with { FieldItems = 3 },
            "resources" => limits with { ResourceObjects = 5 },
            "strings" => limits with { StringBytes = 3 },
            _ => limits,
        };
        var source = Source(document =>
        {
            switch (kind)
            {
                case "outline-items": OutlineList(document, 5, chain: true); break;
                case "outline-depth": OutlineList(document, 5, chain: true); break;
                case "strings":
                    var root = OutlineRoot(document);
                    Children(root, Bookmark(document, "a"), Bookmark(document, "bb"), Bookmark(document, "c"));
                    break;
                case "name-nodes":
                case "name-depth":
                    var tree = NameTree(document);
                    for (var i = 0; i < 5; i++)
                    {
                        tree.Elements["/Names"] = Items(document, new PdfString($"n{i}"), Dest(document, 0));
                        var child = Object(document);
                        tree.Elements["/Kids"] = Items(document, child.Reference!);
                        tree = child;
                    }
                    break;
                case "names":
                    var pairs = new PdfArray(document);
                    NameTree(document).Elements["/Names"] = pairs;
                    for (var i = 0; i < 5; i++)
                    {
                        pairs.Elements.Add(new PdfString($"n{i}"));
                        pairs.Elements.Add(Dest(document, 0));
                    }
                    break;
                case "field-depth":
                case "field-items":
                    var form = Form(document);
                    Field(document, form, "before", "ok");
                    var parent = Object(document);
                    parent.Elements["/T"] = new PdfString("broken");
                    A(form.Elements["/Fields"]).Elements.Add(parent.Reference!);
                    for (var i = 0; i < 3; i++)
                    {
                        var child = i == 2 ? Widget(document, 0, parent) : Object(document);
                        child.Elements["/Parent"] = parent.Reference;
                        parent.Elements["/Kids"] = Items(document, child.Reference!);
                        parent = child;
                    }
                    break;
                case "resources":
                    var resourceForm = Form(document);
                    Field(document, resourceForm, "name", "v");
                    var resources = new PdfDictionary(document);
                    var fonts = new PdfDictionary(document);
                    fonts.Elements["/Before"] = ResourceChain(document, 3).Reference;
                    fonts.Elements["/Broken"] = ResourceChain(document, 3).Reference;
                    fonts.Elements["/After"] = Resource(document, "Courier").Reference;
                    resources.Elements["/Font"] = fonts;
                    resourceForm.Elements["/DR"] = resources;
                    break;
            }
        });
        using var result = PdfReader.Open(MergeOne(source, limits), PdfDocumentOpenMode.Import);
        Assert.Equal(2, result.PageCount);
        switch (kind)
        {
            case "outline-items":
            case "outline-depth":
                var outlines = OutlineItems(result);
                Assert.Equal(3, outlines.Count);
                Assert.Equal(2, Assert.IsType<PdfInteger>(D(result.Internals.Catalog.Elements["/Outlines"]).Elements["/Count"]).Value);
                Assert.Equal(1, Assert.IsType<PdfInteger>(outlines[0].Elements["/Count"]).Value);
                Assert.Equal(-1, Assert.IsType<PdfInteger>(outlines[1].Elements["/Count"]).Value);
                Assert.False(outlines[2].Elements.ContainsKey("/Count"));
                break;
            case "name-nodes":
            case "name-depth":
            case "names": Assert.Equal(3, Destinations(result).Count); break;
            case "strings": Assert.Equal(new[] { "a", "bb" }, OutlineItems(result).Select(d => S(d.Elements["/Title"]))); break;
            case "field-depth":
            case "field-items": Assert.Equal(new[] { "before" }, Fields(result).Keys); break;
            case "resources":
                var fonts = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]);
                Assert.Equal(new[] { "/Before" }, fonts.Elements.Keys);
                Assert.Equal(2, result.Internals.GetAllObjects().OfType<PdfDictionary>().Count(d => d.Elements.ContainsKey("/Next")));
                break;
        }
    }

    [Fact]
    public void Fields_keep_the_default_appearance_and_resources_of_their_source()
    {
        using var document = PdfReader.Open(Merge(), PdfDocumentOpenMode.Import);
        AssertDefaults(document);
        var seed = Source(pdf =>
        {
            var form = Form(pdf);
            Field(pdf, form, "first", "v").Elements["/DA"] = new PdfString("/F1 10 Tf");
            var resources = new PdfDictionary(pdf);
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            resources.Elements["/Font"] = fonts;
            form.Elements["/DR"] = resources;
        });
        var second = Source(pdf =>
        {
            var form = Form(pdf);
            Field(pdf, form, "second", "v").Elements["/DA"] = new PdfString("/F1 12 Tf");
            Field(pdf, form, "own", "v").Elements["/DA"] = new PdfString("/F1_2 9 Tf");
            var resources = new PdfDictionary(pdf);
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Courier").Reference;
            fonts.Elements["/F1_2"] = Resource(pdf, "Times-Roman").Reference;
            resources.Elements["/Font"] = fonts;
            form.Elements["/DR"] = resources;
        });
        var path = Path.Combine(Path.GetDirectoryName(seed)!, "reserved.pdf");
        MergeFiles([seed, second], path, TestContext.Current.CancellationToken);
        using var reserved = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var fields = Fields(reserved);
        var fonts = D(D(D(reserved.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]);
        foreach (var (field, font) in new[] { ("first", "/Helvetica"), ("second", "/Courier"), ("own", "/Times-Roman") })
        {
            var name = S(fields[field].Elements["/DA"]).Split(' ')[0];
            Assert.Equal(font, S(D(fonts.Elements[name]).Elements["/BaseFont"]));
        }
        Assert.StartsWith("/F1_2 ", S(fields["own"].Elements["/DA"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Renamed_fonts_also_resolve_in_a_fields_own_resources()
    {
        // LibreOffice writes a /DR on each field as well as on the form.
        string Make(string font, string? ownOther = null) => Source(pdf =>
        {
            var form = Form(pdf);
            var helv = Resource(pdf, font).Reference;
            var field = Field(pdf, form, "name", "v");
            field.Elements["/DA"] = new PdfString("0 g /He 8 Tf");
            foreach (var owner in new[] { form, field })
            {
                var resources = new PdfDictionary(pdf);
                var fonts = new PdfDictionary(pdf);
                fonts.Elements["/He"] = helv;
                // A name only the field's own /DR has must not be taken by the renamed form font.
                if (owner == field && ownOther is not null)
                    fonts.Elements["/He_2"] = Resource(pdf, ownOther).Reference;
                resources.Elements["/Font"] = fonts;
                owner.Elements["/DR"] = resources;
            }
        });
        var first = Make("Helvetica");
        var path = Path.Combine(Path.GetDirectoryName(first)!, "own-resources.pdf");
        MergeFiles([first, Make("Courier", ownOther: "Times-Roman")], path, TestContext.Current.CancellationToken);
        using var merged = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var fields = Fields(merged);
        var formFonts = D(D(D(merged.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]);
        foreach (var (field, font) in new[] { ("name", "/Helvetica"), ("2.name", "/Courier") })
        {
            var name = S(fields[field].Elements["/DA"]).Split(' ')[2];
            var ownFonts = D(D(fields[field].Elements["/DR"]).Elements["/Font"]);
            Assert.Equal(font, S(D(formFonts.Elements[name]).Elements["/BaseFont"]));
            Assert.Equal(font, S(D(ownFonts.Elements[name]).Elements["/BaseFont"]));
        }
    }

    [Fact]
    public void Resource_strings_are_counted_at_every_use()
    {
        // One indirect string shared by three fonts is written once per font in the merged /DR.
        var source = Source(pdf =>
        {
            var form = Form(pdf);
            Field(pdf, form, "name", "v");
            var shared = new PdfStringObject(pdf, new string('c', 1000));
            pdf.Internals.AddObject(shared);
            var fonts = new PdfDictionary(pdf);
            foreach (var key in new[] { "/F1", "/F2", "/F3" })
            {
                var font = Resource(pdf, "Helvetica");
                font.Elements["/CharSet"] = shared.Reference;
                fonts.Elements[key] = font.Reference;
            }
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            form.Elements["/DR"] = resources;
        });
        // Each font writes /Font, /Type1, /Helvetica and the 1,000-byte string: 1,021 bytes.
        using var result = PdfReader.Open(MergeOne(source, new PdfMergeStructure.Limits { ResourceBytes = 2500 }), PdfDocumentOpenMode.Import);
        var fonts = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]);
        Assert.Equal(new[] { "/F1", "/F2" }, fonts.Elements.Keys);
    }

    [Fact]
    public void Resources_that_lead_to_a_page_are_left_out()
    {
        var source = Source(pdf =>
        {
            var form = Form(pdf);
            Field(pdf, form, "name", "v");
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            var odd = Resource(pdf, "Courier");
            odd.Elements["/ExamplePage"] = pdf.Pages[0].Reference;
            fonts.Elements["/F2"] = odd.Reference;
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            form.Elements["/DR"] = resources;
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        var fonts = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]);
        Assert.Equal(new[] { "/F1" }, fonts.Elements.Keys);
        Assert.Equal(result.PageCount, result.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Count(d => V(d.Elements["/Type"]) is PdfName { Value: "/Page" }));
    }

    [Fact]
    public void Names_repeated_across_sources_take_the_next_free_suffix()
    {
        string Make(string destination, string field) => Source(pdf =>
        {
            NameTree(pdf).Elements["/Names"] = Items(pdf, new PdfString(destination), Dest(pdf, 0));
            var form = Form(pdf);
            Field(pdf, form, field, "v");
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            form.Elements["/DR"] = resources;
        });
        var sources = new[] { Make("intro", "f0"), Make("intro", "f1"), Make("intro-3", "f2"), Make("intro", "f0") };
        var path = Path.Combine(Path.GetDirectoryName(sources[0])!, "names.pdf");
        MergeFiles(sources, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "intro", "intro-2", "intro-3", "intro-4" }, Destinations(result).Keys.OrderBy(k => k, StringComparer.Ordinal));
        var fonts = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]);
        Assert.Equal(new[] { "/F1", "/F1_2", "/F1_3", "/F1_4" }, fonts.Elements.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "4.f0", "f0", "f1", "f2" }, Fields(result).Keys.Where(k => k != "4").OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void A_name_one_source_only_reserves_stays_free_for_the_next()
    {
        string Make(string font, int fields, int ownNames = 0, string? appearance = null) => Source(pdf =>
        {
            var form = Form(pdf);
            if (appearance is not null)
                form.Elements["/DA"] = new PdfString(appearance);
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, font).Reference;
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            form.Elements["/DR"] = resources;
            for (var i = 0; i < fields; i++)
            {
                var field = Field(pdf, form, $"{font}{i}", "v");
                if (ownNames == 0)
                    continue;
                field.Elements["/DA"] = new PdfString("/F1 10 Tf");
                var own = new PdfDictionary(pdf);
                own.Elements["/F1"] = fonts.Elements["/F1"];
                for (var n = 2; n < 2 + ownNames; n++)
                    own.Elements[$"/F1_{n}"] = Resource(pdf, "Times-Roman").Reference;
                var ownResources = new PdfDictionary(pdf);
                ownResources.Elements["/Font"] = own;
                field.Elements["/DR"] = ownResources;
            }
        });
        // The third source's 256 fields fit the 1 MiB appearance budget exactly when its font becomes /F1_2.
        var sources = new[] { Make("Helvetica", 1), Make("Courier", 1, ownNames: 8), Make("Symbol", 256, appearance: "/F1 10 Tf " + new string(' ', 4094 - 10)) };
        var path = Path.Combine(Path.GetDirectoryName(sources[0])!, "reserved.pdf");
        MergeFiles(sources, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var fonts = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Font"]);
        Assert.Equal("/Courier", S(D(fonts.Elements["/F1_10"]).Elements["/BaseFont"]));
        Assert.Equal("/Symbol", S(D(fonts.Elements["/F1_2"]).Elements["/BaseFont"]));
        var fields = Fields(result);
        Assert.Equal(256, fields.Keys.Count(k => k.StartsWith("Symbol", StringComparison.Ordinal)));
        Assert.All(fields.Where(f => f.Key.StartsWith("Symbol", StringComparison.Ordinal)), f => Assert.StartsWith("/F1_2 10 Tf ", S(f.Value.Elements["/DA"]), StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_key_whose_first_destination_is_broken_takes_the_next_one()
    {
        var source = Source(pdf =>
        {
            var key = new PdfStringObject(pdf, new string('k', 256));
            pdf.Internals.AddObject(key);
            NameTree(pdf).Elements["/Names"] = Items(pdf, key.Reference!, new PdfInteger(42), key.Reference!, Dest(pdf, 0));
            Link(pdf, 1, key.Reference);
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { new string('k', 256) }, Destinations(result).Keys);
        Assert.True(Assert.Single(Links(result, 1)).Elements.ContainsKey("/Dest"));
    }

    [Fact]
    public void A_shared_name_tree_array_is_read_again_from_a_shallower_node_and_per_role()
    {
        var source = Source(pdf =>
        {
            var leaf = Object(pdf);
            leaf.Elements["/Names"] = Items(pdf, new PdfString("ok"), Dest(pdf, 0));
            var shared = Items(pdf, leaf.Reference!);
            pdf.Internals.AddObject(shared);
            var deep = Object(pdf);
            deep.Elements["/Kids"] = shared.Reference;
            var a1 = Object(pdf);
            a1.Elements["/Kids"] = Items(pdf, deep.Reference!);
            var b = Object(pdf);
            b.Elements["/Kids"] = shared.Reference;
            // One array as an odd-length /Names and as /Kids: the broken role must not hide the other.
            var other = Object(pdf);
            other.Elements["/Names"] = Items(pdf, new PdfString("ok2"), Dest(pdf, 1));
            var both = Items(pdf, other.Reference!);
            pdf.Internals.AddObject(both);
            var c = Object(pdf);
            c.Elements["/Names"] = both.Reference;
            c.Elements["/Kids"] = both.Reference;
            NameTree(pdf).Elements["/Kids"] = Items(pdf, a1.Reference!, b.Reference!, c.Reference!);
        });
        // Depth 3: the leaf is cut below a1 (depth 4) but fits below b (depth 3).
        using var result = PdfReader.Open(MergeOne(source, new PdfMergeStructure.Limits { NameTreeDepth = 3 }), PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { "ok", "ok2" }, Destinations(result).Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void A_fields_own_dictionary_shared_by_two_kinds_reserves_names_for_both()
    {
        PdfDictionary State(PdfDocument pdf, double alpha)
        {
            var state = Object(pdf);
            state.Elements["/ca"] = new PdfReal(alpha);
            return state;
        }
        string Make(string field, double alpha, bool own) => Source(pdf =>
        {
            var form = Form(pdf);
            var states = new PdfDictionary(pdf);
            states.Elements["/R1"] = State(pdf, alpha).Reference;
            var resources = new PdfDictionary(pdf);
            resources.Elements["/ExtGState"] = states;
            form.Elements["/DR"] = resources;
            var widget = Field(pdf, form, field, "v");
            if (!own)
                return;
            widget.Elements["/DA"] = new PdfString("0 g /R1 gs");
            var shared = Object(pdf);
            shared.Elements["/R1"] = states.Elements["/R1"];
            shared.Elements["/R1_2"] = State(pdf, 0.9).Reference;
            var ownResources = new PdfDictionary(pdf);
            ownResources.Elements["/Font"] = shared.Reference;
            ownResources.Elements["/ExtGState"] = shared.Reference;
            widget.Elements["/DR"] = ownResources;
        });
        var sources = new[] { Make("a", 0.1, false), Make("b", 0.4, true) };
        var path = Path.Combine(Path.GetDirectoryName(sources[0])!, "kinds.pdf");
        MergeFiles(sources, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var field = Fields(result)["b"];
        Assert.Equal("0 g /R1_3 gs", S(field.Elements["/DA"]));
        double Alpha(PdfItem? state) => Assert.IsType<PdfReal>(D(state).Elements["/ca"]).Value;
        Assert.Equal(0.4, Alpha(D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/ExtGState"]).Elements["/R1_3"]));
        Assert.Equal(0.4, Alpha(D(D(field.Elements["/DR"]).Elements["/ExtGState"]).Elements["/R1_3"]));
    }

    [Fact]
    public void Built_in_colour_space_names_are_not_renamed()
    {
        string Make(string field, string appearance) => Source(pdf =>
        {
            var form = Form(pdf);
            var spaces = new PdfDictionary(pdf);
            spaces.Elements["/DeviceRGB"] = new PdfName("/DeviceGray");
            var resources = new PdfDictionary(pdf);
            resources.Elements["/ColorSpace"] = spaces;
            form.Elements["/DR"] = resources;
            Field(pdf, form, field, "v").Elements["/DA"] = new PdfString(appearance);
        });
        var sources = new[] { Make("a", "/DeviceRGB cs 0 1 0 sc"), Make("b", "/DeviceRGB cs 1 0 0 sc /DeviceRGB CS") };
        var path = Path.Combine(Path.GetDirectoryName(sources[0])!, "colour.pdf");
        MergeFiles(sources, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal("/DeviceRGB cs 1 0 0 sc /DeviceRGB CS", S(Fields(result)["b"].Elements["/DA"]));
    }

    [Fact]
    public void A_goto_link_keeps_its_next_action()
    {
        var source = Source(pdf =>
        {
            var next = new PdfDictionary(pdf);
            next.Elements["/S"] = new PdfName("/URI");
            next.Elements["/URI"] = new PdfString("https://example.org/receipt");
            var action = Object(pdf);
            action.Elements["/S"] = new PdfName("/GoTo");
            action.Elements["/D"] = Dest(pdf, 1);
            action.Elements["/Next"] = next;
            Link(pdf, 0, action: action);
            Link(pdf, 0, action: action);
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        var links = Links(result, 0);
        Assert.Equal(2, links.Count);
        // Both links still share one action object.
        var reference = Assert.IsType<PdfReference>(links[0].Elements["/A"]);
        Assert.Same(reference.Value, Assert.IsType<PdfReference>(links[1].Elements["/A"]).Value);
        var merged = D(reference);
        Assert.Equal(result.Pages[1].Reference, A(merged.Elements["/D"]).Elements[0]);
        Assert.Equal("https://example.org/receipt", S(D(merged.Elements["/Next"]).Elements["/URI"]));
    }

    [Fact]
    public void A_kid_that_inherits_its_appearance_gets_the_renamed_font_in_its_own_resources()
    {
        string Make(string font, bool group) => Source(pdf =>
        {
            var form = Form(pdf);
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/He"] = Resource(pdf, font).Reference;
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            form.Elements["/DR"] = resources;
            if (!group)
            {
                Field(pdf, form, "a", "v");
                return;
            }
            var parent = Object(pdf);
            parent.Elements["/T"] = new PdfString("group");
            parent.Elements["/DA"] = new PdfString("/He 8 Tf 0 g");
            var kid = Widget(pdf, 0, parent);
            kid.Elements["/T"] = new PdfString("name");
            kid.Elements["/FT"] = new PdfName("/Tx");
            kid.Elements["/V"] = new PdfString("B value");
            var own = new PdfDictionary(pdf);
            own.Elements["/He"] = fonts.Elements["/He"];
            var ownResources = new PdfDictionary(pdf);
            ownResources.Elements["/Font"] = own;
            kid.Elements["/DR"] = ownResources;
            parent.Elements["/Kids"] = Items(pdf, kid.Reference!);
            A(form.Elements["/Fields"]).Elements.Add(parent.Reference!);
        });
        var sources = new[] { Make("Helvetica", false), Make("Courier", true) };
        var path = Path.Combine(Path.GetDirectoryName(sources[0])!, "inherit.pdf");
        MergeFiles(sources, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var fields = Fields(result);
        Assert.Equal("/He_2 8 Tf 0 g", S(fields["group"].Elements["/DA"]));
        var own = D(D(fields["group.name"].Elements["/DR"]).Elements["/Font"]);
        Assert.Equal("/Courier", S(D(own.Elements["/He_2"]).Elements["/BaseFont"]));
        Assert.Equal("/Courier", S(D(own.Elements["/He"]).Elements["/BaseFont"]));
    }

    [Fact]
    public void An_annotation_listed_many_times_is_rewritten_once()
    {
        var source = Source(pdf =>
        {
            var action = Object(pdf);
            action.Elements["/S"] = new PdfName("/GoTo");
            action.Elements["/D"] = Dest(pdf, 1);
            var next = new PdfDictionary(pdf);
            next.Elements["/S"] = new PdfName("/URI");
            next.Elements["/URI"] = new PdfString("https://example.org/receipt");
            action.Elements["/Next"] = next;
            var link = Link(pdf, 0, action: action);
            var annotations = A(pdf.Pages[0].Elements["/Annots"]);
            annotations.Elements.Add(link.Reference!);
            annotations.Elements.Add(link.Reference!);
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        var links = Links(result, 0);
        Assert.Equal(3, links.Count);
        var action = Assert.Single(links.Select(l => Assert.IsType<PdfReference>(l.Elements["/A"]).Value).Distinct());
        Assert.Equal(result.Pages[1].Reference, A(D(action).Elements["/D"]).Elements[0]);
        Assert.Single(result.Internals.GetAllObjects().OfType<PdfDictionary>(), d => d.Elements.ContainsKey("/Next"));
    }

    [Fact]
    public void A_repeated_annotation_spends_its_destination_name_once()
    {
        var source = Source(pdf =>
        {
            NameTree(pdf).Elements["/Names"] = Items(pdf, new PdfString("abc"), Dest(pdf, 1));
            var repeated = Link(pdf, 0, new PdfString("abc"));
            var annotations = A(pdf.Pages[0].Elements["/Annots"]);
            annotations.Elements.Add(repeated.Reference!);
            annotations.Elements.Add(repeated.Reference!);
            Link(pdf, 0, new PdfString("abc"));
        });
        // 9 bytes: the registered name and one use for each of the two distinct links.
        using var result = PdfReader.Open(MergeOne(source, new PdfMergeStructure.Limits { StringBytes = 9 }), PdfDocumentOpenMode.Import);
        Assert.All(Links(result, 0), link => Assert.Equal("abc", Key(link.Elements["/Dest"]!)));
    }

    [Fact]
    public void Each_sources_default_colour_space_stays_with_its_fields()
    {
        string Make(string field, double? gamma) => Source(pdf =>
        {
            var form = Form(pdf);
            form.Elements["/DA"] = new PdfString("/F1 10 Tf /DeviceRGB cs 0.5 0.5 0.5 sc 0.1 0.2 0.3 rg");
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            if (gamma is { } value)
            {
                var parameters = new PdfDictionary(pdf);
                parameters.Elements["/WhitePoint"] = Items(pdf, new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089));
                parameters.Elements["/Gamma"] = Items(pdf, new PdfReal(value), new PdfReal(value), new PdfReal(value));
                var spaces = new PdfDictionary(pdf);
                spaces.Elements["/DefaultRGB"] = Items(pdf, new PdfName("/CalRGB"), parameters);
                resources.Elements["/ColorSpace"] = spaces;
            }
            form.Elements["/DR"] = resources;
            Field(pdf, form, field, "v");
        });
        var sources = new[] { Make("a", 1.8), Make("b", 2.2), Make("c", null) };
        var path = Path.Combine(Path.GetDirectoryName(sources[0])!, "defaults.pdf");
        MergeFiles(sources, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var spaces = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/ColorSpace"]);
        // No default space is left to recolour the device colours of another source's fields.
        Assert.Equal(new[] { "/DefaultRGB_2", "/DefaultRGB_3" }, spaces.Elements.Keys.OrderBy(k => k, StringComparer.Ordinal));
        double Gamma(string name) => Assert.IsType<PdfReal>(A(D(A(spaces.Elements[name]).Elements[1]).Elements["/Gamma"]).Elements[0]).Value;
        Assert.Equal(1.8, Gamma("/DefaultRGB_2"));
        Assert.Equal(2.2, Gamma("/DefaultRGB_3"));
        var fields = Fields(result);
        Assert.Equal("/F1 10 Tf /DefaultRGB_2 cs 0.5 0.5 0.5 sc /DefaultRGB_2 cs 0.1 0.2 0.3 sc", S(fields["a"].Elements["/DA"]));
        Assert.Equal("/F1_2 10 Tf /DefaultRGB_3 cs 0.5 0.5 0.5 sc /DefaultRGB_3 cs 0.1 0.2 0.3 sc", S(fields["b"].Elements["/DA"]));
        Assert.Equal("/F1_3 10 Tf /DeviceRGB cs 0.5 0.5 0.5 sc 0.1 0.2 0.3 rg", S(fields["c"].Elements["/DA"]));
    }

    [Fact]
    public void A_colour_space_naming_a_device_space_keeps_its_sources_default()
    {
        string Make(string field, bool withDefault) => Source(pdf =>
        {
            var form = Form(pdf);
            form.Elements["/DA"] = new PdfString("/F1 10 Tf /CS1 cs 0.5 0.5 0.5 sc");
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            var spaces = new PdfDictionary(pdf);
            spaces.Elements["/CS1"] = new PdfName("/DeviceRGB");
            spaces.Elements["/CS2"] = Items(pdf, new PdfName("/Indexed"), new PdfName("/DeviceRGB"), new PdfInteger(1), new PdfString("abcdef"));
            if (withDefault)
            {
                var parameters = new PdfDictionary(pdf);
                parameters.Elements["/WhitePoint"] = Items(pdf, new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089));
                parameters.Elements["/Gamma"] = Items(pdf, new PdfReal(2.2), new PdfReal(2.2), new PdfReal(2.2));
                spaces.Elements["/DefaultRGB"] = Items(pdf, new PdfName("/CalRGB"), parameters);
            }
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            resources.Elements["/ColorSpace"] = spaces;
            form.Elements["/DR"] = resources;
            Field(pdf, form, field, "v");
        });
        var sources = new[] { Make("a", true), Make("b", false) };
        var path = Path.Combine(Path.GetDirectoryName(sources[0])!, "aliases.pdf");
        MergeFiles(sources, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var spaces = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/ColorSpace"]);
        bool IsDefault(PdfItem? item) => V(item) is PdfArray array && S(array.Elements[0]) == "/CalRGB";
        Assert.True(IsDefault(spaces.Elements["/CS1"]));
        Assert.True(IsDefault(A(spaces.Elements["/CS2"]).Elements[1]));
        Assert.Equal("/DeviceRGB", S(spaces.Elements["/CS1_2"]));
        Assert.Equal("/DeviceRGB", S(A(spaces.Elements["/CS2_2"]).Elements[1]));
        Assert.Equal("/F1_2 10 Tf /CS1_2 cs 0.5 0.5 0.5 sc", S(Fields(result)["b"].Elements["/DA"]));
    }

    private static PdfArray CalRgb(PdfDocument pdf)
    {
        var parameters = new PdfDictionary(pdf);
        parameters.Elements["/WhitePoint"] = Items(pdf, new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089));
        parameters.Elements["/Gamma"] = Items(pdf, new PdfReal(2.2), new PdfReal(2.2), new PdfReal(2.2));
        return Items(pdf, new PdfName("/CalRGB"), parameters);
    }

    private static bool IsCalRgb(PdfItem? item) => V(item) is PdfArray array && S(array.Elements[0]) == "/CalRGB";

    [Fact]
    public void A_pattern_over_an_indexed_space_keeps_its_sources_default()
    {
        string Make(string field, bool withDefault) => Source(pdf =>
        {
            var form = Form(pdf);
            form.Elements["/DA"] = new PdfString("/F1 10 Tf /CS1 cs 0 /P1 scn");
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            var spaces = new PdfDictionary(pdf);
            spaces.Elements["/CS1"] = Items(pdf, new PdfName("/Pattern"),
                Items(pdf, new PdfName("/Indexed"), new PdfName("/DeviceRGB"), new PdfInteger(0), new PdfString("abc")));
            if (withDefault)
            {
                var parameters = new PdfDictionary(pdf);
                parameters.Elements["/WhitePoint"] = Items(pdf, new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089));
                parameters.Elements["/Gamma"] = Items(pdf, new PdfReal(2.2), new PdfReal(2.2), new PdfReal(2.2));
                spaces.Elements["/DefaultRGB"] = Items(pdf, new PdfName("/CalRGB"), parameters);
            }
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            resources.Elements["/ColorSpace"] = spaces;
            form.Elements["/DR"] = resources;
            Field(pdf, form, field, "v");
        });
        var sources = new[] { Make("a", true), Make("b", false) };
        var path = Path.Combine(Path.GetDirectoryName(sources[0])!, "indexed-pattern.pdf");
        MergeFiles(sources, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var spaces = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/ColorSpace"]);
        PdfItem? Base(string name) => A(A(spaces.Elements[name]).Elements[1]).Elements[1];
        Assert.True(V(Base("/CS1")) is PdfArray array && S(array.Elements[0]) == "/CalRGB");
        Assert.Equal("/DeviceRGB", S(Base("/CS1_2")));
    }

    [Fact]
    public void A_shading_shared_with_a_tiling_pattern_keeps_the_tiling_patterns_default()
    {
        PdfArray CalRgb(PdfDocument pdf, double gamma)
        {
            var parameters = new PdfDictionary(pdf);
            parameters.Elements["/WhitePoint"] = Items(pdf, new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089));
            parameters.Elements["/Gamma"] = Items(pdf, new PdfReal(gamma), new PdfReal(gamma), new PdfReal(gamma));
            return Items(pdf, new PdfName("/CalRGB"), parameters);
        }
        var source = Source(pdf =>
        {
            var form = Form(pdf);
            form.Elements["/DA"] = new PdfString("/F1 10 Tf /Pattern cs /P1 scn");
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            var shading = Object(pdf);
            shading.Elements["/ShadingType"] = new PdfInteger(2);
            shading.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
            var tileSpaces = new PdfDictionary(pdf);
            tileSpaces.Elements["/DefaultRGB"] = CalRgb(pdf, 1.8);
            var shadings = new PdfDictionary(pdf);
            shadings.Elements["/S1"] = shading.Reference;
            var tileResources = new PdfDictionary(pdf);
            tileResources.Elements["/ColorSpace"] = tileSpaces;
            tileResources.Elements["/Shading"] = shadings;
            var tile = Object(pdf);
            tile.Elements["/PatternType"] = new PdfInteger(1);
            tile.Elements["/Resources"] = tileResources;
            var gradient = Object(pdf);
            gradient.Elements["/PatternType"] = new PdfInteger(2);
            gradient.Elements["/Shading"] = shading.Reference;
            var patterns = new PdfDictionary(pdf);
            patterns.Elements["/P1"] = tile.Reference;
            patterns.Elements["/P2"] = gradient.Reference;
            var spaces = new PdfDictionary(pdf);
            spaces.Elements["/DefaultRGB"] = CalRgb(pdf, 2.2);
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            resources.Elements["/ColorSpace"] = spaces;
            resources.Elements["/Pattern"] = patterns;
            form.Elements["/DR"] = resources;
            Field(pdf, form, "f", "v");
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        var patterns = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Pattern"]);
        var formSpace = V(D(D(patterns.Elements["/P2"]).Elements["/Shading"]).Elements["/ColorSpace"]);
        Assert.True(formSpace is PdfArray array && S(array.Elements[0]) == "/CalRGB");
        var tileShading = D(D(D(D(patterns.Elements["/P1"]).Elements["/Resources"]).Elements["/Shading"]).Elements["/S1"]);
        Assert.Equal("/DeviceRGB", S(tileShading.Elements["/ColorSpace"]));
    }

    private string DefaultPatternSource(Action<PdfDocument, PdfDictionary> addPatterns) => Source(pdf =>
    {
        var form = Form(pdf);
        form.Elements["/DA"] = new PdfString("/F1 10 Tf /Pattern cs /P1 scn");
        var fonts = new PdfDictionary(pdf);
        fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
        var parameters = new PdfDictionary(pdf);
        parameters.Elements["/WhitePoint"] = Items(pdf, new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089));
        parameters.Elements["/Gamma"] = Items(pdf, new PdfReal(2.2), new PdfReal(2.2), new PdfReal(2.2));
        var spaces = new PdfDictionary(pdf);
        spaces.Elements["/DefaultRGB"] = Items(pdf, new PdfName("/CalRGB"), parameters);
        var patterns = new PdfDictionary(pdf);
        addPatterns(pdf, patterns);
        var resources = new PdfDictionary(pdf);
        resources.Elements["/Font"] = fonts;
        resources.Elements["/ColorSpace"] = spaces;
        resources.Elements["/Pattern"] = patterns;
        form.Elements["/DR"] = resources;
        Field(pdf, form, "f", "v");
    });

    [Fact]
    public void A_mesh_shading_keeps_its_data_when_its_default_is_kept()
    {
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var source = DefaultPatternSource((pdf, patterns) =>
        {
            var shading = Object(pdf);
            shading.Elements["/ShadingType"] = new PdfInteger(4);
            shading.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
            shading.CreateStream(data);
            var pattern = Object(pdf);
            pattern.Elements["/PatternType"] = new PdfInteger(2);
            pattern.Elements["/Shading"] = shading.Reference;
            patterns.Elements["/P1"] = pattern.Reference;
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        var patterns = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Pattern"]);
        var shading = D(D(patterns.Elements["/P1"]).Elements["/Shading"]);
        Assert.True(IsCalRgb(shading.Elements["/ColorSpace"]));
        Assert.Equal(data, shading.Stream!.UnfilteredValue);
    }

    [Fact]
    public void A_dictionary_used_as_pattern_and_shading_keeps_both_defaults()
    {
        var source = DefaultPatternSource((pdf, patterns) =>
        {
            var inner = Object(pdf);
            inner.Elements["/ShadingType"] = new PdfInteger(2);
            inner.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
            var both = Object(pdf);
            both.Elements["/PatternType"] = new PdfInteger(2);
            both.Elements["/ShadingType"] = new PdfInteger(2);
            both.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
            both.Elements["/Shading"] = inner.Reference;
            var outer = Object(pdf);
            outer.Elements["/PatternType"] = new PdfInteger(2);
            outer.Elements["/Shading"] = both.Reference;
            patterns.Elements["/P1"] = both.Reference;
            patterns.Elements["/P2"] = outer.Reference;
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        var patterns = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Pattern"]);
        Assert.True(IsCalRgb(D(D(patterns.Elements["/P1"]).Elements["/Shading"]).Elements["/ColorSpace"]));
        Assert.True(IsCalRgb(D(D(patterns.Elements["/P2"]).Elements["/Shading"]).Elements["/ColorSpace"]));
    }

    [Fact]
    public void A_pattern_over_an_indexed_separation_keeps_its_sources_default()
    {
        var source = Source(pdf =>
        {
            var form = Form(pdf);
            form.Elements["/DA"] = new PdfString("/F1 10 Tf /CS1 cs 0 /P1 scn");
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            var parameters = new PdfDictionary(pdf);
            parameters.Elements["/WhitePoint"] = Items(pdf, new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089));
            parameters.Elements["/Gamma"] = Items(pdf, new PdfReal(2.2), new PdfReal(2.2), new PdfReal(2.2));
            var spaces = new PdfDictionary(pdf);
            spaces.Elements["/DefaultRGB"] = Items(pdf, new PdfName("/CalRGB"), parameters);
            var separation = Items(pdf, new PdfName("/Separation"), new PdfName("/Spot"), new PdfName("/DeviceRGB"), Object(pdf).Reference!);
            spaces.Elements["/CS1"] = Items(pdf, new PdfName("/Pattern"),
                Items(pdf, new PdfName("/Indexed"), separation, new PdfInteger(0), new PdfString("a")));
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            resources.Elements["/ColorSpace"] = spaces;
            form.Elements["/DR"] = resources;
            Field(pdf, form, "f", "v");
        });
        using var result = PdfReader.Open(MergeOne(source), PdfDocumentOpenMode.Import);
        var spaces = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/ColorSpace"]);
        var indexed = A(A(spaces.Elements["/CS1"]).Elements[1]);
        Assert.True(IsCalRgb(A(indexed.Elements[1]).Elements[2]));
    }

    [Fact]
    public void A_shading_pattern_keeps_its_sources_default_colour_space()
    {
        string Make(string field, bool withDefault) => Source(pdf =>
        {
            var form = Form(pdf);
            form.Elements["/DA"] = new PdfString("/F1 10 Tf /Pattern cs /P1 scn");
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            var shading = new PdfDictionary(pdf);
            shading.Elements["/ShadingType"] = new PdfInteger(2);
            shading.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
            var pattern = new PdfDictionary(pdf);
            pattern.Elements["/Type"] = new PdfName("/Pattern");
            pattern.Elements["/PatternType"] = new PdfInteger(2);
            pattern.Elements["/Shading"] = shading;
            var patterns = new PdfDictionary(pdf);
            patterns.Elements["/P1"] = pattern;
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            resources.Elements["/Pattern"] = patterns;
            if (withDefault)
            {
                var spaces = new PdfDictionary(pdf);
                spaces.Elements["/DefaultRGB"] = CalRgb(pdf);
                resources.Elements["/ColorSpace"] = spaces;
            }
            form.Elements["/DR"] = resources;
            Field(pdf, form, field, "v");
        });
        var sources = new[] { Make("a", true), Make("b", false) };
        var path = Path.Combine(Path.GetDirectoryName(sources[0])!, "shading.pdf");
        MergeFiles(sources, path, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var patterns = D(D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]).Elements["/Pattern"]);
        Assert.True(IsCalRgb(D(D(patterns.Elements["/P1"]).Elements["/Shading"]).Elements["/ColorSpace"]));
        Assert.Equal("/DeviceRGB", S(D(D(patterns.Elements["/P1_2"]).Elements["/Shading"]).Elements["/ColorSpace"]));
    }

    [Fact]
    public void Carried_colour_spaces_keep_their_default_when_a_later_resource_is_over_the_limit()
    {
        var source = Source(pdf =>
        {
            var form = Form(pdf);
            form.Elements["/DA"] = new PdfString("/F1 10 Tf /CS1 cs 0.5 0.5 0.5 sc");
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/F1"] = Resource(pdf, "Helvetica").Reference;
            var spaces = new PdfDictionary(pdf);
            spaces.Elements["/CS1"] = new PdfName("/DeviceRGB");
            spaces.Elements["/DefaultRGB"] = CalRgb(pdf);
            var patterns = new PdfDictionary(pdf);
            patterns.Elements["/Unused"] = new PdfDictionary(pdf);
            var resources = new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            resources.Elements["/ColorSpace"] = spaces;
            resources.Elements["/Pattern"] = patterns;
            form.Elements["/DR"] = resources;
            Field(pdf, form, "f", "v");
        });
        // 5 objects: the font and the four of the CalRGB space; the unused pattern is the sixth.
        using var result = PdfReader.Open(MergeOne(source, new PdfMergeStructure.Limits { ResourceObjects = 5 }), PdfDocumentOpenMode.Import);
        var resources = D(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/DR"]);
        Assert.False(resources.Elements.ContainsKey("/Pattern"));
        Assert.True(IsCalRgb(D(resources.Elements["/ColorSpace"]).Elements["/CS1"]));
    }

    [Fact]
    public void Outline_and_link_destinations_keep_their_view()
    {
        using var document = PdfReader.Open(Merge(), PdfDocumentOpenMode.Import);
        AssertBookmarks(document);
        AssertLinks(document);
    }

    [Fact]
    public void Fields_from_two_sources_change_independently() => ChangeIndependently(Merge());

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(false, false, false)]
    [InlineData(null, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, null, true)]
    public void Need_appearances_is_on_when_any_source_asks_for_it(bool? first, bool? second, bool expected)
    {
        var folder = fx.NewFolder();
        var output = Path.Combine(folder, "merged.pdf");
        MergeFiles([Make(folder, "a", 0, first), Make(folder, "b", 1, second)], output, TestContext.Current.CancellationToken);
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(expected, V(D(result.Internals.Catalog.Elements["/AcroForm"]).Elements["/NeedAppearances"]) is PdfBoolean { Value: true });
    }

    [Fact]
    public void Cancelling_while_carrying_structure_stops_the_merge()
    {
        var folder = fx.NewFolder();
        var source = Make(folder, "a");
        var output = Path.Combine(folder, "cancelled.pdf");
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => MergeFiles([source], output, cts.Token, new() { BeforeCarry = cts.Cancel }));
        Assert.False(File.Exists(output));
    }
}
