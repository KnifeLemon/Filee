// A watch folder's optional settings: which files it converts, and how converted files are named.

using Filee.Core.Presets;
using Filee.Core.Watching;

namespace Filee.Core.Tests;

public class WatchRuleOptionsTests
{
    [Theory]
    [InlineData("*.heic", "IMG_0001.HEIC", true)]
    [InlineData("*.heic", "IMG_0001.jpg", false)]
    [InlineData("scan_??.png", "scan_12.png", true)]
    [InlineData("scan_??.png", "scan_123.png", false)]
    [InlineData(@"/^IMG_\d+\.jpg$/", "img_20261011.jpg", true)]
    [InlineData(@"/^IMG_\d+\.jpg$/", "IMG_cat.jpg", false)]
    [InlineData(@"/^report-\d{2,4}/", "report-2026 final.docx", true)]
    public void A_pattern_lets_through_the_names_it_matches(string pattern, string name, bool matches)
    {
        Assert.Null(FileNameFilter.Validate(pattern));
        Assert.Equal(matches, FileNameFilter.Of([pattern]).Matches(name));
    }

    [Fact]
    public void A_file_matching_any_pattern_passes_and_no_patterns_let_everything_through()
    {
        var filter = FileNameFilter.Of(["*.heic", "scan_*"]);
        Assert.True(filter.Matches("scan_0412.pdf"));
        Assert.True(filter.Matches("a.heic"));
        Assert.False(filter.Matches("notes.txt"));
        Assert.True(FileNameFilter.Of([]).Matches("anything.pdf"));
    }

    [Theory]
    [InlineData("heic", "*.heic")]
    [InlineData(".HEIC", "*.heic")]
    [InlineData("scan", "*scan*")]
    [InlineData("scan_*", "scan_*")]
    [InlineData(@"/^IMG_\d+/", @"/^IMG_\d+/")]
    [InlineData("  ", null)]
    public void What_is_typed_becomes_a_pattern(string typed, string? pattern)
    {
        Assert.Equal(pattern, FileNameFilter.Normalize(typed));
    }

    [Fact]
    public void A_regular_expression_that_is_not_valid_is_reported_and_left_out()
    {
        Assert.NotNull(FileNameFilter.Validate("/IMG_(/"));
        Assert.True(FileNameFilter.Of(["/IMG_(/"]).Matches("anything.jpg"));
    }

    [Fact]
    public void Files_the_patterns_leave_out_are_not_converted()
    {
        using var dir = new TempDir();
        var heic = dir.File("IMG_0001.heic");
        var jpg = dir.File("IMG_0002.jpg");
        var rule = new WatchRule { Folder = dir.Path, Include = ["*.heic"] };

        Assert.False(FolderWatcher.ShouldIgnore(heic, rule));
        Assert.True(FolderWatcher.ShouldIgnore(jpg, rule));
    }

    [Fact]
    public void The_folder_name_rules_replace_the_presets_and_empty_ones_keep_them()
    {
        var preset = new Preset { TargetFormat = "pdf", Output = { FileNamePattern = "{name}_{preset}" } };
        var plain = new WatchRule { Folder = Path.Combine("x", "Inbox") }.Apply(preset);
        Assert.Equal("{name}_{preset}", plain.Output.FileNamePattern);
        Assert.Empty(plain.Output.Renames);
        Assert.Equal(OutputLocation.CustomFolder, plain.Output.Location);
        Assert.Equal(Path.Combine("x", "Inbox", "converted"), plain.Output.CustomFolder);

        var rule = new WatchRule
        {
            Folder = "Inbox",
            FileNamePattern = "{name}_{date}",
            Renames = [new() { Kind = RenameKind.Regex, Find = @"IMG_(\d+)", Replace = "Photo_$1" }, new() { Find = "", Replace = "typing" }],
        };
        var own = rule.Apply(preset);
        Assert.Equal("{name}_{date}", own.Output.FileNamePattern);
        Assert.Equal(@"IMG_(\d+)", Assert.Single(own.Output.Renames).Find); // a step still being typed is left out
        Assert.Equal("{name}_{preset}", preset.Output.FileNamePattern); // the preset itself is not changed
        Assert.Empty(preset.Output.Renames);
    }

    [Fact]
    public void Rename_steps_are_applied_in_order()
    {
        var rule = new OutputRule
        {
            Location = OutputLocation.SameFolder,
            Renames = [new() { Find = "img_", Replace = "Photo " }, new() { Kind = RenameKind.SpacesToUnderscores }],
        };
        var source = Path.Combine(Path.GetTempPath(), "IMG_0412.heic");

        var path = OutputPathResolver.Resolve(rule, new OutputPathResolver.Tokens(source, "JPG", 1, DateTime.Now), "jpg", _ => false);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "Photo_0412.jpg"), path);
    }

    [Theory]
    [InlineData(RenameKind.Replace, "IMG_", "Photo_", "IMG_0412", "Photo_0412")]
    [InlineData(RenameKind.Remove, "_copy", "", "report_COPY", "report")]
    [InlineData(RenameKind.Prefix, "", "2026_", "report", "2026_report")]
    [InlineData(RenameKind.Suffix, "", "_final", "report", "report_final")]
    [InlineData(RenameKind.SpacesToUnderscores, "", "", "trip to  busan", "trip_to_busan")]
    [InlineData(RenameKind.RemoveCopyNumber, "", "", "report (2)", "report")]
    [InlineData(RenameKind.RemoveBrackets, "", "", "song [remix] (live)", "song")]
    [InlineData(RenameKind.RemoveLeadingNumber, "", "", "01 - intro", "intro")]
    [InlineData(RenameKind.RemoveSymbols, "", "", "invoice#12@acme!", "invoice12acme")]
    [InlineData(RenameKind.RemoveSymbols, "", "", "보고서#1", "보고서1")]
    [InlineData(RenameKind.Lowercase, "", "", "Report_FINAL", "report_final")]
    [InlineData(RenameKind.Uppercase, "", "", "report", "REPORT")]
    [InlineData(RenameKind.Regex, @"(\d{4})(\d{2})(\d{2})", "$1-$2-$3", "scan_20261011", "scan_2026-10-11")]
    [InlineData(RenameKind.Replace, "", "x", "report", "report")] // still being typed: no change
    public void Each_kind_of_rule_changes_the_name_as_it_says(RenameKind kind, string find, string replace, string name, string expected)
    {
        Assert.Equal(expected, new RenameStep { Kind = kind, Find = find, Replace = replace }.Apply(name));
    }

    [Fact]
    public void A_rename_pattern_that_is_not_valid_leaves_the_name_as_it_is()
    {
        Assert.NotNull(OutputPathResolver.RenameError("IMG_("));
        Assert.Equal("IMG_0412", OutputPathResolver.Rename("IMG_0412", "IMG_(", "x"));
        Assert.Null(OutputPathResolver.RenameError(""));
    }
}
