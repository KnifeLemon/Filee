// Decides whether a preset can run for a set of files and why not — used to grey out donut slices.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.Services;

public sealed class PresetAvailability(ConverterCatalog catalog, ILocalizer loc, EngineDownloadService downloads)
{
    /// <summary>
    /// Returns <c>null</c> when at least one of <paramref name="formats"/> can be converted with the preset,
    /// otherwise a localized reason — naming the optional engine to download when that would help.
    /// </summary>
    public string? Check(Preset preset, IReadOnlyList<FileFormat> formats)
    {
        if (formats.Count == 0)
            return loc["error.unsupported_source"];

        var merge = preset.Pdf.MergeIntoSingle && preset.TargetFormat == "pdf";
        bool Possible(RoutePlanner planner) => formats.Any(f =>
        {
            var target = preset.TargetFormat == BuiltInData.SameAsSource ? f.Id : preset.TargetFormat;
            if (merge && f.Id == "pdf")
                return true;
            return planner.CanConvert(f.Id, target);
        });

        if (Possible(catalog.CreatePlanner()))
            return null;
        return downloads.PackageEnabling(Possible) is { } package
            ? loc.Format("engine.reason.needs_package", package.Name)
            : loc["error.no_route"];
    }
}
