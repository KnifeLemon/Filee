// Clip "flip": the image donut opens with "Tab ↻ back" in its hub, Tab turns it over to its back face, and trip.jpg is
// dropped on "Remove background" there; trip_transparent.png appears beside it.

using Avalonia;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.ViewModels;

namespace Filee.App.Tests.DemoVideos;

internal sealed class FlipClip(DemoFormat format, DemoPainter painter, RadialViewModel vm) : PicturesClip(format, painter, vm)
{
    /// <summary>The back-face preset the file is dropped on (the image donut as Filee ships it).</summary>
    private const string TargetPreset = "remove-background";

    private const double Tab = 2.0;

    private int _target = -1;

    protected override double Drop => 3.5;

    protected override DemoFile Result { get; } = new("trip_transparent.png", "PNG", Color.Parse("#378ADD"), IsPhoto: true, Cutout: true);

    protected override DonutHit Target => new(DonutHitKind.Segment, _target);

    /// <summary>Rests in the hole while the donut opens and Tab turns it over; the way on is planned on the back face.</summary>
    protected override void PlanOnDonut()
    {
        if (!ViewModel.CanFlip)
            throw new InvalidOperationException("The image donut has no back face to flip to.");
        Path.Hold(2.3);
    }

    protected override void DraggedOver(double t, DonutHit hit)
    {
        // The global hook reports Tab during the drag; RadialController flips the donut (DonutMenu.Flip).
        if (Cues.Hit(Tab) && !Donut.Donut.Flip())
            throw new InvalidOperationException("The donut didn't flip.");
        if (_target >= 0 || !ViewModel.IsBackFace)
            return;
        // Turned over: from the hole to the slice on the back.
        _target = ViewModel.Presets.ToList().FindIndex(p => p.Id == TargetPreset);
        var donut = Donut.Donut;
        var inner = donut.OuterRadius * donut.HoleRatio;
        var onTarget = DonutGeometry.PointAt(Donut.Center, (inner + donut.OuterRadius) / 2 + 6,
            DonutGeometry.MidAngle(_target, donut.Items!.Count) - 0.04);
        Path.At(2.95, onTarget, 0.12).Hold(Drop);
        PlanAfterDrop();
    }

    protected override void DrawKeys(DrawingContext dc, double t)
    {
        // Ctrl starts the drag, then Tab flips the donut, in the same place.
        Painter.KeyCap(dc, Format.KeyCap, "Ctrl", Ease.Window(t, 0.42, 1.72, 0.14), Ease.Span(t, 0.5, 0.06) * (1 - Ease.Span(t, 1.5, 0.08)));
        Painter.KeyCap(dc, Format.KeyCap, "Tab", Ease.Window(t, 1.72, 3.0, 0.14), Ease.Span(t, Tab - 0.06, 0.06) * (1 - Ease.Span(t, Tab + 0.14, 0.08)));
    }
}
