// Mutation fuzzing of the PML and OEB e-book readers, next to the other legacy formats in DocumentFuzzTests: the PML
// seed is changed as text with PML's own codes, the OEB seed (an .oeb package) part by part as text, or as bytes.
// Every input must be read or refused with InvalidDataException / ConversionRefusedException, in time. Run by
// FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to
// repeat, FILEE_FUZZ_OUT to keep failing inputs.

using System.IO.Compression;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class EbookFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 60;

    private static readonly string[] PmlTokens =
    [
        @"\i", @"\u", @"\o", @"\B", @"\b", @"\l", @"\k", @"\Sp", @"\Sb", @"\x", @"\X0", @"\X4", @"\X9", @"\C1=""a""", @"\c", @"\r",
        @"\t", @"\T=""50%""", @"\T=""999%""", @"\w=""30%""", @"\v", @"\n", @"\s", @"\p", @"\m=""pic.png""", @"\m=""../../x.png""",
        @"\m=""C:\x.png""", @"\q=""#a""", @"\q", @"\Q=""a""", @"\Fn=""n1""", @"\Fn", @"\Sd=""s1""", @"\Sd", @"\a169", @"\a999",
        @"\a", @"\U263A", @"\UD800", @"\U0000", @"\\", @"\-", @"\", "=\"", "<footnote id=\"n1\">", "</footnote>",
        "<sidebar id=\"s1\">", "</sidebar>", "<footnote", "\r\n",
    ];

    private static readonly string[] OebTokens =
    [
        "<item id=\"x\" href=\"x.html\" media-type=\"text/x-oeb1-document\"/>", "<itemref idref=\"x\"/>", "href=\"../../secret.html\"",
        "href=\"/etc/passwd\"", "href=\"file:///c:/x\"", "href=\"\"", "<spine>", "</spine>", "<manifest>", "<dc:Title>", "<p>",
        "</p>", "<img src=\"../pic.png\"/>", "<img src=\"http://example.invalid/a.png\"/>", "<a href=\"#end\">", "<table><tr><td>",
        "<!DOCTYPE x [<!ENTITY a \"aaaa\">]>", "&a;", "&nbsp;", "<![CDATA[", "]]>",
    ];

    [Fact]
    public void PmlReader_reads_or_refuses_every_mutated_book() =>
        Fuzz("pml", "pml", [File.ReadAllBytes(LegacyEbookBuilders.Pml(fx.NewFolder()))], Book(PmlReader.Read),
            (seed, random) => new TextFuzzer(seed, random, PmlTokens).Next);

    [Fact]
    public void OebReader_reads_or_refuses_every_mutated_package() =>
        Fuzz("oeb", "oeb", [File.ReadAllBytes(LegacyEbookBuilders.OebZip(fx.NewFolder()))], Book(OebReader.Read), PackageMutator);

    /// <summary>
    /// Mutates an .oeb: mostly one of its text parts (the package or a page) as text, sometimes the ZIP's bytes.
    /// </summary>
    private static Func<byte[]> PackageMutator(byte[] seed, Random random)
    {
        var parts = new List<(string Name, byte[] Bytes)>();
        using (var zip = new ZipArchive(new MemoryStream(seed), ZipArchiveMode.Read))
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                parts.Add((entry.FullName, copy.ToArray()));
            }
        var text = parts.Select((p, i) => (Index: i, p.Name)).Where(p => p.Name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase)
                                                                   || p.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Index, p => new TextFuzzer(parts[p.Index].Bytes, random, OebTokens));
        var bytes = new BinaryFuzzer(seed, random);
        return () =>
        {
            if (random.Next(5) == 0)
                return bytes.Next();
            var changed = parts.ToList();
            var index = text.Keys.ElementAt(random.Next(text.Count));
            changed[index] = (changed[index].Name, text[index].Next());
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, data) in changed)
                {
                    using var stream = zip.CreateEntry(name).Open();
                    stream.Write(data);
                }
            return output.ToArray();
        };
    }

    /// <summary>An e-book reader; a refusal for DRM counts as a refusal.</summary>
    private static Action<string, string> Book(Func<string, string, HDocument> read) => (path, media) =>
    {
        try
        {
            read(path, media);
        }
        catch (InvalidOperationException ex) when (ex.Message is EbookFiles.DrmMessage or PdbReader.EreaderMessage)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    };

    private void Fuzz(string name, string extension, List<byte[]> seeds, Action<string, string> read, Func<byte[], Random, Func<byte[]>> mutator) =>
        new FuzzHarness(fx, QuickIterations, 20261114).Files(name, extension, seeds, mutator, read);
}
