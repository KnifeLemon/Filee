// Creates data ISO images in-process; folder contents become the disc root without an external program.

using System.Text;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.Engines.Archives;

/// <summary>Writes Joliet or UDF 2.01 data images from explicitly identified folder inputs.</summary>
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
    public EngineStatus GetStatus() => EngineStatus.Available(version: "LTRData.DiscUtils 1.0.90");

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(0);
            var root = new DirectoryInfo(Path.GetFullPath(Path.TrimEndingDirectorySeparator(step.InputPath)));
            if (!root.Exists) throw new DirectoryNotFoundException(root.FullName);
            if (root.Parent is null)
                throw new ConversionRefusedException("error.iso_root", root.FullName);
            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ConversionRefusedException("error.iso_link", root.FullName);
            var output = step.Output.Allocate("iso");
            if (output is null) return [];
            output = Path.GetFullPath(output);
            var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "FILEE" };
            var udf = step.Preset.Archive.IsoFileSystem == IsoFileSystem.Udf201
                ? new UdfBuilder { VolumeIdentifier = "FILEE" } : null;
            if (!Enum.IsDefined(step.Preset.Archive.IsoFileSystem))
                throw new ArgumentException("Unknown ISO filesystem.");
            var sources = new List<(string Path, long Length, DateTime Modified)>();
            string? mark = null;
            void Add(DirectoryInfo directory, string prefix)
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if (FileSystemPaths.Comparer.Equals(entry.FullName, output)) continue;
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new ConversionRefusedException("error.iso_link", entry.Name);
                    var name = prefix + entry.Name;
                    if (udf is not null)
                        ValidateUdfName(entry.Name, name);
                    // DiscUtils appends a version suffix (;1), and a dot for extensionless files.
                    if (udf is null && (entry.Name.Length > 62 || entry.Name.Any(c => char.IsControl(c) || char.IsSurrogate(c) || c is ';' or '\\' or '*' or ':' or '?') || name.Length > 240))
                        throw new ConversionRefusedException("error.iso_name", name);
                    BuildDirectoryMember? member = null;
                    if (entry is DirectoryInfo child)
                    {
                        if (udf is null) member = builder.AddDirectory(name);
                        else udf.AddDirectory(name);
                        Add(child, name + "\\");
                    }
                    else
                    {
                        sources.Add((entry.FullName, ((FileInfo)entry).Length, entry.LastWriteTimeUtc));
                        if (udf is null && ((FileInfo)entry).Length > uint.MaxValue)
                            throw new ConversionRefusedException("error.iso_file_size", name);
                        if (udf is null) member = builder.AddFile(name, entry.FullName);
                        else udf.AddFile(name, entry.FullName);
                        mark ??= MarkOfTheWeb.Read(entry.FullName);
                    }
                    if (member is not null) member.CreationTime = new DateTime(
                        Math.Clamp(entry.LastWriteTimeUtc.Ticks, MinimumTimestamp.Ticks, MaximumTimestamp.Ticks), DateTimeKind.Utc);
                }
            }
            // Build the manifest before opening the temporary output, including when the destination is in the source.
            Add(root, "");
            ct.ThrowIfCancellationRequested();
            var temporary = Path.Combine(Path.GetDirectoryName(output)!, ".filee-iso-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var image = udf is null ? builder.Build() : udf.Build())
                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536];
                    long copied = 0;
                    long lastReport = 0;
                    int count;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        try { count = image.Read(buffer, 0, buffer.Length); }
                        catch (IOException) when (udf is not null)
                        {
                            ct.ThrowIfCancellationRequested();
                            throw new ConversionRefusedException("error.iso_changed");
                        }
                        if (count == 0) break;
                        // Keep destination failures outside the source-read catch: a full or unwritable disk is
                        // not evidence that the source changed.
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
                        throw new ConversionRefusedException("error.iso_changed");
                }
                ct.ThrowIfCancellationRequested();
                foreach (var source in sources)
                {
                    var current = new FileInfo(source.Path);
                    if (!current.Exists || current.Length != source.Length || current.LastWriteTimeUtc != source.Modified)
                        throw new ConversionRefusedException("error.iso_changed");
                }
                if (mark is not null)
                    MarkOfTheWeb.TryWrite(temporary, mark);
                File.Move(temporary, output, overwrite: true);
                progress?.Report(1);
                return [output];
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, ct);

    /// <summary>Checks one component against UdfBuilder 1.0.90's path and compressed-Unicode identifier rules.</summary>
    internal static void ValidateUdfName(string component, string relativePath)
    {
        // Slash and backslash are both interpreted as separators by the builder, even on Unix. The payload has
        // 254 bytes plus a compression-ID byte: Latin-1 uses one byte per UTF-16 unit; other names use two.
        if (component.Length == 0 || component is "." or ".." || component.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ConversionRefusedException("error.iso_udf_name", relativePath);
        var wide = component.Any(c => c > byte.MaxValue);
        try
        {
            var bytes = wide ? new UnicodeEncoding(true, false, true).GetByteCount(component) : component.Length;
            if (bytes <= 254) return;
        }
        catch (EncoderFallbackException) { }
        throw new ConversionRefusedException("error.iso_udf_name", relativePath);
    }
}
