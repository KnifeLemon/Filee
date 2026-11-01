// The render quality thresholds (render-thresholds.json next to this file): what each renderer measured on each
// feature where it falls short of the ideal. A measurement must meet the ideal (the exact text, no overlaps, every
// font subset, identical renderings ...) unless the file gives a threshold for it, and must never be worse than that
// threshold: a worse value is a regression and fails the test. A better value passes and is reported, so the
// threshold can be raised in the same change. Allow-listed measurements (known rhwp defects) are reported only.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filee.Engines.Tests.RenderQuality;

/// <summary>A measured value of one feature, rendered by one renderer.</summary>
internal sealed record Measurement(string Renderer, string Feature, CheckResult Result);

internal enum Verdict
{
    Ideal,
    WithinThreshold,
    Improved,
    Allowed,
    Regression,
}

internal sealed class RenderThresholds
{
    /// <summary>Measurements where a higher value is better; the others count faults (lower is better).</summary>
    private static readonly HashSet<string> HigherIsBetter = [PdfChecks.Text, PdfChecks.Baselines, Lines, Placed, MinSsim];

    /// <summary>Diff measurements: share of lines with the same text, share also on the same baseline, lowest page SSIM.</summary>
    public const string Lines = "lines", Placed = "placed", MinSsim = "ssim";

    /// <summary>Prefix of the diff measurements of a candidate renderer against the reference.</summary>
    public const string Versus = "vs-rhwp-";

    /// <summary>renderer → feature → measurement → threshold.</summary>
    [JsonPropertyName("thresholds")]
    public Dictionary<string, Dictionary<string, Dictionary<string, double>>> Thresholds { get; init; } = [];

    [JsonPropertyName("allow")]
    public List<AllowEntry> Allow { get; init; } = [];

    public sealed record AllowEntry(
        [property: JsonPropertyName("renderer")] string Renderer,
        [property: JsonPropertyName("feature")] string Feature,
        [property: JsonPropertyName("check")] string Check,
        [property: JsonPropertyName("reason")] string Reason);

    public static RenderThresholds Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "RenderQuality", "render-thresholds.json");
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonSerializer.Deserialize<RenderThresholds>(File.ReadAllText(path), options) ?? new RenderThresholds();
    }

    public double? Threshold(string renderer, string feature, string check) =>
        Thresholds.TryGetValue(renderer, out var features) && features.TryGetValue(feature, out var checks) && checks.TryGetValue(check, out var value)
            ? value
            : null;

    /// <summary>
    /// The allow-list entry for a measurement; "*" stands for any feature, a trailing "*" for checks starting so. A
    /// feature's entry also covers its rendering from the binary HWP ("floating" covers "floating.hwp").
    /// </summary>
    public AllowEntry? Allowed(string renderer, string feature, string check) =>
        Allow.FirstOrDefault(a => a.Renderer == renderer && (a.Feature == feature || a.Feature + ".hwp" == feature || a.Feature == "*")
                                  && (a.Check == check || (a.Check.EndsWith('*') && check.StartsWith(a.Check[..^1], StringComparison.Ordinal))));

    public Verdict Judge(Measurement measurement)
    {
        var (renderer, feature, result) = measurement;
        if (Allowed(renderer, feature, result.Name) is not null)
            return Verdict.Allowed;
        if (Threshold(renderer, feature, result.Name) is not { } threshold)
            return result.Ok ? Verdict.Ideal : Verdict.Regression;
        const double Epsilon = 1e-4; // values are printed and kept to four decimals
        var higher = HigherIsBetter.Contains(Base(result.Name));
        var better = higher ? result.Value > threshold + Epsilon : result.Value < threshold - Epsilon;
        var worse = higher ? result.Value < threshold - Epsilon : result.Value > threshold + Epsilon;
        if (Base(result.Name) == PdfChecks.Pages)
            return Math.Abs(result.Value - threshold) < Epsilon ? Verdict.WithinThreshold : Verdict.Regression;
        return worse ? Verdict.Regression : better ? (result.Ok ? Verdict.Ideal : Verdict.Improved) : Verdict.WithinThreshold;
    }

    /// <summary>The value to keep in the file for a measurement (four decimals, rounded towards passing).</summary>
    public static string Suggest(CheckResult result)
    {
        var scaled = result.Value * 10000;
        var value = (HigherIsBetter.Contains(Base(result.Name)) ? Math.Floor(scaled + 1e-6) : Math.Ceiling(scaled - 1e-6)) / 10000;
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>Prefix of the slide renderer's compares with LibreOffice's rendering (SlideQualityTests).</summary>
    public const string VersusLibreOffice = "vs-libreoffice-";

    private static string Base(string name) =>
        name.StartsWith(Versus, StringComparison.Ordinal) ? name[Versus.Length..]
        : name.StartsWith(VersusLibreOffice, StringComparison.Ordinal) ? name[VersusLibreOffice.Length..]
        : name;
}
