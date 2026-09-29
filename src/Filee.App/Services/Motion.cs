// Global "reduce animations" switch. Styles react through the .reduced-motion class on windows;
// custom-drawn controls (DonutMenu) read Motion.Enabled directly.

using Avalonia.Controls;

namespace Filee.App.Services;

/// <summary>Whether animations are enabled.</summary>
public static class Motion
{
    private static readonly List<WeakReference<Window>> Windows = [];

    /// <summary>False when the user (or the OS) asked for reduced motion.</summary>
    public static bool Enabled { get; private set; } = true;

    public static event EventHandler? Changed;

    public static void Set(bool enabled)
    {
        Enabled = enabled;
        lock (Windows)
        {
            Windows.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Windows)
                if (weak.TryGetTarget(out var window))
                    Apply(window);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Call once per window so its styles follow the setting.</summary>
    public static void Track(Window window)
    {
        lock (Windows)
            Windows.Add(new WeakReference<Window>(window));
        Apply(window);
    }

    private static void Apply(Window window) => window.Classes.Set("reduced-motion", !Enabled);
}
