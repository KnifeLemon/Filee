// Opens, fills and closes the live donut toolbar.
//
// Drag mode  (modifier + drag in Explorer):
//   gesture detected → an invisible window waits at the cursor (not activated) → DragEnter brings the file list →
//   the donut pops out → drop on a slice converts / drop in the hole or release elsewhere closes. Without files
//   (text, a rubber-band selection) nothing ever shows.
// Click mode (keyboard shortcut, context menu, drop zone):
//   files are known up front → donut appears activated → click a slice to convert, right-click to edit,
//   Esc / click outside closes.
// After a drop / click the donut becomes a progress ring at the same spot (DonutMenu.Progress.cs):
//   finished quickly → ✓ pops up in the centre, then the ring fades out;
//   still running after HandOffAfter, failed, Esc, click elsewhere or a new gesture → the toast takes over.

using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class RadialController(
    RadialViewModel viewModel,
    ConversionService conversions,
    WindowService windows,
    UserDataStore store,
    ILogger<RadialController> log)
{
    /// <summary>
    /// How long the ring shows a running job before the toast window takes over. Long enough for a typical office
    /// document (LibreOffice needs a few seconds), so the user sees it finish where they dropped it.
    /// </summary>
    private static readonly TimeSpan HandOffAfter = TimeSpan.FromSeconds(5);

    private RadialWindow? _window;
    private bool _visible;
    private bool _dropped;
    private bool _editing;
    private JobViewModel? _progressJob;   // job currently shown by the ring (null once finished / handed off)
    private JobViewModel? _lastRingJob;   // for "click the ✓ to open the folder"
    private IDisposable? _handOffTimer;

    public bool IsVisible => _visible;

    /// <summary>
    /// Drag gesture started at the given physical screen point. The window is placed under the cursor but stays
    /// invisible until files are dragged into it (FilesEntered); a gesture without files (text, a rubber-band
    /// selection, nothing at all) never shows anything and closes when the button is released.
    /// </summary>
    public void ShowForDrag(int x, int y)
    {
        ReleaseProgressToToast();
        var window = EnsureWindow();
        _dropped = false;
        viewModel.ResetForDrag();
        window.DonutControl.AllowContextEdit = false;
        PlaceAndShow(window, x, y, activate: false);
    }

    /// <summary>Opens the donut for known files (click mode).</summary>
    public void ShowForFiles(int x, int y, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return;
        ReleaseProgressToToast();
        var window = EnsureWindow();
        _dropped = false;
        viewModel.Load(files, clickMode: true);
        window.DonutControl.AllowContextEdit = true;
        PlaceAndShow(window, x, y, activate: true);
        window.DonutControl.PlayOpenAnimation();
    }

    /// <summary>The mouse button of the drag gesture was released somewhere.</summary>
    public void OnDragGestureEnded()
    {
        if (!_visible || viewModel.IsClickMode)
            return;
        // A drop on the donut arrives around the same time as the button release; give it a moment.
        DispatcherTimer.RunOnce(() =>
        {
            if (_visible && !_dropped && !viewModel.IsClickMode)
                Close(animate: true);
        }, TimeSpan.FromMilliseconds(250));
    }

    public void Close(bool animate = true)
    {
        if (!_visible || _window is null)
            return;
        if (_window.DonutControl.IsInProgressMode)
        {
            // Esc / click elsewhere while the ring is showing: let the toast carry on (a finishing ✓ just fades).
            if (_progressJob is not null)
                HandOffToToast();
            return;
        }
        _visible = false;
        var window = _window;
        if (animate)
            window.DonutControl.PlayCloseAnimation(() =>
            {
                if (!_visible)
                    window.Hide();
            });
        else
            window.Hide();
    }

    private RadialWindow EnsureWindow()
    {
        if (_window is not null)
            return _window;

        var window = new RadialWindow { DataContext = viewModel };
        var donut = window.DonutControl;

        window.FilesEntered += files =>
        {
            if (viewModel.IsClickMode || SameFiles(files, viewModel.Files))
                return;
            viewModel.Load(files, clickMode: false);
            donut.PlayOpenAnimation();
        };
        window.Dropped += (hit, files) =>
        {
            _dropped = true;
            if (hit.Kind == DonutHitKind.Segment)
                Convert(hit.Index, files.Count > 0 ? files : viewModel.Files);
            else
                Close();
        };
        window.EscapePressed += () => Close();
        window.Deactivated += (_, _) =>
        {
            if (viewModel.IsClickMode && !_editing)
                Close();
        };

        donut.ActiveIndexChanged += (_, index) => viewModel.ShowHint(index, donut.IsCenterHot);
        donut.SegmentInvoked += (_, index) => Convert(index, viewModel.Files);
        donut.CenterInvoked += (_, _) => Close();
        donut.SegmentContextRequested += async (_, index) => await EditPresetAsync(index);
        donut.ProgressCancelRequested += (_, _) => _progressJob?.CancelCommand.Execute(null);
        donut.ProgressClicked += (_, _) =>
        {
            if (_lastRingJob is { HasOutputs: true } job)
                job.OpenFolderCommand.Execute(null);
        };

        _window = window;
        return window;
    }

    private void Convert(int index, IReadOnlyList<string> files)
    {
        if (index < 0 || index >= viewModel.Presets.Count || !viewModel.Items[index].IsEnabled || files.Count == 0)
        {
            Close();
            return;
        }

        var preset = viewModel.Presets[index];
        log.LogInformation("Converting {Count} file(s) with preset {Preset}", files.Count, preset.Id);
        var job = conversions.Start(files, preset, showInToast: false);
        if (job is null || _window is null)
        {
            Close();
            return;
        }
        ShowProgress(job);
    }

    // ───────── Progress ring ─────────

    private void ShowProgress(JobViewModel job)
    {
        var donut = _window!.DonutControl;
        _dropped = true;
        _progressJob = job;
        _lastRingJob = job;
        donut.BeginProgress();
        donut.SetProgress(job.Progress / 100);
        job.PropertyChanged += OnProgressJobChanged;
        _handOffTimer = DispatcherTimer.RunOnce(() =>
        {
            if (_progressJob == job && job.IsRunning)
                HandOffToToast();
        }, HandOffAfter);
    }

    private void OnProgressJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not JobViewModel job || job != _progressJob || _window is null)
            return;
        var donut = _window.DonutControl;
        donut.SetProgress(job.Progress / 100);
        if (job.IsRunning || e.PropertyName != nameof(JobViewModel.IsRunning))
            return;

        var outcome = job.Job.State switch
        {
            JobState.Completed => DonutProgressOutcome.Succeeded,
            JobState.Cancelled => DonutProgressOutcome.Cancelled,
            _ => DonutProgressOutcome.Failed,
        };
        if (outcome == DonutProgressOutcome.Failed)
            conversions.ShowInToast(job); // the toast explains what went wrong
        DetachProgress();
        donut.CompleteProgress(outcome, HideWindow);
    }

    /// <summary>Hands the running job to the toast with a short "shrink away" of the ring.</summary>
    private void HandOffToToast()
    {
        if (_progressJob is null || _window is null)
            return;
        conversions.ShowInToast(_progressJob);
        DetachProgress();
        _window.DonutControl.HandOffProgress(HideWindow);
    }

    /// <summary>A new gesture needs the window right now: move the job to the toast without animation.</summary>
    private void ReleaseProgressToToast()
    {
        if (_progressJob is not null)
            conversions.ShowInToast(_progressJob);
        DetachProgress();
    }

    private void DetachProgress()
    {
        _handOffTimer?.Dispose();
        _handOffTimer = null;
        if (_progressJob is not null)
            _progressJob.PropertyChanged -= OnProgressJobChanged;
        _progressJob = null;
    }

    private void HideWindow()
    {
        _visible = false;
        _window?.Hide();
    }

    private async Task EditPresetAsync(int index)
    {
        if (_window is null || index < 0 || index >= viewModel.Presets.Count)
            return;
        _editing = true;
        try
        {
            if (await windows.EditPresetAsync(viewModel.Presets[index], _window))
            {
                store.SaveLibrary();
                viewModel.Load(viewModel.Files, viewModel.IsClickMode);
            }
        }
        finally
        {
            _editing = false;
        }
        if (_visible)
            _window.Activate();
    }

    private void PlaceAndShow(RadialWindow window, int x, int y, bool activate)
    {
        var cursor = new PixelPoint(x, y);
        var screen = window.Screens.ScreenFromPoint(cursor) ?? window.Screens.Primary;
        var scaling = screen?.Scaling ?? 1;

        // Window size follows the donut size (SizeToContent); compute it up front to centre on the cursor.
        var side = 2 * (viewModel.OuterRadius + 7 + 14);
        var sidePx = (int)Math.Ceiling(side * scaling);
        var position = new PixelPoint(x - sidePx / 2, y - sidePx / 2);
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            position = new PixelPoint(
                Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - sidePx)),
                Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - sidePx)));
        }

        window.Position = position;
        if (!window.IsVisible)
            window.Show();
        window.Position = position; // again, in case showing changed the DPI scale
        _visible = true;
        if (activate)
            window.Activate();
    }

    private static bool SameFiles(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
}
