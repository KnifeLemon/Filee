// The donut's look as a slider moves (Settings → Donut toolbar → Edit Donut Size, Theme → donut opacity): the settings
// in memory change at once, and the donut reads them each time it opens; settings.json is written once the slider
// rests, as the window zoom is (UiZoom). Quietly: nothing that SettingsChanged applies again depends on the donut's
// look.

using Avalonia.Threading;
using Filee.Core.Settings;

namespace Filee.App.Services;

internal static class DonutLookSave
{
    /// <summary>How long a slider rests before its value is written.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(600);

    private static DispatcherTimer? _timer;
    private static UserDataStore? _store;

    /// <summary>
    /// Writes <paramref name="store"/>'s settings once no further change comes for <see cref="Delay"/>.
    /// </summary>
    public static void Schedule(UserDataStore store)
    {
        if (_store is { } other && !ReferenceEquals(other, store))
            Flush(); // another store's change still waiting (tests make several)
        _store = store;
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = Delay };
            _timer.Tick += (_, _) => Flush();
        }
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Writes a change still waiting for its moment (the page is left, Filee exits).</summary>
    public static void Flush()
    {
        if (_timer is not { IsEnabled: true } timer)
            return;
        timer.Stop();
        _store?.SaveDonutLook();
    }

    /// <summary>A change is waiting to be written.</summary>
    internal static bool IsPending => _timer is { IsEnabled: true };
}
