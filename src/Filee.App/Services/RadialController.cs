// Opens, fills and closes the live donut toolbar.
//
// Drag mode  (modifier + drag in Explorer):
//   gesture detected → an invisible window waits at the cursor (not activated) → DragEnter brings the file list →
//   the donut pops out → drop on a slice converts / drop in the hole or release elsewhere closes. Without files
//   (text, a rubber-band selection) nothing ever shows.
// Files from a web browser (a picture, a link to a PDF) stand as placeholder paths named like the files will be, so the
// donut shows their slices at once; they are saved when the conversion starts (WebDrop, ConversionService).
// Click mode (keyboard shortcut, context menu, drop zone):
//   files are known up front → donut appears activated → click a slice to convert, right-click to edit,
//   Esc / click outside closes.
// The flip key (Tab) and the mouse wheel flip the open donut to its other face (DonutMenu.Flip.cs): in click mode the
// focused window gets them, in drag mode (no focus during an OS drag) the global hook reports them while the donut
// shows files.
// After a drop / click the donut becomes a progress ring at the same spot (DonutMenu.Progress.cs):
//   finished quickly → ✓ pops up in the centre, then the ring fades out;
//   still running after HandOffAfter, failed, Esc, click elsewhere or a new gesture → the toast takes over.

using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services.Triggers;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Filee.Core.Web;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class RadialController(
    RadialViewModel viewModel,
    ConversionService conversions,
    WindowService windows,
    UserDataStore store,
    IPlatformServices platform,
    TriggerService triggers,
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
    private int _dragGeneration;
    private bool _dragReachedDonut;
    private bool _dragEndedOnDonut;   // the window was told how the drag over it ended: a drop or a leave
    private bool _fileDragsAccepted;
    private readonly ActivationGrace _grace = new();

    /// <summary>A gesture over something that isn't a file drag (a rubber-band selection) is common: a line now and then.</summary>
    private static readonly LogThrottle Throttle = new(TimeSpan.FromMinutes(1));
    private bool _converted;   // a conversion started from the files on the donut now
    private readonly HashSet<string> _formatRequestsOffered = new(StringComparer.OrdinalIgnoreCase);
    private WebDrop? _web;      // the browser drop the click-mode donut's files stand for, until a conversion takes it
    private bool _toClipboard;  // the click-mode donut's files came from the clipboard: their results go back on it
    private bool _keepOrder;    // the click-mode donut's files come in the order given (the Home page's queue), not sorted
    private Action<ConversionJob>? _onJob;  // told the conversion the donut starts (the Home page's list follows it)
    private bool _forLaunches;  // the click-mode donut's files came from later launches, so more of them can join it
    private bool _dragBackFace; // the drag gesture opens the donut on its back face, once files enter it

    /// <summary>
    /// How long a drag that never brought files may keep the invisible window up, in case the button release is
    /// missed (the system dropped the input hook, a UAC prompt took the desktop): the window catches clicks.
    /// </summary>
    private static readonly TimeSpan EmptyDragLimit = TimeSpan.FromSeconds(10);

    public bool IsVisible => _visible;

    internal RadialWindow? Window => _window;

    /// <summary>
    /// Drag gesture started at the given physical screen point. The window is placed under the cursor but stays
    /// invisible until files are dragged into it (FilesEntered); a gesture without files (text, a rubber-band
    /// selection, nothing at all) never shows anything and closes when the button is released.
    /// <paramref name="backFace"/>: the gesture opens the donut on its back face (the front when the back has no slice
    /// for the files).
    /// </summary>
    public void ShowForDrag(int x, int y, bool backFace = false)
    {
        // A donut with files that nothing was dropped on yet is about to lose them (#784): a line, as it looks like a
        // drop that did nothing.
        if (_visible && !_dropped && !viewModel.IsClickMode && viewModel.HasFiles)
            log.LogInformation("A new drag gesture replaced the donut's files before a drop");
        ReleaseProgressToToast();
        _grace.Cancel();
        ReplaceWeb(null);
        _toClipboard = false;
        var window = EnsureWindow();
        // A ring from an earlier conversion (finished or handed to the toast) must not keep this gesture's window up.
        window.DonutControl.LeaveProgressMode();
        _dropped = false;
        _dragReachedDonut = false;
        _converted = false;
        _keepOrder = false;
        _onJob = null;
        _dragBackFace = backFace;
        viewModel.ResetForDrag();
        window.DonutControl.AllowContextEdit = false;
        PlaceAndShow(window, x, y, activate: false);
        WatchFlipKey();

        var generation = ++_dragGeneration;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == _dragGeneration && _visible && !viewModel.HasFiles && !viewModel.IsClickMode)
                Close(animate: false);
        }, EmptyDragLimit);
    }

    /// <summary>
    /// Opens the donut for known files (click mode). Shortcuts among them stand for the files they point to.
    /// <paramref name="forAnotherProcess"/>: the files came from a later launch or the Services menu, so on macOS the
    /// donut may lose focus once while Filee becomes the active app. <paramref name="web"/>: the files are placeholders
    /// of this browser drop (the drop zone, a picture on the clipboard), which the donut now owns.
    /// <paramref name="toClipboard"/>: the files came from the clipboard, and their results go back on it.
    /// <paramref name="backFace"/>: open on the back face (a shortcut set to it), or the front when the back has no slice
    /// for the files.
    /// </summary>
    /// <param name="keepOrder">
    /// The files are merged in the order given (the Home page's queue, which the user sorted), not sorted by name first.
    /// </param>
    public void ShowForFiles(int x, int y, IReadOnlyList<string> files, bool forAnotherProcess = false, WebDrop? web = null,
        bool toClipboard = false, bool keepOrder = false, Action<ConversionJob>? onJob = null, bool backFace = false)
    {
        if (web is null)
            files = platform.ResolveShortcuts(files);
        if (files.Count == 0)
        {
            web?.Dispose();
            return;
        }
        ReleaseProgressToToast();
        ReplaceWeb(web);
        _toClipboard = toClipboard;
        _keepOrder = keepOrder;
        _onJob = onJob;
        _forLaunches = forAnotherProcess;
        if (forAnotherProcess && OperatingSystem.IsMacOS())
            _grace.Start();
        else
            _grace.Cancel();
        var window = EnsureWindow();
        _dropped = false;
        _converted = false;
        viewModel.Load(files, clickMode: true, backFace);
        viewModel.FitToThisDonut();
        window.DonutControl.AllowContextEdit = true;
        // Set the initial pose before showing the reused window, avoiding a fully-open flash.
        window.DonutControl.PlayOpenAnimation();
        PlaceAndShow(window, x, y, activate: true);
        WatchFlipKey();
    }

    public void ShowForConnect(int x, int y)
    {
        ReleaseProgressToToast();
        _grace.Cancel();
        ReplaceWeb(null);
        _toClipboard = false;
        var window = EnsureWindow();
        _dropped = false;
        _converted = false;
        viewModel.LoadForConnect();
        viewModel.FitToThisDonut();
        window.DonutControl.AllowContextEdit = false;
        window.DonutControl.PlayOpenAnimation();
        PlaceAndShow(window, x, y, activate: true);
    }

    /// <summary>
    /// Adds files of later launches to the donut that is open for earlier ones, without opening it again. False when
    /// there is no such donut (closed, opened for something else, or a conversion took its files): they need their own.
    /// </summary>
    public bool TryAddFiles(IReadOnlyList<string> files)
    {
        if (!_visible || !_forLaunches || _converted || !viewModel.IsClickMode)
            return false;
        var all = viewModel.Files.Union(platform.ResolveShortcuts(files), FileSystemPaths.Comparer).ToList();
        if (all.Count > viewModel.Files.Count)
            viewModel.Load(all, clickMode: true, viewModel.IsBackFace);
        return true;
    }

    /// <summary>The mouse button of the drag gesture was released somewhere.</summary>
    public void OnDragGestureEnded()
    {
        if (!_visible || viewModel.IsClickMode)
            return;
        // No drag ever entered the donut's window: nothing was dragged (a rubber-band selection), or Windows kept
        // Explorer's drag out (Filee runs as administrator). Tells "the donut didn't react" reports apart.
        if (!_dragReachedDonut && Throttle.TryEnter("no-drag-enter", out var held))
            log.LogInformation("Drag gesture ended without a drag entering the donut{Elevation}{Held}",
                Program.Elevation.ShellCannotReachFilee ? " (Filee runs as administrator: Windows blocks drops from Explorer)" : "",
                LogThrottle.HeldText(held));
        // Nothing was brought to the donut: the window is invisible but catches clicks, so go right away.
        if (!viewModel.HasFiles && !_window!.DonutControl.IsInProgressMode)
        {
            Close(animate: false);
            return;
        }
        // A drop on the donut arrives around the same time as the button release; give it a moment.
        DispatcherTimer.RunOnce(() =>
        {
            if (!_visible || _dropped || viewModel.IsClickMode)
                return;
            // Neither a drop nor a leave: the system ended the drag without telling the window, or the drop is late (#784).
            if (_dragReachedDonut && !_dragEndedOnDonut)
                log.LogInformation("The donut closed 250 ms after the release, with no drop or leave from the system");
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
            {
                HandOffToToast();
                return;
            }
            if (_window.DonutControl.IsFinishingProgress)
                return;
            // A ring left over after it finished: nothing is showing any more, so close like any donut.
            _window.DonutControl.LeaveProgressMode();
        }
        _visible = false;
        ReturnKeyboard();
        WatchFlipKey();
        _grace.Cancel();
        ReplaceWeb(null);
        var window = _window;
        // A group about to open (its dwell timer running) must not open on a closing donut.
        window.DonutControl.CollapseGroup();
        if (animate)
            window.DonutControl.PlayCloseAnimation(() =>
            {
                if (!_visible)
                    window.Hide();
            });
        else
            window.Hide();
        OfferFormatRequest();
    }

    /// <summary>
    /// The donut closed on files Filee can't read at all (".ncm"): offer to request the format, once per extension
    /// while Filee runs. Files it reads, even mixed with unknown ones, get a conversion and the toast instead.
    /// </summary>
    private void OfferFormatRequest()
    {
        if (_converted || viewModel.Files.Count == 0 || ProfileSelector.DetectFormats(viewModel.Files).Count > 0)
            return;
        var extensions = FormatRequest.UnknownExtensions(viewModel.Files).Where(_formatRequestsOffered.Add).ToList();
        if (extensions.Count == 0)
            return;
        log.LogInformation("Donut closed on files Filee can't read: {Extensions}", FormatRequest.Label(extensions));
        windows.ShowFormatRequestNotice(extensions);
    }

    private RadialWindow EnsureWindow()
    {
        if (_window is not null)
            return _window;

        var window = new RadialWindow { DataContext = viewModel };
        var donut = window.DonutControl;

        // macOS uses a panel while the donut is shown over a full-screen app. Restore its original window class
        // after any hide and before Avalonia releases it at quit, including a quit while it is still visible (#805).
        void RestoreNativeWindow() => platform.RestoreWindow(window.TryGetPlatformHandle()?.Handle ?? 0);
        window.Closing += (_, _) => RestoreNativeWindow();
        window.PropertyChanged += (_, change) =>
        {
            if (change.Property == RadialWindow.IsVisibleProperty && !change.GetNewValue<bool>())
                RestoreNativeWindow();
        };

        // On native Wayland, hiding an xdg_toplevel surface causes the compositor to destroy its surface,
        // and re-showing raises an InvalidOperationException. Track Closed to re-create on the next gesture.
        if (Filee.Platform.Linux.WaylandSupport.NativeWaylandRequested)
        {
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window))
                    _window = null;
            };
        }

        window.DragEntered += () => (_dragReachedDonut, _dragEndedOnDonut) = (true, false);
        // Tells "the drag reached the donut but brought no files" apart, with what it brought (file promises, say).
        window.DragWithoutFiles += formats =>
        {
            if (Throttle.TryEnter("drag-without-files", out var held))
                log.LogInformation("A drag without files Filee reads entered the donut; it offered: {Formats}{Held}",
                    formats, LogThrottle.HeldText(held));
        };
        window.FilesEntered += files =>
        {
            if (viewModel.IsClickMode || SameFiles(files, viewModel.Files) || SameWebFiles(files, viewModel.Files))
                return;
            viewModel.Load(files, clickMode: false, _dragBackFace);
            donut.PlayOpenAnimation();
            TakeKeyboard(window);
            WatchFlipKey();
        };
        window.Dropped += (hit, dropped) =>
        {
            _dropped = true;
            WatchFlipKey();
            if (hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment)
            {
                Convert(hit.Index, hit.Sub, dropped.Files.Count > 0 ? dropped.Files : viewModel.Files, dropped.Web);
            }
            else
            {
                dropped.Web?.Dispose();
                Close();
            }
        };
        // One line per drag, to tell "the drag never reached the donut" from "it did but nothing lit up" when someone
        // reports a donut that doesn't react, and a drop from a drag the system ended without one (#784).
        window.DragSummary += (events, hit, lit, dropped) =>
        {
            _dragEndedOnDonut = true;
            log.LogInformation("Drag over the donut: {Events} move event(s), ended on {Kind} {Index}, drawn lit {Lit:0.00}{Drop}",
                events, hit.Kind, hit.Index, lit, dropped ? "" : ", left without a drop");
        };
        window.EscapePressed += () => Close();
        // The flip key comes from the window while it has the keyboard, else from the hook (WatchFlipKey): followed by
        // the value, which the Activated event may come before.
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.WindowBase.IsActiveProperty)
                WatchFlipKey();
        };
        window.Deactivated += (_, _) =>
        {
            if (!viewModel.IsClickMode || _editing)
                return;
            if (_grace.Forgives())
            {
                log.LogInformation("The donut lost focus as it opened for another app's request; it stays open");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_visible && viewModel.IsClickMode && !window.IsActive)
                        window.Activate();
                });
                return;
            }
            Close();
        };

        donut.ActiveIndexChanged += (_, index) => viewModel.ShowHint(index, donut.ActiveSubIndex, donut.IsCenterHot);
        // Drag mode: the flip key and the wheel seen by the global hook (click mode gets them in the focused window, and so
        // does a drag-mode donut that has the keyboard: see TakeKeyboard).
        triggers.FlipKeyPressed += (_, _) =>
        {
            if (HookFlipsKey)
                donut.Flip();
        };
        triggers.FlipWheelTurned += (_, _) =>
        {
            if (FlipsFromHook)
                donut.FlipFromWheel(Environment.TickCount64);
        };
        donut.SegmentInvoked += (_, index) => Convert(index, -1, viewModel.Files, TakeWeb());
        donut.SubSegmentInvoked += (_, hit) => Convert(hit.Group, hit.Sub, viewModel.Files, TakeWeb());
        donut.CenterInvoked += (_, _) => Close();
        donut.SegmentContextRequested += async (_, index) => await EditPresetAsync(index);
        donut.SubSegmentContextRequested += async (_, hit) => await EditPresetAsync(hit.Group, hit.Sub);
        donut.ProgressCancelRequested += (_, _) => _progressJob?.CancelCommand.Execute(null);
        donut.ProgressClicked += (_, _) =>
        {
            if (_lastRingJob is { HasOutputs: true } job)
                job.OpenFolderCommand.Execute(null);
        };

        _window = window;
        return window;
    }

    /// <summary>
    /// Converts with slice <paramref name="index"/>, or with slice <paramref name="sub"/> of that group's ring.
    /// <paramref name="web"/>: the browser drop the files stand for, handed to the conversion (or disposed).
    /// </summary>
    private void Convert(int index, int sub, IReadOnlyList<string> files, WebDrop? web = null)
    {
        if (viewModel.IsConnectMode || viewModel.PresetAt(index, sub)?.Id == "preset.filee_connect")
        {
            KeepKeyboard();
            Close(animate: false);
            windows.ShowFileeConnect();
            return;
        }
        // Placeholders whose drop is gone (the drag listed files it didn't hand over in the end) can't be converted.
        var orphaned = web is null && files.Any(WebDrop.IsPlaceholder);
        if (viewModel.PresetAt(index, sub) is not { } preset || SourcesFor(preset, files) is not { Count: > 0 } sources || orphaned)
        {
            // Said in the log, as a drop that closes the donut looks the same as one the system never delivered (#784).
            log.LogInformation("The donut closed without converting: {Reason}",
                viewModel.PresetAt(index, sub) is not { } slice ? $"no preset at slice {index}/{sub}"
                : orphaned ? "the browser drop behind the files is gone"
                : $"none of the {files.Count} file(s) fit preset {slice.Id}");
            web?.Dispose();
            Close();
            return;
        }

        if (preset.IsTool && !preset.IsCopyToFolder)
        {
            log.LogInformation("Opening {Preset} for {Count} file(s)", preset.Id, sources.Count);
            var near = _window?.Position;
            KeepKeyboard(); // for the tool's window
            Close(animate: false);
            if (web is null)
                windows.ShowTool(sources, preset, near);
            else
                _ = ShowToolForBrowserFilesAsync(web, preset, near);
            return;
        }

        // Merged PDFs, joined clips, slideshows and merged pictures follow the order the user picks.
        if ((preset.TargetFormat == "pdf" && preset.Pdf.MergeIntoSingle || preset.JoinsIntoOneMedia || preset.JoinsImages) && sources.Count > 1)
        {
            KeepKeyboard(); // for the order window
            Close(animate: false);
            _ = MergeInOrderAsync(sources, preset.Clone(), web, _toClipboard, _keepOrder);
            return;
        }

        // "Ask every time" for the pages (#981): a small window per PDF first, then the save dialog when that asks too; the
        // toast shows the jobs, the donut has closed for the window.
        if (AskedPages.Asks(preset) && sources.Any(AskedPages.IsPdf))
        {
            KeepKeyboard(); // for the pages window
            Close(animate: false);
            _ = AskPagesThenConvertAsync(sources, preset, web, _toClipboard, _onJob);
            return;
        }

        // "Ask every time" (#521): the save dialog first; the toast shows the job, the donut has closed for the dialog.
        // shortcut: files from a browser aren't asked about, they save into the download folder as "next to the source".
        if (preset.Output.AskEveryTime && (web is null || preset.IsCopyToFolder))
        {
            KeepKeyboard(); // for the save dialog
            Close(animate: false);
            _ = AskThenConvertAsync(sources, preset, _toClipboard, web, _onJob);
            return;
        }

        log.LogInformation("Converting {Count} file(s) with preset {Preset}{Web}{Clipboard}", sources.Count, preset.Id,
            web is null ? "" : " (from a browser)", _toClipboard ? " (from the clipboard)" : "");
        var job = conversions.Start(sources, preset, showInToast: false, web, _toClipboard);
        if (job is null || _window is null)
        {
            Close();
            return;
        }
        _onJob?.Invoke(job.Job);
        _converted = true;
        ShowProgress(job);
        ReturnKeyboard(); // the ring shows the conversion; nothing here needs the keyboard any more
    }

    private async Task MergeInOrderAsync(IReadOnlyList<string> files, Preset preset, WebDrop? web, bool toClipboard, bool keepOrder)
    {
        // The donut's job callback, taken now: the window may be waiting a while, and another donut may open meanwhile.
        var onJob = _onJob;
        try
        {
            // The queue's order is the one the user already set on the Home page: the chooser only shows for a merge that
            // has more to pick (the layout of merged pictures, the music of merged videos).
            var videos = preset is { JoinsIntoOneMedia: true, TargetFormat: "mp4", Media.Slideshow: false };
            var ordered = keepOrder && !preset.JoinsImages && !videos
                ? files
                : await windows.ChooseMergeOrderAsync(files, preset, keepOrder);
            if (ordered is not null && preset.JoinsImages)
                RememberMergeLayout(preset);
            if (ordered is not null && videos)
                RememberLoopMusic(preset);
            if (ordered is not null && preset.Output.AskEveryTime && web is null)
                await AskThenConvertAsync(ordered, preset, toClipboard, null, onJob);
            else if (ordered is not null)
            {
                var job = conversions.Start(ordered, preset, web: web, toClipboard: toClipboard);
                if (job is not null)
                    onJob?.Invoke(job.Job);
                web = null; // the conversion owns the browser drop now
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Could not start the ordered merge");
        }
        finally
        {
            web?.Dispose();
        }
    }

    /// <summary>
    /// "Merge images": the layout chosen in the merge order window (<paramref name="chosen"/>, a copy of the preset)
    /// goes into the preset as saved, so the window opens with it next time.
    /// </summary>
    private void RememberMergeLayout(Preset chosen)
    {
        if (store.FindPreset(chosen.Id) is not { JoinsImages: true } saved)
            return;
        var image = saved.Image;
        if (image.JoinDirection == chosen.Image.JoinDirection && image.JoinFit == chosen.Image.JoinFit
            && image.JoinGap == chosen.Image.JoinGap && image.JoinBackground == chosen.Image.JoinBackground)
            return;
        image.JoinDirection = chosen.Image.JoinDirection;
        image.JoinFit = chosen.Image.JoinFit;
        image.JoinGap = chosen.Image.JoinGap;
        image.JoinBackground = chosen.Image.JoinBackground;
        try
        {
            store.SaveLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not remember the layout of merged pictures");
        }
    }

    /// <summary>
    /// "Merge videos": whether the music repeats, as chosen in the merge order window (<paramref name="chosen"/>, a copy
    /// of the preset), goes into the preset as saved, as the layout of merged pictures does.
    /// </summary>
    private void RememberLoopMusic(Preset chosen)
    {
        if (store.FindPreset(chosen.Id) is not { JoinsIntoOneMedia: true } saved || saved.Media.LoopMusic == chosen.Media.LoopMusic)
            return;
        saved.Media.LoopMusic = chosen.Media.LoopMusic;
        try
        {
            store.SaveLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not remember whether merged videos loop their music");
        }
    }

    /// <summary>
    /// "Choose pages" (#981): one window per PDF, in the order dropped, each with its own page count, so two PDFs can
    /// give two different chapters. Cancelling any of them starts nothing. PDFs given the same pages convert as one
    /// job; other files dropped with them convert as the preset says. Files from a browser are saved first, as for a
    /// tool: the window counts the pages of the PDF itself.
    /// </summary>
    private async Task AskPagesThenConvertAsync(IReadOnlyList<string> files, Preset preset, WebDrop? web, bool toClipboard,
        Action<ConversionJob>? onJob)
    {
        try
        {
            if (web is not null)
            {
                var drop = web;
                web = null; // saving it disposes it
                files = await conversions.SaveBrowserFilesAsync(drop, (placeholder, ex) =>
                    log.LogWarning(ex, "Saving {File} from a browser for {Preset} failed", Path.GetFileName(placeholder), preset.Id));
            }
            var pdfs = files.Where(AskedPages.IsPdf).ToList();
            var chosen = new List<(string File, PageChoice Choice)>();
            var asked = preset;
            for (var i = 0; i < pdfs.Count; i++)
            {
                if (await windows.AskPagesAsync(pdfs[i], asked, i, pdfs.Count) is not { } choice)
                {
                    log.LogInformation("Choosing the pages for {Preset} was cancelled; nothing converts", preset.Id);
                    return;
                }
                chosen.Add((pdfs[i], choice));
                // The next PDF's window starts with this one's choice of one PDF or how it is split.
                asked = AskedPages.Apply(preset, preset.Pdf.PageRange, choice.SplitPages, choice.SplitEvery, choice.SplitParts);
            }
            if (chosen.Count == 0)
                return;
            RememberSplitChoice(preset, chosen[^1].Choice);

            var runs = new List<(IReadOnlyList<string> Files, Preset Preset)>();
            var others = files.Where(file => !AskedPages.IsPdf(file)).ToList();
            if (others.Count > 0)
                runs.Add((others, preset));
            runs.AddRange(chosen.GroupBy(c => c.Choice).Select(group =>
                ((IReadOnlyList<string>)[.. group.Select(c => c.File)], AskedPages.Apply(preset, group.Key.Range, group.Key.SplitPages, group.Key.SplitEvery, group.Key.SplitParts))));
            log.LogInformation("Converting {Count} PDF(s) with preset {Preset} on the pages chosen", pdfs.Count, preset.Id);
            foreach (var (runFiles, runPreset) in runs)
            {
                if (runPreset.Output.AskEveryTime)
                    await AskThenConvertAsync(runFiles, runPreset, toClipboard, null, onJob);
                else if (conversions.Start(runFiles, runPreset, toClipboard: toClipboard) is { } job)
                    onJob?.Invoke(job.Job);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Could not convert on the pages chosen");
        }
        finally
        {
            web?.Dispose();
        }
    }

    /// <summary>
    /// "Choose pages": one PDF, one file per page or the pages per file or parts, as chosen last
    /// (<paramref name="chosen"/>), goes into the preset as saved, so the window starts with it next time (as "Merge
    /// images" keeps its layout). The range is not kept; with one PDF the numbers of the last split stay.
    /// </summary>
    private void RememberSplitChoice(Preset preset, PageChoice chosen)
    {
        if (!AskedPages.OffersSplit(preset) || store.FindPreset(preset.Id) is not { } saved)
            return;
        var pdf = saved.Pdf;
        if (pdf.SplitPages == chosen.SplitPages
            && (!chosen.SplitPages || (pdf.SplitEvery, pdf.SplitParts) == (chosen.SplitEvery, chosen.SplitParts)))
            return;
        pdf.SplitPages = chosen.SplitPages;
        if (chosen.SplitPages)
            (pdf.SplitEvery, pdf.SplitParts) = (chosen.SplitEvery, chosen.SplitParts);
        try
        {
            store.SaveLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not remember how the pages were split");
        }
    }

    private async Task AskThenConvertAsync(IReadOnlyList<string> sources, Preset preset, bool toClipboard, WebDrop? web = null,
        Action<ConversionJob>? onJob = null)
    {
        try
        {
            // The job starts only once the user has chosen where to save: the list follows it then, as any other job.
            var job = await conversions.AskThenStartAsync(sources, preset, web,
                (running, one) => windows.AskSaveLocationAsync(sources, running, one), toClipboard);
            if (job is not null)
                onJob?.Invoke(job.Job);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Could not ask where to save");
        }
    }

    /// <summary>A tool (crop, clip) on files from a browser: they are saved first, then it opens on them.</summary>
    private async Task ShowToolForBrowserFilesAsync(WebDrop web, Filee.Core.Presets.Preset preset, PixelPoint? near)
    {
        var files = await conversions.SaveBrowserFilesAsync(web, (placeholder, ex) =>
            log.LogWarning(ex, "Saving {File} from a browser for {Preset} failed", Path.GetFileName(placeholder), preset.Id));
        if (files.Count > 0)
            windows.ShowTool(files, preset, near);
    }

    /// <summary>
    /// Keeps folders for direct folder actions and combined archives; other presets convert only the files beside them.
    /// </summary>
    internal static IReadOnlyList<string> SourcesFor(Preset preset, IReadOnlyList<string> files) =>
        preset.PacksIntoOneArchive || BuiltInData.AcceptsFolders(preset) ? files : [.. files.Where(path => !Directory.Exists(path))];

    // ───────── Progress ring ─────────

    private void ShowProgress(JobViewModel job)
    {
        var donut = _window!.DonutControl;
        _dropped = true;
        _progressJob = job;
        viewModel.ProgressJob = job;
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
        viewModel.ProgressJob = null;
    }

    private void HideWindow()
    {
        _visible = false;
        ReturnKeyboard();
        WatchFlipKey();
        _window?.DonutControl.CollapseGroup();
        _window?.Hide();
    }

    /// <summary>Edits the preset of slice <paramref name="index"/>, or of slice <paramref name="sub"/> of its group's ring.</summary>
    private async Task EditPresetAsync(int index, int sub = -1)
    {
        if (_window is null || viewModel.PresetShownAt(index, sub) is not { } preset)
            return;
        _editing = true;
        try
        {
            if (await windows.EditPresetAsync(preset, _window,
                folderInput: viewModel.Files.Count > 0 && viewModel.Files.All(Directory.Exists)))
            {
                store.SaveLibrary();
                viewModel.Load(viewModel.Files, viewModel.IsClickMode, viewModel.IsBackFace);
            }
        }
        finally
        {
            _editing = false;
        }
        if (_visible)
            _window.Activate();
    }

    /// <summary>
    /// Has the global hook report the flip key and the wheel only while a drag-mode donut shows files that could flip;
    /// they are left alone otherwise (see <see cref="TriggerService.WatchFlipKey"/>).
    /// </summary>
    private void WatchFlipKey()
    {
        triggers.WatchFlipKey = FlipsFromHook;
        // One source per press (#504): the window while it has the keyboard (click mode, or a drag-mode donut that took
        // it), else the hook.
        if (_window is not null)
            _window.DonutControl.FlipKeyFromHook = HookFlipsKey;
    }

    /// <summary>A drag-mode donut shows files and nothing was dropped yet: the hook's flip key and wheel flip it.</summary>
    private bool FlipsFromHook => _visible && !_dropped && !viewModel.IsClickMode && viewModel.HasFiles;

    /// <summary>The hook's flip key flips the donut: in drag mode, while its window doesn't have the keyboard.</summary>
    private bool HookFlipsKey => FlipsFromHook && _window?.IsActive != true;

    // The donut took the keyboard during a drag (TakeKeyboard) and hasn't given it back yet.
    private bool _tookKeyboard;

    /// <summary>
    /// macOS: once files are dragged onto the donut, it takes the keyboard without activating the entire application
    /// (by making the non-activating panel the key window), so the flip key reaches its window as a key press while
    /// leaving macOS "Show Desktop" (Desktop Reveal) mode uninterrupted. Watched from outside by the global hook it
    /// may not reach: while an app holds Secure Input (Terminal with Secure Keyboard Entry, which can keep it from the
    /// background) macOS gives no keys to a global hook, only to the key window and app in front. The drag carries on;
    /// the keyboard goes back to the app that had it once the conversion starts or the donut closes (<see cref="ReturnKeyboard"/>),
    /// releasing key window status so typing returns to the previous app while the conversion ring finishes, and stays
    /// with Filee when the drop opens a window of its own: a tool, the merge order, the save dialog (<see cref="KeepKeyboard"/>).
    /// Not on Windows, where a window taking the keyboard during a drag breaks Explorer's drag.
    /// </summary>
    private void TakeKeyboard(RadialWindow window)
    {
        if (!OperatingSystem.IsMacOS() || _tookKeyboard || window.IsActive)
            return;
        platform.RememberFrontApp();
        _tookKeyboard = true;
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        platform.TakeKeyWindow(handle);
        if (handle == 0)
            window.Activate();
    }

    /// <summary>The drop opens a window of Filee's own: the keyboard stays with Filee for it, not given back on close.</summary>
    private void KeepKeyboard() => _tookKeyboard = false;

    /// <summary>Gives the keyboard back to the app the donut took it from, if the donut still has it.</summary>
    private void ReturnKeyboard()
    {
        if (!_tookKeyboard)
            return;
        _tookKeyboard = false;
        var handle = _window?.TryGetPlatformHandle()?.Handle ?? 0;
        platform.ReleaseKeyWindow(handle);
        platform.ReturnFocus();
    }

    /// <summary>
    /// Top-left corner of a <paramref name="sidePx"/> square window centred on <paramref name="cursor"/>, moved into
    /// <paramref name="area"/> when that would cross its edge. With group slices the window holds the ring they fan out
    /// (<see cref="DonutMenu.SideFor"/>), so near a screen edge the donut moves in far enough for every ring to fit.
    /// </summary>
    internal static PixelPoint WindowPosition(PixelPoint cursor, PixelRect? area, int sidePx)
    {
        var position = new PixelPoint(cursor.X - sidePx / 2, cursor.Y - sidePx / 2);
        if (area is not { } a)
            return position;
        return new PixelPoint(
            Math.Clamp(position.X, a.X, Math.Max(a.X, a.Right - sidePx)),
            Math.Clamp(position.Y, a.Y, Math.Max(a.Y, a.Bottom - sidePx)));
    }

    private void PlaceAndShow(RadialWindow window, int x, int y, bool activate)
    {
        var cursor = new PixelPoint(x, y);
        var screen = window.Screens.ScreenFromPoint(cursor) ?? window.Screens.Primary;
        var scaling = OperatingSystem.IsMacOS() ? 1 : screen?.Scaling ?? 1;

        // Window size follows the donut size (SizeToContent); compute it up front to centre on the cursor.
        var side = DonutMenu.SideFor(viewModel.OuterRadius, viewModel.ReserveSubRing, viewModel.GroupRingSize);
        var sidePx = (int)Math.Ceiling(side * scaling);
        var position = WindowPosition(cursor, screen?.WorkingArea, sidePx);

        Motion.Refresh();
        window.Position = position;
        if (!window.IsVisible)
            window.Show();
        window.Position = position; // again, in case showing changed the DPI scale
        // Showing a window that is already marked topmost does not move it in the z-order, and Windows can push it
        // below normal windows while it keeps the mark (after the Photos app opened, for one). Then Explorer stays on
        // top of the donut and the drop never reaches it, so put it back on top every time.
        if (window.TryGetPlatformHandle()?.Handle is { } handle)
        {
            platform.RaiseTopmost(handle);
            if (!_fileDragsAccepted && OperatingSystem.IsMacOS() && platform is Filee.Platform.MacOS.MacOSPlatformServices mac)
            {
                mac.AcceptFileDrags(handle);
                _fileDragsAccepted = true;
            }
        }
        _visible = true;
        if (activate)
        {
            window.Activate();
            // ShowActivated is false because the same window is also used during an OS drag, where taking focus would
            // break Explorer's drag. Click mode is different: the donut owns the keyboard until it closes, so focus it
            // explicitly for Tab/arrow/Enter handling.
            window.DonutControl.Focus();
        }
    }

    private static bool SameFiles(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.SequenceEqual(b, FileSystemPaths.Comparer);

    /// <summary>The same browser files dragged in again: each entry makes new placeholders, with the same names.</summary>
    private static bool SameWebFiles(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.All(WebDrop.IsPlaceholder) && b.All(WebDrop.IsPlaceholder)
        && a.Select(Path.GetFileName).SequenceEqual(b.Select(Path.GetFileName), StringComparer.Ordinal);

    /// <summary>Hands the click-mode donut's browser drop to a conversion.</summary>
    private WebDrop? TakeWeb()
    {
        var web = _web;
        _web = null;
        return web;
    }

    /// <summary>The click-mode donut now stands for <paramref name="web"/>'s files (or local ones): the old drop is done.</summary>
    private void ReplaceWeb(WebDrop? web)
    {
        if (!ReferenceEquals(_web, web))
            _web?.Dispose();
        _web = web;
    }
}
