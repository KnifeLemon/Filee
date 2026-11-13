// SafeXml builds the tree from an XmlReader that skips comments and processing instructions. The reader hands the text
// on either side of a skipped one over as separate text nodes, so SafeXml has to join them into one, as XDocument.Load
// does, and do it in time linear in the text however many pieces it comes in.

using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public class SafeXmlTests
{
    private const string Pieces = "<r>a<!--x-->b<?p q?>c</r>";

    [Fact]
    public void The_reader_splits_text_around_a_skipped_comment_or_processing_instruction()
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreProcessingInstructions = true };
        using var reader = XmlReader.Create(new StringReader(Pieces), settings);
        var texts = new List<string>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Text)
                texts.Add(reader.Value);
        }
        Assert.Equal(["a", "b", "c"], texts);
    }

    [Fact]
    public void Text_split_by_comments_and_processing_instructions_becomes_one_node()
    {
        var document = Load(Pieces);
        var text = Assert.IsType<XText>(Assert.Single(document.Root!.Nodes()));
        Assert.Equal("abc", text.Value);

        // CDATA stays a node of its own, as after XDocument.Load; the text on each side of it is joined as before.
        document = Load("<r>a<!---->b<![CDATA[<c>]]>d<?p?>e<s/>f</r>");
        var nodes = document.Root!.Nodes().ToList();
        Assert.Equal(5, nodes.Count);
        Assert.Equal(["ab", "<c>", "de"], nodes.Take(3).Select(n => ((XText)n).Value));
        Assert.IsType<XCData>(nodes[1]);
        Assert.IsType<XElement>(nodes[3]);
        Assert.Equal("f", ((XText)nodes[4]).Value);
    }

    [Fact]
    public void Text_in_a_million_pieces_is_joined_in_linear_time()
    {
        const int Count = 1_000_000;
        var xml = new StringBuilder("<r>", Count * 8 + 8);
        for (var i = 0; i < Count; i++)
            xml.Append("a<!---->");
        xml.Append("</r>");

        var watch = Stopwatch.StartNew();
        var document = Load(xml.ToString());
        Assert.Equal(Count, document.Root!.Value.Length);
        Assert.Single(document.Root.Nodes());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    private static XDocument Load(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return SafeXml.Load(stream, new XmlLimits(10, 100, 64L * 1024 * 1024));
    }
}
