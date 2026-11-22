// The marked frames' ticks on Cut a clip's timelines (#1205): a small accent mark in the ruler and a faint line through
// the pictures at each marked frame's time, drawn under the playhead, so it stays on top.

using Avalonia;
using Avalonia.Media;

namespace Filee.App.Controls;

internal static class MarkTicks
{
    /// <summary>
    /// A tick at each of <paramref name="times"/> that <paramref name="xOf"/> puts between <paramref name="left"/> and
    /// <paramref name="right"/>: a mark hanging from the ruler's top to <paramref name="ruler"/>, a line on to
    /// <paramref name="bottom"/>.
    /// </summary>
    public static void Draw(DrawingContext context, IReadOnlyList<double>? times, Func<double, double> xOf, double left, double right,
        double ruler, double bottom, IBrush accent)
    {
        if (times is not { Count: > 0 })
            return;
        var line = new Pen(accent, 1.5);
        var xs = times.Select(time => Math.Round(xOf(time)) + 0.5).Where(x => x >= left - 0.5 && x <= right + 0.5).ToList();
        using (context.PushOpacity(0.55))
            foreach (var x in xs)
                context.DrawLine(line, new Point(x, ruler), new Point(x, bottom));
        foreach (var x in xs)
        {
            var mark = new StreamGeometry();
            using (var g = mark.Open())
            {
                g.BeginFigure(new Point(x - 4.5, ruler - 9), true);
                g.LineTo(new Point(x + 4.5, ruler - 9));
                g.LineTo(new Point(x, ruler - 2));
                g.EndFigure(true);
            }
            context.DrawGeometry(accent, null, mark);
        }
    }
}
