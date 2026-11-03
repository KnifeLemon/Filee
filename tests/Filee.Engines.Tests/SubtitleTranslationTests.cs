// Subtitle translation: cue numbers, timestamps and headers must survive byte-for-byte; only the cue text is
// sent to the model (in keyed batches), and cues the model drops are asked for again — only the missing ones.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Translation;
using Xunit;

namespace Filee.Engines.Tests;

public class SubtitleTranslationTests
{
    private const string Srt = """
        1
        00:00:01,000 --> 00:00:02,500
        Hello, world

        2
        00:00:03,000 --> 00:00:05,000
        <i>Second</i> line

        3
        00:01:10,000 --> 00:01:12,250
        The <font color="#FF0000">end</font>
        """;

    /// <summary>
    /// Translates a keyed request by re-emitting each <c>&lt;sN&gt;…&lt;/sN&gt;</c> with "[en] " prepended to the text.
    /// When <paramref name="dropKeys"/> is non-empty, the first reply omits exactly those markers (simulating a
    /// partial model answer) and later replies translate what is asked.
    /// </summary>
    private sealed class KeyedService : ITranslationService
    {
        private readonly int[] _dropKeys;
        private int _calls;
        public KeyedService(int[]? dropKeys = null) => _dropKeys = dropKeys ?? [];
        public int Calls => _calls;
        public List<string> Requests { get; } = [];

        public Task<string> TranslateTextAsync(string text, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default)
        {
            _calls++;
            Requests.Add(text);
            var sb = new System.Text.StringBuilder();
            foreach (System.Text.RegularExpressions.Match m in new System.Text.RegularExpressions.Regex(@"<s(\d+)>(.*?)</s\1>", System.Text.RegularExpressions.RegexOptions.Singleline).Matches(text))
            {
                var key = int.Parse(m.Groups[1].Value);
                if (_dropKeys.Contains(key) && Requests.Count == 1)
                    continue; // first reply only: drop these markers
                sb.Append("<s").Append(key).Append(">[en] ").Append(m.Groups[2].Value).Append("</s").Append(key).Append(">").Append('\n');
            }
            return Task.FromResult(sb.ToString());
        }

        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RefusingService : ITranslationService
    {
        public int Calls;

        public Task<string> TranslateTextAsync(string text, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult("no markers at all");
        }
        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static async Task<string> Translate(string inputFormat, string text, ITranslationService service, string outputFormat)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "subtitle-translate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var input = Path.Combine(dir, $"in.{inputFormat}");
            File.WriteAllText(input, text);
            var converter = new TranslatorConverter(service);
            var step = new ConversionStep(input, inputFormat, outputFormat,
                new Preset { Translate = { Enabled = true, TargetLanguage = "en", SourceLanguage = "auto" } },
                new FixedPath(Path.Combine(dir, $"out.{outputFormat}")), dir);
            var outputs = await converter.ConvertAsync(step, null, TestContext.Current.CancellationToken);
            Assert.Single(outputs);
            return File.ReadAllText(outputs.Single());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Srt_translates_cue_text_and_keeps_numbers_and_timings()
    {
        var service = new KeyedService();
        var outText = await Translate("srt", Srt, service, "srt");

        // Timings and cue numbers are untouched…
        Assert.Contains("00:00:01,000 --> 00:00:02,500", outText);
        Assert.Contains("00:00:03,000 --> 00:00:05,000", outText);
        Assert.Contains("00:01:10,000 --> 00:01:12,250", outText);
        // …and every cue's text came back (translated by the mock).
        Assert.Contains("[en] Hello, world", outText);
        Assert.Contains("[en] <i>Second</i> line", outText);
        Assert.Contains("[en] The <font color=\"#FF0000\">end</font>", outText);
        Assert.Equal(1, service.Calls); // three short cues fit in one keyed batch
    }

    [Fact]
    public async Task Subtitle_markup_survives_normalization()
    {
        var service = new KeyedService();
        var outText = await Translate("srt", Srt, service, "srt");
        Assert.Contains("[en] <i>Second</i> line", outText);
        Assert.Contains("[en] The <font color=\"#FF0000\">end</font>", outText);
    }

    [Fact]
    public async Task Vtt_is_translated_and_written_back_as_Vtt()
    {
        var vtt = "WEBVTT\n\n1\n00:00:00.000 --> 00:00:02.500\nHello, world\n\n2\n00:00:03.000 --> 00:00:05.000\nSecond line\n";
        var service = new KeyedService();
        var outText = await Translate("vtt", vtt, service, "vtt");

        Assert.StartsWith("WEBVTT", outText);
        Assert.Contains("00:00:00.000 --> 00:00:02.500", outText);
        Assert.Contains("00:00:03.000 --> 00:00:05.000", outText);
        Assert.Contains("[en] Hello, world", outText);
        Assert.Contains("[en] Second line", outText);
    }

    [Fact]
    public async Task Dropped_cues_are_asked_again_only_for_the_missing_ones()
    {
        var service = new KeyedService([1, 2]);
        var outText = await Translate("srt", Srt, service, "srt");

        Assert.Contains("[en] Hello, world", outText);   // first reply kept as-is
        Assert.Contains("[en] <i>Second</i> line", outText); // recovered in round two
        Assert.Contains("[en] The <font color=\"#FF0000\">end</font>", outText); // recovered in round two
        Assert.Equal(2, service.Calls);

        // The second request contains only the cues the first reply dropped.
        var second = service.Requests[1];
        Assert.Contains("<s1>", second);
        Assert.Contains("<s2>", second);
        Assert.DoesNotContain("<s0>", second);
    }

    [Fact]
    public async Task A_model_ignoring_the_markers_keeps_the_source_text_instead_of_losing_it()
    {
        var service = new RefusingService();
        var outText = await Translate("srt", Srt, service, "srt");

        Assert.Contains("Hello, world", outText);
        Assert.Contains("Second", outText);
        Assert.Contains("end", outText);
        Assert.True(service.Calls <= 4); // recovery stops after the rounds are used
    }

    [Fact]
    public void The_translator_advertises_the_subtitle_edges()
    {
        var converter = new TranslatorConverter();
        Assert.Contains(converter.Edges, e => e.From == "srt" && e.To == "srt" && e.Translate);
        Assert.Contains(converter.Edges, e => e.From == "vtt" && e.To == "vtt" && e.Translate);
    }

    private sealed class FixedPath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
