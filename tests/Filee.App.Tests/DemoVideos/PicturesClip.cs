// The clips played in a file manager's Pictures folder: trip.jpg is dragged with Ctrl held, the donut opens at the
// pointer, the file is dropped on a slice, the progress ring completes and the converted file appears beside it.
// What happens on the donut in between is the clip's own.

using Avalonia;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.ViewModels;

namespace Filee.App.Tests.DemoVideos;

internal abstract class PicturesClip : DemoClip
{
    protected const double Press = 0.85, Open = 1.0, End = 8.0;

    /// <summary>A row of documents, then the photo at the start of the next row (it sorts last).</summary>
    private static readonly DemoFile[] Documents =
    [
        new("budget.xlsx", "XLSX", Color.Parse("#1D9E75")),
        new("notes.txt", "TXT", Color.Parse("#5F5E5A")),
        new("report.docx", "DOCX", Color.Parse("#378ADD")),
        new("slides.pdf", "PDF", Color.Parse("#D85A30")),
    ];

    protected static readonly DemoFile Photo = new("trip.jpg", "JPG", Color.Parse("#BA7517"), IsPhoto: true);
    private static readonly string[] Places = ["Home", "Desktop", "Documents", "Downloads", "Pictures"];

    private readonly DemoFile[] _files;
    private double _appeared = -1;  // when the converted file showed up (the ✓ on the ring)

    protected PicturesClip(DemoFormat format, DemoPainter painter, RadialViewModel vm) : base(format, painter)
    {
        ViewModel = vm;
        Donut = new DonutLayer(vm, format.Scale);
        _files = [.. Documents.Take(format.Columns), Photo];
        Source = format.Slot(_files.Length - 1);
        Rest = Source + new Point(250, 175);
        // Up to the drag starting; the rest is known once the donut is open (where its slices are).
        Path.At(0, Rest).Hold(0.15)
            .At(0.75, Source + new Point(4, -2), 0.1).Hold(Press)
            .At(Open, Source + new Point(10, 4), 0);
    }

    public override double Duration => End;

    protected RadialViewModel ViewModel { get; }
    protected DonutLayer Donut { get; }
    protected MousePath Path { get; } = new();

    /// <summary>Middle of trip.jpg's icon, and where the pointer rests at the start and end of the loop.</summary>
    protected Point Source { get; }
    protected Point Rest { get; }

    /// <summary>Where the converted file appears: the slot after trip.jpg.</summary>
    protected Point Converted => Format.Slot(_files.Length);

    /// <summary>When the file is dropped.</summary>
    protected abstract double Drop { get; }

    /// <summary>The file Filee writes.</summary>
    protected abstract DemoFile Result { get; }

    /// <summary>The pointer's way on the open donut to the slice it drops on, then aside (from <see cref="Open"/>).</summary>
    protected abstract void PlanOnDonut();

    /// <summary>What the drop must land on, so a change to the donut can't quietly drop the file elsewhere.</summary>
    protected abstract DonutHit Target { get; }

    /// <summary>Each frame while the file is dragged over the open donut.</summary>
    protected virtual void DraggedOver(double t, DonutHit hit)
    {
    }

    /// <summary>The keys shown at the bottom: Ctrl, held to start the drag.</summary>
    protected virtual void DrawKeys(DrawingContext dc, double t) =>
        Painter.KeyCap(dc, Format.KeyCap, "Ctrl", Ease.Window(t, 0.42, 2.1, 0.14), Ease.Span(t, 0.5, 0.06) * (1 - Ease.Span(t, 1.55, 0.08)));

    protected Point Pointer(double t) => Path[t];

    protected override void Step(double t)
    {
        if (Cues.Hit(Open))
        {
            Donut.OpenAt(Pointer(Open), [@"C:\Users\demo\Pictures\trip.jpg"]);
            PlanOnDonut();
        }
        if (Donut.IsShown && t < Drop)
            DraggedOver(t, Donut.DragOver(Pointer(t)));

        if (Cues.Hit(Drop))
        {
            var hit = Donut.Drop(Pointer(t));
            if (hit != Target)
                throw new InvalidOperationException($"The file was dropped on {hit}, not on {Target}.");
            Donut.Donut.BeginProgress();
            Donut.Donut.SetProgress(0.08);
        }
        if (Cues.Hit(Drop + 0.35))
            Donut.Donut.SetProgress(0.46);
        if (Cues.Hit(Drop + 0.6))
            Donut.Donut.SetProgress(0.8);
        if (Cues.Hit(Drop + 0.82))
            Donut.Donut.CompleteProgress(DonutProgressOutcome.Succeeded, Donut.Hide);

        if (Donut.IsShown)
            Donut.Step();
        if (_appeared < 0 && Donut.Donut.ShownOutcome is not null)
            _appeared = t;
    }

    /// <summary>After the drop: aside while the ring finishes, onto the new file, and back to where the loop starts.</summary>
    protected void PlanAfterDrop() =>
        Path.At(Drop + 0.75, Source + new Point(160, 92), 0.1).Hold(5.7)
            .At(6.25, Converted + new Point(8, 6), 0.08).Hold(7.0)
            .At(End - 0.08, Rest, 0.1).Hold(End);

    protected override void Draw(DrawingContext dc, double t)
    {
        var p = Painter;
        var pointer = Pointer(t);
        // The new file shows from the ✓ on the ring until it fades for the loop to start over.
        var shown = _appeared < 0 ? 0 : Ease.Span(t, _appeared, 0.38) * (1 - Ease.Span(t, 7.25, 0.45));
        var present = shown > 0 && t < 7.7;

        Backdrop(dc);
        p.StatusLine(dc, Format.Window, Format.Sidebar, present ? _files.Length + 1 : _files.Length);

        // The source stays selected from the press until the loop winds down; the new file glows as it arrives, then
        // shows the pointer resting on it.
        p.FileHighlight(dc, Source, Ease.Span(t, Press, 0.08) * (1 - Ease.Span(t, 7.2, 0.4)));
        for (var i = 0; i < _files.Length; i++)
            p.File(dc, Format.Slot(i), _files[i]);
        if (shown > 0)
        {
            var at = Converted;
            var arrival = 1 - Ease.Span(t, _appeared + 0.5, 1.0);
            var hover = Ease.Window(t, 6.1, 7.1, 0.15) * 0.55;
            p.FileHighlight(dc, at, Math.Max(arrival, hover) * shown);
            var grow = Ease.OutBack(Ease.Span(t, _appeared, 0.42));
            var scale = (0.55 + 0.45 * grow) * (1 - 0.1 * Ease.Span(t, 7.25, 0.45));
            using (dc.PushOpacity(shown))
            using (dc.PushTransform(Matrix.CreateTranslation(-at.X, -at.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
                p.File(dc, at, Result);
        }

        Donut.Draw(dc);

        p.DragGhost(dc, pointer, Photo, t >= Open && t < Drop ? Ease.Span(t, Open - 0.1, 0.12) : 0);
        p.Ripple(dc, Pointer(Press), t - Press);
        p.Ripple(dc, Pointer(Drop), t - Drop);
        DrawKeys(dc, t);
        p.Pointer(dc, pointer, t >= Press && t < Drop ? 1 : 0);
    }

    protected override void DrawBackdrop(DrawingContext dc)
    {
        Painter.Desktop(dc, Format);
        Painter.FileManager(dc, Format.Window, Format.Sidebar, "Home", "Pictures", Places, Places.Length - 1);
    }

    public override void Dispose()
    {
        Donut.Dispose();
        base.Dispose();
    }
}
