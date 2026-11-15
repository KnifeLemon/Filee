using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("Portable UI environment")]
public sealed class PortableFirstStartTests
{
    [AvaloniaFact]
    public void Import_choices_default_to_both_and_require_at_least_one_selection()
    {
        TestServices.EnsureInitialized("en");
        Window? window = null;
        var selection = new PortableImport.Selection();
        var result = PortableFirstStart.AskAsync("Import?", _ => Task.CompletedTask,
            sourcePath: "old", destinationPath: "portable", selection: selection, engineBytes: 10485760,
            shown: value => window = value);
        try
        {
            var choices = window!.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Assert.Equal(5, choices.Length);
            Assert.All(choices, choice => Assert.True(choice.IsChecked));
            Assert.Contains("10", choices[4].Content?.ToString());
            var import = window.GetVisualDescendants().OfType<Button>().Last();
            foreach (var choice in choices) choice.IsChecked = false;
            Assert.False(import.IsEnabled);
            choices[4].IsChecked = true;
            Assert.True(import.IsEnabled);
            Assert.False(selection.Settings);
            Assert.False(selection.History);
            Assert.True(selection.Engines);
        }
        finally { window?.Close(); }
        Assert.False(result.Result);
    }

    [AvaloniaFact]
    public void Import_confirmation_shows_the_profile_and_portable_destination()
    {
        TestServices.EnsureInitialized("en");
        Window? window = null;
        var catalog = LocalizationService.LoadStrings("en");
        var prompt = string.Format(catalog["portable.import_prompt"],
            @"C:\Users\Administrator\AppData\Roaming\Filee", @"D:\Filee\data");
        var result = PortableFirstStart.AskAsync(prompt, _ => Task.CompletedTask,
            determinate: true, shown: value => window = value,
            acceptText: catalog["portable.import_existing"], declineText: catalog["portable.start_fresh"],
            sourcePath: @"C:\Users\Administrator\AppData\Roaming\Filee", destinationPath: @"D:\Filee\data");
        try
        {
            RenderTests.Pump();
            Assert.Contains(window!.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Text == @"D:\Filee\data");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == catalog["portable.import_next"]);
            var labels = window.GetVisualDescendants().OfType<Button>().Where(button => button is not CheckBox).Select(button => button.Content?.ToString()).ToArray();
            Assert.Equal(["Start fresh", "Import existing data"], labels);
            RenderTests.Save(window, "portable-import-confirmation.png");
        }
        finally { window?.Close(); }
        Assert.False(result.GetAwaiter().GetResult());
    }

    [AvaloniaFact]
    public void Failed_copy_keeps_dialog_open_and_can_be_retried()
    {
        TestServices.EnsureInitialized("en");
        Window? window = null;
        var attempts = 0;
        var result = PortableFirstStart.AskAsync("Import?", _ =>
        {
            if (++attempts == 1) throw new IOException("copy failed");
            return Task.CompletedTask;
        }, shown: value => window = value);
        try
        {
            var buttons = window!.GetVisualDescendants().OfType<Button>().ToArray();
            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(result.IsCompleted);
            Assert.Contains("copy failed", window.GetVisualDescendants().OfType<TextBlock>().First().Text);
            Assert.All(buttons, button => Assert.True(button.IsEnabled));
            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(result.Result);
            Assert.Equal(2, attempts);
        }
        finally { window?.Close(); }
    }

    [AvaloniaFact]
    public void Cancel_does_not_run_the_import()
    {
        TestServices.EnsureInitialized("en");
        Window? window = null;
        var called = false;
        var result = PortableFirstStart.AskAsync("Import?", _ => { called = true; return Task.CompletedTask; },
            shown: value => window = value);
        try
        {
            var buttons = window!.GetVisualDescendants().OfType<Button>().ToArray();
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(result.GetAwaiter().GetResult());
            Assert.False(called);
        }
        finally { window?.Close(); }
    }

    [AvaloniaFact]
    public void Import_shows_percent_and_cannot_close_until_copy_finishes()
    {
        TestServices.EnsureInitialized("en");
        Window? window = null;
        var complete = new TaskCompletionSource();
        var result = PortableFirstStart.AskAsync("Import?", progress =>
        {
            progress.Report(42);
            return complete.Task;
        }, determinate: true, shown: value => window = value);
        try
        {
            var buttons = window!.GetVisualDescendants().OfType<Button>().ToArray();
            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RenderTests.Pump();
            var bar = window!.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.False(bar.IsIndeterminate);
            Assert.True(bar.ShowProgressText);
            Assert.Equal(42, bar.Value);
            window.Close();
            Assert.True(window.IsVisible);
            Assert.All(buttons, button => Assert.False(button.IsEnabled));
            RenderTests.Save(window, "portable-import-progress.png");
            complete.SetResult();
            RenderTests.Pump();
            Assert.True(result.IsCompletedSuccessfully);
            Assert.True(result.Result);
        }
        finally { complete.TrySetResult(); RenderTests.Pump(); window?.Close(); }
    }
}
