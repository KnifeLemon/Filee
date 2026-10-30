// The clipboard switch of a keyboard shortcut: off by default, turned on by the user for keys of their own choosing
// in Settings > Shortcuts. Its results go back on the clipboard, and the log says how many files failed.

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.App.ViewModels.Pages;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Web;
using Microsoft.Extensions.Logging;

namespace Filee.App.Tests;

public class ClipboardShortcutTests
{
    /// <summary>A shortcut as a user sets it up: their own keys, the clipboard switch on.</summary>
    private static TriggerGesture Chosen() => new()
    {
        Kind = TriggerKind.KeyChord,
        Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt,
        Key = "V",
        Button = TriggerMouseButton.None,
        Scope = TriggerScope.Anywhere,
        Clipboard = true,
    };

    [Fact]
    public void Only_a_keyboard_shortcut_where_the_clipboard_can_be_read_offers_the_switch()
    {
        var shortcut = new TriggerGestureViewModel(Chosen(), new KeyLocalizer(), () => { }, clipboardAvailable: true);
        Assert.True(shortcut.UsesClipboard);
        Assert.True(shortcut.Clipboard);

        var drag = new TriggerGestureViewModel(new TriggerGesture { Kind = TriggerKind.Drag }, new KeyLocalizer(), () => { }, clipboardAvailable: true);
        Assert.False(drag.UsesClipboard);

        var elsewhere = new TriggerGestureViewModel(Chosen(), new KeyLocalizer(), () => { });
        Assert.False(elsewhere.UsesClipboard);
    }

    [Fact]
    public void Switching_it_saves_the_shortcut()
    {
        var gesture = new TriggerGesture { Kind = TriggerKind.KeyChord, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt, Key = "Space" };
        var saves = 0;
        var vm = new TriggerGestureViewModel(gesture, new KeyLocalizer(), () => saves++, clipboardAvailable: true);

        vm.Clipboard = true;

        Assert.True(gesture.Clipboard);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void The_keys_the_user_chose_fire_their_clipboard_shortcut()
    {
        var detector = new GestureDetector();
        detector.Configure([Chosen()]);
        TriggerGesture? fired = null;
        detector.KeyChordTriggered += (g, _, _) => fired = g;

        detector.KeyDown("V", ModifierKeys.Ctrl);
        Assert.Null(fired);
        detector.KeyDown("V", ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.NotNull(fired);
        Assert.True(fired.Clipboard);
    }

    [Fact]
    public async Task A_partly_failed_clipboard_job_copies_its_outputs_and_logs_the_failed_count()
    {
        var dir = Directory.CreateTempSubdirectory("filee-clipboard-").FullName;
        var output = Path.Combine(dir, "shot.jpg");
        File.WriteAllBytes(output, [1]);
        await using var queue = new JobQueue(new ConverterCatalog([]));
        var platform = new ClipboardPlatform();
        var log = new CapturingLogger();
        var service = new ConversionService(queue, new UserDataStore(dir), new KeyLocalizer(), platform, new WebDownloader(), log);
        try
        {
            // One of three converted: its output replaces the copied files, and the log carries the two failures.
            var partly = new ConversionJob([Path.Combine(dir, "a.png"), Path.Combine(dir, "b.png"), Path.Combine(dir, "c.png")],
                new Preset { TargetFormat = "jpg" }, "JPG");
            partly.Files[0].State = FileState.Done;
            partly.Files[0].Outputs.Add(output);
            partly.Files[1].State = FileState.Failed;
            partly.Files[2].State = FileState.Failed;

            service.CopyResults(partly);

            Assert.Equal([output], Assert.Single(platform.Calls));
            Assert.Contains(log.Messages, m => m.Contains("Put 1 converted file(s) on the clipboard; 2 file(s) failed"));

            // Nothing converted: the clipboard is left alone, and the log still says how many failed.
            var none = new ConversionJob([Path.Combine(dir, "x.png")], new Preset { TargetFormat = "jpg" }, "JPG");
            none.Files[0].State = FileState.Failed;

            service.CopyResults(none);

            Assert.Single(platform.Calls); // no second copy
            Assert.Contains(log.Messages, m => m.Contains("made nothing to put on the clipboard; 1 file(s) failed"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Settings → Shortcuts shows the clipboard switch on a keyboard shortcut the user turned it on for
    /// (screenshots in light and dark for review).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_shortcuts_page_shows_the_clipboard_switch(bool dark)
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var theme = AppHost.Get<ThemeService>();
        var triggersBefore = store.Settings.Triggers.Select(t => t.Clone()).ToList();
        Window? window = null;
        try
        {
            store.Settings.Triggers.Add(Chosen());
            if (dark)
                theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, "en");
            var page = new TriggersPageViewModel(store, loc, new ClipboardPlatform());
            window = new Window
            {
                Content = new Filee.App.Views.Pages.TriggersPage { DataContext = page },
                Width = 820,
                Height = 1500,
            };
            window.Show();
            RenderTests.Pump();

            // Every keyboard shortcut shows the row (a drag gesture's row stays hidden); the one the user turned
            // on is checked, the defaults stay off.
            var shown = window.GetVisualDescendants().OfType<ToggleSwitch>()
                .Where(t => AutomationProperties.GetName(t) == loc["triggers.clipboard"] && t.IsEffectivelyVisible).ToList();
            Assert.Single(shown, t => t.IsChecked == true);
            Assert.Contains(shown, t => t.IsChecked == false);
            RenderTests.Save(window, dark ? "triggers-clipboard-en-dark.png" : "triggers-clipboard-en.png");
        }
        finally
        {
            window?.Close();
            if (dark)
                theme.Apply(new ThemeSettings(), "en");
            store.Settings.Triggers.Clear();
            store.Settings.Triggers.AddRange(triggersBefore);
            store.SaveSettings();
        }
    }

    private sealed class CapturingLogger : ILogger<ConversionService>
    {
        public List<string> Messages { get; } = [];

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    private sealed class ClipboardPlatform : IPlatformServices
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public bool SupportsClipboard => true;

        public bool CopyToClipboard(IReadOnlyList<string> paths)
        {
            Calls.Add(paths);
            return true;
        }

        public ForegroundWindowInfo? GetForegroundWindow() => null;
        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) =>
            Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void OpenFile(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
    }
}
