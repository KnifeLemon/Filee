// The clipboard switch of a keyboard shortcut: opt-in only. No shortcut converts the clipboard by default and no
// upgrade adds one (Shift + Cmd + V is Paste and Match Style in most text fields); whoever wants it turns it on for
// a shortcut with keys of their own choosing in Settings > Shortcuts.

using System.Text.Json.Nodes;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ClipboardShortcutTests
{
    [Fact]
    public void No_default_shortcut_converts_the_clipboard()
    {
        Assert.All(TriggerGesture.Defaults(), g => Assert.False(g.Clipboard));
    }

    [Fact]
    public void An_upgrade_adds_no_clipboard_shortcut()
    {
        var own = new TriggerGesture { Kind = TriggerKind.KeyChord, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt, Key = "Space", Button = TriggerMouseButton.None };
        var settings = new AppSettings { SchemaVersion = 54, Triggers = [own] };

        SettingsMigrations.Apply(settings);

        Assert.Same(own, Assert.Single(settings.Triggers));
        Assert.False(own.Clipboard);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void The_option_the_user_switched_on_is_saved_and_read_back()
    {
        var chosen = new TriggerGesture
        {
            Kind = TriggerKind.KeyChord,
            Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt,
            Key = "V",
            Button = TriggerMouseButton.None,
            Scope = TriggerScope.Anywhere,
            Clipboard = true,
        };
        Assert.True(chosen.Clone().Clipboard);

        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.Triggers = [chosen];
        store.SaveSettings();

        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(dir.Path, "settings.json")))!;
        Assert.True(json["triggers"]![0]!["clipboard"]!.GetValue<bool>());
        var again = new UserDataStore(dir.Path);
        again.Load();
        Assert.True(Assert.Single(again.Settings.Triggers).Clipboard);
    }
}
