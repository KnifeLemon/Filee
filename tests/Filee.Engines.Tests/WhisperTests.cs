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
        Assert.Equal("Whisper (AI Captions)", converter.DisplayName);
        Assert.NotEmpty(converter.Edges);

        // Caption edges produce mp4 with Caption == true
        var captionEdges = converter.Edges.Where(e => e.Caption).ToList();
        Assert.NotEmpty(captionEdges);
        foreach (var edge in captionEdges)
        {
            Assert.Equal("mp4", edge.To);
        }

        // Common video formats are supported
        var fromFormats = converter.Edges.Select(e => e.From).ToHashSet();
        Assert.Contains("mp4", fromFormats);
        Assert.Contains("mkv", fromFormats);
        Assert.Contains("mov", fromFormats);
        Assert.Contains("avi", fromFormats);
        Assert.Contains("webm", fromFormats);
    }

    [Fact]
    public void Whisper_converter_status_does_not_throw()
    {
        var converter = new WhisperConverter();
        var status = converter.GetStatus();

        Assert.NotNull(status);
        if (!status.IsAvailable)
        {
            Assert.Equal("engine.reason.not_installed", status.ReasonKey);
        }
    }
}
