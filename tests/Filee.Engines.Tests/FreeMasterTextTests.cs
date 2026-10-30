// FreeMASTER oscilloscope text exports become CSV without interpreting measurement values.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Tests;

public class FreeMasterTextTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oscilloscope_export_becomes_csv_with_text_and_empty_fields(bool utf16)
    {
        var path = Path.Combine(fx.NewFolder(), "scope.txt");
        const string input = "# Oscilloscope Data\r\n\r\n# Seconds\tLabel\tValue\tEmpty\r\n"
            + "0.000\t\"cell, \"\"A\"\"\"\t4.12300\t\r\n0.541\t한글\t007\t\r\n";
        await File.WriteAllTextAsync(path, input, utf16 ? Encoding.Unicode : new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "csv" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => f.ErrorDetail)));
        var bytes = await File.ReadAllBytesAsync(Assert.Single(job.Outputs), TestContext.Current.CancellationToken);
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..]);
        var rows = CsvFormat.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), ',');
        Assert.Equal(new[] { "Seconds", "Label", "Value", "Empty" }, rows[0]);
        Assert.Equal(new[] { "0.000", "cell, \"A\"", "4.12300", "" }, rows[1]);
        Assert.Equal(new[] { "0.541", "한글", "007", "" }, rows[2]);
        Assert.Equal(3, rows.Count);
    }

    [Theory]
    [InlineData("ordinary plain text\nwithout table\n")]
    [InlineData("# Oscilloscope Data\n# Seconds\tValue\n0.0\t1\t2\n")]
    [InlineData("# Oscilloscope Data\n# Seconds\tValue\n")]
    public async Task Non_matching_or_incomplete_data_is_refused(string input)
    {
        var path = Path.Combine(fx.NewFolder(), "scope.txt");
        await File.WriteAllTextAsync(path, input, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "csv" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Empty(job.Outputs);
        Assert.Equal(input, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }
}
