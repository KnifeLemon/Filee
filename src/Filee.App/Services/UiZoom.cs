// Everything in Filee's windows larger or smaller, as a browser zooms a page: Ctrl + mouse wheel, Ctrl + plus / minus,
// Ctrl + 0 back to normal. Each tracked window's content is scaled by a layout transform (so layouts that adapt to their
// width, like the clip editor's, see the room they really have), every window at once, and the size is remembered.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Filee.App.Services;

public static class UiZoom
{
    /// <summary>The smallest and largest size, and one step.</summary>
    public const double Min = 0.7, Max = 1.8, Step = 0.1;

    private static readonly List<WeakReference<Window>> Windows = [];
    private static Action<double>? _save;

    /// <summary>How large everything is drawn: 1 as designed.</summary>
    public static double Scale { get; private set; } = 1;

    /// <summary>The size remembered and how to remember a new one.</summary>
    public static void Configure(double scale, Action<double> save)
    {
        _save = save;
        Set(scale, remember: false);
    }

    /// <summary>Scales <paramref name="window"/>'s content and gives it the zoom keys and wheel.</summary>
    public static void Track(Window window)
    {
        lock (Windows)
        {
            Windows.RemoveAll(w => !w.TryGetTarget(out _));
            Windows.Add(new WeakReference<Window>(window));
        }
        window.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Apply(window);
    }

    /// <summary>Sets the size (kept between <see cref="Min"/> and <see cref="Max"/>) in every window.</summary>
    public static void Set(double scale, bool remember = true)
    {
        var value = Math.Round(Math.Clamp(double.IsFinite(scale) ? scale : 1, Min, Max), 2);
        var changed = Math.Abs(value - Scale) > 1e-9;
        Scale = value;
        List<Window> windows;
        lock (Windows)
            windows = Windows.Select(w => w.TryGetTarget(out var window) ? window : null).OfType<Window>().ToList();
        foreach (var window in windows)
            Apply(window);
        if (changed && remember)
            _save?.Invoke(value);
    }

    private static void Apply(Window window)
    {
        if (window.Content is not Control content)
            return;
        if (content is not LayoutTransformControl { Tag: "ui-zoom" } scaled)
        {
            window.Content = null;
            scaled = new LayoutTransformControl { Tag = "ui-zoom", Child = content };
            window.Content = scaled;
        }
        scaled.LayoutTransform = Math.Abs(Scale - 1) < 1e-9 ? null : new ScaleTransform(Scale, Scale);
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl, or Cmd on a Mac.
        if (!(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) || e.Delta.Y == 0)
            return;
        Set(Scale + Math.Sign(e.Delta.Y) * Step);
        e.Handled = true;
    }

    private static void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers is not (KeyModifiers.Control or KeyModifiers.Meta))
            return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                Set(Scale + Step);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                Set(Scale - Step);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                Set(1);
                e.Handled = true;
                break;
        }
    }
}
