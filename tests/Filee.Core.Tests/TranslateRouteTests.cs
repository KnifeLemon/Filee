using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Xunit;

namespace Filee.Core.Tests;

public class TranslateRouteTests
{
    private sealed class DummyTranslateConverter : IConverter
    {
        public string Id => "translator";
        public string DisplayName => "Translator";
        public int MaxParallelism => 1;

        public IReadOnlyList<ConversionEdge> Edges { get; } =
        [
            new("epub", "epub", Cost: 10, Translate: true),
            new("md", "md", Cost: 10, Translate: true),
            new("txt", "txt", Cost: 10, Translate: true),
            new("docx", "docx", Cost: 10, Translate: true),
            new("epub", "docx", Cost: 12, Translate: true),
        ];

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([step.Output.Allocate(step.To) ?? ""]);

        public EngineStatus GetStatus() => EngineStatus.Available("Translate", "1.0");
    }

    [Fact]
    public void BuiltIn_translate_preset_has_translate_flag_and_targets_same_as_source()
    {
        var preset = BuiltInData.CreatePresets().FirstOrDefault(p => p.Id == "translate");
        Assert.NotNull(preset);
        Assert.True(preset.Translate.Enabled);
        Assert.Equal(BuiltInData.SameAsSource, preset.TargetFormat);
        Assert.Equal("en", preset.Translate.TargetLanguage);
        Assert.Equal("{name}_translated", preset.Output.FileNamePattern);
    }

    [Theory]
    [InlineData("epub", true)]
    [InlineData("md", true)]
    [InlineData("txt", true)]
    [InlineData("docx", true)]
    [InlineData("pdf", false)]
    [InlineData("json", false)]
    [InlineData("yaml", false)]
    [InlineData("srt", false)]
    [InlineData("png", false)]
    [InlineData("mp4", false)]
    [InlineData("zip", false)]
    public void TranslatesDocument_recognizes_valid_document_ebook_and_text_formats(string format, bool expected)
    {
        var preset = new Preset
        {
            Id = "test-translate",
            Translate = { Enabled = true, TargetLanguage = "hi" }
        };

        Assert.Equal(expected, preset.TranslatesDocument(format));
    }

    [Fact]
    public void RoutePlanner_routes_to_translator_when_translate_flag_is_true()
    {
        var planner = new RoutePlanner([new DummyTranslateConverter()]);

        // Regular route without translate should return null (translate edges are excluded from normal routes)
        var normalRoute = planner.Plan("epub", "epub", translate: false);
        Assert.Null(normalRoute);

        // With translate: true, it finds the route
        var translateRoute = planner.Plan("epub", "epub", translate: true);
        Assert.NotNull(translateRoute);
        Assert.Equal("translator", translateRoute.Steps[0].Converter.Id);
        Assert.True(translateRoute.Steps[0].Translate);
    }

    [Fact]
    public void Fresh_install_profiles_offer_translate_on_back_face()
    {
        var profiles = BuiltInData.CreateProfiles();
        Assert.Contains("translate", profiles.First(p => p.Id == "ebooks").BackPresetIds!);
        Assert.Contains("translate", profiles.First(p => p.Id == "text").BackPresetIds!);
        Assert.Contains("translate", profiles.First(p => p.Id == "office").BackPresetIds!);
        Assert.DoesNotContain("translate", profiles.First(p => p.Id == "pdf").BackPresetIds!);
        Assert.DoesNotContain("translate", profiles.First(p => p.Id == "hwp").BackPresetIds!);
    }
}
