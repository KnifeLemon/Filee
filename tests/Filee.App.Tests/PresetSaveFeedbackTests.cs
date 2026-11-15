// The Presets page reports real write results and keeps failed edits available without changing the saved library.

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("Motion")]
public sealed class PresetSaveFeedbackTests
{
    public static TheoryData<string> Languages => new(LocalizationService.Languages.Select(l => l.Code));

    private sealed class Fixture : IDisposable
    {
        internal readonly string Folder = Directory.CreateTempSubdirectory("filee-save-feedback-").FullName;
        internal readonly UserDataStore Store;
        internal readonly PresetsPageViewModel ViewModel;

        internal Fixture()
        {
            TestServices.EnsureInitialized("en");
            Store = new UserDataStore(Folder);
            Store.Presets.Clear();
            Store.Presets.Add(new Preset { Id = "feedback", Name = "Original", TargetFormat = "png" });
            Store.SaveLibrary();
            ViewModel = new PresetsPageViewModel(Store, AppHost.Get<ILocalizer>(), AppHost.Get<WindowService>());
        }

        public void Dispose() => Directory.Delete(Folder, true);
    }

    [AvaloniaFact]
    public void Success_is_reported_after_the_library_is_written_and_can_be_repeated()
    {
        using var f = new Fixture();
        var results = new List<SaveResult>();
        f.Store.LibraryChanged += (_, _) => results.Add(f.ViewModel.SaveResult);
        f.ViewModel.Editor!.Name = "Changed";
        f.ViewModel.SaveCommand.Execute(null);
        Assert.Equal([SaveResult.None], results);
        Assert.Equal(SaveResult.Succeeded, f.ViewModel.SaveResult);
        Assert.Equal("Saved", f.ViewModel.SaveStatus);
        Assert.Null(f.ViewModel.SaveError);
        var reloaded = new UserDataStore(f.Folder);
        reloaded.Load();
        Assert.Equal("Changed", reloaded.FindPreset("feedback")!.Name);

        var transitions = new List<SaveResult>();
        f.ViewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(f.ViewModel.SaveResult)) transitions.Add(f.ViewModel.SaveResult); };
        f.ViewModel.SaveCommand.Execute(null);
        Assert.Equal([SaveResult.None, SaveResult.Succeeded], transitions);
        f.ViewModel.Editor!.Name = "Unsaved";
        f.ViewModel.RevertCommand.Execute(null);
        Assert.Equal("Changed", f.ViewModel.Editor!.Name);
        Assert.Equal(SaveResult.None, f.ViewModel.SaveResult);
    }

    [AvaloniaTheory]
    [InlineData("name")]
    [InlineData("range")]
    [InlineData("size")]
    public void Validation_explains_the_field_and_does_not_write(string invalid)
    {
        using var f = new Fixture();
        var editor = f.ViewModel.Editor!;
        switch (invalid)
        {
            case "name": editor.Name = " "; break;
            case "range":
                editor.Target = editor.Targets.Single(c => c.Value == "jpg");
                editor.ImageMinSize = 5;
                editor.ImageMaxSize = 1;
                break;
            case "size":
                editor.Resize = editor.ResizeModes.Single(c => c.Value == ResizeMode.Custom);
                editor.CustomWidth = null;
                editor.CustomHeight = null;
                break;
        }
        var before = File.ReadAllText(Path.Combine(f.Folder, "presets.json"));
        var revealed = false;
        f.ViewModel.ValidationFailed += (_, _) => revealed = true;
        f.ViewModel.SaveCommand.Execute(null);
        Assert.True(revealed);
        Assert.Equal(SaveResult.Failed, f.ViewModel.SaveResult);
        Assert.Equal(editor.NameError ?? editor.SizeRangeError ?? editor.DestinationError ?? editor.CustomSizeError, f.ViewModel.SaveError);
        Assert.False(string.IsNullOrWhiteSpace(f.ViewModel.SaveError));
        Assert.Equal(before, File.ReadAllText(Path.Combine(f.Folder, "presets.json")));
        Assert.Equal("Original", f.Store.FindPreset("feedback")!.Name);
    }

    [AvaloniaFact]
    public void Write_failure_is_readable_edits_can_be_retried_and_revert_keeps_the_saved_values()
    {
        using var f = new Fixture();
        var blocked = Path.Combine(f.Folder, "presets.json.tmp");
        Directory.CreateDirectory(blocked); // A deterministic access failure on Windows and Unix, even as root.
        var before = File.ReadAllText(Path.Combine(f.Folder, "presets.json"));
        f.ViewModel.Editor!.Name = "Retry this";
        f.ViewModel.SaveCommand.Execute(null);
        Assert.Equal(SaveResult.Failed, f.ViewModel.SaveResult);
        Assert.StartsWith("Could not save presets: ", f.ViewModel.SaveError);
        Assert.Contains("presets.json.tmp", f.ViewModel.SaveError);
        Assert.Equal("Retry this", f.ViewModel.Editor!.Name);
        Assert.Equal("Original", f.Store.FindPreset("feedback")!.Name);
        Assert.Equal(before, File.ReadAllText(Path.Combine(f.Folder, "presets.json")));
        f.ViewModel.RevertCommand.Execute(null);
        Assert.Equal("Original", f.ViewModel.Editor!.Name);
        Assert.Null(f.ViewModel.SaveError);
        Directory.Delete(blocked);
        f.ViewModel.Editor.Name = "Retry succeeded";
        f.ViewModel.SaveCommand.Execute(null);
        Assert.Equal(SaveResult.Succeeded, f.ViewModel.SaveResult);
        Assert.Null(f.ViewModel.SaveError);
        Assert.Equal("Retry succeeded", f.Store.FindPreset("feedback")!.Name);
    }

    [AvaloniaFact]
    public void Switching_presets_clears_the_previous_failure()
    {
        using var f = new Fixture();
        f.ViewModel.Editor!.Name = "";
        f.ViewModel.SaveCommand.Execute(null);
        f.ViewModel.SelectedPreset = null;
        Assert.Null(f.ViewModel.SaveStatus);
        Assert.Equal(SaveResult.None, f.ViewModel.SaveResult);
        Assert.Null(f.ViewModel.Editor);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public void Save_feedback_is_translated_in_every_catalog(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        Assert.NotEqual("presets.saved", loc["presets.saved"]);
        Assert.False(string.IsNullOrWhiteSpace(loc["presets.saved"]));
        var failure = loc.Format("presets.save_failed", "write-error-detail");
        Assert.Contains("write-error-detail", failure);
        Assert.DoesNotContain("{0}", failure);
        if (language != "en")
            Assert.DoesNotContain("Could not save presets", failure);
    }

    [AvaloniaFact]
    public void Invalid_field_is_brought_into_view_even_when_the_editor_is_scrolled_away()
    {
        using var f = new Fixture();
        var page = new PresetsPage { DataContext = f.ViewModel };
        var window = new Window { Content = page, Width = 980, Height = 420 };
        try
        {
            window.Show();
            RenderTests.Pump();
            var scroll = page.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Content is StackPanel panel && panel.Children.OfType<Filee.App.Views.PresetEditorView>().Any());
            scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height);
            RenderTests.Pump();
            f.ViewModel.Editor!.Name = "";
            f.ViewModel.SaveCommand.Execute(null);
            RenderTests.Pump();
            var error = page.FindControl<Filee.App.Views.PresetEditorView>("PresetEditor")!.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == "Enter a name");
            var point = error.TranslatePoint(default, scroll)!.Value;
            Assert.InRange(point.Y, 0, scroll.Viewport.Height - error.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Button_keeps_its_size_restores_its_label_and_respects_reduced_motion(bool reducedMotion, bool failure)
    {
        using var f = new Fixture();
        AppHost.Get<ThemeService>().Apply(new ThemeSettings { Mode = reducedMotion ? ThemeMode.Dark : ThemeMode.Light }, "en");
        Motion.Set(!reducedMotion);
        var page = new PresetsPage { DataContext = f.ViewModel };
        var window = new Window { Content = page, Width = 980, Height = 760 };
        try
        {
            window.Show();
            RenderTests.Pump();
            var button = page.FindControl<Button>("SavePresetButton")!;
            var feedback = page.FindControl<SaveFeedback>("SavePresetFeedback")!;
            var bounds = button.Bounds;
            if (failure)
                Directory.CreateDirectory(Path.Combine(f.Folder, "presets.json.tmp"));
            f.ViewModel.SaveCommand.Execute(null);
            RenderTests.Pump();
            Assert.True(feedback.IsShowingResult);
            Assert.Equal(!reducedMotion, feedback.IsPlayingAnimation);
            if (reducedMotion)
                Assert.Equal(1, feedback.BadgeScale);
            Assert.Equal(bounds.Size, button.Bounds.Size);
            Assert.Equal("Save", AutomationProperties.GetName(button));
            Assert.Equal(f.ViewModel.SaveStatus, AutomationProperties.GetHelpText(button));
            if (reducedMotion)
                RenderTests.Save(window, failure ? "preset-save-failure.png" : "preset-save-success.png");
            await Task.Delay(1050);
            RenderTests.Pump();
            Assert.False(feedback.IsShowingResult);
            Assert.False(feedback.IsPlayingAnimation);
            Assert.Equal(bounds.Size, button.Bounds.Size);
            if (failure)
                Assert.False(string.IsNullOrEmpty(page.FindControl<TextBlock>("SaveErrorMessage")!.Text));

            // Repeated outcomes restart the display; leaving the page cancels every timer.
            f.ViewModel.SaveCommand.Execute(null);
            Assert.True(feedback.IsShowingResult);
            Motion.Set(false);
            Assert.False(feedback.IsPlayingAnimation);
            Assert.Equal(1, feedback.BadgeScale);
            window.Content = null;
            Assert.False(feedback.IsShowingResult);
        }
        finally
        {
            window.Close();
            Motion.Set(true);
        }
    }
}
