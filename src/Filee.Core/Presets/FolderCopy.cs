using Filee.Core.Platform;

namespace Filee.Core.Presets;

/// <summary>Byte-preserving copies, published without overwriting any existing file or folder.</summary>
public static class FolderCopy
{
    private const string WorkPrefix = ".filee-copy-";

    /// <summary>Watchers ignore unpublished copies, including files inside their temporary directories.</summary>
    public static bool IsWorkPath(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.StartsWith(WorkPrefix, StringComparison.Ordinal) && part.EndsWith(".tmp", StringComparison.Ordinal));

    /// <returns>The new path, or null when the source is already in the destination folder.</returns>
    public static async Task<string?> CopyAsync(string source, OutputRule rule, CancellationToken cancellationToken)
    {
        if (rule.AskEveryTime || rule.Location != OutputLocation.CustomFolder || string.IsNullOrWhiteSpace(rule.CustomFolder))
            throw new IOException("Choose a destination folder for Copy to folder.");
        var destination = Path.GetFullPath(rule.CustomFolder);
        if (!Directory.Exists(destination))
            throw new DirectoryNotFoundException("The destination folder is missing or unavailable: " + destination);
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        if (FileSystemPaths.Comparer.Equals(Path.GetDirectoryName(source), Path.TrimEndingDirectorySeparator(destination)))
            return null;
        var directory = Directory.Exists(source);
        if (directory && (FileSystemPaths.Comparer.Equals(destination, source)
            || destination.StartsWith(source + Path.DirectorySeparatorChar, FileSystemPaths.Comparison)))
            throw new IOException("A folder cannot be copied into itself or one of its subfolders.");
        var name = rule.ConfirmedPath is { } chosen ? Path.GetFileName(chosen) : Path.GetFileName(source);
        var stage = Path.Combine(destination, WorkPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (directory)
                await CopyDirectoryAsync(source, stage, cancellationToken);
            else
                await CopyFileAsync(source, stage, cancellationToken);
            var extension = directory ? "" : Path.GetExtension(name);
            var stem = directory ? name : Path.GetFileNameWithoutExtension(name);
            for (var n = 1; ; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(destination, n == 1 ? name : $"{stem} ({n}){extension}");
                try
                {
                    if (directory) Directory.Move(stage, target);
                    else File.Move(stage, target, overwrite: false);
                    return target;
                }
                catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
            }
        }
        finally
        {
            // Only this operation's unpublished path is removed, never a source or a published result.
            if (File.Exists(stage)) File.Delete(stage);
            else if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task CopyDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Copy to folder cannot follow a linked directory: " + source);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            await CopyFileAsync(file, Path.Combine(target, Path.GetFileName(file)), cancellationToken);
        foreach (var folder in Directory.EnumerateDirectories(source))
            await CopyDirectoryAsync(folder, Path.Combine(target, Path.GetFileName(folder)), cancellationToken);
    }
}
