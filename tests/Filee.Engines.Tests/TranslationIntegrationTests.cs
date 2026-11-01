using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Translation;
using Xunit;

namespace Filee.Engines.Tests;

public class TranslationIntegrationTests
{
    private sealed class MockTranslationService : ITranslationService
    {
        public Task<string> TranslateTextAsync(string text, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default)
        {
            if (toLang == "hi")
                return Task.FromResult("हर यात्रा एक कदम से शुरू होती है (brother)");
            if (toLang == "es")
                return Task.FromResult("El zorro marrón rápido salta sobre el perro perezoso.");
            return Task.FromResult($"[brother in {toLang}] {text}");
        }

        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> res = texts.Select(t =>
            {
                if (toLang == "hi")
                    return "हर कदम (brother)";
                if (toLang == "es")
                    return "Texto traducido al español.";
                return $"[brother in {toLang}] {t}";
            }).ToList();
            return Task.FromResult(res);
        }
    }

    private sealed class TestOutputAllocator : IOutputAllocator
    {
        private readonly string _dir;
        public TestOutputAllocator(string dir) => _dir = dir;

        public string? Allocate(string extension, string? name = null) =>
            Path.Combine(_dir, $"output-{Guid.NewGuid():N}.{extension}");

        public string Reserve(string path) => path;
        public void Relinquish(string path) { }
    }

    [Fact]
    public async Task Translates_chinese_markdown_to_english_preserving_markdown_structure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "filee-trans-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputFile = Path.Combine(tempDir, "sample_zh.md");
            var inputContent = "# 桃园三结义\n\n东汉末年，天下大乱。\n\n- 刘备为兄\n- 关羽次之\n- 张飞为弟\n";
            await File.WriteAllTextAsync(inputFile, inputContent, TestContext.Current.CancellationToken);

            var preset = new Preset
            {
                Id = "translate",
                TargetFormat = "md",
                Translate = { Enabled = true, TargetLanguage = "en", SourceLanguage = "zh-CN" }
            };

            var step = new ConversionStep(
                inputFile,
                "md",
                "md",
                preset,
                new TestOutputAllocator(tempDir),
                tempDir,
                Translate: true
            );

            var converter = new TranslatorConverter(new MockTranslationService());
            var outputs = await converter.ConvertAsync(step, null, TestContext.Current.CancellationToken);

            Assert.Single(outputs);
            var translatedPath = outputs[0];
            Assert.True(File.Exists(translatedPath));

            var translatedContent = await File.ReadAllTextAsync(translatedPath, TestContext.Current.CancellationToken);
            Assert.NotEmpty(translatedContent);

            // Structure must have headings and list bullets
            Assert.Contains("#", translatedContent);
            Assert.Contains("-", translatedContent);

            // Content check
            Assert.Contains("brother", translatedContent, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Translates_english_markdown_to_hindi_preserving_markdown_structure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "filee-trans-hi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputFile = Path.Combine(tempDir, "sample_en.md");
            var inputContent = "# The Great Journey\n\nEvery journey begins with a single step.\n\n- First step\n- Second step\n";
            await File.WriteAllTextAsync(inputFile, inputContent, TestContext.Current.CancellationToken);

            var preset = new Preset
            {
                Id = "translate",
                TargetFormat = "md",
                Translate = { Enabled = true, TargetLanguage = "hi", SourceLanguage = "en" }
            };

            var step = new ConversionStep(
                inputFile,
                "md",
                "md",
                preset,
                new TestOutputAllocator(tempDir),
                tempDir,
                Translate: true
            );

            var converter = new TranslatorConverter(new MockTranslationService());
            var outputs = await converter.ConvertAsync(step, null, TestContext.Current.CancellationToken);

            Assert.Single(outputs);
            var translatedPath = outputs[0];
            Assert.True(File.Exists(translatedPath));

            var translatedContent = await File.ReadAllTextAsync(translatedPath, TestContext.Current.CancellationToken);
            Assert.NotEmpty(translatedContent);

            // Structure check
            Assert.Contains("#", translatedContent);
            Assert.Contains("-", translatedContent);

            // Hindi Devanagari characters check (\u0900 - \u097F)
            Assert.Contains(translatedContent, c => c >= '\u0900' && c <= '\u097F');
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Translates_document_to_docx_producing_valid_output()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "filee-trans-docx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputFile = Path.Combine(tempDir, "document.txt");
            var inputContent = "The Quick Brown Fox Jumps Over The Lazy Dog.\nThis is a sample document for translation into DOCX.\n";
            await File.WriteAllTextAsync(inputFile, inputContent, TestContext.Current.CancellationToken);

            var preset = new Preset
            {
                Id = "translate",
                TargetFormat = "docx",
                Translate = { Enabled = true, TargetLanguage = "es", SourceLanguage = "en" }
            };

            var step = new ConversionStep(
                inputFile,
                "txt",
                "docx",
                preset,
                new TestOutputAllocator(tempDir),
                tempDir,
                Translate: true
            );

            var converter = new TranslatorConverter(new MockTranslationService());
            var outputs = await converter.ConvertAsync(step, null, TestContext.Current.CancellationToken);

            Assert.Single(outputs);
            var docxPath = outputs[0];
            Assert.True(File.Exists(docxPath));
            Assert.True(new FileInfo(docxPath).Length > 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
