// What a shared preset does, in a line or two for the preset gallery's detail view (#1530): its target or tool and the
// options it changes from the defaults, in the words of the preset editor where it has them.

using System.Globalization;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

internal static class GallerySummaries
{
    /// <summary>Most options listed for one preset.</summary>
    private const int MaxOptions = 8;

    private static readonly IReadOnlyDictionary<string, string> ToolNames = new Dictionary<string, string>
    {
        [Preset.CropTool] = "preset.crop",
        [Preset.ClipTool] = "preset.clip",
        [Preset.ImageEditTool] = "preset.image_edit",
        [Preset.HideTool] = "preset.hide",
        [Preset.AnnotateTool] = "preset.annotate",
        [Preset.BackgroundTool] = "preset.background",
        [Preset.UpscaleTool] = "preset.upscale",
    };

    private static readonly HashSet<string> LossyPictures = ["jpg", "webp", "avif", "heic", "jxl"];

    /// <summary>The name, what <paramref name="preset"/> does and its main options; <paramref name="all"/> names a group's.</summary>
    public static PresetGallerySummary Describe(Preset preset, IReadOnlyList<Preset> all, ILocalizer loc)
    {
        var what = preset.Kind switch
        {
            PresetKind.Group => loc.Format("gallery.what_group", string.Join(", ",
                preset.SubPresetIds.Select(id => all.FirstOrDefault(p => p.Id == id)).OfType<Preset>().Select(loc.DisplayName))),
            PresetKind.Tool => loc[ToolNames.GetValueOrDefault(preset.Tool ?? Preset.CropTool, "preset.crop")],
            _ => preset.TargetFormat == BuiltInData.SameAsSource
                ? loc["presets.same_as_source"]
                : "→ " + (FormatLabels.NameOf(loc, preset.TargetFormat) ?? preset.TargetFormat.ToUpperInvariant()),
        };
        var options = preset.IsGroup ? [] : Options(preset, loc).Take(MaxOptions);
        return new PresetGallerySummary(loc.DisplayName(preset), what, string.Join(" · ", options));
    }

    private static IEnumerable<string> Options(Preset preset, ILocalizer loc)
    {
        var category = FormatRegistry.FindById(preset.TargetFormat)?.Category;
        var pictures = category == FormatCategory.Image || preset.TargetFormat == BuiltInData.SameAsSource || preset.IsTool;
        var image = preset.Image;
        if (pictures)
        {
            if (LossyPictures.Contains(preset.TargetFormat) && image.Quality != new ImageOptions().Quality)
                yield return $"{loc["presets.quality"]} {image.Quality}";
            if (image.CustomSize is { } size)
                yield return $"{loc["presets.resize.Custom"]} {loc.Format("gallery.option.size", size.ClampedWidth, size.ClampedHeight)}";
            else if (image.ClampedMinShortEdge is { } edge)
                yield return $"{loc["presets.resize.MinShortEdge"]} {loc.Format("gallery.option.pixels", edge)}";
            else if (image.Resize == ResizeMode.Percent)
                yield return $"{loc["presets.resize.Percent"]} {image.ResizePercent}%";
            else if (image.Resize == ResizeMode.LongEdge)
                yield return $"{loc["presets.resize.LongEdge"]} {loc.Format("gallery.option.pixels", image.LongEdge)}";
            else if (image.Resize == ResizeMode.Width)
                yield return $"{loc["presets.resize.Width"]} {loc.Format("gallery.option.pixels", image.Width)}";
            if (image.Rotation != ImageRotation.None)
                yield return loc[$"presets.rotation.{image.Rotation}"];
            if (image.Flip != ImageFlip.None)
                yield return loc[$"presets.flip.{image.Flip}"];
            if (image.Grayscale)
                yield return loc["presets.grayscale"];
            if (image.RemoveBackground)
                yield return loc["presets.remove_background"];
            if (image.MaxSizeMb > 0)
                yield return loc.Format("gallery.option.max_size", image.MaxSizeMb.ToString("0.##", CultureInfo.CurrentCulture));
            if (!image.KeepMetadata)
                yield return loc["gallery.option.no_metadata"];
        }

        var pdf = preset.Pdf;
        if (pdf.MergeIntoSingle || image.JoinIntoOne || preset.Media.JoinIntoOne || preset.Archive.CombineIntoOne)
            yield return loc["gallery.option.merge"];
        if (preset.TargetFormat == "pdf")
        {
            if (pdf.Compression != PdfCompression.None)
                yield return loc[$"presets.pdf_compression.{pdf.Compression}"];
            if (pdf.MakesSheets)
                yield return $"{loc["presets.pages_per_sheet"]} {pdf.PagesPerSheet}";
            if (pdf.SplitPages)
                yield return loc["presets.split"];
            if (pdf.Ocr)
                yield return loc["presets.ocr"];
            if (pdf.Watermark.Length > 0)
                yield return $"{loc["presets.watermark"]}: “{pdf.Watermark}”";
        }
        if (pdf.PageRange.Length > 0)
            yield return loc.Format("gallery.option.pages", pdf.PageRange);

        if (category is FormatCategory.Video or FormatCategory.Audio)
        {
            var media = preset.Media;
            if (media.Quality != MediaQuality.Balanced)
                yield return $"{loc["presets.media_quality"]}: {loc[$"presets.media_quality.{media.Quality}"]}";
            if (media.MaxHeight > 0)
                yield return loc.Format("gallery.option.max_height", media.MaxHeight);
            if (media.RemoveAudio)
                yield return loc["presets.remove_audio"];
            if (category == FormatCategory.Video && media.TurnUpright)
                yield return loc["presets.turn_upright"];
        }

        if (preset.Translate.Enabled)
            yield return loc.Format("gallery.option.translate", LanguageName(preset.Translate.TargetLanguage));
        if (preset.AskCompression)
            yield return loc["gallery.option.ask_compression"];
        if (preset.Output.Location == OutputLocation.Subfolder)
            yield return loc.Format("gallery.option.subfolder", preset.Output.SubfolderName);
        if (preset.Output.FileNamePattern is { Length: > 0 } pattern && pattern != "{name}")
            yield return loc.Format("gallery.option.file_name", pattern);
    }

    private static string LanguageName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}

/// <summary>Converters of the preset gallery's window.</summary>
public static class GalleryConverters
{
    /// <summary>A high warning in semibold, the others regular.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter Emphasis =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.FontWeight>(high => high ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);
}
