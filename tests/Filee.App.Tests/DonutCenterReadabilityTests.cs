using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Filee.App.Controls;
using Filee.App.Services;

namespace Filee.App.Tests;

[Collection("Motion")]
public class DonutCenterReadabilityTests
{
    public static TheoryData<string, string, string> DefaultCenters
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var language in LocalizationService.Languages.Select(x => x.Code))
                foreach (var scope in new[] { "single", "all", "some" })
                    foreach (var hint in new[] { "none", "key", "wheel" })
                        data.Add(language, scope, hint);
            return data;
        }
    }

    [AvaloniaTheory, MemberData(nameof(DefaultCenters))]
    public void Default_size_keeps_the_whole_notice(string language, string scope, string hint)
    {
        using var f = new PresetAvailabilityTests.Fixture(new OrdinaryConversionNoticeTests.Inputs());
        TestServices.EnsureInitialized(language);
        var donut = new DonutMenu
        {
            OuterRadius = 150,
            HoleRatio = 0.42,
            Items = [new() { Id = "one", Label = "" }],
            Foreground = Brushes.Black,
            SecondaryForeground = Brushes.Gray,
            CenterNotice = f.Loc["donut.ordinary_conversion." + scope],
            CenterSubtitle = f.Loc.Format("donut.drop_to", f.Loc["preset.extract_frames_png"]),
            CenterHint = hint == "none" ? null : "Tab back",
            CenterHintWheel = hint == "wheel",
        };
        donut.SetValue(TextElement.FontFamilyProperty, SoftPalette.FontFamilyFor(language));
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            RenderTests.Pump();
            Assert.NotEmpty(Runs(donut, donut.Foreground!));
            AssertInside(donut, Runs(donut, donut.Foreground!));
            AssertInside(donut, Runs(donut, donut.SecondaryForeground!));
            AssertRectsInside(donut, HintBounds(donut));
            AssertNoticeGap(donut, Runs(donut, donut.Foreground!), Runs(donut, donut.SecondaryForeground!));
            var lines = Lines(Layout(donut, donut.CenterNotice!));
            Assert.NotEmpty(lines);
            Assert.All(lines, line => Assert.False(line.HasCollapsed || line.HasOverflowed));
            Assert.True(lines.Sum(line => line.Length) >= donut.CenterNotice!.Length);
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    public static TheoryData<string, string, bool> SmallCenters
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            // Portuguese and Spanish had the longest notices in the default-size measurements.
            // Thai exercised the one-line fallback in the minimum-size measurements.
            foreach (var language in new[] { "en", "pt-BR", "pt-PT", "es", "ja", "th", "ar", "fa", "bn-BD", "hi", "ne" })
                foreach (var scope in new[] { "single", "all", "some" })
                    foreach (var click in new[] { false, true })
                        data.Add(language, scope, click);
            return data;
        }
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("de", false)]
    [InlineData("de", true)]
    [InlineData("es", true)]
    [InlineData("vi", true)]
    public void A_default_hub_with_a_flip_hint_prefers_the_complete_notice(string language, bool wheel)
    {
        using var f = new PresetAvailabilityTests.Fixture(new OrdinaryConversionNoticeTests.Inputs());
        TestServices.EnsureInitialized(language);
        var donut = new DonutMenu
        {
            OuterRadius = 150,
            HoleRatio = 0.42,
            Items = [new() { Id = "one", Label = "" }],
            Foreground = Brushes.Lime,
            SecondaryForeground = Brushes.Red,
            CenterNotice = f.Loc["donut.ordinary_conversion.all"],
            CenterSubtitle = f.Loc.Format("donut.drop_to", f.Loc["preset.extract_frames_png"]),
            CenterHint = "Tab back",
            CenterHintWheel = wheel,
        };
        donut.SetValue(TextElement.FontFamilyProperty, SoftPalette.FontFamilyFor(language));
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            RenderTests.Pump();
            var lines = Lines(Layout(donut, donut.CenterNotice!));
            Assert.All(lines, line => Assert.False(line.HasCollapsed || line.HasOverflowed));
            Assert.True(lines.Sum(line => line.Length) >= donut.CenterNotice!.Length);
            AssertInside(donut, Runs(donut, Brushes.Lime, centerOnly: true));
            AssertInside(donut, Runs(donut, Brushes.Red, centerOnly: true));
            AssertRectsInside(donut, HintBounds(donut));
            AssertNoticeGap(donut, Runs(donut, Brushes.Lime, centerOnly: true),
                Runs(donut, Brushes.Red, centerOnly: true).Where(run => run.Glyph.FontRenderingEmSize == 11.5).ToList());
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void A_small_hub_keeps_the_file_count_and_restores_it_after_a_notice()
    {
        using var f = new PresetAvailabilityTests.Fixture(new OrdinaryConversionNoticeTests.Inputs());
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu
        {
            OuterRadius = 100,
            HoleRatio = 0.25,
            Items = [new() { Id = "one", Label = "" }],
            Foreground = Brushes.Lime,
            SecondaryForeground = Brushes.Red,
            CenterTitle = "2 files",
            CenterSubtitle = f.Loc.Format("donut.drop_to", f.Loc["preset.extract_frames_png"]),
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            RenderTests.Pump();
            var original = Runs(donut, Brushes.Lime, centerOnly: true);
            Assert.NotEmpty(original);
            Assert.All(original, run => Assert.Equal(14, run.Glyph.FontRenderingEmSize));
            var originalBounds = original.Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)).ToArray();
            donut.CenterNotice = f.Loc["donut.ordinary_conversion.some"];
            Assert.DoesNotContain(Runs(donut, Brushes.Lime, centerOnly: true),
                run => run.Glyph.FontRenderingEmSize == 14);
            donut.CenterNotice = null;
            var restored = Runs(donut, Brushes.Lime, centerOnly: true);
            Assert.Equal(Glyphs(original), Glyphs(restored));
            Assert.Equal(originalBounds, restored.Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)).ToArray());
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaTheory, MemberData(nameof(SmallCenters))]
    public void A_small_hub_keeps_action_text_size_and_bounds_the_notice(string language, string scope, bool click)
    {
        using var f = new PresetAvailabilityTests.Fixture(new OrdinaryConversionNoticeTests.Inputs());
        TestServices.EnsureInitialized(language);
        var donut = new DonutMenu
        {
            OuterRadius = 100,
            HoleRatio = 0.25,
            Items = [new() { Id = "one", Label = "" }],
            Foreground = Brushes.Lime,
            SecondaryForeground = Brushes.Red,
            CenterSubtitle = f.Loc.Format(click ? "donut.click_to" : "donut.drop_to",
                f.Loc["preset.extract_frames_png"]),
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            RenderTests.Pump();
            var original = Runs(donut, Brushes.Red);
            Assert.NotEmpty(original);
            donut.CenterNotice = f.Loc["donut.ordinary_conversion." + scope];
            var action = Runs(donut, Brushes.Red);
            Assert.NotEmpty(action);
            Assert.Equal(Glyphs(original), Glyphs(action));
            Assert.All(action, run =>
            {
                Assert.Equal(11.5, run.Glyph.FontRenderingEmSize, 6);
                Assert.Equal(original[0].Transform.M11, run.Transform.M11, 6);
                Assert.Equal(original[0].Transform.M22, run.Transform.M22, 6);
            });
            var guidance = Layout(donut, donut.CenterSubtitle!);
            Assert.Equal(2, guidance.MaxLineCount);
            Assert.InRange(Lines(guidance).Count, 1, 2);
            AssertRectsInsideOrNoFurtherOut(donut, true,
                original.Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)),
                action.Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)));
            var notice = Runs(donut, Brushes.Lime);
            if (notice.Count > 0)
            {
                Assert.All(notice, run =>
                {
                    var effective = run.Glyph.FontRenderingEmSize / 11.5 * run.Transform.M11 / (100.0 / 150);
                    Assert.InRange(effective, 0.78 - 1e-6, 1 + 1e-6);
                });
                var layout = Layout(donut, donut.CenterNotice!);
                Assert.InRange(layout.MaxLineCount, 1, 3);
                Assert.InRange(Lines(layout).Count, 1, 2);
                AssertInside(donut, notice);
                AssertNoticeGap(donut, notice, action);
            }
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    public static TheoryData<string, bool, bool, string> HintCenters
    {
        get
        {
            var data = new TheoryData<string, bool, bool, string>();
            foreach (var language in new[] { "en", "ar", "bn-BD", "ne", "de", "fa", "ko" })
                foreach (var small in new[] { false, true })
                    foreach (var wheel in new[] { false, true })
                        foreach (var hint in new[] { "Tab back", "back", "…" })
                            data.Add(language, small, wheel, hint);
            return data;
        }
    }

    [AvaloniaTheory, MemberData(nameof(HintCenters))]
    public void Hint_text_and_stroked_icons_stay_inside_without_losing_guidance(string language, bool small, bool wheel, string hint)
    {
        using var f = new PresetAvailabilityTests.Fixture(new OrdinaryConversionNoticeTests.Inputs());
        TestServices.EnsureInitialized(language);
        var donut = new DonutMenu
        {
            OuterRadius = small ? 100 : 150,
            HoleRatio = small ? 0.25 : 0.42,
            Items = [new() { Id = "one", Label = "" }],
            Foreground = Brushes.Lime,
            SecondaryForeground = Brushes.Red,
            CenterTitle = "2 files",
            CenterSubtitle = f.Loc.Format("donut.drop_to", f.Loc["preset.extract_frames_png"]),
            CenterHint = hint,
            CenterHintWheel = wheel,
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            RenderTests.Pump();
            var original = Runs(donut, Brushes.Red, centerOnly: true);
            var originalIcons = HintBounds(donut);
            donut.CenterNotice = f.Loc["donut.ordinary_conversion.some"];
            var actionAndHint = Runs(donut, Brushes.Red, centerOnly: true);
            foreach (var size in new[] { 11.5, 10.5 })
                AssertRectsInsideOrNoFurtherOut(donut, small,
                    original.Where(run => run.Glyph.FontRenderingEmSize == size)
                        .Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)),
                    actionAndHint.Where(run => run.Glyph.FontRenderingEmSize == size)
                        .Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)));
            var action = actionAndHint.Where(run => run.Glyph.FontRenderingEmSize == 11.5).ToList();
            Assert.NotEmpty(action);
            Assert.All(action, run => Assert.Equal(11.5, run.Glyph.FontRenderingEmSize));
            var guidance = Layout(donut, donut.CenterSubtitle!);
            Assert.Equal(2, guidance.MaxLineCount);
            Assert.InRange(Lines(guidance).Count, 1, 2);
            var notice = Runs(donut, Brushes.Lime, centerOnly: true);
            if (notice.Count > 0)
            {
                AssertInside(donut, notice);
                Assert.All(notice, run => Assert.InRange(run.Glyph.FontRenderingEmSize / 11.5, 0.78 - 1e-6, 1));
                AssertNoticeGap(donut, notice,
                    actionAndHint.Where(run => run.Glyph.FontRenderingEmSize == 11.5).ToList());
            }
            else
            {
                // In a minimum hub with a flip hint, the notice yields before the action or icons.
                Assert.True(small);
            }
            var icons = HintBounds(donut);
            Assert.Equal(wheel ? 4 : 2, icons.Count);
            AssertRectsInsideOrNoFurtherOut(donut, small, originalIcons, icons);
            Assert.Equal(Glyphs(original.Where(run => run.Glyph.FontRenderingEmSize == 11.5).ToList()),
                Glyphs(action));
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    public static TheoryData<string, bool, string, string> SpacedCenters
    {
        get
        {
            var data = new TheoryData<string, bool, string, string>();
            foreach (var language in new[] { "en", "bn-BD", "hi", "th" })
                foreach (var small in new[] { false, true })
                    foreach (var hint in new[] { "none", "key", "wheel" })
                        data.Add(language, small, hint, "some");
            data.Add("ar", true, "wheel", "single");
            return data;
        }
    }

    [AvaloniaTheory, MemberData(nameof(SpacedCenters))]
    public void Notice_ink_keeps_the_subtitle_gap_without_taking_guidance_glyphs(string language, bool small, string hint, string scope)
    {
        using var f = new PresetAvailabilityTests.Fixture(new OrdinaryConversionNoticeTests.Inputs());
        TestServices.EnsureInitialized(language);
        var donut = new DonutMenu
        {
            OuterRadius = small ? 100 : 150,
            HoleRatio = small ? 0.25 : 0.42,
            Items = [new() { Id = "one", Label = "" }],
            Foreground = Brushes.Lime,
            SecondaryForeground = Brushes.Red,
            CenterSubtitle = f.Loc.Format("donut.drop_to", f.Loc["preset.extract_frames_png"]),
            CenterHint = hint == "none" ? null : "Tab back",
            CenterHintWheel = hint == "wheel",
        };
        donut.SetValue(TextElement.FontFamilyProperty, SoftPalette.FontFamilyFor(language));
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            RenderTests.Pump();
            var originalSecondary = Runs(donut, Brushes.Red, centerOnly: true);
            var original = originalSecondary.Where(run => run.Glyph.FontRenderingEmSize == 11.5).ToList();
            var originalIcons = HintBounds(donut);
            donut.CenterNotice = f.Loc["donut.ordinary_conversion." + scope];
            var notice = Runs(donut, Brushes.Lime, centerOnly: true);
            var secondary = Runs(donut, Brushes.Red, centerOnly: true);
            var action = secondary.Where(run => run.Glyph.FontRenderingEmSize == 11.5).ToList();
            Assert.NotEmpty(action);
            Assert.Equal(Glyphs(original), Glyphs(action));
            Assert.All(action, run => Assert.Equal(11.5, run.Glyph.FontRenderingEmSize));
            var guidance = Layout(donut, donut.CenterSubtitle!);
            Assert.Equal(2, guidance.MaxLineCount);
            Assert.InRange(Lines(guidance).Count, 1, 2);
            foreach (var size in new[] { 11.5, 10.5 })
                AssertRectsInsideOrNoFurtherOut(donut, small,
                    originalSecondary.Where(run => run.Glyph.FontRenderingEmSize == size)
                        .Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)),
                    secondary.Where(run => run.Glyph.FontRenderingEmSize == size)
                        .Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)));
            AssertRectsInsideOrNoFurtherOut(donut, small, originalIcons, HintBounds(donut));
            if (notice.Count > 0)
            {
                AssertInside(donut, notice);
                AssertNoticeGap(donut, notice, action);
                Assert.All(notice, run => Assert.InRange(run.Glyph.FontRenderingEmSize / 11.5, 0.78 - 1e-6, 1));
            }
            else
            {
                Assert.True(small);
            }
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaTheory]
    [InlineData("none")]
    [InlineData("key")]
    [InlineData("wheel")]
    public void An_unplaceable_notice_preserves_the_no_notice_guidance_and_hint(string hint)
    {
        using var f = new PresetAvailabilityTests.Fixture(new OrdinaryConversionNoticeTests.Inputs());
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu
        {
            OuterRadius = 100,
            // Simulate glyph ink too wide/tall for the hub without depending on OS font metrics.
            HoleRatio = 0.12,
            Items = [new() { Id = "one", Label = "" }],
            Foreground = Brushes.Lime,
            SecondaryForeground = Brushes.Red,
            CenterTitle = "2 files",
            CenterSubtitle = f.Loc.Format("donut.drop_to", f.Loc["preset.extract_frames_png"]),
            CenterHint = hint == "none" ? null : "Tab back",
            CenterHintWheel = hint == "wheel",
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            RenderTests.Pump();
            var original = Runs(donut, Brushes.Red, centerOnly: true);
            var originalBounds = original.Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)).ToArray();
            var originalIcons = HintBounds(donut);
            donut.CenterNotice = f.Loc["donut.ordinary_conversion.some"];
            var current = Runs(donut, Brushes.Red, centerOnly: true);
            Assert.Empty(Runs(donut, Brushes.Lime, centerOnly: true));
            Assert.Equal(Glyphs(original), Glyphs(current));
            Assert.Equal(originalBounds, current.Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)).ToArray());
            AssertRectsInsideOrNoFurtherOut(donut, true, originalIcons, HintBounds(donut));
            var guidance = Layout(donut, donut.CenterSubtitle!);
            Assert.Equal(2, guidance.MaxLineCount);
            Assert.All(current.Where(run => run.Glyph.FontRenderingEmSize == 11.5),
                run => Assert.Equal(100.0 / 150, run.Transform.M11, 6));
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaTheory]
    [InlineData("fa", "some", 0.40)]
    [InlineData("hi", "all", 0.38)]
    public void A_wide_notice_uses_the_circle_before_collapsing_text(string language, string scope, double holeRatio)
    {
        using var f = new PresetAvailabilityTests.Fixture(new OrdinaryConversionNoticeTests.Inputs());
        TestServices.EnsureInitialized(language);
        var donut = new DonutMenu
        {
            OuterRadius = 150,
            // Tighten the ink budget to model larger platform metrics with real script glyphs.
            HoleRatio = holeRatio,
            Items = [new() { Id = "one", Label = "" }],
            Foreground = Brushes.Lime,
            SecondaryForeground = Brushes.Red,
            CenterSubtitle = f.Loc.Format("donut.drop_to", f.Loc["preset.extract_frames_png"]),
            CenterHint = "Tab back",
            CenterHintWheel = true,
        };
        donut.SetValue(TextElement.FontFamilyProperty, SoftPalette.FontFamilyFor(language));
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            RenderTests.Pump();
            var original = Runs(donut, Brushes.Red, centerOnly: true)
                .Where(run => run.Glyph.FontRenderingEmSize == 11.5).ToList();
            Assert.NotEmpty(original);
            donut.CenterNotice = f.Loc["donut.ordinary_conversion." + scope];
            var lines = Lines(Layout(donut, donut.CenterNotice!));
            Assert.NotEmpty(lines);
            Assert.All(lines, line => Assert.False(line.HasCollapsed || line.HasOverflowed));
            Assert.True(lines.Sum(line => line.Length) >= donut.CenterNotice.Length);
            var notice = Runs(donut, Brushes.Lime, centerOnly: true);
            Assert.All(notice, run => Assert.InRange(run.Glyph.FontRenderingEmSize / 11.5, 0.78 - 1e-6, 1));
            AssertInside(donut, notice);
            AssertInside(donut, Runs(donut, Brushes.Red, centerOnly: true));
            AssertRectsInside(donut, HintBounds(donut));
            var action = Runs(donut, Brushes.Red, centerOnly: true)
                .Where(run => run.Glyph.FontRenderingEmSize == 11.5).ToList();
            Assert.Equal(Glyphs(original), Glyphs(action));
            var guidance = Layout(donut, donut.CenterSubtitle!);
            Assert.Equal(2, guidance.MaxLineCount);
            Assert.InRange(Lines(guidance).Count, 1, 2);
            AssertNoticeGap(donut, notice, action);
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void Stable_frames_reuse_the_selected_layout_and_changed_inputs_replan()
    {
        using var f = new PresetAvailabilityTests.Fixture(new OrdinaryConversionNoticeTests.Inputs());
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu
        {
            OuterRadius = 100,
            HoleRatio = 0.25,
            Items = [new() { Id = "one", Label = "" }],
            CenterNotice = f.Loc["donut.ordinary_conversion.some"],
            CenterSubtitle = f.Loc.Format("donut.drop_to", f.Loc["preset.extract_frames_png"]),
            Foreground = Brushes.Lime,
            SecondaryForeground = Brushes.Red,
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            RenderTests.Pump();
            var field = typeof(DonutMenu).GetField("_centerPlans", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var cache = (IDictionary)field.GetValue(donut)!;
            object Current() => cache.Values.Cast<object>().Last();
            var first = Current();
            var count = cache.Count;
            for (var frame = 0; frame < 5; frame++)
                Runs(donut, donut.Foreground!, centerOnly: true);
            Assert.Equal(count, cache.Count);
            Assert.Same(first, Current());
            foreach (var change in new Action[]
            {
                () => donut.CenterSubtitle += " PDF",
                () => donut.CenterHint = "Tab back",
                () => donut.CenterHintWheel = true,
                () => donut.HoleRatio = 0.26,
                () => donut.OuterRadius = 101,
                () => donut.Foreground = Brushes.Blue,
                () => donut.SecondaryForeground = Brushes.Green,
                () => TestServices.EnsureInitialized("ar"),
            })
            {
                var prior = Current();
                change();
                Runs(donut, donut.Foreground!, centerOnly: true);
                Assert.NotSame(prior, Current());
            }
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    internal static int[] Glyphs(List<(GlyphRun Glyph, Matrix Transform)> runs) =>
        runs.SelectMany(run => run.Glyph.GlyphInfos.Select(info => (int)info.GlyphIndex)).ToArray();

    internal static string Visible(List<(GlyphRun Glyph, Matrix Transform)> runs) =>
        string.Join(" / ", runs.Select(run => new string(run.Glyph.Characters.Span)));

    internal static DrawingGroup CenterDrawing(DonutMenu donut)
    {
        var frame = new DrawingGroup();
        using var context = frame.Open();
        typeof(DonutMenu).GetMethod("DrawCenter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(donut,
            [context, new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2), donut.OuterRadius * donut.HoleRatio,
                new Typeface(donut.GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold),
                new Typeface(donut.GetValue(TextElement.FontFamilyProperty))]);
        return frame;
    }

    internal static List<Rect> HintBounds(DonutMenu donut)
    {
        var result = new List<Rect>();
        void Walk(Drawing item, Matrix transform)
        {
            if (item is DrawingGroup group)
            {
                var within = (group.Transform?.Value ?? Matrix.Identity) * transform;
                foreach (var child in group.Children) Walk(child, within);
            }
            else if (item is GeometryDrawing { Geometry: { } geometry } drawing
                && (ReferenceEquals(drawing.Brush, donut.SecondaryForeground)
                    || ReferenceEquals(drawing.Pen?.Brush, donut.SecondaryForeground)))
                result.Add((drawing.Pen is { } pen ? geometry.GetRenderBounds(pen) : geometry.Bounds).TransformToAABB(transform));
        }
        Walk(CenterDrawing(donut), Matrix.Identity);
        return result;
    }

    internal static void AssertInside(DonutMenu donut, List<(GlyphRun Glyph, Matrix Transform)> runs)
    {
        Assert.NotEmpty(runs);
        AssertRectsInside(donut, runs.Select(run => run.Glyph.InkBounds.TransformToAABB(run.Transform)));
    }

    internal static void AssertNoticeGap(DonutMenu donut, List<(GlyphRun Glyph, Matrix Transform)> notice,
        List<(GlyphRun Glyph, Matrix Transform)> guidance)
    {
        Assert.NotEmpty(notice);
        Assert.NotEmpty(guidance);
        var noticeBottom = notice.Max(run => run.Glyph.InkBounds.TransformToAABB(run.Transform).Bottom);
        var guidanceTop = guidance.Min(run => run.Glyph.InkBounds.TransformToAABB(run.Transform).Top);
        var gap = (guidanceTop - noticeBottom) / donut.DonutScale;
        Assert.True(gap >= 2 - 1e-6, $"Notice-to-guidance ink gap {gap:F6} is below 2 donut-coordinate pixels.");
    }

    [AvaloniaTheory]
    [InlineData(true, 18, 18.5, true)]
    [InlineData(true, 18, 19.2, false)]
    [InlineData(true, 20, 20.9, true)]
    [InlineData(true, 20, 21.01, false)]
    [InlineData(false, 20, 20.9, false)]
    public void Baseline_rounding_tolerance_applies_only_to_overflowing_small_hubs(
        bool small, double baselineDistance, double distance, bool passes)
    {
        var donut = new DonutMenu { OuterRadius = 100, HoleRatio = 0.25 };
        var error = Record.Exception(() => AssertRectsInsideOrNoFurtherOut(donut, small,
            [new Rect(baselineDistance, 0, 0, 0)], [new Rect(distance, 0, 0, 0)]));
        if (passes) Assert.Null(error);
        else Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error);
    }

    private static void AssertRectsInsideOrNoFurtherOut(DonutMenu donut, bool small,
        IEnumerable<Rect> original, IEnumerable<Rect> current)
    {
        var before = original.ToArray();
        var after = current.ToArray();
        if (before.Length > 0) Assert.NotEmpty(after);
        var center = new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
        var radius = donut.OuterRadius * donut.HoleRatio - 6;
        double MaxDistance(IEnumerable<Rect> rectangles) => rectangles
            .SelectMany(bounds => new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight })
            .Select(point => Math.Sqrt(Math.Pow(point.X - center.X, 2) + Math.Pow(point.Y - center.Y, 2)))
            .DefaultIfEmpty(0).Max();
        var baselineDistance = MaxDistance(before);
        if (small && baselineDistance > radius + 0.01)
        {
            // Only an already-overflowing baseline gets 1 DIP for OS glyph-bound rounding.
            const double roundingTolerance = 1;
            var distance = MaxDistance(after);
            Assert.True(distance <= baselineDistance + roundingTolerance,
                $"Drawn bounds distance {distance:F3} exceeds no-notice baseline {baselineDistance:F3} plus {roundingTolerance} DIP.");
        }
        else
        {
            AssertRectsInside(donut, after);
        }
    }

    internal static void AssertRectsInside(DonutMenu donut, IEnumerable<Rect> rectangles)
    {
        var center = new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
        var radius = donut.OuterRadius * donut.HoleRatio - 6;
        foreach (var bounds in rectangles)
        {
            foreach (var point in new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight })
            {
                var distance = Math.Sqrt(Math.Pow(point.X - center.X, 2) + Math.Pow(point.Y - center.Y, 2));
                Assert.True(distance <= radius + 0.01,
                    $"Drawn text corner {point}, bounds {bounds}, distance {distance:F3} exceeds hub radius {radius:F3}.");
            }
        }
    }

    internal static List<(GlyphRun Glyph, Matrix Transform)> Runs(DonutMenu donut, IBrush brush, bool centerOnly = false)
    {
        var frame = new DrawingGroup();
        using (var context = frame.Open())
        {
            if (centerOnly)
                typeof(DonutMenu).GetMethod("DrawCenter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(donut,
                    [context, new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2), donut.OuterRadius * donut.HoleRatio,
                        new Typeface(donut.GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold),
                        new Typeface(donut.GetValue(TextElement.FontFamilyProperty))]);
            else
                donut.Render(context);
        }
        return TextRuns(frame, brush);
    }

    private static List<(GlyphRun Glyph, Matrix Transform)> TextRuns(DrawingGroup frame, IBrush brush)
    {
        var result = new List<(GlyphRun, Matrix)>();
        void Walk(Drawing drawing, Matrix around)
        {
            if (drawing is DrawingGroup group)
            {
                var within = (group.Transform?.Value ?? Matrix.Identity) * around;
                foreach (var child in group.Children)
                    Walk(child, within);
            }
            else if (drawing is GlyphRunDrawing { GlyphRun: { } glyph } text && ReferenceEquals(text.Foreground, brush))
                result.Add((glyph, around));
        }
        Walk(frame, Matrix.Identity);
        return result;
    }

    internal static FormattedText Layout(DonutMenu donut, string text)
    {
        var cache = (Dictionary<(string, Typeface, double, IBrush?, double, int, string), FormattedText>)
            typeof(DonutMenu).GetField("_textLayouts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(donut)!;
        var brush = (text == donut.CenterNotice ? donut.Foreground : donut.SecondaryForeground)!;
        var actual = Runs(donut, brush, centerOnly: true);
        if (text == donut.CenterSubtitle)
            actual = actual.Where(run => Math.Abs(run.Glyph.FontRenderingEmSize - 11.5) < 1e-6).ToList();
        var scale = donut.OuterRadius / 150;
        var centerX = donut.Bounds.Width / 2;
        var firstTop = actual[0].Glyph.InkBounds.TransformToAABB(actual[0].Transform).Top;
        foreach (var entry in cache.Where(entry => entry.Key.Item1 == text).Reverse())
        {
            var frame = new DrawingGroup();
            using (var context = frame.Open())
                DonutMenu.DrawCentered(context, entry.Value, 0, 0);
            var candidate = TextRuns(frame, brush);
            if (candidate.Count != actual.Count) continue;
            var candidateTop = candidate[0].Glyph.InkBounds.TransformToAABB(candidate[0].Transform).Top;
            var matches = actual.Zip(candidate).All(pair =>
            {
                var (drawn, expected) = pair;
                var a = drawn.Glyph.InkBounds.TransformToAABB(drawn.Transform);
                var b = expected.Glyph.InkBounds.TransformToAABB(expected.Transform);
                return drawn.Glyph.FontRenderingEmSize == expected.Glyph.FontRenderingEmSize
                    && drawn.Glyph.GlyphInfos.Select(info => info.GlyphIndex).SequenceEqual(expected.Glyph.GlyphInfos.Select(info => info.GlyphIndex))
                    && Math.Abs((a.Left - centerX) / scale - b.Left) < 0.001
                    && Math.Abs((a.Top - firstTop) / scale - (b.Top - candidateTop)) < 0.001
                    && Math.Abs(a.Width / scale - b.Width) < 0.001
                    && Math.Abs(a.Height / scale - b.Height) < 0.001;
            });
            if (matches) return entry.Value;
        }
        throw new InvalidOperationException("No cached layout matches the actual drawn glyphs.");
    }

    internal static List<TextLine> Lines(FormattedText text)
    {
        var enumerator = (IEnumerator)typeof(FormattedText).GetMethod("GetEnumerator",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(text, null)!;
        var result = new List<TextLine>();
        while (enumerator.MoveNext())
            result.Add((TextLine)enumerator.Current);
        return result;
    }
}
