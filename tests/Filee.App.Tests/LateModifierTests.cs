// A drag gesture's modifier keys may also be pressed once the drag is under way (Explorer users often start dragging
// and then press Ctrl). Turned off for a gesture (#1863), the keys count only when held as the button goes down, so
// Ctrl pressed during a drag stays Explorer's "copy instead of move".

using Filee.App.Services.Triggers;
using Filee.App.ViewModels.Pages;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class LateModifierTests
{
    private static TriggerGesture CtrlDrag(bool allowLate) =>
        new() { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl, Button = TriggerMouseButton.Left, AllowLateModifier = allowLate };

    private static (GestureDetector Detector, List<TriggerGesture> Started) Create(params TriggerGesture[] gestures)
    {
        var detector = new GestureDetector { SystemDragThreshold = 4 };
        detector.Configure(gestures);
        var started = new List<TriggerGesture>();
        detector.DragStarted += (gesture, _, _) => started.Add(gesture);
        return (detector, started);
    }

    [Fact]
    public void Turned_off_a_key_pressed_during_the_drag_opens_nothing()
    {
        var (detector, started) = Create(CtrlDrag(allowLate: false));

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.None);
        detector.MouseMove(30, 0, ModifierKeys.None, TriggerMouseButton.Left);
        detector.MouseMove(40, 0, ModifierKeys.Ctrl, TriggerMouseButton.Left);
        detector.MouseMove(80, 0, ModifierKeys.Ctrl, TriggerMouseButton.Left);
        detector.MouseUp(TriggerMouseButton.Left, 80, 0);

        Assert.Empty(started);
    }

    [Fact]
    public void Turned_off_a_key_held_before_the_press_still_opens_the_donut()
    {
        var (detector, started) = Create(CtrlDrag(allowLate: false));

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.Ctrl);
        detector.MouseMove(50, 0, ModifierKeys.Ctrl, TriggerMouseButton.Left);

        Assert.Single(started);
    }

    [Fact]
    public void On_by_default_a_key_pressed_during_the_drag_opens_the_donut()
    {
        Assert.True(new TriggerGesture().AllowLateModifier);
        Assert.All(TriggerGesture.Defaults(), gesture => Assert.True(gesture.AllowLateModifier));
        var (detector, started) = Create(new TriggerGesture { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl, Button = TriggerMouseButton.Left });

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.None);
        detector.MouseMove(30, 0, ModifierKeys.None, TriggerMouseButton.Left);
        detector.MouseMove(40, 0, ModifierKeys.Ctrl, TriggerMouseButton.Left);

        Assert.Single(started);
    }

    [Fact]
    public void Each_gesture_decides_for_itself()
    {
        var ctrl = CtrlDrag(allowLate: false);
        var ctrlShift = new TriggerGesture
        {
            Kind = TriggerKind.Drag,
            Modifiers = ModifierKeys.Ctrl | ModifierKeys.Shift,
            Button = TriggerMouseButton.Left,
            Face = DonutFace.Back,
        };
        var (detector, started) = Create(ctrl, ctrlShift);

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.None);
        detector.MouseMove(30, 0, ModifierKeys.None, TriggerMouseButton.Left);
        detector.MouseMove(40, 0, ModifierKeys.Ctrl, TriggerMouseButton.Left);
        Assert.Empty(started);
        detector.MouseMove(50, 0, ModifierKeys.Ctrl | ModifierKeys.Shift, TriggerMouseButton.Left);

        Assert.Equal(DonutFace.Back, Assert.Single(started).Face);
    }

    [Fact]
    public void The_choice_is_kept_by_a_copy()
    {
        Assert.False(CtrlDrag(allowLate: false).Clone().AllowLateModifier);
    }

    [Fact]
    public void Only_a_drag_with_modifier_keys_offers_the_switch()
    {
        var drag = new TriggerGestureViewModel(CtrlDrag(allowLate: true), new KeyLocalizer(), () => { });
        Assert.True(drag.UsesLateModifier);
        Assert.True(drag.AllowLateModifier);

        drag.Ctrl = false;
        Assert.False(drag.UsesLateModifier); // no key to press late
        drag.Alt = true;
        Assert.True(drag.UsesLateModifier);

        drag.Kind = drag.Kinds.First(kind => kind.Value == TriggerKind.Hold);
        Assert.False(drag.UsesLateModifier);

        var chord = new TriggerGestureViewModel(
            new TriggerGesture { Kind = TriggerKind.KeyChord, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt, Key = "Space", Button = TriggerMouseButton.None },
            new KeyLocalizer(), () => { });
        Assert.False(chord.UsesLateModifier);
    }

    [Fact]
    public void Switching_it_off_saves_the_gesture()
    {
        var gesture = CtrlDrag(allowLate: true);
        var saves = 0;
        var vm = new TriggerGestureViewModel(gesture, new KeyLocalizer(), () => saves++);

        vm.AllowLateModifier = false;

        Assert.False(gesture.AllowLateModifier);
        Assert.Equal(1, saves);
    }
}
