// The allow-list of what a preset shared in the preset gallery may hold (#1530): every field of a preset and a toolbar
// profile that may appear in a shared preset.json, with the values it may take. A field that isn't listed here fails the
// check, so a field added to Preset later stays out of the gallery until someone decides it is safe and lists it.
// Shared presets get as much freedom as the model has; settings that touch the installer's files (replacing or moving
// originals, overwriting, editing metadata) are allowed and reported as warnings (SharedPresetWarnings). Only what
// can't work on another computer or would give something of the sharer's away is "left out": a folder of one's own, a
// password, a picture's or a sound's path, and names, places and copyright owners written into metadata. Those may be
// there, as an export writes them, but only empty.

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
/// An object whose field names are data (metadata fields, "XMP-dc:Title"): at most <see cref="MaxCount"/>, each name
/// passing <see cref="KeyCheck"/> (null: fine), each value checked with the rule <see cref="ValueFor"/> gives for it.
/// </summary>
internal sealed record MapRule(int MaxCount, Func<string, string?> KeyCheck, Func<string, ValueRule> ValueFor) : ValueRule;

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

    /// <summary>
    /// The kinds of preset that may be shared: every kind there is. Copy to folder comes without its folder (the
    /// installer chooses one); a warning says so.
    /// </summary>
    public static readonly IReadOnlyList<string> Kinds =
        [nameof(PresetKind.Convert), nameof(PresetKind.Group), nameof(PresetKind.Tool), nameof(PresetKind.CopyToFolder)];

    /// <summary>
    /// The tools that may be shared (<see cref="Preset.Tool"/>; null is the crop): every tool there is. A tool that runs
    /// a program or a command (none yet) must never be added here.
    /// </summary>
    public static readonly IReadOnlyList<string?> Tools =
    [
        null, Preset.CropTool, Preset.ClipTool, Preset.ImageEditTool, Preset.HideTool, Preset.AnnotateTool,
        Preset.BackgroundTool, Preset.MetadataTool, Preset.UpscaleTool,
    ];

    /// <summary>
    /// Where a shared preset may save: every location. The folder of <see cref="OutputLocation.CustomFolder"/> is left
    /// out (a path of one's own), so that one saves next to the source until the installer picks a folder.
    /// </summary>
    public static readonly IReadOnlyList<string> Locations = System.Enum.GetNames<OutputLocation>();

    /// <summary>The placeholders a file name pattern may use (<see cref="OutputPathResolver.ExpandPattern"/>).</summary>
    public static readonly IReadOnlyList<string> Placeholders = ["name", "ext", "preset", "date", "time", "index"];

    private static readonly long[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    private static EnumRule Enum<T>(params T[] except) where T : struct, System.Enum =>
        new([.. System.Enum.GetNames<T>().Where(n => !except.Select(e => e.ToString()).Contains(n))]);

    private static ObjectRule Obj(params (string Name, ValueRule Rule)[] fields) =>
        new(fields.ToDictionary(f => f.Name, f => f.Rule, StringComparer.Ordinal));

    private static readonly BoolRule Flag = new();
    private static readonly StringRule Colour = new(9, SharedText.HexColour);

    private static readonly ObjectRule Backdrop = Obj(
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
        ("canvas", Enum<BackdropCanvas>()));

    /// <summary>
    /// A metadata field given a value: one that names a person, a place or a copyright owner
    /// (<see cref="SharedText.IsPersonalField"/>) may only be emptied ([], which removes it), any other may hold text.
    /// </summary>
    private static ValueRule MetadataValue(string field) => SharedText.IsPersonalField(field)
        ? new LeftOutRule("names, places and copyright owners are personal data; a shared preset may only remove this field")
        : new ListRule(new StringRule(500), 50);

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
        ("gallery", new LeftOutRule("set by the app on a preset installed from the gallery")),
        ("image", Obj(
            ("quality", new IntRule(1, 100)),
            ("maxSizeMb", new NumberRule(0, 10_000)),
            ("maxSizeKeepsPixels", Flag),
            ("minSizeMb", new NumberRule(0, 10_000)),
            ("resize", Enum<ResizeMode>()),
            ("rotation", Enum<ImageRotation>()),
            ("flip", Enum<ImageFlip>()),
            ("annotations", new ListRule(Obj(
                ("kind", Enum<AnnotationKind>()),
                ("color", Colour),
                ("width", Enum<AnnotationWidth>()),
                ("points", new ListRule(Obj(("x", new NumberRule(-1, 2)), ("y", new NumberRule(-1, 2))), 5000)),
                ("text", new StringRule(500) { Nullable = true }),
                ("number", new IntRule(-9999, 9999))), 500)
            { Nullable = true }),
            ("annotationColor", Colour with { Nullable = true }),
            ("annotationWidth", Enum<AnnotationWidth>()),
            ("backdrop", Backdrop with { Nullable = true }),
            ("backdropLook", Backdrop with { Nullable = true }),
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
            ("hiddenAreas", new ListRule(Obj(
                ("x", new NumberRule(0, 1)),
                ("y", new NumberRule(0, 1)),
                ("width", new NumberRule(0, 1)),
                ("height", new NumberRule(0, 1)),
                ("style", Enum<HideStyle>() with { Nullable = true })), 500)
            { Nullable = true }),
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
            ("passwordLeftOut", Flag),
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
            ("clip", Obj(
                ("parts", new ListRule(Obj(
                    ("start", new NumberRule(0, 1_000_000)),
                    ("end", new NumberRule(0, 1_000_000)),
                    ("blank", Flag),
                    ("volume", new NumberRule(0, ClipPart.MaxVolume)),
                    ("muted", Flag),
                    ("speed", new NumberRule(ClipPart.MinSpeed, ClipPart.MaxSpeed))), 1000)),
                ("fadeIn", new NumberRule(0, ClipEdit.MaxFade)),
                ("fadeOut", new NumberRule(0, ClipEdit.MaxFade)),
                ("music", Obj(
                    ("path", new LeftOutRule("a sound of one's own is a path on one's computer")),
                    ("offset", new NumberRule(0, 1_000_000)),
                    ("from", new NumberRule(0, 1_000_000)),
                    ("length", new NumberRule(0, 1_000_000)),
                    ("volume", new NumberRule(0, ClipPart.MaxVolume)),
                    ("fade", Flag)) with
                { Nullable = true })) with
            { Nullable = true }),
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
            ("model", new StringRule(100, SharedText.OllamaModel) { Nullable = true }))),
        ("upscale", Obj(
            ("enabled", Flag),
            ("model", new StringRule(80, SharedText.ModelId) { Nullable = true }),
            ("scale", new IntRule(1, 8)))),
        ("metadata", Obj(
            ("set", new MapRule(200, SharedText.MetadataField, MetadataValue)),
            ("remove", new ListRule(new StringRule(100, SharedText.MetadataField), 500)),
            ("removeLocation", Flag),
            ("removeAll", Flag)) with
        { Nullable = true }),
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
            ("conflict", Enum<ConflictPolicy>()),
            ("keepDates", Flag),
            ("zipResult", Flag),
            ("onlyIfSmaller", Flag),
            ("replaceOriginal", Flag),
            ("moveInsteadOfCopy", Flag))));

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
        ("language", new StringRule(16, code => SharedPresetFolderCheck.Languages.Contains(code) ? null : "is not a language of Filee's") { Nullable = true }),
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
