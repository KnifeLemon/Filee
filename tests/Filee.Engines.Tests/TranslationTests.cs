using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Translation;
using Xunit;

namespace Filee.Engines.Tests;

public class TranslationTests
{
    private sealed class MockTranslationService : ITranslationService
    {
        public Task<string> TranslateTextAsync(string text, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default)
        {
            return Task.FromResult($"[TRANSLATED_{toLang}] {text}");
        }

        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> res = texts.Select(t => $"[TRANSLATED_{toLang}] {t}").ToList();
            return Task.FromResult(res);
        }
    }

    [Fact]
    public void Normalizes_language_names_and_codes()
    {
        Assert.Equal("hi", TranslationService.NormalizeLanguage("hindi"));
        Assert.Equal("hi", TranslationService.NormalizeLanguage("HI"));
        Assert.Equal("en", TranslationService.NormalizeLanguage("English"));
        Assert.Equal("zh", TranslationService.NormalizeLanguage("Chinese"));
        Assert.Equal("es", TranslationService.NormalizeLanguage("spanish"));
        Assert.Equal("fr", TranslationService.NormalizeLanguage("french"));
    }

    [Theory]
    [InlineData("gpt-oss:120b-cloud", true)]
    [InlineData("model-cloud", true)]
    [InlineData("llama3.2:cloud", true)]
    [InlineData("my-cloud-model:latest", true)]
    [InlineData("cloud/llama3", true)]
    [InlineData("llama3.2", false)]
    [InlineData("qwen2.5:3b", false)]
    [InlineData("mistral:7b-instruct", false)]
    [InlineData("gemma2:9b", false)]
    public void IsCloudModel_identifies_cloud_forwarding_tags(string modelName, bool expected)
    {
        Assert.Equal(expected, TranslationService.IsCloudModel(modelName));
    }

    [Fact]
    public async Task DocumentTranslator_preserves_inline_formatting()
    {
        var doc = new HDocument();
        var sec = new HSection();
        doc.Sections.Add(sec);

        var p = new HParagraph();
        p.Inlines.Add(new HText("Chapter 1: ", new HCharFormat()));
        p.Inlines.Add(new HText("The Beginning", new HCharFormat()));
        sec.Blocks.Add(p);

        var service = new MockTranslationService();
        await DocumentTranslator.TranslateDocumentAsync(doc, "hi", "en", service, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, p.Inlines.Count);
        Assert.True(p.Inlines[0] is HText t0 && !string.IsNullOrEmpty(t0.Text));
    }

    [Fact]
    public async Task TranslationService_throws_when_no_local_engine_is_available()
    {
        var service = new TranslationService();
        if (!await TranslationService.IsLocalOllamaAvailableAsync(TestContext.Current.CancellationToken))
        {
            await Assert.ThrowsAsync<ConversionRefusedException>(() => service.TranslateTextAsync("Hello", "es", "en", cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// Answers a keyed request by re-emitting each <c>&lt;sN&gt;…&lt;/sN&gt;</c> with "[T] " prepended to the text.
    /// The dropped slots are omitted — or, when asked to, answered with an empty marker — while the call
    /// counter is within the drop window. Plain (unkeyed) text is translated directly; a keyed request of
    /// which every marker is dropped comes back empty, like a model that lost the thread.
    /// </summary>
    private sealed class FlakyTranslationService : ITranslationService
    {
        private static readonly Regex Slots = new(@"<s(\d+)>(.*?)</s\1>", RegexOptions.Singleline);

        private readonly int[] _drop;
        private readonly int _dropUntil;
        private readonly bool _answerEmpty;
        private int _calls;

        public FlakyTranslationService(int[] drop, int dropUntil, bool answerEmpty = false)
        {
            _drop = drop;
            _dropUntil = dropUntil;
            _answerEmpty = answerEmpty;
        }

        public int Calls => _calls;
        public List<string> Requests { get; } = [];

        public Task<string> TranslateTextAsync(string text, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default)
        {
            _calls++;
            Requests.Add(text);
            var sb = new StringBuilder();
            foreach (Match m in Slots.Matches(text))
            {
                var key = int.Parse(m.Groups[1].Value);
                if (_drop.Contains(key) && _calls <= _dropUntil)
                {
                    if (_answerEmpty)
                        sb.Append($"<s{key}></s{key}>");
                    continue;
                }
                sb.Append($"<s{key}>[T] {m.Groups[2].Value}</s{key}>");
            }
            var reply = Slots.IsMatch(text) ? sb.ToString() : $"[T] {text}";
            return Task.FromResult(reply);
        }

        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static async Task<List<HText>> TranslateParagraph(FlakyTranslationService service, params string[] runs)
    {
        var doc = new HDocument();
        var sec = new HSection();
        doc.Sections.Add(sec);
        var p = new HParagraph();
        foreach (var run in runs)
            p.Inlines.Add(new HText(run, new HCharFormat()));
        sec.Blocks.Add(p);
        await DocumentTranslator.TranslateDocumentAsync(doc, "hi", "en", service, null, TestContext.Current.CancellationToken);
        return p.Inlines.Cast<HText>().ToList();
    }

    [Fact]
    public async Task A_complete_keyed_answer_needs_no_recovery_rounds()
    {
        var service = new FlakyTranslationService([], int.MaxValue);
        var inlines = await TranslateParagraph(service, "Alpha.", "Beta.", "Gamma.");
        Assert.Equal(1, service.Calls);
        Assert.Equal("[T] Alpha.", inlines[0].Text);
        Assert.Equal("[T] Beta.", inlines[1].Text);
        Assert.Equal("[T] Gamma.", inlines[2].Text);
    }

    [Fact]
    public async Task Dropped_runs_are_asked_again_only_for_the_missing_ones()
    {
        var service = new FlakyTranslationService([1, 2], 1);
        var inlines = await TranslateParagraph(service, "Alpha.", "Beta.", "Gamma.");
        Assert.Equal(2, service.Calls);
        Assert.Contains("<s1>", service.Requests[1]);
        Assert.Contains("<s2>", service.Requests[1]);
        Assert.DoesNotContain("<s0>", service.Requests[1]);
        Assert.Equal("[T] Alpha.", inlines[0].Text);
        Assert.Equal("[T] Beta.", inlines[1].Text);
        Assert.Equal("[T] Gamma.", inlines[2].Text);
    }

    [Fact]
    public async Task A_run_the_model_never_returns_is_translated_on_its_own()
    {
        var service = new FlakyTranslationService([1], int.MaxValue);
        var inlines = await TranslateParagraph(service, "Alpha.", "Beta.", "Gamma.");
        // the keyed round, one keyed retry that misses again, then the plain single-run translation
        Assert.Equal(3, service.Calls);
        Assert.Equal("<s1>Beta.</s1>", service.Requests[1]);
        Assert.Equal("Beta.", service.Requests[2]);
        Assert.Equal("[T] Alpha.", inlines[0].Text);
        Assert.Equal("[T] Beta.", inlines[1].Text);
        Assert.Equal("[T] Gamma.", inlines[2].Text);
    }

    [Fact]
    public async Task A_run_answered_with_empty_text_is_never_emptied()
    {
        var service = new FlakyTranslationService([1], int.MaxValue, answerEmpty: true);
        var inlines = await TranslateParagraph(service, "Alpha.", "Beta.", "Gamma.");
        Assert.Equal(3, service.Calls);
        Assert.Equal("[T] Beta.", inlines[1].Text); // salvaged by the plain single-run translation
    }

    private sealed class StatusMockHandler(System.Net.HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    [Fact]
    public async Task TranslationService_throws_model_not_found_on_404()
    {
        using var client = new HttpClient(new StatusMockHandler(System.Net.HttpStatusCode.NotFound));
        var options = new Filee.Core.Presets.TranslateOptions { Model = "deleted-model:latest" };
        var service = new TranslationService(options, client);

        var ex = await Assert.ThrowsAsync<ConversionRefusedException>(() => service.TranslateTextAsync("Hello", "es", "en", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("error.translation_model_not_found", ex.Message);
    }

    [Fact]
    public async Task TranslationService_throws_cloud_forbidden_on_non_404_error()
    {
        using var client = new HttpClient(new StatusMockHandler(System.Net.HttpStatusCode.InternalServerError));
        var options = new Filee.Core.Presets.TranslateOptions { Model = "error-model:latest" };
        var service = new TranslationService(options, client);

        var ex = await Assert.ThrowsAsync<ConversionRefusedException>(() => service.TranslateTextAsync("Hello", "es", "en", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("error.translation_cloud_model_forbidden", ex.Message);
    }
}
