using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class PdfSourceBookmarksEditorTests
{
    [AvaloniaFact]
    public void Source_bookmarks_checkbox_saves_reopens_and_turns_off()
    {
        TestServices.EnsureInitialized();
        var preset = new Preset { Id = "test-source-bookmarks", Name = "Merge", TargetFormat = "pdf" };
        preset.Pdf.MergeIntoSingle = true;
        for (var opening = 0; opening < 3; opening++)
        {
            var expected = opening == 1;
            var window = Open(preset);
            try
            {
                var checkbox = Checkbox(window);
                Assert.True(checkbox.IsEffectivelyVisible);
                Assert.Equal(expected, checkbox.IsChecked);
                Assert.Equal(expected, Editor(window).SourceBookmarks);
                if (opening == 2)
                    break;
                checkbox.BringIntoView();
                RenderTests.Pump();
                var at = checkbox.TranslatePoint(new Point(checkbox.Bounds.Width / 2, checkbox.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                RenderTests.Pump();
                Assert.Equal(!expected, checkbox.IsChecked);
                Save(window);
                Assert.Equal(!expected, preset.Pdf.SourceBookmarks);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public void Source_bookmarks_visibility_tracks_merge_and_target_without_losing_the_choice()
    {
        TestServices.EnsureInitialized();
        var preset = new Preset { Id = "test-source-bookmark-targets", Name = "Merge", TargetFormat = "pdf" };
        preset.Pdf.MergeIntoSingle = true;
        preset.Pdf.SourceBookmarks = true;
        var window = Open(preset);
        try
        {
            Assert.True(Checkbox(window).IsEffectivelyVisible);
            Editor(window).Merge = false;
            RenderTests.Pump();
            Assert.False(Checkbox(window).IsEffectivelyVisible);
            Save(window);
            Assert.True(preset.Pdf.SourceBookmarks);
        }
        finally { window.Close(); }

        window = Open(preset);
        try
        {
            Assert.False(Checkbox(window).IsEffectivelyVisible);
            Assert.True(Checkbox(window).IsChecked);
            Editor(window).Merge = true;
            RenderTests.Pump();
            Assert.True(Checkbox(window).IsEffectivelyVisible);
            Save(window);
        }
        finally { window.Close(); }

        foreach (var target in new[] { "jpg", "mp4", "docx", "zip" })
        {
            window = Open(preset);
            try
            {
                var vm = Editor(window);
                vm.Target = vm.Targets.Single(choice => choice.Value == target);
                RenderTests.Pump();
                Assert.False(Checkbox(window).IsEffectivelyVisible);
                Save(window);
                Assert.Equal(target, preset.TargetFormat);
                Assert.True(preset.Pdf.SourceBookmarks);
            }
            finally { window.Close(); }

            window = Open(preset);
            try
            {
                Assert.False(Checkbox(window).IsEffectivelyVisible);
                Assert.True(Checkbox(window).IsChecked);
                var vm = Editor(window);
                vm.Target = vm.Targets.Single(choice => choice.Value == "pdf");
                RenderTests.Pump();
                Assert.True(Checkbox(window).IsEffectivelyVisible);
                Assert.True(Checkbox(window).IsChecked);
                Save(window);
                Assert.True(preset.Pdf.SourceBookmarks);
            }
            finally { window.Close(); }
        }
    }

    public static TheoryData<string, bool, double> Layouts
    {
        get
        {
            var data = new TheoryData<string, bool, double>();
            foreach (var language in new[] { "en", "ko", "ru", "es" })
                foreach (var dark in new[] { false, true })
                    foreach (var width in new[] { 440d, 560d })
                        data.Add(language, dark, width);
            return data;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Layouts))]
    public void Source_bookmarks_label_fits_the_minimum_editor_width(string language, bool dark, double width)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        var preset = new Preset { Id = "test-source-bookmark-layout", Name = "Merge", TargetFormat = "pdf" };
        preset.Pdf.MergeIntoSingle = true;
        preset.Pdf.SourceBookmarks = true;
        theme.Apply(new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light }, language);
        var window = Open(preset, width);
        try
        {
            var checkbox = Checkbox(window);
            checkbox.BringIntoView();
            RenderTests.Pump();
            var label = Assert.IsType<TextBlock>(checkbox.Content);
            Assert.Equal(AppHost.Get<ILocalizer>()["presets.source_bookmarks"], label.Text);
            Assert.True(checkbox.IsEffectivelyVisible);
            Assert.True(label.Bounds.Width > 0 && label.Bounds.Height > 0);
            Assert.True(label.TextLayout.Width <= label.Bounds.Width + 1);
            Assert.True(label.TextLayout.Height <= label.Bounds.Height + 1);
            Rect Box(Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
            var checkBounds = Box(checkbox);
            var labelBounds = Box(label);
            var glyph = checkbox.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "NormalRectangle");
            Assert.True(Box(glyph).Right <= labelBounds.Left);
            Assert.True(checkBounds.Contains(labelBounds));
            Assert.True(new Rect(window.ClientSize).Contains(checkBounds));
            var siblings = ((StackPanel)checkbox.Parent!).Children.Where(control => control != checkbox && control.IsEffectivelyVisible);
            Assert.All(siblings, sibling =>
                Assert.True(Box(sibling).Bottom <= checkBounds.Top || Box(sibling).Top >= checkBounds.Bottom));
            RenderTests.Save(window, $"source-bookmarks-{language}-{(dark ? "dark" : "light")}-{width}.png");
        }
        finally
        {
            window.Close();
            theme.Apply(new ThemeSettings(), language);
        }
    }

    private static PresetEditorWindow Open(Preset preset, double width = 560)
    {
        var window = new PresetEditorWindow
        {
            DataContext = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>()),
            Width = width,
        };
        window.Show();
        RenderTests.Pump();
        return window;
    }

    private static PresetEditorViewModel Editor(PresetEditorWindow window) =>
        Assert.IsType<PresetEditorViewModel>(window.DataContext);

    private static CheckBox Checkbox(PresetEditorWindow window) =>
        window.GetVisualDescendants().OfType<CheckBox>().Single(checkbox => checkbox.Name == "SourceBookmarks");

    private static void Save(PresetEditorWindow window)
    {
        window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, AppHost.Get<ILocalizer>()["common.save"]))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        RenderTests.Pump();
        Assert.True(window.Saved);
    }
}
