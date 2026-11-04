// State of the "Add background" window: one picture at a time, shown set on the look as the file will be, drawn live
// by the window (the canvas, background, rounded corners and shadow from the same layout and shadow formulas the file
// is composed with, and the picture and the one behind it in the colours the file gets, so moving a slider shows at
// once); the look (background, padding, corners, shadow, canvas), one for all pictures or one each; and the conversion
// that writes each picture, which runs in the toast like any other. The look is kept in the tool for the next time.

using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Magick;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class BackgroundViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The gradients offered first (top left → bottom right), with what a screen reader says for each.
    /// </summary>
    public static readonly (string From, string To, string NameKey)[] GradientPresets =
    [
        ("#F6B26B", "#E06C9F", "background.gradient_sunset"), ("#8EC5FC", "#E0C3FC", "background.gradient_sky"),
        ("#43E97B", "#38F9D7", "background.gradient_mint"), ("#FA709A", "#FEE140", "background.gradient_peach"),
        ("#30CFD0", "#330867", "background.gradient_ocean"), ("#A18CD1", "#FBC2EB", "background.gradient_lilac"),
    ];

    /// <summary>The colours offered first, with what a screen reader says for each.</summary>
    public static readonly (string Color, string NameKey)[] ColorPresets =
    [
        ("#FFFFFF", "background.color_white"), ("#F2F0EB", "background.color_paper"), ("#1E1E1E", "background.color_charcoal"),
        ("#2D3A8C", "background.color_navy"), ("#E8D5C4", "background.color_sand"), ("#C9E4DE", "background.color_sage"),
    ];

    /// <summary>The preview of a picture behind is read as large as it takes to cover a square this wide.</summary>
    private const int PictureBehindSide = 1600;

    /// <summary>The preview of a picture behind is at most this many pixels along its longer side.</summary>
    private const int PictureBehindLongest = 4096;

    private readonly IReadOnlyList<string> _files;
    private readonly Preset _tool;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly Action<string, Preset> _export;
    private readonly ILogger? _log;
    private readonly BackdropOptions[] _looks;
    private int _originalWidth, _originalHeight;
    private bool _closed;
    /// <summary>
    /// The pictures behind read so far (null: can't be read), with their files' stamps when they were read.
    /// </summary>
    private readonly Dictionary<string, (Bitmap? Bitmap, (DateTime Written, long Length) Stamp)> _backgroundPictures = [];
    private bool _syncing;

    /// <summary>How each picture went: saved, skipped or failed (none yet while it is open).</summary>
    private readonly CropItemState?[] _outcomes;

    /// <summary>The picture each of <see cref="Problems"/> is about.</summary>
    private readonly List<int> _problemFiles = [];

    public BackgroundViewModel(IReadOnlyList<string> files, Preset tool, ILocalizer loc, UserDataStore store,
        Action<string, Preset> export, ILogger? log = null)
    {
        _files = files;
        _tool = tool;
        _loc = loc;
        _store = store;
        _export = export;
        _log = log;
        var look = BackgroundTool.LookOf(tool);
        _remembered = tool.Image.BackdropLook is null ? null : look.Clone();
        _looks = [.. files.Select(_ => look.Clone())];
        _outcomes = new CropItemState?[files.Count];
        _hasMultipleFiles = files.Count > 1;
        Gradients = [.. GradientPresets.Select(g => new BackdropSwatch(g.From, g.To, g.NameKey, this) { Name = loc[g.NameKey] })];
        Colors = [.. ColorPresets.Select(c => new BackdropSwatch(c.Color, c.Color, c.NameKey, this) { Name = loc[c.NameKey] })];
        loc.LanguageChanged += OnLanguageChanged;
        ShowLook();
        _ = LoadFileAsync(0);
    }

    public IReadOnlyList<BackdropSwatch> Gradients { get; }
    public IReadOnlyList<BackdropSwatch> Colors { get; }

    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private string _currentFileName = "";
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private bool _hasMultipleFiles;

    /// <summary>
    /// The picture itself, preview-sized, in the colours the file gets: the window sets it on the look live.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private Bitmap? _pictureImage;

    /// <summary>The preview's canvas, in units of a canvas <see cref="PreviewSide"/> long.</summary>
    [ObservableProperty] private double _canvasWidth = PreviewSide;
    [ObservableProperty] private double _canvasHeight = PreviewSide;
    [ObservableProperty] private Avalonia.Thickness _pictureMargin;
    [ObservableProperty] private double _pictureWidth;
    [ObservableProperty] private double _pictureHeight;
    [ObservableProperty] private Avalonia.CornerRadius _pictureCorners;
    [ObservableProperty] private IBrush? _backgroundBrush;

    /// <summary>The picture's shadow as Avalonia draws it (null for none): the same as the file's.</summary>
    [ObservableProperty] private IEffect? _shadowEffect;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _statusNotice = "";

    /// <summary>
    /// The picture to put behind can't be read: the gradient shows meanwhile, and Save waits for another picture or
    /// another background (the file would be refused).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _pictureUnreadable;

    /// <summary>
    /// What the window says while the picture behind can't be read ("" otherwise): a text that appears is what a
    /// screen reader announces, where a text only shown wasn't.
    /// </summary>
    [ObservableProperty] private string _pictureNotice = "";

    partial void OnPictureUnreadableChanged(bool value) => PictureNotice = value ? _loc["background.picture_unreadable"] : "";

    /// <summary>
    /// All pictures are done and some could not be opened or saved: the window stays open on <see cref="Summary"/> and
    /// <see cref="Problems"/> until it is closed (as "Pixelate / blur" does).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private bool _isFinished;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _skippedCount;
    [ObservableProperty] private int _failedCount;

    /// <summary>"file: reason" for each picture that could not be opened or saved.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    public bool IsEditing => !IsFinished;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    /// <summary>One look for every picture (on), or one each.</summary>
    [ObservableProperty] private bool _sameLook = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGradient), nameof(IsColor), nameof(IsPicture))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private BackdropKind _kind;

    [ObservableProperty] private Color _fromColor;
    [ObservableProperty] private Color _toColor;
    [ObservableProperty] private Color _fillColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PictureName), nameof(HasPicture))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string? _picture;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaddingText))]
    private int _padding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CornersText))]
    private int _corners;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShadowText))]
    private int _shadow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuto), nameof(IsSquare), nameof(IsLandscape), nameof(IsPortrait))]
    private BackdropCanvas _canvas;

    public bool IsGradient => Kind == BackdropKind.Gradient;
    public bool IsColor => Kind == BackdropKind.Color;
    public bool IsPicture => Kind == BackdropKind.Picture;
    public bool IsAuto => Canvas == BackdropCanvas.Auto;
    public bool IsSquare => Canvas == BackdropCanvas.Square;
    public bool IsLandscape => Canvas == BackdropCanvas.Landscape;
    public bool IsPortrait => Canvas == BackdropCanvas.Portrait;
    public bool HasPicture => !string.IsNullOrEmpty(Picture);
    public string PictureName => HasPicture ? Path.GetFileName(Picture!) : _loc["background.no_picture"];

    public string PaddingText => $"{Padding} %";

    /// <summary>The corner radius in pixels of the file, as it will be.</summary>
    public string CornersText => _originalWidth == 0 ? $"{Corners} %" : $"{FileLayout().Radius} px";

    public string ShadowText => Shadow == 0 ? _loc["background.shadow_none"]
        : Shadow < 40 ? _loc["background.shadow_light"] : Shadow < 75 ? _loc["background.shadow_soft"] : _loc["background.shadow_strong"];

    public string SaveText => _loc[CurrentIndex < _files.Count - 1 ? "background.save_next" : "background.save"];

    public int MaxPadding => BackdropOptions.MaxPadding;
    public int MaxCorners => BackdropOptions.MaxCorners;
    public int MaxShadow => BackdropOptions.MaxShadow;

    /// <summary>The look of the picture shown.</summary>
    internal BackdropOptions Look => _looks[CurrentIndex];

    public event Action? CloseRequested;

    /// <summary>The name of the pictures the picker offers.</summary>
    public string PicturesFilterName => _loc["profile.images"];

    /// <summary>Asks for a picture to put behind (the window's file picker); null when none was chosen.</summary>
    public Func<Task<string?>>? PickPicture { get; set; }

    // ───────── The look ─────────

    /// <summary>Shows the current picture's look in the controls.</summary>
    private void ShowLook()
    {
        _syncing = true;
        var look = _looks[CurrentIndex];
        Kind = look.Kind;
        FromColor = ParseColor(look.From);
        ToColor = ParseColor(look.To);
        FillColor = ParseColor(look.Color);
        Picture = look.Picture;
        Padding = look.Padding;
        Corners = look.Corners;
        Shadow = look.Shadow;
        Canvas = look.Canvas;
        _syncing = false;
        UpdateSwatches();
    }

    /// <summary>
    /// Writes a change into the look of the picture shown, or of every picture, and draws the preview again.
    /// </summary>
    private void Change(Action<BackdropOptions> change)
    {
        if (_syncing)
            return;
        foreach (var look in SameLook ? _looks : [_looks[CurrentIndex]])
            change(look);
        UpdateSwatches();
        OnPropertyChanged(nameof(CornersText));
        ShowPreview();
    }

    partial void OnKindChanged(BackdropKind value) => Change(look => look.Kind = value);
    partial void OnFromColorChanged(Color value) => Change(look => look.From = Hex(value));
    partial void OnToColorChanged(Color value) => Change(look => look.To = Hex(value));
    partial void OnFillColorChanged(Color value) => Change(look => look.Color = Hex(value));
    partial void OnPictureChanged(string? value) => Change(look => look.Picture = value);
    partial void OnPaddingChanged(int value) => Change(look => look.Padding = value);
    partial void OnCornersChanged(int value) => Change(look => look.Corners = value);
    partial void OnShadowChanged(int value) => Change(look => look.Shadow = value);
    partial void OnCanvasChanged(BackdropCanvas value) => Change(look => look.Canvas = value);

    /// <summary>Turning "same look" on gives every picture the look of the one shown.</summary>
    partial void OnSameLookChanged(bool value)
    {
        if (!value)
            return;
        for (var i = 0; i < _looks.Length; i++)
            _looks[i] = _looks[CurrentIndex].Clone();
    }

    [RelayCommand]
    private void SetKind(BackdropKind kind) => Kind = kind;

    [RelayCommand]
    private void SetCanvas(BackdropCanvas canvas) => Canvas = canvas;

    /// <summary>A gradient or colour swatch was chosen.</summary>
    internal void Choose(BackdropSwatch swatch)
    {
        if (Gradients.Contains(swatch))
        {
            Kind = BackdropKind.Gradient;
            FromColor = ParseColor(swatch.From);
            ToColor = ParseColor(swatch.To);
        }
        else
        {
            Kind = BackdropKind.Color;
            FillColor = ParseColor(swatch.From);
        }
    }

    [RelayCommand]
    private async Task ChoosePictureAsync()
    {
        if (PickPicture is not null && await PickPicture() is { } path)
        {
            // Picked again (fixed, say, since it couldn't be read): read again.
            Forget(path);
            Picture = path;
            Kind = BackdropKind.Picture;
            ShowPreview();
        }
    }

    private void UpdateSwatches()
    {
        var look = _looks[CurrentIndex];
        foreach (var swatch in Gradients)
            swatch.IsSelected = look.Kind == BackdropKind.Gradient && Same(swatch.From, look.From) && Same(swatch.To, look.To);
        foreach (var swatch in Colors)
            swatch.IsSelected = look.Kind == BackdropKind.Color && Same(swatch.From, look.Color);
    }

    private static bool Same(string a, string b) => string.Equals(a.TrimStart('#'), b.TrimStart('#'), StringComparison.OrdinalIgnoreCase);

    internal static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color ParseColor(string? text) => Color.TryParse(text, out var color) ? color : Avalonia.Media.Colors.White;

    // ───────── Pictures and the preview ─────────

    /// <summary>
    /// Opens picture <paramref name="index"/>; past the last one, closes, or shows the summary when a picture could not
    /// be opened or saved.
    /// </summary>
    public async Task LoadFileAsync(int index)
    {
        if (_closed)
            return;
        if (index >= _files.Count)
        {
            Finish();
            return;
        }
        IsBusy = true;
        CurrentIndex = index;
        var path = _files[index];
        CurrentFileName = Path.GetFileName(path);
        CounterText = _loc.Format("crop.page_counter", index + 1, _files.Count);
        OnPropertyChanged(nameof(SaveText));
        ShowLook();
        try
        {
            var (preview, width, height) = await HideService.LoadPreviewAsync(path, _tool);
            Bitmap bitmap;
            using (preview)
            {
                bitmap = await Task.Run(() =>
                {
                    // In the colours the file gets: a CMYK, greyscale or wide-gamut picture is brought to sRGB as seen.
                    BackdropComposer.ToSrgb(preview);
                    return HideService.ToBitmap(preview);
                });
            }
            if (_closed)
            {
                bitmap.Dispose();
                return;
            }
            var old = PictureImage;
            (_originalWidth, _originalHeight) = (width, height);
            PictureImage = bitmap;
            old?.Dispose();
            OnPropertyChanged(nameof(CornersText));
            ShowPreview();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogInformation(ex, "Could not open {File} to add a background", CurrentFileName);
            AddProblem(index, ex is NotSupportedException ? "error.unsupported_source" : "error.conversion_failed");
            IsBusy = false;
            await LoadFileAsync(index + 1);
            return;
        }
        IsBusy = false;
    }

    /// <summary>The preview canvas's longer side, in the units the window scales to fit.</summary>
    internal const double PreviewSide = 1000;

    /// <summary>
    /// Where everything goes in the file: the picture's size on its canvas (smaller when the canvas would be over the
    /// pixel limit), laid out as the file is composed.
    /// </summary>
    private BackdropComposer.Layout FileLayout()
    {
        var look = Look.Clamped();
        var (width, height) = BackdropComposer.PictureSizeOn(_originalWidth, _originalHeight, look);
        return BackdropComposer.LayoutOf(width, height, look);
    }

    /// <summary>
    /// Lays the preview out for the look: the canvas, where the picture goes, its corners and its shadow, from the same
    /// layout and shadow formulas the file is composed with (BackdropComposer), scaled to <see cref="PreviewSide"/>.
    /// </summary>
    private void ShowPreview()
    {
        if (_originalWidth == 0 || _closed)
            return;
        var look = Look.Clamped();
        var layout = FileLayout();
        var (pictureWidth, pictureHeight) = BackdropComposer.PictureSizeOn(_originalWidth, _originalHeight, look);
        var scale = PreviewSide / Math.Max(layout.Width, layout.Height);
        CanvasWidth = layout.Width * scale;
        CanvasHeight = layout.Height * scale;
        PictureMargin = new Avalonia.Thickness(layout.X * scale, layout.Y * scale, 0, 0);
        PictureWidth = pictureWidth * scale;
        PictureHeight = pictureHeight * scale;
        PictureCorners = new Avalonia.CornerRadius(Math.Min(layout.Radius, Math.Min(pictureWidth, pictureHeight) / 2) * scale);
        if (look.Shadow == 0)
            ShadowEffect = null;
        else
        {
            var shape = BackdropComposer.ShadowOf(pictureWidth, pictureHeight, look.Shadow);
            // Avalonia (Skia) turns a blur radius into a sigma of 0.288675 × radius + 0.5.
            ShadowEffect = new DropShadowEffect
            {
                BlurRadius = Math.Max(0, (shape.Sigma * scale - 0.5) / 0.288675),
                OffsetX = 0,
                OffsetY = shape.Drop * scale,
                Color = Avalonia.Media.Colors.Black,
                Opacity = shape.Opacity,
            };
        }
        BackgroundBrush = BrushOf(look);
        // Off the screen now (as the picture itself is disposed once replaced).
        foreach (var bitmap in _retired)
            bitmap.Dispose();
        _retired.Clear();
        SizeText = $"{layout.Width} × {layout.Height} px · PNG";
        // A redraw can start reading a picture behind, which Save waits for.
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The background as a brush: the gradient corner to corner, the colour, or one's own picture covering.
    /// </summary>
    private IBrush BrushOf(BackdropOptions look)
    {
        // Worked out once: set false then true again on every redraw, the notice blinked (and a screen reader could
        // say it again on every slider step).
        var unreadable = false;
        var picture = look.Kind == BackdropKind.Picture ? BackgroundPicture(look.Picture, out unreadable) : null;
        PictureUnreadable = unreadable;
        if (look.Kind == BackdropKind.Color)
            return new SolidColorBrush(ParseColor(look.Color));
        if (picture is not null)
            return new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
        // While it is read: white, as under its see-through parts, rather than a look not chosen.
        if (look.Kind == BackdropKind.Picture && look.Picture is { } reading && _reading.Contains(reading))
            return new SolidColorBrush(Avalonia.Media.Colors.White);
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops = { new GradientStop(ParseColor(look.From), 0), new GradientStop(ParseColor(look.To), 1) },
        };
    }

    /// <summary>
    /// One's own picture for the preview, read as the file's composer reads it (in sRGB, white where see-through), as
    /// large as covering a square <see cref="PictureBehindSide"/> across takes, once (and again when it is picked
    /// again); null while it is read or when it can't be (<see cref="PictureUnreadable"/> says so), and the window
    /// won't save. Its file's stamp as it was read goes with the conversion, which refuses a picture behind changed
    /// since.
    /// </summary>
    private Bitmap? BackgroundPicture(string? path, out bool unreadable)
    {
        unreadable = false;
        if (string.IsNullOrEmpty(path))
            return null;
        if (_backgroundPictures.TryGetValue(path, out var known))
        {
            unreadable = known.Bitmap is null && !_reading.Contains(path);
            return known.Bitmap;
        }
        Prune();
        _backgroundPictures[path] = (null, default);
        _reading.Add(path);
        _ = Task.Run(() =>
        {
            Bitmap? bitmap = null;
            // Before it is read: a change meanwhile makes the stamp an old one, and the file is refused.
            var written = StampOf(path);
            try
            {
                using var image = ReadPictureBehind(path, PictureBehindSide, PictureBehindSide);
                if (image is not null)
                {
                    // A panorama covers the square only whole: its long side, far past what the preview shows, held
                    // ten times the pixels.
                    if (Math.Max(image.Width, image.Height) > PictureBehindLongest)
                        image.Resize(new ImageMagick.MagickGeometry(PictureBehindLongest, PictureBehindLongest) { Greater = true });
                    bitmap = HideService.ToBitmap(image);
                }
            }
            catch (Exception ex)
            {
                // Whatever went wrong (a picture too large for memory, say), it is one that can't be read: never one
                // read for ever.
                _log?.LogInformation(ex, "Could not read the picture to put behind");
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_closed)
                    {
                        bitmap?.Dispose();
                        return;
                    }
                    _reading.Remove(path);
                    _backgroundPictures[path] = (bitmap, written);
                    ShowPreview();
                    SaveCommand.NotifyCanExecuteChanged();
                });
            }
        });
        return null;
    }

    /// <summary>
    /// When <paramref name="path"/> was last written and how long it is (none when it is gone): a copy that kept the
    /// time it was written still differs in length, mostly.
    /// </summary>
    private static (DateTime Written, long Length) StampOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (DateTime.MinValue, -1);
        }
    }

    /// <summary>Drops what was read of <paramref name="path"/>, unless it is being read.</summary>
    private void Forget(string path)
    {
        if (_reading.Contains(path) || !_backgroundPictures.Remove(path, out var known) || known.Bitmap is null)
            return;
        // The brush on screen still draws it: disposed once another is set (ShowPreview).
        _retired.Add(known.Bitmap);
    }

    /// <summary>Drops the pictures behind no picture's look uses any more (picked once, then another).</summary>
    private void Prune()
    {
        foreach (var path in _backgroundPictures.Keys.Where(p => _looks.All(look => look.Picture != p)).ToList())
            Forget(path);
    }

    /// <summary>Pictures behind let go of, kept until the brush that drew them is replaced.</summary>
    private readonly List<Bitmap> _retired = [];

    /// <summary>Reads a picture to put behind (tests make it fail).</summary>
    internal static Func<string, int, int, ImageMagick.MagickImage?> ReadPictureBehind { get; set; } =
        (path, width, height) => BackdropComposer.ReadPictureBehind(path, width, height);

    /// <summary>The pictures behind being read.</summary>
    private readonly HashSet<string> _reading = [];

    /// <summary>The picture is loaded and laid out, and any picture behind read (tests wait for it).</summary>
    internal bool IsPreviewCurrent => PictureImage is not null && BackgroundBrush is not null && _reading.Count == 0;

    // ───────── Saving ─────────

    /// <summary>
    /// A picture shown, and with "Picture", one chosen that can be read: the file is never written on a background
    /// other than the one asked for.
    /// </summary>
    private bool CanSave() => !IsBusy && PictureImage is not null
                              && (!IsPicture || (HasPicture && !PictureUnreadable && _reading.Count == 0));

    /// <summary>
    /// Starts the conversion of the picture on its look (shown in the toast), keeps the look, and opens the next
    /// picture or closes. When it can't be started, the picture stays, to save again or skip.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var path = _files[CurrentIndex];
        var look = Look.Clone();
        // The picture behind as the window showed it: changed since, the file is refused, not written with another.
        // Changed already (edited in another app since it was read), it is read again and shown first, saved with the
        // next press: every picture after it went to a conversion that was refused.
        if (look.Kind == BackdropKind.Picture && look.Picture is { } behind && _backgroundPictures.TryGetValue(behind, out var shown))
        {
            if (StampOf(behind) != shown.Stamp)
            {
                Forget(behind);
                ShowPreview();
                return;
            }
            (look.PictureWritten, look.PictureLength) = shown.Stamp;
        }
        Remember();
        try
        {
            _export(path, BackgroundTool.ConversionOf(_tool, look));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not start adding a background to {File}", CurrentFileName);
            AddProblem(CurrentIndex, "error.conversion_failed");
            return;
        }
        _outcomes[CurrentIndex] = CropItemState.Done;
        ClearProblems(CurrentIndex);
        await LoadFileAsync(CurrentIndex + 1);
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (IsBusy || IsFinished)
            return;
        _outcomes[CurrentIndex] ??= CropItemState.Skipped;
        await LoadFileAsync(CurrentIndex + 1);
    }

    private void AddProblem(int index, string key)
    {
        _outcomes[index] = CropItemState.Failed;
        var line = $"{Path.GetFileName(_files[index])}: {_loc[key]}";
        Problems.Add(line);
        _problemFiles.Add(index);
        StatusNotice = line;
    }

    /// <summary>A picture saved after all: what went wrong with it before is no failure any more.</summary>
    private void ClearProblems(int index)
    {
        for (var i = _problemFiles.Count - 1; i >= 0; i--)
        {
            if (_problemFiles[i] != index)
                continue;
            _problemFiles.RemoveAt(i);
            Problems.RemoveAt(i);
        }
        StatusNotice = Problems.LastOrDefault() ?? "";
    }

    /// <summary>
    /// Closes after the last picture, or stays open on the summary when a picture could not be opened or saved.
    /// </summary>
    private void Finish()
    {
        if (Problems.Count == 0)
        {
            Close();
            return;
        }
        Remember();
        FailedCount = _outcomes.Count(o => o == CropItemState.Failed);
        DoneCount = _outcomes.Count(o => o == CropItemState.Done);
        SkippedCount = _outcomes.Count(o => o == CropItemState.Skipped);
        Summary = _loc.Format("toast.done_errors", FailedCount, _files.Count);
        StatusNotice = "";
        HasMultipleFiles = false;
        IsBusy = false;
        var old = PictureImage;
        PictureImage = null;
        old?.Dispose();
        IsFinished = true;
    }

    [RelayCommand]
    private void Close()
    {
        Remember();
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Keeps the look of the picture shown in the tool, as given to this window and as saved, and writes the library
    /// when it changed. The tool given is mostly the library's own: compared with it after the look was put in, it was
    /// always the same, and the look was never written.
    /// </summary>
    private void Remember()
    {
        var look = Look.Clamped();
        if (Same(_remembered, look))
            return;
        _remembered = look.Clone();
        BackgroundTool.Remember(_tool, look);
        if (_store.FindPreset(_tool.Id) is not { IsBackgroundTool: true } saved)
            return;
        BackgroundTool.Remember(saved, look);
        try
        {
            _store.SaveLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not remember the background look");
        }
    }

    /// <summary>The look last kept in the tool (as it was when the window opened, at first).</summary>
    private BackdropOptions? _remembered;

    private static bool Same(BackdropOptions? a, BackdropOptions b) =>
        a is not null && a.Kind == b.Kind && a.From == b.From && a.To == b.To && a.Color == b.Color && a.Picture == b.Picture
        && a.Padding == b.Padding && a.Corners == b.Corners && a.Shadow == b.Shadow && a.Canvas == b.Canvas;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (CurrentIndex < _files.Count)
            CounterText = _loc.Format("crop.page_counter", CurrentIndex + 1, _files.Count);
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(ShadowText));
        OnPropertyChanged(nameof(PictureName));
        if (PictureUnreadable)
            PictureNotice = _loc["background.picture_unreadable"];
        foreach (var swatch in Gradients.Concat(Colors))
            swatch.Name = _loc[swatch.NameKey];
    }

    public void Dispose()
    {
        // Closed with the title bar or Alt+F4 too, while the first picture is still read as well: the look is kept for
        // next time (when it changed).
        if (!_closed)
            Remember();
        _closed = true;
        _loc.LanguageChanged -= OnLanguageChanged;
        PictureImage?.Dispose();
        PictureImage = null;
        foreach (var (bitmap, _) in _backgroundPictures.Values)
            bitmap?.Dispose();
        _backgroundPictures.Clear();
        foreach (var bitmap in _retired)
            bitmap.Dispose();
        _retired.Clear();
    }
}

/// <summary>A gradient or colour offered as a swatch, named for screen readers and its tooltip.</summary>
public sealed partial class BackdropSwatch(string from, string to, string nameKey, BackgroundViewModel owner) : ObservableObject
{
    public string From { get; } = from;
    public string To { get; } = to;
    public string NameKey { get; } = nameKey;

    [ObservableProperty] private string _name = "";

    public IBrush Brush { get; } = new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
    };

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Choose() => owner.Choose(this);
}
