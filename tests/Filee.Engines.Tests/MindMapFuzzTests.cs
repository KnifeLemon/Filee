// Mutation fuzzing of the mind map reader (Hwp/Hwpx/Markup/MindMapReader): FreeMind / Freeplane maps as XML
// (XmlFuzzer), XMind packages (DocumentFuzzer: parts changed, dropped, swapped) and XMind's content.json on its own
// (JsonFuzzer, packed into a map for each input). The seeds are MindMapTests' maps; every input must be read or refused
// with InvalidDataException / ConversionRefusedException, in time. Run by FuzzHarness: a short fixed run normally,
// FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using System.IO.Compression;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx.Markup;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class MindMapFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 50;

    [Fact]
    public void FreeMind_maps_are_read_or_refused() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Files("mm", "mm", [System.Text.Encoding.UTF8.GetBytes(MindMapTests.FreeMindMap)],
            FuzzHarness.Xml(["<node TEXT=\"x\">", "</node>", "<node/>", " LINK=\"#ID_root\"", " LINK=\"javascript:x\"", "<icon BUILTIN=\"full-1\"/>",
                "<richcontent TYPE=\"NOTE\"><html><body><p>", "</p></body></html></richcontent>", "<richcontent TYPE=\"NODE\">",
                "<attribute NAME=\"a\" VALUE=\"b\"/>", "&#10;", "&amp;", "<!DOCTYPE map [<!ENTITY a \"aaaa\">]>", "&a;", "<![CDATA[", "]]>"]),
            (path, media) => MindMapReader.ReadFreeMind(path, media));

    [Fact]
    public void XMind_packages_are_read_or_refused() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Files("xmind", "xmind", [MindMapTests.XMindZen(), MindMapTests.XMind8()], FuzzHarness.Packages,
            (path, media) => MindMapReader.ReadXMind(path, media));

    [Fact]
    public void XMind_content_json_is_read_or_refused()
    {
        using var zip = new ZipArchive(new MemoryStream(MindMapTests.XMindZen(withStub: false)));
        using var seed = new MemoryStream();
        using (var content = zip.GetEntry("content.json")!.Open())
            content.CopyTo(seed);
        var media = fx.NewFolder();
        new FuzzHarness(fx, QuickIterations, 20261120).Data("xmind-json", "json", [seed.ToArray()],
            FuzzHarness.Json(["{", "}", "[", "]", ",", ":", "\"rootTopic\"", "\"children\"", "\"attached\"", "\"detached\"", "\"title\"", "\"notes\"",
                "\"realHTML\"", "\"content\"", "\"markers\"", "\"markerId\"", "\"priority-1\"", "\"href\"", "\"xmind:#root\"", "\"labels\"", "\"image\"",
                "\"xap:resources/pic.png\"", "null", "1e999", "\"<b>x</b>\""]),
            json => MindMapReader.ReadXMind(new MemoryStream(MindMapTests.Zip(new Dictionary<string, byte[]> { ["content.json"] = json })),
                Path.GetTempPath(), media, DocumentLimits.Default));
    }
}
