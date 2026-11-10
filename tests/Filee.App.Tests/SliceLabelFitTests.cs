// Every built-in donut, front and back and every group's ring (at three ring sizes and spreads), at the smallest,
// default and largest donut, at Label scale 80 / 100 / 160 %, in the four Show modes and every language: each label
// stays inside its slice's safe zone, a whole name keeps its words whole (Chinese and Japanese may break between
// characters), and a ring has one name size and one icon size. And never a stub: with Auto or Icon & text, a ring's
// slices that have an icon show one style, a name with an icon is shortened (Icon & text only) to 60 % of its letters
// at most, an icon never comes with a caption, names alone show their captions on all or none, and the face's names
// are 11 px × the donut's size at least (never under 10 px, or as small as the Label scale asks). Layout only, nothing
// is drawn.

using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class SliceLabelFitTests
{
    private static readonly double[] Radii = [100, 150, 240];
    private static readonly double[] Scales = [0.8, 1, 1.6];

    // Group ring size and spread: the default, the smallest, and the largest spread out widest.
    private static readonly (double Size, double Spread)[] Rings = [(1, 1), (0.75, 1), (1.5, 3)];

    // Every combination of the above and the four Show modes.
    private static readonly (double Radius, double Scale, LabelShow Show, (double Size, double Spread) Ring)[] Settings =
        [.. from radius in Radii
            from scale in Scales
            from show in Enum.GetValues<LabelShow>()
            from ring in Rings
            select (radius, scale, show, ring)];

    [AvaloniaFact]
    public void Every_label_of_every_built_in_donut_fits_its_slice_in_every_language()
    {
        var problems = new List<string>();
        try
        {
            foreach (var language in AppLanguages.All.Select(l => l.Code))
            {
                TestServices.EnsureInitialized(language);
                var loc = AppHost.Get<ILocalizer>();
                var store = AppHost.Get<UserDataStore>();
                var family = SoftPalette.FontFamilyFor(language);
                var bold = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
                var regular = new Typeface(family);
                var donut = new DonutMenu();
                foreach (var profile in BuiltInData.CreateProfiles())
                    foreach (var (faceName, ids) in new[] { ("front", profile.PresetIds), ("back", profile.BackPresetIds ?? []) })
                    {
                        donut.Items = Items(ids, store, loc);
                        foreach (var (radius, scale, show, ring) in Settings)
                        {
                            donut.OuterRadius = radius;
                            donut.LabelScale = scale;
                            donut.DefaultShow = show;
                            (donut.GroupRingSize, donut.GroupRingSpread) = ring;
                            var (face, rings) = donut.PlanLabels(bold, regular);
                            var where = $"{language} {profile.Id} {faceName} r{radius} ×{scale} {show} ring {ring.Size}/{ring.Spread}";
                            Check(face, donut.Items, where, problems, show);
                            var floor = Math.Min(SliceLabelLayout.NameSizeFor(scale, donut.DonutScale),
                                Math.Max(SliceLabelLayout.MinNameSize, SliceLabelLayout.MinStepNameSize * donut.DonutScale));
                            if (face.Select(l => l.Text?.Size).OfType<double>().Any(size => size < floor - 1e-9))
                                problems.Add($"{where}: names under {floor:0.#} px");
                            for (var g = 0; g < rings.Count; g++)
                                Check(rings[g], donut.Items[g].Children ?? [], $"{where} ring {donut.Items[g].Id}", problems, show);
                        }
                    }
            }
        }
        finally
        {
            TestServices.EnsureInitialized("en");
        }
        Assert.True(problems.Count == 0, $"{problems.Count} problems:\n{string.Join("\n", problems.Take(40))}");
    }

    // The slices as the live donut gives them (RadialViewModel.ItemOf), without checking them against files.
    private static List<DonutItem> Items(IEnumerable<string> ids, UserDataStore store, ILocalizer loc)
    {
        DonutItem ItemOf(Preset p, bool ring) => new()
        {
            Id = p.Id,
            Label = ring ? loc.RingName(p) : loc.DonutName(p),
            Caption = ring ? null : p.TargetFormat == BuiltInData.SameAsSource ? "JPG/PNG" : FormatLabels.NameOf(loc, p.TargetFormat),
            FullName = loc.DisplayName(p),
            IconKey = SliceIcons.For(p),
        };
        return ids.Select(store.FindPreset).OfType<Preset>().Select(p =>
        {
            if (!p.IsGroup)
                return ItemOf(p, ring: false);
            var children = p.SubPresetIds.Select(store.FindPreset).OfType<Preset>().Where(c => !c.IsGroup)
                .Take(Preset.MaxSubPresets).Select(c => ItemOf(c, ring: true)).ToList();
            return new DonutItem
            {
                Id = p.Id,
                Label = loc.DonutName(p),
                Caption = loc.Format("donut.group_caption", children.Count),
                FullName = loc.DisplayName(p),
                IconKey = SliceIcons.For(p),
                Children = children,
            };
        }).ToList();
    }

    private static void Check(IReadOnlyList<SliceLabel> ring, IReadOnlyList<DonutItem> items, string where, List<string> problems,
        LabelShow show)
    {
        var styles = ring.Where((_, i) => items[i].IconKey is not null && items[i].Label.Length > 0).Select(l => l.Kind).Distinct().ToList();
        if (show is LabelShow.Auto or LabelShow.IconAndText && styles.Count > 1)
            problems.Add($"{where}: mixed styles {string.Join(", ", styles)}");
        // One direction for the ring's names: straight, or along their slices (at 3 and 9 o'clock the two look alike).
        var ways = new HashSet<string> { "straight", "along" };
        foreach (var l in ring.Where(l => l.Text is not null))
        {
            var t = l.Text!;
            var could = new HashSet<string>();
            if (Math.Abs(Math.IEEERemainder(t.Rotation, 2 * Math.PI)) < 1e-6)
                could.Add("straight");
            if (Math.Abs(Math.IEEERemainder(t.Rotation - SliceLabelLayout.Along(l.Zone.Mid), 2 * Math.PI)) < 1e-6)
                could.Add("along");
            ways.IntersectWith(could);
        }
        if (ways.Count == 0)
            problems.Add($"{where}: names run in different directions");
        var captioned = ring.Where((l, i) => l.Kind == SliceLabelKind.Text && !string.IsNullOrEmpty(items[i].Caption) &&
            !string.Equals(items[i].Caption, items[i].Label, StringComparison.OrdinalIgnoreCase)).ToList();
        var shown = captioned.Count(l => l.Text!.Caption is not null);
        if (shown > 0 && shown < captioned.Count)
            problems.Add($"{where}: captions on {shown} of {captioned.Count} names");
        if (ring.Select(l => l.Text?.Size).OfType<double>().Distinct().Count() > 1)
            problems.Add($"{where}: name sizes {string.Join(", ", ring.Select(l => l.Text?.Size).OfType<double>().Distinct())}");
        if (ring.Where(l => l.IconSize > 0).Select(l => l.IconSize).Distinct().Count() > 1)
            problems.Add($"{where}: icon sizes {string.Join(", ", ring.Where(l => l.IconSize > 0).Select(l => l.IconSize).Distinct())}");
        for (var i = 0; i < ring.Count; i++)
        {
            var label = ring[i];
            var name = items[i].Label;
            if (label.Kind == SliceLabelKind.None && name.Length > 0)
                problems.Add($"{where}: \"{name}\" shows nothing");
            if (label.Kind == SliceLabelKind.IconAndText && label.Text?.Caption is not null)
                problems.Add($"{where}: \"{name}\" has an icon and a caption");
            // A format's name (no icon to fall back on) is shortened as far as it takes on the smallest donuts, as
            // Text only is: the centre names it in full.
            if (label.Text is { Whole: false } cut && show != LabelShow.TextOnly && items[i].IconKey is not null)
            {
                var kept = new System.Globalization.StringInfo(cut.Lines[0].TrimEnd('…').TrimEnd()).LengthInTextElements;
                if (kept < SliceLabelLayout.MinKept(name) || (show == LabelShow.Auto && items[i].IconKey is not null))
                    problems.Add($"{where}: \"{name}\" cut to \"{cut.Lines[0]}\"");
            }
            if (label.Text is { } text)
            {
                if (!label.Zone.Contains(text.Box))
                    problems.Add($"{where}: \"{name}\" leaves its zone");
                var lines = string.Join(" ", text.Lines);
                if (text.Whole && !name.Any(DonutMenu.IsHanOrKana) && lines != string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                    problems.Add($"{where}: \"{name}\" drawn as \"{string.Join(" | ", text.Lines)}\"");
            }
            if (label.IconSize > 0 && !label.Zone.Contains(label.IconCenter, SliceLabel.IconReach(label.IconSize)))
                problems.Add($"{where}: the icon of \"{name}\" leaves its zone");
        }
    }
}
