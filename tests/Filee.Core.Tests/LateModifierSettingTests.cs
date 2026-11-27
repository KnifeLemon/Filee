// Whether a drag gesture's keys may be pressed once the drag is under way (#1863) is saved with the gesture. Settings
// written before the option existed have no "allowLateModifier" and keep working as they did, without a migration.

using System.Text.Json.Nodes;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class LateModifierSettingTests
{
    [Fact]
    public void A_gesture_saved_without_the_option_allows_a_late_key()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveSettings();
        var path = Path.Combine(dir.Path, "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var gesture in json["triggers"]!.AsArray())
            gesture!.AsObject().Remove("allowLateModifier");
        File.WriteAllText(path, json.ToJsonString());

        var again = new UserDataStore(dir.Path);
        again.Load();

        Assert.NotEmpty(again.Settings.Triggers);
        Assert.All(again.Settings.Triggers, g => Assert.True(g.AllowLateModifier));
    }

    [Fact]
    public void A_gesture_switched_off_is_saved_and_read_back()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.Triggers =
        [
            new TriggerGesture { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl, AllowLateModifier = false },
            new TriggerGesture { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Shift },
        ];
        store.SaveSettings();

        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(dir.Path, "settings.json")))!;
        Assert.False(json["triggers"]![0]!["allowLateModifier"]!.GetValue<bool>());
        var again = new UserDataStore(dir.Path);
        again.Load();
        Assert.Equal([false, true], again.Settings.Triggers.Select(g => g.AllowLateModifier));
    }
}
