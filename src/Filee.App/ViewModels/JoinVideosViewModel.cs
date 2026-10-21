// "Join videos" from the donut: the videos in the order they are joined (sorted by name the way people count, moved
// before the join), what each is (its size and length, read as the window opens), and how videos of another shape fill
// the frame when they differ. Join starts one conversion that writes them one after the other (copied together when
// they share their format); Open in editor puts them on the editor's timeline instead.

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Engines.Media;

namespace Filee.App.ViewModels;

/// <summary>One video to join.</summary>
public sealed partial class JoinSource(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>"1920×1080 · 0:42", once read.</summary>
    [ObservableProperty] private string _details = "";

    internal VideoSummary? Video { get; set; }
}

/// <summary>What "Join videos" was asked to do.</summary>
public sealed record JoinChoice(IReadOnlyList<string> Files, ClipEdit Edit, bool OpenInEditor);

public sealed partial class JoinVideosViewModel : ObservableObject
{
    private readonly ILocalizer _loc;
    private readonly IVideoFrameSource _frames;
    private readonly CancellationTokenSource _cancel = new();

    public JoinVideosViewModel(IReadOnlyList<string> files, ILocalizer loc, IVideoFrameSource frames)
    {
        _loc = loc;
        _frames = frames;
        Files = new(files
            .OrderBy(file => System.IO.Path.GetFileName(file), NaturalComparer.Instance)
            .ThenBy(file => file, NaturalComparer.Instance)
            .Select(file => new JoinSource(file)));
        SelectedFile = Files.FirstOrDefault();
        Fits =
        [
            new(ClipFit.Blur, loc["editor.fit_blur"]),
            new(ClipFit.Bars, loc["editor.fit_bars"]),
            new(ClipFit.Crop, loc["editor.fit_crop"]),
        ];
        Fit = Fits[0];
        Resolutions =
        [
            new(0, loc["join.size_first"]),
            new(1080, "1080p"),
            new(720, "720p"),
        ];
        Resolution = Resolutions[0];
        Reading = loc["editor.reading"];
        _ = ReadAsync();
    }

    public ObservableCollection<JoinSource> Files { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private JoinSource? _selectedFile;

    public IReadOnlyList<Choice<ClipFit>> Fits { get; }
    public IReadOnlyList<Choice<int>> Resolutions { get; }

    [ObservableProperty] private Choice<ClipFit> _fit;
    [ObservableProperty] private Choice<int> _resolution;

    /// <summary>The videos differ in shape or size: how they fill the frame is asked.</summary>
    [ObservableProperty] private bool _differ;

    /// <summary>What was found: reading, all alike (joined quickly), or different shapes.</summary>
    [ObservableProperty] private string _reading;

    /// <summary>Every video was read: Join can start.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    private bool _isReady;

    /// <summary>What to do, once Join or Open in editor was chosen (null: cancelled).</summary>
    public JoinChoice? Result { get; private set; }

    public event Action? CloseRequested;

    private async Task ReadAsync()
    {
        var failed = 0;
        foreach (var file in Files.ToList())
        {
            try
            {
                var video = await _frames.ProbeAsync(file.Path, _cancel.Token);
                file.Video = video;
                file.Details = $"{video.Width}×{video.Height} · {Math.Round(video.FrameRate).ToString(CultureInfo.CurrentCulture)} {_loc["editor.fps"]} · {ClipRange.Display(video.Duration)}";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                file.Details = _loc["error.unsupported_source"];
                failed++;
            }
        }
        var read = Files.Where(f => f.Video is not null).ToList();
        Differ = read.Select(f => (f.Video!.Width, f.Video.Height)).Distinct().Count() > 1;
        Reading = failed > 0 ? _loc.Format("join.unreadable", failed)
            : Differ ? _loc["join.differ"]
            : _loc["join.alike"];
        IsReady = read.Count >= 1;
    }

    private bool CanMoveUp() => SelectedFile is not null && Files.IndexOf(SelectedFile) > 0;
    private bool CanMoveDown() => SelectedFile is not null && Files.IndexOf(SelectedFile) is >= 0 and var index && index < Files.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private void Move(int direction)
    {
        if (SelectedFile is not { } selected)
            return;
        var index = Files.IndexOf(selected);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Files.Count)
            return;
        Files.Move(index, target);
        SelectedFile = selected;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The videos that could be read, in order, as an editor clip of their whole lengths.</summary>
    internal ClipEdit Edit()
    {
        var read = Files.Where(f => f.Video is not null).ToList();
        var edit = new ClipEdit { Editor = true, Fit = Fit.Value, Resolution = Resolution.Value };
        for (var i = 0; i < read.Count; i++)
            edit.Parts.Add(new ClipPart { Source = i == 0 ? null : read[i].Path, Start = 0, End = read[i].Video!.Duration });
        return edit;
    }

    private IReadOnlyList<string> Ordered => Files.Where(f => f.Video is not null).Select(f => f.Path).ToList();

    [RelayCommand(CanExecute = nameof(IsReady))]
    private void Join()
    {
        if (Ordered.Count == 0)
            return;
        Result = new JoinChoice(Ordered, Edit(), OpenInEditor: false);
        Close();
    }

    [RelayCommand]
    private void OpenInEditor()
    {
        var files = IsReady ? Ordered : Files.Select(f => f.Path).ToList();
        if (files.Count == 0)
            return;
        Result = new JoinChoice(files, Edit(), OpenInEditor: true);
        Close();
    }

    [RelayCommand]
    private void Cancel() => Close();

    private void Close()
    {
        _cancel.Cancel();
        CloseRequested?.Invoke();
        CloseRequested = null;
    }
}
