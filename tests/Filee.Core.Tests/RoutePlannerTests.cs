using Filee.Core.Conversion;

namespace Filee.Core.Tests;

public class RoutePlannerTests
{
    [Fact]
    public void Direct_edge_is_used()
    {
        var magick = new FakeConverter("magick", new ConversionEdge("jpg", "png"));
        var route = new RoutePlanner([magick]).Plan("jpg", "png");

        Assert.NotNull(route);
        Assert.Single(route.Steps);
        Assert.Same(magick, route.Steps[0].Converter);
    }

    [Fact]
    public void Two_step_route_is_found()
    {
        var rhwp = new FakeConverter("rhwp", new ConversionEdge("hwpx", "pdf"));
        var pdfium = new FakeConverter("pdfium", new ConversionEdge("pdf", "png"));

        var route = new RoutePlanner([rhwp, pdfium]).Plan("hwpx", "png");

        Assert.NotNull(route);
        Assert.Equal(["hwpx→pdf", "pdf→png"], route.Steps.Select(s => $"{s.From}→{s.To}"));
    }

    [Fact]
    public void Cheaper_route_wins()
    {
        var slow = new FakeConverter("slow", new ConversionEdge("docx", "pdf", 30));
        var fast = new FakeConverter("fast", new ConversionEdge("docx", "pdf", 10));

        var route = new RoutePlanner([slow, fast]).Plan("docx", "pdf");

        Assert.Equal("fast", route!.Steps[0].Converter.Id);
    }

    [Fact]
    public void Priority_breaks_ties()
    {
        var word = new FakeConverter("word", new ConversionEdge("docx", "pdf"));
        var libre = new FakeConverter("libreoffice", new ConversionEdge("docx", "pdf"));

        var route = new RoutePlanner([word, libre], c => c.Id == "word" ? 5 : 0).Plan("docx", "pdf");

        Assert.Equal("libreoffice", route!.Steps[0].Converter.Id);
    }

    [Fact]
    public void Same_format_requires_self_edge()
    {
        var withSelf = new FakeConverter("magick", new ConversionEdge("jpg", "jpg"));
        var withoutSelf = new FakeConverter("other", new ConversionEdge("jpg", "png"), new ConversionEdge("png", "jpg"));

        Assert.NotNull(new RoutePlanner([withSelf]).Plan("jpg", "jpg"));
        Assert.Null(new RoutePlanner([withoutSelf]).Plan("jpg", "jpg"));
    }

    [Fact]
    public void Impossible_conversion_returns_null()
    {
        var magick = new FakeConverter("magick", new ConversionEdge("jpg", "png"));
        Assert.Null(new RoutePlanner([magick]).Plan("docx", "png"));
    }

    [Fact]
    public void Routes_longer_than_max_steps_are_rejected()
    {
        var chain = new FakeConverter("chain",
            new ConversionEdge("a", "b"), new ConversionEdge("b", "c"),
            new ConversionEdge("c", "d"), new ConversionEdge("d", "e"));

        Assert.NotNull(new RoutePlanner([chain]).Plan("a", "d"));
        Assert.Null(new RoutePlanner([chain]).Plan("a", "e"));
    }

    [Fact]
    public void Catalog_excludes_unavailable_engines()
    {
        var missing = new FakeConverter("missing", new ConversionEdge("docx", "hwpx")) { Available = false };
        var catalog = new ConverterCatalog([missing]);

        Assert.False(catalog.CreatePlanner().CanConvert("docx", "hwpx"));
    }

    [Fact]
    public void A_planner_can_assume_an_optional_engine_is_installed()
    {
        var office = new FakeConverter("office", new ConversionEdge("xlsx", "pdf")) { Available = false };
        var images = new FakeConverter("images", new ConversionEdge("pdf", "png"));
        var catalog = new ConverterCatalog([office, images]);

        Assert.False(catalog.CreatePlanner().CanConvert("xlsx", "png"));
        Assert.True(catalog.CreatePlanner(["office"]).CanConvert("xlsx", "png"));
    }
}
