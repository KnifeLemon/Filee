// Archives: unpack (to a folder, the "Extract" preset), convert between archive formats (ZIP, 7Z, TAR, TAR.GZ,
// TAR.BZ2, TAR.XZ, single-file GZ / BZ2 / XZ, the Minecraft and Modrinth packs that are ZIPs, see GamePacks, and Garry's
// Mod addons, see Gma), and "Compress into one archive" for any files (IFileCombiner).
// Every format is read in-process (SharpCompress, DiscUtils, .NET and Filee's own readers, see ArchiveExtractor) and
// written in-process: ZIP (and the packs), 7Z, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, BZ2, XZ and GMA (7Z and XZ with Filee's
// own LZMA2 encoder, BZ2 with its own bzip2 encoder, see SevenZipWriter, XzWriter and Bzip2Writer). 7-Zip is an
// optional engine (Settings → Engines, LGPL, separate process) that adds no route: when it is installed it unpacks the
// rare variants Filee's readers leave out (PPMd 7z, Quantum CABs, NTFS or APFS disk images …); without it those are
// refused with a message that suggests installing it.
// See docs/ENGINES.md.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Archives;

/// <summary>The archive engine.</summary>
public sealed class ArchiveConverter : IConverter, IFileCombiner
{
    /// <summary>Archive formats Filee writes (plus <see cref="FormatRegistry.Folder"/>).</summary>
    private static readonly string[] ArchiveTargets = ["zip", "7z", "tar", "tgz", "tbz2", "txz", "gma", .. GamePacks.Formats];

    /// <summary>Compressed single files (no names or folders inside); they convert among each other.</summary>
    private static readonly string[] SingleFileSources = ["gz", "bz2", "xz", "lzma", "z", "lz"];

    private static readonly IReadOnlyList<ConversionEdge> AllEdges = CreateEdges();

    private SevenZip? _sevenZip;
    private bool _located;

    /// <summary>How the optional 7-Zip is found (<see cref="SevenZip.Locate"/>); tests run the engine without it.</summary>
    internal Func<SevenZip?> LocateSevenZip { get; init; } = SevenZip.Locate;

    public string Id => "archive";
    public string DisplayName => "Archives (built-in)";

    /// <summary>The optional 7-Zip, looked for once (and again on every status check), or null.</summary>
    private SevenZip? Tool
    {
        get
        {
            if (!_located)
            {
                _sevenZip = LocateSevenZip();
                _located = true;
            }
            return _sevenZip;
        }
    }

    /// <summary>
    /// The bzip2 writer already uses several threads (and 7-Zip, when it unpacks); two jobs at once keep the disk busy
    /// without thrashing it.
    /// </summary>
    public int MaxParallelism => 2;

    /// <summary>The same with or without 7-Zip: every format is read and written in-process.</summary>
    public IReadOnlyList<ConversionEdge> Edges => AllEdges;

    private static IReadOnlyList<ConversionEdge> CreateEdges()
    {
        var sources = ArchiveExtractor.BuiltInFormats;
        var targets = ArchiveTargets.Append(FormatRegistry.Folder).ToList();
        var singleTargets = ArchivePacker.SingleFileFormats.ToList();
        string[] folderTargets = ["zip", "7z", "tar", "tgz", "tbz2", "txz", "gma"];
        return
        [
            .. from source in sources
               from target in targets
               select new ConversionEdge(source, target),
            .. from source in SingleFileSources
               from target in singleTargets
               select new ConversionEdge(source, target),
            .. sources.Select(source => new ConversionEdge(source, "iso", DirectOnly: true)),
            .. folderTargets.Select(target => new ConversionEdge(FormatRegistry.Folder, target, DirectOnly: true)),
        ];
    }

    public EngineStatus GetStatus()
    {
        _sevenZip = LocateSevenZip();
        _located = true;
        return _sevenZip is null
            ? EngineStatus.Available(null, "built in")
            : EngineStatus.Available(_sevenZip.Executable, EngineEnvironment.UsesFileesCopy("7zip")
                ? $"built in + 7-Zip {EngineVersions.Component("7zip")}"
                : "built in + 7-Zip");
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (step.From == FormatRegistry.Folder)
        {
            var folder = new DirectoryInfo(Path.GetFullPath(Path.TrimEndingDirectorySeparator(step.InputPath)));
            if (!folder.Exists) throw new DirectoryNotFoundException(folder.FullName);
            if (folder.Parent is null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ConversionRefusedException("error.unsupported_source", folder.FullName);
            var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
            if (output is null) return [];
            var mark = MarkOfTheWeb.ReadFirst([folder.FullName], cancellationToken: cancellationToken);
            await CombineAsync([folder.FullName], output, step.To, step.Preset, step.WorkDirectory, progress, cancellationToken);
            if (mark is not null) MarkOfTheWeb.TryWrite(output, mark);
            return [output];
        }
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
            if (step.To == "iso")
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await new FolderIsoConverter().ConvertAsync(
                    step with { InputPath = unpacked, From = FormatRegistry.Folder },
                    Scaled(progress, 0.5, 1), cancellationToken);
            }
            if (GamePacks.Formats.Contains(step.To))
                unpacked = GamePacks.Root(unpacked, step.To);
            else if (step.To == "gma")
                unpacked = Gma.Root(unpacked);
            var items = Directory.EnumerateFileSystemEntries(unpacked)
                .Order(StringComparer.Ordinal)
                .Select(path => new PackItem(path, Path.GetFileName(path)))
                .ToList();

            var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
            if (output is null)
                return [];
            await new ArchivePacker().PackAsync(items, output, step.To, step.Preset.Archive.Level, scratch,
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

    public bool CanCombineInto(string format) => ArchivePacker.BuiltInFormats.Contains(format);

    public async Task CombineAsync(IReadOnlyList<string> inputPaths, string outputPath, string format, Preset preset,
        string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // A pack (or a Garry's Mod addon) made from its folder holds the folder's content, so its marker file (addon.json)
        // is at the top.
        var entries = (GamePacks.Formats.Contains(format) || format == "gma") && inputPaths is [var folder] && Directory.Exists(folder)
            ? Directory.EnumerateFileSystemEntries(format == "gma" ? Gma.Root(folder) : GamePacks.Root(folder, format))
                .Order(StringComparer.Ordinal).Select(path => new PackItem(path, Path.GetFileName(path))).ToList()
            : UniqueEntries(inputPaths);
        await new ArchivePacker().PackAsync(entries, outputPath, format, preset.Archive.Level,
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
