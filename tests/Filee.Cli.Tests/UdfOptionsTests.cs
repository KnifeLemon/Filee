// UDF is explicit; command-line parsing never silently changes an existing Joliet preset.

using Filee.Core.Presets;

namespace Filee.Cli.Tests;

public sealed class UdfOptionsTests
{
    [Theory]
    [InlineData("joliet", IsoFileSystem.Joliet)]
    [InlineData("udf201", IsoFileSystem.Udf201)]
    public void Filesystem_is_parsed_explicitly(string value, IsoFileSystem expected)
    {
        var options = CliOptions.Parse(["convert", "folder", "--to", "iso", "--iso-filesystem", value]);
        Assert.Null(options.Error);
        Assert.Equal(expected, options.IsoFileSystem);
    }

    [Fact]
    public void Missing_choice_keeps_the_preset_and_unknown_revisions_are_rejected()
    {
        Assert.Null(CliOptions.Parse(["convert", "folder", "--to", "iso"]).IsoFileSystem);
        Assert.NotNull(CliOptions.Parse(["convert", "folder", "--to", "iso", "--iso-filesystem", "udf250"]).Error);
    }
}
