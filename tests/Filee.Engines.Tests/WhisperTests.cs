using Filee.Core.Conversion;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public class WhisperTests
{
    [Fact]
    public void Whisper_converter_has_correct_metadata_and_caption_edges()
    {
        var converter = new WhisperConverter();

        Assert.Equal("whisper", converter.Id);
        Assert.Equal("Whisper", converter.DisplayName);
        Assert.NotEmpty(converter.Edges);

        // Every edge must produce srt directly and require the Caption flag
        Assert.All(converter.Edges, edge =>
        {
            Assert.True(edge.Caption);
            Assert.Equal("srt", edge.To);
        });

        // Supports video formats to SRT
        var fromFormats = converter.Edges.Select(e => e.From).ToHashSet();
        Assert.Contains("mp4", fromFormats);
        Assert.Contains("mkv", fromFormats);
        Assert.Contains("mov", fromFormats);
        Assert.Contains("avi", fromFormats);
        Assert.Contains("webm", fromFormats);

        // Supports audio formats to SRT
        Assert.Contains("mp3", fromFormats);
        Assert.Contains("wav", fromFormats);
        Assert.Contains("m4a", fromFormats);
        Assert.Contains("flac", fromFormats);
        Assert.Contains("aac", fromFormats);
        Assert.Contains("ogg", fromFormats);
    }

    [Fact]
    public void Whisper_converter_status_reports_correctly()
    {
        var converter = new WhisperConverter();
        var status = converter.GetStatus();

        Assert.NotNull(status);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(status.IsAvailable);
            Assert.Equal("engine.reason.unsupported_platform", status.ReasonKey);
        }
        else if (!status.IsAvailable)
        {
            Assert.Equal("engine.reason.not_installed", status.ReasonKey);
        }
        else
        {
            Assert.True(status.IsAvailable);
        }
    }
}
