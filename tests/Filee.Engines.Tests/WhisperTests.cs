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

        // Caption edges produce srt directly
        var captionSrtEdges = converter.Edges.Where(e => e.Caption && e.To == "srt").ToList();
        Assert.NotEmpty(captionSrtEdges);

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

    [Fact]
    public void Urdu_to_devanagari_transliterates_hindustani_perso_arabic_text()
    {
        var urdu = "آپ سب کچھ کر سکتے ہیں کہ یہی سب کا بول ہے کہ یہ تو کسی کسی کے بھاگے میں لکھا ہوتا کچھ نہیں تو جو چاہو اپنا بھاگے بنا سکتے ہو یہ منش شریر جو چاہو کر سکتے ہو بھگوان کو حاصل کر سکتے تو کیا نہیں بن سکتے ہو۔";
        var dev = UrduToDevanagari.ConvertText(urdu);

        Assert.Contains("आप सब कुछ कर सकते हैं", dev);
        Assert.Contains("भाग्य", dev);
        Assert.Contains("मनुष्य शरीर", dev);
        Assert.Contains("भगवान", dev);
        Assert.False(UrduToDevanagari.ContainsUrdu(dev));
    }

    [Fact]
    public void Urdu_to_devanagari_converts_full_srt_properly()
    {
        var srt = """
            1
            00:00:00,000 --> 00:00:08,700
            آپ سب کچھ کر سکتے ہیں

            2
            00:00:08,700 --> 00:00:14,380
            یہ منش شریر جو چاہو کر سکتے ہو
            """;

        var converted = UrduToDevanagari.ConvertSrt(srt);

        Assert.Contains("1", converted);
        Assert.Contains("00:00:00,000 --> 00:00:08,700", converted);
        Assert.Contains("आप सब कुछ कर सकते हैं", converted);
        Assert.Contains("2", converted);
        Assert.Contains("यह मनुष्य शरीर जो चाहो कर सकते हो", converted);
        Assert.False(UrduToDevanagari.ContainsUrdu(converted));
    }
}
