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
/// One slice's outline in the donut's coordinates (angles clockwise from 12 o'clock, as in <see cref="DonutGeometry"/>).
/// <paramref name="OuterKeepOut"/>: how far in from the outer edge something else is drawn (a group's chevron), 0 for none.
/// </summary>
internal readonly record struct SliceShape(Point Center, double Inner, double Outer, double Mid, double Span, double Gap,
    double OuterKeepOut = 0);

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
internal readonly record struct SafeZone(Point Center, double Inner, double Outer, double Mid, double HalfSpan, double Side)
{
    /// <summary>The inset: this share of the slice's depth, and at least <see cref="MinMargin"/> px × the donut's scale.</summary>
    internal const double MarginShare = 0.08, MinMargin = 6;

    internal static SafeZone Of(SliceShape shape, double scale)
    {
        var margin = Math.Max(MinMargin * scale, MarginShare * (shape.Outer - shape.Inner));
        return new SafeZone(shape.Center, shape.Inner + margin, shape.Outer - Math.Max(margin, shape.OuterKeepOut),
            shape.Mid, shape.Span / 2, margin + shape.Gap / 2);
    }

    internal double Depth => Outer - Inner;

    /// <summary>The point on the slice's middle line <paramref name="radius"/> from the donut's centre.</summary>
    internal Point At(double radius) => DonutGeometry.PointAt(Center, radius, Mid);

    internal bool Contains(Point p) => Contains(p, 0);

    /// <summary>True if a circle of <paramref name="radius"/> round <paramref name="p"/> lies inside.</summary>
    internal bool Contains(Point p, double radius)
    {
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
        for (var i = 0; i < corners.Length; i++)
            if (DistanceTo(corners[i], corners[(i + 1) % corners.Length]) < Inner - 1e-9)
                return false;
        return true;
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
    bool Whole, IReadOnlyList<double>? CurvedWidths = null)
{
    /// <summary>
    /// Bent along the ring: each line on its own circle round the donut's centre, its letters placed one by one
    /// (<see cref="SliceLabelLayout.CurvedGlyphs"/>), each as wide as <see cref="CurvedWidths"/> says. Its middle is
    /// <see cref="Anchor"/>; <see cref="Rotation"/> isn't used.
    /// </summary>
    internal bool Curved => CurvedWidths is not null;

    /// <summary>Its outline, for straight text.</summary>
    internal LabelBox Box => new(Anchor, Width, Height, Rotation);

    /// <summary>True when all of it lies inside <paramref name="zone"/>.</summary>
    internal bool Inside(SafeZone zone) => Curved ? SliceLabelLayout.CurvedFits(zone, this) : zone.Contains(Box);
}

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

    /// <summary>How far round an icon's centre it is checked against the zone: the drawn part of a 24 × 24 icon.</summary>
    internal static double IconReach(double size) => size * IconReachShare;

    internal const double IconReachShare = 0.6;
}

/// <summary>One slice to lay out: its name, caption, icon and Show, and its outline.</summary>
internal sealed record SliceLabelInput(string Name, string? Caption, string? IconKey, LabelShow Show, SliceShape Shape,
    LabelDirection Direction = LabelDirection.Auto);

/// <summary>Lays out the labels of one ring of slices (the donut's face, or a group's ring).</summary>
internal sealed class SliceLabelLayout
{
    /// <summary>A name's size at Label scale 100 % on a donut of the default size, and the smallest it is drawn.</summary>
    internal const double NameSize = 13, MinNameSize = 10;

    /// <summary>
    /// How far a ring's names step down (× the donut's scale, in <see cref="SizeStep"/> steps) for every name to fit
    /// whole: smaller gets hard to read.
    /// </summary>
    internal const double MinStepNameSize = 11, SizeStep = 0.5;

    /// <summary>A caption's size as a share of the name's.</summary>
    internal const double CaptionShare = 0.8;

    /// <summary>The most lines a name wraps to.</summary>
    internal const int MaxLines = 3;

    /// <summary>
    /// The share of its letters a shortened name keeps at least (and it never drops a whole word): fewer ("Tri…" for
    /// "Trim transparent borders") say nothing.
    /// </summary>
    internal const double MinKeptShare = 0.6;

    /// <summary>An icon above a name, and the largest and smallest icon drawn alone, at 100 % and the default size.</summary>
    internal const double IconWithText = 20, IconAloneMax = 28, IconAloneMin = 16, IconStep = 2, IconGap = 3;

    /// <summary>The default donut radius, at which the donut's scale is 1.</summary>
    internal const double DefaultRadius = 150;

    // Where along a slice's middle line a label is tried: the zone's middle first, then further in and out.
    private static readonly double[] Offsets = [0, 0.1, -0.1, 0.2, -0.2, 0.3, -0.3, 0.4, -0.4];

    private readonly LabelFonts _fonts;
    private readonly Dictionary<(string Text, Typeface Face, double Size), Size> _sizes = new();
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
    /// there, at the largest smaller Label scale (in <see cref="ToolbarProfile.LabelScaleStep"/> steps, down to 100 %) at
    /// which every slice does. For a group's ring, which has no room to grow (yet) with the labels.
    /// </summary>
    internal IReadOnlyList<SliceLabel> PlanRingToFit(IReadOnlyList<SliceLabelInput> slices, double labelScale, double scale,
        bool showCaptions)
    {
        var floor = Math.Min(labelScale, 1);
        for (var step = labelScale; ; step = Math.Round(step - ToolbarProfile.LabelScaleStep, 2))
        {
            var plan = PlanRing(slices, Math.Max(step, floor), scale, showCaptions);
            if (step <= floor + 1e-9 || plan.Select((label, i) => Fits(label, slices[i])).All(f => f))
                return plan;
        }

        // Shows what it was asked to: its whole name, or its icon.
        static bool Fits(SliceLabel label, SliceLabelInput slice) =>
            (slice.Name.Length == 0 && slice.IconKey is null) || label.Kind == SliceLabelKind.Icon ||
            (label.Kind != SliceLabelKind.None && label.NameWhole);
    }

    /// <summary>
    /// The labels of one ring: one style for the slices of each Show that have an icon, one name size and one icon
    /// size. Each style is tried at every size of <see cref="SizesFor"/>, largest first, and taken only if every slice
    /// fits: Auto (whole names only) icon and name, else name, else icon; Icon & text icon and name, whole names first,
    /// then names shortened to <see cref="MinKept"/>, else the icon alone; Text only the name, whole first, then
    /// shortened to <see cref="MinKept"/>, then as far as it takes (the centre names the slice in full). A slice
    /// without an icon (a format) shows its name and never decides the others' style. Captions show under names alone
    /// only, on every slice that has one or on none.
    /// </summary>
    internal IReadOnlyList<SliceLabel> PlanRing(IReadOnlyList<SliceLabelInput> slices, double labelScale, double scale,
        bool showCaptions)
    {
        var baseSize = NameSizeFor(labelScale, scale);
        var sizes = SizesFor(baseSize, scale);
        var k = labelScale * scale;
        var zones = slices.Select(s => SafeZone.Of(s.Shape, scale)).ToArray();
        // The ways the ring's names are tried, one for all of them: on a deep ring straight, out from the centre, then
        // bent; on a wide one bent, straight, then out from the centre. A preset's own direction keeps its slice.
        LabelDirection[] ways = zones.Length > 0 && IsWide(zones[0])
            ? [LabelDirection.AlongRing, LabelDirection.Straight, LabelDirection.OutFromCentre]
            : [LabelDirection.Straight, LabelDirection.OutFromCentre, LabelDirection.AlongRing];
        LabelDirection DirectionOf(int i, LabelDirection way) => slices[i].Direction == LabelDirection.Auto ? way : slices[i].Direction;
        // Bent along the ring only if every name of the ring can be (else all level with it).
        var ringBends = slices.Where(s => s.Direction == LabelDirection.Auto && s.Name.Length > 0).All(s => CanBend(s.Name));
        bool? BendsOf(int i) => slices[i].Direction == LabelDirection.Auto ? ringBends : null;
        // An icon beside a smaller name shrinks with it, to no less than the smallest icon drawn alone.
        double IconFor(double size) => IconWithText * k * Math.Max(IconAloneMin / IconWithText, size / baseSize);
        string? CaptionOf(SliceLabelInput s) => showCaptions && !string.IsNullOrEmpty(s.Caption) &&
            !string.Equals(s.Caption, s.Name, StringComparison.OrdinalIgnoreCase) ? s.Caption : null;

        var result = new SliceLabel?[slices.Count];
        var wantIcon = new bool[slices.Count];

        // The icon and the name, no caption: the icon stands in for it.
        SliceLabel? Both(int i, double size, bool shorten, LabelDirection way)
        {
            var s = slices[i];
            var icon = IconFor(size);
            var fit = FitIconAndText(zones[i], s.Name, null, size, 0, icon, k, shorten, DirectionOf(i, way), MinKept(s.Name), BendsOf(i));
            return fit is { } f ? new SliceLabel(SliceLabelKind.IconAndText, f.Text, s.IconKey, f.Icon, icon, zones[i]) : null;
        }
        // The name, with its caption when withCaption (null if the caption doesn't fit under it); shortened to minKept
        // (a caption never goes with a shortened name).
        SliceLabel? Name(int i, double size, bool withCaption, bool shorten, LabelDirection way, int? minKept = null)
        {
            var s = slices[i];
            var caption = withCaption ? CaptionOf(s) : null;
            var block = FitText(zones[i], s.Name, caption, size, CaptionSizeFor(size), alongToo: true, DirectionOf(i, way), BendsOf(i));
            if (caption is not null && block?.Caption is null)
                return null;
            if (block is null && shorten && caption is null)
                block = FitShortened(zones[i], s.Name, size, alongToo: true, DirectionOf(i, way), minKept ?? MinKept(s.Name), BendsOf(i));
            return block is null ? null : new SliceLabel(SliceLabelKind.Text, block, null, default, 0, zones[i]);
        }
        // A style for all of group, at the largest size every slice fits; false (nothing set) if none does. A slice
        // without an icon shows its name beside the others' icons, shortened if need be.
        bool Take(List<int> group, Func<int, double, SliceLabel?> fit)
        {
            if (Plan(group, fit) is not { } plan)
                return false;
            for (var j = 0; j < group.Count; j++)
                result[group[j]] = plan[j];
            return true;
        }
        // The labels of group at the largest size every slice fits, or null.
        List<SliceLabel>? Plan(List<int> group, Func<int, double, SliceLabel?> fit)
        {
            foreach (var size in sizes)
            {
                var plan = group.Select(i => fit(i, size)).ToList();
                if (plan.All(p => p is not null))
                    return plan!;
            }
            return null;
        }
        // Icons and names, one way for the ring's names (a name without an icon runs that way too), each way through
        // the sizes.
        bool TakeBoth(List<int> group, bool shorten)
        {
            if (!group.Any(i => slices[i].IconKey is not null))
                return false;
            foreach (var way in ways)
            {
                // A name without an icon runs the way the names beside icons do: out from the centre beside an icon
                // before it, bent beside one on a wide ring that bends, else straight (under an upright icon).
                var alone = way == LabelDirection.OutFromCentre ? way
                    : way == LabelDirection.AlongRing && zones.Length > 0 && IsWide(zones[0]) && ringBends ? way
                    : LabelDirection.Straight;
                if (Take(group, (i, size) => slices[i].IconKey is null ? Name(i, size, false, true, alone) : Both(i, size, shorten, way)))
                    return true;
            }
            return false;
        }
        // One direction for the ring's names: straight through every size, then along the slices.
        // Names alone, one way for the ring: captions on every slice that has one, else on none; of the ways, the one
        // that shows the most names whole, then at the largest size, then keeps the most letters (the first way of
        // those, in order, when they tie).
        bool TakeNames(List<int> group, bool shorten)
        {
            foreach (var withCaption in group.Any(i => CaptionOf(slices[i]) is not null) ? new[] { true, false } : new[] { false })
            {
                List<SliceLabel>? best = null;
                (int Whole, double Size, int Letters) bestScore = default;
                foreach (var way in ways)
                {
                    if (Plan(group, (i, size) => Name(i, size, withCaption, shorten || slices[i].IconKey is null, way)) is not { } plan)
                        continue;
                    var score = (plan.Count(l => l.NameWhole), plan.Max(l => l.Text!.Size), plan.Sum(l => l.Text!.Lines.Sum(t => t.Length)));
                    if (best is null || score.CompareTo(bestScore) > 0)
                        (best, bestScore) = (plan, score);
                }
                if (best is not null)
                {
                    for (var j = 0; j < group.Count; j++)
                        result[group[j]] = best[j];
                    return true;
                }
            }
            return false;
        }
        // As far as it takes, at the smallest size, still one way for all of them.
        void LastResort(List<int> group)
        {
            foreach (var way in ways)
            {
                var plan = group.Select(i => Name(i, sizes[^1], false, true, way, 0)).ToList();
                if (plan.All(p => p is not null) || way == ways[^1])
                {
                    for (var j = 0; j < group.Count; j++)
                        result[group[j]] = plan[j] ?? SliceLabel.Empty;
                    return;
                }
            }
        }
        List<int> Showing(LabelShow show) => Enumerable.Range(0, slices.Count)
            .Where(i => slices[i].Show == show && slices[i].Name.Length > 0).ToList();
        void Icons(List<int> group)
        {
            foreach (var i in group)
                wantIcon[i] = slices[i].IconKey is not null;
        }

        var auto = Showing(LabelShow.Auto);
        if (auto.Count > 0 && !TakeBoth(auto, false) && !TakeNames(auto, false))
            Icons(auto);
        var both = Showing(LabelShow.IconAndText);
        if (both.Count > 0 && !TakeBoth(both, false) && !TakeBoth(both, true))
            Icons(both);
        var names = Showing(LabelShow.TextOnly);
        if (names.Count > 0 && !TakeNames(names, false) && !TakeNames(names, true))
            LastResort(names); // the centre names the slice in full

        // What's left: icons (Icon only, or a ring gone to icons) and names of slices without one.
        var rest = new List<int>();
        for (var i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            if (result[i] is not null || wantIcon[i])
                continue;
            if (s.Name.Length == 0 && s.IconKey is null)
                result[i] = SliceLabel.Empty;
            else if (s.IconKey is not null && (s.Show == LabelShow.IconOnly || s.Name.Length == 0))
                wantIcon[i] = true;
            else
                rest.Add(i);
        }
        if (rest.Count > 0 && !TakeNames(rest, false) && !TakeNames(rest, true))
            LastResort(rest);

        // Icons alone: one size for the ring, the largest every one of them fits at, centred in its slice.
        var alone = Enumerable.Range(0, slices.Count).Where(i => wantIcon[i]).ToList();
        if (alone.Count > 0)
        {
            var size = 0.0;
            for (var candidate = IconAloneMax; candidate >= IconAloneMin - 1e-9; candidate -= IconStep)
                if (alone.All(i => zones[i].Contains(zones[i].At((zones[i].Inner + zones[i].Outer) / 2), SliceLabel.IconReach(candidate * k))))
                {
                    size = candidate * k;
                    break;
                }
            // Next to icons drawn beside names, the same size as those: one icon size for the ring.
            var withName = result.Where(r => r?.Kind == SliceLabelKind.IconAndText).Select(r => r!.IconSize).DefaultIfEmpty(0).Max();
            if (size > 0 && withName > 0)
                size = Math.Min(size, withName);
            if (size > 0)
                foreach (var i in alone)
                    result[i] = new SliceLabel(SliceLabelKind.Icon, null, slices[i].IconKey, zones[i].At((zones[i].Inner + zones[i].Outer) / 2), size, zones[i]);
            else
                LastResort(alone);
        }
        // The size each label's name was laid out at (for icons alone, the smallest tried), and the scale of its icon.
        return result.Select((r, i) =>
        {
            var size = r?.Text?.Size ?? sizes[^1];
            return (r ?? SliceLabel.Empty with { Zone = zones[i] }) with { NameSize = size, Scale = IconFor(size) / IconWithText };
        }).ToArray();
    }

    /// <summary>
    /// The name whole (with its caption under it when both fit), in as few lines as it takes, in the directions
    /// <paramref name="direction"/> allows (<see cref="Tries"/>; only straight without <paramref name="alongToo"/>).
    /// Null if it doesn't fit whole.
    /// </summary>
    internal LabelText? FitText(SafeZone zone, string name, string? caption, double size, double captionSize, bool alongToo,
        LabelDirection direction = LabelDirection.Auto, bool? bends = null)
    {
        var units = Units(name);
        if (units.Count == 0)
            return null;
        foreach (var turns in Tries(zone, name, alongToo, direction, bends))
            foreach (var withCaption in caption is null ? new[] { false } : new[] { true, false })
                for (var lines = 1; lines <= Math.Min(MaxLines, units.Count); lines++)
                {
                    var text = Split(units, lines, size);
                    foreach (var turn in turns)
                        if (Place(zone, text, size, withCaption ? caption : null, captionSize, turn, whole: true) is { } block)
                            return block;
                }
        return null;
    }

    /// <summary>
    /// The name on one line, shortened with "…" until it fits, keeping <paramref name="minKept"/> text elements at least,
    /// in whichever direction keeps the most of it. Null if it doesn't fit even then.
    /// </summary>
    internal LabelText? FitShortened(SafeZone zone, string name, double size, bool alongToo,
        LabelDirection direction = LabelDirection.Auto, int minKept = 0, bool? bends = null)
    {
        var turns = Tries(zone, name, alongToo, direction, bends).SelectMany(t => t).ToArray();
        var elements = TextElements(name.Trim());
        for (var keep = elements.Count - 1; keep >= minKept; keep--)
        {
            var text = string.Concat(elements.Take(keep)).TrimEnd() + "…";
            foreach (var turn in turns)
                if (Place(zone, [text], size, null, 0, turn, whole: false) is { } block)
                    return block;
        }
        return null;
    }

    /// <summary>
    /// The icon above the name, both upright, placed along the slice's middle line; the name shortened to one line
    /// (keeping <paramref name="minKept"/> text elements at least) if <paramref name="shorten"/> and it doesn't fit
    /// whole. Null if they don't fit.
    /// </summary>
    internal (LabelText Text, Point Icon)? FitIconAndText(SafeZone zone, string name, string? caption, double size,
        double captionSize, double iconSize, double scale, bool shorten, LabelDirection direction = LabelDirection.Auto,
        int minKept = 1, bool? bends = null)
    {
        var units = Units(name);
        if (units.Count == 0)
            return null;
        var gap = IconGap * scale;
        var options = new List<(IReadOnlyList<string> Lines, string? Caption, bool Whole)>();
        foreach (var withCaption in caption is null ? new[] { false } : new[] { true, false })
            for (var lines = 1; lines <= Math.Min(MaxLines, units.Count); lines++)
                options.Add((Split(units, lines, size), withCaption ? caption : null, true));
        if (shorten)
        {
            var elements = TextElements(name.Trim());
            for (var keep = elements.Count - 1; keep >= Math.Max(1, minKept); keep--)
                options.Add(([string.Concat(elements.Take(keep)).TrimEnd() + "…"], null, false));
        }
        // On a wide slice an upright stack only fits where the ring runs up and down: the name bends along the ring
        // beside (or under) the upright icon instead, on every slice of the ring alike.
        var bent = direction is LabelDirection.Auto or LabelDirection.AlongRing && IsWide(zone) && (bends ?? CanBend(name));
        if (bent)
        {
            foreach (var (lines, cap, whole) in options)
            {
                var (_, heights, _) = Measure(lines, size, cap, captionSize);
                if (cap is null && (FitIconBesideBent(zone, lines, heights, size, captionSize, iconSize, gap, whole)
                                    ?? FitIconOverBent(zone, lines, heights, size, captionSize, iconSize, gap, whole)) is { } fit)
                    return fit;
            }
            return null;
        }
        // A deep slice: the icon above its name, both upright; where that can't show the name whole, the icon before a
        // name running out from the centre (the only way for Out from the centre). Whole names before shortened ones.
        var above = direction != LabelDirection.OutFromCentre;
        var before = direction is LabelDirection.Auto or LabelDirection.OutFromCentre;
        foreach (var whole in new[] { true, false })
            foreach (var beside in new[] { false, true })
            {
                if (beside ? !before : !above)
                    continue;
                foreach (var (lines, cap, isWhole) in options.Where(o => o.Whole == whole))
                {
                    var (textWidth, heights, captionHeight) = Measure(lines, size, cap, captionSize);
                    if ((beside
                            ? FitIconBeforeAlong(zone, lines, heights, textWidth, cap, captionSize, captionHeight, size, iconSize, gap, isWhole)
                            : FitIconAbove(zone, lines, heights, textWidth, cap, captionSize, captionHeight, size, iconSize, gap, isWhole))
                        is { } fit)
                        return fit;
                }
            }
        return null;
    }

    // The icon above its name, both upright, on the slice's middle line.
    private static (LabelText Text, Point Icon)? FitIconAbove(SafeZone zone, IReadOnlyList<string> lines, double[] heights,
        double textWidth, string? cap, double captionSize, double captionHeight, double size, double iconSize, double gap, bool whole)
    {
        var textHeight = heights.Sum() + captionHeight;
        var width = Math.Max(iconSize, textWidth);
        var height = iconSize + gap + textHeight;
        foreach (var offset in Offsets)
        {
            var at = zone.At((zone.Inner + zone.Outer) / 2 + offset * zone.Depth);
            var icon = new Point(at.X, at.Y - height / 2 + iconSize / 2);
            var textAt = new Point(at.X, at.Y + height / 2 - textHeight / 2);
            if (zone.Contains(new LabelBox(at, width, height, 0)) && zone.Contains(icon, SliceLabel.IconReach(iconSize)))
                return (new LabelText(lines, heights, size, cap, captionSize, captionHeight, textWidth, textHeight, textAt, 0, whole), icon);
        }
        return null;
    }

    /// <summary>
    /// The upright icon before a name running out from the centre, both on the slice's middle line: first as read, so
    /// the inner of the two on the right half (read outward) and the outer on the left half (read inward).
    /// </summary>
    private static (LabelText Text, Point Icon)? FitIconBeforeAlong(SafeZone zone, IReadOnlyList<string> lines, double[] heights,
        double textWidth, string? cap, double captionSize, double captionHeight, double size, double iconSize, double gap, bool whole)
    {
        var textHeight = heights.Sum() + captionHeight;
        var turn = Along(zone.Mid);
        var a = (zone.Mid % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        var outward = a < Math.PI ? 1 : -1; // which way the name reads along the middle line
        var total = iconSize + gap + textWidth;
        foreach (var offset in Offsets)
        {
            var middle = (zone.Inner + zone.Outer) / 2 + offset * zone.Depth;
            var icon = zone.At(middle - outward * (total / 2 - iconSize / 2));
            var textAt = zone.At(middle + outward * (total / 2 - textWidth / 2));
            if (zone.Contains(icon, SliceLabel.IconReach(iconSize)) && zone.Contains(new LabelBox(textAt, textWidth, textHeight, turn)))
                return (new LabelText(lines, heights, size, cap, captionSize, captionHeight, textWidth, textHeight, textAt, turn, whole), icon);
        }
        return null;
    }

    /// <summary>
    /// The upright icon before a name bent along the ring, side by side along it (first as read: clockwise on the top
    /// half, anticlockwise on the bottom): a group ring is wide and shallow, too shallow at most sizes for one above the
    /// other. Null if they don't fit.
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
    /// The turn that runs text along a slice whose middle is at <paramref name="mid"/>: outward on the right half, inward
    /// on the left half, so it is never upside down.
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

    /// <summary>True on the bottom half of the donut, where bent text runs the other way round to stay upright.</summary>
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

    /// <summary>How far round from the zone's middle a bent name is centred (beside an icon): its anchor's angle.</summary>
    private static double Shift(SafeZone zone, LabelText text) =>
        Math.IEEERemainder(DonutGeometry.AngleOf(zone.Center, text.Anchor) - zone.Mid, 2 * Math.PI);

    private static bool CurvedFits(SafeZone zone, double middle, IReadOnlyList<double> heights, double height,
        IReadOnlyList<double> widths, double shift = 0)
    {
        if (middle - height / 2 < zone.Inner - 1e-9 || middle + height / 2 > zone.Outer + 1e-9)
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
        double turn, bool whole)
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
        foreach (var offset in Offsets)
        {
            var at = zone.At((zone.Inner + zone.Outer) / 2 + offset * zone.Depth);
            if (zone.Contains(new LabelBox(at, width, height, turn)))
                return new LabelText(lines, heights, size, caption, captionSize, captionHeight, width, height, at, turn, whole);
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

    private Size SizeOf(string text, Typeface face, double size)
    {
        var key = (text, face, size);
        if (_sizes.TryGetValue(key, out var known))
            return known;
        if (_sizes.Count >= MaxCachedSizes)
            _sizes.Clear();
        var measured = new FormattedText(DonutMenu.KeepKoreanWords(text), CultureInfo.CurrentUICulture, _fonts.Direction, face, size, null);
        return _sizes[key] = new Size(measured.Width, measured.Height);
    }

    // ───────────────────────── Words and lines ─────────────────────────

    /// <summary>
    /// A name's pieces a line may break between: words, and each character of a Chinese or Japanese word (those break
    /// anywhere). <c>Space</c>: a space goes before it when it doesn't start a line.
    /// </summary>
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
            var first = true;
            foreach (var element in TextElements(word))
            {
                units.Add((element, first && units.Count > 0));
                first = false;
            }
        }
        return units;
    }

    /// <summary>The pieces in <paramref name="count"/> lines, split so the widest line is as narrow as can be.</summary>
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
