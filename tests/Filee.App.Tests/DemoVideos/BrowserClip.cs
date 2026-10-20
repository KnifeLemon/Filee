// Clip "browser": a picture is dragged out of a web page with Ctrl held, the donut opens at the pointer, the picture
// is dropped on JPG, the download badge (speed · size) pops up above the progress ring while it is saved, the ring
// completes and the saved picture and its JPG appear in Downloads.

using Avalonia;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.App.Tests.DemoVideos;

internal sealed class BrowserClip : DemoClip
{
    /// <summary>The JPG slice of the image donut as Filee ships it.</summary>
    private const string TargetPreset = "to-jpg";

    private const double Press = 0.85, Open = 1.0, Drop = 2.55, End = 8.0;

    /// <summary>The picture's size and how long saving it takes (from the drop).</summary>
    private const long Total = 4_823_000;
    private const double Latency = 0.15, Saving = 1.25;

    private static readonly DemoFile Invoice = new("invoice.pdf", "PDF", Color.Parse("#D85A30"));
    private static readonly DemoFile Saved = new("lake.webp", "WEBP", Color.Parse("#7F77DD"), IsPhoto: true, Lake: true);
    private static readonly DemoFile Converted = new("lake.jpg", "JPG", Color.Parse("#BA7517"), IsPhoto: true, Lake: true);
    private static readonly DemoFile Dragged = new("", "", default, IsPhoto: true, Lake: true);

    private readonly RadialViewModel _vm;
    private readonly DonutLayer _donut;
    private readonly MousePath _path = new();
    private readonly Rect _browser, _downloads;
    private readonly Point _keyCap, _picture, _rest;
    private readonly DownloadBadge _badge = new(AppHost.Get<ILocalizer>());
    private JobViewModel? _job;
    private int _target = -1;
    private double _saved = -1;      // when the picture was saved into Downloads (the badge goes)
    private double _converted = -1;  // when its JPG appeared there (the ✓ on the ring)
    private double _nextTick;

    public BrowserClip(DemoFormat format, DemoPainter painter, RadialViewModel vm) : base(format, painter)
    {
        _vm = vm;
        (_browser, _downloads, _keyCap) = format.Name switch
        {
            "square" => (new Rect(20, 34, 500, 440), new Rect(318, 420, 384, 254), new Point(160, 640)),
            "vertical" => (new Rect(14, 64, 512, 500), new Rect(70, 596, 456, 266), new Point(270, 908)),
            _ => (new Rect(100, 24, 600, 500), new Rect(640, 300, 360, 250), new Point(60, 536)),
        };
        _donut = new DonutLayer(vm, format.Scale);
        _picture = DemoPainter.BrowserPicture(_browser).Center;
        _rest = _picture + new Point(170, 130);
        _path.At(0, _rest).Hold(0.15)
            .At(0.75, _picture + new Point(-20, 10), 0.1).Hold(Press)
            .At(Open, _picture + new Point(-12, 15), 0);
    }

    public override double Duration => End;

    private Point Pointer(double t) => _path[t];

    private Point Slot(int slot) => DemoFormat.Slot(_downloads, 0, 3, slot);

    protected override void Step(double t)
    {
        if (Cues.Hit(Open))
        {
            // The browser's file stands as a placeholder named like the file it will be (WebDrop).
            _donut.OpenAt(Pointer(Open), [@"C:\Users\demo\Downloads\lake.webp"]);
            PlanAfterOpening();
        }
        if (_donut.IsShown && t < Drop)
            _donut.DragOver(Pointer(t));

        if (Cues.Hit(Drop))
        {
            var hit = _donut.Drop(Pointer(t));
            if (hit != new DonutHit(DonutHitKind.Segment, _target))
                throw new InvalidOperationException($"The picture was dropped on {hit}, not on slice {_target}.");
            _donut.Donut.BeginProgress();
            _donut.Donut.SetProgress(0.02);
            // What RadialController shows for the job: the ring, and the badge bound to the job (RadialViewModel.ProgressJob).
            var preset = AppHost.Get<Filee.Core.Settings.UserDataStore>().FindPreset(TargetPreset)!;
            var job = new ConversionJob([@"C:\Users\demo\Downloads\lake.webp"], preset, "JPG");
            _job = new JobViewModel(job, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { });
            _vm.ProgressJob = _job;
            _nextTick = 0;
        }

        if (_job is not null && t >= Drop)
        {
            var e = t - Drop;
            var done = Latency + Saving;
            // The badge is rewritten a few times a second (JobViewModel's timer), from what has arrived so far.
            if (e >= _nextTick - 1e-9)
            {
                _nextTick += DownloadBadge.Interval.TotalSeconds;
                var received = (long)(Total * Ease.Clamp01((e - Latency) / Saving));
                var text = _badge.Update(TimeSpan.FromSeconds(e), e < done ? new JobTransfer(received, Total, 0, 1, received) : null);
                if (text is not null)
                    _job.DownloadText = text;
                _job.IsDownloading = text is not null;
                if (text is null && e >= done && _saved < 0)
                    _saved = t;
            }
            if (e < done)
                _donut.Donut.SetProgress(0.02 + 0.48 * Ease.Clamp01((e - Latency) / Saving));
            if (Cues.Hit(Drop + done + 0.3))
                _donut.Donut.SetProgress(0.8);
            if (Cues.Hit(Drop + done + 0.55))
                _donut.Donut.CompleteProgress(DonutProgressOutcome.Succeeded, () =>
                {
                    _vm.ProgressJob = null;
                    _donut.Hide();
                });
        }

        if (_donut.IsShown)
            _donut.Step();
        if (_converted < 0 && _donut.Donut.ShownOutcome is not null)
            _converted = t;
    }

    /// <summary>From the hole to JPG; after the drop, down towards Downloads, then onto the new JPG.</summary>
    private void PlanAfterOpening()
    {
        var donut = _donut.Donut;
        var presets = _vm.Presets;
        _target = presets.ToList().FindIndex(p => p.Id == TargetPreset);
        var n = donut.Items!.Count;
        var inner = donut.OuterRadius * donut.HoleRatio;
        var onTarget = DonutGeometry.PointAt(_donut.Center, (inner + donut.OuterRadius) / 2 + 6, DonutGeometry.MidAngle(_target, n) + 0.05);
        _path.Hold(1.55)
            .At(2.1, onTarget, -0.12).Hold(Drop)
            .At(3.5, Slot(1) + new Point(30, 92), 0.12).Hold(5.75)
            .At(6.3, Slot(2) + new Point(8, 6), 0.08).Hold(7.0)
            .At(End - 0.08, _rest, 0.1).Hold(End);
    }

    protected override void Draw(DrawingContext dc, double t)
    {
        var p = Painter;
        var pointer = Pointer(t);
        var leaving = 1 - Ease.Span(t, 7.25, 0.45);
        Backdrop(dc);

        var files = 1 + (_saved >= 0 && t < 7.7 ? 1 : 0) + (_converted >= 0 && t < 7.7 ? 1 : 0);
        p.StatusLine(dc, _downloads, 0, files);
        p.File(dc, Slot(0), Invoice);
        Arrive(_saved, Slot(1), Saved, hover: 0);
        Arrive(_converted, Slot(2), Converted, hover: Ease.Window(t, 6.15, 7.1, 0.15) * 0.55);

        _donut.Draw(dc);

        p.DragGhost(dc, pointer, Dragged, t >= Open && t < Drop ? Ease.Span(t, Open - 0.1, 0.12) : 0);
        p.Ripple(dc, Pointer(Press), t - Press);
        p.Ripple(dc, Pointer(Drop), t - Drop);
        p.KeyCap(dc, _keyCap, "Ctrl", Ease.Window(t, 0.42, 2.0, 0.14), Ease.Span(t, 0.5, 0.06) * (1 - Ease.Span(t, 1.5, 0.08)));
        p.Pointer(dc, pointer, t >= Press && t < Drop ? 1 : 0);

        // A file popping into Downloads with a glow that fades, and going again as the loop winds down.
        void Arrive(double since, Point at, DemoFile file, double hover)
        {
            if (since < 0 || t >= 7.7)
                return;
            var shown = Ease.Span(t, since, 0.3) * leaving;
            var glow = 1 - Ease.Span(t, since + 0.5, 1.0);
            p.FileHighlight(dc, at, Math.Max(glow, hover) * shown);
            var scale = (0.55 + 0.45 * Ease.OutBack(Ease.Span(t, since, 0.42))) * (1 - 0.1 * Ease.Span(t, 7.25, 0.45));
            using (dc.PushOpacity(shown))
            using (dc.PushTransform(Matrix.CreateTranslation(-at.X, -at.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
                p.File(dc, at, file);
        }
    }

    protected override void DrawBackdrop(DrawingContext dc)
    {
        Painter.Desktop(dc, Format);
        Painter.Browser(dc, _browser, "Lake at dawn", "photos.example/lake-at-dawn", "Photo journal", "Lake at dawn");
        Painter.FileManager(dc, _downloads, 0, "Home", "Downloads", [], -1);
    }

    public override void Dispose()
    {
        _vm.ProgressJob = null;
        _donut.Dispose();
        base.Dispose();
    }
}
