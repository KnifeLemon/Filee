using System.Text;
using Filee.Engines.Text;

namespace Filee.Engines.Tests;

public class SqlTextConverterTests
{
    private const string Script = "SELECT 'café' AS Name;\r\nGO\r\n";

    [Fact]
    public void Decodes_utf16_with_bom_like_ssms_unicode()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Script)).ToArray();
        Assert.Equal(Script, SqlTextConverter.Decode(bytes));
    }

    [Fact]
    public void Decodes_utf8_with_bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Script)).ToArray();
        Assert.Equal(Script, SqlTextConverter.Decode(bytes));
    }

    [Fact]
    public void Decodes_utf8_without_bom()
    {
        Assert.Equal(Script, SqlTextConverter.Decode(Encoding.UTF8.GetBytes(Script)));
    }

    [Fact]
    public void Decodes_windows_1252_ansi()
    {
        // 0xE9 alone is "é" in Windows-1252 and is not valid UTF-8.
        var bytes = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 };
        Assert.Equal("café", SqlTextConverter.Decode(bytes));
    }
}

// End-to-end check, same style as the other engine tests. Add it inside a class that has the `fx` EngineFixture:
//
// [Fact]
// public async Task Sql_to_txt()
// {
//     var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sql");
//     await File.WriteAllTextAsync(path, "SELECT 1;", Encoding.Unicode);
//     var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "txt" });
//     Assert.Equal(JobState.Completed, job.State);
// }