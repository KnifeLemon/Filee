// Runs before loading the portable profile, so importing it does not race with first-run saves.

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Filee.Core.Settings;

namespace Filee.App.Services;

internal static class PortableFirstStart
{
    public static async Task RunAsync()
    {
        if (!PortableMode.IsEnabled || PortableMode.DataDirectory is not { } destination) return;
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Filee");
        if (PortableImport.CanOffer(source, destination))
        {
            var engines = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Filee", "engines");
            var selection = new PortableImport.Selection { Engines = Directory.Exists(engines) };
            var engineBytes = await Task.Run(() => PortableImport.EngineSize(engines));
            var prompt = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.EarlyText("portable.import_prompt"), source, destination);
            var imported = await AskAsync(prompt, async progress =>
            {
                await Task.Run(() => PortableImport.Copy(source, engines, destination, progress, selection));
            }, determinate: true,
                acceptText: LocalizationService.EarlyText("portable.import_existing"),
                declineText: LocalizationService.EarlyText("portable.start_fresh"), sourcePath: source, destinationPath: destination,
                selection: selection, enginesPath: engines, engineBytes: engineBytes);
            if (!imported) PortableImport.Decline(destination);
        }
        if (Environment.ProcessPath is { } executable)
            await OfferCleanupAsync(destination, () => PortableIntegrationCleanup.HasOwnedEntries(executable),
                () => PortableIntegrationCleanup.RemoveOwnedEntriesAsync(executable),
                remove => AskAsync(LocalizationService.EarlyText("portable.cleanup_prompt"), _ => remove()));
    }

    internal const string CleanupDecisionFile = ".portable-cleanup-decided";

    internal static async Task OfferCleanupAsync(string destination, Func<bool> hasOwnedEntries,
        Func<Task> remove, Func<Func<Task>, Task<bool>> ask)
    {
        var decision = Path.Combine(destination, CleanupDecisionFile);
        if (File.Exists(decision) || !hasOwnedEntries()) return;
        var accepted = await ask(remove);
        File.WriteAllText(decision, accepted ? "completed" : "declined");
    }

    internal static Task<bool> AskAsync(string message, Func<IProgress<double>, Task> accept, bool determinate = false,
        Action<Window>? shown = null, string? acceptText = null, string? declineText = null,
        string? sourcePath = null, string? destinationPath = null, PortableImport.Selection? selection = null, string? enginesPath = null,
        long? engineBytes = null)
    {
        var answer = new TaskCompletionSource<bool>();
        var window = new Window
        {
            Title = "Filee",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        var yes = new Button { Content = acceptText ?? LocalizationService.EarlyText("common.ok") };
        yes.Classes.Add("accent");
        var no = new Button { Content = declineText ?? LocalizationService.EarlyText("common.cancel") };
        var progress = new ProgressBar
        {
            IsIndeterminate = !determinate,
            IsVisible = false,
            Minimum = 0,
            Maximum = 100,
            ShowProgressText = determinate,
            ProgressTextFormat = "{1:0}%"
        };
        var reporter = new Progress<double>(value => progress.Value = value);
        var busy = false;
        CheckBox? enginesChoice = null;
        var choices = new List<CheckBox>();
        void UpdateChoices() => yes.IsEnabled = !busy && (selection is null || selection.Any);
        var content = new StackPanel
        {
            Margin = new(28),
            Spacing = 20,
        };
        if (sourcePath is not null && destinationPath is not null)
        {
            content.Children.Add(new TextBlock { Text = LocalizationService.EarlyText("portable.import_existing"), FontSize = 22, FontWeight = FontWeight.SemiBold });
            text.Text = LocalizationService.EarlyText("portable.import_summary");
            content.Children.Add(text);
            selection ??= new PortableImport.Selection();
            var choicePanel = new StackPanel { Spacing = 2 };
            void AddChoice(string key, bool selected, Action<bool> changed)
            {
                var label = LocalizationService.EarlyText(key);
                if (key == "portable.choose_profiles") label += " *";
                var checkbox = new CheckBox { Content = label, IsChecked = selected };
                checkbox.IsCheckedChanged += (_, _) => { changed(checkbox.IsChecked == true); UpdateChoices(); };
                choices.Add(checkbox);
                choicePanel.Children.Add(checkbox);
            }
            AddChoice("portable.choose_settings", selection.Settings, value => selection.Settings = value);
            AddChoice("portable.choose_presets", selection.Presets, value => selection.Presets = value);
            AddChoice("portable.choose_profiles", selection.Profiles, value => selection.Profiles = value);
            AddChoice("portable.choose_history", selection.History, value => selection.History = value);
            enginesChoice = new CheckBox
            {
                Content = LocalizationService.EarlyText("portable.choose_engines"),
                IsChecked = selection.Engines,
                IsEnabled = enginesPath is null || Directory.Exists(enginesPath)
            };
            if (engineBytes is { } size)
                enginesChoice.Content += size >= 1024L * 1024 * 1024 ? $" ({size / (1024.0 * 1024 * 1024):N1} GB)" : $" ({size / (1024.0 * 1024):N1} MB)";
            enginesChoice.IsCheckedChanged += (_, _) => { selection.Engines = enginesChoice.IsChecked == true; UpdateChoices(); };
            choices.Add(enginesChoice);
            choicePanel.Children.Add(enginesChoice);
            content.Children.Add(choicePanel);
            content.Children.Add(new TextBlock { Text = "* " + LocalizationService.EarlyText("portable.profile_dependencies"), TextWrapping = TextWrapping.Wrap, Opacity = .8 });
            UpdateChoices();
            var paths = new StackPanel { Spacing = 10 };
            paths.Children.Add(new SelectableTextBlock { Text = sourcePath, TextWrapping = TextWrapping.Wrap });
            paths.Children.Add(new TextBlock { Text = "↓", FontSize = 20 });
            paths.Children.Add(new SelectableTextBlock { Text = destinationPath, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
            content.Children.Add(new Border
            {
                Padding = new(16),
                CornerRadius = new(8),
                BorderThickness = new(1),
                BorderBrush = Brushes.Gray,
                Child = paths
            });
            content.Children.Add(new TextBlock { Text = LocalizationService.EarlyText("portable.import_next"), TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = LocalizationService.EarlyText("portable.fresh_next"), TextWrapping = TextWrapping.Wrap, Opacity = .8 });
        }
        else content.Children.Add(text);
        content.Children.Add(progress);
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { no, yes }
        });
        window.Content = content;
        no.Click += (_, _) => window.Close();
        yes.Click += async (_, _) =>
        {
            busy = true;
            yes.IsEnabled = no.IsEnabled = false;
            foreach (var choice in choices) choice.IsEnabled = false;
            progress.IsVisible = true;
            try
            {
                await accept(reporter);
                busy = false;
                answer.TrySetResult(true);
                window.Close();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                text.Text = message + "\n\n" + ex.Message;
                busy = false;
                yes.IsEnabled = no.IsEnabled = true;
                foreach (var choice in choices) choice.IsEnabled = true;
                if (enginesChoice is not null) enginesChoice.IsEnabled = enginesPath is null || Directory.Exists(enginesPath);
                UpdateChoices();
                progress.IsVisible = false;
            }
        };
        window.Closing += (_, e) => e.Cancel = busy;
        window.Closed += (_, _) => answer.TrySetResult(false);
        window.Show();
        shown?.Invoke(window);
        return answer.Task;
    }
}
