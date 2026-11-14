// Mutation fuzzing of the markup readers on the shapes that cost the most: grid and simple tables, footnotes
// referenced many times and from each other, substitutions, drawers and templates. DocumentFuzzTests fuzzes each
// reader from a seed of ordinary text; these seeds start from the hostile structures instead, and TextFuzzer repeats
// their pieces. Every input must be read or refused (InvalidDataException / ConversionRefusedException) in time,
// models included: a note left inside itself would make the walk below run forever. Run by FuzzHarness:
// FILEE_FUZZ_SECONDS runs longer with a new random seed, FILEE_FUZZ_SEED repeats a run.

using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class MarkupFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 40;

    private const string RstSeed = """
        Title
        =====

        Text [1]_ and [#]_ and [1]_ with |s| and `link`_.

        +-----+-----+
        | a   | b   |
        +=====+=====+
        | c         |
        +-----+-----+

        ===  ===
        x    y
        ===  ===
        z    w
          more
        ===  ===

        | line
          goes on

        .. [1] Note one [2]_ refers on.
        .. [2] Note two [1]_ refers back.
        .. [#] Auto [1]_.
        .. |s| replace:: sub text
        .. _link: http://example.invalid/
           path
        """;

    private const string OrgSeed = """
        #+TITLE: One
        #+TITLE: Two
        * Head   :tag:
        :PROPERTIES:
        :A: 1
        :END:
        Text [fn:1] and [fn:a:inline [fn:1]] and [fn:1].

        | a | b |
        |---+---|
        | c | d |

        [fn:1] Note [fn:2] on.
        [fn:2] Back [fn:1].
        """;

    private const string TextileSeed = """
        h1. Title

        Text[1] and[2] and[1].

        |_. a |_. b |
        |\2. wide |
        |/2. tall | x |
        | y |

        fn1. Note[2] on.

        fn2. Back[1].
        """;

    private const string WikiSeed = """
        == Title ==
        Text<ref name="a">Note <ref name="b">inner</ref></ref> and <ref name="a" /> and <ref name="b" />.
        {|
        ! a !! b
        |-
        | rowspan="3" colspan="2" | c || d
        |-
        | e
        |}
        {{template|x}}
        """;

    [Fact]
    public void RstReader_reads_or_refuses_every_mutated_hostile_document() =>
        Fuzz("rst", RstSeed, text => RstReader.Read(text, fx.NewFolder(), DocumentLimits.Default),
            ["[1]_ ", "[2]_ ", "[#]_ ", ".. [1] ", ".. [2] [1]_ ", "|s| ", "+-----+", "+-+", "| a |", "+=====+", "===  ===\n", "  ", "| ",
             ".. _t: u\n   v\n", "\n"]);

    [Fact]
    public void OrgReader_reads_or_refuses_every_mutated_hostile_file() =>
        Fuzz("org", OrgSeed, text => OrgReader.Read(text, fx.NewFolder(), DocumentLimits.Default),
            ["[fn:1] ", "[fn:2]", "[fn:a:x [fn:a]]", "[fn:1] [fn:1]\n", ":A:\n", ":END:\n", "#+TITLE: x\n", "| a |", "|---|\n", "* ", "\n"]);

    [Fact]
    public void TextileReader_reads_or_refuses_every_mutated_hostile_page() =>
        Fuzz("textile", TextileSeed, text => TextileReader.Read(text, fx.NewFolder(), DocumentLimits.Default),
            ["[1]", "[2]", "fn1. [2]", "fn2. [1]", "|", "|\\2. ", "|/9999. ", "|_. ", "\n"]);

    [Fact]
    public void MediaWikiReader_reads_or_refuses_every_mutated_hostile_page() =>
        Fuzz("mediawiki", WikiSeed, text => MediaWikiReader.Read(text, fx.NewFolder(), DocumentLimits.Default),
            ["<ref name=\"a\" />", "<ref name=\"b\" />", "<ref name=\"a\">", "</ref>", "||", "!!", "| rowspan=\"9999\" colspan=\"99999\" |", "{|\n",
             "|}\n", "|-\n", "{{", "}}", "\n"]);

    /// <summary>Fuzzes <paramref name="read"/> from <paramref name="seed"/>; each model it reads is walked to its end.</summary>
    private void Fuzz(string name, string seed, Func<string, HDocument> read, string[] tokens) =>
        new FuzzHarness(fx, QuickIterations, 20261114).Data(name, "txt", [Encoding.UTF8.GetBytes(seed)], FuzzHarness.Text(tokens),
            bytes => _ = HDocumentWalker.Inlines(read(Encoding.UTF8.GetString(bytes)).Sections.SelectMany(section => section.Blocks)).Count());
}
