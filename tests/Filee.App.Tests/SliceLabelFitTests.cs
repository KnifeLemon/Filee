// Every built-in donut, front and back and every group's ring (at three ring sizes and spreads), at the smallest,
// default and largest donut, at Label scale 80 / 100 / 160 %, in the four Show modes, with Show output format on and
// off, and every language: each label stays inside its slice's safe zone, a whole name keeps its words whole (Chinese
// and Japanese may break between characters), and a ring has one name size, one icon size and one direction for its
// names. And: Auto shows icons on all of a ring's names or on none, Icon & text keeps 60 % of a shortened name's
// letters at least, a slice that needs its caption never shows its icon alone (but in Icon only), slices of one name
// look alike, and the face's names are 10 px × the donut's size at least (never under 10 px, or as small as the Label
// scale asks). Layout only, nothing is drawn.

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

    // One case per language: each runs in well under CI's five-minute hang limit, and a failure names its language.
    public static TheoryData<string> AppLanguageCodes => [.. AppLanguages.All.Select(l => l.Code)];

    [AvaloniaTheory]
    [MemberData(nameof(AppLanguageCodes))]
    public void Every_label_of_every_built_in_donut_fits_its_slice_in_every_language(string code)
    {
        var problems = new List<string>();
        try
        {
            foreach (var language in new[] { code })
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
                            foreach (var captions in new[] { true, false })
                            {
                                donut.ShowCaptions = captions;
                                donut.OuterRadius = radius;
                                donut.LabelScale = scale;
                                donut.DefaultShow = show;
                                (donut.GroupRingSize, donut.GroupRingSpread) = ring;
                                var (face, rings) = donut.PlanLabels(bold, regular);
                                var where = $"{language} {profile.Id} {faceName} r{radius} ×{scale} {show}{(captions ? "" : " off")} ring {ring.Size}/{ring.Spread}";
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

    // The English faces as a file of each donut's own format shows them: "Same as source" presets captioned with that
    // format, as the live donut does.
    private static List<(string Face, List<DonutItem> Items, IReadOnlyList<SliceLabel> Labels)> EnglishFaces(LabelShow show = LabelShow.Auto)
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var store = AppHost.Get<UserDataStore>();
        var family = SoftPalette.FontFamilyFor("en");
        var bold = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
        var regular = new Typeface(family);
        var faces = new List<(string, List<DonutItem>, IReadOnlyList<SliceLabel>)>();
        foreach (var profile in BuiltInData.CreateProfiles())
            foreach (var (faceName, ids) in new[] { ("front", profile.PresetIds), ("back", profile.BackPresetIds ?? []) })
            {
                var format = (profile.Extensions.FirstOrDefault() ?? "pdf").ToUpperInvariant();
                var items = Items(ids, store, loc).Select(i => i.Caption != "JPG/PNG" ? i : new DonutItem
                {
                    Id = i.Id,
                    Label = i.Label,
                    Caption = format,
                    FullName = i.FullName,
                    IconKey = i.IconKey,
                    KeepsCaption = i.KeepsCaption,
                    Children = i.Children,
                }).ToList();
                if (items.Count == 0)
                    continue;
                var donut = new DonutMenu { Items = items, ShowCaptions = true, DefaultShow = show };
                faces.Add(($"{profile.Id} {faceName}", items, donut.PlanLabels(bold, regular).Face));
            }
        return faces;
    }

    [AvaloniaFact]
    public void English_names_sit_on_their_slices_middle_as_today()
    {
        // Every name of every English face, with its caption, its letters centred on its slice's middle (today's donut
        // draws them there; the safe zone once kept 10 px or more round them and pushed some toward the rim).
        var off = EnglishFaces().SelectMany(f => f.Labels.Select((l, i) => (f.Face, Name: f.Items[i].Label, Label: l)))
            .Where(x => x.Label.Text is not null && !SliceLabelLayout.OnMiddle(x.Label))
            .Select(x => $"{x.Face}: \"{x.Name}\"").ToList();
        Assert.True(off.Count == 0, string.Join("\n", off));
    }

    [AvaloniaFact]
    public void English_names_keep_the_captions_today_shows()
    {
        // As today: "Remove metadata" with PDF under it on the PDF back face, "Compress image" with PNG on the image
        // back face, "Crop video" with "Group of 6" beside its chevron (once kept off a band across the slice).
        var lost = EnglishFaces().SelectMany(f => f.Labels.Select((l, i) => (f.Face, Item: f.Items[i], Label: l)))
            .Where(x => x.Label.Text is { Caption: null } && x.Item.Caption is { Length: > 0 } c &&
                        !string.Equals(c, x.Item.Label, StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Face}: \"{x.Item.Label}\" without \"{x.Item.Caption}\"").ToList();
        Assert.True(lost.Count == 0, string.Join("\n", lost));
    }

    [AvaloniaFact]
    public void Auto_shows_icons_only_where_they_cost_nothing()
    {
        // The image back face has room in every slice for icon, name and caption, centred: icons. The PDF back face
        // has room for icon and name in every slice, but not for everything each shows without its icon: names, with
        // their captions, as today.
        var faces = EnglishFaces();
        var (_, _, images) = faces.Single(f => f.Face == "images back");
        Assert.All(images, l => Assert.Equal(SliceLabelKind.IconAndText, l.Kind));
        Assert.All(images, l => Assert.NotNull(l.Text!.Caption));
        var (_, items, pdf) = faces.Single(f => f.Face == "pdf back");
        Assert.All(pdf.Where((_, i) => items[i].IconKey is not null), l => Assert.Equal(SliceLabelKind.Text, l.Kind));
    }

    // The slices as the live donut gives them (RadialViewModel.ItemOf), without checking them against files.
    internal static List<DonutItem> Items(IEnumerable<string> ids, UserDataStore store, ILocalizer loc, LabelDirection? direction = null)
    {
        DonutItem ItemOf(Preset p, bool ring) => new()
        {
            Id = p.Id,
            Label = ring ? loc.RingName(p) : loc.DonutName(p),
            Caption = ring ? null : p.TargetFormat == BuiltInData.SameAsSource ? "JPG/PNG" : FormatLabels.NameOf(loc, p.TargetFormat),
            FullName = loc.DisplayName(p),
            IconKey = SliceIcons.For(p),
            Direction = direction,
            KeepsCaption = !ring && p.IsCopyToFolder,
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
                Direction = direction,
            };
        }).ToList();
    }

    private static void Check(IReadOnlyList<SliceLabel> ring, IReadOnlyList<DonutItem> items, string where, List<string> problems,
        LabelShow show)
    {
        // Slices of one name look alike: the caption is all that tells them apart.
        foreach (var same in Enumerable.Range(0, ring.Count).Where(i => items[i].IconKey is not null && items[i].Label.Length > 0)
                     .GroupBy(i => items[i].Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            if (same.Select(i => ring[i].Kind).Distinct().Count() > 1)
                problems.Add($"{where}: \"{same.Key}\" shown as {string.Join(", ", same.Select(i => ring[i].Kind))}");
        // Auto: icons on all of the ring's names or on none (an icon alone, the last resort, aside).
        if (show == LabelShow.Auto && ring.Where((l, i) => items[i].IconKey is not null && items[i].Label.Length > 0 &&
                l.Kind is SliceLabelKind.Text or SliceLabelKind.IconAndText).Select(l => l.Kind).Distinct().Count() > 1)
            problems.Add($"{where}: Auto mixes icons and names alone");
        // One direction for the ring's names: straight, along their slices, bent along the ring or level with it (two
        // can look alike on some slices). A preset's own direction keeps its slice out of it.
        var ways = new HashSet<string> { "straight", "along", "bent", "level" };
        for (var i = 0; i < ring.Count; i++)
        {
            if (ring[i].Text is not { } t || (items[i].Direction ?? LabelDirection.Auto) != LabelDirection.Auto)
                continue;
            bool Is(double turn) => Math.Abs(Math.IEEERemainder(t.Rotation - turn, 2 * Math.PI)) < 1e-6;
            var could = new HashSet<string>();
            if (t.Curved)
                could.Add("bent");
            else
            {
                if (Is(0)) could.Add("straight");
                if (Is(SliceLabelLayout.Along(ring[i].Zone.Mid))) could.Add("along");
                if (Is(SliceLabelLayout.Across(ring[i].Zone.Mid))) could.Add("level");
            }
            ways.IntersectWith(could);
        }
        if (ways.Count == 0)
            problems.Add($"{where}: names run in different directions");
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
            var needs = items[i].KeepsCaption ||
                        items.Any(o => !ReferenceEquals(o, items[i]) && string.Equals(o.Label, name, StringComparison.OrdinalIgnoreCase));
            if (label.Kind == SliceLabelKind.Icon && show != LabelShow.IconOnly && needs)
                problems.Add($"{where}: \"{name}\" shows its icon alone without the caption it needs");
            // Icon & text shortens a name beside its icon to MinKept at most; Auto and Text only shorten a name alone
            // further only where nothing else fits (the centre names the slice in full).
            // Its letters on every line count ("Remove / metad…"), spaces and line breaks not.
            if (label is { Kind: SliceLabelKind.IconAndText, Text: { Whole: false } cut })
            {
                static List<string> Elements(string s)
                {
                    var list = new List<string>();
                    var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
                    while (e.MoveNext())
                        list.Add(e.GetTextElement());
                    return list;
                }
                static int Letters(IEnumerable<string> elements) => elements.Count(e => !string.IsNullOrWhiteSpace(e));
                var kept = Letters(Elements(string.Concat(cut.Lines).TrimEnd().TrimEnd('…')));
                var need = Letters(Elements(name.Trim()).Take(SliceLabelLayout.MinKept(name)));
                if (kept < need)
                    problems.Add($"{where}: \"{name}\" cut to \"{string.Join(" / ", cut.Lines)}\" beside its icon");
            }
            if (label.Text is { } text)
            {
                if (!text.Inside(label.Zone))
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
