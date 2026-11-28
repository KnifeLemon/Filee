// "Choose the DVD title": a DVD-Video disc image dropped on a video or audio preset first opens a window for the title
// and its audio and subtitle tracks (Filee.App's AskDvdWindow). The longest title with every track is chosen to start
// with, so Enter converts what a preset without the window would.

using Filee.Core.Formats;

namespace Filee.Core.Presets;

/// <summary>When the "Choose the DVD title" window opens, and the preset as it then runs for one disc.</summary>
public static class AskedDvd
{
    /// <summary>
    /// The preset converts discs to a video or a sound file, one file per disc: not a group, a tool or a join, which
    /// have no single title to show.
    /// </summary>
    public static bool Asks(Preset preset) =>
        !preset.IsGroup && !preset.IsTool && !preset.JoinsIntoOneMedia && preset.Media.Dvd is null
        && FormatRegistry.FindById(preset.TargetFormat) is { Category: FormatCategory.Video or FormatCategory.Audio };

    /// <summary>
    /// True for a disc image among the dropped files (also a browser file, by its name); whether it holds a DVD-Video
    /// title is read from the image itself.
    /// </summary>
    public static bool IsDiscImage(string path) => FormatRegistry.Detect(path)?.Id == "iso";

    /// <summary>The preset as it runs for one disc: its title and tracks. A copy; the saved preset is left alone.</summary>
    public static Preset Apply(Preset preset, DvdChoice choice)
    {
        var copy = preset.Clone();
        copy.Media.Dvd = choice;
        return copy;
    }
}
