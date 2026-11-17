// Turns an icon while Spin.IsActive is true (About: Check now, Engines: Check again). It is done in code, not with a
// XAML animation: a Style.Animations on RotateTransform.Angle in Soft.axaml stopped the ComboBox chevron from flipping.
// With "Reduce animations" on the icon does not turn.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Controls;

public static class Spin
{
    private const double TurnMilliseconds = 900;

    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsActive", typeof(Spin));

    public static bool GetIsActive(Control control) => control.GetValue(IsActiveProperty);
    public static void SetIsActive(Control control, bool value) => control.SetValue(IsActiveProperty, value);

    private sealed class State
    {
        public DispatcherTimer? Timer;

        /// <summary>The detach handler is added once per icon, not on every start.</summary>
        public bool Hooked;
    }

    private static readonly ConditionalWeakTable<Control, State> States = new();

    static Spin()
    {
        IsActiveProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
                Start(control);
            else
                Stop(control);
        });
    }

    private static void Start(Control control)
    {
        if (!Motion.Enabled)
            return;
        var state = States.GetOrCreateValue(control);
        if (state.Timer is not null)
            return;

        // Leaving the page stops the turning. Subscribed once: Stop is safe to call again, but handlers added on
        // every click would pile up for as long as the icon lives.
        if (!state.Hooked)
        {
            control.DetachedFromVisualTree += (_, _) => Stop(control);
            state.Hooked = true;
        }

        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var angle = clock.Elapsed.TotalMilliseconds % TurnMilliseconds / TurnMilliseconds * 360;
            control.RenderTransform = new RotateTransform(angle); // the origin is the middle of the icon
        };
        state.Timer = timer;
        timer.Start();
    }

    private static void Stop(Control control)
    {
        if (!States.TryGetValue(control, out var state) || state.Timer is null)
            return;
        state.Timer.Stop();
        state.Timer = null;
        control.RenderTransform = null;
    }
}
