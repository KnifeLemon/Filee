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

    [Fact]
    public void The_largest_model_is_used_even_next_to_the_downloaded_base()
    {
        var root = Directory.CreateTempSubdirectory("filee-whisper-").FullName;
        try
        {
            // Filee downloads base into whisper-model; the user puts small next to it and medium in their own copy's folder.
            var downloaded = Directory.CreateDirectory(Path.Combine(root, "whisper-model")).FullName;
            var own = Directory.CreateDirectory(Path.Combine(root, "own")).FullName;
            Model(downloaded, "ggml-base.bin", 148);
            Assert.Equal(Path.Combine(downloaded, "ggml-base.bin"), WhisperConverter.PickModel([downloaded, own]));

            Model(downloaded, "ggml-small.bin", 466);
            Model(downloaded, "ggml-tiny.bin", 75);
            Assert.Equal(Path.Combine(downloaded, "ggml-small.bin"), WhisperConverter.PickModel([downloaded, own]));

            Model(own, "ggml-medium.bin", 1500);
            Assert.Equal(Path.Combine(own, "ggml-medium.bin"), WhisperConverter.PickModel([downloaded, own, "missing"]));

            // An English-only model is bigger, but would turn speech in other languages into English.
            Model(own, "ggml-large-v3.en.bin", 3000);
            Assert.Equal(Path.Combine(own, "ggml-medium.bin"), WhisperConverter.PickModel([downloaded, own]));
            // Alone, it is still used.
            var english = Directory.CreateDirectory(Path.Combine(root, "english")).FullName;
            Model(english, "ggml-base.en.bin", 148);
            Assert.Equal(Path.Combine(english, "ggml-base.en.bin"), WhisperConverter.PickModel([english]));

            Assert.Null(WhisperConverter.PickModel([Path.Combine(root, "missing")]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        // The size of the model file stands for the model: a few bytes per megabyte keep the test fast.
        static void Model(string dir, string name, int megabytes) =>
            File.WriteAllBytes(Path.Combine(dir, name), new byte[megabytes]);
    }
}
