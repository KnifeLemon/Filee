// Archives: unpack (to a folder, the "Extract" preset), convert between archive formats (ZIP, 7Z, TAR, TAR.GZ,
// TAR.BZ2, TAR.XZ and single-file GZ / BZ2 / XZ), and "Compress into one archive" for any files (IFileCombiner).
// Reading is in-process for the common formats (SharpCompress, .NET and Filee's own readers, see ArchiveExtractor);
// the bundled 7-Zip (engines/7zip, LGPL, separate process) reads CAB, DMG, IMG, LHA, ARJ and UDF discs and is the
// fallback for what the in-process readers can't. ZIP, TAR, TAR.GZ and GZ are written in-process, 7Z, BZ2 and XZ
// (also inside TARs) by 7-Zip. Without 7-Zip the engine stays available with the in-process edges only.
// See docs/ENGINES.md.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Archives;

/// <summary>The archive engine.</summary>
public sealed class ArchiveConverter : IConverter, IFileCombiner
{
    /// <summary>Archive formats Filee writes (plus <see cref="FormatRegistry.Folder"/>).</summary>
    private static readonly string[] ArchiveTargets = ["zip", "7z", "tar", "tgz", "tbz2", "txz"];

    /// <summary>Compressed single files (no names or folders inside); they convert among each other.</summary>
    private static readonly string[] SingleFileSources = ["gz", "bz2", "xz", "lzma", "z", "lz"];

    private static readonly IReadOnlyList<ConversionEdge> AllEdges = CreateEdges(withSevenZip: true);
    private static readonly IReadOnlyList<ConversionEdge> BuiltInEdges = CreateEdges(withSevenZip: false);

    private SevenZip? _sevenZip;
    private bool _located;

    public string Id => "archive";
    public string DisplayName => "Archives (7-Zip)";

    /// <summary>The bundled 7-Zip, looked for once (and again on every status check), or null.</summary>
    private SevenZip? Tool
    {
        get
        {
            if (!_located)
            {
                _sevenZip = SevenZip.Locate();
                _located = true;
            }
            return _sevenZip;
        }
    }

    /// <summary>7-Zip already uses several threads; two jobs at once keep the disk busy without thrashing it.</summary>
    public int MaxParallelism => 2;

    /// <summary>Every edge with 7-Zip; without it, the formats read and written in-process.</summary>
    public IReadOnlyList<ConversionEdge> Edges => Tool is null ? BuiltInEdges : AllEdges;

    private static IReadOnlyList<ConversionEdge> CreateEdges(bool withSevenZip)
    {
        var sources = withSevenZip ? ArchiveExtractor.ReadableFormats : ArchiveExtractor.BuiltInFormats;
        var targets = ArchiveTargets.Where(t => withSevenZip || ArchivePacker.BuiltInFormats.Contains(t)).Append(FormatRegistry.Folder).ToList();
        var singleTargets = ArchivePacker.SingleFileFormats.Where(t => withSevenZip || ArchivePacker.BuiltInFormats.Contains(t)).ToList();
        return
        [
            .. from source in sources
               from target in targets
               select new ConversionEdge(source, target),
            .. from source in SingleFileSources
               from target in singleTargets
               select new ConversionEdge(source, target),
        ];
    }

    public EngineStatus GetStatus()
    {
        _sevenZip = SevenZip.Locate();
        _located = true;
        return _sevenZip is null
            ? EngineStatus.Available(null, "built in")
            : EngineStatus.Available(_sevenZip.Executable, $"built in + 7-Zip {EngineVersions.Component("7zip")}");
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var sevenZip = Tool;
        var extractor = new ArchiveExtractor(sevenZip);
        if (step.To == FormatRegistry.Folder)
            return await ExtractToFolderAsync(step, extractor, progress, cancellationToken);

        // Archive → archive: unpack into the job's work folder, then pack everything that came out.
        var scratch = Path.Combine(step.WorkDirectory, "archive-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            string unpacked;
            using (ProgressEstimate.Start(progress, TypicalDuration(step.InputPath), 0.02, 0.45))
                unpacked = await extractor.ExtractAsync(step.InputPath, step.From, scratch, cancellationToken);
            var items = Directory.EnumerateFileSystemEntries(unpacked)
                .Order(StringComparer.Ordinal)
                .Select(path => new PackItem(path, Path.GetFileName(path)))
                .ToList();

            var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
            if (output is null)
                return [];
            await new ArchivePacker(sevenZip).PackAsync(items, output, step.To, step.Preset.Archive.Level, scratch,
                Scaled(progress, 0.5, 1), cancellationToken);
            progress?.Report(1);
            return [output];
        }
        finally
        {
            ArchiveFiles.TryDeleteFolder(scratch);
        }
    }

    /// <summary>
    /// Unpacks into the folder the output rule names. The archive is unpacked into a hidden folder next to it (same
    /// drive, so the final move is instant) and only moved into place when complete; one top folder is not nested twice.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ExtractToFolderAsync(ConversionStep step, ArchiveExtractor extractor,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var output = step.Output.Allocate("");
        if (output is null)
            return [];

        var staging = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.filee-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(staging).Attributes |= FileAttributes.Hidden;
        try
        {
            string unpacked;
            using (ProgressEstimate.Start(progress, TypicalDuration(step.InputPath), 0.02, 0.95))
                unpacked = await extractor.ExtractAsync(step.InputPath, step.From, staging, cancellationToken);
            ArchiveFiles.MoveFolder(ArchiveFiles.ContentRoot(unpacked), output);
            progress?.Report(1);
            return [output];
        }
        finally
        {
            ArchiveFiles.TryDeleteFolder(staging);
        }
    }

    public bool CanCombineInto(string format) =>
        ArchivePacker.BuiltInFormats.Contains(format)
        || (ArchiveTargets.Contains(format) || ArchivePacker.SingleFileFormats.Contains(format))
           && Tool is not null;

    public async Task CombineAsync(IReadOnlyList<string> inputPaths, string outputPath, string format, Preset preset,
        string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var sevenZip = ArchivePacker.BuiltInFormats.Contains(format) ? null : Tool;
        await new ArchivePacker(sevenZip).PackAsync(UniqueEntries(inputPaths), outputPath, format, preset.Archive.Level,
            workDirectory, progress, cancellationToken);
        progress?.Report(1);
    }

    /// <summary>
    /// Each file (or folder) under its own name; later ones with a name already used become "name (2).ext".
    /// </summary>
    internal static List<PackItem> UniqueEntries(IReadOnlyList<string> paths)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<PackItem>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (var n = 2; !used.Add(name); n++)
                name = $"{stem} ({n}){ext}";
            items.Add(new PackItem(path, name));
        }
        return items;
    }

    /// <summary>Expected duration of unpacking for the progress estimate (~60 MB/s).</summary>
    private static TimeSpan TypicalDuration(string path) =>
        TimeSpan.FromSeconds(Math.Max(0.5, new FileInfo(path).Length / 60e6));

    private static IProgress<double>? Scaled(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Progress<double>(p => progress.Report(from + (to - from) * p));
}
