// A group slice's mark: a small chevron by its outer edge pointing outward, where the group's ring fans out. Drawn on
// group slices only, on the donut and in the Donut toolbar page's preview alike.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class DonutGroupMarkTests
{
    /// <summary>A donut of six slices, the first a group or the same slice as a preset, drawn as it is shown.</summary>
    private static byte[] Render(bool group, Action<DonutMenu>? setUp = null, string label = "\u00A0", double radius = 150, Action<DonutMenu>? after = null)
    {
        TestServices.EnsureInitialized();
        Motion.Configure(false, () => false);
        DonutItem Slice(string name) => new() { Id = name, Label = "\u00A0" };
        var first = group
            ? new DonutItem { Id = "first", Label = label, Children = [Slice("Left")] }
            : new DonutItem { Id = "first", Label = label };
        var donut = new DonutMenu { Items = [first, Slice("b"), Slice("c"), Slice("d"), Slice("e"), Slice("f")], OuterRadius = radius, ReserveSubRing = true };
        setUp?.Invoke(donut);
        var side = (int)(radius * 4 + 40);
        var window = new Window { Content = donut, Width = side, Height = side };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            for (var i = 0; i < 60; i++)
                donut.Advance(1 / 60.0);
            RenderTests.Pump();
            // Stepped by hand from here: the dispatcher's own clock would draw each donut a different frame.
            after?.Invoke(donut);
            using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(side, side));
            image.Render(donut);
            var pixels = new byte[side * side * 4];
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { image.CopyPixels(new PixelRect(0, 0, side, side), pin.AddrOfPinnedObject(), pixels.Length, side * 4); }
            finally { pin.Free(); }
            return pixels;
        }
        finally
        {
            window.Close();
        }
    }

    // The picture's side at the default radius.
    private const int Picture = 640;

    /// <summary>Where two pictures at the default radius differ, from the donut's centre.</summary>
    private static List<Point> Ink(byte[] a, byte[] b) =>
        [.. Changed(a, b).Select(i => new Point(i % Picture - Picture / 2.0, i / Picture - Picture / 2.0))];

    [AvaloniaFact]
    public void Only_a_group_slice_has_the_chevron_by_its_outer_edge()
    {
        var ink = Ink(Render(true), Render(false));

        // At the top, the first slice's outer edge, 7 to 12 px in from it, and nowhere else.
        Assert.NotEmpty(ink);
        Assert.All(ink, p =>
        {
            Assert.InRange(p.X, -7, 7);
            Assert.InRange(p.Y, -145, -136);
        });
    }

    private static IEnumerable<int> Changed(byte[] a, byte[] b) =>
        Enumerable.Range(0, a.Length / 4).Where(i => Enumerable.Range(0, 3).Any(k => Math.Abs(a[i * 4 + k] - b[i * 4 + k]) > 24));

    [AvaloniaFact]
    public void The_chevron_is_as_large_as_the_donut_says()
    {
        int Pixels(double radius) => Changed(Render(true, radius: radius), Render(false, radius: radius)).Count();

        Assert.InRange(Pixels(220) / (double)Pixels(110), 2.5, 6);
    }

    [AvaloniaFact]
    public void A_groups_name_moves_in_to_leave_the_chevron_room()
    {
        // The first row the name's ink reaches, the name being what differs from the same slice without one.
        int NameTop(bool group) => Changed(Render(group, label: "Rotate"), Render(group)).Min() / Picture;

        Assert.InRange(NameTop(true) - NameTop(false), 2, 4);
    }

    [AvaloniaFact]
    public void Lit_the_chevron_takes_the_highlights_text_colour()
    {
        static bool IsLime(byte[] p, int i) => p[i * 4 + 1] > 200 && p[i * 4] < 90 && p[i * 4 + 2] < 90;
        static int Lime(byte[] p) => Enumerable.Range(0, p.Length / 4).Count(i => IsLime(p, i));
        void Lit(DonutMenu donut)
        {
            donut.OnAccentBrush = Brushes.Lime;
            donut.SetExternalHighlight(0);
        }

        Assert.InRange(Lime(Render(true, Lit)), 1, 400);
        Assert.Equal(0, Lime(Render(false, Lit)));
    }

    [AvaloniaFact]
    public void A_disabled_groups_chevron_is_faded()
    {
        static int Strongest(byte[] p) => Enumerable.Range(0, p.Length / 4).Max(i => p[i * 4 + 1] - Math.Max(p[i * 4], p[i * 4 + 2]));
        void Green(DonutMenu donut) => donut.SecondaryForeground = Brushes.Lime;
        void Disabled(DonutMenu donut)
        {
            Green(donut);
            var first = donut.Items![0];
            donut.Items = [new DonutItem { Id = first.Id, Label = first.Label, Children = first.Children, IsEnabled = false }, .. donut.Items.Skip(1)];
        }

        Assert.True(Strongest(Render(true, Disabled)) < Strongest(Render(true, Green)) * 0.6);
    }

    [AvaloniaFact]
    public void The_chevron_collapses_into_the_progress_ring_with_its_slice()
    {
        byte[] Shot(bool group) => Render(group, after: donut =>
        {
            donut.BeginProgress();
            for (var i = 0; i < 8; i++)
                donut.Advance(1 / 60.0);
        });
        static double Reach(List<Point> ink) => ink.Max(p => Point.Distance(p, new Point(0, 0)));

        var collapsing = Ink(Shot(true), Shot(false));

        // On its way to the centre, where its slice is going.
        Assert.NotEmpty(collapsing);
        Assert.True(Reach(collapsing) < Reach(Ink(Render(true), Render(false))) - 5);
    }

    [AvaloniaFact]
    public void On_a_side_slice_the_chevron_turns_with_it_and_pops_out_with_it()
    {
        // The first slice moved to third of six, at the lower right. Lit, it pops out and grows, its chevron with it.
        static void Side(DonutMenu donut) => donut.Items = [.. donut.Items!.Skip(4), .. donut.Items!.Take(4)];
        static void Lit(DonutMenu donut)
        {
            Side(donut);
            donut.SetExternalHighlight(2);
        }
        double Reach(Action<DonutMenu> setUp, double within)
        {
            var ink = Ink(Render(true, setUp), Render(false, setUp));
            Assert.NotEmpty(ink);
            var mark = DonutGeometry.PointAt(new Point(0, 0), 141, DonutGeometry.MidAngle(2, 6));
            Assert.All(ink, p => Assert.InRange(Point.Distance(p, mark), 0, within));
            return ink.Max(p => Point.Distance(p, new Point(0, 0)));
        }

        Assert.True(Reach(Lit, 24) > Reach(Side, 12) + 5);
    }

    [AvaloniaFact]
    public void The_flip_draws_the_chevron_on_the_old_face_too()
    {
        byte[] Shot(bool group) => Render(group, after: donut =>
        {
            var back = donut.Items!.Select(i => new DonutItem { Id = "back" + i.Id, Label = "\u00A0" }).ToList();
            donut.CanFlip = true;
            donut.FaceSwapping += (_, _) => donut.Items = back;
            Assert.True(donut.Flip());
            for (var i = 0; i < 2; i++)
                donut.Advance(1 / 60.0);
        });
        static double Width(List<Point> ink) => ink.Max(p => p.X) - ink.Min(p => p.X);

        var turning = Ink(Shot(true), Shot(false));

        // The slice at the top turns about its middle line: its chevron narrows with it.
        Assert.NotEmpty(turning);
        Assert.True(Width(turning) < Width(Ink(Render(true), Render(false))));
    }

    [AvaloniaFact]
    public void In_the_editor_a_group_is_marked_at_the_size_of_a_preset_and_so_is_the_slice_being_dragged_in()
    {
        static void Editing(DonutMenu donut)
        {
            donut.IsEditMode = true;
            donut.ReserveSubRing = false;
            donut.Items = [.. donut.Items!.Select(i => i.IsGroup ? new DonutItem { Id = i.Id, Label = i.Label, Children = [] } : i)];
        }
        double Measured(bool group)
        {
            var size = 0.0;
            Render(group, Editing, after: donut =>
            {
                donut.Measure(Size.Infinity);
                size = donut.DesiredSize.Width;
            });
            return size;
        }
        byte[] Dragging(bool group) => Render(false, Editing, after: donut =>
        {
            donut.SetDropPreview(3, "\u00A0", group: group);
            for (var i = 0; i < 60; i++)
                donut.Advance(1 / 60.0);
        });

        Assert.NotEmpty(Changed(Render(true, Editing), Render(false, Editing)));
        Assert.Equal(Measured(false), Measured(true));
        Assert.NotEmpty(Changed(Dragging(true), Dragging(false)));
    }

    /// <summary>One frame of the donut, recorded: each chevron's shape and the transforms it is drawn inside.</summary>
    private static List<(object Shape, Matrix Transform)> Chevrons(DonutMenu donut)
    {
        var frame = new DrawingGroup();
        using (var context = frame.Open())
            donut.Render(context);
        var chevrons = new List<(object, Matrix)>();
        void Walk(Drawing drawing, Matrix around)
        {
            if (drawing is DrawingGroup group)
            {
                var within = (group.Transform?.Value ?? Matrix.Identity) * around;
                foreach (var child in group.Children)
                    Walk(child, within);
            }
            else if (drawing is GeometryDrawing { Pen.Brush: var brush } line && ReferenceEquals(brush, ChevronBrush))
                chevrons.Add((PlatformShape.GetValue(line.Geometry)!, around));
        }
        Walk(frame, Matrix.Identity);
        return chevrons;
    }

    // What a drawing keeps of the geometry it was given: the shape itself, so the same one can be told from a copy.
    private static readonly System.Reflection.PropertyInfo PlatformShape = typeof(Geometry).GetProperty("PlatformImpl",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new MissingMemberException(nameof(Geometry), "PlatformImpl");

    // Only the chevrons are stroked with it here, lit or not: the slices' names and captions are text.
    private static readonly IBrush ChevronBrush = Brushes.Lime;

    [AvaloniaTheory]
    [InlineData("pop out")]
    [InlineData("flip")]
    public void An_animation_moves_the_same_chevron_shape_and_makes_no_new_one(string animation)
    {
        Render(true, donut =>
        {
            donut.SecondaryForeground = ChevronBrush;
            donut.OnAccentBrush = ChevronBrush;
        }, after: donut =>
        {
            var back = donut.Items!.Select(i => new DonutItem { Id = i.Id + "-back", Label = i.Label, Children = i.Children }).ToList();
            donut.CanFlip = true;
            donut.FaceSwapping += (_, _) => donut.Items = back;
            var first = Assert.Single(Chevrons(donut));
            if (animation == "pop out")
                donut.SetExternalHighlight(0);
            else
                Assert.True(donut.Flip());

            var frames = new List<(object Shape, Matrix Transform)>();
            for (var i = 0; i < 30; i++)
            {
                donut.Advance(1 / 60.0);
                frames.AddRange(Chevrons(donut));
            }

            Assert.NotEmpty(frames);
            Assert.All(frames, chevron => Assert.Same(first.Shape, chevron.Shape));
            // The same shape, moved by its slice's transform.
            // An arm's end: the flip turns the slice about its middle line, which stays where it is.
            var arm = new Point(330, 180);
            Assert.Contains(frames, chevron => Point.Distance(arm.Transform(chevron.Transform), arm.Transform(first.Transform)) > 1);
        });
    }

    [AvaloniaTheory]
    [InlineData("radius")]
    [InlineData("hole")]
    [InlineData("slice count")]
    [InlineData("slice angle")]
    [InlineData("centre")]
    public void A_change_to_the_donut_draws_the_chevron_as_a_new_donut_would(string change)
    {
        void Changed(DonutMenu donut)
        {
            switch (change)
            {
                case "radius": donut.OuterRadius = 170; break;
                case "hole": donut.HoleRatio = 0.65; break;
                case "slice count": donut.Items = [.. donut.Items!.SkipLast(1)]; break;
                case "slice angle": donut.Items = [donut.Items![^1], .. donut.Items!.SkipLast(1)]; break;
                case "centre": donut.Width = 600; break;
            }
        }
        // Off the first slice, whose angle stays the same whatever the number of slices.
        static void Placed(DonutMenu donut)
        {
            donut.Items = [.. donut.Items!.Skip(4), .. donut.Items!.Take(4)];
            donut.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            donut.Width = 640;
            donut.SecondaryForeground = ChevronBrush;
        }

        var kept = Render(true, Placed, after: donut =>
        {
            Assert.Single(Chevrons(donut));
            Changed(donut);
            RenderTests.Pump();
            for (var i = 0; i < 60; i++)
                donut.Advance(1 / 60.0);
        });

        Assert.Equal(Render(true, donut =>
        {
            Placed(donut);
            Changed(donut);
        }), kept);
    }

    [AvaloniaFact]
    public void Kept_chevron_shapes_are_bounded_on_a_donut_that_keeps_changing()
    {
        Render(true, donut => donut.SecondaryForeground = ChevronBrush, after: donut =>
        {
            for (var radius = 100.0; radius < 500; radius++)
            {
                donut.OuterRadius = radius;
                Assert.Single(Chevrons(donut));
                Assert.InRange(donut.CachedGroupMarkCount, 1, 256);
            }
        });
    }

    /// <summary>The Donut toolbar page on the PDF donut's front or back, and where to point at its slices.</summary>
    private static (MainWindow Window, ToolbarPageViewModel Page, DonutMenu Donut, Func<double, int, Point> On) Editor(MainWindowViewModel main, bool back)
    {
        main.Navigate("toolbar");
        var window = new MainWindow { DataContext = main, Width = 1080, Height = 1000 };
        window.Show();
        var page = (ToolbarPageViewModel)main.CurrentPage!;
        page.SelectedProfile = page.Profiles.Single(p => p.Profile.Id == "pdf");
        page.EditingBack = back;
        RenderTests.Pump();
        var donut = window.GetVisualDescendants().OfType<DonutMenu>().Single(d => d.IsEditMode);
        var count = page.DonutItems.Count;
        Point On(double radius, int slice) => donut.TranslatePoint(
            DonutGeometry.PointAt(new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2), radius, DonutGeometry.MidAngle(slice, count)), window)!.Value;
        return (window, page, donut, On);
    }

    private static bool Dragging(MainWindow window) => window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "Ghost").IsVisible;

    [AvaloniaFact]
    public void A_profile_change_during_a_drag_does_not_crash_and_a_release_off_the_donut_changes_nothing()
    {
        TestServices.EnsureInitialized();
        Motion.Configure(false, () => false);
        using var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        var (window, page, donut, on) = Editor(main, back: false);
        try
        {
            var count = page.DonutItems.Count;
            var other = page.Profiles.Where(p => p.Profile.Id != "pdf").MinBy(p => p.Profile.PresetIds.Count)!;
            var before = page.Profiles.Select(p => string.Join(",", p.Profile.PresetIds)).ToList();

            window.MouseDown(on(donut.OuterRadius - 40, count - 1), MouseButton.Left);
            window.MouseMove(on(donut.OuterRadius - 20, count - 1));
            window.MouseMove(on(donut.OuterRadius - 30, count - 2));
            Assert.True(Dragging(window));
            page.SelectedProfile = other;
            RenderTests.Pump();
            Assert.True(page.DonutItems.Count < count);

            window.MouseMove(new Point(5, 5));
            window.MouseUp(new Point(5, 5), MouseButton.Left);

            Assert.False(Dragging(window));
            Assert.Equal(before, page.Profiles.Select(p => string.Join(",", p.Profile.PresetIds)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dragging_a_slice_shows_it_marked_as_it_is_on_the_donut(bool group)
    {
        TestServices.EnsureInitialized();
        Motion.Configure(false, () => false);
        using var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        var (window, page, donut, on) = Editor(main, back: true);
        try
        {
            Assert.Contains(page.DonutItems, i => i.IsGroup == group);
            var slice = page.DonutItems.ToList().FindIndex(i => i.IsGroup == group);
            var next = (slice + 1) % page.DonutItems.Count;

            window.MouseDown(on(donut.OuterRadius - 40, slice), MouseButton.Left);
            window.MouseMove(on(donut.OuterRadius - 20, slice));
            window.MouseMove(on(donut.OuterRadius - 40, next));

            Assert.True(Dragging(window));
            Assert.Equal(group, donut.PreviewIsGroup);

            window.MouseMove(new Point(5, 5));
            window.MouseUp(new Point(5, 5), MouseButton.Left);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dragging_a_chip_shows_it_marked_as_it_is_on_the_donut(bool group)
    {
        TestServices.EnsureInitialized();
        Motion.Configure(false, () => false);
        using var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        var (window, page, donut, on) = Editor(main, back: true);
        try
        {
            page.SelectedTab = page.Tabs.First();
            RenderTests.Pump();
            var chip = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("chip") && b.DataContext is PaletteChip { CanAdd: true } c && c.IsGroup == group);
            chip.BringIntoView();
            RenderTests.Pump();
            var before = string.Join(",", page.DonutItems.Select(i => i.Id));

            var over = on(donut.OuterRadius - 40, 0);
            window.MouseDown(chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value, MouseButton.Left);
            window.MouseMove(over);

            Assert.True(Dragging(window));
            Assert.InRange(donut.SlotAt(window.TranslatePoint(over, donut)!.Value, page.SliceCount + 1), 0, page.SliceCount);
            Assert.Equal(group, donut.PreviewIsGroup);

            window.MouseMove(new Point(5, 5));
            window.MouseUp(new Point(5, 5), MouseButton.Left);
            Assert.Equal(before, string.Join(",", page.DonutItems.Select(i => i.Id)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_toolbar_preview_marks_group_slices_as_groups()
    {
        TestServices.EnsureInitialized();
        using var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        main.Navigate("toolbar");
        var window = new MainWindow { DataContext = main, Width = 1080, Height = 1000 };
        window.Show();
        try
        {
            var page = (ToolbarPageViewModel)main.CurrentPage!;
            page.SelectedProfile = page.Profiles.Single(p => p.Profile.Id == "pdf");
            page.EditingBack = true;
            RenderTests.Pump();
            var store = AppHost.Get<UserDataStore>();
            Assert.Contains(page.DonutItems, i => i.IsGroup);
            Assert.All(page.DonutItems, i => Assert.Equal(store.FindPreset(i.Id)?.IsGroup == true, i.IsGroup));
            // The chips the page shows, whatever the tab or the search.
            page.SelectedTab = page.Tabs.First();
            RenderTests.Pump();
            Assert.Contains(page.VisiblePalette, c => c.IsGroup);
            Assert.All(page.VisiblePalette, c => Assert.Equal(store.FindPreset(c.PresetId)?.IsGroup == true, c.IsGroup));
            RenderTests.Save(window, "page-toolbar-group-mark.png");
        }
        finally
        {
            window.Close();
        }
    }
}
