// Where the shortcuts that brought files into Filee were, so a shortcut's results are saved next to the shortcut
// rather than next to the file it points to (issue #1706).

using System.Collections.Concurrent;
using Filee.Core.Presets;

namespace Filee.Core.Platform;

/// <summary>
/// The folder of the shortcut each file came in through. A shortcut is replaced by its target as soon as it comes in
/// (<see cref="IPlatformServices.ResolveShortcuts(IReadOnlyList{string})"/>), so this remembers where the shortcut
/// itself was until the conversion starts. The latest way a file came in wins: brought in directly, it is forgotten.
/// </summary>
public sealed class ShortcutFolders
{
    private readonly ConcurrentDictionary<string, string> _folders = new(FileSystemPaths.Comparer);

    /// <summary>The one the app's drops, menus and selections fill.</summary>
    public static ShortcutFolders Shared { get; } = new();

    /// <summary><paramref name="target"/> came in through the shortcut at <paramref name="shortcut"/>.</summary>
    public void Note(string shortcut, string target)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(shortcut)) is { Length: > 0 } folder)
            _folders[Key(target)] = folder;
    }

    /// <summary><paramref name="path"/> came in as itself: it is saved next to itself again.</summary>
    public void Forget(string path) => _folders.TryRemove(Key(path), out _);

    /// <summary>The folder of the shortcut <paramref name="path"/> last came in through; null when it came in directly.</summary>
    public string? FolderOf(string path) => _folders.TryGetValue(Key(path), out var folder) ? folder : null;

    /// <summary>
    /// <paramref name="preset"/> with "next to the source" meaning the shortcut's folder for the
    /// <paramref name="sources"/> that came in through one; the same preset when none did.
    /// </summary>
    public Preset Apply(Preset preset, IEnumerable<string> sources)
    {
        var folders = new Dictionary<string, string>(FileSystemPaths.Comparer);
        foreach (var source in sources)
        {
            if (FolderOf(source) is { } folder)
                folders[Key(source)] = folder;
        }
        if (folders.Count == 0)
            return preset;
        var copy = preset.Clone();
        copy.Output.SourceFolders = folders;
        return copy;
    }

    internal static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
