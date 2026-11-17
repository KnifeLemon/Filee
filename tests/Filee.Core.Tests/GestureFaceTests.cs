// The face a gesture opens the donut on (#195) is saved with the gesture; settings written before it existed have no
// "face" and open the front, as they always did, without a migration.

using System.Text.Json.Nodes;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class GestureFaceTests
{
    [Fact]
    public void A_gesture_set_to_the_back_is_saved_and_read_back()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.Triggers =
        [
            new TriggerGesture { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl },
            new TriggerGesture { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Shift, Face = DonutFace.Back },
        ];
        store.SaveSettings();

        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(dir.Path, "settings.json")))!;
        Assert.Equal("Back", json["triggers"]![1]!["face"]!.GetValue<string>());
        var again = new UserDataStore(dir.Path);
        again.Load();
        Assert.Equal([DonutFace.Front, DonutFace.Back], again.Settings.Triggers.Select(g => g.Face));
    }

    [Fact]
    public void A_gesture_saved_without_a_face_opens_the_front()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveSettings();
        var path = Path.Combine(dir.Path, "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var gesture in json["triggers"]!.AsArray())
            gesture!.AsObject().Remove("face");
        File.WriteAllText(path, json.ToJsonString());

        var again = new UserDataStore(dir.Path);
        again.Load();

        Assert.NotEmpty(again.Settings.Triggers);
        Assert.All(again.Settings.Triggers, g => Assert.Equal(DonutFace.Front, g.Face));
    }
}
