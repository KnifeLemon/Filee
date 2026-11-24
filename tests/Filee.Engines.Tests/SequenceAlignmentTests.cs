// NEXUS, FASTA and PHYLIP biological sequence alignments through the built-in data engine (#1798): sequential and
// interleaved layouts, nested comments, quoted names, MATCHCHAR expansion, datatype guessing, strict/relaxed PHYLIP,
// refusals and atomic output preservation. Inputs are written in the test.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

public class SequenceAlignmentTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string Write(string name, string text)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private async Task<string> ConvertTextAsync(string name, string text, string target)
    {
        var job = await fx.ConvertAsync([Write(name, text)], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = job.Outputs.Single();
        var bytes = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "UTF-8 without BOM");
        Assert.DoesNotContain("\r", Encoding.UTF8.GetString(bytes));
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<(string? ErrorKey, string ErrorDetail)> FailAsync(string name, string text, string target)
    {
        var job = await fx.ConvertAsync([Write(name, text)], new Preset { TargetFormat = target });
        Assert.Equal(JobState.Failed, job.State);
        var file = job.Files.Single();
        return (file.ErrorKey, file.ErrorDetail ?? "");
    }

    [Fact]
    public void Planner_provides_all_six_direct_edges_through_the_data_engine()
    {
        var planner = fx.Catalog.CreatePlanner();
        string[] formats = ["nex", "fasta", "phy"];

        foreach (var from in formats)
            foreach (var to in formats)
            {
                if (from == to)
                    continue;
                var route = planner.Plan(from, to);
                Assert.NotNull(route);
                var step = Assert.Single(route.Steps);
                Assert.Equal("data", step.Converter.Id);
            }
    }

    [Fact]
    public async Task Nexus_sequential_with_comments_quotes_underscores_and_matchchar_converts_to_fasta_and_phylip()
    {
        const string Nexus = """
            #NEXUS
            [Top-level [nested [deep]] comment]
            begin data;
              dimensions ntax=3 nchar=70;
              format datatype=dna missing=? gap=- matchchar=.;
              matrix
                [Comment inside matrix]
                Homo_sapiens           ACGTACGTAC [inline comment] GTACGTACGT ACGTACGTAC GTACGTACGT ACGTACGTAC GTACGTACGT ACGTACGTAC
                'Pan''s_troglodytes'   .......... .......... .......... .......... .......... .......... .........G
                Gorilla_gorilla        ....T..... .......... [nested [comment]] .......... .......... .......... .......... ..........
              ;
            end;
            """;

        var fasta = await ConvertTextAsync("primates.nex", Nexus, "fasta");
        Assert.Equal(
            ">Homo sapiens\n"
            + "ACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGT\n"
            + "ACGTACGTAC\n"
            + ">Pan's_troglodytes\n"
            + "ACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGT\n"
            + "ACGTACGTAG\n"
            + ">Gorilla gorilla\n"
            + "ACGTTCGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGT\n"
            + "ACGTACGTAC\n",
            fasta);

        var phylip = await ConvertTextAsync("primates.nxs", Nexus, "phy");
        Assert.Equal(
            "3 70\n"
            + "Homo_sapiens  ACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTAC\n"
            + "Pan's_troglodytes  ACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTAG\n"
            + "Gorilla_gorilla  ACGTTCGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTACGTAC\n",
            phylip);
    }

    [Fact]
    public async Task Nexus_interleaved_characters_block_converts_to_fasta_and_phylip()
    {
        const string Nexus = """
            #nexus
            begin taxa;
              dimensions ntax=2;
              taxlabels Alpha_sp 'Beta''s sp';
            end;

            begin characters;
              dimensions ntax=2 nchar=10;
              format datatype=dna matchchar=. interleave=yes;
              matrix
                Alpha_sp    ACGTA [block 1]
                'Beta''s sp' ....G

                Alpha_sp    TGCAT
                'Beta''s sp' ..A..
              ;
            end;
            """;

        var fasta = await ConvertTextAsync("interleaved.nexus", Nexus, "fasta");
        Assert.Equal(
            ">Alpha sp\nACGTATGCAT\n>Beta's sp\nACGTGTGAAT\n",
            fasta);

        var phylip = await ConvertTextAsync("interleaved.nex", Nexus, "phy");
        Assert.Equal(
            "2 10\nAlpha_sp  ACGTATGCAT\nBeta's_sp  ACGTGTGAAT\n",
            phylip);
    }

    [Fact]
    public async Task Fasta_multiline_converts_to_phylip_and_nexus_with_datatype_guessing_and_quoting()
    {
        const string DnaFasta = """
            ; FASTA comment line ignored
            >Homo sapiens
            ACGT ACGT
            NN--??RY
            >Pan's_1
            TGCA TGCA
            ACGTACGT
            """;

        var phylip = await ConvertTextAsync("dna.fa", DnaFasta, "phy");
        Assert.Equal(
            "2 16\nHomo_sapiens  ACGTACGTNN--??RY\nPan's_1  TGCATGCAACGTACGT\n",
            phylip);

        var nexus = await ConvertTextAsync("dna.fasta", DnaFasta, "nex");
        Assert.Equal(
            "#NEXUS\n"
            + "BEGIN DATA;\n"
            + "  DIMENSIONS NTAX=2 NCHAR=16;\n"
            + "  FORMAT DATATYPE=DNA MISSING=? GAP=-;\n"
            + "  MATRIX\n"
            + "    'Homo sapiens' ACGTACGTNN--??RY\n"
            + "    'Pan''s_1' TGCATGCAACGTACGT\n"
            + "  ;\n"
            + "END;\n",
            nexus);

        var rnaNexus = await ConvertTextAsync("rna.fna", ">Seq_1\nACGUA\n>Seq-2.a\nUGCUA\n", "nex");
        Assert.Contains("FORMAT DATATYPE=RNA MISSING=? GAP=-;\n", rnaNexus);
        Assert.Contains("    Seq_1 ACGUA\n", rnaNexus);
        Assert.Contains("    Seq-2.a UGCUA\n", rnaNexus);

        var proteinNexus = await ConvertTextAsync("prot.faa", ">ProtA\nMVLSPADKTN\n>ProtB\nMVLSGEDKSN\n", "nex");
        Assert.Contains("FORMAT DATATYPE=PROTEIN MISSING=? GAP=-;\n", proteinNexus);
    }

    [Fact]
    public async Task Unaligned_fasta_is_refused_for_phylip_and_nexus_while_aligned_roundtrips()
    {
        const string Unaligned = ">Seq1\nACGTACGT\n>Seq2\nACGT\n";

        var (phyKey, phyDetail) = await FailAsync("unaligned.fasta", Unaligned, "phy");
        Assert.Equal("error.unsupported_source", phyKey);
        Assert.Contains("equal-length", phyDetail);

        var (nexKey, nexDetail) = await FailAsync("unaligned.fasta", Unaligned, "nex");
        Assert.Equal("error.unsupported_source", nexKey);
        Assert.Contains("equal-length", nexDetail);

        // FASTA reader itself accepts unaligned sequences without throwing.
        var parsed = Filee.Engines.Data.SequenceAlignment.ReadFasta(Unaligned, TestContext.Current.CancellationToken);
        Assert.Equal(2, parsed.Sequences.Count);
        Assert.Equal("ACGTACGT", parsed.Sequences[0].Sequence);
        Assert.Equal("ACGT", parsed.Sequences[1].Sequence);
    }

    [Fact]
    public async Task Phylip_relaxed_strict_and_interleaved_convert_to_fasta_and_nexus()
    {
        const string Relaxed = """
            2 8
            Homo_sapiens_long  ACGT ACGT
            Pan_troglodytes    TGCA TGCA
            """;
        Assert.Equal(
            ">Homo_sapiens_long\nACGTACGT\n>Pan_troglodytes\nTGCATGCA\n",
            await ConvertTextAsync("relaxed.phy", Relaxed, "fasta"));

        // Strict PHYLIP: 10-character names glued directly to the sequences (with and without spaces in sequence).
        const string StrictGlued = """
             2 10
            TaxonName1ACGTACGTAC
            TaxonName2TGCATGCATG
            """;
        Assert.Equal(
            ">TaxonName1\nACGTACGTAC\n>TaxonName2\nTGCATGCATG\n",
            await ConvertTextAsync("strict.phylip", StrictGlued, "fasta"));

        const string StrictWithSpaces = """
             2 10
            TaxonName1ACGTA CGTAC
            TaxonName2TGCAT GCATG
            """;
        Assert.Equal(
            ">TaxonName1\nACGTACGTAC\n>TaxonName2\nTGCATGCATG\n",
            await ConvertTextAsync("strict-spaced.phy", StrictWithSpaces, "fasta"));

        const string Interleaved = """
             3 12
            Alpha     ACGTAC
            Beta      TGCATG
            Gamma     GGGGGG

            GTACGT
            CATGCA
            CCCCCC
            """;
        var fasta = await ConvertTextAsync("interleaved.phy", Interleaved, "fasta");
        Assert.Equal(
            ">Alpha\nACGTACGTACGT\n>Beta\nTGCATGCATGCA\n>Gamma\nGGGGGGCCCCCC\n",
            fasta);

        var nexus = await ConvertTextAsync("interleaved.phy", Interleaved, "nex");
        Assert.Contains("DIMENSIONS NTAX=3 NCHAR=12;\n", nexus);
        Assert.Contains("    Alpha ACGTACGTACGT\n", nexus);
    }

    [Fact]
    public async Task Malformed_or_unsupported_inputs_are_refused()
    {
        // No #NEXUS header
        var (key, detail) = await FailAsync("bad.nex", "BEGIN DATA;\nMATRIX\nA ACGT\n;\nEND;\n", "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("#NEXUS", detail);

        // No DATA/CHARACTERS matrix
        (key, detail) = await FailAsync("nomatrix.nex", "#NEXUS\nBEGIN TAXA;\nDIMENSIONS NTAX=1;\nTAXLABELS A;\nEND;\n", "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("MATRIX", detail);

        // Missing ';' ending matrix
        (key, detail) = await FailAsync("nosemi.nex", "#NEXUS\nBEGIN DATA;\nDIMENSIONS NTAX=1 NCHAR=4;\nMATRIX\nA ACGT\n", "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("';'", detail);

        // Unterminated comment
        (key, detail) = await FailAsync("unterm-comment.nex", "#NEXUS\n[unclosed comment\nBEGIN DATA;\nMATRIX\nA ACGT\n;\nEND;\n", "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("comment", detail, StringComparison.OrdinalIgnoreCase);

        // Deeply nested comments (hostile input)
        var deepComments = "#NEXUS\n" + new string('[', 100) + new string(']', 100) + "\nBEGIN DATA;\nMATRIX\nA ACGT\n;\nEND;\n";
        (key, detail) = await FailAsync("deep-comment.nex", deepComments, "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("nested", detail, StringComparison.OrdinalIgnoreCase);

        // Unterminated quote
        (key, detail) = await FailAsync("unterm-quote.nex", "#NEXUS\nBEGIN DATA;\nMATRIX\n'unclosed ACGT\n;\nEND;\n", "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("quote", detail, StringComparison.OrdinalIgnoreCase);

        // Polymorphism / uncertainty notation {AC} and (AC)
        (key, detail) = await FailAsync("poly-brace.nex", "#NEXUS\nBEGIN DATA;\nDIMENSIONS NTAX=1 NCHAR=4;\nMATRIX\nA AC{GT}T\n;\nEND;\n", "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("polymorphism", detail, StringComparison.OrdinalIgnoreCase);

        (key, detail) = await FailAsync("poly-paren.nex", "#NEXUS\nBEGIN DATA;\nDIMENSIONS NTAX=1 NCHAR=4;\nMATRIX\nA AC(GT)T\n;\nEND;\n", "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("polymorphism", detail, StringComparison.OrdinalIgnoreCase);

        // PHYLIP count mismatch (ntax / nchar)
        (key, detail) = await FailAsync("bad-ntax.phy", "3 4\nA ACGT\nB TGCA\n", "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("mismatch", detail, StringComparison.OrdinalIgnoreCase);

        (key, detail) = await FailAsync("bad-nchar.phy", "2 6\nA ACGT\nB TGCA\n", "fasta");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("dimensions", detail, StringComparison.OrdinalIgnoreCase);

        // FASTA with no '>' record
        (key, detail) = await FailAsync("nofasta.fa", "; just a comment\n\n", "phy");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("'>'", detail);

        // Duplicate names after sanitising for PHYLIP and NEXUS
        (key, detail) = await FailAsync("dup-phy.fa", ">Homo sapiens\nACGT\n>Homo_sapiens\nTGCA\n", "phy");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("Duplicate", detail);

        (key, detail) = await FailAsync("dup-nex.fa", ">Homo sapiens\nACGT\n>Homo_sapiens\nTGCA\n", "nex");
        Assert.Equal("error.unsupported_source", key);
        Assert.Contains("Duplicate", detail);
    }

    [Fact]
    public async Task Failed_conversion_leaves_an_existing_output_file_untouched()
    {
        var folder = fx.NewFolder();
        var input = Path.Combine(folder, "unaligned.fasta");
        var existing = Path.Combine(folder, "unaligned.phy");
        await File.WriteAllTextAsync(input, ">Seq1\nACGTACGT\n>Seq2\nACGT\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(existing, "keep existing alignment", TestContext.Current.CancellationToken);

        var preset = new Preset
        {
            TargetFormat = "phy",
            Output = { Location = OutputLocation.SameFolder, Conflict = ConflictPolicy.Overwrite },
        };
        var job = await fx.ConvertAsync([input], preset);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("keep existing alignment", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
    }
}
