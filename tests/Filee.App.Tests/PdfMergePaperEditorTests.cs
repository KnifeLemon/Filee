using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class PdfMergePaperEditorTests
{
    [AvaloniaFact]
    public void Merge_paper_controls_save_reopen_and_turn_off_without_losing_choices()
    {
        TestServices.EnsureInitialized();
        var preset = new Preset { Id = "test-merge-paper", Name = "Merge", TargetFormat = "pdf" };
        preset.Pdf.MergeIntoSingle = true;
        for (var opening = 0; opening < 3; opening++)
        {
            var window = Open(preset);
            try
            {
                var checkbox = Find<CheckBox>(window, "NormalizeMergePaper");
                var settings = Find<StackPanel>(window, "MergePaperSettings");
                var paper = Find<ComboBox>(window, "MergePaper");
                var orientation = Find<ComboBox>(window, "MergeOrientation");
                var enabled = opening == 1;
                var expectedPaper = opening == 0 ? PdfSheetPaper.A4 : PdfSheetPaper.Letter;
                var expectedOrientation = opening == 0 ? PdfSheetOrientation.Auto : PdfSheetOrientation.Landscape;
                Assert.True(checkbox.IsEffectivelyVisible);
                Assert.Equal(enabled, checkbox.IsChecked);
                Assert.Equal(enabled, settings.IsEffectivelyVisible);
                Assert.Equal(expectedPaper, Assert.IsType<Choice<PdfSheetPaper>>(paper.SelectedItem).Value);
                Assert.Equal(expectedOrientation, Assert.IsType<Choice<PdfSheetOrientation>>(orientation.SelectedItem).Value);
                if (opening == 2)
                    break;

                Click(window, checkbox);
                Assert.Equal(!enabled, checkbox.IsChecked);
                Assert.Equal(!enabled, settings.IsEffectivelyVisible);
                if (opening == 0)
                {
                    var vm = Editor(window);
                    paper.SelectedItem = vm.SheetPapers.Single(choice => choice.Value == PdfSheetPaper.Letter);
                    orientation.SelectedItem = vm.SheetOrientations.Single(choice => choice.Value == PdfSheetOrientation.Landscape);
                    RenderTests.Pump();
                    Assert.Equal(PdfSheetPaper.Letter, vm.MergePaper.Value);
                    Assert.Equal(PdfSheetOrientation.Landscape, vm.MergeOrientation.Value);
                    Assert.False(preset.Pdf.NormalizeMergePaper);
                    Assert.Equal(PdfSheetPaper.A4, preset.Pdf.MergePaper);
                    Assert.Equal(PdfSheetOrientation.Auto, preset.Pdf.MergeOrientation);
                }
                Save(window);
                Assert.Equal(!enabled, preset.Pdf.NormalizeMergePaper);
                Assert.Equal(PdfSheetPaper.Letter, preset.Pdf.MergePaper);
                Assert.Equal(PdfSheetOrientation.Landscape, preset.Pdf.MergeOrientation);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public void Merge_paper_visibility_tracks_merge_and_target_without_losing_choices()
    {
        TestServices.EnsureInitialized();
        var preset = new Preset { Id = "test-merge-paper-targets", Name = "Merge", TargetFormat = "pdf" };
        preset.Pdf.MergeIntoSingle = true;
        preset.Pdf.NormalizeMergePaper = true;
        preset.Pdf.MergePaper = PdfSheetPaper.A3;
        preset.Pdf.MergeOrientation = PdfSheetOrientation.Portrait;
        var window = Open(preset);
        try
        {
            AssertVisible(window, true);
            var merge = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(control => Equals(control.Content, AppHost.Get<ILocalizer>()["presets.merge"]));
            Click(window, merge);
            AssertVisible(window, false);
            Save(window);
            AssertSavedChoices(preset);
        }
        finally { window.Close(); }

        window = Open(preset);
        try
        {
            AssertVisible(window, false);
            Assert.True(Find<CheckBox>(window, "NormalizeMergePaper").IsChecked);
            var merge = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(control => Equals(control.Content, AppHost.Get<ILocalizer>()["presets.merge"]));
            Click(window, merge);
            AssertVisible(window, true);
            Save(window);
            AssertSavedChoices(preset);
        }
        finally { window.Close(); }

        foreach (var target in new[] { "jpg", "mp4", "docx", "zip" })
        {
            window = Open(preset);
            try
            {
                Find<ComboBox>(window, "TargetBox").SelectedItem = Editor(window).Targets.Single(choice => choice.Value == target);
                RenderTests.Pump();
                AssertVisible(window, false);
                Save(window);
                Assert.Equal(target, preset.TargetFormat);
                AssertSavedChoices(preset);
            }
            finally { window.Close(); }

            window = Open(preset);
            try
            {
                AssertVisible(window, false);
                Assert.True(Find<CheckBox>(window, "NormalizeMergePaper").IsChecked);
                Find<ComboBox>(window, "TargetBox").SelectedItem = Editor(window).Targets.Single(choice => choice.Value == "pdf");
                RenderTests.Pump();
                AssertVisible(window, true);
                Assert.Equal(PdfSheetPaper.A3, Assert.IsType<Choice<PdfSheetPaper>>(Find<ComboBox>(window, "MergePaper").SelectedItem).Value);
                Assert.Equal(PdfSheetOrientation.Portrait, Assert.IsType<Choice<PdfSheetOrientation>>(Find<ComboBox>(window, "MergeOrientation").SelectedItem).Value);
                Save(window);
                AssertSavedChoices(preset);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("ru")]
    public void Merge_paper_controls_fit_the_default_editor_layout(string language)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        theme.Apply(new ThemeSettings(), language);
        var preset = new Preset { Id = "test-merge-paper-layout", Name = "Merge", TargetFormat = "pdf" };
        preset.Pdf.MergeIntoSingle = true;
        preset.Pdf.NormalizeMergePaper = true;
        preset.Pdf.MergePaper = PdfSheetPaper.SameAsFirstPage;
        var window = Open(preset);
        try
        {
            var checkbox = Find<CheckBox>(window, "NormalizeMergePaper");
            var settings = Find<StackPanel>(window, "MergePaperSettings");
            settings.BringIntoView();
            RenderTests.Pump();
            AssertVisible(window, true);
            Rect Box(Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
            var checkboxBounds = Box(checkbox);
            var settingsBounds = Box(settings);
            Assert.True(new Rect(window.ClientSize).Contains(checkboxBounds));
            Assert.True(new Rect(window.ClientSize).Contains(settingsBounds));
            Assert.True(checkboxBounds.Bottom <= settingsBounds.Top);

            var label = Assert.IsType<TextBlock>(checkbox.Content);
            var glyph = checkbox.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "NormalRectangle");
            Assert.True(Box(glyph).Right <= Box(label).Left);
            Assert.True(checkboxBounds.Contains(Box(label)));
            var captions = ((StackPanel)checkbox.Parent!).Children.OfType<TextBlock>();
            var textBlocks = settings.GetVisualDescendants().OfType<TextBlock>().Concat(captions).Append(label)
                .Where(text => text.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(textBlocks);
            Assert.All(textBlocks, text =>
            {
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.True(text.Bounds.Width > 0 && text.Bounds.Height > 0);
                Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1);
                Assert.True(text.TextLayout.Height <= text.Bounds.Height + 1);
                Assert.True(new Rect(window.ClientSize).Contains(Box(text)));
            });
            var rows = settings.Children.Where(control => control.IsEffectivelyVisible).ToArray();
            for (var index = 1; index < rows.Length; index++)
                Assert.True(Box(rows[index - 1]).Bottom <= Box(rows[index]).Top);
            var siblings = ((StackPanel)settings.Parent!).Children
                .Where(control => control != settings && control.IsEffectivelyVisible);
            Assert.All(siblings, sibling =>
                Assert.True(Box(sibling).Bottom <= settingsBounds.Top || Box(sibling).Top >= settingsBounds.Bottom));
            foreach (var control in new Control[] { checkbox, Find<ComboBox>(window, "MergePaper"), Find<ComboBox>(window, "MergeOrientation") })
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)));
            RenderTests.Save(window, $"merge-paper-{language}-560.png");
        }
        finally
        {
            window.Close();
            theme.Apply(new ThemeSettings(), language);
        }
    }

    private static void AssertSavedChoices(Preset preset)
    {
        Assert.True(preset.Pdf.NormalizeMergePaper);
        Assert.Equal(PdfSheetPaper.A3, preset.Pdf.MergePaper);
        Assert.Equal(PdfSheetOrientation.Portrait, preset.Pdf.MergeOrientation);
    }

    private static void AssertVisible(PresetEditorWindow window, bool visible)
    {
        Assert.Equal(visible, Find<CheckBox>(window, "NormalizeMergePaper").IsEffectivelyVisible);
        Assert.Equal(visible, Find<StackPanel>(window, "MergePaperSettings").IsEffectivelyVisible);
        Assert.Equal(visible, Find<ComboBox>(window, "MergePaper").IsEffectivelyVisible);
        Assert.Equal(visible, Find<ComboBox>(window, "MergeOrientation").IsEffectivelyVisible);
    }

    private static PresetEditorWindow Open(Preset preset)
    {
        var window = new PresetEditorWindow
        {
            DataContext = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>()),
        };
        window.Show();
        RenderTests.Pump();
        return window;
    }

    private static PresetEditorViewModel Editor(PresetEditorWindow window) =>
        Assert.IsType<PresetEditorViewModel>(window.DataContext);

    private static T Find<T>(PresetEditorWindow window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Click(PresetEditorWindow window, CheckBox checkbox)
    {
        checkbox.BringIntoView();
        RenderTests.Pump();
        var at = checkbox.TranslatePoint(new Point(checkbox.Bounds.Width / 2, checkbox.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        RenderTests.Pump();
    }

    private static void Save(PresetEditorWindow window)
    {
        window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, AppHost.Get<ILocalizer>()["common.save"]))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        RenderTests.Pump();
        Assert.True(window.Saved);
    }
}
