// "Edit pictures" (#584): the size, format, turn and colour of the dropped pictures, chosen right before they are
// converted instead of in a preset made for each size. The window opens with the tool's last choices; Convert keeps them
// in the tool and starts ordinary conversions (ImageEditTool), so the toast, cancelling and the output rules work as usual.

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Magick;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class ImageEditViewModel : ObservableObject
{
    /// <summary>File names listed in the heading's tooltip before "…".</summary>
    private const int NamesInTip = 12;

    private readonly Preset _tool;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly Func<string, string, string> _targetFor;
    private readonly Action<IReadOnlyList<string>, Preset> _start;
    private readonly ILogger? _log;
    private readonly (int Width, int Height)? _firstSize;
    private readonly IReadOnlyList<string> _sourceFormats;
    private readonly bool _keepsLossyFormat;

    /// <param name="pictures">The pictures to convert (other files are left out by the caller).</param>
    /// <param name="tool">The "Edit pictures" tool: its last choices, name and output rule.</param>
    /// <param name="targetFor">The format a picture (path) is written in for the chosen one (<see cref="ImageEditTool.TargetFor"/>).</param>
    /// <param name="start">Starts the conversion of some of the pictures.</param>
    /// <param name="sizeOf">
    /// The pixel size of a picture as it is seen (EXIF orientation applied), null when it can't be read; for the size
    /// shown in the window.
    /// </param>
    /// <param name="targets">The formats offered after "Same format" (<see cref="ImageEditTool.TargetsFrom"/>); all by default.</param>
    public ImageEditViewModel(IReadOnlyList<string> pictures, Preset tool, ILocalizer loc, UserDataStore store,
        Func<string, string, string> targetFor, Action<IReadOnlyList<string>, Preset> start,
        Func<string, (int Width, int Height)?>? sizeOf = null, ILogger? log = null, IReadOnlyList<string>? targets = null)
    {
        Pictures = pictures;
        _tool = tool;
        _loc = loc;
        _store = store;
        _targetFor = targetFor;
        _start = start;
        _log = log;
        _firstSize = pictures.Count > 0 && sizeOf?.Invoke(pictures[0]) is { Width: > 0, Height: > 0 } size ? size : null;
        _sourceFormats = pictures.Select(path => FormatRegistry.Detect(path)?.Id ?? "").ToList();
        _keepsLossyFormat = pictures.Where((path, i) => ImageEditTool.LossyTargets.Contains(_sourceFormats[i])
            && targetFor(path, BuiltInData.SameAsSource) == BuiltInData.SameAsSource).Any();

        Targets =
        [
            new Choice<string>(BuiltInData.SameAsSource, loc["presets.same_as_source"]),
            .. (targets ?? ImageEditTool.Targets).Select(id => new Choice<string>(id, FormatRegistry.Get(id).DisplayName)),
        ];
        ResizeModes = ImageEditTool.ResizeModes.Select(v => new Choice<ResizeMode>(v, loc[$"presets.resize.{v}"])).ToList();
        Rotations = Enum.GetValues<ImageRotation>().Select(v => new Choice<ImageRotation>(v, loc[$"presets.rotation.{v}"])).ToList();
        Flips = Enum.GetValues<ImageFlip>().Select(v => new Choice<ImageFlip>(v, loc[$"presets.flip.{v}"])).ToList();
        Units = Enum.GetValues<SizeUnit>().Select(v => new Choice<SizeUnit>(v, loc[$"image_edit.unit.{v}"])).ToList();

        var image = tool.Image;
        _target = Targets.FirstOrDefault(t => t.Value == tool.TargetFormat) ?? Targets[0];
        // A free width × height is stored as CustomSize with Resize None (as presets store it, #576).
        _resize = image.CustomSize is not null
            ? ResizeModes.First(r => r.Value == ResizeMode.Custom)
            : ResizeModes.FirstOrDefault(r => r.Value == image.Resize) ?? ResizeModes[0];
        // Until one is chosen: the shape kept, so a first try never squeezes a photo.
        var custom = image.CustomSize ?? new CustomImageSize { LockAspect = true };
        _unit = Units.FirstOrDefault(u => u.Value == custom.Unit) ?? Units[0];
        _dpi = Math.Clamp(custom.Dpi, CustomImageSize.MinDpi, CustomImageSize.MaxDpi);
        _customWidth = (decimal)CustomImageSize.FromPixels(custom.ClampedWidth, _unit.Value, _dpi);
        _customHeight = (decimal)CustomImageSize.FromPixels(custom.ClampedHeight, _unit.Value, _dpi);
        _lockAspect = custom.LockAspect;
        _rotation = Rotations.FirstOrDefault(r => r.Value == image.Rotation) ?? Rotations[0];
        _flip = Flips.FirstOrDefault(f => f.Value == image.Flip) ?? Flips[0];
        _resizePercent = image.ResizePercent;
        _longEdge = image.LongEdge;
        _width = image.Width;
        _grayscale = image.Grayscale;
        _quality = image.Quality;
    }

    public IReadOnlyList<string> Pictures { get; }

    public IReadOnlyList<Choice<string>> Targets { get; }
    public IReadOnlyList<Choice<ResizeMode>> ResizeModes { get; }
    public IReadOnlyList<Choice<ImageRotation>> Rotations { get; }
    public IReadOnlyList<Choice<ImageFlip>> Flips { get; }
    public IReadOnlyList<Choice<SizeUnit>> Units { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQuality), nameof(SizeAfter))]
    private Choice<string> _target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPercent), nameof(ShowLongEdge), nameof(ShowWidth), nameof(ShowCustom), nameof(ShowDpi), nameof(SizeAfter))]
    private Choice<ResizeMode> _resize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeAfter))]
    private Choice<ImageRotation> _rotation;

    [ObservableProperty] private Choice<ImageFlip> _flip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeAfter))]
    private int _resizePercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeAfter))]
    private int _longEdge;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeAfter))]
    private int _width;

    /// <summary>With <see cref="ResizeMode.Custom"/>: the width, in <see cref="Unit"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeAfter))]
    private decimal _customWidth;

    /// <summary>With <see cref="ResizeMode.Custom"/>: the height, in <see cref="Unit"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeAfter))]
    private decimal _customHeight;

    /// <summary>With <see cref="ResizeMode.Custom"/>: fit inside the width × height instead of stretching to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeAfter))]
    private bool _lockAspect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDpi), nameof(SizeFormat), nameof(SizeAfter))]
    private Choice<SizeUnit> _unit;

    /// <summary>The resolution a size in cm, mm or inches becomes pixels at; also written into the pictures.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeAfter))]
    private int _dpi;

    [ObservableProperty] private bool _grayscale;
    [ObservableProperty] private int _quality;

    partial void OnTargetChanged(Choice<string>? oldValue, Choice<string> newValue) => Choices.Restore(oldValue, newValue, v => Target = v);
    partial void OnResizeChanged(Choice<ResizeMode>? oldValue, Choice<ResizeMode> newValue) => Choices.Restore(oldValue, newValue, v => Resize = v);
    partial void OnRotationChanged(Choice<ImageRotation>? oldValue, Choice<ImageRotation> newValue) => Choices.Restore(oldValue, newValue, v => Rotation = v);
    partial void OnFlipChanged(Choice<ImageFlip>? oldValue, Choice<ImageFlip> newValue) => Choices.Restore(oldValue, newValue, v => Flip = v);

    partial void OnUnitChanged(Choice<SizeUnit>? oldValue, Choice<SizeUnit> newValue)
    {
        if (Choices.Restore(oldValue, newValue, v => Unit = v) || oldValue is null || oldValue.Value == newValue.Value)
            return;
        // The same pixels shown in the new unit, so switching the unit never changes the size by itself.
        var dpi = Math.Clamp(Dpi, CustomImageSize.MinDpi, CustomImageSize.MaxDpi);
        CustomWidth = (decimal)CustomImageSize.FromPixels(CustomImageSize.ToPixels((double)CustomWidth, oldValue.Value, dpi), newValue.Value, dpi);
        CustomHeight = (decimal)CustomImageSize.FromPixels(CustomImageSize.ToPixels((double)CustomHeight, oldValue.Value, dpi), newValue.Value, dpi);
    }

    public bool ShowPercent => Resize.Value == ResizeMode.Percent;
    public bool ShowLongEdge => Resize.Value == ResizeMode.LongEdge;
    public bool ShowWidth => Resize.Value == ResizeMode.Width;
    public bool ShowCustom => Resize.Value == ResizeMode.Custom;

    /// <summary>A size in cm, mm or inches needs a resolution to become pixels.</summary>
    public bool ShowDpi => ShowCustom && Unit.Value != SizeUnit.Pixels;

    /// <summary>The width and height boxes: whole pixels, one decimal for mm, two for cm and inches.</summary>
    public string SizeFormat => Unit.Value switch
    {
        SizeUnit.Pixels => "0",
        SizeUnit.Millimetres => "0.#",
        _ => "0.##",
    };

    /// <summary>
    /// JPG, WEBP, AVIF and HEIC; with "Same format", when one of the pictures is one of them (a PNG has no quality).
    /// </summary>
    public bool ShowQuality => Target.Value == BuiltInData.SameAsSource ? _keepsLossyFormat : ImageEditTool.LossyTargets.Contains(Target.Value);

    /// <summary>The first picture's name, or how many pictures there are.</summary>
    public string Heading => Pictures.Count == 1 ? Path.GetFileName(Pictures[0]) : _loc.Format("image_edit.count", Pictures.Count);

    /// <summary>The heading's tooltip: the picture's whole path, or the names of the pictures.</summary>
    public string HeadingTip => Pictures.Count == 1
        ? Pictures[0]
        : string.Join('\n', Pictures.Take(NamesInTip).Select(Path.GetFileName)) + (Pictures.Count > NamesInTip ? "\n…" : "");

    /// <summary>The before → after line is shown: the first picture's size could be read.</summary>
    public bool ShowSize => _firstSize is not null;

    /// <summary>The first picture's size, "512 × 256"; empty when it can't be read.</summary>
    public string SizeBefore => _firstSize is { } size ? $"{size.Width} × {size.Height}" : "";

    /// <summary>The size the first picture is written in with the choices, "32 × 16"; empty when it can't be read.</summary>
    public string SizeAfter => SizeOfFirst() is { } size ? $"{size.Width} × {size.Height}" : "";

    /// <summary>
    /// The first picture's size once converted, worked out as the encoder does it: turned, resized, brought within what
    /// the format stores, and for an icon squared and at most 256 px.
    /// </summary>
    internal (int Width, int Height)? SizeOfFirst()
    {
        if (_firstSize is not { } size)
            return null;
        var options = Options();
        var (width, height) = options.Rotation is ImageRotation.Left90 or ImageRotation.Right90 ? (size.Height, size.Width) : size;
        if (options.CustomSize is { Width: > 0, Height: > 0, LockAspect: false } stretched)
        {
            // Stretched to exactly that size, not scaled.
            (width, height) = (stretched.ClampedWidth, stretched.ClampedHeight);
        }
        else
        {
            var scale = ImageEncoder.ResizeScale(width, height, options);
            (width, height) = (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }
        var target = _targetFor(Pictures[0], Target.Value) is var chosen && chosen == BuiltInData.SameAsSource ? _sourceFormats[0] : chosen;
        var fit = Math.Min(1, ImageEncoder.MaxSide(target) / Math.Max(width, height));
        if (fit < 1)
            (width, height) = (Math.Max(1, (int)Math.Floor(width * fit)), Math.Max(1, (int)Math.Floor(height * fit)));
        if (target == "ico")
            width = height = Math.Min(256, Math.Max(width, height));
        return (width, height);
    }

    /// <summary>The tool's picture options with the window's choices.</summary>
    internal ImageOptions Options()
    {
        var image = _tool.Image.Clone();
        image.Resize = Resize.Value;
        image.CustomSize = null;
        image.MinShortEdge = null;
        if (Resize.Value == ResizeMode.Custom)
        {
            // Stored as presets store it, CustomSize with Resize None, so an older version reads the tool (#576).
            var dpi = Math.Clamp(Dpi, CustomImageSize.MinDpi, CustomImageSize.MaxDpi);
            int Side(decimal value) =>
                Math.Clamp(CustomImageSize.ToPixels((double)value, Unit.Value, dpi), CustomImageSize.MinSide, CustomImageSize.MaxSide);
            image.Resize = ResizeMode.None;
            image.CustomSize = new CustomImageSize
            {
                Width = Side(CustomWidth),
                Height = Side(CustomHeight),
                LockAspect = LockAspect,
                Unit = Unit.Value,
                Dpi = dpi,
            };
            // A printed size: the pictures say how large they are meant to be printed.
            if (Unit.Value != SizeUnit.Pixels)
                image.Dpi = dpi;
        }
        image.ResizePercent = Math.Clamp(ResizePercent, 1, 1000);
        image.LongEdge = Math.Clamp(LongEdge, 16, 20000);
        image.Width = Math.Clamp(Width, 16, 20000);
        image.Rotation = Rotation.Value;
        image.Flip = Flip.Value;
        image.Grayscale = Grayscale;
        image.Quality = Math.Clamp(Quality, 1, 100);
        return image;
    }

    /// <summary>
    /// Keeps the choices in the tool for the next time and starts the conversions: one for each format the pictures are
    /// written in (a camera RAW kept in "its own format" becomes PNG).
    /// </summary>
    public void Convert()
    {
        var image = Options();
        Remember(image);
        foreach (var group in Pictures.GroupBy(path => _targetFor(path, Target.Value)))
            _start([.. group], ImageEditTool.ConversionOf(_tool, group.Key, image));
    }

    private void Remember(ImageOptions image)
    {
        // In the tool as saved, and in the one given to this window (maybe the same).
        ImageEditTool.Remember(_tool, Target.Value, image);
        if (_store.FindPreset(_tool.Id) is not { IsImageEditTool: true } saved)
            return;
        ImageEditTool.Remember(saved, Target.Value, image);
        try
        {
            _store.SaveLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not remember the picture edits");
        }
    }
}
