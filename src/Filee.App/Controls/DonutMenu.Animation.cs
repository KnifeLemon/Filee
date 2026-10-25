// DonutMenu: the per-frame animation loop. Springs for hover, open/close and layout; with
// Motion.Enabled == false every value jumps to its target.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Controls;

public sealed partial class DonutMenu
{
    // ───────────────────────── Animation ─────────────────────────

    /// <summary>Damping at which a spring of stiffness 320 settles without overshooting (2·√320).</summary>
    private const double CriticalDamping = 36;

    /// <summary>Underdamped spring: gives the soft "boing" when a slice pops out.</summary>
    private struct Spring
    {
        public double Value;
        public double Velocity;

        /// <summary>
        /// Longest time one integration step covers. With these stiffnesses a single step of a late frame (50 ms)
        /// overshoots more each frame instead of settling: slices stayed lit and flew apart on a busy machine.
        /// </summary>
        private const double MaxStep = 1 / 120.0;

        public bool Step(double target, double dt, double stiffness = 320, double damping = 20)
        {
            var steps = Math.Max(1, (int)Math.Ceiling(dt / MaxStep));
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                var force = stiffness * (target - Value) - damping * Velocity;
                Velocity += force * h;
                Value += Velocity * h;
            }
            var settled = Math.Abs(target - Value) < 0.001 && Math.Abs(Velocity) < 0.01;
            if (settled)
            {
                Value = target;
                Velocity = 0;
            }
            return !settled;
        }
    }

    /// <summary>
    /// A frame loop that got no frame for this long is treated as stopped: a frame request can be lost (the window
    /// hidden in between, for one), and every later animation would wait for it, so the slice under a dragged file
    /// would never light up.
    /// </summary>
    internal const long StalledLoopMs = 250;

    private int _loop;           // the current frame loop; frames of an older one are ignored
    private long _lastTick;      // Environment.TickCount64 of the last frame or frame request

    /// <summary>Frame requests asked again before the animation is finished at once (see <see cref="CheckLoop"/>).</summary>
    internal const int FrameRetries = 2;

    private int _stalls; // times the current animation's frames stopped coming

    private void StartAnimation()
    {
        InvalidateVisual();
        if (_animating && Environment.TickCount64 - _lastTick < StalledLoopMs)
            return;
        _stalls = 0;
        StartLoop();
    }

    private void StartLoop()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            _animating = false;
            return;
        }
        _animating = true;
        _lastFrame = null;
        _lastTick = Environment.TickCount64;
        var loop = ++_loop;
        top.RequestAnimationFrame(time => OnFrame(time, loop));
        Watch(loop);
    }

    private void Watch(int loop) =>
        DispatcherTimer.RunOnce(() =>
        {
            if (loop == _loop)
                CheckLoop();
        }, TimeSpan.FromMilliseconds(StalledLoopMs + 50));

    /// <summary>
    /// Watches the frame loop without waiting for another animation call. When its frames stop (a request lost
    /// while the window was hidden, or the renderer waking up after a long idle), the frame is asked for again; after
    /// <see cref="FrameRetries"/> stalls the animations jump to their end, so an opening donut always appears. Stalls
    /// are counted per animation: frames that trickle in once per retry would otherwise open the donut over seconds.
    /// </summary>
    internal void CheckLoop()
    {
        if (!_animating)
            return;
        if (Environment.TickCount64 - _lastTick < StalledLoopMs)
        {
            Watch(_loop); // frames are coming: keep watching until the loop ends
            return;
        }
        if (++_stalls <= FrameRetries)
        {
            StartLoop();
            return;
        }
        _animating = false;
        _loop++;
        FinishAnimations();
    }

    /// <summary>
    /// Sets the opening, the highlights and the slice layout to where they end (a closing donut closes). The progress
    /// ring keeps its own timers.
    /// </summary>
    internal void FinishAnimations()
    {
        for (var i = 0; i < _hover.Length; i++)
            _hover[i] = new Spring { Value = i == ActiveIndex ? 1 : 0 };
        _centerPulse = default;
        _expand = new Spring { Value = _expanded >= 0 ? 1 : 0 };
        if (_expanded < 0)
            _shownExpanded = -1;
        StepLayout(0, snap: true);
        FinishFlip();
        if (_openClock < double.MaxValue)
        {
            _openClock = double.MaxValue;
            Array.Fill(_open, _closing ? 0 : 1);
            if (_closing)
                FinishClose();
        }
        InvalidateVisual();
    }

    private void OnFrame(TimeSpan time, int loop)
    {
        if (loop != _loop)
            return; // a loop that was given up on as stalled, replaced by a newer one
        _lastTick = Environment.TickCount64;
        var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.05) : 1 / 60.0;
        _lastFrame = time;
        var active = Advance(dt);

        InvalidateVisual();
        if (active && TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(next => OnFrame(next, loop));
        else
            _animating = false;
    }

    /// <summary>
    /// Advances every animation by <paramref name="dt"/> seconds. Returns true while more frames are needed.
    /// Internal so tests can step time deterministically.
    /// </summary>
    internal bool Advance(double dt)
    {
        var active = false;

        if (!Motion.Enabled)
        {
            for (var i = 0; i < _hover.Length; i++)
            {
                _hover[i].Value = i == ActiveIndex ? 1 : 0;
                _hover[i].Velocity = 0;
            }
            Array.Fill(_open, _closing ? 0 : 1);
            _centerPulse = default;
            _expand = new Spring { Value = _expanded >= 0 ? 1 : 0 };
            if (_expanded < 0)
                _shownExpanded = -1;
            StepLayout(dt);
            FinishFlip(); // animations switched off halfway through a flip
            if (_closing)
                FinishClose();
            active |= StepProgress(dt); // hold timers still run without animation
        }
        else
        {
            // Popping out bounces; letting go settles without a bounce: one below zero would make the faded accent
            // flash once more on its way back.
            for (var i = 0; i < _hover.Length; i++)
                active |= i == ActiveIndex ? _hover[i].Step(1, dt) : _hover[i].Step(0, dt, 320, CriticalDamping);
            active |= _centerPulse.Step(0, dt, 260, 12);
            // A soft bounce opening, like the slices popping out; folding away settles without one. The bounce grows the
            // ring by about a pixel, well inside the room kept for it (SubRingSpace).
            active |= _expanded >= 0 ? _expand.Step(1, dt, 320, 24) : _expand.Step(0, dt, 320, CriticalDamping);
            if (_expanded < 0 && _expand.Value <= 0.001)
                _shownExpanded = -1;
            active |= StepProgress(dt);
            active |= StepLayout(dt);
            active |= StepFlip(dt);

            if (_openClock < double.MaxValue)
            {
                _openClock += dt;
                var done = true;
                for (var i = 0; i < _open.Length; i++)
                {
                    var t = Math.Clamp((_openClock - i * (_closing ? 0.01 : OpenStagger)) / (_closing ? 0.16 : OpenDuration), 0, 1);
                    _open[i] = _closing ? 1 - EaseInCubic(t) : EaseOutBack(t);
                    done &= t >= 1;
                }
                if (done)
                {
                    _openClock = double.MaxValue;
                    if (_closing)
                        FinishClose();
                }
                else
                {
                    active = true;
                }
            }
        }

        return active;
    }

    private void FinishClose()
    {
        _closing = false;
        var callback = _closeCallback;
        _closeCallback = null;
        callback?.Invoke();
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.55;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private static double EaseInCubic(double t) => t * t * t;
}
