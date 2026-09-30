// Editable view of one Preset. Shared by the Presets page and the preset popup (right-click / ✎ on the donut),
// so both always show exactly the same editor.
//
// Works on a clone; Apply() copies the edits back into the original preset (Cancel = just drop the VM).

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

/// <summary>A value with a localized label for combo boxes.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class PresetEditorViewModel : ObservableObject
{
    private static readonly HashSet<string> LossyTargets = ["jpg", "webp", "avif"];
    private readonly Preset _original;
    private readonly Preset _edit;
    private readonly ILocalizer _loc;

    public PresetEditorViewModel(Preset preset, ILocalizer loc)
    {
        _original = preset;
        _edit = preset.Clone();
        _loc = loc;

        Targets =
        [
            new Choice<string>(BuiltInData.SameAsSource, loc["presets.same_as_source"]),
            .. FormatRegistry.Known
                .Where(f => f.Writable)
                .Select(f => new Choice<string>(f.Id, f.DisplayName)),
        ];
        ResizeModes = Enum.GetValues<ResizeMode>().Select(v => new Choice<ResizeMode>(v, loc[$"presets.resize.{v}"])).ToList();
        PageSizes = Enum.GetValues<PdfPageSize>().Select(v => new Choice<PdfPageSize>(v, loc[$"presets.page_size.{v}"])).ToList();
        Locations = Enum.GetValues<OutputLocation>().Select(v => new Choice<OutputLocation>(v, loc[$"presets.location.{v}"])).ToList();
        Conflicts = Enum.GetValues<ConflictPolicy>().Select(v => new Choice<ConflictPolicy>(v, loc[$"presets.conflict.{v}"])).ToList();
        TiffCompressions = Enum.GetValues<TiffCompression>().Select(v => new Choice<TiffCompression>(v, v.ToString().ToUpperInvariant())).ToList();

        _name = string.IsNullOrWhiteSpace(_edit.Name) ? loc.DisplayName(_edit) : _edit.Name;
        _target = Targets.FirstOrDefault(t => t.Value == _edit.TargetFormat) ?? Targets[1];
        _resize = ResizeModes.First(r => r.Value == _edit.Image.Resize);
        _pageSize = PageSizes.First(p => p.Value == _edit.Pdf.PageSize);
        _location = Locations.First(l => l.Value == _edit.Output.Location);
        _conflict = Conflicts.First(c => c.Value == _edit.Output.Conflict);
        _tiffCompression = TiffCompressions.First(t => t.Value == _edit.Image.TiffCompression);
        _quality = _edit.Image.Quality;
        _resizePercent = _edit.Image.ResizePercent;
        _longEdge = _edit.Image.LongEdge;
        _setDpi = _edit.Image.Dpi.HasValue;
        _dpi = _edit.Image.Dpi ?? 300;
        _keepMetadata = _edit.Image.KeepMetadata;
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
    }

    public string PresetId => _original.Id;

    public IReadOnlyList<Choice<string>> Targets { get; }
    public IReadOnlyList<Choice<ResizeMode>> ResizeModes { get; }
    public IReadOnlyList<Choice<PdfPageSize>> PageSizes { get; }
    public IReadOnlyList<Choice<OutputLocation>> Locations { get; }
    public IReadOnlyList<Choice<ConflictPolicy>> Conflicts { get; }
    public IReadOnlyList<Choice<TiffCompression>> TiffCompressions { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NameError), nameof(IsValid))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowImage), nameof(ShowQuality), nameof(ShowTiff), nameof(ShowWebp), nameof(ShowIco),
        nameof(ShowPdfBuild), nameof(ShowDocument), nameof(ShowPdfSplit))]
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
    [ObservableProperty] private bool _keepMetadata;
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

    private string TargetId => Target.Value;
    private bool TargetIsImage => TargetId == BuiltInData.SameAsSource || FormatRegistry.FindById(TargetId)?.Category == FormatCategory.Image;

    public bool ShowImage => TargetIsImage;
    public bool ShowQuality => TargetId == BuiltInData.SameAsSource || LossyTargets.Contains(TargetId);
    public bool ShowTiff => TargetId is "tiff" or BuiltInData.SameAsSource;
    public bool ShowWebp => TargetId is "webp" or BuiltInData.SameAsSource;
    public bool ShowIco => TargetId == "ico";
    public bool ShowPdfBuild => TargetId == "pdf";
    public bool ShowPdfSplit => TargetId == "pdf";
    public bool ShowDocument => TargetId == "pdf";
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

        var output = _original.Output;
        output.Location = Location.Value;
        output.SubfolderName = string.IsNullOrWhiteSpace(SubfolderName) ? "converted" : SubfolderName.Trim();
        output.CustomFolder = CustomFolder.Trim();
        output.FileNamePattern = string.IsNullOrWhiteSpace(Pattern) ? "{name}" : Pattern.Trim();
        output.Conflict = Conflict.Value;
        return true;
    }
}
