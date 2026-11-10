// The most readable label, every time: every built-in donut, front and back and every group's ring, over the range of
// donut sizes, holes, Label scales, ring sizes and spreads, Show modes, text directions and scripts (Latin, Cyrillic,
// Japanese, Korean, Arabic). On every slice:
//
//   Shown          → a slice with a name or an icon shows something.
//   Never cut      → a name is shortened with "…" only where the direction it runs in (its ring's, or its preset's) has
//                    no room for it whole.
//   Icon & text    → a ring's slices asked for icon and name show icons alone only where one of them can't fit both,
//                    not even with its name shortened as far as allowed (SliceLabelLayout.MinKept): one style a ring.
//   One style      → the Auto slices of a ring all show the same style, and not a lesser one (icons alone, or names
//                    without their icons) where every one of them has room for more.
//   Bent           → only names whose letters can be placed one by one bend along the ring.
//   One direction  → the names of a ring whose presets leave Text direction on Auto all run one way: straight, out
//                    from the centre, bent along the ring, or level with it.
//
// Layout only, nothing is drawn. "Room" is judged apart from the layout, from the slice's shape and the name's measured
// size with a margin to spare (Room), so a gap in the layout shows up instead of being agreed with.

using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class SliceLabelReadabilityTests
{
    private static readonly string[] Languages = ["en", "de", "ru", "ja", "ko", "ar"];

    private static readonly (double Radius, double Hole, double Scale, LabelShow Show, double RingSize, double Spread)[] Settings =
        [.. from radius in new[] { 110.0, 160, 220 }
            from hole in new[] { 0.42, 0.64 }
            from scale in new[] { 0.8, 1.15, 1.6 }
            from show in new[] { LabelShow.Auto, LabelShow.IconAndText, LabelShow.TextOnly }
            from ring in new[] { (1.0, 1.0), (1.5, 1.9), (0.75, 3.0) }
            select (radius, hole, scale, show, ring.Item1, ring.Item2)];

    [AvaloniaFact]
    public void Every_label_is_the_most_readable_that_fits_in_every_script()
    {
        var problems = new List<string>();
        try
        {
            foreach (var language in Languages)
                Sweep(language, null, Settings, problems);
        }
        finally
        {
            TestServices.EnsureInitialized("en");
        }
        Report(problems);
    }

    [AvaloniaFact]
    public void A_presets_own_direction_is_followed_and_still_reads_best_in_it()
    {
        var problems = new List<string>();
        try
        {
            foreach (var direction in new[] { LabelDirection.Straight, LabelDirection.OutFromCentre, LabelDirection.AlongRing })
                Sweep("en", direction, Settings.Where(s => s.Show != LabelShow.TextOnly || s.Scale == 1.15).ToArray(), problems);
        }
        finally
        {
            TestServices.EnsureInitialized("en");
        }
        Report(problems);
    }

    // Every problem in a file beside the screenshots (only the first 40 in the failure message).
    private static void Report(List<string> problems, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        File.WriteAllLines(Path.Combine(TestServices.ScreenshotDirectory, $"readability-{test}.txt"), problems);
        Assert.True(problems.Count == 0, $"{problems.Count} problems:\n{string.Join("\n", problems.Take(40))}");
    }

    private static void Sweep(string language, LabelDirection? direction,
        IReadOnlyList<(double Radius, double Hole, double Scale, LabelShow Show, double RingSize, double Spread)> settings,
        List<string> problems)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        var store = AppHost.Get<UserDataStore>();
        var family = SoftPalette.FontFamilyFor(language);
        var bold = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
        var regular = new Typeface(family);
        var layout = new SliceLabelLayout(new LabelFonts(bold, regular,
            CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight));
        var donut = new DonutMenu { ShowCaptions = false };
        foreach (var profile in BuiltInData.CreateProfiles())
            foreach (var (faceName, ids) in new[] { ("front", profile.PresetIds), ("back", profile.BackPresetIds ?? []) })
            {
                donut.Items = SliceLabelFitTests.Items(ids, store, loc, direction);
                foreach (var (radius, hole, scale, show, ringSize, spread) in settings)
                {
                    (donut.OuterRadius, donut.HoleRatio, donut.LabelScale, donut.DefaultShow) = (radius, hole, scale, show);
                    (donut.GroupRingSize, donut.GroupRingSpread) = (ringSize, spread);
                    var (face, rings) = donut.PlanLabels(bold, regular);
                    var where = $"{language} {profile.Id} {faceName} r{radius} hole {hole} ×{scale} {show} ring {ringSize}/{spread}" +
                                (direction is { } d ? $" {d}" : "");
                    Check(layout, face, donut.Items, show, where, problems);
                    for (var g = 0; g < rings.Count; g++)
                        Check(layout, rings[g], donut.Items[g].Children ?? [], show, $"{where} ring {donut.Items[g].Id}", problems);
                }
            }
    }

    private static void Check(SliceLabelLayout layout, IReadOnlyList<SliceLabel> ring, IReadOnlyList<DonutItem> items,
        LabelShow show, string where, List<string> problems)
    {
        var autoKinds = new HashSet<SliceLabelKind>();
        var (allRoomForName, allRoomForBoth) = (true, true);
        var (bothAsked, iconsAlone, allRoomForShortened) = (0, 0, true);
        var ways = new HashSet<string> { "straight", "out", "bent", "level" };
        for (var i = 0; i < ring.Count; i++)
        {
            var (label, item) = (ring[i], items[i]);
            var name = item.Label;
            var direction = item.Direction ?? LabelDirection.Auto;
            var wanted = item.Show ?? show;
            if (label.Kind == SliceLabelKind.None)
            {
                if (name.Length > 0 || item.IconKey is not null)
                    problems.Add($"{where}: \"{name}\" shows nothing");
                continue;
            }
            var zone = label.Zone;
            var captionSize = SliceLabelLayout.CaptionSizeFor(label.NameSize);

            var line = Measure(layout, name, label.NameSize);
            var icon = SliceLabelLayout.IconWithText * label.Scale;
            var gap = SliceLabelLayout.IconGap * label.Scale;
            var bends = layout.CanBend(name);

            if (label.Text is { Whole: false } cut)
            {
                // In the way it runs (a ring's names all run one way, unless a preset says its own).
                var runs = direction != LabelDirection.Auto ? direction
                    : cut.Curved ? LabelDirection.AlongRing
                    : Math.Abs(Math.IEEERemainder(cut.Rotation - SliceLabelLayout.Along(zone.Mid), 2 * Math.PI)) < 1e-6 ? LabelDirection.OutFromCentre
                    : Math.Abs(Math.IEEERemainder(cut.Rotation, 2 * Math.PI)) < 1e-6 ? LabelDirection.Straight
                    : LabelDirection.AlongRing;
                var room = label.Kind == SliceLabelKind.IconAndText
                    ? Room(zone, icon + gap + line.Width, Math.Max(icon, line.Height), runs, bends)
                    : Room(zone, line.Width, line.Height, runs, bends);
                if (room is { } way)
                    problems.Add($"{where}: \"{name}\" cut to \"{cut.Lines[0]}\" though it has room whole ({way})");
            }

            if (wanted == LabelShow.IconAndText && item.IconKey is not null && name.Length > 0)
            {
                bothAsked++;
                iconsAlone += label.Kind == SliceLabelKind.Icon ? 1 : 0;
                var elements = new StringInfo(name.Trim());
                var kept = SliceLabelLayout.MinKept(name);
                var shortest = kept >= elements.LengthInTextElements ? line
                    : Measure(layout, elements.SubstringByTextElements(0, kept).TrimEnd() + "…", label.NameSize);
                allRoomForShortened &= Room(zone, icon + gap + shortest.Width, Math.Max(icon, shortest.Height), direction, bends) is not null;
            }

            if (wanted == LabelShow.Auto && item.IconKey is not null && name.Length > 0)
            {
                autoKinds.Add(label.Kind);
                allRoomForName &= Room(zone, line.Width, line.Height, direction, bends) is not null;
                allRoomForBoth &= Room(zone, icon + gap + line.Width, Math.Max(icon, line.Height), direction, bends) is not null;
            }

            if (label.Text is { } run && direction == LabelDirection.Auto)
            {
                bool Is(double turn) => Math.Abs(Math.IEEERemainder(run.Rotation - turn, 2 * Math.PI)) < 1e-6;
                var could = run.Curved ? new HashSet<string> { "bent" } : new HashSet<string>();
                if (!run.Curved && Is(0)) could.Add("straight");
                if (!run.Curved && Is(SliceLabelLayout.Along(zone.Mid))) could.Add("out");
                if (!run.Curved && Is(SliceLabelLayout.Across(zone.Mid))) could.Add("level");
                ways.IntersectWith(could);
            }

            if (label.Text is { Curved: true } && !layout.CanBend(name))
                problems.Add($"{where}: \"{name}\" is bent though its letters can't be");
        }
        if (ways.Count == 0)
            problems.Add($"{where}: names run in different directions");
        if (bothAsked > 0 && iconsAlone == bothAsked && allRoomForShortened)
            problems.Add($"{where}: Icon & text shows icons alone though every slice has room for icon and name");
        else if (iconsAlone > 0 && iconsAlone < bothAsked)
            problems.Add($"{where}: Icon & text mixes icons alone with icons and names");
        if (autoKinds.Count > 1)
            problems.Add($"{where}: Auto slices mix {string.Join(", ", autoKinds)}");
        else if (autoKinds.SetEquals([SliceLabelKind.Icon]) && allRoomForName)
            problems.Add($"{where}: Auto shows icons alone though every name has room");
        else if (autoKinds.SetEquals([SliceLabelKind.Text]) && allRoomForBoth)
            problems.Add($"{where}: Auto shows names without icons though every slice has room for both");
    }

    // A name on one line as drawn.
    private static Avalonia.Size Measure(SliceLabelLayout layout, string name, double size)
    {
        var text = new FormattedText(DonutMenu.KeepKoreanWords(name), CultureInfo.CurrentUICulture, layout.Fonts.Direction,
            layout.Fonts.Bold, size, null);
        return new Avalonia.Size(text.WidthIncludingTrailingWhitespace, text.Height);
    }

    /// <summary>The share of a slice's room a label may take before it counts as plenty: the rest is kept to spare.</summary>
    private const double Plenty = 0.85;

    /// <summary>
    /// The way a block of <paramref name="width"/> by <paramref name="height"/> has plenty of room in
    /// <paramref name="zone"/>, among the directions <paramref name="direction"/> allows, or null. Along the ring: its
    /// height within the zone's depth, its width within the arc at its inner edge less the side margins. Out from the
    /// centre: the same turned a quarter. Straight only on a slice at 12 or 6 o'clock, where it is the same as along.
    /// </summary>
    private static string? Room(SafeZone zone, double width, double height, LabelDirection direction, bool bends)
    {
        bool AlongRing()
        {
            if (height > zone.Depth * Plenty)
                return false;
            var inner = (zone.Inner + zone.Outer) / 2 - height / 2;
            var arc = 2 * Math.Min(zone.HalfSpan, Math.PI) * inner - 2 * zone.Side;
            return width <= arc * Plenty;
        }
        bool OutFromCentre() => width <= zone.Depth * Plenty && height <= (2 * Math.Min(zone.HalfSpan, Math.PI) * zone.Inner - 2 * zone.Side) * Plenty;
        return direction switch
        {
            LabelDirection.AlongRing when bends => AlongRing() ? "along the ring" : null,
            LabelDirection.OutFromCentre => OutFromCentre() ? "out from the centre" : null,
            LabelDirection.Straight => null,
            LabelDirection.Auto when bends && SliceLabelLayout.IsWide(zone) && AlongRing() => "along the ring",
            LabelDirection.Auto when OutFromCentre() => "out from the centre",
            _ => null,
        };
    }
}
