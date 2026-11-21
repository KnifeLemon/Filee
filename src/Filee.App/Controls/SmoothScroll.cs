// Smooth mouse-wheel scrolling for every ScrollViewer. Styles/Controls.axaml turns it on for all of them;
// SmoothScroll.IsEnabled="False" on one ScrollViewer turns it off there. Avalonia moves the page by a whole wheel
// notch at once. Here every notch adds to a target and the page follows it like a weight on a critically damped
// spring: it starts gently, speeds up, and settles without a bounce, and a continued spin carries it on (its speed is
// never reset between notches). A mouse wheel turned quickly also scrolls further per notch, as on a Mac. It is done
// in code, one step per rendered frame (TopLevel.RequestAnimationFrame, so every step lands on a screen refresh; a
// timer drifts against the refresh and the page flutters), not with a XAML transition: a transition on Offset would
// also animate the scrollbar and the keyboard.
//
// Left alone (Avalonia's own scrolling runs): "Reduce animations" on (Motion.Enabled is false), any key held (Ctrl is
// the window zoom, Shift sideways scrolling), mostly sideways movement, anything but whole wheel notches on every system
// (a touchpad and a smooth wheel send small steps, and their inertia too, which already move the page smoothly:
// smoothing those again makes the page lag), and a wheel over a control that uses the wheel itself (a slider, a number
// box, a closed ComboBox, the donut and the crop canvases).

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Filee.App.Services;

namespace Filee.App.Controls;

public static class SmoothScroll
{
    /// <summary>How far one slow wheel notch scrolls: three lines of 16 px, as the platforms do.</summary>
    public const double PixelsPerNotch = 48;

    /// <summary>
    /// How quickly the page follows its target, per second. Higher is snappier, lower is floatier: the page settles in
    /// about four times the inverse of this (0.3 s here).
    /// </summary>
    public const double Stiffness = 13;

    /// <summary>Notches closer together than this (milliseconds) count as one spin, which scrolls further.</summary>
    private const double SpinGap = 140;

    /// <summary>Each notch of a spin adds this share to the distance of one notch, up to <see cref="MaxBoost"/> times.</summary>
    private const double BoostPerNotch = 0.3;

    private const double MaxBoost = 4;

    /// <summary>
    /// How far past an end the target may be set (pixels). The page is aimed a little beyond the top or the bottom, so it
    /// arrives there at speed and stops at once: aimed exactly at the end, a spring creeps the last pixels for a moment
    /// and the page looks stuck just short of it.
    /// </summary>
    private const double Overshoot = 120;

    private const double Tolerance = 0.4;
    private const double RestSpeed = 8; // pixels per second

    /// <summary>
    /// The page moved by this much (pixels) other than by this class in one frame: the scrollbar was dragged, a key was
    /// pressed, or the program scrolled something into view. A smaller difference is the content shifting under the page
    /// (a list that measures more of its rows), which the movement follows.
    /// </summary>
    private const double MovedElsewhere = 60;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(SmoothScroll));

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(IsEnabledProperty, value);

    private sealed class State
    {
        public bool Active;      // a step is requested every frame while the page moves
        public int Run;          // told apart from an earlier run whose last frame is still on its way
        public Stopwatch Clock = new();
        public double At;        // the clock's time at the last step
        public double Target;    // where the page is going
        public double Velocity;  // pixels per second
        public double Last;      // where this class left the page: a different offset means someone else moved it
        public ulong NotchAt;    // the time of the last whole notch (event time, milliseconds)
        public int Streak;       // whole notches in a row, each within SpinGap of the one before
        public bool Hooked;      // the detach handler is added once per ScrollViewer
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    static SmoothScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
        {
            if (e.NewValue is true)
            {
                viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            }
            else
            {
                viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
                Stop(viewer);
            }
        });
    }

    /// <summary>
    /// A wheel notch: a whole number, one or more. A touchpad (a Windows precision touchpad's inertia, libinput's smooth
    /// scrolling on Linux, a Mac's trackpad) and a smooth wheel send fractions of one: those are left to Avalonia, since
    /// the system already makes them smooth.
    /// </summary>
    public static bool IsWholeNotch(double delta) =>
        Math.Abs(delta) >= 1 - 1e-6 && Math.Abs(delta - Math.Round(delta)) < 1e-3;

    /// <summary>
    /// How far one notch scrolls as the spin goes on: <paramref name="streak"/> notches in a row, 0 for the first.
    /// </summary>
    public static double Boost(int streak) => Math.Min(MaxBoost, 1 + Math.Max(0, streak) * BoostPerNotch);

    /// <summary>
    /// The page's position and speed after <paramref name="seconds"/> of following <paramref name="target"/>: a
    /// critically damped spring, solved exactly, so the result is the same however the time is split into frames. From
    /// rest it never goes past the target; the speed it already has is kept, so a new target doesn't stop the page.
    /// </summary>
    public static (double Position, double Velocity) Follow(double position, double velocity, double target, double seconds)
    {
        if (seconds <= 0)
            return (position, velocity);
        var offset = position - target;
        var fall = Math.Exp(-Stiffness * seconds);
        var carry = velocity + Stiffness * offset;
        return (target + (offset + carry * seconds) * fall, (velocity - Stiffness * carry * seconds) * fall);
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.Handled || !Motion.Enabled || e.KeyModifiers != KeyModifiers.None)
            return;
        // Only whole notches (see IsWholeNotch); a sideways wheel is left to Avalonia.
        if (!IsWholeNotch(e.Delta.Y) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
            return;
        // The ScrollViewers outside this one see the wheel first (tunnelling), so each works out who should scroll: the
        // nearest one that can still move that way. A list at its end hands the wheel on to the page around it, as
        // Avalonia's own scrolling does.
        if (e.Source is not Visual source || FindScroller(source, e.Delta.Y) != viewer)
            return;
        Scroll(viewer, e.Delta.Y, e.Timestamp);
        e.Handled = true;
    }

    private static ScrollViewer? FindScroller(Visual source, double delta)
    {
        foreach (var visual in source.GetSelfAndVisualAncestors())
        {
            // These use the wheel themselves (a value, a selection, a zoom, a flip): the page doesn't scroll over them.
            if (visual is Slider or NumericUpDown or ComboBox or DonutMenu or CropCanvas or HideCanvas)
                return null;
            if (visual is ScrollViewer viewer && CanScroll(viewer, delta))
                return viewer;
        }
        return null;
    }

    private static bool CanScroll(ScrollViewer viewer, double delta)
    {
        if (viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
            return false;
        var max = MaxOffset(viewer);
        if (max <= Tolerance)
            return false;
        // While the page is moving this ScrollViewer keeps the wheel, even once the target is at an end: giving it up
        // (to Avalonia, or to the page around) would move the page by a whole notch at once in the middle of the glide.
        if (States.TryGetValue(viewer, out var state) && state.Active)
            return true;
        // At rest at an end it hands the wheel on, as Avalonia's own scrolling does.
        return delta > 0 ? viewer.Offset.Y > Tolerance : viewer.Offset.Y < max - Tolerance;
    }

    private static double ClampTarget(double target, double max) => Math.Clamp(target, -Overshoot, max + Overshoot);

    private static double MaxOffset(ScrollViewer viewer) => Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);

    /// <summary>Where the page is going while it moves, else where it is.</summary>
    private static double CurrentTarget(ScrollViewer viewer) =>
        States.TryGetValue(viewer, out var state) && state.Active ? state.Target : viewer.Offset.Y;

    private static void Scroll(ScrollViewer viewer, double delta, ulong timestamp)
    {
        var state = States.GetOrCreateValue(viewer);

        // A quick spin of the wheel scrolls further per notch: notches in the same direction, close together.
        // The same way as the page is going (wheel up is toward offset 0).
        var continued = state.Active && timestamp - state.NotchAt <= SpinGap
            && Math.Sign(state.Target - viewer.Offset.Y) == -Math.Sign(delta);
        state.Streak = continued ? state.Streak + 1 : 0;
        state.NotchAt = timestamp;
        var boost = Boost(state.Streak);

        // Another notch while the page still moves adds to where it is going, and its speed carries on.
        state.Target = ClampTarget(CurrentTarget(viewer) - delta * PixelsPerNotch * boost, MaxOffset(viewer));
        if (state.Active)
            return;

        // Without a window to draw frames for (not shown yet) the page just goes to its target.
        if (TopLevel.GetTopLevel(viewer) is not { } top)
        {
            viewer.Offset = new Vector(viewer.Offset.X, Math.Clamp(state.Target, 0, MaxOffset(viewer)));
            return;
        }
        if (!state.Hooked)
        {
            viewer.DetachedFromVisualTree += (_, _) => Stop(viewer);
            state.Hooked = true;
        }
        state.Last = viewer.Offset.Y;
        state.Velocity = 0;
        state.Clock.Restart();
        state.At = 0;
        state.Active = true;
        var run = ++state.Run;
        RequestFrame(top, viewer, state, run);
    }

    private static void RequestFrame(TopLevel top, ScrollViewer viewer, State state, int run) =>
        top.RequestAnimationFrame(_ =>
        {
            if (state.Active && state.Run == run)
                Step(top, viewer, state, run);
        });

    private static void Step(TopLevel top, ScrollViewer viewer, State state, int run)
    {
        // Someone else moved the page (the scrollbar was dragged, a key was pressed, the program scrolled it into view):
        // that wins, and the movement stops where it is. A small difference is content shifting under the page: the
        // target shifts with it and the movement goes on.
        var shift = viewer.Offset.Y - state.Last;
        if (Math.Abs(shift) > MovedElsewhere)
        {
            Stop(viewer);
            return;
        }
        state.Target += shift;

        var now = state.Clock.Elapsed.TotalMilliseconds;
        // A long pause (the window was busy) is not allowed to throw the page across the screen.
        var seconds = Math.Min(now - state.At, 100) / 1000;
        state.At = now;

        var max = MaxOffset(viewer);
        state.Target = ClampTarget(state.Target, max); // the content may have grown or shrunk meanwhile
        var (position, velocity) = Follow(viewer.Offset.Y, state.Velocity, state.Target, seconds);

        // At either end the page stops at once: no speed is kept against the edge.
        var atEnd = position <= 0 && state.Target <= Tolerance || position >= max && state.Target >= max - Tolerance;
        if (position <= 0 || position >= max)
        {
            position = Math.Clamp(position, 0, max);
            velocity = 0;
        }
        state.Velocity = velocity;

        var rest = Math.Clamp(state.Target, 0, max);
        var arrived = atEnd || Math.Abs(rest - position) <= Tolerance && Math.Abs(velocity) <= RestSpeed;
        viewer.Offset = new Vector(viewer.Offset.X, arrived ? rest : position);
        state.Last = viewer.Offset.Y; // read back: the ScrollViewer may have rounded or limited it
        if (arrived)
            Stop(viewer);
        else
            RequestFrame(top, viewer, state, run);
    }

    private static void Stop(ScrollViewer viewer)
    {
        if (!States.TryGetValue(viewer, out var state) || !state.Active)
            return;
        state.Active = false;
        state.Clock.Stop();
        state.Velocity = 0;
        state.Streak = 0;
    }
}
