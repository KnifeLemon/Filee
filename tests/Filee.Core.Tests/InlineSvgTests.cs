using System.Text;
using System.Xml.Linq;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public class InlineSvgTests
{
    [Theory]
    [InlineData("<svg><rect width='20' height='20'/></svg>")]
    [InlineData("<svg><rect width='20' height='20'/></svg >")]
    [InlineData(" \r\n<svg xmlns='http://www.w3.org/2000/svg'><text>A &amp; B</text></svg>\t ")]
    [InlineData("<svg><defs><path id='p' d='M0 0L10 10'/></defs><use href='#p'/></svg>")]
    public void A_single_well_formed_svg_is_accepted(string source)
    {
        var bytes = InlineSvg.Read(source);
        Assert.NotNull(bytes);
        Assert.Equal("http://www.w3.org/2000/svg", XElement.Parse(Encoding.UTF8.GetString(bytes)).Name.NamespaceName);
        InlineSvg.EnsureSelfContained(bytes);
    }

    [Theory]
    [InlineData("ordinary text")]
    [InlineData("https://example.com/image.svg")]
    [InlineData("<div><svg></svg></div>")]
    [InlineData("<svg></svg><svg></svg>")]
    [InlineData("&lt;svg&gt;&lt;/svg&gt;")]
    [InlineData("<svg><path></svg>")]
    [InlineData("<svg><text>A & B</text></svg>")]
    [InlineData("<svg><rect width='1' width='2'/></svg>")]
    [InlineData("<svg xmlns='urn:other'></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///secret'>]><svg>&x;</svg>")]
    [InlineData("<svg></svg>trailing text")]
    [InlineData("<svg></svg><!-- outside the selection -->")]
    public void Unrelated_mixed_escaped_and_almost_valid_xml_are_rejected(string source) =>
        Assert.Null(InlineSvg.Read(source));

    [Fact]
    public void Oversized_selection_is_not_parsed() =>
        Assert.Null(InlineSvg.Read("<svg>" + new string(' ', InlineSvg.MaxCharacters) + "</svg>"));

    [Theory]
    [InlineData("<image href='missing.png'/>")]
    [InlineData("<g xml:base='https://example.com/'><image href='image.png'/></g>")]
    [InlineData("<use xmlns:xlink='http://www.w3.org/1999/xlink' xlink:href='other.svg#shape'/>")]
    [InlineData("<style>@import 'missing.css';</style>")]
    [InlineData("<style>@font-face {font-family: Icon;src:url(missing.woff2)}</style>")]
    [InlineData("<rect style='fill:url(https://example.com/paint.svg#p)'/>")]
    [InlineData("<style>@\\69mport 'missing.css';</style>")]
    public void External_dependencies_are_reported_when_the_job_fetches_the_selection(string body)
    {
        var svg = InlineSvg.Read("<svg>" + body + "</svg>");
        Assert.NotNull(svg); // Valid SVG still reaches the result, with a useful failure message.
        var error = Assert.Throws<InvalidDataException>(() => InlineSvg.EnsureSelfContained(svg));
        Assert.Contains("external", error.Message);
    }

    [Fact]
    public void Html_is_not_used_as_a_fallback_for_selected_svg()
    {
        var drag = new BrowserDragContent
        {
            Texts = new Dictionary<string, string>
            {
                [BrowserDragFormats.PlainText] = "not SVG",
                [BrowserDragFormats.Html] = "<svg><rect width='20' height='20'/></svg>"
            }
        };
        Assert.Empty(BrowserDrag.Files(drag));
    }
}
