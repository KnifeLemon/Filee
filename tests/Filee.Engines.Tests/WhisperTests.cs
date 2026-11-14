using System.Text;
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

        // Every edge must produce srt or plain text directly and require the Caption flag
        Assert.All(converter.Edges, edge =>
        {
            Assert.True(edge.Caption);
            Assert.Contains(edge.To, new[] { "srt", "txt" });
        });
        Assert.Equal(converter.Edges.Where(e => e.To == "srt").Select(e => e.From), converter.Edges.Where(e => e.To == "txt").Select(e => e.From));

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
    public void Transcript_is_the_spoken_text_without_times()
    {
        // As whisper-cli writes it: a line repeated over several segments, which happens on long pauses.
        var srt = "1\n00:00:00,000 --> 00:00:02,000\n Hello everyone.\n\n2\n00:00:02,000 --> 00:00:04,000\n Today we talk about Filee.\n\n" +
                  "3\n00:00:04,000 --> 00:00:06,000\n Today we talk about Filee.\n\n4\n00:00:06,000 --> 00:00:08,000\n Thank you.\n\n";
        var text = new UTF8Encoding(false).GetString(WhisperConverter.Transcript(Encoding.UTF8.GetBytes(srt))).TrimStart('﻿');

        Assert.Equal("Hello everyone.\r\nToday we talk about Filee.\r\nThank you.\r\n", text);
        // Silence: Whisper's SRT has no cues, and the text is empty like it.
        Assert.Empty(WhisperConverter.Transcript(Encoding.UTF8.GetBytes("\n")));
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
