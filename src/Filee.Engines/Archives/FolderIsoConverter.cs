// Creates data ISO images in-process; folder contents become the disc root without an external program.

using DiscUtils.Iso9660;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.Engines.Archives;

/// <summary>Writes ISO 9660 with Joliet names from explicitly identified folder inputs.</summary>
public sealed class FolderIsoConverter : IConverter
{
    private static readonly DateTime MinimumTimestamp = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaximumTimestamp = new(2155, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    /// <inheritdoc />
    public string Id => "iso";
    /// <inheritdoc />
    public string DisplayName => "ISO images";
    /// <inheritdoc />
    public int MaxParallelism => 2;
    /// <inheritdoc />
    public IReadOnlyList<ConversionEdge> Edges { get; } = [new(FormatRegistry.Folder, "iso", DirectOnly: true)];
    /// <inheritdoc />
    public EngineStatus GetStatus() => EngineStatus.Available(version: "DiscUtils 0.16.13");

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(0);
            var root = new DirectoryInfo(Path.GetFullPath(Path.TrimEndingDirectorySeparator(step.InputPath)));
            if (!root.Exists) throw new DirectoryNotFoundException(root.FullName);
            if (root.Parent is null)
                throw new NotSupportedException("ISO creation does not support drive or filesystem roots. Select a folder inside the drive instead.");
            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new NotSupportedException("Select an original folder, not a symbolic link or junction.");
            var output = step.Output.Allocate("iso");
            if (output is null) return [];
            output = Path.GetFullPath(output);
            var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "FILEE" };
            string? mark = null;
            void Add(DirectoryInfo directory, string prefix)
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if (FileSystemPaths.Comparer.Equals(entry.FullName, output)) continue;
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new NotSupportedException($"ISO creation does not follow symbolic links or junctions: '{entry.Name}'.");
                    var name = prefix + entry.Name;
                    if (entry.Name.Length > 64 || entry.Name.Any(c => char.IsControl(c) || char.IsSurrogate(c) || c is ';' or '\\') || name.Length > 240)
                        throw new NotSupportedException($"Joliet cannot preserve '{name}': names must be at most 64 BMP Unicode characters without control characters, semicolons or backslashes; paths must be at most 240 characters.");
                    BuildDirectoryMember member;
                    if (entry is DirectoryInfo child)
                    {
                        member = builder.AddDirectory(name);
                        Add(child, name + "\\");
                    }
                    else
                    {
                        if (((FileInfo)entry).Length > uint.MaxValue)
                            throw new NotSupportedException($"ISO creation requires individual files smaller than 4 GiB: '{name}'.");
                        member = builder.AddFile(name, entry.FullName);
                        mark ??= MarkOfTheWeb.Read(entry.FullName);
                    }
                    member.CreationTime = new DateTime(
                        Math.Clamp(entry.LastWriteTimeUtc.Ticks, MinimumTimestamp.Ticks, MaximumTimestamp.Ticks), DateTimeKind.Utc);
                }
            }
            // Build the manifest before opening the temporary output, including when the destination is in the source.
            Add(root, "");
            ct.ThrowIfCancellationRequested();
            var temporary = Path.Combine(Path.GetDirectoryName(output)!, ".filee-iso-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var image = builder.Build())
                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536];
                    long copied = 0;
                    long lastReport = 0;
                    int count;
                    while ((count = image.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        destination.Write(buffer, 0, count);
                        copied += count;
                        var now = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (copied == image.Length || System.Diagnostics.Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds >= 100)
                        {
                            lastReport = now;
                            progress?.Report(Math.Min(0.99, copied / (double)image.Length));
                        }
                    }
                    if (copied != image.Length)
                        throw new IOException("ISO creation stopped before all data was written. A source file may have changed during conversion.");
                }
                ct.ThrowIfCancellationRequested();
                if (mark is not null)
                    MarkOfTheWeb.TryWrite(temporary, mark);
                File.Move(temporary, output, overwrite: true);
                progress?.Report(1);
                return [output];
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, ct);
}
