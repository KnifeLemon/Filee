// "Ask every time" for the ringtone's part (#1291): which drops ask, and the preset as it runs once the user has chosen
// the part in the "Cut a clip" window.

using Filee.Core.Formats;

namespace Filee.Core.Presets;

public static class AskedRingtone
{
    /// <summary>The preset makes iPhone ringtones and asks for the part of each sound (<see cref="MediaOptions.AskRingtonePart"/>).</summary>
    public static bool Asks(Preset preset) =>
        preset.Media.AskRingtonePart && preset.TargetFormat == "m4r" && !preset.IsGroup && !preset.IsTool;

    /// <summary>True for a sound among the dropped files, whose part is asked for.</summary>
    public static bool IsSound(string path) => FormatRegistry.Detect(path)?.Category == FormatCategory.Audio;

    /// <summary>
    /// The preset as it runs for one sound: the part from <paramref name="start"/>, <paramref name="length"/> seconds long
    /// (within the limits of a ringtone), and no asking again. A copy; the saved preset is left alone.
    /// </summary>
    public static Preset Apply(Preset preset, double start, double length)
    {
        var copy = preset.Clone();
        copy.Media.RingtoneStart = double.IsFinite(start) ? Math.Max(0, start) : 0;
        copy.Media.RingtoneLength = double.IsFinite(length)
            ? Math.Clamp(length, MediaOptions.MinRingtoneLength, MediaOptions.MaxRingtoneLength)
            : MediaOptions.DefaultRingtoneLength;
        copy.Media.AskRingtonePart = false;
        return copy;
    }
}
