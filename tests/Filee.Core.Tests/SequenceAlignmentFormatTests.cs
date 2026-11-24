// Sequence alignment formats (NEXUS, FASTA, PHYLIP; issue #1798): extension lookup, writable data category,
// Specialist grade and Science & medical field membership.

using Filee.Core.Formats;

namespace Filee.Core.Tests;

public class SequenceAlignmentFormatTests
{
    [Theory]
    [InlineData("tree.nex", "nex")]
    [InlineData("tree.nxs", "nex")]
    [InlineData("tree.nexus", "nex")]
    [InlineData("TREE.NEX", "nex")]
    [InlineData("seqs.fasta", "fasta")]
    [InlineData("seqs.fa", "fasta")]
    [InlineData("seqs.fas", "fasta")]
    [InlineData("seqs.fna", "fasta")]
    [InlineData("seqs.faa", "fasta")]
    [InlineData("align.phy", "phy")]
    [InlineData("align.phylip", "phy")]
    public void Alignment_files_are_recognised_by_their_extension(string file, string expectedId) =>
        Assert.Equal(expectedId, FormatRegistry.Detect(file)?.Id);

    [Fact]
    public void Clustal_aln_is_not_claimed_by_fasta() =>
        Assert.Null(FormatRegistry.Detect("clustal.aln"));

    [Theory]
    [InlineData("nex", "NEXUS")]
    [InlineData("fasta", "FASTA")]
    [InlineData("phy", "PHYLIP")]
    public void Alignment_formats_are_writable_specialist_data_formats_in_science(string id, string displayName)
    {
        var format = FormatRegistry.Get(id);

        Assert.Equal(displayName, format.DisplayName);
        Assert.Equal(FormatCategory.Data, format.Category);
        Assert.True(format.Writable);
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(format));
        Assert.Equal(["science"], FormatFields.Of(format).Select(f => f.Id));
    }
}
