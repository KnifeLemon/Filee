// A gesture can open the donut on its back face (#195): two gestures with different modifiers open two different
// sets of slices, Ctrl + drag the front and Ctrl + Shift + drag the back. A back with nothing for the files opens
// the front instead, so the gesture never shows an empty donut.

using System.Reflection;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class GestureFaceTests
{
    private static readonly TriggerGesture FrontDrag = new() { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl, Button = TriggerMouseButton.Left };

    private static readonly TriggerGesture BackDrag = new()
    {
        Kind = TriggerKind.Drag,
        Modifiers = ModifierKeys.Ctrl | ModifierKeys.Shift,
        Button = TriggerMouseButton.Left,
        Face = DonutFace.Back,
    };

    private const string Picture = @"C:\photos\a.jpg";

    [Fact]
    public void Each_modifier_set_starts_its_own_drag_with_its_face()
    {
        var detector = new GestureDetector { SystemDragThreshold = 4 };
        detector.Configure([FrontDrag, BackDrag]);
        var faces = new List<DonutFace>();
        detector.DragStarted += (gesture, _, _) => faces.Add(gesture.Face);

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.Ctrl | ModifierKeys.Shift);
        detector.MouseMove(50, 0, ModifierKeys.Ctrl | ModifierKeys.Shift, TriggerMouseButton.Left);
        detector.MouseUp(TriggerMouseButton.Left, 50, 0);
        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.Ctrl);
        detector.MouseMove(50, 0, ModifierKeys.Ctrl, TriggerMouseButton.Left);
        detector.MouseUp(TriggerMouseButton.Left, 50, 0);
        // Both modifiers pressed after the drag started (Explorer users often do): the set held then decides.
        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.None);
        detector.MouseMove(30, 0, ModifierKeys.None, TriggerMouseButton.Left);
        detector.MouseMove(40, 0, ModifierKeys.Ctrl | ModifierKeys.Shift, TriggerMouseButton.Left);

        Assert.Equal([DonutFace.Back, DonutFace.Front, DonutFace.Back], faces);
    }

    [Fact]
    public void The_face_is_kept_by_a_copy_and_old_gestures_open_the_front()
    {
        Assert.Equal(DonutFace.Back, BackDrag.Clone().Face);
        Assert.All(TriggerGesture.Defaults(), gesture => Assert.Equal(DonutFace.Front, gesture.Face));
    }

    [Fact]
    public void Choosing_the_face_in_settings_saves_the_gesture()
    {
        var gesture = new TriggerGesture { Kind = TriggerKind.Drag };
        var saves = 0;
        var vm = new TriggerGestureViewModel(gesture, new KeyLocalizer(), () => saves++);
        Assert.Equal(DonutFace.Front, vm.Face.Value);
        Assert.Equal("triggers.face.Back", vm.Faces[1].Label);

        vm.Face = vm.Faces.First(face => face.Value == DonutFace.Back);

        Assert.Equal(DonutFace.Back, gesture.Face);
        Assert.Equal(1, saves);
    }

    [AvaloniaFact]
    public void A_shortcut_set_to_the_back_opens_the_donut_on_its_back() => WithBack(null, () =>
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        var model = AppHost.Get<RadialViewModel>();
        try
        {
            radial.ShowForFiles(400, 400, [Picture], backFace: true);
            Assert.True(model.IsBackFace);
            Assert.True(model.CanFlip);

            radial.ShowForFiles(400, 400, [Picture]);
            Assert.False(model.IsBackFace);
        }
        finally
        {
            radial.Close(animate: false);
        }
    });

    [AvaloniaFact]
    public void A_drag_set_to_the_back_opens_on_its_back_once_files_enter() => WithBack(null, () =>
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        var model = AppHost.Get<RadialViewModel>();
        try
        {
            radial.ShowForDrag(400, 400, backFace: true);
            Assert.False(model.IsBackFace); // the empty "bring files here" ring has no faces yet
            FilesEntered(radial.Window!)([Picture]);
            Assert.True(model.IsBackFace);

            // The next drag of a front gesture starts on the front again.
            radial.ShowForDrag(400, 400);
            FilesEntered(radial.Window!)([@"C:\photos\b.jpg"]);
            Assert.False(model.IsBackFace);
        }
        finally
        {
            radial.Close(animate: false);
        }
    });

    [AvaloniaFact]
    public void A_back_with_nothing_for_the_files_opens_the_front() => WithBack([], () =>
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        var model = AppHost.Get<RadialViewModel>();
        try
        {
            radial.ShowForFiles(400, 400, [Picture], backFace: true);

            Assert.False(model.IsBackFace);
            Assert.NotEmpty(model.Items);
        }
        finally
        {
            radial.Close(animate: false);
        }
    });

    /// <summary>Runs <paramref name="test"/> with the pictures donut's back set to <paramref name="back"/> (null: as it ships).</summary>
    private static void WithBack(List<string>? back, Action test)
    {
        TestServices.EnsureInitialized("en");
        var images = AppHost.Get<UserDataStore>().Profiles.First(p => p.Id == "images");
        var before = images.BackPresetIds?.ToList();
        if (back is not null)
            images.BackPresetIds = back;
        try
        {
            test();
        }
        finally
        {
            images.BackPresetIds = before;
        }
    }

    private static Action<IReadOnlyList<string>> FilesEntered(RadialWindow window) =>
        (Action<IReadOnlyList<string>>)typeof(RadialWindow)
            .GetField("FilesEntered", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(window)!;
}
