// Routes of the markup languages Filee reads and writes itself (Text/MarkupConverter): the built-in route wins over
// Pandoc's wherever both exist, Pandoc keeps what only it does, and no other conversion takes a detour through one of
// them (a notebook or a wiki page in the middle of DOCX → HTML would lose what the format can't hold).

using Filee.Core.Formats;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Text;

namespace Filee.Engines.Tests;

public class MarkupRouteTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    public static TheoryData<string> Owned() => [.. MarkupConverter.Writers.Keys];

    [Theory]
    [MemberData(nameof(Owned))]
    public void Built_in_routes_win_over_pandoc_and_pandoc_keeps_the_rest(string markup)
    {
        Assert.False(DocumentReaders.NeedsPandoc(markup));
        foreach (var planner in new[] { fx.Catalog.CreatePlanner(["pandoc"]), MarkupTestKit.WithoutPandoc(fx).CreatePlanner() })
        {
            foreach (var (from, to, engine) in new[]
                     {
                         (markup, "md", "markup"), (markup, "html", "markup"), (markup, "txt", "markup"), ("md", markup, "markup"),
                         ("docx", markup, "markup"), ("html", markup, "markup"), (markup, "docx", "docx-writer"),
                         (markup, "hwpx", "hwpx-writer"), (markup, "odt", "odt-writer"),
                     })
                Assert.Equal(engine, Assert.Single(planner.Plan(from, to)!.Steps).Converter.Id);
        }

        // Slides come from the PPTX writer, which takes every built-in reader; what only Pandoc writes stays Pandoc's.
        var withPandoc = fx.Catalog.CreatePlanner(["pandoc"]);
        Assert.Equal("pptx-writer", Assert.Single(withPandoc.Plan(markup, "pptx")!.Steps).Converter.Id);
        Assert.Equal("pandoc", Assert.Single(withPandoc.Plan(markup, "tex")!.Steps).Converter.Id);
    }

    [Fact]
    public void No_other_route_takes_a_detour_through_them()
    {
        var owned = MarkupConverter.Writers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = FormatRegistry.Known.Where(f => f.Writable).Select(f => f.Id).ToList();
        foreach (var planner in new[] { fx.Catalog.CreatePlanner(["pandoc", "libreoffice", "calibre"]), MarkupTestKit.WithoutPandoc(fx).CreatePlanner() })
        {
            foreach (var from in FormatRegistry.Known.Select(f => f.Id))
            {
                foreach (var to in targets)
                {
                    if (from == to || planner.Plan(from, to) is not { } route)
                        continue;
                    var through = route.Steps.Take(route.Steps.Count - 1).Select(s => s.To).Where(owned.Contains).ToList();
                    Assert.True(through.Count == 0,
                        $"{from} → {to} goes through {string.Join(", ", through)}: {string.Join(" → ", route.Steps.Select(s => s.Converter.Id))}");
                }
            }
        }
    }
}
