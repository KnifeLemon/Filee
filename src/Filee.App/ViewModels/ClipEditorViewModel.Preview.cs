// The editor's preview: the frame at the playhead (a picture of the part's video there, placed in the clip's frame by
// the part's zoom and focus; the timeline's nearest picture until the exact one is taken), or the clip playing as it
// will be written; the texts shown at the time, dragged where they go; and the player's keys.

using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class ClipEditorViewModel
{
    /// <summary>Height of the picture taken of the frame looked at.</summary>
    private const int PreviewHeight = 720;

    /// <summary>The clip plays scaled to fit this square (pictures come 25 times a second).</summary>
    private const int PlayBox = 640;

    /// <summary>How long the playhead rests before the exact frame is taken.</summary>
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(90);

    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _playCts;
    private Bitmap? _ownPicture;
    private (string Path, double Seconds)? _ownPictureOf;
    private readonly WriteableBitmap?[] _playFrames = new WriteableBitmap?[2];
    private readonly Lock _playFramesLock = new();
    private int _playFrame;
    private int _pictureNumber;

    public Bitmap? Picture { get; private set; }
    public bool IsComposed { get; private set; }
    public ClipPart? PicturePart { get; private set; }
    public (int Width, int Height) PictureSize { get; private set; } = (16, 9);
    public IReadOnlyList<(int Index, ClipText Text)> VisibleTexts { get; private set; } = [];
    public ClipFit Fit => _project.Edit.Fit;

    /// <summary>The frame's size: the clip's shape around the first video's picture.</summary>
    public (int Width, int Height) Canvas
    {
        get
        {
            var first = _project.Parts.FirstOrDefault(p => !p.Blank) is { } part ? MediaOf(part.Source) : MediaOf(null);
            return ClipCanvas.Size(_project.Edit.Aspect, 0, Math.Max(16, first?.Width ?? 1920), Math.Max(16, first?.Height ?? 1080));
        }
    }

    /// <summary>The part the picture is of, by its index in the clip, or -1.</summary>
    public int PictureIndex { get; private set; } = -1;

    /// <summary>The pictures over the video shown now, with their index in the clip.</summary>
    public IReadOnlyList<(int Index, ClipImage Image)> VisibleImages { get; private set; } = [];

    /// <summary>What has the frame with handles in the preview: the selected text or picture when it shows, or the part shown when it alone is selected.</summary>
    public EditorItem? Framed =>
        IsPlaying ? null
        : HasTextSelection && VisibleTexts.Any(t => t.Index == SelectedText) ? new EditorItem(EditorItemKind.Text, SelectedText)
        : HasImageSelection && VisibleImages.Any(i => i.Index == SelectedImage) ? new EditorItem(EditorItemKind.Image, SelectedImage)
        : Selection == EditorSelection.Parts && _selectedParts.Count == 1 && _selectedParts[0] == PictureIndex && PicturePart is { Blank: false }
            ? new EditorItem(EditorItemKind.Part, PictureIndex)
            : null;

    public void Transform(EditorItem item, double x, double y, double size, double rotation)
    {
        StopPlaying();
        var join = $"transform {item.Kind} {item.Index}";
        switch (item.Kind)
        {
            case EditorItemKind.Text:
                _project.ChangeText(item.Index, t =>
                {
                    t.X = x;
                    t.Y = y;
                    t.Size = Math.Clamp(size, ClipText.MinSize, ClipText.MaxSize);
                    t.Rotation = rotation;
                }, join);
                break;
            case EditorItemKind.Image:
                _project.ChangeImage(item.Index, i =>
                {
                    i.X = x;
                    i.Y = y;
                    i.Width = size;
                    i.Rotation = rotation;
                }, join);
                break;
            case EditorItemKind.Part:
                _project.ChangePart(item.Index, p =>
                {
                    p.FocusX = x;
                    p.FocusY = y;
                    p.Zoom = Math.Round(size, 3);
                    p.Rotation = rotation;
                }, join);
                break;
        }
    }

    public void EditText(int index)
    {
        SelectText(index);
        TextEditRequested?.Invoke();
    }

    [RelayCommand]
    public void ToggleFullPreview() => IsFullPreview = !IsFullPreview;

    /// <summary>
    /// Shows the frame at the playhead: the nearest timeline picture at once, the exact frame once the playhead rests
    /// (at once with <paramref name="immediately"/>).
    /// </summary>
    private void RefreshPreview(bool immediately)
    {
        if (_closed || IsPlaying)
            return;
        VisibleTexts = TextsAt(Playhead);
        VisibleImages = ImagesAt(Playhead);
        var index = _project.IndexAt(Playhead);
        PictureIndex = index;
        if (index < 0)
        {
            ShowPicture(null, null);
            return;
        }
        var part = _project.Parts[index];
        if (part.Blank)
        {
            ShowPicture(null, part);
            return;
        }
        var media = MediaOf(part.Source);
        PictureSize = (Math.Max(16, media?.Width ?? 16), Math.Max(16, media?.Height ?? 9));
        var seconds = Math.Clamp(part.SourceAt(Playhead - _project.Starts()[index]), 0, Math.Max(0, DurationOf(part.Source) - 0.04));
        var path = media?.Path ?? _mainKey;
        if (_ownPictureOf is { } shown && string.Equals(shown.Path, path, StringComparison.OrdinalIgnoreCase) && Math.Abs(shown.Seconds - seconds) < 0.02
            && _ownPicture is not null)
        {
            ShowPicture(_ownPicture, part);
            return;
        }
        if (PictureAt(part.Source, seconds) is { } nearest)
            ShowPicture(nearest, part);
        else
            ShowPicture(Picture is not null && !IsComposed ? Picture : null, part);

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _previewCts = cts;
        _ = TakePictureAsync(path, seconds, part, immediately ? TimeSpan.Zero : PreviewDelay, cts.Token);
    }

    private async Task TakePictureAsync(string path, double seconds, ClipPart part, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
            var output = Path.Combine(_workFolder, $"frame_{++_pictureNumber}.jpg");
            if (!await _frames.WriteFrameAsync(path, seconds, output, PreviewHeight, ct) || ct.IsCancellationRequested || _closed)
                return;
            if (Load(output) is { } bitmap)
            {
                if (ct.IsCancellationRequested || _closed || IsPlaying)
                    bitmap.Dispose();
                else
                {
                    var old = _ownPicture;
                    _ownPicture = bitmap;
                    _ownPictureOf = (path, seconds);
                    ShowPicture(bitmap, part);
                    if (old is not null && !ReferenceEquals(old, bitmap))
                        old.Dispose();
                }
            }
            TryDelete(output);
        }
        catch (OperationCanceledException)
        {
            // The playhead moved on.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "No frame at {Seconds} s of {File}", seconds, path);
        }
    }

    private void ShowPicture(Bitmap? picture, ClipPart? part)
    {
        Picture = picture;
        IsComposed = false;
        PicturePart = part?.Clone();
        PreviewChanged?.Invoke();
    }

    private IReadOnlyList<(int Index, ClipImage Image)> ImagesAt(double seconds) =>
        _project.Images.Select((image, index) => (index, image))
            .Where(i => seconds >= i.image.Start - 1e-6 && seconds < i.image.Start + i.image.Length)
            .Select(i => (i.index, i.image.Clone()))
            .ToList();

    private IReadOnlyList<(int Index, ClipText Text)> TextsAt(double seconds) =>
        _project.Texts.Select((text, index) => (index, text))
            .Where(t => seconds >= t.text.Start - 1e-6 && seconds < t.text.Start + t.text.Length)
            .Select(t => (t.index, t.text.Clone()))
            .ToList();

    // ───────────────────────── Playing ─────────────────────────

    /// <summary>Space / K: plays the clip as it will be written from the playhead; again stops.</summary>
    [RelayCommand]
    public void PlayPause()
    {
        if (IsPlaying)
            StopPlaying();
        else
            _ = PlayAsync();
    }

    private async Task PlayAsync()
    {
        if (_closed)
            return;
        var edit = _project.ToEdit();
        var from = Playhead >= edit.Length - 0.05 ? 0 : Playhead;
        _previewCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _playCts = cts;
        IsPlaying = true;
        IsBuffering = true;
        Playhead = from;
        try
        {
            await _frames.PlayEditorAsync(_main, edit.From(from), PlayBox, PlayBox,
                (pixels, width, height, seconds) => ShowFrame(pixels, width, height, from + seconds, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not play the editor's clip of {File}", _main);
            ShowMessage(_loc["clip.cannot_play"]);
        }
        finally
        {
            if (ReferenceEquals(_playCts, cts))
            {
                _playCts = null;
                IsPlaying = false;
                IsBuffering = false;
                if (!_closed && !cts.IsCancellationRequested)
                    RefreshPreview(immediately: true);
            }
            cts.Dispose();
        }
    }

    /// <summary>Stops playing; the playhead stays where it got to.</summary>
    public void StopPlaying()
    {
        if (_playCts is not { } cts)
            return;
        _playCts = null;
        cts.Cancel();
        IsPlaying = false;
        IsBuffering = false;
        if (!_closed)
            RefreshPreview(immediately: true);
    }

    /// <summary>A picture of the clip playing (on a worker thread): written into a frame not on screen, then shown.</summary>
    private void ShowFrame(byte[] pixels, int width, int height, double seconds, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _closed)
            return;
        WriteableBitmap bitmap;
        lock (_playFramesLock)
        {
            _playFrame = 1 - _playFrame;
            bitmap = _playFrames[_playFrame] is { } existing && existing.PixelSize == new PixelSize(width, height)
                ? existing
                : _playFrames[_playFrame] = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var buffer = bitmap.Lock();
            var row = width * 4;
            for (var y = 0; y < height; y++)
                Marshal.Copy(pixels, y * row, buffer.Address + y * buffer.RowBytes, row);
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested || _closed)
                return;
            IsBuffering = false;
            Playhead = Math.Min(seconds, Length);
            Picture = bitmap;
            IsComposed = true;
            VisibleTexts = TextsAt(Playhead);
            VisibleImages = ImagesAt(Playhead);
            PreviewChanged?.Invoke();
            TimelineChanged?.Invoke();
        });
    }

    /// <summary>One frame on (, and .), as the first video counts them.</summary>
    [RelayCommand]
    public void StepFrame(string? direction)
    {
        var frame = MediaOf(null) is { FrameRate: > 1 and < 1000 } media ? 1 / media.FrameRate : 1 / 30.0;
        Seek(Playhead + (direction == "-1" ? -frame : frame));
    }

    /// <summary>J and L: five seconds back or on.</summary>
    [RelayCommand]
    public void Jump(string? seconds) =>
        Seek(Playhead + (double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var by) ? by : 5));

    [RelayCommand]
    public void GoToStart() => Seek(0);

    [RelayCommand]
    public void GoToEnd() => Seek(Length);

    private void DisposePictures()
    {
        _previewCts?.Dispose();
        _ownPicture?.Dispose();
        _ownPicture = null;
        Picture = null;
        lock (_playFramesLock)
        {
            foreach (var frame in _playFrames)
                frame?.Dispose();
            Array.Clear(_playFrames);
        }
    }
}
