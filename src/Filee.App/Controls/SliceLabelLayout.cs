// How a slice's label fits its slice: the safe zone it stays inside, its name wrapped at word breaks and drawn straight
// or along the slice, its icon, and the style one ring shows (Show). Nothing is drawn here: DonutMenu draws what this
// lays out, and the tests check the same layouts.
//
//   Safe zone      → the slice shrunk on all four sides (8 % of its depth, at least 6 px × the donut's scale, plus half
//                    the gap between slices); a group slice also keeps clear of its chevron.
//   One size       → every name on a ring is drawn at one size: 13 × Label scale × donut scale, stepped down 0.5 px at
//                    a time (to 11 px × donut scale, never under 10 px) until every name fits whole; names wrap at word
//                    breaks (up to 3 lines) and are never split, except Chinese and Japanese.
//   Direction      → one for the ring's names. Automatic: on deep slices (the donut's, mostly) straight, else out
//                    from the centre, else bent; on wide ones (a group ring's, or the donut's with a big hole) bent
//                    along the ring, else straight, else out from the centre; each way for every name of the ring. A
//                    preset can ask for one direction (Straight, Out from the centre, Along the ring), which its slice
//                    keeps. Scripts whose letters join or reorder (Arabic, Hebrew, the Indic scripts, Thai) aren't
//                    bent: level with the ring instead.
//   Show           → Auto / Icon & text / Text only / Icon only; one style for the ring's slices that have an icon.
//                    Auto: icon and name, else name, else icon, each only if every name shows whole. Icon & text: icon
//                    and name, a name shortened to 60 % of its letters at most, else the icon alone. No stub ("Tri…").
//   Captions       → only under a name shown alone, and then on every slice that has one or on none: an icon stands
//                    in for the format line.

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.App.Controls;

/// <summary>
/// One slice's outline in the donut's coordinates (angles clockwise from 12 o'clock, as in
/// <see cref="DonutGeometry"/>). A group's chevron, drawn on the slice's middle line by its outer edge:
/// <paramref name="ChevronIn"/> in from the outer edge to its middle, and <paramref name="ChevronReach"/> round that
/// (0 for none).
/// </summary>
internal readonly record struct SliceShape(Point Center, double Inner, double Outer, double Mid, double Span, double Gap,
    double ChevronIn = 0, double ChevronReach = 0);

/// <summary>A rectangle turned by <paramref name="Rotation"/> (radians, clockwise) about its centre.</summary>
internal readonly record struct LabelBox(Point Center, double Width, double Height, double Rotation)
{
    /// <summary>Its four corners, in order round it.</summary>
    internal Point[] Corners()
    {
        double c = Math.Cos(Rotation), s = Math.Sin(Rotation), w = Width / 2, h = Height / 2;
        var (x, y) = (Center.X, Center.Y);
        return [new(x - w * c + h * s, y - w * s - h * c), new(x + w * c + h * s, y + w * s - h * c),
            new(x + w * c - h * s, y + w * s + h * c), new(x - w * c - h * s, y - w * s + h * c)];
    }
}

/// <summary>
/// The part of a slice its label may use: between <paramref name="Inner"/> and <paramref name="Outer"/> from the
/// donut's centre, and at least <paramref name="Side"/> from both side edges.
/// </summary>
internal readonly record struct SafeZone(Point Center, double Inner, double Outer, double Mid, double HalfSpan, double Side,
    Point KeepOut = default, double KeepOutRadius = 0)
{
    /// <summary>
    /// The inset: this share of the slice's depth, and at least <see cref="MinMargin"/> px × the donut's scale. It is
    /// kept between the slice's edges and a label's letters (<see cref="LabelText.InkBox"/>), not the boxes of its
    /// lines, which hold room above and below them: about the room today's donut leaves round its names.
    /// </summary>
    internal const double MarginShare = 0.025, MinMargin = 2;

    internal static SafeZone Of(SliceShape shape, double scale)
    {
        var margin = Math.Max(MinMargin * scale, MarginShare * (shape.Outer - shape.Inner));
        // A group's chevron keeps the label clear of itself, by the margin kept from the slice's edges, not of a band
        // across the slice: the label stays centred on the slice and only steps round the mark.
        var chevron = shape.ChevronReach > 0 ? DonutGeometry.PointAt(shape.Center, shape.Outer - shape.ChevronIn, shape.Mid) : default;
        return new SafeZone(shape.Center, shape.Inner + margin, shape.Outer - margin, shape.Mid, shape.Span / 2, margin + shape.Gap / 2,
            chevron, shape.ChevronReach > 0 ? shape.ChevronReach + margin : 0);
    }

    internal double Depth => Outer - Inner;

    /// <summary>The point on the slice's middle line <paramref name="radius"/> from the donut's centre.</summary>
    internal Point At(double radius) => DonutGeometry.PointAt(Center, radius, Mid);

    internal bool Contains(Point p) => Contains(p, 0);

    /// <summary>
    /// How far out from the centre a bent line may reach on the slice's middle line: to <see cref="Outer"/>, or short
    /// of the chevron there is one.
    /// </summary>
    internal double OuterOnMiddle => KeepOutRadius > 0
        ? Math.Min(Outer, Math.Sqrt(Math.Pow(KeepOut.X - Center.X, 2) + Math.Pow(KeepOut.Y - Center.Y, 2)) - KeepOutRadius)
        : Outer;

    /// <summary>True if a circle of <paramref name="radius"/> round <paramref name="p"/> lies inside.</summary>
    internal bool Contains(Point p, double radius)
    {
        if (KeepOutRadius > 0 && Math.Sqrt(Math.Pow(p.X - KeepOut.X, 2) + Math.Pow(p.Y - KeepOut.Y, 2)) < KeepOutRadius + radius - 1e-9)
            return false;
        double dx = p.X - Center.X, dy = p.Y - Center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance - radius < Inner - 1e-9 || distance + radius > Outer + 1e-9)
            return false;
        if (HalfSpan >= Math.PI)
            return true; // one slice round the whole donut: no side edges
        var off = Math.Abs(Turn(Math.Atan2(dx, -dy) - Mid));
        // The distance to a side edge's line, as long as the point is on this side of it.
        return off < HalfSpan && distance * Math.Sin(Math.Min(HalfSpan - off, Math.PI / 2)) >= Side + radius - 1e-9;
    }

    /// <summary>
    /// True if all of <paramref name="box"/> lies inside. The outer circle and the side edges bound a convex area, so
    /// its corners tell; the inner circle curves in, so each of its sides must also pass outside that circle.
    /// </summary>
    internal bool Contains(LabelBox box)
    {
        var corners = box.Corners();
        if (!corners.All(Contains))
            return false;
        if (KeepOutRadius > 0 && DistanceToBox(KeepOut, box) < KeepOutRadius - 1e-9)
            return false;
        for (var i = 0; i < corners.Length; i++)
            if (DistanceTo(corners[i], corners[(i + 1) % corners.Length]) < Inner - 1e-9)
                return false;
        return true;
    }

    // The distance from p to the nearest point of a turned box (0 inside it).
    private static double DistanceToBox(Point p, LabelBox box)
    {
        double c = Math.Cos(box.Rotation), sn = Math.Sin(box.Rotation);
        double dx = p.X - box.Center.X, dy = p.Y - box.Center.Y;
        double along = dx * c + dy * sn, across = -dx * sn + dy * c; // into the box's own axes
        double ox = Math.Max(0, Math.Abs(along) - box.Width / 2), oy = Math.Max(0, Math.Abs(across) - box.Height / 2);
        return Math.Sqrt(ox * ox + oy * oy);
    }

    // The distance from the donut's centre to the nearest point of the segment from a to b.
    private double DistanceTo(Point a, Point b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = dx * dx + dy * dy;
        var t = length == 0 ? 0 : Math.Clamp(((Center.X - a.X) * dx + (Center.Y - a.Y) * dy) / length, 0, 1);
        var (x, y) = (a.X + t * dx - Center.X, a.Y + t * dy - Center.Y);
        return Math.Sqrt(x * x + y * y);
    }

    private static double Turn(double a)
    {
        a %= 2 * Math.PI;
        return a > Math.PI ? a - 2 * Math.PI : a < -Math.PI ? a + 2 * Math.PI : a;
    }
}

/// <summary>The fonts and reading direction labels are measured and drawn with.</summary>
internal sealed record LabelFonts(Typeface Bold, Typeface Regular, FlowDirection Direction);

/// <summary>
/// A slice's name (and caption), laid out: <see cref="Lines"/> at <see cref="Size"/>, centred on <see cref="Anchor"/>
/// and turned by <see cref="Rotation"/>. <see cref="Whole"/> is false for a name shortened with "…".
/// </summary>
internal sealed record LabelText(IReadOnlyList<string> Lines, IReadOnlyList<double> LineHeights, double Size,
    string? Caption, double CaptionSize, double CaptionHeight, double Width, double Height, Point Anchor, double Rotation,
    bool Whole, IReadOnlyList<double>? CurvedWidths = null, double InkTop = 0, double InkBottom = 0,
    IReadOnlyList<double>? RowWidths = null)
{
    /// <summary>
    /// Bent along the ring: each line on its own circle round the donut's centre, its letters placed one by one
    /// (<see cref="SliceLabelLayout.CurvedGlyphs"/>), each as wide as <see cref="CurvedWidths"/> says. Its middle is
    /// <see cref="Anchor"/>; <see cref="Rotation"/> isn't used.
    /// </summary>
    internal bool Curved => CurvedWidths is not null;

    /// <summary>Its outline, for straight text: its lines' boxes, room above and below the letters included.</summary>
    internal LabelBox Box => new(Anchor, Width, Height, Rotation);

    /// <summary>
    /// The outline of its letters, for straight text: <see cref="Box"/> less the room above its first line's letters
    /// (<see cref="InkTop"/>) and below its last line's (<see cref="InkBottom"/>), as the font reports them.
    /// </summary>
    internal LabelBox InkBox => InkBoxAt(Anchor, Width, Height, Rotation, InkTop, InkBottom);

    /// <summary>The outline of the letters of a block laid out round <paramref name="anchor"/>.</summary>
    internal static LabelBox InkBoxAt(Point anchor, double width, double height, double rotation, double inkTop, double inkBottom)
    {
        var shift = (inkTop - inkBottom) / 2;
        return new LabelBox(new Point(anchor.X - shift * Math.Sin(rotation), anchor.Y + shift * Math.Cos(rotation)), width,
            height - inkTop - inkBottom, rotation);
    }

    /// <summary>
    /// Where a block's lines go so that its letters' middle is <paramref name="letters"/>: the inverse of
    /// <see cref="InkBoxAt"/>.
    /// </summary>
    internal static Point AnchorFor(Point letters, double rotation, double inkTop, double inkBottom)
    {
        var shift = (inkTop - inkBottom) / 2;
        return new Point(letters.X + shift * Math.Sin(rotation), letters.Y - shift * Math.Cos(rotation));
    }

    /// <summary>
    /// True when all of it lies inside <paramref name="zone"/>: for straight text each line as wide as it is
    /// (<see cref="RowWidths"/>, its caption last), else the whole <see cref="InkBox"/>.
    /// </summary>
    internal bool Inside(SafeZone zone) =>
        Curved ? SliceLabelLayout.CurvedFits(zone, this)
        : RowWidths is null ? zone.Contains(InkBox)
        : StackInside(zone, InkBox.Center, Rotation, RowWidths, Caption is null ? LineHeights : [.. LineHeights, CaptionHeight], InkTop, InkBottom);

    /// <summary>
    /// True when a stack of centred rows, its letters' middle at <paramref name="letters"/>, lies inside
    /// <paramref name="zone"/>: each row as wide as its own text, the first less the room above its letters and the
    /// last less the room below. A wedge narrows towards the hole, and a short line there needs less of it than the
    /// widest line would.
    /// </summary>
    internal static bool StackInside(SafeZone zone, Point letters, double rotation, IReadOnlyList<double> widths,
        IReadOnlyList<double> heights, double inkTop, double inkBottom)
    {
        double sin = Math.Sin(rotation), cos = Math.Cos(rotation);
        var y = -(heights.Sum() - inkTop - inkBottom) / 2 - inkTop; // the first row's top, from the letters' middle
        for (var i = 0; i < heights.Count; i++)
        {
            var top = y + (i == 0 ? inkTop : 0);
            var bottom = y + heights[i] - (i == heights.Count - 1 ? inkBottom : 0);
            var middle = (top + bottom) / 2;
            var at = new Point(letters.X - middle * sin, letters.Y + middle * cos);
            if (!zone.Contains(new LabelBox(at, widths[i], bottom - top, rotation)))
                return false;
            y += heights[i];
        }
        return true;
    }
}

/// <summary>
/// Where an icon goes beside its name: above it as read, before it (side by side), or whichever fits.
/// </summary>
internal enum IconPlacement { Either, Above, Before }

/// <summary>What a slice shows: nothing, its name, its icon above its name, or its icon.</summary>
internal enum SliceLabelKind { None, Text, IconAndText, Icon }

/// <summary>One slice's laid-out label, ready to draw.</summary>
internal sealed record SliceLabel(SliceLabelKind Kind, LabelText? Text, string? IconKey, Point IconCenter, double IconSize,
    SafeZone Zone)
{
    internal static readonly SliceLabel Empty = new(SliceLabelKind.None, null, null, default, 0, default);

    /// <summary>
    /// The size its name was laid out at (for an icon alone, the smallest its ring tried), and the scale its icon was
    /// (Label scale × the donut's, less beside a stepped-down name); 0 for <see cref="Empty"/>.
    /// </summary>
    internal double NameSize { get; init; }

    /// <inheritdoc cref="NameSize"/>
    internal double Scale { get; init; }

    /// <summary>True when the slice's whole name is shown.</summary>
    internal bool NameWhole => Text?.Whole == true;

    /// <summary>
    /// How far round an icon's centre it is checked against the zone: the drawn part of a 24 × 24 icon.
    /// </summary>
    internal static double IconReach(double size) => size * IconReachShare;

    internal const double IconReachShare = 0.6;
}

/// <summary>
/// One slice to lay out: its name, caption, icon, Show and direction, and its outline. <paramref name="NeedsCaption"/>:
/// the caption tells the slice apart ("Copy to folder" or "Move to folder" under a folder's name, or the format under a
/// name another slice of the ring has too), so it shows with Show output format off, and beside an icon.
/// </summary>
internal sealed record SliceLabelInput(string Name, string? Caption, string? IconKey, LabelShow Show, SliceShape Shape,
    LabelDirection Direction = LabelDirection.Auto, bool NeedsCaption = false);

/// <summary>Lays out the labels of one ring of slices (the donut's face, or a group's ring).</summary>
internal sealed class SliceLabelLayout
{
    /// <summary>
    /// A name's size at Label scale 100 % on a donut of the default size, and the smallest it is drawn.
    /// </summary>
    internal const double NameSize = 13, MinNameSize = 10;

    /// <summary>
    /// How far a ring's names step down (× the donut's scale, in <see cref="SizeStep"/> steps) for every name to fit
    /// whole: smaller gets hard to read.
    /// </summary>
    internal const double MinStepNameSize = 10, SizeStep = 0.5;

    /// <summary>A caption's size as a share of the name's.</summary>
    internal const double CaptionShare = 0.8;

    /// <summary>The most lines a name wraps to.</summary>
    internal const int MaxLines = 3;

    /// <summary>
    /// The share of its letters a shortened name keeps at least (and it never drops a whole word): fewer ("Tri…" for
    /// "Trim transparent borders") say nothing.
    /// </summary>
    internal const double MinKeptShare = 0.6;

    /// <summary>
    /// An icon above a name, and the largest and smallest icon drawn alone, at 100 % and the default size.
    /// </summary>
    internal const double IconWithText = 20, IconAloneMax = 28, IconAloneMin = 16, IconStep = 2, IconGap = 3;

    /// <summary>The default donut radius, at which the donut's scale is 1.</summary>
    internal const double DefaultRadius = 150;

    // Where along a slice's middle line a label is tried: the zone's middle first, then further in and out.
    private static readonly double[] Offsets = [0, 0.1, -0.1, 0.2, -0.2, 0.3, -0.3, 0.4, -0.4];

    /// <summary>
    /// True when an upright label sits where a block is tried first: at its slice's middle height (with its icon, for
    /// an icon above its name), as much room left of it as right of it at that height. Text turned or bent along its
    /// slice, and an icon before its name, are placed along the middle line, so they count as there.
    /// </summary>
    internal static bool OnMiddle(SliceLabel label)
    {
        if (label.Text is not { Curved: false, Rotation: 0, RowWidths: not null } text)
            return true;
        var ink = text.InkBox;
        var (top, bottom) = (ink.Center.Y - ink.Height / 2, ink.Center.Y + ink.Height / 2);
        var icon = label.Kind == SliceLabelKind.IconAndText;
        // An icon before its name (side by side, out from the centre) is placed along the middle line, as turned text.
        if (icon && (Math.Abs(label.IconCenter.X - ink.Center.X) > 0.5 || label.IconCenter.Y > top))
            return true;
        if (icon)
        {
            top = Math.Min(top, label.IconCenter.Y - label.IconSize / 2);
            bottom = Math.Max(bottom, label.IconCenter.Y + label.IconSize / 2);
        }
        var middle = label.Zone.At((label.Zone.Inner + label.Zone.Outer) / 2);
        if (Math.Abs((top + bottom) / 2 - middle.Y) >= 0.5)
            return false;
        var zone = label.Zone;
        IReadOnlyList<double> heights = text.Caption is null ? text.LineHeights : [.. text.LineHeights, text.CaptionHeight];
        bool Fits(double dx) =>
            LabelText.StackInside(zone, new Point(ink.Center.X + dx, ink.Center.Y), 0, text.RowWidths, heights, text.InkTop, text.InkBottom) &&
            (!icon || IconFits(zone, new Point(label.IconCenter.X + dx, label.IconCenter.Y), label.IconSize));
        return Math.Abs(Room(Fits, -1) - Room(Fits, 1)) <= 1;
    }

    // An icon inside the zone: its drawn part, and its box clear of a chevron.
    private static bool IconFits(SafeZone zone, Point icon, double size) =>
        zone.Contains(icon, SliceLabel.IconReach(size)) && zone.Contains(new LabelBox(icon, size, size, 0));

    /// <summary>
    /// How far a block can move along the screen's x, in <paramref name="direction"/> (−1 left, +1 right), and still
    /// fit: to a quarter pixel, up to 200 px.
    /// </summary>
    private static double Room(Func<double, bool> fits, int direction)
    {
        double fit = 0, step = 1;
        while (fit + step <= 200 && fits(direction * (fit + step)))
        {
            fit += step;
            step *= 2;
        }
        for (step /= 2; step >= 0.25; step /= 2)
            if (fit + step <= 200 && fits(direction * (fit + step)))
                fit += step;
        return fit;
    }

    /// <summary>
    /// <paramref name="at"/> moved along the screen's x until a block there has as much room left of it as right of it:
    /// a slice that runs aslant has more room on one side of its middle than the other at a given height.
    /// </summary>
    private static Point Balanced(Point at, Func<double, bool> fits)
    {
        var shift = (Room(fits, 1) - Room(fits, -1)) / 2;
        return Math.Abs(shift) < 0.25 || !fits(shift) ? at : new Point(at.X + shift, at.Y);
    }

    /// <summary>
    /// How wide an upright line may be and still leave a slice room round it: the slice's width at its middle, less
    /// 10 px, between 30 and 120 px, as today's donut wraps its names. A longer line goes only where no wrapping of
    /// the name fits.
    /// </summary>
    internal static double ComfortableWidth(SafeZone zone) =>
        Math.Clamp(2 * zone.HalfSpan * (zone.Inner + zone.Outer) / 2 - 10, 30, 120);

    // How far round from the middle line an upright block is tried where it fits nowhere on it, as a share of the
    // zone's half span, either way: a three-slice face's slices run aslant, and a line centred on their middle line
    // meets the rim at one end and the hole at the other while the slice has room beside it.
    private static readonly double[] Sideways = [0.15, -0.15, 0.3, -0.3, 0.45, -0.45];

    /// <summary>
    /// Where a block's middle is tried in <paramref name="zone"/>: along its middle line first (<see cref="Offsets"/>),
    /// then round from it (<see cref="Sideways"/>) at each of those distances from the centre.
    /// </summary>
    private static IEnumerable<Point> Positions(SafeZone zone)
    {
        foreach (var offset in Offsets)
            yield return zone.At((zone.Inner + zone.Outer) / 2 + offset * zone.Depth);
        foreach (var across in Sideways)
            foreach (var offset in Offsets)
                yield return DonutGeometry.PointAt(zone.Center, (zone.Inner + zone.Outer) / 2 + offset * zone.Depth,
                    zone.Mid + across * zone.HalfSpan);
    }

    private readonly LabelFonts _fonts;
    private readonly Dictionary<(string Text, Typeface Face, double Size), (Size Size, double InkTop, double InkBottom, double InkWidth)> _sizes = new();
    private const int MaxCachedSizes = 2048;

    internal SliceLabelLayout(LabelFonts fonts) => _fonts = fonts;

    internal LabelFonts Fonts => _fonts;

    /// <summary>The size every name on a ring is drawn at.</summary>
    internal static double NameSizeFor(double labelScale, double scale) => Math.Max(MinNameSize, NameSize * labelScale * scale);

    internal static double CaptionSizeFor(double nameSize) => Math.Round(nameSize * CaptionShare * 2) / 2;

    /// <summary>
    /// The sizes a ring's names are tried at, largest first: <paramref name="nameSize"/>, then down in
    /// <see cref="SizeStep"/> steps to <see cref="MinStepNameSize"/> × <paramref name="scale"/> (never under
    /// <see cref="MinNameSize"/>). Just <paramref name="nameSize"/> when it is that small already.
    /// </summary>
    internal static IReadOnlyList<double> SizesFor(double nameSize, double scale)
    {
        var sizes = new List<double> { nameSize };
        var floor = Math.Max(MinNameSize, MinStepNameSize * scale);
        for (var size = Math.Ceiling(nameSize / SizeStep - 1) * SizeStep; size >= floor - 1e-9; size -= SizeStep)
            sizes.Add(size);
        return sizes;
    }

    /// <summary>
    /// The fewest text elements a shortened <paramref name="name"/> keeps: <see cref="MinKeptShare"/> of its letters
    /// (spaces not counted), and two letters into its last word at least, so no word is dropped whole.
    /// </summary>
    internal static int MinKept(string name)
    {
        var elements = TextElements(name.Trim());
        var need = Math.Max(1, (int)Math.Ceiling(MinKeptShare * elements.Count(e => !string.IsNullOrWhiteSpace(e))));
        var lastWord = elements.FindLastIndex(string.IsNullOrWhiteSpace) + 1;
        var words = lastWord > 0 ? Math.Min(elements.Count, lastWord + 2) : 0;
        var letters = 0;
        for (var i = 0; i < elements.Count; i++)
            if (!string.IsNullOrWhiteSpace(elements[i]) && ++letters >= need)
                return Math.Max(i + 1, words);
        return elements.Count;
    }

    /// <summary>
    /// <see cref="PlanRing"/> at <paramref name="labelScale"/> or, if a slice wouldn't show its whole name or its icon
    /// there, at the largest smaller Label scale (in <see cref="ToolbarProfile.LabelScaleStep"/> steps, down to 100 %)
    /// at which every slice does. For a group's ring, which has no room to grow (yet) with the labels.
    /// </summary>
    internal IReadOnlyList<SliceLabel> PlanRingToFit(IReadOnlyList<SliceLabelInput> slices, double labelScale, double scale,
        bool showCaptions)
    {
        var floor = Math.Min(labelScale, 1);
        for (var step = labelScale; ; step = Math.Round(step - ToolbarProfile.LabelScaleStep, 2))
        {
            var plan = PlanRing(slices, Math.Max(step, floor), scale, showCaptions, ring: true);
            if (step <= floor + 1e-9 || plan.Select((label, i) => Fits(label, slices[i])).All(f => f))
                return plan;
        }

        // Shows what it was asked to: its whole name, or its icon.
        static bool Fits(SliceLabel label, SliceLabelInput slice) =>
            (slice.Name.Length == 0 && slice.IconKey is null) || label.Kind == SliceLabelKind.Icon ||
            (label.Kind != SliceLabelKind.None && label.NameWhole);
    }

    /// <summary>
    /// The labels of one ring: one name size, one direction for its names, one icon size, and for each slice the best
    /// its Show allows there. The donut's face keeps its names upright; a group's ring (<paramref name="ring"/>) runs
    /// them the way its shape reads best (<see cref="LabelDirection"/>); a preset's own direction keeps its slice.
    /// Auto: icon above the whole name where every Auto slice of the ring has room for both and loses nothing for it
    /// (the caption it shows without its icon, its place on the middle), else the whole name alone, else shortened,
    /// else (last resort) the icon alone. Icon & text: icon above the whole name, else (a slice that needs its caption)
    /// the name and caption alone, else icon above the name shortened to <see cref="MinKept"/> (on as many lines as it
    /// takes), else the icon alone. Text only: the whole name, else shortened, else as far as it takes. Icon only: the
    /// icon. A slice without an icon goes as Text only. The ring takes the size and direction with the fewest last
    /// resorts, then the fewest needed captions lost, then the fewest names shortened past <see cref="MinKept"/>
    /// (keeping the most letters), then the earlier direction, then the largest size, then the fewest names shortened
    /// at all: on the face names keep their size and are shortened rather than made smaller (a group's ring, of short
    /// words, steps down before it shortens), and captions never make them smaller. Icon & text keeps its icons: where
    /// an icon and its name, whole or shortened to <see cref="MinKept"/>, don't fit, the ring steps down rather than
    /// cut the name to a word ("Merge…"), which says less. Captions show per slice, under a name where they fit (with
    /// Show output format on); a caption a slice needs (<see cref="SliceLabelInput.NeedsCaption"/>) shows with it off
    /// and beside an icon too. Slices of one name look alike. A block goes on its slice's middle first (moved sideways
    /// to as much room left of it as right), where it drops a caption it doesn't need before it moves; upright lines
    /// wrap at <see cref="ComfortableWidth"/> where that fits.
    /// </summary>
    internal IReadOnlyList<SliceLabel> PlanRing(IReadOnlyList<SliceLabelInput> slices, double labelScale, double scale,
        bool showCaptions, bool ring = false)
    {
        var baseSize = NameSizeFor(labelScale, scale);
        // A group's ring grows its names with the ring, but steps them down to the donut's own floor before shortening.
        var sizes = SizesFor(baseSize, ring ? Math.Min(scale, 1) : scale);
        var k = labelScale * scale;
        var zones = slices.Select(s => SafeZone.Of(s.Shape, scale)).ToArray();
        var wide = zones.Length > 0 && IsWide(zones[0]);
        // The directions the ring's names are tried in, one for all of them. The donut's face keeps them upright:
        // turned out from the centre they read sideways on its top and bottom slices, and bent they lose their
        // captions. A group's ring: deep, straight, out from the centre, then bent; wide, bent, straight, then out from
        // the centre.
        // Chinese, Japanese and Korean stay upright on a ring too: turned, their characters read sideways.
        var upright = slices.Any(s => s.Direction == LabelDirection.Auto && s.Name.Any(c => DonutMenu.IsHanOrKana(c) || c is >= '\uAC00' and <= '\uD7A3'));
        LabelDirection[] ways = !ring || upright ? [LabelDirection.Straight]
            : wide ? [LabelDirection.AlongRing, LabelDirection.Straight, LabelDirection.OutFromCentre]
            : [LabelDirection.Straight, LabelDirection.OutFromCentre, LabelDirection.AlongRing];
        // Bent along the ring only if every name of the ring can be (else all level with it).
        var ringBends = slices.Where(s => s.Direction == LabelDirection.Auto && s.Name.Length > 0).All(s => CanBend(s.Name));
        bool? BendsOf(int i) => slices[i].Direction == LabelDirection.Auto ? ringBends : null;
        LabelDirection DirectionOf(int i, LabelDirection way) => slices[i].Direction == LabelDirection.Auto ? way : slices[i].Direction;
        // An icon beside a smaller name shrinks with it, to no less than the smallest icon drawn alone.
        double IconFor(double size) => IconWithText * k * Math.Max(IconAloneMin / IconWithText, size / baseSize);
        string? CaptionOf(SliceLabelInput s) => !string.IsNullOrEmpty(s.Caption) &&
            !string.Equals(s.Caption, s.Name, StringComparison.OrdinalIgnoreCase) ? s.Caption : null;
        bool Needs(SliceLabelInput s) => s.NeedsCaption && CaptionOf(s) is not null;

        // The icon beside the name, whole or shortened, with its caption where it fits (shown, or needed: a needed one
        // has to).
        SliceLabel? Both(int i, double size, bool shorten, LabelDirection way, IconPlacement placement)
        {
            var s = slices[i];
            var icon = IconFor(size);
            // Its caption under the name where it fits too ("1つに" says what it does only with "ZIP" under it).
            var caption = Needs(s) || showCaptions ? CaptionOf(s) : null;
            var fit = FitIconAndText(zones[i], s.Name, caption, size, CaptionSizeFor(size), icon, k, shorten, DirectionOf(i, way),
                MinKept(s.Name), BendsOf(i), placement, captionNeeded: Needs(s));
            if (fit is not { } f || (Needs(s) && f.Text.Caption is null) || (shorten && f.Text.Whole))
                return null;
            return new SliceLabel(SliceLabelKind.IconAndText, f.Text, s.IconKey, f.Icon, icon, zones[i]);
        }
        // The whole name, with its caption where it fits (shown, or needed); with mustKeepNeeded, null if a needed one
        // doesn't fit.
        SliceLabel? Whole(int i, double size, LabelDirection way, bool mustKeepNeeded)
        {
            var s = slices[i];
            var caption = showCaptions || Needs(s) ? CaptionOf(s) : null;
            var block = FitText(zones[i], s.Name, caption, size, CaptionSizeFor(size), alongToo: true, DirectionOf(i, way), BendsOf(i),
                captionNeeded: Needs(s));
            if (block is null || (mustKeepNeeded && Needs(s) && block.Caption is null))
                return null;
            return new SliceLabel(SliceLabelKind.Text, block, null, default, 0, zones[i]);
        }
        SliceLabel? Shortened(int i, double size, LabelDirection way, int minKept) =>
            FitShortened(zones[i], slices[i].Name, size, alongToo: true, DirectionOf(i, way), minKept, BendsOf(i)) is { } block
                ? new SliceLabel(SliceLabelKind.Text, block, null, default, 0, zones[i])
                : null;

        // True when an icon would cost a slice what its name alone shows: a caption, or its place on the middle.
        bool IconCosts(int i, SliceLabel both, double size, LabelDirection way)
        {
            if (Whole(i, size, way, mustKeepNeeded: false) is not { } alone)
                return false;
            return (alone.Text!.Caption is not null && both.Text!.Caption is null) || (OnMiddle(alone) && !OnMiddle(both));
        }

        // A name shortened by a letter or two ("الفيديوها…" for "الفيديوهات"): that reads as a mistake, so the ring
        // steps down a little rather than cut so little.
        bool Nearly(int i, SliceLabel label) =>
            TextElements(slices[i].Name.Trim()).Count(e => !string.IsNullOrWhiteSpace(e)) -
            label.Text!.Lines.Sum(l => TextElements(l.TrimEnd('…')).Count(e => !string.IsNullOrWhiteSpace(e))) <= 2;

        // A name cut past MinKept that still reads (four letters and 40 % of them at least: "PDF zusam…") keeps the
        // face's size, as any shortened name does; only a cut shorter than that makes the ring step down. A long word
        // ("zusammenführen") would otherwise make every name of the face smaller.
        bool Severe(int i, SliceLabel label)
        {
            var letters = TextElements(slices[i].Name.Trim()).Count(e => !string.IsNullOrWhiteSpace(e));
            var kept = label.Text!.Lines.Sum(l => TextElements(l.TrimEnd('…')).Count(e => !string.IsNullOrWhiteSpace(e)));
            return ring || kept < 4 || kept < 0.4 * letters;
        }

        // What one slice shows at a size and direction: its label (null: its icon alone), and how far down its Show's
        // list it had to go.
        Pick PickFor(int i, double size, LabelDirection way, bool autoIcons, IconPlacement placement)
        {
            var s = slices[i];
            if (s.Name.Length == 0)
                return s.IconKey is null ? new Pick(SliceLabel.Empty) : new Pick(null);
            var show = s.IconKey is null && s.Show != LabelShow.TextOnly ? LabelShow.TextOnly : s.Show;
            switch (show)
            {
                case LabelShow.IconOnly:
                    return new Pick(null);
                case LabelShow.Auto:
                    if (autoIcons && Both(i, size, shorten: false, way, placement) is { } auto)
                        return new Pick(auto);
                    if (Whole(i, size, way, mustKeepNeeded: true) is { } alone)
                        return new Pick(alone);
                    if (Whole(i, size, way, mustKeepNeeded: false) is { } lost)
                        return new Pick(lost, NeededLost: true);
                    // Shortened rather than the icon alone among names: a lone icon there reads as a name gone missing.
                    if (Shortened(i, size, way, MinKept(s.Name)) is { } cut)
                        return new Pick(cut, Shortened: true, Nearly: Nearly(i, cut), NeededLost: Needs(s));
                    if (Shortened(i, size, way, 1) is { } stub)
                        return new Pick(stub, Shortened: true, Stub: Severe(i, stub), NeededLost: Needs(s));
                    return new Pick(null, LastResort: true, NeededLost: Needs(s));
                case LabelShow.IconAndText:
                    if (Both(i, size, shorten: false, way, placement) is { } both)
                        return new Pick(both);
                    if (Needs(s) && Whole(i, size, way, mustKeepNeeded: true) is { } told)
                        return new Pick(told, IconGivenUp: true);
                    if (Both(i, size, shorten: true, way, placement) is { } cutBoth)
                        return new Pick(cutBoth, Shortened: true, Nearly: Nearly(i, cutBoth), NeededLost: Needs(s));
                    return new Pick(null, LastResort: true, NeededLost: Needs(s));
                default:
                    if (Whole(i, size, way, mustKeepNeeded: true) is { } name)
                        return new Pick(name);
                    if (Whole(i, size, way, mustKeepNeeded: false) is { } bare)
                        return new Pick(bare, NeededLost: true);
                    if (Shortened(i, size, way, MinKept(s.Name)) is { } shortened)
                        return new Pick(shortened, Shortened: true, Nearly: Nearly(i, shortened), NeededLost: Needs(s));
                    // As far as it takes, a letter at least; else its icon, else nothing (the centre names it in full).
                    return Shortened(i, size, way, 1) is { } stubText
                        ? new Pick(stubText, Shortened: true, Stub: Severe(i, stubText), NeededLost: Needs(s))
                        : new Pick(s.IconKey is null ? SliceLabel.Empty : null, LastResort: true, NeededLost: Needs(s));
            }
        }

        Pick[]? best = null;
        (int, int, int, int, int, int, int, double, int, int, int, int) bestKey = default;
        (double Size, LabelDirection Way) at = default;
        // The labels at a size, way and icon placement.
        Pick[] PicksAt(double size, LabelDirection way, IconPlacement placement)
        {
            // Auto shows icons only where every Auto slice with an icon has room for its icon and name, and loses
            // nothing for it: the caption it shows without its icon, and its place on the slice's middle. A ring never
            // has some of its names with an icon and some without.
            var autoIcons = Enumerable.Range(0, slices.Count).All(i =>
                slices[i].Show != LabelShow.Auto || slices[i].IconKey is null || slices[i].Name.Length == 0 ||
                Both(i, size, shorten: false, way, placement) is { } both && !IconCosts(i, both, size, way));
            return Enumerable.Range(0, slices.Count).Select(i => PickFor(i, size, way, autoIcons, placement)).ToArray();
        }
        static int IconsOf(Pick[] picks) => picks.Count(p => p.Label?.Kind == SliceLabelKind.IconAndText);
        static (int, int, int, int, int) Worse(Pick[] picks) => (picks.Count(p => p.LastResort), picks.Count(p => p.NeededLost),
            picks.Count(p => p.Stub), picks.Count(p => p.Shortened), -IconsOf(picks));

        for (var w = 0; w < ways.Length; w++)
            foreach (var size in sizes)
            {
                // One placement for every icon of the ring: above its name (as read, as in every other mode), else
                // before it, side by side, where that labels the ring better (a group ring's shallow chips).
                var above = PicksAt(size, ways[w], IconPlacement.Above);
                var beside = PicksAt(size, ways[w], IconPlacement.Before);
                var picks = Worse(beside).CompareTo(Worse(above)) < 0 ? beside : above;
                var before = ReferenceEquals(picks, beside) && IconsOf(beside) > 0 ? 1 : 0;
                var captionsLost = showCaptions
                    ? picks.Where((p, i) => p.Label?.Text is { Caption: null } && CaptionOf(slices[i]) is not null).Count()
                    : 0;
                // Of names shortened past MinKept, the most letters kept.
                var stubLetters = picks.Where(p => p.Stub).Sum(p => p.Label?.Text?.Lines.Sum(l => l.TrimEnd('…').Length) ?? 0);
                // On the face, names keep their size and are shortened to MinKept rather than made smaller (smaller
                // text is harder to read, and the centre names the slice in full); they step down only to keep more
                // than MinKept. A group's ring holds short words ("Left", "16:9"), which a "…" ruins: it steps down
                // first. The face steps down too to put its icons above their names rather than before them; a group's
                // ring, its chips too shallow for that at most sizes, keeps its size.
                var shortened = picks.Count(p => p.Shortened);
                var key = (picks.Count(p => p.LastResort), picks.Count(p => p.NeededLost), picks.Count(p => p.Stub || p.Nearly), -stubLetters,
                    ring ? shortened : 0, ring ? 0 : before, w, -size, ring ? 0 : shortened, picks.Count(p => p.IconGivenUp), captionsLost,
                    ring ? before : 0);
                if (best is null || key.CompareTo(bestKey) < 0)
                    (best, bestKey, at) = (picks, key, (size, ways[w]));
            }

        var result = (best ?? []).Select(p => p.Label).ToArray();
        // Slices of one name look alike: where one of them shows its name alone, the others do too ("Rotate left" as
        // Same format and as PDF, or a Copy and a Move slice for one folder).
        foreach (var same in Enumerable.Range(0, slices.Count)
                     .Where(i => slices[i].Name.Length > 0 && slices[i].IconKey is not null)
                     .GroupBy(i => (slices[i].Name, slices[i].Show), NameComparer).Where(g => g.Count() > 1))
            if (same.Any(i => result[i]?.Kind == SliceLabelKind.Text))
                foreach (var i in same.Where(i => result[i]?.Kind == SliceLabelKind.IconAndText))
                    result[i] = Whole(i, at.Size, at.Way, mustKeepNeeded: true) ?? Whole(i, at.Size, at.Way, mustKeepNeeded: false) ?? result[i];

        // Icons alone: one size for the ring, the largest every one of them fits at, on its slice's middle line: in the
        // middle, or (a group's, clear of its chevron) as near it as it fits, half a pixel at a time.
        var alone = Enumerable.Range(0, slices.Count).Where(i => result[i] is null && slices[i].IconKey is not null).ToList();
        Point? IconPlace(int i, double icon)
        {
            var zone = zones[i];
            var middle = (zone.Inner + zone.Outer) / 2;
            for (var d = 0.0; d <= zone.Depth / 2; d += 0.5)
                foreach (var r in d == 0 ? [middle] : new[] { middle - d, middle + d })
                    if (zone.Contains(zone.At(r), SliceLabel.IconReach(icon)))
                        return zone.At(r);
            return null;
        }
        if (alone.Count > 0)
        {
            bool FitsAll(double icon) => alone.All(i => IconPlace(i, icon) is not null);
            // Next to icons drawn beside names, the same size as those where they fit: one icon size for the ring.
            var withName = result.Where(r => r?.Kind == SliceLabelKind.IconAndText).Select(r => r!.IconSize).DefaultIfEmpty(0).Max();
            var size = withName > 0 && FitsAll(withName) ? withName : 0.0;
            for (var candidate = IconAloneMax; size == 0 && candidate >= IconAloneMin - 1e-9; candidate -= IconStep)
                if ((withName == 0 || candidate * k <= withName) && FitsAll(candidate * k))
                    size = candidate * k;
            foreach (var i in alone)
                result[i] = size > 0
                    ? new SliceLabel(SliceLabelKind.Icon, null, slices[i].IconKey, IconPlace(i, size)!.Value, size, zones[i])
                    : Shortened(i, sizes[^1], ways[0], 0) ?? SliceLabel.Empty;
        }
        // The size each label's name was laid out at (for icons alone, the ring's), and the scale of its icon.
        return result.Select((r, i) =>
        {
            var size = r?.Text?.Size ?? at.Size;
            if (size <= 0)
                size = sizes[^1];
            return (r ?? SliceLabel.Empty with { Zone = zones[i] }) with { NameSize = size, Scale = IconFor(size) / IconWithText };
        }).ToArray();
    }

    // One slice's choice at a size and direction: its label (null: its icon alone, sized with the ring's).
    private readonly record struct Pick(SliceLabel? Label, bool LastResort = false, bool NeededLost = false, bool Nearly = false,
        bool Shortened = false, bool Stub = false, bool IconGivenUp = false);

    private static readonly IEqualityComparer<(string Name, LabelShow Show)> NameComparer =
        EqualityComparer<(string, LabelShow)>.Create(
            (a, b) => a.Item2 == b.Item2 && string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase),
            k => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), k.Item2));

    /// <summary>
    /// The name whole (with its caption under it when both fit), in as few lines as it takes, in the directions
    /// <paramref name="direction"/> allows (<see cref="Tries"/>; only straight without <paramref name="alongToo"/>).
    /// On the slice's middle first: there the name drops a caption it doesn't need before it moves off the middle
    /// (<paramref name="captionNeeded"/>: one that tells the slice apart moves with it instead). Null if it doesn't
    /// fit whole.
    /// </summary>
    internal LabelText? FitText(SafeZone zone, string name, string? caption, double size, double captionSize, bool alongToo,
        LabelDirection direction = LabelDirection.Auto, bool? bends = null, bool captionNeeded = false)
    {
        var units = Units(name);
        if (units.Count == 0)
            return null;
        var comfortable = ComfortableWidth(zone);
        foreach (var turns in Tries(zone, name, alongToo, direction, bends))
            foreach (var middle in new[] { true, false })
                foreach (var withCaption in caption is null ? new[] { false } : new[] { true, false })
                {
                    if (middle && !withCaption && captionNeeded && caption is not null)
                        continue;
                    // Upright, its lines no wider than the slice leaves them room for comfortably, wrapped as today's
                    // donut wraps them, where that fits; else as few lines as fit.
                    foreach (var comfort in new[] { true, false })
                        for (var lines = 1; lines <= Math.Min(MaxLines, units.Count); lines++)
                        {
                            var text = Split(units, lines, size);
                            var wide = text.Any(l => SizeOf(l, _fonts.Bold, size).Width > comfortable);
                            foreach (var turn in turns)
                            {
                                // Upright: the comfortable wrappings first, then the wide ones. Turned: once.
                                if (turn == 0 ? comfort == wide : !comfort)
                                    continue;
                                if (Place(zone, text, size, withCaption ? caption : null, captionSize, turn, whole: true, middle) is { } block)
                                    return block;
                            }
                        }
                }
        return null;
    }

    /// <summary>
    /// The name shortened with "…" until it fits, keeping <paramref name="minKept"/> text elements at least, on as few
    /// lines as it takes (up to <see cref="MaxLines"/>, wrapped at word breaks as a whole name is: "Remove /
    /// backgro…"), in whichever direction keeps the most of it. Null if it doesn't fit even then.
    /// </summary>
    internal LabelText? FitShortened(SafeZone zone, string name, double size, bool alongToo,
        LabelDirection direction = LabelDirection.Auto, int minKept = 0, bool? bends = null)
    {
        var turns = Tries(zone, name, alongToo, direction, bends).SelectMany(t => t).ToArray();
        var elements = TextElements(name.Trim());
        for (var keep = elements.Count - 1; keep >= minKept; keep--)
        {
            var units = Units(string.Concat(elements.Take(keep)).TrimEnd() + "…");
            for (var lines = 1; lines <= Math.Min(MaxLines, units.Count); lines++)
            {
                var text = Split(units, lines, size);
                foreach (var turn in turns)
                    if (Place(zone, text, size, null, 0, turn, whole: false) is { } block)
                        return block;
            }
        }
        return null;
    }

    /// <summary>
    /// The icon above the name, both upright, placed along the slice's middle line; the name shortened (keeping
    /// <paramref name="minKept"/> text elements at least, on as few lines as it takes) if <paramref name="shorten"/>
    /// and it doesn't fit whole. Null if they don't fit.
    /// </summary>
    internal (LabelText Text, Point Icon)? FitIconAndText(SafeZone zone, string name, string? caption, double size,
        double captionSize, double iconSize, double scale, bool shorten, LabelDirection direction = LabelDirection.Auto,
        int minKept = 1, bool? bends = null, IconPlacement placement = IconPlacement.Either, bool captionNeeded = false)
    {
        var units = Units(name);
        if (units.Count == 0)
            return null;
        var gap = IconGap * scale;
        var options = new List<(IReadOnlyList<string> Lines, string? Caption, bool Whole)>();
        foreach (var withCaption in caption is null ? new[] { false } : new[] { true, false })
            for (var lines = 1; lines <= Math.Min(MaxLines, units.Count); lines++)
                options.Add((Split(units, lines, size), withCaption ? caption : null, true));
        // Shortened, on as few lines as it takes, as a name alone is ("Remove / metad…" rather than "Remov…").
        if (shorten)
        {
            var elements = TextElements(name.Trim());
            for (var keep = elements.Count - 1; keep >= Math.Max(1, minKept); keep--)
            {
                var cut = Units(string.Concat(elements.Take(keep)).TrimEnd() + "…");
                for (var lines = 1; lines <= Math.Min(MaxLines, cut.Count); lines++)
                    options.Add((Split(cut, lines, size), null, false));
            }
        }
        // On a wide slice an upright stack only fits where the ring runs up and down: the name bends along the ring
        // under the upright icon instead (above it as read, as in every other mode), or beside it where the slice is
        // too shallow for one above the other (a group ring's chip).
        var bent = direction is LabelDirection.Auto or LabelDirection.AlongRing && IsWide(zone) && (bends ?? CanBend(name));
        if (bent)
        {
            foreach (var (lines, cap, whole) in options)
            {
                var (_, heights, _) = Measure(lines, size, cap, captionSize);
                if (cap is null && ((placement != IconPlacement.Before ? FitIconOverBent(zone, lines, heights, size, captionSize, iconSize, gap, whole) : null)
                                    ?? (placement != IconPlacement.Above ? FitIconBesideBent(zone, lines, heights, size, captionSize, iconSize, gap, whole) : null)) is { } fit)
                    return fit;
            }
            return null;
        }
        // A deep slice: the icon above its name, both upright; where that can't show the name whole, the icon before a
        // name running out from the centre (the only way for Out from the centre). Whole names before shortened ones.
        var above = direction != LabelDirection.OutFromCentre && placement != IconPlacement.Before;
        var before = direction is LabelDirection.Auto or LabelDirection.OutFromCentre && placement != IconPlacement.Above;
        // On the slice's middle first, where a caption it doesn't need goes before the block moves off the middle.
        foreach (var whole in new[] { true, false })
            foreach (var middle in new[] { true, false })
                foreach (var beside in new[] { false, true })
                {
                    if (beside ? !before : !above)
                        continue;
                    // Upright under its icon, the name wrapped comfortably first (ComfortableWidth).
                    var comfortable = ComfortableWidth(zone);
                    foreach (var (lines, cap, isWhole) in options.Where(o => o.Whole == whole)
                                 .OrderBy(o => !beside && o.Lines.Any(l => SizeOf(l, _fonts.Bold, size).Width > comfortable)))
                    {
                        if (middle && cap is null && captionNeeded && caption is not null)
                            continue;
                        var (textWidth, heights, captionHeight) = Measure(lines, size, cap, captionSize);
                        var (inkTop, inkBottom) = Ink(lines, size, cap, captionSize);
                        var rows = RowWidthsOf(lines, size, cap, captionSize);
                        if ((beside
                                ? FitIconBeforeAlong(zone, lines, heights, textWidth, cap, captionSize, captionHeight, size, iconSize, gap, isWhole, inkTop, inkBottom, rows, middle)
                                : FitIconAbove(zone, lines, heights, textWidth, cap, captionSize, captionHeight, size, iconSize, gap, isWhole, inkTop, inkBottom, rows, middle))
                            is { } fit)
                            return fit;
                    }
                }
        return null;
    }

    // The icon above its name, both upright, on the slice's middle line: the block from the icon's top to the last
    // line's letters centred there.
    private static (LabelText Text, Point Icon)? FitIconAbove(SafeZone zone, IReadOnlyList<string> lines, double[] heights,
        double textWidth, string? cap, double captionSize, double captionHeight, double size, double iconSize, double gap, bool whole,
        double inkTop, double inkBottom, IReadOnlyList<double> rows, bool middleOnly)
    {
        var textHeight = heights.Sum() + captionHeight;
        var height = iconSize + gap + textHeight - inkBottom;
        IReadOnlyList<double> rowHeights = cap is null ? heights : [.. heights, captionHeight];
        foreach (var at in middleOnly ? Positions(zone).Take(1) : Positions(zone))
        {
            var icon = new Point(at.X, at.Y - height / 2 + iconSize / 2);
            var textAt = new Point(at.X, at.Y - height / 2 + iconSize + gap + textHeight / 2);
            var letters = LabelText.InkBoxAt(textAt, textWidth, textHeight, 0, inkTop, inkBottom).Center;
            bool Fits(double dx) =>
                IconFits(zone, new Point(icon.X + dx, icon.Y), iconSize) &&
                LabelText.StackInside(zone, new Point(letters.X + dx, letters.Y), 0, rows, rowHeights, inkTop, inkBottom);
            if (!Fits(0))
                continue;
            // Moved sideways to have as much room left of it as right of it.
            var shift = Balanced(default, Fits).X;
            return (new LabelText(lines, heights, size, cap, captionSize, captionHeight, textWidth, textHeight,
                new Point(textAt.X + shift, textAt.Y), 0, whole, InkTop: inkTop, InkBottom: inkBottom, RowWidths: rows),
                new Point(icon.X + shift, icon.Y));
        }
        return null;
    }

    /// <summary>
    /// The upright icon before a name running out from the centre, both on the slice's middle line: first as read, so
    /// the inner of the two on the right half (read outward) and the outer on the left half (read inward).
    /// </summary>
    private static (LabelText Text, Point Icon)? FitIconBeforeAlong(SafeZone zone, IReadOnlyList<string> lines, double[] heights,
        double textWidth, string? cap, double captionSize, double captionHeight, double size, double iconSize, double gap, bool whole,
        double inkTop, double inkBottom, IReadOnlyList<double> rows, bool middleOnly)
    {
        var textHeight = heights.Sum() + captionHeight;
        IReadOnlyList<double> rowHeights = cap is null ? heights : [.. heights, captionHeight];
        var turn = Along(zone.Mid);
        var a = (zone.Mid % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        var outward = a < Math.PI ? 1 : -1; // which way the name reads along the middle line
        var total = iconSize + gap + textWidth;
        foreach (var offset in middleOnly ? Offsets.Take(1) : Offsets)
        {
            var middle = (zone.Inner + zone.Outer) / 2 + offset * zone.Depth;
            var icon = zone.At(middle - outward * (total / 2 - iconSize / 2));
            var letters = zone.At(middle + outward * (total / 2 - textWidth / 2));
            if (zone.Contains(icon, SliceLabel.IconReach(iconSize)) &&
                LabelText.StackInside(zone, letters, turn, rows, rowHeights, inkTop, inkBottom))
                return (new LabelText(lines, heights, size, cap, captionSize, captionHeight, textWidth, textHeight,
                    LabelText.AnchorFor(letters, turn, inkTop, inkBottom), turn, whole, InkTop: inkTop, InkBottom: inkBottom,
                    RowWidths: rows), icon);
        }
        return null;
    }

    /// <summary>
    /// The upright icon before a name bent along the ring, side by side along it (first as read: clockwise on the top
    /// half, anticlockwise on the bottom), where the slice is too shallow for one above the other (a group ring's chip,
    /// at most sizes). Null if they don't fit.
    /// </summary>
    private (LabelText Text, Point Icon)? FitIconBesideBent(SafeZone zone, IReadOnlyList<string> lines, double[] heights,
        double size, double captionSize, double iconSize, double gap, bool whole)
    {
        var widths = lines.Select(l => CurvedWidth(l, size)).ToArray();
        var textHeight = heights.Sum();
        var textWidth = widths.Max();
        var total = iconSize + gap + textWidth;
        var step = Bottom(zone.Mid) ? -1 : 1;
        foreach (var offset in Offsets)
        {
            var middle = (zone.Inner + zone.Outer) / 2 + offset * zone.Depth;
            var icon = DonutGeometry.PointAt(zone.Center, middle, zone.Mid - step * (total / 2 - iconSize / 2) / middle);
            var anchor = DonutGeometry.PointAt(zone.Center, middle, zone.Mid + step * (total / 2 - textWidth / 2) / middle);
            var text = new LabelText(lines, heights, size, null, captionSize, 0, textWidth, textHeight, anchor, 0, whole, widths);
            if (zone.Contains(icon, SliceLabel.IconReach(iconSize)) && CurvedFits(zone, text))
                return (text, icon);
        }
        return null;
    }

    /// <summary>
    /// The upright icon above a name bent along the ring: above as read, so the icon is the outer of the two on the top
    /// half and the inner on the bottom half. Null if they don't fit.
    /// </summary>
    private (LabelText Text, Point Icon)? FitIconOverBent(SafeZone zone, IReadOnlyList<string> lines, double[] heights,
        double size, double captionSize, double iconSize, double gap, bool whole)
    {
        var widths = lines.Select(l => CurvedWidth(l, size)).ToArray();
        var textHeight = heights.Sum();
        var height = iconSize + gap + textHeight;
        var up = Bottom(zone.Mid) ? -1 : 1; // towards the top of the label, as read
        foreach (var offset in Offsets)
        {
            var middle = (zone.Inner + zone.Outer) / 2 + offset * zone.Depth;
            var iconRadius = middle + up * (height / 2 - iconSize / 2);
            var textMiddle = middle - up * (height / 2 - textHeight / 2);
            var icon = DonutGeometry.PointAt(zone.Center, iconRadius, zone.Mid);
            if (zone.Contains(icon, SliceLabel.IconReach(iconSize)) && CurvedFits(zone, textMiddle, heights, textHeight, widths))
                return (new LabelText(lines, heights, size, null, captionSize, 0, widths.Max(), textHeight, zone.At(textMiddle), 0,
                    whole, widths), icon);
        }
        return null;
    }

    // The turns a name is tried in: straight, along the slice, or straight then along.

    /// <summary>
    /// The turn that runs text along a slice whose middle is at <paramref name="mid"/>: outward on the right half,
    /// inward on the left half, so it is never upside down.
    /// </summary>
    internal static double Along(double mid)
    {
        var a = (mid % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        return a < Math.PI ? mid - Math.PI / 2 : mid + Math.PI / 2;
    }

    /// <summary>
    /// The turn that runs text level with the ring, across a slice whose middle is at <paramref name="mid"/>: at right
    /// angles to its middle line, turned round on the bottom half so it is never upside down.
    /// </summary>
    internal static double Across(double mid)
    {
        var a = (mid % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        return a > Math.PI / 2 && a < 3 * Math.PI / 2 ? mid - Math.PI : mid;
    }

    /// <summary>The turn that stands for text bent along the ring (<see cref="LabelText.Curved"/>).</summary>
    internal const double CurvedTurn = double.NaN;

    /// <summary>
    /// True for a zone wider (along its middle) than it is deep: a group ring's slice, or the donut's with a big hole.
    /// The ring's size, its spread and the name size decide.
    /// </summary>
    internal static bool IsWide(SafeZone zone) =>
        2 * zone.HalfSpan * (zone.Inner + zone.Outer) / 2 - 2 * zone.Side > zone.Depth;

    /// <summary>
    /// The turns a name is tried in, in groups: every line count of one group before the next. Automatic: straight or
    /// out from the centre on a deep zone, then bent; bent or straight on a wide one, then out from the centre. A
    /// preset's own direction is the only one tried.
    /// </summary>
    internal double[][] Tries(SafeZone zone, string name, bool alongToo, LabelDirection direction, bool? bends = null)
    {
        if (!alongToo)
            return [[0]];
        var along = Along(zone.Mid);
        // Letters that join or reorder can't be placed one by one: level with the ring instead.
        var bent = bends ?? CanBend(name) ? CurvedTurn : Across(zone.Mid);
        return direction switch
        {
            LabelDirection.Straight => [[0]],
            LabelDirection.OutFromCentre => [[along]],
            LabelDirection.AlongRing => [[bent]],
            _ => IsWide(zone) ? [[bent, 0], [along]] : [[0, along], [bent]],
        };
    }

    /// <summary>
    /// True when <paramref name="name"/> can be bent letter by letter: read left to right, in a script whose letters
    /// neither join nor reorder (not Arabic, Hebrew, the Indic scripts, Thai, Lao, Myanmar).
    /// </summary>
    internal bool CanBend(string name) => _fonts.Direction == FlowDirection.LeftToRight &&
        !name.Any(c => c is >= '\u0590' and <= '\u08FF' or >= '\u0900' and <= '\u0EFF' or >= '\u1000' and <= '\u109F'
            or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF');

    /// <summary>
    /// Where each line of a bent name sits: the radius of its middle. On the top half the first line is the outermost;
    /// on the bottom half, read the other way up, the innermost.
    /// </summary>
    private static double LineRadius(SafeZone zone, double middle, IReadOnlyList<double> heights, double height, int line)
    {
        var before = heights.Take(line).Sum();
        return Bottom(zone.Mid) ? middle - height / 2 + before + heights[line] / 2 : middle + height / 2 - before - heights[line] / 2;
    }

    /// <summary>
    /// True on the bottom half of the donut, where bent text runs the other way round to stay upright.
    /// </summary>
    internal static bool Bottom(double mid)
    {
        var a = (mid % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        return a > Math.PI / 2 && a < 3 * Math.PI / 2;
    }

    /// <summary>
    /// True when a bent name lies inside <paramref name="zone"/>: between its inner and outer circles, and each line's
    /// ends (at the line's inner edge, where the sides are closest) clear of the side edges.
    /// </summary>
    internal static bool CurvedFits(SafeZone zone, LabelText text) =>
        CurvedFits(zone, Distance(zone.Center, text.Anchor), text.LineHeights, text.Height, text.CurvedWidths!, Shift(zone, text));

    /// <summary>
    /// How far round from the zone's middle a bent name is centred (beside an icon): its anchor's angle.
    /// </summary>
    private static double Shift(SafeZone zone, LabelText text) =>
        Math.IEEERemainder(DonutGeometry.AngleOf(zone.Center, text.Anchor) - zone.Mid, 2 * Math.PI);

    private static bool CurvedFits(SafeZone zone, double middle, IReadOnlyList<double> heights, double height,
        IReadOnlyList<double> widths, double shift = 0)
    {
        if (middle - height / 2 < zone.Inner - 1e-9 || middle + height / 2 > zone.OuterOnMiddle + 1e-9)
            return false;
        for (var i = 0; i < heights.Count; i++)
        {
            var radius = LineRadius(zone, middle, heights, height, i);
            var reach = widths[i] / radius / 2;
            if (zone.HalfSpan >= Math.PI)
            {
                if (reach > Math.PI)
                    return false;
                continue;
            }
            var room = zone.HalfSpan - Math.Abs(shift) - reach;
            if (room <= 0 || (radius - heights[i] / 2) * Math.Sin(Math.Min(room, Math.PI / 2)) < zone.Side - 1e-9)
                return false;
        }
        return true;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// The letters of a bent name as drawn: each text element, the point its middle sits on, its turn (upright to the
    /// reader) and its advance, line by line round the donut's centre.
    /// </summary>
    internal IEnumerable<(string Text, Point At, double Rotation, double Advance, int Line)> CurvedGlyphs(SafeZone zone, LabelText text)
    {
        var middle = Distance(zone.Center, text.Anchor);
        var bottom = Bottom(zone.Mid);
        var centre = zone.Mid + Shift(zone, text);
        for (var i = 0; i < text.Lines.Count; i++)
        {
            var radius = LineRadius(zone, middle, text.LineHeights, text.Height, i);
            var step = bottom ? -1 : 1;
            var angle = centre - step * text.CurvedWidths![i] / radius / 2;
            foreach (var element in TextElements(text.Lines[i]))
            {
                var advance = AdvanceOf(element, text.Size);
                angle += step * advance / radius / 2;
                yield return (element, DonutGeometry.PointAt(zone.Center, radius, angle), bottom ? angle + Math.PI : angle, advance, i);
                angle += step * advance / radius / 2;
            }
        }
    }

    // How far a letter moves the pen: what bent text is laid out with, spaces included.
    private readonly Dictionary<(string Text, double Size), double> _advances = new();

    private double AdvanceOf(string element, double size)
    {
        if (_advances.TryGetValue((element, size), out var known))
            return known;
        if (_advances.Count >= MaxCachedSizes)
            _advances.Clear();
        var measured = new FormattedText(element, CultureInfo.CurrentUICulture, _fonts.Direction, _fonts.Bold, size, null);
        return _advances[(element, size)] = measured.WidthIncludingTrailingWhitespace;
    }

    private double CurvedWidth(string line, double size) => TextElements(line).Sum(e => AdvanceOf(e, size));

    private LabelText? Place(SafeZone zone, IReadOnlyList<string> lines, double size, string? caption, double captionSize,
        double turn, bool whole, bool middleOnly = false)
    {
        var (width, heights, captionHeight) = Measure(lines, size, caption, captionSize);
        var height = heights.Sum() + captionHeight;
        if (double.IsNaN(turn))
        {
            // Bent: no caption (it would need its own circle), each line as wide as its letters' advances.
            if (caption is not null)
                return null;
            var widths = lines.Select(l => CurvedWidth(l, size)).ToArray();
            foreach (var offset in Offsets)
            {
                var middle = (zone.Inner + zone.Outer) / 2 + offset * zone.Depth;
                if (CurvedFits(zone, middle, heights, height, widths))
                    return new LabelText(lines, heights, size, null, captionSize, 0, widths.Max(), height, zone.At(middle), 0, whole, widths);
            }
            return null;
        }
        // Its letters' middle on each place tried, the slice's middle first.
        var (inkTop, inkBottom) = Ink(lines, size, caption, captionSize);
        var rows = RowWidthsOf(lines, size, caption, captionSize);
        IReadOnlyList<double> rowHeights = caption is null ? heights : [.. heights, captionHeight];
        var places = turn == 0 ? Positions(zone) : Offsets.Select(o => zone.At((zone.Inner + zone.Outer) / 2 + o * zone.Depth));
        foreach (var place in middleOnly ? places.Take(1) : places)
        {
            if (!LabelText.StackInside(zone, place, turn, rows, rowHeights, inkTop, inkBottom))
                continue;
            // Upright, it moves sideways to have as much room left of it as right of it.
            var at = turn != 0 ? place
                : Balanced(place, dx => LabelText.StackInside(zone, new Point(place.X + dx, place.Y), 0, rows, rowHeights, inkTop, inkBottom));
            return new LabelText(lines, heights, size, caption, captionSize, captionHeight, width, height,
                LabelText.AnchorFor(at, turn, inkTop, inkBottom), turn, whole, InkTop: inkTop, InkBottom: inkBottom, RowWidths: rows);
        }
        return null;
    }

    private (double Width, double[] Heights, double CaptionHeight) Measure(IReadOnlyList<string> lines, double size,
        string? caption, double captionSize)
    {
        var width = 0.0;
        var heights = new double[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = SizeOf(lines[i], _fonts.Bold, size);
            width = Math.Max(width, line.Width);
            heights[i] = line.Height;
        }
        var captionHeight = 0.0;
        if (caption is not null)
        {
            var c = SizeOf(caption, _fonts.Regular, captionSize);
            width = Math.Max(width, c.Width);
            captionHeight = c.Height;
        }
        return (width, heights, captionHeight);
    }

    /// <summary>A line of text as drawn: one line, never wrapped, centred.</summary>
    internal static FormattedText MakeLine(string text, Typeface face, double size, IBrush? brush, FlowDirection direction,
        double width) =>
        new(DonutMenu.KeepKoreanWords(text), CultureInfo.CurrentUICulture, direction, face, size, brush)
        {
            // A pixel or two to spare: a line exactly as wide as its box would wrap, and the second line be dropped.
            MaxTextWidth = width + 2,
            MaxLineCount = 1,
            TextAlignment = TextAlignment.Center,
        };

    private Size SizeOf(string text, Typeface face, double size) => Measured(text, face, size).Size;

    // A line's box; the room in it above and below its letters; and how wide its letters reach, centred as the line
    // is drawn (a "J" reaches out of its box). From the letters' outlines, which match the drawn letters to half a
    // pixel; the font's own extent leaves a pixel or two more.
    private (Size Size, double InkTop, double InkBottom, double InkWidth) Measured(string text, Typeface face, double size)
    {
        var key = (text, face, size);
        if (_sizes.TryGetValue(key, out var known))
            return known;
        if (_sizes.Count >= MaxCachedSizes)
            _sizes.Clear();
        // A brush, or the text has no outlines to build.
        var measured = new FormattedText(DonutMenu.KeepKoreanWords(text), CultureInfo.CurrentUICulture, _fonts.Direction, face, size, Brushes.Black);
        var box = new Size(measured.Width, measured.Height);
        if (measured.BuildGeometry(new Point(0, 0))?.Bounds is not { Width: > 0, Height: > 0 } ink)
            return _sizes[key] = (box, 0, 0, box.Width);
        var reach = Math.Max(box.Width / 2 - ink.Left, ink.Right - box.Width / 2);
        return _sizes[key] = (box, ink.Top, box.Height - ink.Bottom, 2 * reach);
    }

    /// <summary>The widths of a block's rows: its lines, then its caption.</summary>
    private double[] RowWidthsOf(IReadOnlyList<string> lines, double size, string? caption, double captionSize) =>
        [.. lines.Select(l => Measured(l, _fonts.Bold, size).InkWidth), .. caption is null ? Array.Empty<double>() : [Measured(caption, _fonts.Regular, captionSize).InkWidth]];

    /// <summary>
    /// The room above a block's first line's letters and below its last line's (or caption's).
    /// </summary>
    private (double Top, double Bottom) Ink(IReadOnlyList<string> lines, double size, string? caption, double captionSize) =>
        lines.Count == 0 ? (0, 0)
        : (Measured(lines[0], _fonts.Bold, size).InkTop,
            caption is not null ? Measured(caption, _fonts.Regular, captionSize).InkBottom : Measured(lines[^1], _fonts.Bold, size).InkBottom);

    // ───────────────────────── Words and lines ─────────────────────────

    /// <summary>
    /// A name's pieces a line may break between: words, and each character of a Chinese or Japanese word (those break
    /// anywhere). <c>Space</c>: a space goes before it when it doesn't start a line.
    /// </summary>
    private static bool OpensUnit(string element) => element is "（" or "「" or "『" or "【" or "〔" or "〈" or "《" or "(" or "[" or "“" or "‘";

    private static bool ClosesUnit(string element) => element is "）" or "」" or "』" or "】" or "〕" or "〉" or "》" or ")" or "]" or "”"
        or "’" or "、" or "。" or "，" or "．" or "！" or "？" or "：" or "；" or "ー" or "…" or "・" or "ゃ" or "ゅ" or "ょ" or "っ" or "ャ"
        or "ュ" or "ョ" or "ッ" or "ァ" or "ィ" or "ゥ" or "ェ" or "ォ" or "ぁ" or "ぃ" or "ぅ" or "ぇ" or "ぉ";

    internal static List<(string Text, bool Space)> Units(string name)
    {
        var units = new List<(string, bool)>();
        foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!word.Any(DonutMenu.IsHanOrKana))
            {
                units.Add((word, units.Count > 0));
                continue;
            }
            // A line never ends on an opening bracket or starts on a closing one or a full stop (Japanese and Chinese
            // line breaking): those stay with the character after or before them.
            var pieces = new List<string>();
            var open = "";
            foreach (var element in TextElements(word))
            {
                if (OpensUnit(element))
                    open += element;
                else if (ClosesUnit(element) && pieces.Count > 0 && open.Length == 0)
                    pieces[^1] += element;
                else
                {
                    pieces.Add(open + element);
                    open = "";
                }
            }
            if (open.Length > 0)
                pieces.Add(open);
            for (var p = 0; p < pieces.Count; p++)
                units.Add((pieces[p], p == 0 && units.Count > 0));
        }
        return units;
    }

    /// <summary>
    /// The pieces in <paramref name="count"/> lines, split so the widest line is as narrow as can be.
    /// </summary>
    internal List<string> Split(List<(string Text, bool Space)> units, int count, double size)
    {
        string Join(int from, int to)
        {
            var text = new System.Text.StringBuilder();
            for (var i = from; i < to; i++)
                text.Append(i > from && units[i].Space ? " " : "").Append(units[i].Text);
            return text.ToString();
        }
        double Width(string line) => SizeOf(line, _fonts.Bold, size).Width;

        var n = units.Count;
        if (count <= 1 || n <= 1)
            return [Join(0, n)];
        List<string>? best = null;
        var narrowest = double.MaxValue;
        if (count == 2)
            for (var a = 1; a < n; a++)
                Consider([Join(0, a), Join(a, n)]);
        else
            for (var a = 1; a < n - 1; a++)
                for (var b = a + 1; b < n; b++)
                    Consider([Join(0, a), Join(a, b), Join(b, n)]);
        return best!;

        void Consider(List<string> lines)
        {
            var widest = lines.Max(Width);
            if (widest < narrowest)
                (best, narrowest) = (lines, widest);
        }
    }

    private static List<string> TextElements(string text)
    {
        var elements = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
            elements.Add((string)e.Current);
        return elements;
    }
}
