using Filee.Core.Conversion;
using Filee.Core.Presets;
using Xunit;

namespace Filee.Core.Tests;

public sealed class UpscaleRouteTests
{
    private sealed class DummyUpscalerConverter : IConverter
    {
        public string Id => "upscaler";
        public string DisplayName => "Upscayl (AI image upscaler)";
        public int MaxParallelism => 1;
        public IReadOnlyList<ConversionEdge> Edges { get; } =
        [
            new("jpg", "jpg", Cost: 900, DirectOnly: true, Upscale: true),
            new("jpg", "png", Cost: 900, DirectOnly: true, Upscale: true),
            new("png", "png", Cost: 900, DirectOnly: true, Upscale: true),
        ];

        public EngineStatus GetStatus() => EngineStatus.Available();
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public void RoutePlanner_ignores_upscale_edges_during_normal_planning()
    {
        var planner = new RoutePlanner([new DummyUpscalerConverter()]);
        var normalRoute = planner.Plan("jpg", "jpg", upscale: false);
        Assert.Null(normalRoute);
    }

    [Fact]
    public void RoutePlanner_routes_to_upscaler_when_upscale_flag_is_true()
    {
        var planner = new RoutePlanner([new DummyUpscalerConverter()]);
        var upscaleRoute = planner.Plan("jpg", "jpg", upscale: true);
        Assert.NotNull(upscaleRoute);
        Assert.Equal("upscaler", upscaleRoute.Steps[0].Converter.Id);
        Assert.True(upscaleRoute.Steps[0].Upscale);
    }

    [Fact]
    public void Preset_UpscaleTool_sets_upscale_flag_and_model()
    {
        var tool = BuiltInData.UpscalePresets()[0];
        var conversion = UpscaleTool.ConversionOf(tool, "png", "4xLSDIRCompactC3", 4);

        Assert.Equal(PresetKind.Convert, conversion.Kind);
        Assert.Null(conversion.Tool);
        Assert.Equal("png", conversion.TargetFormat);
        Assert.True(conversion.Upscale.Enabled);
        Assert.Equal("4xLSDIRCompactC3", conversion.Upscale.Model);
        Assert.Equal(4, conversion.Upscale.Scale);
    }
}
