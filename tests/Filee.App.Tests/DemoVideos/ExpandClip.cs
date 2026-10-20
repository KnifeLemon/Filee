// Clip "expand": on the open donut, resting on the "Formats" group fans out its ring, and trip.jpg is dropped on WEBP
// there; trip.webp appears beside it.

using Avalonia;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.ViewModels;

namespace Filee.App.Tests.DemoVideos;

internal sealed class ExpandClip(DemoFormat format, DemoPainter painter, RadialViewModel vm) : PicturesClip(format, painter, vm)
{
    /// <summary>The group slice (index in the demo donut, <see cref="DemoData"/>) and the slice of its ring dropped on.</summary>
    public const int Group = 2;
    public const int Sub = 2;

    private double _dwell = -1; // when the pointer came to rest on the group slice

    protected override double Drop => 3.55;

    protected override DemoFile Result { get; } = new("trip.webp", "WEBP", Color.Parse("#7F77DD"), IsPhoto: true);

    protected override DonutHit Target => new(DonutHitKind.SubSegment, Group, Sub);

    /// <summary>From the hole out to the group, on outward until its ring opens, round to WEBP.</summary>
    protected override void PlanOnDonut()
    {
        var donut = Donut.Donut;
        var center = Donut.Center;
        var mid = DonutGeometry.MidAngle(Group, donut.Items!.Count);
        var inner = donut.OuterRadius * donut.HoleRatio;
        var onGroup = DonutGeometry.PointAt(center, (inner + donut.OuterRadius) / 2 + 4, mid + 0.06);
        var outward = DonutGeometry.PointAt(center, donut.OuterRadius - 10, mid + 0.04);
        var target = Donut.ToDesktop(donut.SubSliceCenter(Group, Sub));
        Path.Hold(1.6)
            .At(2.1, onGroup, 0.12).Hold(2.25)
            .At(2.42, outward, 0.02).Hold(2.55)
            .At(3.1, target + new Point(2, 4), -0.18).Hold(Drop);
        PlanAfterDrop();
    }

    protected override void DraggedOver(double t, DonutHit hit)
    {
        // RadialWindow's group timer (DonutMenu.ExpandDelay) runs on the dispatcher, which isn't running here.
        if (hit is { Kind: DonutHitKind.Segment, Index: Group })
        {
            if (_dwell < 0)
                _dwell = t;
            else if (t - _dwell >= DonutMenu.ExpandDelay.TotalSeconds)
                Donut.Donut.ExpandPendingGroup();
        }
        else
        {
            _dwell = -1;
        }
    }
}
