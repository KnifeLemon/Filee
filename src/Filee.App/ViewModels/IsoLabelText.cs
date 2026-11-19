using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

internal static class IsoLabelText
{
    public static int Limit(string label, IsoFileSystem filesystem) =>
        filesystem == IsoFileSystem.Joliet ? 16 : label.Any(c => c > 255) ? 15 : 30;

    public static string Counter(string label, IsoFileSystem filesystem) => $"{label.Length} / {Limit(label, filesystem)}";

    public static string Hint(IsoFileSystem filesystem, ILocalizer loc) =>
        loc[filesystem == IsoFileSystem.Joliet ? "presets.iso_label_joliet_hint" : "presets.iso_label_udf_hint"];

    public static string? Error(string label, IsoFileSystem filesystem, ILocalizer loc)
    {
        if (IsoVolumeLabels.ErrorKey(label, filesystem) is null) return null;
        return label.Length > Limit(label, filesystem)
            ? loc.Format("error.iso_label_length", filesystem == IsoFileSystem.Joliet ? "Joliet" : "UDF 2.01")
            : loc["error.iso_label_characters"];
    }
}
