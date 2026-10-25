// JobQueue: the work root for temporary files and the start-up sweep of folders a crash left behind.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Conversion;

public sealed partial class JobQueue
{
    /// <summary>Where every job has its work folder, named by the job's <see cref="ConversionJob.Id"/>.</summary>
    public static string DefaultWorkRoot => Path.Combine(Path.GetTempPath(), "Filee");

    /// <summary>
    /// Deletes the work folders of jobs a crash, a forced quit or a power cut kept from removing them: intermediate
    /// PDFs, TIFF pages and video frames can take gigabytes, and the OS doesn't empty its temp folder by itself. Only a
    /// job's folder nothing was written in for <paramref name="age"/> goes, so a job still running in another Filee (the
    /// command line) keeps its own; files from a browser have a sweep of their own (<c>WebDrop.DeleteStale</c>).
    /// </summary>
    public static void DeleteStaleWorkFolders(string workRoot, TimeSpan age)
    {
        try
        {
            var root = new DirectoryInfo(workRoot);
            // A temp folder everyone can write in (/tmp on Linux) may hold a link to somewhere else: never follow one.
            if (!root.Exists || root.LinkTarget is not null)
                return;
            foreach (var folder in root.EnumerateDirectories())
            {
                if (!IsJobId(folder.Name) || folder.LinkTarget is not null)
                    continue;
                try
                {
                    if (DateTime.UtcNow - LastWrite(folder) > age)
                        TryDelete(folder.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Something in it can't be read: it stays, and the other folders are still looked at.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        static bool IsJobId(string name) => name.Length == 10 && name.All(char.IsAsciiHexDigitLower);

        // A folder's own time changes only when an entry right inside it does: a long job writes deeper down. Links
        // inside are not followed, and a folder that can't be read throws (the job's folder then stays).
        static DateTime LastWrite(DirectoryInfo folder)
        {
            var last = folder.LastWriteTimeUtc;
            var everything = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            };
            foreach (var entry in folder.EnumerateFileSystemInfos("*", everything))
            {
                if (entry.LastWriteTimeUtc > last)
                    last = entry.LastWriteTimeUtc;
            }
            return last;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* a viewer may still hold a temp file; the OS cleans temp eventually */ }
        catch (UnauthorizedAccessException) { }
    }
}
