// JobQueue: the output allocators converters write through: final outputs (named by OutputPathResolver)
// and temporary files for intermediate steps.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Conversion;

public sealed partial class JobQueue
{
    /// <summary>Allocates final output paths according to the preset's <see cref="OutputRule"/>.</summary>
    private sealed class FinalAllocator(JobQueue queue, OutputRule rule, OutputPathResolver.Tokens tokens) : IOutputAllocator
    {
        /// <summary>
        /// Every path handed out, and whether it is this conversion's alone: nothing was there before (not a file it
        /// overwrites) and no other conversion had it.
        /// </summary>
        private readonly List<(string Path, bool Own)> _allocated = [];

        /// <summary>How many paths were handed out: an output and the picture folder that goes with it are two.</summary>
        public int Handed => _allocated.Count;

        public string? Allocate(string extension, string? suffix = null)
        {
            // Reserve under a lock so two parallel files never pick the same name.
            lock (queue._reservedOutputs)
            {
                var path = OutputPathResolver.Resolve(rule, tokens, extension,
                    p => File.Exists(p) || Directory.Exists(p) || queue._reservedOutputs.ContainsKey(p), suffix);
                if (path is null)
                    return null;

                var nothingThere = !File.Exists(path) && !Directory.Exists(path);
                _allocated.Add((path, queue.Reserve(path) && nothingThere));
                var folder = Path.GetFullPath(Path.GetDirectoryName(path)!);
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    queue._madeFolders.Add(folder);
                }
                // An empty extension asks for a folder (archive extraction): the converter creates it.
                return path;
            }
        }

        /// <summary>The one output was moved to <paramref name="to"/> (next to its source): its name goes with it.</summary>
        public void Moved(string from, string to)
        {
            lock (queue._reservedOutputs)
            {
                var index = _allocated.FindIndex(a => string.Equals(a.Path, from, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    return;
                // Finished and in its place: nothing to undo there any more.
                _allocated[index] = (to, false);
                queue.Unreserve(from);
                queue.Reserve(to);
            }
        }

        /// <summary>
        /// Frees the names handed out but not in <paramref name="except"/> (the outputs the engine returned, which stay
        /// taken until the job ends).
        /// </summary>
        public void Release(IEnumerable<string> except)
        {
            var kept = except.ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (queue._reservedOutputs)
            {
                foreach (var (path, _) in _allocated)
                {
                    if (!kept.Contains(path))
                        queue.Unreserve(path);
                }
                _allocated.Clear();
            }
        }

        /// <summary>
        /// Undoes a conversion that failed or was cancelled: deletes what it started writing at paths that were its
        /// alone (never a file it was to overwrite, nor one another conversion had too), so no half-written file is left
        /// where the user expects a finished one; frees every name, so trying again gets the same one instead of
        /// "name (2)"; and removes a folder it made for them if that left it empty.
        /// </summary>
        public void Discard()
        {
            lock (queue._reservedOutputs)
            {
                foreach (var (path, own) in _allocated)
                {
                    if (own && queue._reservedOutputs.TryGetValue(path, out var reservation) && !reservation.Shared)
                        TryDeleteOutput(path);
                    queue.Unreserve(path);
                }
                foreach (var folder in _allocated.Select(a => Path.GetDirectoryName(Path.GetFullPath(a.Path))!)
                             .Distinct(Platform.FileSystemPaths.Comparer).ToList())
                    queue.RemoveIfEmpty(folder);
                _allocated.Clear();
            }
        }

        private void TryDeleteOutput(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                else if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                queue._log.LogWarning(ex, "Could not delete the unfinished output {Output}", path);
            }
        }
    }

    /// <summary>Allocates unique paths inside the job's work directory for intermediate files.</summary>
    private sealed class TempAllocator(string workDir, string baseName) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null)
        {
            var dir = Path.Combine(workDir, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var ext = extension.TrimStart('.');
            return Path.Combine(dir, ext.Length == 0 ? $"{baseName}{suffix}" : $"{baseName}{suffix}.{ext}");
        }
    }
}
