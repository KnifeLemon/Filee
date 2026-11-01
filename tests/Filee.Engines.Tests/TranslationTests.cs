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
        await DocumentTranslator.TranslateDocumentAsync(doc, "hi", "en", service, null, default);

        Assert.Equal(2, p.Inlines.Count);
        Assert.True(p.Inlines[0] is HText t0 && !string.IsNullOrEmpty(t0.Text));
    }

    [Fact]
    public async Task TranslationService_throws_when_no_local_engine_is_available()
    {
        var service = new TranslationService();
        if (!await TranslationService.IsLocalOllamaAvailableAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateTextAsync("Hello", "es", "en"));
        }
    }
}
