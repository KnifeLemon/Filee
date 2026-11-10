// How slice labels fit their slices: the safe zone (inset on every side, clear of a group's chevron, the inner edge
// curving in), names wrapped at word breaks and never split (except Chinese and Japanese), drawn straight or along the
// slice, icons above names or alone and centred, and one size and one style per ring (Show, Auto).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class SliceLabelLayoutTests
{
    private static readonly Point Center = new(300, 300);

    // The default donut: radius 150, hole 42 %, 12 slices.
    private const double Outer = 150, Inner = 63, Gap = 3;

    private static SliceShape Shape(int index, int count, double outerKeepOut = 0) =>
        new(Center, Inner, Outer, DonutGeometry.MidAngle(index, count), DonutGeometry.Span(count), Gap, outerKeepOut);

    // The app's own font (Noto Sans): the headless test font's lines are shorter, which would make names fit that don't.
    private static SliceLabelLayout Layout()
    {
        TestServices.EnsureInitialized("en");
        var family = SoftPalette.FontFamilyFor("en");
        return new SliceLabelLayout(new LabelFonts(new Typeface(family, FontStyle.Normal, FontWeight.SemiBold), new Typeface(family),
            FlowDirection.LeftToRight));
    }

    private static SliceLabelInput Slice(string name, int index, int count, LabelShow show = LabelShow.TextOnly,
        string? icon = null, string? caption = null) =>
        new(name, caption, icon, show, Shape(index, count));

    // ───────────────────────── The safe zone ─────────────────────────

    [Fact]
    public void The_zone_is_the_slice_inset_on_every_side()
    {
        var zone = SafeZone.Of(Shape(0, 12), scale: 1);
        var margin = 0.08 * (Outer - Inner); // 6.96: more than the 6 px floor
        Assert.Equal(Inner + margin, zone.Inner, 6);
        Assert.Equal(Outer - margin, zone.Outer, 6);
        Assert.Equal(margin + Gap / 2, zone.Side, 6);

        // A thin ring: the 6 px floor, scaled with the donut.
        var thin = SafeZone.Of(new SliceShape(Center, 100, 140, 0, 0.5, Gap), scale: 1.6);
        Assert.Equal(100 + 6 * 1.6, thin.Inner, 6);
    }

    [Fact]
    public void A_group_slice_keeps_clear_of_its_chevron()
    {
        var plain = SafeZone.Of(Shape(0, 12), 1);
        var group = SafeZone.Of(Shape(0, 12, outerKeepOut: 15.5), 1);
        Assert.Equal(Outer - 15.5, group.Outer, 6);
        Assert.True(group.Outer < plain.Outer);
    }

    [Fact]
    public void A_box_whose_corners_are_inside_but_whose_edge_dips_past_the_inner_edge_does_not_fit()
    {
        // A wide, flat box at 12 o'clock whose bottom edge is 1 px inside the hole at its middle: its corners, further
        // out, are in the zone, but the inner edge curves into the box between them.
        var zone = SafeZone.Of(new SliceShape(Center, 63, 150, 0, Math.PI, Gap), 1);
        var d = zone.Inner - 1;
        var box = new LabelBox(new Point(Center.X, Center.Y - d - 2), 60, 4, 0);
        Assert.True(zone.Contains(new Point(Center.X - 30, Center.Y - d)));
        Assert.True(zone.Contains(new Point(Center.X + 30, Center.Y - d)));
        Assert.False(zone.Contains(box));
        Assert.True(zone.Contains(box with { Center = new Point(Center.X, Center.Y - d - 12) }));

        // Dipping in only a fifth of a pixel, off the box's middle: every point a sample of its edge might take (each
        // sixth of it) is 6 px across from the dip and outside the hole; the edge itself isn't.
        var shallow = zone.Inner - 0.2;
        var off = new LabelBox(new Point(Center.X + 6, Center.Y - shallow - 2), 72, 4, 0);
        Assert.True(zone.Contains(new Point(Center.X + 6, Center.Y - shallow)));
        Assert.True(zone.Contains(new Point(Center.X - 6, Center.Y - shallow)));
        Assert.False(zone.Contains(off));
    }

    [Fact]
    public void Nothing_near_a_side_edge_fits()
    {
        var zone = SafeZone.Of(Shape(0, 12), 1);
        var edge = DonutGeometry.PointAt(Center, 100, -Math.PI / 12 + 0.01); // just inside the slice's left edge
        Assert.False(zone.Contains(edge));
        Assert.True(zone.Contains(DonutGeometry.PointAt(Center, 100, 0)));
    }

    // ───────────────────────── Words, lines and direction ─────────────────────────

    [AvaloniaTheory]
    [InlineData("Merge images", 1.0)]
    [InlineData("Grayscale", 1.0)]
    [InlineData("Remove metadata", 0.8)]   // at 100 % its words are too wide for a full donut: icons (Auto) or "…"
    [InlineData("Remove background", 0.8)]
    public void Names_fit_whole_on_every_slice_of_a_full_donut(string name, double labelScale)
    {
        var layout = Layout();
        var size = SliceLabelLayout.NameSizeFor(labelScale, 1);
        var fits = Enumerable.Range(0, 12).Count(i =>
            layout.FitText(SafeZone.Of(Shape(i, 12), 1), name, null, size, 10.5, alongToo: true) is { Whole: true });
        Assert.Equal(12, fits);
    }

    [AvaloniaFact]
    public void A_name_with_a_word_too_wide_for_a_full_donut_at_100_percent_doesnt_fit_whole()
    {
        var layout = Layout();
        Assert.All(Enumerable.Range(0, 12), i =>
            Assert.Null(layout.FitText(SafeZone.Of(Shape(i, 12), 1), "Remove background", null, 13, 10.5, alongToo: true)));
        Assert.NotNull(layout.FitText(SafeZone.Of(Shape(0, 6), 1), "Remove background", null, 13, 10.5, alongToo: true));
    }

    [AvaloniaFact]
    public void A_word_is_never_split()
    {
        var layout = Layout();
        var block = layout.FitText(SafeZone.Of(Shape(3, 12), 1), "Trim transparent borders", null, 13, 10.5, alongToo: true);
        if (block is not null)
            Assert.All(block.Lines, line => Assert.All(line.Split(' '), w => Assert.Contains(w, new[] { "Trim", "transparent", "borders" })));
        // Too long a word for any slice: no layout rather than a split one.
        Assert.Null(layout.FitText(SafeZone.Of(Shape(0, 12), 1), "Supercalifragilisticexpialidocious", null, 13, 10.5, alongToo: true));
    }

    [Fact]
    public void Chinese_and_Japanese_break_between_characters_and_Korean_keeps_its_words()
    {
        Assert.Equal(4, SliceLabelLayout.Units("去除背景").Count);
        Assert.Equal(["背", "景"], SliceLabelLayout.Units("背景").Select(u => u.Text));
        Assert.Equal(["ZIP", "하나로"], SliceLabelLayout.Units("ZIP 하나로").Select(u => u.Text));
        Assert.Equal([false, true], SliceLabelLayout.Units("Merge PDF").Select(u => u.Space));
    }

    [AvaloniaFact]
    public void Lines_are_split_so_the_widest_is_as_narrow_as_can_be()
    {
        var layout = Layout();
        var lines = layout.Split(SliceLabelLayout.Units("Trim transparent borders"), 2, 13);
        Assert.Equal(["Trim transparent", "borders"], lines);
        Assert.Equal(["Trim", "transparent", "borders"], layout.Split(SliceLabelLayout.Units("Trim transparent borders"), 3, 13));
    }

    [Fact]
    public void Text_along_a_slice_is_never_upside_down()
    {
        // Upright: the text's reading direction never points left (cos ≥ 0), all round the donut.
        for (var mid = 0.0; mid < 2 * Math.PI; mid += 0.05)
            Assert.True(Math.Cos(SliceLabelLayout.Along(mid)) >= -1e-9, $"upside down at {mid:F2}");
        // It runs along the slice: at right angles to the straight direction everywhere but 3 and 9 o'clock.
        Assert.Equal(0, Math.Sin(SliceLabelLayout.Along(Math.PI / 2)), 6);
        Assert.Equal(1, Math.Abs(Math.Sin(SliceLabelLayout.Along(Math.PI))), 6);
    }

    [AvaloniaFact]
    public void Straight_wins_when_both_directions_fit()
    {
        var block = Layout().FitText(SafeZone.Of(Shape(0, 6), 1), "Crop", null, 13, 10.5, alongToo: true)!;
        Assert.Equal(0, block.Rotation);
        Assert.Single(block.Lines);
    }

    [AvaloniaFact]
    public void A_name_too_long_to_fit_is_shortened_on_one_line_where_allowed()
    {
        var block = Layout().FitShortened(SafeZone.Of(Shape(3, 12), 1), "Trim transparent borders", 13, alongToo: true)!;
        Assert.False(block.Whole);
        Assert.EndsWith("…", Assert.Single(block.Lines));
    }

    // ───────────────────────── Rings ─────────────────────────

    [AvaloniaFact]
    public void Every_name_on_a_ring_is_one_size_and_inside_its_zone()
    {
        string[] names = ["Crop", "Rotate", "Flip", "Remove background", "Trim transparent borders", "Grayscale", "AVIF",
            "Merge images", "Merge PDF", "Compress image", "Remove metadata", "Actions"];
        var plan = Layout().PlanRing(names.Select((n, i) => Slice(n, i, 12)).ToList(), labelScale: 1, scale: 1, showCaptions: true);
        Assert.Single(plan.Where(l => l.Text is not null).Select(l => l.Text!.Size).Distinct());
        Assert.All(plan, l => Assert.True(l.Text is null || l.Zone.Contains(l.Text.Box)));
    }

    [AvaloniaFact]
    public void Auto_shows_one_style_for_the_whole_ring()
    {
        var layout = Layout();
        // Six roomy slices with short names and icons: icon and name on every one.
        string[] six = ["Crop", "Rotate", "Flip", "AVIF", "Merge PDF", "Actions"];
        var roomy = layout.PlanRing(six.Select((n, i) => Slice(n, i, 6, LabelShow.Auto, "crop")).ToList(), 1, 1, false);
        Assert.All(roomy, l => Assert.Equal(SliceLabelKind.IconAndText, l.Kind));

        // Twelve slices where one name can't fit whole: no slice shows its name, every one its icon.
        string[] twelve = ["Crop", "Rotate", "Flip", "Remove background", "Trim transparent borders", "Grayscale", "AVIF",
            "Merge images", "Merge PDF", "Compress image", "Remove metadata", "Actions"];
        var full = layout.PlanRing(twelve.Select((n, i) => Slice(n, i, 12, LabelShow.Auto, "crop")).ToList(), 1, 1, false);
        Assert.Single(full.Select(l => l.Kind).Distinct());
    }

    [AvaloniaFact]
    public void Auto_slices_without_an_icon_show_their_name_and_explicit_slices_keep_their_style()
    {
        var layout = Layout();
        var plan = layout.PlanRing(
        [
            Slice("Trim transparent borders", 0, 12, LabelShow.Auto, "crop-free"),
            Slice("PNG", 1, 12, LabelShow.Auto),
            Slice("Crop", 2, 12, LabelShow.TextOnly, "crop"),
            Slice("Crop", 3, 12, LabelShow.IconOnly, "crop"),
        ], 1, 1, false);
        Assert.Equal(SliceLabelKind.Icon, plan[0].Kind);       // its name can't fit whole: the ring's Auto slices go to icons
        Assert.Equal(SliceLabelKind.Text, plan[1].Kind);       // no icon: its name
        Assert.Equal(SliceLabelKind.Text, plan[2].Kind);       // Text only stays text
        Assert.Equal(SliceLabelKind.Icon, plan[3].Kind);       // Icon only stays an icon
        Assert.Equal(plan[0].IconSize, plan[3].IconSize);      // one icon size for the ring
    }

    [AvaloniaFact]
    public void An_icon_alone_is_centred_and_an_icon_with_a_name_sits_above_it_upright()
    {
        var layout = Layout();
        var alone = layout.PlanRing([Slice("Crop", 2, 6, LabelShow.IconOnly, "crop")], 1, 1, false)[0];
        var zone = alone.Zone;
        Assert.Equal(zone.At((zone.Inner + zone.Outer) / 2), alone.IconCenter);

        var both = layout.PlanRing([Slice("Crop", 2, 6, LabelShow.IconAndText, "crop")], 1, 1, false)[0];
        Assert.Equal(SliceLabelKind.IconAndText, both.Kind);
        Assert.Equal(0, both.Text!.Rotation);
        Assert.True(both.IconCenter.Y < both.Text.Anchor.Y);
        Assert.Equal(both.IconCenter.X, both.Text.Anchor.X, 6);
    }

    [AvaloniaFact]
    public void A_group_ring_follows_the_label_scale_as_far_as_its_slices_have_room()
    {
        // A group's ring at the default donut: a 46 px band, three slices of about 0.26 rad (some 30 px across).
        var layout = Layout();
        const double inner = Outer + 13, outer = inner + 46;
        var span = DonutGeometry.SubSpan(DonutGeometry.Span(12), 3, (inner + outer) / 2);
        SliceLabelInput Ring(string name, int k, LabelShow show) =>
            new(name, null, "rotate-left", show, new SliceShape(Center, inner, outer, DonutGeometry.SubMidAngle(0.3, k, 3, span), span, Gap));
        foreach (var show in Enum.GetValues<LabelShow>())
        {
            List<SliceLabelInput> ring = [Ring("Left", 0, show), Ring("Right", 1, show), Ring("180°", 2, show)];
            // At 160 % the names don't fit whole at 20.8 px (icons do): a text ring's names step down (PlanRing does
            // that itself now, in 0.5 px steps) until every slice shows its whole name; any ring ends with every slice
            // showing its name or icon.
            if (show == LabelShow.TextOnly)
                Assert.All(layout.PlanRing(ring, 1.6, 1, false), l => Assert.True(l.NameWhole && l.Text!.Size < 13 * 1.6));
            var fitted = layout.PlanRingToFit(ring, 1.6, 1, false);
            Assert.All(fitted, l => Assert.True(l.Kind == SliceLabelKind.Icon || l.NameWhole, show.ToString()));
            var size = fitted.Select(l => l.Text?.Size ?? 0).Max();
            Assert.True(size is 0 or (> 13 and < 13 * 1.6), $"{show}: {size}");
            // At 100 % (or less) it is what PlanRing gives: no step below the scale asked for.
            Assert.Equal(layout.PlanRing(ring, 0.8, 1, false).Select(l => (l.Kind, l.Text?.Size, l.IconSize)),
                layout.PlanRingToFit(ring, 0.8, 1, false).Select(l => (l.Kind, l.Text?.Size, l.IconSize)));
        }
        // Room enough: the scale asked for.
        List<SliceLabelInput> roomy = [Slice("Crop", 0, 6, LabelShow.TextOnly)];
        Assert.Equal(13 * 1.6, layout.PlanRingToFit(roomy, 1.6, 1, false)[0].Text!.Size, 6);
    }

    [AvaloniaFact]
    public void Sizes_follow_the_label_scale_and_the_donut_with_a_floor()
    {
        Assert.Equal(13, SliceLabelLayout.NameSizeFor(1, 1));
        Assert.Equal(26, SliceLabelLayout.NameSizeFor(1.25, 1.6), 6);
        Assert.Equal(10, SliceLabelLayout.NameSizeFor(0.8, 100 / 150.0)); // 6.9 → the 10 px floor
        Assert.Equal(10.5, SliceLabelLayout.CaptionSizeFor(13));
    }

    [AvaloniaFact]
    public void A_caption_is_dropped_before_the_name_is()
    {
        var layout = Layout();
        var roomy = layout.PlanRing([Slice("Crop", 0, 6, caption: "JPG")], 1, 1, showCaptions: true)[0];
        Assert.Equal("JPG", roomy.Text!.Caption);
        var off = layout.PlanRing([Slice("Crop", 0, 6, caption: "JPG")], 1, 1, showCaptions: false)[0];
        Assert.Null(off.Text!.Caption);
        var same = layout.PlanRing([Slice("PNG", 0, 6, caption: "png")], 1, 1, showCaptions: true)[0];
        Assert.Null(same.Text!.Caption);
    }

    [AvaloniaFact]
    public void A_name_beside_an_icon_keeps_most_of_its_letters_or_the_whole_ring_shows_icons()
    {
        var layout = Layout();
        foreach (var count in new[] { 6, 12 })
        {
            var plan = layout.PlanRing(Enumerable.Range(0, count)
                .Select(i => Slice("Open in Terminal and more words", i, count, LabelShow.IconAndText, "console")).ToList(), 1, 1, false);
            var kind = Assert.Single(plan.Select(l => l.Kind).Distinct());
            Assert.Contains(kind, new[] { SliceLabelKind.IconAndText, SliceLabelKind.Icon });
            foreach (var label in plan)
                if (label.Text is { Whole: false } text)
                    Assert.True(Kept(text) >= SliceLabelLayout.MinKept("Open in Terminal and more words"), text.Lines[0]);
            Assert.Single(plan.Where(l => l.IconSize > 0).Select(l => l.IconSize).Distinct());
        }
    }

    // ───────────────────────── One style and size per ring, never a stub ─────────────────────────

    // The Images donut's back face, as shipped: every slice has an icon but AVIF (a format).
    private static readonly string[] Back =
        ["Crop", "Rotate", "Flip", "Remove background", "Trim transparent borders", "Grayscale", "AVIF", "Merge images",
            "Merge PDF", "Compress image", "Remove metadata", "One ZIP"];
    private static readonly string[] Six = ["Crop", "Rotate", "Flip", "Remove background", "Grayscale", "Compress image"];

    private static List<SliceLabelInput> Face(string[] names, LabelShow show) => names.Select((n, i) =>
        Slice(n, i, names.Length, show, n == "AVIF" ? null : "crop", n == "Rotate" ? "Group of 3" : n == "Remove background" ? "PNG" : "JPG")).ToList();

    private static int Kept(LabelText text) => new System.Globalization.StringInfo(text.Lines[0].TrimEnd('…').TrimEnd()).LengthInTextElements;

    [Fact]
    public void A_shortened_name_keeps_sixty_percent_of_its_letters_and_every_word_but_the_last()
    {
        Assert.Equal(19, SliceLabelLayout.MinKept("Trim transparent borders")); // "Trim transparent bo…", not "Tri…"
        Assert.Equal(11, SliceLabelLayout.MinKept("Compress image"));           // "Compress im…", not "Compres…"
        Assert.Equal(6, SliceLabelLayout.MinKept("PDF 병합"));                  // all of it: "PDF…" would drop a word
        Assert.Equal(3, SliceLabelLayout.MinKept("トリミング"));                // 60 % of five characters
    }

    [AvaloniaTheory]
    [InlineData(LabelShow.Auto)]
    [InlineData(LabelShow.IconAndText)]
    public void No_ring_shows_a_stub_or_mixes_styles(LabelShow show)
    {
        var layout = Layout();
        foreach (var names in new[] { Back, Six })
            foreach (var labelScale in new[] { 0.8, 1, 1.3, 1.6 })
            {
                var slices = Face(names, show);
                var plan = layout.PlanRing(slices, labelScale, 1, showCaptions: true);
                var where = $"{names.Length} slices ×{labelScale} {show}";
                // The slices with an icon show one style; one without shows its name.
                Assert.Single(plan.Where((_, i) => slices[i].IconKey is not null).Select(l => l.Kind).Distinct());
                Assert.All(plan.Where((_, i) => slices[i].IconKey is null), l => Assert.Equal(SliceLabelKind.Text, l.Kind));
                for (var i = 0; i < plan.Count; i++)
                    if (plan[i].Text is { Whole: false } text)
                    {
                        Assert.True(show == LabelShow.IconAndText, $"{where}: Auto shortened \"{names[i]}\"");
                        Assert.True(Kept(text) >= SliceLabelLayout.MinKept(names[i]), $"{where}: \"{text.Lines[0]}\"");
                    }
            }
    }

    [AvaloniaFact]
    public void An_icon_stands_in_for_the_caption_and_names_alone_show_captions_on_all_or_none()
    {
        var layout = Layout();
        foreach (var show in new[] { LabelShow.Auto, LabelShow.IconAndText, LabelShow.TextOnly })
            foreach (var names in new[] { Back, Six })
            {
                var plan = layout.PlanRing(Face(names, show), 1, 1, showCaptions: true);
                Assert.All(plan.Where(l => l.Kind == SliceLabelKind.IconAndText), l => Assert.Null(l.Text!.Caption));
                // AVIF's caption is its own name: never shown. Every other name alone shows its caption, or none does.
                var alone = plan.Where((l, i) => l.Kind == SliceLabelKind.Text && names[i] != "AVIF").ToList();
                var captions = alone.Count(l => l.Text!.Caption is not null);
                Assert.True(captions == 0 || captions == alone.Count, $"{names.Length} {show}: {captions} of {alone.Count}");
            }
        // Room for them: names alone with their captions.
        var six = layout.PlanRing(Face(Six, LabelShow.TextOnly), 0.8, 1, showCaptions: true);
        Assert.All(six, l => Assert.NotNull(l.Text!.Caption));
    }

    [AvaloniaFact]
    public void A_ring_steps_its_names_down_to_eleven_px_times_the_donut_size_and_never_under_ten()
    {
        Assert.Equal([13, 12.5, 12, 11.5, 11], SliceLabelLayout.SizesFor(13, 1));
        Assert.Equal(16.5, SliceLabelLayout.SizesFor(20.8, 1.5)[^1]);         // 11 × 1.5
        Assert.Equal(10, SliceLabelLayout.SizesFor(13, 0.7)[^1]);             // 11 × 0.7 = 7.7 → never under 10
        Assert.Equal([10.4], SliceLabelLayout.SizesFor(10.4, 1));             // already under the floor: as it is

        var layout = Layout();
        foreach (var show in new[] { LabelShow.Auto, LabelShow.IconAndText, LabelShow.TextOnly })
            foreach (var radius in new[] { 150.0, 220 })
            {
                var scale = radius / SliceLabelLayout.DefaultRadius;
                var slices = Face(Back, show).Select(s => s with
                {
                    Shape = s.Shape with { Inner = Inner * scale, Outer = radius },
                }).ToList();
                var plan = layout.PlanRing(slices, 1, scale, showCaptions: false);
                var size = Assert.Single(plan.Where(l => l.Text is not null).Select(l => l.Text!.Size).Distinct());
                Assert.InRange(size, Math.Max(10, 11 * scale) - 1e-9, 13 * scale + 1e-9);
            }
    }

    [AvaloniaFact]
    public void A_ring_runs_all_its_names_one_way()
    {
        // A group's ring at the default donut, Group ring size 150 %: "Right" alone fits only along its slice; the ring's
        // other names then run along theirs too, rather than straight beside it.
        var layout = Layout();
        foreach (var (inner, depth) in new[] { (Outer + 13, 46.0), (Outer + 19.5, 69.0) })
        {
            var outer = inner + depth;
            var span = DonutGeometry.SubSpan(DonutGeometry.Span(12), 3, (inner + outer) / 2);
            var ring = new[] { "Left", "Right", "180°" }.Select((n, k) => new SliceLabelInput(n, null, "rotate-left", LabelShow.TextOnly,
                new SliceShape(Center, inner, outer, DonutGeometry.SubMidAngle(0.3, k, 3, span), span, Gap))).ToList();
            foreach (var scale in new[] { 1.0, 1.5 })
            {
                var plan = layout.PlanRing(ring, 1, scale, false);
                var straight = plan.Select(l => Math.Abs(l.Text!.Rotation) < 1e-6).Distinct().ToList();
                Assert.Single(straight);
            }
        }
        foreach (var names in new[] { Back, Six })
            foreach (var labelScale in new[] { 0.8, 1, 1.3, 1.6 })
            {
                var plan = layout.PlanRing(Face(names, LabelShow.TextOnly), labelScale, 1, true);
                var kinds = plan.Where(l => l.Text is not null && Math.Abs(Math.Cos(SliceLabelLayout.Along(l.Zone.Mid))) < 0.99)
                    .Select(l => Math.Abs(l.Text!.Rotation) < 1e-6).Distinct().ToList();
                Assert.True(kinds.Count <= 1, $"{names.Length} slices ×{labelScale}");
            }
    }

    [AvaloniaFact]
    public void Auto_shows_icon_and_name_on_the_six_slice_face_at_100_percent()
    {
        var plan = Layout().PlanRing(Face(Six, LabelShow.Auto), 1, 1, showCaptions: true);
        Assert.All(plan, l =>
        {
            Assert.Equal(SliceLabelKind.IconAndText, l.Kind);
            Assert.True(l.NameWhole);
        });
        Assert.InRange(plan[0].Text!.Size, 11, 13); // a step down from 13 px is allowed, 11 px at most
    }

    [AvaloniaFact]
    public void Icon_and_text_on_the_twelve_slice_face_shows_icons_alone_rather_than_cut_names()
    {
        var slices = Face(Back, LabelShow.IconAndText);
        var plan = Layout().PlanRing(slices, 1, 1, showCaptions: true);
        for (var i = 0; i < plan.Count; i++)
            Assert.Equal(slices[i].IconKey is null ? SliceLabelKind.Text : SliceLabelKind.Icon, plan[i].Kind);
        Assert.True(plan[Array.IndexOf(Back, "AVIF")].NameWhole);
    }

    [AvaloniaFact]
    public void A_donut_lays_its_labels_out_once_not_on_every_frame()
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu
        {
            Items = Enumerable.Range(0, 12).Select(i => new DonutItem { Id = $"s{i}", Label = $"Slice {i}", IconKey = "crop" }).ToList(),
            SliceBrush = Brushes.White,
            CenterBrush = Brushes.White,
            Foreground = Brushes.Black,
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        void Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                donut.Advance(1 / 60.0);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }
        Frames(30);
        using (window.CaptureRenderedFrame()) { }
        var made = donut.PlansMade;
        Assert.True(made >= 1);
        Frames(30);
        using (window.CaptureRenderedFrame()) { }
        Assert.Equal(made, donut.PlansMade);

        donut.LabelScale = 1.25; // a setting changes: laid out again
        Frames(2);
        using (window.CaptureRenderedFrame()) { }
        Assert.True(donut.PlansMade > made);
        window.Close();
    }
}
