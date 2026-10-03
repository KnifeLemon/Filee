// Editable view of one Preset. Shared by the Presets page and the preset popup (right-click / ✎ on the donut),
// so both always show exactly the same editor.
//
// Works on a clone; Apply() copies the edits back into the original preset (Cancel = just drop the VM).

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

/// <summary>What item templates show of a <see cref="Choice{T}"/> (XAML can't name a generic type).</summary>
public interface IChoice
{
    string Label { get; }

    /// <summary>Secondary text shown right-aligned, e.g. the category of a format.</summary>
    string? Detail { get; }
}

/// <summary>A value with a localized label for combo boxes.</summary>
public sealed record Choice<T>(T Value, string Label, string? Detail = null) : IChoice
{
    public override string ToString() => Label;
}

public sealed partial class PresetEditorViewModel : ObservableObject
{
    private static readonly HashSet<string> LossyTargets = ["jpg", "webp", "avif"];

    /// <summary>Audio targets without a bitrate or quality to choose.</summary>
    private static readonly HashSet<string> LosslessAudioTargets = ["wav", "flac", "aiff"];

    private static readonly int[] MaxHeightSteps = [0, 2160, 1440, 1080, 720, 480];
    private static readonly int[] BitrateSteps = [0, 96, 128, 192, 256, 320];

    private readonly Preset _original;
    private readonly Preset _edit;
    private readonly ILocalizer _loc;

    public PresetEditorViewModel(Preset preset, ILocalizer loc)
    {
        _original = preset;
        _edit = preset.Clone();
        _loc = loc;

        // ~150 formats in registry order, which groups them by category; the category is shown next to each name
        // and typing the first letters of a name jumps to it.
        Targets =
        [
            new Choice<string>(BuiltInData.SameAsSource, loc["presets.same_as_source"]),
            .. FormatRegistry.Known
                .Where(f => f.Writable)
                .Select(f => new Choice<string>(
                    f.Id,
                    f.Id == FormatRegistry.Folder ? loc["presets.target_folder"] : f.DisplayName,
                    FormatLabels.Category(loc, f.Category))),
        ];
        ResizeModes = Enum.GetValues<ResizeMode>().Select(v => new Choice<ResizeMode>(v, loc[$"presets.resize.{v}"])).ToList();
        PageSizes = Enum.GetValues<PdfPageSize>().Select(v => new Choice<PdfPageSize>(v, loc[$"presets.page_size.{v}"])).ToList();
        Locations = Enum.GetValues<OutputLocation>().Select(v => new Choice<OutputLocation>(v, loc[$"presets.location.{v}"])).ToList();
        Conflicts = Enum.GetValues<ConflictPolicy>().Select(v => new Choice<ConflictPolicy>(v, loc[$"presets.conflict.{v}"])).ToList();
        TiffCompressions = Enum.GetValues<TiffCompression>().Select(v => new Choice<TiffCompression>(v, v.ToString().ToUpperInvariant())).ToList();
        MediaQualities = Enum.GetValues<MediaQuality>().Select(v => new Choice<MediaQuality>(v, loc[$"presets.media_quality.{v}"])).ToList();
        MaxHeights = Steps(MaxHeightSteps, _edit.Media.MaxHeight, h => h == 0 ? loc["presets.max_height.original"] : $"{h}p");
        AudioBitrates = Steps(BitrateSteps, _edit.Media.AudioBitrateKbps, k => k == 0 ? loc["presets.audio_bitrate.auto"] : $"{k} kbps");
        ArchiveLevels = Enum.GetValues<ArchiveLevel>().Select(v => new Choice<ArchiveLevel>(v, loc[$"presets.archive_level.{v}"])).ToList();

        _name = string.IsNullOrWhiteSpace(_edit.Name) ? loc.DisplayName(_edit) : _edit.Name;
        _target = Targets.FirstOrDefault(t => t.Value == _edit.TargetFormat) ?? Targets[1];
        _resize = ResizeModes.First(r => r.Value == _edit.Image.Resize);
        _pageSize = PageSizes.First(p => p.Value == _edit.Pdf.PageSize);
        _location = Locations.First(l => l.Value == _edit.Output.Location);
        _conflict = Conflicts.First(c => c.Value == _edit.Output.Conflict);
        _tiffCompression = TiffCompressions.First(t => t.Value == _edit.Image.TiffCompression);
        _mediaQuality = MediaQualities.First(q => q.Value == _edit.Media.Quality);
        _maxHeight = MaxHeights.First(h => h.Value == _edit.Media.MaxHeight);
        _audioBitrate = AudioBitrates.First(b => b.Value == _edit.Media.AudioBitrateKbps);
        _removeAudio = _edit.Media.RemoveAudio;
        _archiveLevel = ArchiveLevels.First(l => l.Value == _edit.Archive.Level);
        _combineIntoOne = _edit.Archive.CombineIntoOne;
        _quality = _edit.Image.Quality;
        _resizePercent = _edit.Image.ResizePercent;
        _longEdge = _edit.Image.LongEdge;
        _setDpi = _edit.Image.Dpi.HasValue;
        _dpi = _edit.Image.Dpi ?? 300;
        _keepMetadata = _edit.Image.KeepMetadata;
        _keepColorProfile = _edit.Image.KeepColorProfile;
        _grayscale = _edit.Image.Grayscale;
        _background = _edit.Image.Background;
        _webpLossless = _edit.Image.WebpLossless;
        _icoSizes = string.Join(", ", _edit.Image.IcoSizes);
        _merge = _edit.Pdf.MergeIntoSingle;
        _marginMm = _edit.Pdf.MarginMm;
        _renderDpi = _edit.Pdf.RenderDpi;
        _pageRange = _edit.Pdf.PageRange;
        _splitPages = _edit.Pdf.SplitPages;
        _pdfA = _edit.Document.PdfA;
        _subfolderName = _edit.Output.SubfolderName;
        _customFolder = _edit.Output.CustomFolder;
        _pattern = _edit.Output.FileNamePattern;
        _keepDates = _edit.Output.KeepDates;
    }

    public string PresetId => _original.Id;

    public IReadOnlyList<Choice<string>> Targets { get; }
    public IReadOnlyList<Choice<ResizeMode>> ResizeModes { get; }
    public IReadOnlyList<Choice<PdfPageSize>> PageSizes { get; }
    public IReadOnlyList<Choice<OutputLocation>> Locations { get; }
    public IReadOnlyList<Choice<ConflictPolicy>> Conflicts { get; }
    public IReadOnlyList<Choice<TiffCompression>> TiffCompressions { get; }
    public IReadOnlyList<Choice<MediaQuality>> MediaQualities { get; }
    public IReadOnlyList<Choice<int>> MaxHeights { get; }
    public IReadOnlyList<Choice<int>> AudioBitrates { get; }
    public IReadOnlyList<Choice<ArchiveLevel>> ArchiveLevels { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NameError), nameof(IsValid))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowImage), nameof(ShowQuality), nameof(ShowTiff), nameof(ShowWebp), nameof(ShowIco),
        nameof(ShowPdf), nameof(ShowPdfBuild), nameof(ShowDocument), nameof(ShowPdfSplit),
        nameof(ShowMedia), nameof(ShowMediaQuality), nameof(ShowMaxHeight), nameof(ShowAudioBitrate), nameof(ShowRemoveAudio),
        nameof(ShowArchive), nameof(ShowArchiveOptions), nameof(ShowExtractNote))]
    private Choice<string> _target;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPercent), nameof(ShowLongEdge))]
    private Choice<ResizeMode> _resize;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowMargin))]
    private Choice<PdfPageSize> _pageSize;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSubfolder), nameof(ShowCustomFolder))]
    private Choice<OutputLocation> _location;

    [ObservableProperty] private Choice<ConflictPolicy> _conflict;
    [ObservableProperty] private Choice<TiffCompression> _tiffCompression;
    [ObservableProperty] private int _quality;
    [ObservableProperty] private int _resizePercent;
    [ObservableProperty] private int _longEdge;
    [ObservableProperty] private bool _setDpi;
    [ObservableProperty] private int _dpi;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeColorProfile), nameof(ColorProfileChecked))]
    private bool _keepMetadata;

    /// <summary>Keep the ICC colour profile when metadata is removed (ImageOptions.KeepColorProfile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorProfileChecked))]
    private bool _keepColorProfile;

    /// <summary>The colour profile choice only matters while metadata is removed; kept metadata includes it.</summary>
    public bool CanChangeColorProfile => !KeepMetadata;

    /// <summary>What the checkbox shows: always ticked (and disabled) while all metadata is kept.</summary>
    public bool ColorProfileChecked
    {
        get => KeepMetadata || KeepColorProfile;
        set
        {
            if (!KeepMetadata)
                KeepColorProfile = value;
        }
    }
    [ObservableProperty] private bool _grayscale;
    [ObservableProperty] private string _background;
    [ObservableProperty] private bool _webpLossless;
    [ObservableProperty] private string _icoSizes;
    [ObservableProperty] private bool _merge;
    [ObservableProperty] private double _marginMm;
    [ObservableProperty] private int _renderDpi;
    [ObservableProperty] private string _pageRange;
    [ObservableProperty] private bool _splitPages;
    [ObservableProperty] private bool _pdfA;
    [ObservableProperty] private string _subfolderName;
    [ObservableProperty] private string _customFolder;
    [ObservableProperty] private string _pattern;
    [ObservableProperty] private bool _keepDates;
    [ObservableProperty] private Choice<MediaQuality> _mediaQuality;
    [ObservableProperty] private Choice<int> _maxHeight;
    [ObservableProperty] private Choice<int> _audioBitrate;
    [ObservableProperty] private bool _removeAudio;
    [ObservableProperty] private Choice<ArchiveLevel> _archiveLevel;
    [ObservableProperty] private bool _combineIntoOne;

    private string TargetId => Target.Value;
    private FormatCategory? TargetCategory => FormatRegistry.FindById(TargetId)?.Category;
    private bool TargetIsImage => TargetId == BuiltInData.SameAsSource || TargetCategory == FormatCategory.Image;
    private bool TargetIsVideo => TargetCategory == FormatCategory.Video;
    private bool TargetIsAudio => TargetCategory == FormatCategory.Audio;
    private bool TargetIsLossyMedia => TargetIsVideo || (TargetIsAudio && !LosslessAudioTargets.Contains(TargetId));

    public bool ShowImage => TargetIsImage;
    public bool ShowQuality => TargetId == BuiltInData.SameAsSource || LossyTargets.Contains(TargetId);
    public bool ShowTiff => TargetId is "tiff" or BuiltInData.SameAsSource;
    public bool ShowWebp => TargetId is "webp" or BuiltInData.SameAsSource;
    public bool ShowIco => TargetId == "ico";
    /// <summary>PDF options (also used when a PDF source becomes images) mean nothing for media, archive and font targets.</summary>
    public bool ShowPdf => TargetCategory is not (FormatCategory.Video or FormatCategory.Audio or FormatCategory.Archive or FormatCategory.Font);
    public bool ShowPdfBuild => TargetId == "pdf";
    public bool ShowPdfSplit => TargetId == "pdf";
    public bool ShowDocument => TargetId == "pdf";
    public bool ShowMedia => ShowMediaQuality || ShowMaxHeight || ShowRemoveAudio;
    public bool ShowMediaQuality => TargetIsLossyMedia;
    public bool ShowMaxHeight => TargetIsVideo;
    public bool ShowAudioBitrate => TargetIsLossyMedia;
    public bool ShowRemoveAudio => TargetIsVideo;
    public bool ShowArchive => TargetCategory == FormatCategory.Archive;
    public bool ShowArchiveOptions => ShowArchive && TargetId != FormatRegistry.Folder;
    /// <summary>"Extract" has nothing to set; a line says what it does instead.</summary>
    public bool ShowExtractNote => TargetId == FormatRegistry.Folder;
    public bool ShowPercent => Resize.Value == ResizeMode.Percent;
    public bool ShowLongEdge => Resize.Value == ResizeMode.LongEdge;
    public bool ShowMargin => PageSize.Value != PdfPageSize.FitImage;
    public bool ShowSubfolder => Location.Value == OutputLocation.Subfolder;
    public bool ShowCustomFolder => Location.Value == OutputLocation.CustomFolder;

    public string? NameError => string.IsNullOrWhiteSpace(Name) ? _loc["presets.name_required"] : null;
    public bool IsValid => NameError is null;

    /// <summary>Writes the edits into the original preset. Returns false if the input is invalid.</summary>
    public bool Apply()
    {
        if (!IsValid)
            return false;

        // Keep a built-in preset's translatable name unless the user actually renamed it.
        if (_original.NameKey is null || Name.Trim() != _loc.DisplayName(_original))
            _original.Name = Name.Trim();

        _original.TargetFormat = TargetId;
        var image = _original.Image;
        image.Quality = Math.Clamp(Quality, 1, 100);
        image.Resize = Resize.Value;
        image.ResizePercent = Math.Clamp(ResizePercent, 1, 1000);
        image.LongEdge = Math.Clamp(LongEdge, 16, 20000);
        image.Dpi = SetDpi ? Math.Clamp(Dpi, 36, 2400) : null;
        image.KeepMetadata = KeepMetadata;
        image.KeepColorProfile = KeepColorProfile;
        image.Grayscale = Grayscale;
        image.Background = string.IsNullOrWhiteSpace(Background) ? "#FFFFFF" : Background.Trim();
        image.TiffCompression = TiffCompression.Value;
        image.WebpLossless = WebpLossless;
        image.IcoSizes = IcoSizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .Where(v => v is > 0 and <= 256)
            .Distinct()
            .ToList();
        if (image.IcoSizes.Count == 0)
            image.IcoSizes = [256, 48, 32, 16];

        var pdf = _original.Pdf;
        pdf.MergeIntoSingle = Merge;
        pdf.PageSize = PageSize.Value;
        pdf.MarginMm = Math.Clamp(MarginMm, 0, 100);
        pdf.RenderDpi = Math.Clamp(RenderDpi, 36, 1200);
        pdf.PageRange = PageRange.Trim();
        pdf.SplitPages = SplitPages;
        _original.Document.PdfA = PdfA;

        var media = _original.Media;
        media.Quality = MediaQuality.Value;
        media.MaxHeight = MaxHeight.Value;
        media.AudioBitrateKbps = AudioBitrate.Value;
        media.RemoveAudio = RemoveAudio;
        _original.Archive.Level = ArchiveLevel.Value;
        _original.Archive.CombineIntoOne = CombineIntoOne;

        var output = _original.Output;
        output.Location = Location.Value;
        output.SubfolderName = string.IsNullOrWhiteSpace(SubfolderName) ? "converted" : SubfolderName.Trim();
        output.CustomFolder = CustomFolder.Trim();
        output.FileNamePattern = string.IsNullOrWhiteSpace(Pattern) ? "{name}" : Pattern.Trim();
        output.Conflict = Conflict.Value;
        output.KeepDates = KeepDates;
        return true;
    }

    /// <summary>Fixed choices plus the preset's own value if it isn't one of them (e.g. edited by hand).</summary>
    private static List<Choice<int>> Steps(int[] steps, int current, Func<int, string> label) =>
        [.. steps.Append(current).Distinct().Select(v => new Choice<int>(v, label(v)))];
}
