// Sequence alignment formats (NEXUS, FASTA, PHYLIP; issue #1798): extension lookup, writable data category,
// Specialist grade and Science & medical field membership.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

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

    [Fact]
    public void Each_alignment_format_has_a_built_in_preset_in_the_palette_after_the_data_presets()
    {
        var ids = BuiltInData.CreatePresets().Select(p => p.Id).ToList();
        var presets = BuiltInData.CreatePresets();

        Assert.Equal(["to-fasta", "to-phy", "to-nex"], ids.Skip(ids.IndexOf("to-ics") + 1).Take(3));
        Assert.Equal("fasta", presets.Single(p => p.Id == "to-fasta").TargetFormat);
        Assert.Equal("phy", presets.Single(p => p.Id == "to-phy").TargetFormat);
        Assert.Equal("nex", presets.Single(p => p.Id == "to-nex").TargetFormat);
        Assert.Equal(["FASTA", "PHYLIP", "NEXUS"], BuiltInData.SequenceAlignmentPresets().Select(p => p.Name));
    }

    [Fact]
    public void Version_103_libraries_learn_the_alignments_once_and_keep_a_custom_claim()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id is "to-fasta" or "to-phy" or "to-nex");
        var profiles = BuiltInData.CreateProfiles();
        var data = profiles.Single(p => p.Id == "data");
        string[] extensions = ["nex", "nxs", "nexus", "fasta", "fa", "fas", "fna", "faa", "phy", "phylip"];
        foreach (var extension in extensions)
            data.Extensions.Remove(extension);

        Assert.True(SettingsMigrations.ApplyToLibrary(103, presets, profiles));
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id)); // after the data presets
        Assert.All(extensions, extension => Assert.Contains(extension, data.Extensions));
        Assert.False(SettingsMigrations.ApplyToLibrary(103, presets, profiles));

        data.Extensions.Remove("fasta");
        profiles.Insert(0, new ToolbarProfile { Id = "my-genes", Extensions = ["FASTA"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(103, presets, profiles));
        Assert.DoesNotContain("fasta", data.Extensions);
        Assert.Equal("my-genes", ProfileSelector.Select(profiles, ["reads.fasta"])?.Id);
    }
}
