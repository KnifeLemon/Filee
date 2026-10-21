// The clip window's editor (its second mode, CapCut-like in Filee's style): the videos, sounds and pictures in Files,
// the clip on a timeline of tracks (texts, pictures over the video, the parts, their sound, the sounds from other
// files), the preview with the player, the selection's properties, right-click menus, and the Export window. The
// editing itself is ClipProject; this keeps what is shown in step with it, reads the files (pictures, loudness), plays
// the clip and starts the conversion that writes it.

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Media;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

/// <summary>What the editor's selection is.</summary>
public enum EditorSelection
{
    None,
    Parts,
    Text,
    Image,
    Sound,
    Transition,
}

/// <summary>What a file in Files is.</summary>
public enum EditorMediaKind
{
    Video,
    Sound,
    Picture,
}

/// <summary>A video, a sound or a picture in the editor's Files.</summary>
public sealed partial class EditorMedia(string path, EditorMediaKind kind) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public EditorMediaKind Kind { get; } = kind;
    public bool IsVideo => Kind == EditorMediaKind.Video;
    public bool IsAudio => Kind == EditorMediaKind.Sound;
    public bool IsPicture => Kind == EditorMediaKind.Picture;

    /// <summary>A video's or picture's card shows its picture; a sound's, a note.</summary>
    public bool HasPicture => !IsAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private double _duration;

    /// <summary>"1920×1080 · 60 fps", or the kind of file.</summary>
    [ObservableProperty] private string _details = "";

    [ObservableProperty] private Bitmap? _thumbnail;

    /// <summary>Used in the clip: on the timeline, as a sound, or over the video.</summary>
    [ObservableProperty] private bool _isUsed;

    /// <summary>Just dropped in: its card flies in.</summary>
    [ObservableProperty] private bool _justAdded;

    public string DurationText => IsPicture ? "" : ClipRange.Display(Duration);

    internal int Width { get; set; }
    internal int Height { get; set; }
    internal double FrameRate { get; set; }
    internal bool HasAudio { get; set; }

    /// <summary>Small pictures over the whole video, for the timeline.</summary>
    internal Bitmap?[] Strip { get; set; } = [];

    /// <summary>The sound's loudness over the whole file.</summary>
    internal float[] Loudness { get; set; } = [];

    /// <summary>A picture's own image, for the preview and the export (not the card's thumbnail: it is larger).</summary>
    internal Bitmap? Image { get; set; }

    /// <summary>Lets go of every picture it holds.</summary>
    internal void Release()
    {
        foreach (var bitmap in Strip)
            bitmap?.Dispose();
        Strip = [];
        if (!ReferenceEquals(Thumbnail, Image))
            Image?.Dispose();
        Image = null;
        var thumbnail = Thumbnail;
        Thumbnail = null;
        if (IsPicture)
            thumbnail?.Dispose();
    }
}

public sealed partial class ClipEditorViewModel : ObservableObject, IEditorTimelineHost, IEditorPreviewHost, IDisposable
{
    /// <summary>Small pictures taken over each video for the timeline.</summary>
    private const int StripCount = 24;

    private const int StripHeight = 96;

    /// <summary>Steps of a file's loudness drawn under its parts or sound.</summary>
    private const int LoudnessSteps = 600;

    /// <summary>FFmpeg runs at the same time while pictures are taken.</summary>
    private const int ParallelPictures = 3;

    /// <summary>A picture over the video is read no wider than this (a photo is often far larger than any frame).</summary>
    private const int PictureWidth = 2048;

    /// <summary>How long a picture over the video shows to start with, in seconds.</summary>
    public const double PictureSeconds = 5;

    /// <summary>The most sound tracks (A2 … A9).</summary>
    public const int MaxSoundTracks = 8;

    /// <summary>How long a short message stays.</summary>
    private static readonly TimeSpan MessageTime = TimeSpan.FromSeconds(4);

    private readonly string _main;
    private readonly string _mainKey;
    private readonly Preset _tool;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly IVideoFrameSource _frames;
    private readonly Action<string, Preset> _export;
    private readonly ILogger? _log;
    private readonly string _workFolder;
    private readonly Dictionary<string, EditorMedia> _media = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClipProject _project;
    private readonly CancellationTokenSource _life = new();
    private readonly List<int> _selectedParts = [];
    private List<ClipPart> _clipboard = [];
    private ClipText? _textClipboard;
    private bool _syncing;
    private bool _closed;

    /// <param name="mainPath">The video the window opened (the conversion's input; parts without a source are of it).</param>
    /// <param name="main">What was read of it.</param>
    /// <param name="start">The clip to start from (the quick cut so far).</param>
    /// <param name="tool">The "Cut a clip" preset: name, output rule and media options.</param>
    /// <param name="export">Starts the conversion that writes the clip (the file, the conversion's preset).</param>
    public ClipEditorViewModel(string mainPath, VideoSummary main, ClipEdit start, Preset tool, ILocalizer loc, UserDataStore store,
        IVideoFrameSource frames, Action<string, Preset> export, string workFolder, ILogger? log = null)
    {
        _main = mainPath;
        _mainKey = Path.GetFullPath(mainPath);
        _tool = tool;
        _loc = loc;
        _store = store;
        _frames = frames;
        _export = export;
        _workFolder = Path.Combine(workFolder, "editor");
        _log = log;
        var media = new EditorMedia(_mainKey, EditorMediaKind.Video);
        Describe(media, main);
        _media[_mainKey] = media;
        Files.Add(media);
        _project = new ClipProject(start, DurationOf);
        foreach (var sound in _project.Sounds.Where(s => !_media.ContainsKey(Path.GetFullPath(s.Path))))
        {
            var song = new EditorMedia(Path.GetFullPath(sound.Path), EditorMediaKind.Sound) { Duration = sound.Length, Details = _loc["editor.sound"] };
            _media[song.Path] = song;
            Files.Add(song);
            _ = LoadLoudnessAsync(song, _life.Token);
        }
        _project.Changed += OnProjectChanged;
        LoadExportSettings();
        loc.LanguageChanged += OnLanguageChanged;
        Directory.CreateDirectory(_workFolder);
        EditorLayers.CleanUp();
        _ = LoadPicturesAsync(media);
        _ = FindGpuAsync();
        Sync();
        RefreshPreview(immediately: true);
    }

    public event Action? TimelineChanged;
    public event Action? PreviewChanged;

    /// <summary>The clip went to the conversion: the window moves on to the next video or closes.</summary>
    public event Action? Exported;

    /// <summary>Back to the quick cut.</summary>
    public event Action? BackRequested;

    /// <summary>A text's words are to be typed (the properties' text box gets the focus).</summary>
    public event Action? TextEditRequested;

    /// <summary>Files just added (their cards fly in, a check draws itself).</summary>
    public event Action<IReadOnlyList<EditorMedia>>? FilesAdded;

    /// <summary>A file is to be shown in its folder (the platform does it).</summary>
    public event Action<string>? RevealRequested;

    /// <summary>The videos, sounds and pictures of the clip.</summary>
    public ObservableCollection<EditorMedia> Files { get; } = [];

    public ClipEdit Edit => _project.Edit;
    public IReadOnlyCollection<int> SelectedParts => _selectedParts;
    public string GapText => _loc["clip.close_gap"];
    public string NoSoundText => _loc["editor.no_sound_yet"];

    /// <summary>The sound tracks in use and an empty one below them to drag a sound to.</summary>
    public int SoundTracks => Math.Min(MaxSoundTracks, _project.Sounds.Count == 0 ? 1 : _project.SoundTracks + 1);

    /// <summary>The window's video, by name.</summary>
    public string Title => Path.GetFileName(_main);

    // ───────────────────────── State shown ─────────────────────────

    /// <summary>Seconds into the clip: where the blade cuts, playing starts and texts are added.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _playhead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    private double _length;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartSelection))]
    [NotifyPropertyChangedFor(nameof(HasTextSelection))]
    [NotifyPropertyChangedFor(nameof(HasImageSelection))]
    [NotifyPropertyChangedFor(nameof(HasSoundSelection))]
    [NotifyPropertyChangedFor(nameof(HasTransitionSection))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    private EditorSelection _selection;

    [ObservableProperty] private int _selectedText = -1;
    [ObservableProperty] private int _selectedImage = -1;
    [ObservableProperty] private int _selectedSound = -1;
    [ObservableProperty] private int _selectedTransition = -1;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _hasGaps;
    [ObservableProperty] private bool _hasSounds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayToolTip))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyText))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isBuffering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyText))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isFindingSilence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyText))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private int _reading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    /// <summary>The preview fills the window (F, a double click); Esc goes back.</summary>
    [ObservableProperty] private bool _isFullPreview;

    /// <summary>The window is narrow: Files and Properties slide out on demand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFiles))]
    [NotifyPropertyChangedFor(nameof(ShowProperties))]
    private bool _isNarrow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFiles))]
    private bool _isFilesOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProperties))]
    private bool _isPropertiesOpen;

    /// <summary>How far the timeline is zoomed in: 0 the whole clip, 1 closest.</summary>
    [ObservableProperty] private double _timelineZoom;

    /// <summary>The question before going back to the quick cut loses what only the editor has.</summary>
    [ObservableProperty] private bool _isBackConfirmOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionTitle))]
    private string _selectionDetail = "";

    public bool ShowFiles => !IsNarrow || IsFilesOpen;
    public bool ShowProperties => !IsNarrow || IsPropertiesOpen;
    public bool HasPartSelection => Selection == EditorSelection.Parts && _selectedParts.Count > 0;
    public bool HasTextSelection => Selection == EditorSelection.Text && SelectedText >= 0 && SelectedText < _project.Texts.Count;
    public bool HasImageSelection => Selection == EditorSelection.Image && SelectedImage >= 0 && SelectedImage < _project.Images.Count;
    public bool HasSoundSelection => Selection == EditorSelection.Sound && SelectedSound >= 0 && SelectedSound < _project.Sounds.Count;
    public bool HasNoSelection => Selection == EditorSelection.None || Selection == EditorSelection.Parts && _selectedParts.Count == 0;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool IsBusy => IsBuffering || IsFindingSilence || Reading > 0;
    public string BusyText => IsBuffering ? _loc["clip.preparing"]
        : IsFindingSilence ? _loc["clip.finding_silence"]
        : Reading > 0 ? _loc["editor.reading"]
        : "";

    public string TimeText => $"{ClipRange.Display(Playhead)} / {ClipRange.Display(Length)}";
    public string LengthText => ClipRange.Display(Length);
    public string PlayToolTip => _loc[IsPlaying ? "clip.pause" : "clip.play"] + " (Space, K)";

    /// <summary>What the properties are of: "Part 2 of 5", "Text", "Picture", "Sound", "Transition", "Clip".</summary>
    public string SelectionTitle => Selection switch
    {
        EditorSelection.Parts when _selectedParts.Count > 1 => _loc.Format("clip.selected_count", _selectedParts.Count),
        EditorSelection.Parts when _selectedParts.Count == 1 => _loc.Format("editor.part_of", _selectedParts[0] + 1, _project.Parts.Count),
        EditorSelection.Text => _loc["editor.text"],
        EditorSelection.Image => _loc["editor.picture"],
        EditorSelection.Sound => _loc["editor.sound"],
        EditorSelection.Transition => _loc.Format("editor.transition_between", SelectedTransition + 1, SelectedTransition + 2),
        _ => _loc["editor.clip"],
    };

    /// <summary>The selected text, picture, sound or transition, for the timeline (null: parts or nothing).</summary>
    public EditorItem? SelectedItem => Selection switch
    {
        EditorSelection.Text when SelectedText >= 0 => new EditorItem(EditorItemKind.Text, SelectedText),
        EditorSelection.Image when SelectedImage >= 0 => new EditorItem(EditorItemKind.Image, SelectedImage),
        EditorSelection.Sound when SelectedSound >= 0 => new EditorItem(EditorItemKind.Sound, SelectedSound),
        EditorSelection.Transition when SelectedTransition >= 0 => new EditorItem(EditorItemKind.Transition, SelectedTransition),
        _ => null,
    };

    // ───────────────────────── Files ─────────────────────────

    /// <summary>How long the video or sound <paramref name="source"/> is (null: the window's video).</summary>
    public double DurationOf(string? source) => MediaOf(source)?.Duration ?? 0;

    private EditorMedia? MediaOf(string? source) =>
        _media.TryGetValue(source is null ? _mainKey : Path.GetFullPath(source), out var media) ? media : null;

    /// <summary>How the parts name a video: null for the window's own, else its full path.</summary>
    private string? SourceKey(string path) => string.Equals(Path.GetFullPath(path), _mainKey, StringComparison.OrdinalIgnoreCase) ? null : Path.GetFullPath(path);

    private void Describe(EditorMedia media, VideoSummary video)
    {
        media.Duration = video.Duration;
        media.Width = video.Width;
        media.Height = video.Height;
        media.FrameRate = video.FrameRate;
        media.HasAudio = video.HasAudio;
        media.Details = $"{video.Width}×{video.Height} · {Math.Round(video.FrameRate).ToString(CultureInfo.CurrentCulture)} {_loc["editor.fps"]}";
    }

    /// <summary>
    /// Adds files to Files: videos, sounds and pictures (anything else is skipped with a message). With
    /// <paramref name="toTimeline"/> the videos also go on the timeline (at part <paramref name="at"/>, else at the
    /// end), the sounds one after another on A2, the pictures over the video at the playhead. Returns the files added.
    /// </summary>
    public async Task<IReadOnlyList<EditorMedia>> AddFilesAsync(IReadOnlyList<string> paths, bool toTimeline, int? at = null)
    {
        var added = new List<EditorMedia>();
        var ready = new List<EditorMedia>();
        var failed = 0;
        Reading++;
        try
        {
            foreach (var path in paths.Where(p => !Directory.Exists(p)))
            {
                var full = Path.GetFullPath(path);
                if (_media.TryGetValue(full, out var known))
                {
                    ready.Add(known);
                    continue;
                }
                var media = await ReadMediaAsync(full);
                if (_closed)
                {
                    media?.Release();
                    return added;
                }
                if (media is null)
                {
                    failed++;
                    continue;
                }
                _media[full] = media;
                Files.Add(media);
                media.JustAdded = true;
                added.Add(media);
                ready.Add(media);
                if (media.IsVideo)
                    _ = LoadPicturesAsync(media);
                else if (media.IsAudio)
                    _ = LoadLoudnessAsync(media, _life.Token);
            }
        }
        finally
        {
            Reading--;
        }
        if (toTimeline && ready.Count > 0)
            PutOnTimeline(ready, at);
        if (failed > 0)
            ShowMessage(_loc.Format("editor.cannot_open", failed));
        else if (added.Count > 0)
            ShowMessage(_loc.Format("editor.added", added.Count));
        if (added.Count > 0)
            FilesAdded?.Invoke(added);
        return added;
    }

    private async Task<EditorMedia?> ReadMediaAsync(string path)
    {
        var category = FormatRegistry.Detect(path)?.Category;
        if (category == FormatCategory.Image)
            return ReadPicture(path);
        // A file known to be something else (a document, an archive) isn't read at all.
        if (category is { } other && other is not (FormatCategory.Video or FormatCategory.Audio))
            return null;
        try
        {
            var video = await _frames.ProbeAsync(path, _life.Token);
            if (video.Duration > 0)
            {
                var media = new EditorMedia(path, EditorMediaKind.Video);
                Describe(media, video);
                return media;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _log?.LogDebug(ex, "{File} is not a video for the editor", path);
        }
        try
        {
            var length = await _frames.ReadSoundLengthAsync(path, _life.Token);
            if (length > 0)
                return new EditorMedia(path, EditorMediaKind.Sound) { Duration = length, Details = _loc["editor.sound"] };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _log?.LogDebug(ex, "{File} has no sound for the editor either", path);
        }
        return null;
    }

    /// <summary>A picture file, read no wider than <see cref="PictureWidth"/>; null when it can't be read.</summary>
    private EditorMedia? ReadPicture(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, PictureWidth, BitmapInterpolationMode.HighQuality);
            if (bitmap.PixelSize.Width > PictureWidth)
                bitmap = Swap(bitmap, bitmap.CreateScaledBitmap(new Avalonia.PixelSize(PictureWidth,
                    Math.Max(1, (int)Math.Round((double)bitmap.PixelSize.Height * PictureWidth / bitmap.PixelSize.Width)))));
            return new EditorMedia(path, EditorMediaKind.Picture)
            {
                Image = bitmap,
                Thumbnail = bitmap,
                Width = bitmap.PixelSize.Width,
                Height = bitmap.PixelSize.Height,
                Details = $"{_loc["editor.picture"]} · {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}",
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "{File} is not a picture the editor can read", path);
            return null;
        }

        static Bitmap Swap(Bitmap old, Bitmap scaled)
        {
            old.Dispose();
            return scaled;
        }
    }

    /// <summary>Puts videos on the timeline, sounds on A2 one after another and pictures over the video (one undo step).</summary>
    private void PutOnTimeline(IReadOnlyList<EditorMedia> media, int? at)
    {
        StopPlaying();
        var start = Playhead;
        _project.Batch(() =>
        {
            var index = Math.Clamp(at ?? _project.Parts.Count, 0, _project.Parts.Count);
            foreach (var item in media)
            {
                switch (item.Kind)
                {
                    case EditorMediaKind.Video:
                        _project.AddVideo(SourceKey(item.Path), index++);
                        break;
                    case EditorMediaKind.Sound:
                        _project.AddSound(item.Path, item.Duration);
                        break;
                    case EditorMediaKind.Picture:
                        _project.AddImage(new ClipImage { Path = item.Path, Start = start, Length = PictureSeconds });
                        break;
                }
            }
        });
    }

    /// <summary>A card's "+" or a double click: a video after the part at the playhead, a sound on A2, a picture at the playhead.</summary>
    [RelayCommand]
    public void UseMedia(EditorMedia? media)
    {
        if (media is not null)
            PutOnTimeline([media], media.IsVideo ? _project.IndexAt(Playhead) + 1 : null);
    }

    /// <summary>
    /// Takes a file out of the project: its card, and its parts, sounds and pictures in the clip (Undo brings them and
    /// the card back: what was read of the file stays until the editor closes). Refused when nothing of the video would
    /// be left to play.
    /// </summary>
    [RelayCommand]
    public void RemoveMedia(EditorMedia? media)
    {
        if (media is null)
            return;
        StopPlaying();
        var used = media.IsUsed;
        if (used && !_project.RemoveFile(media.Path, media.IsVideo ? SourceKey(media.Path) : "\0"))
        {
            ShowMessage(_loc["editor.cannot_remove_file"]);
            return;
        }
        Files.Remove(media);
        ClearSelection();
    }

    [RelayCommand]
    private void RevealMedia(EditorMedia? media)
    {
        if (media is not null)
            RevealRequested?.Invoke(media.Path);
    }

    /// <summary>The right-click menu of a card in Files.</summary>
    public IReadOnlyList<EditorMenuItem> MenuForMedia(EditorMedia media) =>
    [
        new(_loc[media.IsAudio ? "editor.menu_add_sound" : media.IsPicture ? "editor.menu_add_picture" : "editor.menu_add_video"], () => UseMedia(media)),
        .. media.IsAudio
            ? new[] { new EditorMenuItem(_loc["editor.menu_new_track"], () => AddSoundOnNewTrack(media)) }
            : [],
        new(_loc["editor.menu_reveal"], () => RevealMedia(media)),
        EditorMenuItem.Separator,
        new(_loc["editor.menu_remove_file"], () => RemoveMedia(media)),
    ];

    private void AddSoundOnNewTrack(EditorMedia media)
    {
        StopPlaying();
        var track = Math.Min(MaxSoundTracks - 1, _project.Sounds.Count == 0 ? 0 : _project.SoundTracks);
        Select(new EditorItem(EditorItemKind.Sound, _project.AddSound(media.Path, media.Duration, track, Playhead)));
    }

    /// <summary>The pictures and the loudness of a video, a few at a time, each shown as soon as it is there.</summary>
    private async Task LoadPicturesAsync(EditorMedia media)
    {
        var ct = _life.Token;
        var folder = Path.Combine(_workFolder, "strip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        media.Strip = new Bitmap?[StripCount];
        _ = LoadLoudnessAsync(media, ct);
        using var gate = new SemaphoreSlim(ParallelPictures);
        // The middle picture first: it is the card's.
        var order = Enumerable.Range(0, StripCount).OrderBy(i => Math.Abs(i - StripCount / 2)).ToList();
        var tasks = order.Select(async i =>
        {
            await gate.WaitAsync(ct);
            string? file = null;
            try
            {
                var output = Path.Combine(folder, $"strip_{i:00}.jpg");
                if (await _frames.WriteFrameAsync(media.Path, VideoFrames.ThumbnailTime(i, StripCount, media.Duration), output, StripHeight, ct))
                    file = output;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _log?.LogDebug(ex, "No timeline picture {Index} of {File}", i, media.Path);
            }
            finally
            {
                gate.Release();
            }
            if (file is null || Load(file) is not { } bitmap)
                return;
            TryDelete(file);
            // The file may have left the project (or the editor closed) meanwhile.
            if (ct.IsCancellationRequested || _closed || media.Strip.Length != StripCount)
            {
                bitmap.Dispose();
                return;
            }
            media.Strip[i] = bitmap;
            media.Thumbnail ??= bitmap;
            TimelineChanged?.Invoke();
            if (Picture is null && !IsPlaying)
                RefreshPreview(immediately: false);
        }).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // The editor closed.
        }
    }

    private async Task LoadLoudnessAsync(EditorMedia media, CancellationToken ct)
    {
        if (media.IsVideo && !media.HasAudio)
            return;
        try
        {
            media.Loudness = await _frames.ReadLoudnessAsync(media.Path, 0, media.Duration, LoudnessSteps, _workFolder, ct);
            TimelineChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "No loudness of {File}", media.Path);
        }
    }

    public Bitmap? PictureAt(string? source, double seconds)
    {
        if (MediaOf(source) is not { Strip.Length: > 0 } media || media.Duration <= 0)
            return null;
        var strip = media.Strip;
        var index = Math.Clamp((int)(seconds / media.Duration * strip.Length), 0, strip.Length - 1);
        for (var d = 0; d < strip.Length; d++)
        {
            if (index - d >= 0 && strip[index - d] is { } before)
                return before;
            if (index + d < strip.Length && strip[index + d] is { } after)
                return after;
        }
        return null;
    }

    public Bitmap? ImageOf(string path) => _media.TryGetValue(Path.GetFullPath(path), out var media) ? media.Image : null;

    public IReadOnlyList<float> LoudnessOf(string? source) => MediaOf(source)?.Loudness ?? [];

    // ───────────────────────── After each change ─────────────────────────

    private void OnProjectChanged()
    {
        // Indices past the end after things went away.
        _selectedParts.RemoveAll(i => i >= _project.Parts.Count);
        if (SelectedText >= _project.Texts.Count)
            SelectedText = -1;
        if (SelectedImage >= _project.Images.Count)
            SelectedImage = -1;
        if (SelectedSound >= _project.Sounds.Count)
            SelectedSound = -1;
        if (SelectedTransition >= _project.Parts.Count - 1)
            SelectedTransition = -1;
        Sync();
        RefreshPreview(immediately: false);
    }

    /// <summary>Everything shown of the clip, after a change.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            Length = _project.Length;
            if (Playhead > Length)
                Playhead = Length;
            CanUndo = _project.CanUndo;
            CanRedo = _project.CanRedo;
            HasGaps = _project.HasGaps;
            HasSounds = _project.Sounds.Count > 0;
            if (Selection is EditorSelection.Text or EditorSelection.Image or EditorSelection.Sound or EditorSelection.Transition && SelectedItem is null)
                Selection = EditorSelection.None;
            foreach (var media in _media.Values)
            {
                media.IsUsed = media.Kind switch
                {
                    EditorMediaKind.Video => _project.Parts.Any(p => !p.Blank && string.Equals(p.Source ?? _mainKey, media.Path, StringComparison.OrdinalIgnoreCase)),
                    EditorMediaKind.Sound => _project.Sounds.Any(s => string.Equals(Path.GetFullPath(s.Path), media.Path, StringComparison.OrdinalIgnoreCase)),
                    _ => _project.Images.Any(i => string.Equals(Path.GetFullPath(i.Path), media.Path, StringComparison.OrdinalIgnoreCase)),
                };
                // A file taken out of the project and brought back by Undo gets its card back.
                if (media.IsUsed && !Files.Contains(media))
                    Files.Add(media);
            }
            UpdateSelectionDetail();
            foreach (var name in PropertyNames)
                OnPropertyChanged(name);
        }
        finally
        {
            _syncing = false;
        }
        TimelineChanged?.Invoke();
        UpdateExportSummary();
    }

    private void UpdateSelectionDetail()
    {
        SelectionDetail = Selection switch
        {
            EditorSelection.Parts when _selectedParts.Count == 1 && _selectedParts[0] < _project.Parts.Count
                => PartDetail(_project.Parts[_selectedParts[0]]),
            EditorSelection.Text when HasTextSelection
                => $"{ClipRange.Display(_project.Texts[SelectedText].Start)} · {ClipRange.Display(_project.Texts[SelectedText].Length)}",
            EditorSelection.Image when HasImageSelection => Path.GetFileName(_project.Images[SelectedImage].Path),
            EditorSelection.Sound when HasSoundSelection
                => $"{Path.GetFileName(_project.Sounds[SelectedSound].Path)} · A{_project.Sounds[SelectedSound].Track + 2}",
            EditorSelection.None or EditorSelection.Parts => $"{_project.Parts.Count(p => !p.Blank)} · {LengthText}",
            _ => "",
        };
        OnPropertyChanged(nameof(SelectionTitle));
    }

    private string PartDetail(ClipPart part) => part.Blank
        ? _loc["editor.pause"] + " · " + ClipRange.Display(part.Length)
        : $"{Path.GetFileName(part.Source ?? _main)} · {ClipRange.Display(part.Length)}";

    // ───────────────────────── Selection ─────────────────────────

    public void SelectPart(int index, ClipSelect mode)
    {
        if (index < 0 || index >= _project.Parts.Count)
            return;
        if (Selection != EditorSelection.Parts)
            _selectedParts.Clear();
        switch (mode)
        {
            case ClipSelect.Toggle:
                if (!_selectedParts.Remove(index))
                    _selectedParts.Add(index);
                break;
            case ClipSelect.Range when _selectedParts.Count > 0:
                var anchor = _selectedParts[^1];
                for (var i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++)
                {
                    if (!_selectedParts.Contains(i))
                        _selectedParts.Add(i);
                }
                break;
            default:
                _selectedParts.Clear();
                _selectedParts.Add(index);
                break;
        }
        _selectedParts.Sort();
        SetSelection(EditorSelection.Parts);
    }

    /// <summary>Selects a text, a picture, a sound, a transition or (one) part.</summary>
    public void Select(EditorItem item)
    {
        switch (item.Kind)
        {
            case EditorItemKind.Part:
                SelectPart(item.Index, ClipSelect.Only);
                break;
            case EditorItemKind.Text when item.Index >= 0 && item.Index < _project.Texts.Count:
                SelectedText = item.Index;
                SetSelection(EditorSelection.Text);
                break;
            case EditorItemKind.Image when item.Index >= 0 && item.Index < _project.Images.Count:
                SelectedImage = item.Index;
                SetSelection(EditorSelection.Image);
                break;
            case EditorItemKind.Sound when item.Index >= 0 && item.Index < _project.Sounds.Count:
                SelectedSound = item.Index;
                SetSelection(EditorSelection.Sound);
                break;
            case EditorItemKind.Transition when item.Index >= 0 && item.Index < _project.Parts.Count - 1:
                SelectedTransition = item.Index;
                SetSelection(EditorSelection.Transition);
                break;
        }
    }

    public void SelectText(int index) => Select(new EditorItem(EditorItemKind.Text, index));

    [RelayCommand]
    public void ClearSelection()
    {
        _selectedParts.Clear();
        SetSelection(EditorSelection.None);
    }

    [RelayCommand]
    public void SelectAll()
    {
        _selectedParts.Clear();
        _selectedParts.AddRange(Enumerable.Range(0, _project.Parts.Count));
        SetSelection(EditorSelection.Parts);
    }

    private void SetSelection(EditorSelection selection)
    {
        if (selection != EditorSelection.Text)
            SelectedText = -1;
        if (selection != EditorSelection.Image)
            SelectedImage = -1;
        if (selection != EditorSelection.Sound)
            SelectedSound = -1;
        if (selection != EditorSelection.Transition)
            SelectedTransition = -1;
        if (selection != EditorSelection.Parts)
            _selectedParts.Clear();
        Selection = selection;
        _syncing = true;
        try
        {
            UpdateSelectionDetail();
            foreach (var name in PropertyNames)
                OnPropertyChanged(name);
            OnPropertyChanged(nameof(HasPartSelection));
            OnPropertyChanged(nameof(HasTextSelection));
            OnPropertyChanged(nameof(HasImageSelection));
            OnPropertyChanged(nameof(HasSoundSelection));
            OnPropertyChanged(nameof(HasNoSelection));
            OnPropertyChanged(nameof(HasTransitionSection));
        }
        finally
        {
            _syncing = false;
        }
        TimelineChanged?.Invoke();
        PreviewChanged?.Invoke();
    }

    /// <summary>The parts the part actions apply to: the selection, else the one at the playhead.</summary>
    public IReadOnlyList<int> TargetParts =>
        Selection == EditorSelection.Parts && _selectedParts.Count > 0 ? _selectedParts.ToList()
        : _project.IndexAt(Playhead) is >= 0 and var at ? [at] : [];

    // ───────────────────────── Timeline (host) ─────────────────────────

    public void Seek(double seconds)
    {
        var wasPlaying = IsPlaying;
        StopPlaying();
        Playhead = Math.Clamp(seconds, 0, Length);
        RefreshPreview(immediately: false);
        TimelineChanged?.Invoke();
        if (wasPlaying)
            _ = PlayAsync();
    }

    public void MovePart(int from, int to)
    {
        StopPlaying();
        if (!_project.Move(from, to))
            return;
        _selectedParts.Clear();
        _selectedParts.Add(to);
        SetSelection(EditorSelection.Parts);
    }

    public void DragEdge(int index, bool end, double length)
    {
        StopPlaying();
        _project.TrimEdge(index, end, length);
        // The picture at the edge being dragged.
        var starts = _project.Starts();
        if (index < _project.Parts.Count)
            Playhead = Math.Clamp(end ? starts[index] + _project.Parts[index].Length - 0.01 : starts[index], 0, Length);
        RefreshPreview(immediately: false);
    }

    public void PlaceItem(EditorItem item, double start, double length)
    {
        switch (item.Kind)
        {
            case EditorItemKind.Text:
                _project.ChangeText(item.Index, t =>
                {
                    t.Start = start;
                    t.Length = length;
                }, join: $"text place {item.Index}");
                break;
            case EditorItemKind.Image:
                _project.ChangeImage(item.Index, i =>
                {
                    i.Start = start;
                    i.Length = length;
                }, join: $"image place {item.Index}");
                break;
        }
    }

    public void MoveSound(int index, double offset, int track) =>
        _project.ChangeSound(index, s =>
        {
            s.Offset = offset;
            s.Track = Math.Clamp(track, 0, MaxSoundTracks - 1);
        }, join: $"sound move {index}");

    public void TrimSound(int index, double offset, double from, double play) =>
        _project.ChangeSound(index, s =>
        {
            s.Offset = offset;
            s.From = from;
            s.Play = play;
        }, join: $"sound trim {index}");

    public void SetPartVolume(int index, double volume)
    {
        var targets = _selectedParts.Contains(index) ? _selectedParts.ToList() : [index];
        _project.ChangeParts(targets, p =>
        {
            p.Volume = volume;
            p.Muted = false;
        }, join: "volume line");
    }

    public void SetSoundVolume(int index, double volume) => _project.ChangeSound(index, s => s.Volume = volume, join: $"sound volume {index}");

    public void CloseGap(int index) => _project.CloseGap(index);

    public void EndDrag() => _project.Seal();

    public void DropFiles(IReadOnlyList<string> files, int at) => _ = AddFilesAsync(files, toTimeline: true, at);

    // ───────────────────────── Editing ─────────────────────────

    /// <summary>The blade (C): cuts the part at the playhead in two.</summary>
    [RelayCommand]
    public void Split()
    {
        StopPlaying();
        if (!_project.Split(Playhead))
            ShowMessage(_loc["clip.cannot_cut"]);
    }

    /// <summary>Delete: parts leave a gap (Shift: what follows closes up); a text, a picture, a sound or a transition goes.</summary>
    public void Delete(bool closeUp)
    {
        StopPlaying();
        switch (Selection)
        {
            case EditorSelection.Text when HasTextSelection:
                _project.RemoveTexts([SelectedText]);
                ClearSelection();
                return;
            case EditorSelection.Image when HasImageSelection:
                _project.RemoveImages([SelectedImage]);
                ClearSelection();
                return;
            case EditorSelection.Sound when HasSoundSelection:
                _project.RemoveSounds([SelectedSound]);
                ClearSelection();
                return;
            case EditorSelection.Transition when SelectedTransition >= 0:
                _project.ChangePart(SelectedTransition, p => p.Transition = ClipTransitionKind.Cut);
                return;
        }
        var targets = TargetParts;
        if (targets.Count == 0)
            return;
        // Gaps selected close; parts that play are cut out.
        if (targets.All(i => _project.Parts[i].Blank))
            closeUp = true;
        if (!_project.Remove(targets, closeUp))
        {
            ShowMessage(_loc["clip.last_piece"]);
            return;
        }
        if (closeUp)
            ClearSelection();
        else
            SetSelection(EditorSelection.Parts);
    }

    [RelayCommand]
    private void DeleteSelected() => Delete(closeUp: false);

    [RelayCommand]
    private void DeleteRipple() => Delete(closeUp: true);

    [RelayCommand]
    public void CloseGaps() => _project.CloseGaps();

    /// <summary>Ctrl+C: the selected parts (or the one at the playhead), or the selected text, are kept to paste.</summary>
    [RelayCommand]
    public void Copy()
    {
        if (HasTextSelection)
        {
            _textClipboard = _project.Texts[SelectedText].Clone();
            _clipboard = [];
            return;
        }
        _textClipboard = null;
        _clipboard = TargetParts.Select(i => _project.Parts[i].Clone()).ToList();
    }

    /// <summary>Ctrl+V: what was copied goes in after the part at the playhead (a text, at the playhead).</summary>
    [RelayCommand]
    public void Paste()
    {
        StopPlaying();
        if (_textClipboard is { } text)
        {
            var copy = text.Clone();
            copy.Start = Playhead;
            SelectText(_project.AddText(copy));
            return;
        }
        if (_clipboard.Count == 0)
            return;
        var at = _project.IndexAt(Playhead) + 1;
        var first = _project.Insert(_clipboard, at);
        SelectRange(first, _clipboard.Count);
    }

    /// <summary>Ctrl+D: copies of the selected parts right after them (of a text, a picture or a sound, right after it).</summary>
    [RelayCommand]
    public void Duplicate()
    {
        StopPlaying();
        if (HasTextSelection)
        {
            var copy = _project.Texts[SelectedText].Clone();
            copy.Start = Math.Min(Length - ClipRange.MinLength, copy.Start + copy.Length);
            SelectText(_project.AddText(copy));
            return;
        }
        if (HasImageSelection)
        {
            var copy = _project.Images[SelectedImage].Clone();
            copy.Start = Math.Min(Length - ClipRange.MinLength, copy.Start + copy.Length);
            Select(new EditorItem(EditorItemKind.Image, _project.AddImage(copy)));
            return;
        }
        if (HasSoundSelection)
        {
            var sound = _project.Sounds[SelectedSound];
            var index = _project.AddSound(sound.Path, sound.Length, sound.Track, sound.Offset + sound.PlayLength(Length));
            _project.ChangeSound(index, s =>
            {
                s.From = sound.From;
                s.Play = sound.Play;
                s.Volume = sound.Volume;
                s.Fade = sound.Fade;
            });
            Select(new EditorItem(EditorItemKind.Sound, index));
            return;
        }
        var targets = TargetParts;
        if (targets.Count == 0)
            return;
        var parts = targets.Select(i => _project.Parts[i]).ToList();
        var first = _project.Insert(parts, targets[^1] + 1);
        SelectRange(first, parts.Count);
    }

    private void SelectRange(int first, int count)
    {
        if (first < 0)
            return;
        _selectedParts.Clear();
        _selectedParts.AddRange(Enumerable.Range(first, count));
        SetSelection(EditorSelection.Parts);
    }

    /// <summary>A still of the picture at the playhead, two seconds long.</summary>
    [RelayCommand]
    public void Freeze()
    {
        StopPlaying();
        var index = _project.Freeze(Playhead);
        if (index < 0)
            ShowMessage(_loc["editor.cannot_freeze"]);
        else
            SelectRange(index, 1);
    }

    /// <summary>The selected parts play backwards (again: forwards).</summary>
    [RelayCommand]
    public void ToggleReverse()
    {
        var targets = TargetParts.Where(i => !_project.Parts[i].Blank && !_project.Parts[i].IsStill).ToList();
        if (targets.Count == 0)
            return;
        var reverse = !_project.Parts[targets[0]].Reverse;
        _project.ChangeParts(targets, p => p.Reverse = reverse);
    }

    /// <summary>A quarter turn clockwise of what is selected: parts, a text or a picture.</summary>
    [RelayCommand]
    public void TurnRight()
    {
        StopPlaying();
        if (HasTextSelection)
            _project.ChangeText(SelectedText, t => t.Rotation += 90);
        else if (HasImageSelection)
            _project.ChangeImage(SelectedImage, i => i.Rotation += 90);
        else if (TargetParts.Where(i => !_project.Parts[i].Blank).ToList() is { Count: > 0 } targets)
            _project.ChangeParts(targets, p => p.Rotation += 90);
    }

    /// <summary>A text of a style at the playhead, selected for typing.</summary>
    [RelayCommand]
    public void AddText(string? style)
    {
        StopPlaying();
        var kind = Enum.TryParse<ClipTextStyle>(style, true, out var parsed) ? parsed : ClipTextStyle.Title;
        var key = kind switch
        {
            ClipTextStyle.Caption => "editor.caption_sample",
            ClipTextStyle.Label => "editor.label_sample",
            _ => "editor.title_sample",
        };
        var start = Playhead >= Length - ClipRange.MinLength ? 0 : Playhead;
        SelectText(_project.AddText(ClipText.Of(kind, _loc[key], start, Math.Min(3, Math.Max(1, Length - start)))));
        TextEditRequested?.Invoke();
    }

    [RelayCommand]
    public void Undo()
    {
        StopPlaying();
        _project.Undo();
    }

    [RelayCommand]
    public void Redo()
    {
        StopPlaying();
        _project.Redo();
    }

    /// <summary>Takes the silent stretches out of the parts (each video read once).</summary>
    [RelayCommand]
    public async Task RemoveSilenceAsync()
    {
        if (IsFindingSilence)
            return;
        StopPlaying();
        IsFindingSilence = true;
        try
        {
            var silences = new Dictionary<string, IReadOnlyList<ClipRange>>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in _project.Parts.Where(p => !p.Blank).Select(p => p.Source ?? _mainKey).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var media = MediaOf(source == _mainKey ? null : source);
                silences[source] = media is { HasAudio: true }
                    ? await _frames.FindSilencesAsync(media.Path, 0, media.Duration, _life.Token)
                    : [];
            }
            var removed = _project.RemoveSilences(source => silences.TryGetValue(source ?? _mainKey, out var found) ? found : []);
            ShowMessage(removed > 0 ? _loc.Format("clip.silence_removed", removed) : _loc["clip.no_silence"]);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not find the silences of the clip");
            ShowMessage(_loc["clip.no_silence"]);
        }
        finally
        {
            IsFindingSilence = false;
        }
    }

    /// <summary>Back to the quick cut: at once when the clip is one, else after asking.</summary>
    [RelayCommand]
    public void Back()
    {
        StopPlaying();
        if (ToQuickCut() is not null)
            BackRequested?.Invoke();
        else
            IsBackConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmBack()
    {
        IsBackConfirmOpen = false;
        BackRequested?.Invoke();
    }

    [RelayCommand]
    private void StayInEditor() => IsBackConfirmOpen = false;

    /// <summary>The clip as a quick cut of the window's video, or null when it uses what only the editor has.</summary>
    public ClipCutter? ToQuickCut() => ClipCutter.FromEdit(DurationOf(null), _project.ToEdit());

    /// <summary>The clip as it is (for a quick cut that starts over from it).</summary>
    public ClipEdit ToEdit() => _project.ToEdit();

    [RelayCommand]
    private void ToggleFiles()
    {
        IsFilesOpen = !IsFilesOpen;
        if (IsFilesOpen)
            IsPropertiesOpen = false;
    }

    [RelayCommand]
    private void ToggleProperties()
    {
        IsPropertiesOpen = !IsPropertiesOpen;
        if (IsPropertiesOpen)
            IsFilesOpen = false;
    }

    [RelayCommand]
    private void ZoomTimelineIn() => TimelineZoom = Math.Min(1, TimelineZoom + 0.15);

    [RelayCommand]
    private void ZoomTimelineOut() => TimelineZoom = Math.Max(0, TimelineZoom - 0.15);

    // ───────────────────────── Messages and the end ─────────────────────────

    private void ShowMessage(string text)
    {
        Message = text;
        _ = ClearMessageLaterAsync(text);
    }

    private async Task ClearMessageLaterAsync(string text)
    {
        try
        {
            await Task.Delay(MessageTime, _life.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (Message == text)
            Message = "";
    }

    private static Bitmap? Load(string file)
    {
        try
        {
            return new Bitmap(file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The work folder goes when the window closes.
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed)
                return;
            foreach (var media in Files.Where(m => m.IsAudio))
                media.Details = _loc["editor.sound"];
            BuildChoices();
            Sync();
            OnPropertyChanged(nameof(BusyText));
            OnPropertyChanged(nameof(PlayToolTip));
            OnPropertyChanged(nameof(GpuText));
        });
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        StopPlaying();
        _life.Cancel();
        _previewCts?.Cancel();
        _loc.LanguageChanged -= OnLanguageChanged;
        _project.Changed -= OnProjectChanged;
        TimelineChanged = null;
        PreviewChanged = null;
        DisposePictures();
        foreach (var media in _media.Values)
            media.Release();
        _media.Clear();
        Files.Clear();
        // _life is cancelled, not disposed: work still finishing may ask for its token.
    }
}
