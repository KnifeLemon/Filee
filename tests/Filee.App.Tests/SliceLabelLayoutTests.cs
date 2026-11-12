// How slice labels fit their slices: the safe zone (inset on every side, clear of a group's chevron, the inner edge
// curving in), names wrapped at word breaks and never split (except Chinese and Japanese), drawn straight or along the
// slice's longer side (out from the centre, or level with a group ring), icons above names or alone and centred, one
// size per ring, and in Auto one style per ring (Show).

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

    // The default donut's chevron: 9.25 px in from the outer edge, reaching 6.2 px round that (DonutMenu.ChevronIn).
    private const double ChevronIn = 9.25, ChevronReach = 6.18;

    private static SliceShape Shape(int index, int count, bool group = false) =>
        new(Center, Inner, Outer, DonutGeometry.MidAngle(index, count), DonutGeometry.Span(count), Gap,
            group ? ChevronIn : 0, group ? ChevronReach : 0);

    // The app's own font (Noto Sans): the headless test font's lines are shorter, which would make names fit that
    // don't.
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
        var margin = SafeZone.MarginShare * (Outer - Inner); // 2.2: more than the 2 px floor
        Assert.True(margin > SafeZone.MinMargin);
        Assert.Equal(Inner + margin, zone.Inner, 6);
        Assert.Equal(Outer - margin, zone.Outer, 6);
        Assert.Equal(margin + Gap / 2, zone.Side, 6);

        // A thin ring: the floor, scaled with the donut.
        var thin = SafeZone.Of(new SliceShape(Center, 100, 140, 0, 0.5, Gap), scale: 1.6);
        Assert.Equal(100 + SafeZone.MinMargin * 1.6, thin.Inner, 6);
    }

    [Fact]
    public void A_group_slice_keeps_clear_of_its_chevron_only()
    {
        // Clear of the mark by its outer edge, with the slice's middle where it is without one: a label is centred on
        // the slice, and only the chevron itself keeps it away (a band across the slice moved it towards the hub).
        var plain = SafeZone.Of(Shape(0, 8), 1);
        var group = SafeZone.Of(Shape(0, 8, group: true), 1);
        Assert.Equal(plain.Inner, group.Inner, 6);
        Assert.Equal(plain.Outer, group.Outer, 6);
        var chevron = group.At(Outer - ChevronIn);
        Assert.False(group.Contains(chevron));
        Assert.False(group.Contains(new LabelBox(group.At(Outer - ChevronIn - 8), 30, 8, 0)));
        // Beside it, by the outer edge, a box still fits.
        Assert.True(group.Contains(new LabelBox(DonutGeometry.PointAt(Center, Outer - 14, 0.28), 10, 6, 0)));
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
    // In Noto Sans, the app's font. At 100 % "Merge images", "Compress image" and "Remove metadata" are too wide for a
    // full donut, and "Remove background" is at any Label scale: icons (Auto) or "…".
    [InlineData("Grayscale", 1.0)]
    [InlineData("Merge PDF", 1.0)]
    [InlineData("Merge images", 0.9)]
    [InlineData("Remove metadata", 0.8)]
    [InlineData("Compress image", 0.8)]
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

    [Fact]
    public void Text_level_with_the_ring_is_never_upside_down_and_crosses_the_slice()
    {
        for (var mid = 0.0; mid < 2 * Math.PI; mid += 0.05)
        {
            var turn = SliceLabelLayout.Across(mid);
            Assert.True(Math.Cos(turn) >= -1e-9, $"upside down at {mid:F2}");
            // At right angles to the slice's middle line (which points at mid): its reading direction is mid ± 90°.
            Assert.Equal(0, Math.Cos(turn - mid) * Math.Sin(turn - mid), 6);
        }
    }

    [AvaloniaFact]
    public void Text_runs_along_a_slices_longer_side()
    {
        var layout = Layout();
        // The donut's slices are deeper than they are wide: straight or out from the centre, then bent.
        var deep = SafeZone.Of(Shape(2, 12), 1);
        Assert.False(SliceLabelLayout.IsWide(deep));
        Assert.Equal([[0, SliceLabelLayout.Along(deep.Mid)], [SliceLabelLayout.CurvedTurn]],
            layout.Tries(deep, "Crop", alongToo: true, LabelDirection.Auto));
        // A group ring's slices are wider than they are deep: bent along the ring first.
        var wide = SafeZone.Of(RingShape(-0.9), 1);
        Assert.True(SliceLabelLayout.IsWide(wide));
        Assert.Equal([[SliceLabelLayout.CurvedTurn, 0], [SliceLabelLayout.Along(wide.Mid)]],
            layout.Tries(wide, "Crop", alongToo: true, LabelDirection.Auto));
        // Letters that join can't be bent: level with the ring instead.
        Assert.Equal([[SliceLabelLayout.Across(wide.Mid), 0], [SliceLabelLayout.Along(wide.Mid)]],
            layout.Tries(wide, "قص", alongToo: true, LabelDirection.Auto));
        // A preset's own direction is the only one tried.
        Assert.Equal([[0.0]], layout.Tries(wide, "Crop", true, LabelDirection.Straight));
        Assert.Equal([[SliceLabelLayout.Along(wide.Mid)]], layout.Tries(wide, "Crop", true, LabelDirection.OutFromCentre));
        Assert.Equal([[SliceLabelLayout.CurvedTurn]], layout.Tries(deep, "Crop", true, LabelDirection.AlongRing));
    }

    // A narrow slice of a group ring round a donut of radius 220 (ring size 115 %), its middle at mid: as wide as the
    // slices of a group of six were before every group's slices got the same width.
    private static SliceShape RingShape(double mid, double span = 0.33) => new(Center, 247, 305, mid, span, Gap);

    [AvaloniaTheory]
    [InlineData(-0.9)]
    [InlineData(-0.3)]
    [InlineData(2.6)] // the bottom half, read the other way up
    public void Names_on_a_group_ring_bend_along_it_and_show_whole(double mid)
    {
        // Measured in Noto Sans, the app's font: at 18.6 px these bend whole round a slice 0.7 rad wide.
        var zone = SafeZone.Of(RingShape(mid, 0.7), 1);
        foreach (var name in new[] { "Rotate left", "Resize for email", "Copy to…", "Move to…" })
        {
            var block = Layout().FitText(zone, name, null, 18.6, 15, alongToo: true);
            Assert.True(block is { Whole: true, Curved: true }, $"{name} at {mid}");
            Assert.True(block!.Inside(zone), $"{name} at {mid}");
        }
    }

    [AvaloniaTheory]
    [InlineData(0.0)]      // top: the icon nearer the rim
    [InlineData(Math.PI)]  // bottom: read the other way up, the icon nearer the centre
    public void On_a_deep_enough_slice_the_icon_stands_above_a_bent_name_as_read(double mid)
    {
        // A slice of the six-slice face, its name bent along the ring (a preset's Text direction): the icon above it as
        // read, on the slice's middle line, as in every other mode, rather than beside it.
        var zone = SafeZone.Of(new SliceShape(Center, Inner, Outer, mid, DonutGeometry.Span(6), Gap), 1);
        var fit = Layout().FitIconAndText(zone, "Remove background", null, 11, 9, 20 * 11 / 13.0, 1, shorten: false,
            LabelDirection.AlongRing)!.Value;
        Assert.True(fit.Text is { Curved: true, Whole: true });
        static double Radius(Point p) => Math.Sqrt(Math.Pow(p.X - Center.X, 2) + Math.Pow(p.Y - Center.Y, 2));
        var (icon, text) = (Radius(fit.Icon), Radius(fit.Text.Anchor));
        Assert.True(mid == 0 ? icon > text : icon < text, $"icon at {icon:0.#}, name at {text:0.#}");
        Assert.Equal(0, Math.IEEERemainder(DonutGeometry.AngleOf(Center, fit.Icon) - mid, 2 * Math.PI), 2);
    }

    [AvaloniaFact]
    public void A_face_bent_along_the_ring_puts_every_icon_above_its_name()
    {
        // Icon & text on the six-slice face, every preset's Text direction Along the ring: one placement for all its
        // icons, above each name as read (nearer the rim on the top half, the centre on the bottom), stepping the names
        // down for it rather than putting some icons beside their names.
        string[] names = ["Remove background", "Rotate left", "Compress image", "Merge images", "Trace", "One ZIP"];
        var slices = names.Select((n, i) => Slice(n, i, 6, LabelShow.IconAndText, "crop") with { Direction = LabelDirection.AlongRing }).ToList();
        var plan = Layout().PlanRing(slices, 1, 1, showCaptions: true);
        static double Radius(Point p) => Math.Sqrt(Math.Pow(p.X - Center.X, 2) + Math.Pow(p.Y - Center.Y, 2));
        foreach (var l in plan)
        {
            Assert.Equal(SliceLabelKind.IconAndText, l.Kind);
            Assert.True(l.Text!.Curved);
            var bottom = Math.Cos(l.Zone.Mid) < 0;
            Assert.True(bottom ? Radius(l.IconCenter) < Radius(l.Text.Anchor) : Radius(l.IconCenter) > Radius(l.Text.Anchor));
            Assert.Equal(0, Math.IEEERemainder(DonutGeometry.AngleOf(Center, l.IconCenter) - l.Zone.Mid, 2 * Math.PI), 2);
        }
    }

    [AvaloniaTheory]
    [InlineData("左", "右", "ページ")]          // Japanese
    [InlineData("左", "右", "页面")]            // Chinese
    [InlineData("왼쪽", "오른쪽", "페이지")]      // Korean
    public void Chinese_Japanese_and_Korean_stay_upright_on_a_group_ring(string a, string b, string c)
    {
        // A wide ring, where Latin names bend along it: these stay straight, their characters upright.
        List<SliceLabelInput> ring = [.. new[] { a, b, c }.Select((n, k) => new SliceLabelInput(n, null, null, LabelShow.TextOnly,
            RingShape(-0.6 + 0.6 * k, 0.6)))];
        var plan = Layout().PlanRing(ring, 1, 1, false, ring: true);
        Assert.All(plan, l => Assert.True(l.Text is { Curved: false, Rotation: 0 }, string.Join("", l.Text?.Lines ?? [])));
        var latin = Layout().PlanRing([.. ring.Select((s, k) => s with { Name = new[] { "Left", "Right", "Pages" }[k] })], 1, 1, false, ring: true);
        Assert.Contains(latin, l => l.Text is { Curved: true } || l.Text?.Rotation != 0);
    }

    [AvaloniaFact]
    public void On_an_aslant_slice_a_short_name_stays_on_one_line_at_its_full_size()
    {
        // A three-slice face's slice at eight o'clock: "Merge PDFs" on one line at its full size, inside the slice
        // (once it fitted only moved round from the middle line; it now fits there, moved sideways to its free width).
        var zone = SafeZone.Of(new SliceShape(Center, Inner, Outer, DonutGeometry.MidAngle(2, 3), DonutGeometry.Span(3), Gap), 1);
        var block = Layout().FitText(zone, "Merge PDFs", null, 13, 10.5, alongToo: false)!;
        Assert.Single(block.Lines);
        Assert.Equal(13, block.Size);
        Assert.True(block.Inside(zone));
    }

    [AvaloniaFact]
    public void One_long_word_doesnt_make_every_name_of_the_face_smaller()
    {
        // "PDF zusammenführen" fits whole on no slice of an eight-slice face at 13 px, nor shortened to MinKept; cut so
        // it still reads ("PDF zusam…"), it keeps the face at 13 px rather than make every other name smaller.
        string[] names = ["PDF zusammenführen", "Crop", "Rotate", "Flip", "Grayscale", "TIFF", "HTML", "ZIP"];
        var plan = Layout().PlanRing([.. names.Select((n, i) => Slice(n, i, names.Length))], 1, 1, false);
        Assert.All(plan, l => Assert.Equal(13, l.Text!.Size));
        Assert.False(plan[0].NameWhole);
    }

    [AvaloniaFact]
    public void A_name_is_not_cut_by_a_letter_or_two_where_a_half_step_smaller_shows_it_whole()
    {
        // Find a face size at which a name only just doesn't fit whole: the ring steps down rather than cut it so
        // little.
        var layout = Layout();
        foreach (var name in new[] { "Videos zusammen", "Remove backgrounds", "Merge picture files" })
            foreach (var count in new[] { 6, 8, 12 })
            {
                var plan = layout.PlanRing([.. Enumerable.Range(0, count).Select(i => Slice(i == 0 ? name : "Crop", i, count))], 1, 1, false);
                if (plan[0].Text is { Whole: false } cut)
                    Assert.True(name.Count(c => c != ' ') - cut.Lines.Sum(l => l.TrimEnd('…').Count(c => c != ' ')) > 2,
                        $"{name} on {count}: \"{string.Join(" ", cut.Lines)}\" at {cut.Size}");
            }
    }

    [AvaloniaFact]
    public void Bent_names_keep_more_than_straight_ones_where_the_ring_is_tight()
    {
        // The slice that cut "Copy to…" to "Cop…": bent, more of it shows; straight it needs the room the curve takes.
        var zone = SafeZone.Of(RingShape(-0.9), 1);
        var layout = Layout();
        var bent = layout.FitShortened(zone, "Open in Terminal", 18.6, true, LabelDirection.AlongRing)!;
        var straight = layout.FitShortened(zone, "Open in Terminal", 18.6, true, LabelDirection.Straight)!;
        Assert.True(bent.Lines[0].Length > straight.Lines[0].Length, $"{bent.Lines[0]} / {straight.Lines[0]}");
    }

    [AvaloniaTheory]
    [InlineData(-0.9)]
    [InlineData(2.6)]
    public void Bent_letters_run_round_the_ring_upright(double mid)
    {
        var zone = SafeZone.Of(RingShape(mid, 0.6), 1);
        var layout = Layout();
        var block = layout.FitText(zone, "Copy path", null, 15, 12, alongToo: true, LabelDirection.AlongRing)!;
        var glyphs = layout.CurvedGlyphs(zone, block).ToList();
        Assert.Equal("Copy path".Length, glyphs.Count);
        foreach (var g in glyphs)
        {
            // Upright: the letter's top points away from the centre on the top half, towards it on the bottom half.
            var outward = DonutGeometry.AngleOf(Center, g.At);
            Assert.Equal(0, Math.Sin(g.Rotation - outward - (SliceLabelLayout.Bottom(mid) ? Math.PI : 0)), 6);
        }
        // Each letter follows the last clockwise on the top half, anticlockwise on the bottom half: left to right as
        // read.
        for (var i = 1; i < glyphs.Count; i++)
        {
            var turn = Math.IEEERemainder(DonutGeometry.AngleOf(Center, glyphs[i].At) - DonutGeometry.AngleOf(Center, glyphs[i - 1].At), 2 * Math.PI);
            Assert.True(SliceLabelLayout.Bottom(mid) ? turn < 0 : turn > 0, $"letter {i} at {mid}");
        }
    }

    [AvaloniaFact]
    public void Straight_wins_when_both_directions_fit()
    {
        var block = Layout().FitText(SafeZone.Of(Shape(0, 6), 1), "Crop", null, 13, 10.5, alongToo: true)!;
        Assert.Equal(0, block.Rotation);
        Assert.Single(block.Lines);
    }

    [AvaloniaFact]
    public void A_name_too_long_to_fit_is_shortened_keeping_the_most_letters_that_fit()
    {
        // On as many lines as keep the most of it: "Trim / transpare…".
        var zone = SafeZone.Of(Shape(3, 12), 1);
        var block = Layout().FitShortened(zone, "Trim transparent borders", 13, alongToo: true)!;
        Assert.False(block.Whole);
        Assert.EndsWith("…", block.Lines[^1]);
        Assert.True(block.Inside(zone));
        Assert.True(string.Concat(block.Lines).Length > "Trim tran…".Length);
    }

    // ───────────────────────── Rings ─────────────────────────

    [AvaloniaFact]
    public void Every_name_on_a_ring_is_one_size_and_inside_its_zone()
    {
        string[] names = ["Crop", "Rotate", "Flip", "Remove background", "Trim transparent borders", "Grayscale", "AVIF",
            "Merge images", "Merge PDF", "Compress image", "Remove metadata", "Actions"];
        var plan = Layout().PlanRing(names.Select((n, i) => Slice(n, i, 12)).ToList(), labelScale: 1, scale: 1, showCaptions: true);
        Assert.Single(plan.Where(l => l.Text is not null).Select(l => l.Text!.Size).Distinct());
        Assert.All(plan, l => Assert.True(l.Text is null || l.Text.Inside(l.Zone)));
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
        Assert.Equal(SliceLabelKind.Text, plan[0].Kind);       // its name can't fit whole: shortened, not its icon alone
        Assert.False(plan[0].NameWhole);
        Assert.Equal(SliceLabelKind.Text, plan[1].Kind);       // no icon: its name
        Assert.Equal(SliceLabelKind.Text, plan[2].Kind);       // Text only stays text
        Assert.Equal(SliceLabelKind.Icon, plan[3].Kind);       // Icon only stays an icon
    }

    [AvaloniaFact]
    public void An_icon_alone_is_centred_and_an_icon_with_a_name_sits_above_it_upright()
    {
        var layout = Layout();
        var alone = layout.PlanRing([Slice("Crop", 2, 6, LabelShow.IconOnly, "crop")], 1, 1, false)[0];
        var zone = alone.Zone;
        Assert.Equal(zone.At((zone.Inner + zone.Outer) / 2), alone.IconCenter);

        // A deep slice (twelve round the donut, at 12 o'clock): the icon above the name, both upright.
        var both = layout.PlanRing([Slice("Crop", 0, 12, LabelShow.IconAndText, "crop")], 1, 1, false)[0];
        Assert.Equal(SliceLabelKind.IconAndText, both.Kind);
        Assert.Equal(0, both.Text!.Rotation);
        Assert.True(both.IconCenter.Y < both.Text.Anchor.Y);
        Assert.Equal(both.IconCenter.X, both.Text.Anchor.X, 6);
    }

    [AvaloniaTheory]
    [InlineData(-0.9)]
    [InlineData(-0.3)]
    [InlineData(2.6)]
    public void On_a_wide_slice_the_icon_stands_before_a_name_bent_along_the_ring(double mid)
    {
        // Where an icon above its name would only fit on the slices that run up and down: before it, on every slice.
        var zone = SafeZone.Of(RingShape(mid, 0.6), 1);
        var layout = Layout();
        var fit = layout.FitIconAndText(zone, "Rotate left", null, 18.6, 15, 20 * 1.4, 1.4, shorten: false)!.Value;
        Assert.True(fit.Text is { Curved: true, Whole: true });
        Assert.True(fit.Text.Inside(zone));
        Assert.True(zone.Contains(fit.Icon, SliceLabel.IconReach(20 * 1.4)));
        // Before the name as read: earlier round the ring (clockwise on the top half, anticlockwise on the bottom).
        var turn = Math.IEEERemainder(DonutGeometry.AngleOf(Center, fit.Text.Anchor) - DonutGeometry.AngleOf(Center, fit.Icon), 2 * Math.PI);
        Assert.True(SliceLabelLayout.Bottom(mid) ? turn < 0 : turn > 0, $"{turn} at {mid}");
        Assert.Equal(0, Math.Round(Distance(fit.Icon) - Distance(fit.Text.Anchor), 6)); // side by side, on one circle
    }

    [AvaloniaFact]
    public void Where_the_name_cant_show_whole_under_its_icon_the_icon_stands_before_it_out_from_the_centre()
    {
        // A deep, narrow slice (a fourteen-slice face): "Pixelate" under its icon would be cut; before it, along the
        // slice, it shows whole.
        var zone = SafeZone.Of(new SliceShape(Center, 67, 160, DonutGeometry.MidAngle(2, 14), DonutGeometry.Span(14), Gap), 160 / 150.0);
        var layout = Layout();
        var fit = layout.FitIconAndText(zone, "Pixelate", null, 11.1, 9, 20 * 0.85, 0.85, shorten: true)!.Value;
        Assert.True(fit.Text.Whole);
        Assert.Equal(SliceLabelLayout.Along(zone.Mid), fit.Text.Rotation, 9);
        Assert.True(Distance(fit.Icon) < Distance(fit.Text.Anchor)); // first as read: inward on the right half
    }

    private static double Distance(Point p) => Math.Sqrt(Math.Pow(p.X - Center.X, 2) + Math.Pow(p.Y - Center.Y, 2));

    [AvaloniaFact]
    public void A_name_shortened_under_its_icon_wraps_as_a_name_alone_does()
    {
        // The eight-slice face's top slice at 13 px: "Remove background" doesn't fit whole under its icon, and on one
        // line no cut that still reads does; on two lines it keeps "Remove / backgr…".
        var fit = Layout().FitIconAndText(SafeZone.Of(Shape(0, 8), 1), "Remove background", null, 13, 10.5, 20, 1, shorten: true,
            LabelDirection.Straight, SliceLabelLayout.MinKept("Remove background"), placement: IconPlacement.Above);
        Assert.NotNull(fit);
        Assert.False(fit.Value.Text.Whole);
        Assert.Equal(2, fit.Value.Text.Lines.Count);
        Assert.Equal("Remove", fit.Value.Text.Lines[0]);
        Assert.StartsWith("backg", fit.Value.Text.Lines[1], StringComparison.Ordinal);
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
                Assert.All(layout.PlanRing(ring, 1.6, 1, false, ring: true), l => Assert.True(l.NameWhole && l.Text!.Size < 13 * 1.6));
            var fitted = layout.PlanRingToFit(ring, 1.6, 1, false);
            Assert.All(fitted, l => Assert.True(l.Kind == SliceLabelKind.Icon || l.NameWhole, show.ToString()));
            // Never smaller than at 100 %, and smaller than asked for, as the names don't fit whole at 160 %.
            var size = fitted.Select(l => l.Text?.Size ?? 0).Max();
            var atFull = layout.PlanRing(ring, 1, 1, false, ring: true).Select(l => l.Text?.Size ?? 0).Max();
            Assert.True(size == 0 || (size >= atFull && size < 13 * 1.6), $"{show}: {size} (at 100 %: {atFull})");
            // At 100 % (or less) it is what PlanRing gives: no step below the scale asked for.
            Assert.Equal(layout.PlanRing(ring, 0.8, 1, false, ring: true).Select(l => (l.Kind, l.Text?.Size, l.IconSize)),
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
    public void A_name_beside_an_icon_keeps_most_of_its_letters_or_its_slice_shows_the_icon()
    {
        var layout = Layout();
        foreach (var count in new[] { 6, 12 })
        {
            var plan = layout.PlanRing(Enumerable.Range(0, count)
                .Select(i => Slice("Open in Terminal and more words", i, count, LabelShow.IconAndText, "console")).ToList(), 1, 1, false);
            Assert.All(plan, l => Assert.Contains(l.Kind, new[] { SliceLabelKind.IconAndText, SliceLabelKind.Icon }));
            foreach (var label in plan)
                if (label.Text is { Whole: false } text)
                    Assert.True(KeepsMinKept(text, "Open in Terminal and more words"), string.Join(" / ", text.Lines));
            Assert.Single(plan.Where(l => l.IconSize > 0).Select(l => l.IconSize).Distinct());
        }
    }

    // ──────────────── One size per ring, Auto one style, no stub beside an icon ────────────────

    // The Images donut's back face, as shipped: every slice has an icon but AVIF (a format).
    private static readonly string[] Back =
        ["Crop", "Rotate", "Flip", "Remove background", "Trim transparent borders", "Grayscale", "AVIF", "Merge images",
            "Merge PDF", "Compress image", "Remove metadata", "One ZIP"];
    private static readonly string[] Six = ["Crop", "Rotate", "Flip", "Remove background", "Grayscale", "Compress image"];

    private static List<SliceLabelInput> Face(string[] names, LabelShow show) => names.Select((n, i) =>
        Slice(n, i, names.Length, show, n == "AVIF" ? null : "crop", n == "Rotate" ? "Group of 3" : n == "Remove background" ? "PNG" : "JPG")).ToList();

    // The letters a shortened name keeps, on every line, against the letters MinKept asks of it (spaces not counted).
    private static bool KeepsMinKept(LabelText text, string name)
    {
        static int Letters(string s)
        {
            var count = 0;
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
            while (e.MoveNext())
                if (!string.IsNullOrWhiteSpace(e.GetTextElement()))
                    count++;
            return count;
        }
        var elements = new System.Globalization.StringInfo(name.Trim());
        var need = Letters(elements.SubstringByTextElements(0, Math.Min(elements.LengthInTextElements, SliceLabelLayout.MinKept(name))));
        return Letters(string.Concat(text.Lines).TrimEnd().TrimEnd('…')) >= need;
    }

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
    public void No_name_beside_an_icon_is_a_stub_and_Auto_never_mixes(LabelShow show)
    {
        var layout = Layout();
        foreach (var names in new[] { Back, Six })
            foreach (var labelScale in new[] { 0.8, 1, 1.3, 1.6 })
            {
                var slices = Face(names, show);
                var plan = layout.PlanRing(slices, labelScale, 1, showCaptions: true);
                var where = $"{names.Length} slices ×{labelScale} {show}";
                // Auto: icons on all of the names or on none; Icon & text per slice. A slice without an icon shows its
                // name.
                if (show == LabelShow.Auto)
                    Assert.True(plan.Where((l, i) => slices[i].IconKey is not null && l.Kind != SliceLabelKind.Icon)
                        .Select(l => l.Kind).Distinct().Count() <= 1, where);
                Assert.All(plan.Where((_, i) => slices[i].IconKey is null), l => Assert.Equal(SliceLabelKind.Text, l.Kind));
                // Beside an icon, a name keeps MinKept; alone, Auto shortens it further where nothing else fits, rather
                // than show its icon alone among names.
                for (var i = 0; i < plan.Count; i++)
                    if (plan[i] is { Kind: SliceLabelKind.IconAndText, Text: { Whole: false } text })
                        Assert.True(KeepsMinKept(text, names[i]), $"{where}: \"{string.Join(" / ", text.Lines)}\"");
            }
    }

    [AvaloniaFact]
    public void Captions_show_where_they_fit_beside_icons_too_and_never_make_the_names_smaller()
    {
        var layout = Layout();
        // Room for them: names alone with their captions, and icon, name and caption at 80 % in Auto.
        var six = layout.PlanRing(Face(Six, LabelShow.TextOnly), 0.8, 1, showCaptions: true);
        Assert.All(six, l => Assert.NotNull(l.Text!.Caption));
        var icons = layout.PlanRing(Face(Six, LabelShow.Auto), 0.8, 1, showCaptions: true);
        Assert.All(icons, l => Assert.Equal(SliceLabelKind.IconAndText, l.Kind));
        Assert.Contains(icons, l => l.Text!.Caption is not null);
        // A caption that doesn't fit leaves its name alone, at the ring's size: an optional caption never makes the
        // names smaller.
        foreach (var show in new[] { LabelShow.Auto, LabelShow.IconAndText, LabelShow.TextOnly })
        {
            var with = layout.PlanRing(Face(Back, show), 1, 1, showCaptions: true);
            var without = layout.PlanRing(Face(Back, show), 1, 1, showCaptions: false);
            Assert.Equal(without.Select(l => l.NameSize), with.Select(l => l.NameSize));
        }
    }

    [Fact]
    public void A_line_never_ends_on_an_opening_bracket_or_starts_on_a_closing_one()
    {
        var units = SliceLabelLayout.Units("フレームを抽出（PNG）").Select(u => u.Text).ToList();
        Assert.DoesNotContain(units, u => u.EndsWith('（'));
        Assert.DoesNotContain(units, u => u.StartsWith('）') || u.StartsWith('ー'));
        Assert.Equal("フレームを抽出（PNG）", string.Concat(units));
        Assert.Equal("PPTX（幻灯片）", string.Concat(SliceLabelLayout.Units("PPTX（幻灯片）").Select(u => u.Text)));
        Assert.DoesNotContain(SliceLabelLayout.Units("PPTX（幻灯片）"), u => u.Text.EndsWith('（'));
    }

    // The six-slice face with two slices of one name, told apart by their format, and a Copy to folder slice named
    // after its folder, told apart from a Move by "Copy to folder" under it: the captions those three need. The others'
    // format is shown only with Show output format on.
    private static List<SliceLabelInput> Told(LabelShow show, string folder = "Projects") =>
    [
        Slice("Crop", 0, 6, show, "crop", "JPG"),
        Slice("Rotate left", 1, 6, show, "rotate-left", "PDF") with { NeedsCaption = true },
        Slice("Rotate left", 2, 6, show, "rotate-left", "JPG") with { NeedsCaption = true },
        Slice("Grayscale", 3, 6, show, "crop", "JPG"),
        Slice(folder, 4, 6, show, "content-copy", "Copy to folder") with { NeedsCaption = true },
        Slice("Compress image", 5, 6, show, "crop", "JPG"),
    ];

    [AvaloniaFact]
    public void A_caption_a_slice_needs_shows_beside_its_icon_and_the_others_where_they_fit()
    {
        // Icon & text: the three that need their caption show it, on and off (beside the icon where it fits there, else
        // under the name alone); with Show output format off, only those three.
        var layout = Layout();
        foreach (var captions in new[] { true, false })
        {
            var slices = Told(LabelShow.IconAndText);
            var plan = layout.PlanRing(slices, 1, 1, captions);
            Assert.All(plan.Where((_, i) => !slices[i].NeedsCaption), l => Assert.Equal(SliceLabelKind.IconAndText, l.Kind));
            for (var i = 0; i < plan.Count; i++)
                if (slices[i].NeedsCaption)
                    Assert.Equal(slices[i].Caption, plan[i].Text!.Caption);
                else if (!captions)
                    Assert.Null(plan[i].Text!.Caption);
        }
    }

    [AvaloniaFact]
    public void Names_alone_show_the_captions_their_slices_need_with_Show_output_format_off()
    {
        var layout = Layout();
        foreach (var show in new[] { LabelShow.TextOnly, LabelShow.Auto })
        {
            var slices = Told(show);
            var off = layout.PlanRing(slices, 1, 1, showCaptions: false);
            for (var i = 0; i < off.Count; i++)
                Assert.Equal(slices[i].NeedsCaption ? slices[i].Caption : null, off[i].Text!.Caption);
            // On: the needed ones still, the others where they fit.
            var on = layout.PlanRing(slices, 1, 1, showCaptions: true);
            Assert.All(on.Where((_, i) => slices[i].NeedsCaption), (l, i) => Assert.NotNull(l.Text!.Caption));
        }
    }

    [AvaloniaFact]
    public void Icon_only_shows_no_caption_even_one_a_slice_needs()
    {
        var plan = Layout().PlanRing(Told(LabelShow.IconOnly), 1, 1, showCaptions: true);
        Assert.All(plan, l => Assert.Equal(SliceLabelKind.Icon, l.Kind));
        Assert.All(plan, l => Assert.Null(l.Text));
    }

    [AvaloniaFact]
    public void Slices_of_one_name_look_alike()
    {
        // Icon & text: where "Rotate left" (Same format) shows its name and caption without its icon, so does
        // "Rotate left" (PDF); and a Copy and a Move slice for one folder alike.
        var layout = Layout();
        List<SliceLabelInput> ring =
        [
            Slice("Projects", 0, 6, LabelShow.IconAndText, "content-copy", "Copy to folder") with { NeedsCaption = true },
            Slice("Projects", 1, 6, LabelShow.IconAndText, "folder-move-outline", "Move to folder") with { NeedsCaption = true },
            Slice("Rotate left", 2, 6, LabelShow.IconAndText, "rotate-left", "Same format") with { NeedsCaption = true },
            Slice("Rotate left", 3, 6, LabelShow.IconAndText, "rotate-left", "PDF") with { NeedsCaption = true },
            Slice("Crop", 4, 6, LabelShow.IconAndText, "crop", "JPG"),
            Slice("Compress image", 5, 6, LabelShow.IconAndText, "crop", "JPG"),
        ];
        foreach (var captions in new[] { true, false })
        {
            var plan = layout.PlanRing(ring, 1, 1, captions);
            Assert.Equal(plan[0].Kind, plan[1].Kind);
            Assert.Equal(plan[2].Kind, plan[3].Kind);
            Assert.All(plan.Take(4), l => Assert.NotNull(l.Text!.Caption));
        }
    }

    [AvaloniaFact]
    public void A_needed_caption_with_no_room_anywhere_gives_way()
    {
        // A folder name so long that "Copy to folder" fits under it nowhere: Icon & text keeps the slice's icon (the
        // name shortened beside it), and Auto shows the names, that one shortened, without it.
        var layout = Layout();
        const string folder = "Holiday photos from the summer of 2026";
        var both = layout.PlanRing(Told(LabelShow.IconAndText, folder), 1, 1, showCaptions: false);
        Assert.Contains(both[4].Kind, new[] { SliceLabelKind.IconAndText, SliceLabelKind.Icon });
        var auto = layout.PlanRing(Told(LabelShow.Auto, folder), 1, 1, showCaptions: false);
        Assert.Equal(SliceLabelKind.Text, auto[4].Kind);
        Assert.False(auto[4].NameWhole);
    }

    [AvaloniaFact]
    public void A_ring_steps_its_names_down_to_ten_px_times_the_donut_size_and_never_under_ten()
    {
        Assert.Equal([13, 12.5, 12, 11.5, 11, 10.5, 10], SliceLabelLayout.SizesFor(13, 1));
        Assert.Equal(15, SliceLabelLayout.SizesFor(20.8, 1.5)[^1]);           // 10 × 1.5
        Assert.Equal(10, SliceLabelLayout.SizesFor(13, 0.7)[^1]);             // 10 × 0.7 = 7 → never under 10
        Assert.Equal([10.4, 10], SliceLabelLayout.SizesFor(10.4, 1));
        Assert.Equal([10], SliceLabelLayout.SizesFor(10, 1));                 // at the floor already: as it is

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
                Assert.InRange(size, Math.Max(10, 10 * scale) - 1e-9, 13 * scale + 1e-9);
            }
    }

    [AvaloniaFact]
    public void A_ring_runs_all_its_names_one_way()
    {
        // A group's ring at the default donut, Group ring size 150 %: "Right" alone fits only along its slice; the
        // ring's other names then run along theirs too, rather than straight beside it.
        var layout = Layout();
        foreach (var (inner, depth) in new[] { (Outer + 13, 46.0), (Outer + 19.5, 69.0) })
        {
            var outer = inner + depth;
            var span = DonutGeometry.SubSpan(DonutGeometry.Span(12), 3, (inner + outer) / 2);
            var ring = new[] { "Left", "Right", "180°" }.Select((n, k) => new SliceLabelInput(n, null, "rotate-left", LabelShow.TextOnly,
                new SliceShape(Center, inner, outer, DonutGeometry.SubMidAngle(0.3, k, 3, span), span, Gap))).ToList();
            foreach (var scale in new[] { 1.0, 1.5 })
            {
                var plan = layout.PlanRing(ring, 1, scale, false, ring: true);
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
    public void Auto_keeps_the_names_large_rather_than_shrink_them_for_icons()
    {
        // The six-slice face at 120 %: "Remove background" has no room beside an icon at 15.6 px, so the ring shows its
        // names alone at 15.6 px rather than smaller names with icons. At 100 % every name has room for its icon.
        var layout = Layout();
        var large = layout.PlanRing(Face(Six, LabelShow.Auto), 1.2, 1, showCaptions: true);
        Assert.All(large, l => Assert.True(l is { Kind: SliceLabelKind.Text, NameWhole: true }));
        Assert.Equal(13 * 1.2, large[0].Text!.Size, 6);
        var full = layout.PlanRing(Face(Six, LabelShow.Auto), 1, 1, showCaptions: true);
        Assert.All(full, l => Assert.True(l is { Kind: SliceLabelKind.IconAndText, NameWhole: true }));
        Assert.Equal(13, full[0].Text!.Size);
    }

    [AvaloniaFact]
    public void Icon_and_text_on_the_twelve_slice_face_keeps_an_icon_on_every_slice()
    {
        var slices = Face(Back, LabelShow.IconAndText);
        var plan = Layout().PlanRing(slices, 1, 1, showCaptions: true);
        for (var i = 0; i < plan.Count; i++)
            if (slices[i].IconKey is null)
                Assert.True(plan[i] is { Kind: SliceLabelKind.Text, NameWhole: true });
            else
                Assert.Contains(plan[i].Kind, new[] { SliceLabelKind.IconAndText, SliceLabelKind.Icon });
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
