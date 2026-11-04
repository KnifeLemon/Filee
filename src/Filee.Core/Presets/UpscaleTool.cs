// "Upscale Quality": upscale images using local AI models (Real-ESRGAN / NCNN).

using Filee.Core.Conversion;
using Filee.Core.Formats;

namespace Filee.Core.Presets;

/// <summary>Turns the "Upscale Quality" tool into the conversions that write the upscaled pictures.</summary>
public static class UpscaleTool
{
    /// <summary>Where an image goes when its own format cannot be written directly.</summary>
    public const string FallbackTarget = "png";

    /// <summary>The formats the upscale window offers, after "Same format as the source".</summary>
    public static readonly IReadOnlyList<string> Targets = ["png", "jpg", "webp", "avif", "heic", "ico", "bmp", "tiff"];

    /// <summary>True when a <paramref name="sourceFormat"/> file is an image the tool can upscale.</summary>
    public static bool CanUpscale(string sourceFormat) =>
        (FormatRegistry.FindById(sourceFormat) ?? FormatRegistry.FindByExtension(sourceFormat))?.Category == FormatCategory.Image;

    /// <summary>
    /// Resolves the output format: either the chosen target or preserves the source format when possible.
    /// </summary>
    public static string TargetFor(string sourceFormat, string chosen)
    {
        if (chosen != BuiltInData.SameAsSource)
            return chosen;
        var detected = FormatRegistry.FindById(sourceFormat) ?? FormatRegistry.FindByExtension(sourceFormat);
        return detected is { Writable: true } ? detected.Id : FallbackTarget;
    }

    /// <summary>
    /// Creates the conversion preset that writes upscaled pictures as <paramref name="targetFormat"/>.
    /// </summary>
    public static Preset ConversionOf(Preset tool, string targetFormat, string? model, int scale)
    {
        var preset = tool.Clone();
        preset.Kind = PresetKind.Convert;
        preset.Tool = null;
        preset.TargetFormat = targetFormat;
        preset.Upscale = new UpscaleOptions
        {
            Enabled = true,
            Model = model,
            Scale = scale,
        };
        return preset;
    }
}
