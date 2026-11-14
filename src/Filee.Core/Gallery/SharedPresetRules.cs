// The allow-list of what a preset shared in the preset gallery may hold (#1530): every field of a preset and a toolbar
// profile that may appear in a shared preset.json, with the values it may take. A field that isn't listed here fails the
// check, so a field added to Preset later stays out of the gallery until someone decides it is safe and lists it.
// Fields that would carry something of the sharer's (a folder, a password, a picture's path) or run something on the
// installer's computer are listed as "left out": they may be there, as an export writes them, but only empty.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Gallery;

/// <summary>What one JSON value of a shared preset may be.</summary>
internal abstract record ValueRule
{
    /// <summary>JSON null is allowed (the field is optional in the model).</summary>
    public bool Nullable { get; init; }
}

/// <summary>An object with exactly these fields (each optional); any other field fails.</summary>
internal sealed record ObjectRule(IReadOnlyDictionary<string, ValueRule> Fields) : ValueRule;

/// <summary>true or false; <see cref="Only"/> when just one of them is allowed.</summary>
internal sealed record BoolRule(bool? Only = null) : ValueRule;

/// <summary>A whole number from <see cref="Min"/> to <see cref="Max"/>; with <see cref="Allowed"/> only those.</summary>
internal sealed record IntRule(long Min, long Max, IReadOnlyCollection<long>? Allowed = null) : ValueRule;

/// <summary>A finite number from <see cref="Min"/> to <see cref="Max"/>.</summary>
internal sealed record NumberRule(double Min, double Max) : ValueRule;

/// <summary>One of <see cref="Names"/>, as the enum's name (any case, as the app reads it).</summary>
internal sealed record EnumRule(IReadOnlyList<string> Names) : ValueRule;

/// <summary>
/// A string of at most <see cref="MaxLength"/> characters that passes <see cref="Check"/> (null: fine) and
/// the checks every shared text gets (no URL, no personal path, no control characters).
/// </summary>
internal sealed record StringRule(int MaxLength, Func<string, string?>? Check = null) : ValueRule;

/// <summary>A list of at most <see cref="MaxCount"/> values that each pass <see cref="Item"/>.</summary>
internal sealed record ListRule(ValueRule Item, int MaxCount) : ValueRule;

/// <summary>
/// A field left out of shared presets: absent, null, "", false, 0, [] or {} only. <see cref="Why"/> says why, for the
/// message.
/// </summary>
internal sealed record LeftOutRule(string Why) : ValueRule;

/// <summary>The allow-list itself, one <see cref="ObjectRule"/> per part of a <see cref="Settings.PresetBundle"/>.</summary>
internal static class SharedPresetRules
{
    /// <summary>Most presets one shared preset.json holds.</summary>
    public const int MaxPresets = 20;

    /// <summary>Most toolbar profiles (donuts) one shared preset.json holds.</summary>
    public const int MaxProfiles = 1;

    /// <summary>Largest preset.json, in bytes.</summary>
    public const int MaxBundleBytes = 256 * 1024;

    /// <summary>How deep the JSON may nest; a preset needs 5 levels.</summary>
    public const int MaxDepth = 16;

    /// <summary>The kinds of preset that may be shared. Copy to folder needs a folder of one's own.</summary>
    public static readonly IReadOnlyList<string> Kinds = [nameof(PresetKind.Convert), nameof(PresetKind.Group), nameof(PresetKind.Tool)];

    /// <summary>
    /// The tools that may be shared (<see cref="Preset.Tool"/>; null is the crop). Metadata is left out: what it
    /// changes (author, GPS) is about people.
    /// </summary>
    public static readonly IReadOnlyList<string?> Tools =
    [
        null, Preset.CropTool, Preset.ClipTool, Preset.ImageEditTool, Preset.HideTool, Preset.AnnotateTool,
        Preset.BackgroundTool, Preset.UpscaleTool,
    ];

    /// <summary>
    /// Where a shared preset may save: the default location, next to the source, or in a folder next to it (named by
    /// <see cref="OutputRule.SubfolderName"/>, which may not hold a separator). Never a folder of its own.
    /// </summary>
    public static readonly IReadOnlyList<string> Locations =
        [nameof(OutputLocation.Default), nameof(OutputLocation.SameFolder), nameof(OutputLocation.Subfolder)];

    /// <summary>The placeholders a file name pattern may use (<see cref="OutputPathResolver.ExpandPattern"/>).</summary>
    public static readonly IReadOnlyList<string> Placeholders = ["name", "ext", "preset", "date", "time", "index"];

    private static readonly long[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    private static EnumRule Enum<T>(params T[] except) where T : struct, System.Enum =>
        new([.. System.Enum.GetNames<T>().Where(n => !except.Select(e => e.ToString()).Contains(n))]);

    private static ObjectRule Obj(params (string Name, ValueRule Rule)[] fields) =>
        new(fields.ToDictionary(f => f.Name, f => f.Rule, StringComparer.Ordinal));

    private static readonly BoolRule Flag = new();
    private static readonly StringRule Colour = new(9, SharedText.HexColour);

    /// <summary>The fields of one preset.</summary>
    public static readonly ObjectRule PresetFields = Obj(
        ("id", new StringRule(64, SharedText.Id)),
        ("name", new StringRule(80)),
        ("nameKey", new StringRule(80, SharedText.NameKey) { Nullable = true }),
        ("targetFormat", new StringRule(32, SharedText.Id)),
        ("kind", new EnumRule(Kinds)),
        ("tool", new StringRule(32, SharedText.Id) { Nullable = true }),
        ("subPresetIds", new ListRule(new StringRule(64, SharedText.Id), Preset.MaxSubPresets)),
        ("icon", new StringRule(48, SharedText.Id) { Nullable = true }),
        ("shortName", new StringRule(40) { Nullable = true }),
        ("show", Enum<LabelShow>() with { Nullable = true }),
        ("direction", Enum<LabelDirection>() with { Nullable = true }),
        ("askCompression", Flag),
        ("image", Obj(
            ("quality", new IntRule(1, 100)),
            ("maxSizeMb", new NumberRule(0, 10_000)),
            ("maxSizeKeepsPixels", Flag),
            ("minSizeMb", new NumberRule(0, 10_000)),
            ("resize", Enum<ResizeMode>()),
            ("rotation", Enum<ImageRotation>()),
            ("flip", Enum<ImageFlip>()),
            ("annotations", new LeftOutRule("marks are drawn on one picture, never saved with a preset")),
            ("annotationColor", Colour with { Nullable = true }),
            ("annotationWidth", Enum<AnnotationWidth>()),
            ("backdrop", new LeftOutRule("set on one conversion, never saved with a preset")),
            ("backdropLook", Obj(
                ("kind", Enum<BackdropKind>()),
                ("from", Colour),
                ("to", Colour),
                ("color", Colour),
                ("picture", new LeftOutRule("a picture of one's own is a path on one's computer")),
                ("pictureWritten", new LeftOutRule("belongs to a picture of one's own")),
                ("pictureLength", new LeftOutRule("belongs to a picture of one's own")),
                ("padding", new IntRule(0, BackdropOptions.MaxPadding)),
                ("corners", new IntRule(0, BackdropOptions.MaxCorners)),
                ("shadow", new IntRule(0, BackdropOptions.MaxShadow)),
                ("canvas", Enum<BackdropCanvas>())) with
            { Nullable = true }),
            ("resizePercent", new IntRule(1, 1000)),
            ("longEdge", new IntRule(1, CustomImageSize.MaxSide)),
            ("width", new IntRule(1, CustomImageSize.MaxSide)),
            ("customSize", Obj(
                ("width", new IntRule(1, CustomImageSize.MaxSide)),
                ("height", new IntRule(1, CustomImageSize.MaxSide)),
                ("lockAspect", Flag),
                ("shrinkOnly", Flag),
                ("unit", Enum<SizeUnit>()),
                ("dpi", new IntRule(CustomImageSize.MinDpi, CustomImageSize.MaxDpi))) with
            { Nullable = true }),
            ("minShortEdge", new IntRule(1, CustomImageSize.MaxSide) { Nullable = true }),
            ("dpi", new IntRule(1, CustomImageSize.MaxDpi) { Nullable = true }),
            ("keepMetadata", Flag),
            ("keepColorProfile", Flag),
            ("maxCompression", Flag),
            ("keepSourceQuality", Flag),
            ("grayscale", Flag),
            ("trimTransparent", Flag),
            ("removeBackground", Flag),
            ("backgroundTolerance", new IntRule(0, 100)),
            ("keepEnclosedBackground", Flag),
            ("background", Colour),
            ("tiffCompression", Enum<TiffCompression>()),
            ("multiPageTiff", Flag),
            ("webpLossless", Flag),
            ("joinIntoOne", Flag),
            ("joinDirection", Enum<ImageJoinDirection>()),
            ("joinFit", Enum<ImageJoinFit>()),
            ("joinGap", new IntRule(0, ImageOptions.MaxJoinGap)),
            ("joinBackground", new StringRule(16, SharedText.JoinBackground)),
            ("fitsTiff", Enum<FitsTiffMode>()),
            ("fitsDebayer", Flag),
            ("icoSizes", new ListRule(new IntRule(16, 256, IcoSizes), IcoSizes.Length)),
            ("hideStyle", Enum<HideStyle>()),
            ("hideStrength", new IntRule(HideTool.MinStrength, HideTool.MaxStrength)),
            ("hiddenAreas", new LeftOutRule("the parts of one picture to hide, never saved with a preset")),
            ("animationSpeed", new NumberRule(0.1, 10)))),
        ("pdf", Obj(
            ("mergeIntoSingle", Flag),
            ("sourceBookmarks", Flag),
            ("normalizeMergePaper", Flag),
            ("mergePaper", Enum<PdfSheetPaper>()),
            ("mergeOrientation", Enum<PdfSheetOrientation>()),
            ("pageSize", Enum<PdfPageSize>()),
            ("marginMm", new NumberRule(0, 200)),
            ("renderDpi", new IntRule(1, 2400)),
            ("pageRange", new StringRule(200, SharedText.PageRange)),
            ("askPageRange", Flag),
            ("splitPages", Flag),
            ("splitEvery", new IntRule(0, Conversion.PageGroups.MaxSize)),
            ("splitParts", new IntRule(0, Conversion.PageGroups.MaxSize)),
            ("rotation", Enum<PdfRotation>()),
            ("pagesPerSheet", new IntRule(0, 9, [0, 1, .. PdfOptions.SheetLayouts.Select(n => (long)n)])),
            ("sheetPaper", Enum<PdfSheetPaper>()),
            ("sheetOrientation", Enum<PdfSheetOrientation>()),
            ("sheetOrder", Enum<PdfSheetOrder>()),
            ("sheetMarginMm", new NumberRule(0, 200)),
            ("sheetGapMm", new NumberRule(0, 200)),
            ("sheetBorders", Flag),
            ("combinePages", Flag),
            ("trimPageMargins", Flag),
            ("splitTallImage", Flag),
            ("compression", Enum<PdfCompression>()),
            ("removeMetadata", Flag),
            ("compressWithGhostscript", Flag),
            ("watermark", new StringRule(100)),
            ("pageNumbers", Flag),
            ("password", new LeftOutRule("a password is never shared")),
            ("passwordLeftOut", new BoolRule(false)),
            ("ocr", Flag),
            ("ocrLanguage", new StringRule(64, SharedText.OcrLanguages)))),
        ("document", Obj(
            ("pdfA", Flag),
            ("leaveOutPictures", Flag))),
        ("media", Obj(
            ("quality", Enum<MediaQuality>()),
            ("maxHeight", new IntRule(0, 8640)),
            ("maxSizeMb", new NumberRule(0, 100_000)),
            ("maxSizePercent", new IntRule(0, 100)),
            ("audioBitrateKbps", new IntRule(0, 1536)),
            ("removeAudio", Flag),
            ("cropFrame", new StringRule(16, SharedText.CropFrame)),
            ("audioChannels", new IntRule(0, 8)),
            ("audioSampleRate", new IntRule(0, 384_000)),
            ("wavFormat", Enum<WavSampleFormat>()),
            ("videoCodec", Enum<VideoCodec>()),
            ("audioCodec", Enum<AudioCodec>()),
            ("hardwareEncoding", Flag),
            ("saveEveryFrame", Flag),
            ("extractMotionPhoto", Flag),
            ("caption", Flag),
            ("spokenLanguage", new StringRule(16, SharedText.SpokenLanguage)),
            ("livePhoto", Enum<LivePhotoKind>()),
            ("livePhotoStart", new NumberRule(0, 86_400)),
            ("livePhotoLength", new NumberRule(0, 60)),
            ("livePhotoCover", new NumberRule(0, 60)),
            ("frameInterval", new IntRule(1, 86_400)),
            ("ringtoneStart", new NumberRule(0, 86_400)),
            ("ringtoneLength", new NumberRule(0, 600)),
            ("askRingtonePart", Flag),
            ("exactCut", Flag),
            ("splitEverySeconds", new IntRule(0, 86_400)),
            ("splitParts", new IntRule(0, MediaOptions.MaxSplitParts)),
            ("clip", new LeftOutRule("a clip is cut from one video, and its music is a file of one's own")),
            ("joinIntoOne", Flag),
            ("loopMusic", Flag),
            ("slideshow", Flag),
            ("slideSeconds", new NumberRule(0, 600)),
            ("crossfade", Flag),
            ("copyAudioWhenPossible", Flag),
            ("removeMetadata", Flag))),
        ("archive", Obj(
            ("combineIntoOne", Flag),
            ("level", Enum<ArchiveLevel>()))),
        ("trace", Obj(
            ("mode", Enum<TracingMode>()),
            ("colors", new IntRule(TracingOptions.MinColors, TracingOptions.MaxColors)),
            ("detail", new IntRule(0, 100)))),
        ("cad", Obj(
            ("version", Enum<CadVersion>()),
            ("colors", Enum<CadColors>()))),
        ("gcode", Obj(
            ("view", Enum<GcodeView>()))),
        ("translate", Obj(
            ("enabled", Flag),
            ("targetLanguage", new StringRule(16, SharedText.LanguageCode)),
            ("sourceLanguage", new StringRule(16, SharedText.LanguageCode)),
            ("model", new LeftOutRule("the AI model is the installer's own choice (Settings)")))),
        ("upscale", Obj(
            ("enabled", Flag),
            ("model", new StringRule(80, SharedText.ModelId) { Nullable = true }),
            ("scale", new IntRule(1, 8)))),
        ("metadata", new LeftOutRule("metadata edits write names, places and dates into files")),
        ("output", Obj(
            ("location", new EnumRule(Locations)),
            ("askEveryTime", Flag),
            ("subfolderName", new StringRule(64, SharedText.FileNamePattern)),
            ("subfolderOnlyForSeveralFiles", Flag),
            ("customFolder", new LeftOutRule("a folder is a path on one's own computer")),
            ("keepFoldersFrom", new LeftOutRule("a folder is a path on one's own computer")),
            ("fileNamePattern", new StringRule(120, SharedText.FileNamePattern)),
            ("renames", new ListRule(Obj(
                ("kind", Enum<RenameKind>()),
                ("find", new StringRule(100)),
                ("replace", new StringRule(100, SharedText.RenameReplace)),
                // Written by an export, never read back (RenameStep.IsBlank has no setter).
                ("isBlank", Flag)), 10)),
            ("conflict", new EnumRule([nameof(ConflictPolicy.Rename), nameof(ConflictPolicy.Skip)])),
            ("keepDates", Flag),
            ("zipResult", Flag),
            ("onlyIfSmaller", Flag),
            ("replaceOriginal", new BoolRule(false)),
            ("moveInsteadOfCopy", new BoolRule(false)))));

    /// <summary>The fields of one toolbar profile (a donut).</summary>
    public static readonly ObjectRule ProfileFields = Obj(
        ("id", new StringRule(64, SharedText.Id)),
        ("name", new StringRule(80)),
        ("nameKey", new StringRule(80, SharedText.NameKey) { Nullable = true }),
        ("extensions", new ListRule(new StringRule(16, SharedText.Extension), 400)),
        ("isFallback", Flag),
        ("presetIds", new ListRule(new StringRule(64, SharedText.Id), ToolbarProfile.MaxSlices)),
        ("backPresetIds", new ListRule(new StringRule(64, SharedText.Id), ToolbarProfile.MaxSlices) { Nullable = true }),
        ("labelShow", Enum<LabelShow>() with { Nullable = true }),
        ("labelScale", new NumberRule(ToolbarProfile.MinLabelScale, ToolbarProfile.MaxLabelScale)));

    /// <summary>The top of preset.json.</summary>
    public static readonly ObjectRule BundleFields = Obj(
        ("schemaVersion", new IntRule(1, 1)),
        ("settingsSchema", new IntRule(1, Settings.AppSettings.CurrentSchemaVersion) { Nullable = true }),
        ("presets", new ListRule(PresetFields, MaxPresets)),
        ("profiles", new ListRule(ProfileFields, MaxProfiles)));

    /// <summary>
    /// Every field path the allow-list knows (<c>image.quality</c>, <c>output.renames[].find</c>), with whether it is
    /// left out: for the tests that compare it with the model, and for the documentation.
    /// </summary>
    public static IEnumerable<(string Path, bool LeftOut)> Paths(ObjectRule rule, string prefix = "")
    {
        foreach (var (name, field) in rule.Fields)
        {
            var path = prefix + name;
            yield return (path, field is LeftOutRule);
            var inner = field is ListRule list ? list.Item : field;
            if (inner is ObjectRule nested)
                foreach (var child in Paths(nested, path + (field is ListRule ? "[]." : ".")))
                    yield return child;
        }
    }

    /// <summary>The format id <paramref name="extension"/> stands for, for <c>formats.from</c>.</summary>
    internal static string? FormatOfExtension(string extension) => FormatRegistry.FindByExtension(extension)?.Id;
}
