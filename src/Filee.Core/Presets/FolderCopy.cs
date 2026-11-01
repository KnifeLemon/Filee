using Filee.Core.Platform;

namespace Filee.Core.Presets;

/// <summary>Byte-preserving copies and moves, published without overwriting any existing file or folder.</summary>
public static class FolderCopy
{
    private const string WorkPrefix = ".filee-copy-";

    /// <summary>Whether two folders are on the same volume, where a move is a rename. Tests replace it.</summary>
    internal static Func<string, string, bool> SameVolume { get; set; } = (a, b) =>
        VolumeOf(a) is { } volume && FileSystemPaths.Comparer.Equals(volume, VolumeOf(b));

    /// <summary>Watchers ignore unpublished copies, including files inside their temporary directories.</summary>
    public static bool IsWorkPath(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.StartsWith(WorkPrefix, StringComparison.Ordinal) && part.EndsWith(".tmp", StringComparison.Ordinal));

    /// <returns>The new path, or null when the source is already in the destination folder.</returns>
    public static async Task<string?> CopyAsync(string source, OutputRule rule, CancellationToken cancellationToken)
    {
        if (Prepare(ref source, rule) is not { } target)
            return null;
        return await CopyThenPublishAsync(source, target, verify: false, cancellationToken);
    }

    /// <summary>
    /// Moves a file or folder into the rule's folder (#856). On the same volume it is a rename. On another one it is
    /// copied under a temporary name, checked to be complete, published, and only then is the original deleted.
    /// </summary>
    /// <returns>
    /// The new path (null when the source is already in the destination folder), and why the original is still there
    /// when it couldn't be deleted after a complete copy (null when it is gone).
    /// </returns>
    public static async Task<(string? Path, string? OriginalKept)> MoveAsync(string source, OutputRule rule, CancellationToken cancellationToken)
    {
        if (Prepare(ref source, rule) is not { } target)
            return (null, null);
        if (SameVolume(Path.GetDirectoryName(source)!, target.Folder) && Rename(source, target, cancellationToken) is { } renamed)
            return (renamed, null);
        var copied = await CopyThenPublishAsync(source, target, verify: true, cancellationToken);
        try
        {
            if (target.Directory) Directory.Delete(source, recursive: true);
            else File.Delete(source);
            return (copied, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy is whole and published: nothing is lost, the original (or what is left of a folder) stays.
            return (copied, ex.Message);
        }
    }

    /// <summary>Where a source goes, or null when it is already in the destination folder.</summary>
    private sealed record Target(string Folder, string Name, bool Directory);

    private static Target? Prepare(ref string source, OutputRule rule)
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
        return new Target(destination, name, directory);
    }

    /// <summary>The free name for the n-th try: the name itself, then "name (2)", "name (3)" ...</summary>
    private static string Candidate(Target target, int n)
    {
        var extension = target.Directory ? "" : Path.GetExtension(target.Name);
        var stem = target.Directory ? target.Name : Path.GetFileNameWithoutExtension(target.Name);
        return Path.Combine(target.Folder, n == 1 ? target.Name : $"{stem} ({n}){extension}");
    }

    /// <summary>
    /// Renames the source to the first free name. Null when the volume can't rename it (a folder that turns out to be
    /// on another volume): the caller copies instead. Any other failure leaves the source where it was.
    /// </summary>
    private static string? Rename(string source, Target target, CancellationToken cancellationToken)
    {
        for (var n = 1; ; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Candidate(target, n);
            try
            {
                if (target.Directory) Directory.Move(source, path);
                else File.Move(source, path, overwrite: false);
                return path;
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
            catch (IOException) when (target.Directory && Directory.Exists(source)) { return null; }
        }
    }

    private static async Task<string> CopyThenPublishAsync(string source, Target target, bool verify, CancellationToken cancellationToken)
    {
        var stage = Path.Combine(target.Folder, WorkPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (target.Directory)
                await CopyDirectoryAsync(source, stage, cancellationToken);
            else
                await CopyFileAsync(source, stage, cancellationToken);
            // A move deletes the original next: only after every byte has arrived.
            if (verify && SizeOf(stage) != SizeOf(source))
                throw new IOException("The copy is incomplete, so the original was kept: " + source);
            for (var n = 1; ; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Candidate(target, n);
                try
                {
                    if (target.Directory) Directory.Move(stage, path);
                    else File.Move(stage, path, overwrite: false);
                    return path;
                }
                catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
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

    /// <summary>Bytes in a file, or in every file of a folder and its subfolders.</summary>
    private static long SizeOf(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
        : new FileInfo(path).Length;

    /// <summary>The mount point (or drive) a path is on: the longest volume root it starts with.</summary>
    private static string? VolumeOf(string path)
    {
        path = Path.GetFullPath(path);
        string? best = null;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                var root = drive.RootDirectory.FullName;
                var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
                if ((FileSystemPaths.Comparer.Equals(path, root) || path.StartsWith(prefix, FileSystemPaths.Comparison))
                    && (best is null || root.Length > best.Length))
                    best = root;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return best;
    }
}
