// CorelDRAW (CDR) version downgrade: CDR X6 and newer are ZIP containers whose root.dat records the writing
// version. Rewriting that record (and the matching fields in the metadata and style streams) makes a drawing saved
// by a newer CorelDRAW open in CorelDRAW 2022 (24.0) without touching the artwork streams.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Corel;

/// <summary>
/// CDR to CDR with the version record of CorelDRAW 2022 (24.0). Files already that old (or older) are copied
/// unchanged: newer CorelDRAW versions open them as they are.
/// </summary>
/// <remarks>
/// This is a compatibility downgrade, not a resave: Filee does not read or rewrite the drawing itself, so effects
/// added after CorelDRAW 2022 stay in the file as CorelDRAW wrote them. Check the result in CorelDRAW before
/// printing. CorelDRAW X4 and X5 (riffdata.cdr) and binary files older than X4 are not ZIP containers and are
/// refused.
/// </remarks>
public sealed partial class CorelDrawConverter : IConverter
{
    /// <summary>Core version written into the file: CorelDRAW 2022 (24.0).</summary>
    public const int TargetVersion = 2400;

    private const string Mimetype = "application/x-vnd.corel.zcf.draw.document+zip";

    public string Id => "cdr";
    public string DisplayName => "CorelDRAW (built-in)";

    /// <summary>Deflate runs on every core; large drawings are streamed, so a few at once are fine.</summary>
    public int MaxParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Only CDR to CDR: the version record changes, the drawing does not.</summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } = [new("cdr", "cdr")];

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.02);
            using var input = OpenContainer(step.InputPath);
            var version = ReadVersion(input);
            cancellationToken.ThrowIfCancellationRequested();

            if (step.Output.Allocate("cdr") is not { } output)
                return [];

            // A drawing CorelDRAW 2022 opens as it is needs no rewrite; copying keeps every byte of it.
            if (version <= TargetVersion)
            {
                File.Copy(step.InputPath, output, overwrite: true);
                progress?.Report(1);
                return [output];
            }

            var temporary = Path.Combine(step.WorkDirectory, "corel-v24.cdr");
            WriteContainer(input, temporary, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Verify(temporary);
            File.Copy(temporary, output, overwrite: true);
            progress?.Report(1);
            return [output];
        }, cancellationToken);

    /// <summary>
    /// Opens the CDR as a ZIP container and checks its floor: CDR X6+ writes <c>content/root.dat</c> after an
    /// uncompressed <c>mimetype</c> entry. Older containers cannot be rewritten safely.
    /// </summary>
    private static ZipArchive OpenContainer(string path)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new ConversionRefusedException("error.cdr_not_zip");
        }

        try
        {
            if (archive.Entries.Count == 0
                || archive.Entries[0].FullName != "mimetype"
                || archive.GetEntry("content/root.dat") is null)
            {
                throw new ConversionRefusedException("error.cdr_not_zip");
            }

            using (var stream = archive.GetEntry("mimetype")!.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                if (reader.ReadToEnd().Trim() != Mimetype)
                    throw new ConversionRefusedException("error.cdr_not_zip");
            }
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>The version CorelDRAW wrote, e.g. 2510 for 2025.1.</summary>
    private static int ReadVersion(ZipArchive archive)
    {
        var root = ReadEntry(archive, "content/root.dat");
        var fver = FindChunk(root, "fver");
        if (fver < 0 || BitConverter.ToInt32(root, fver + 4) != 16)
            throw new ConversionRefusedException("error.cdr_no_version");
        var data = fver + 8;
        return root[data + 8] | root[data + 9] << 8;
    }

    /// <summary>
    /// Rebuilds the container in its original entry order. Only the small version-bearing entries are patched; every
    /// other stream is copied as it is. <c>mimetype</c> stays first and uncompressed, as CorelDRAW requires.
    /// </summary>
    private static void WriteContainer(ZipArchive input, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        long copied = 0;
        var total = Math.Max(input.Entries.Sum(e => e.Length), 1);

        using (var file = File.Create(path))
        using (var output = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var entry in input.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var level = entry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var copy = output.CreateEntry(entry.FullName, level);
                copy.LastWriteTime = entry.LastWriteTime;
                copy.ExternalAttributes = entry.ExternalAttributes;
                copy.Comment = entry.Comment;

                using (var target = copy.Open())
                {
                    switch (entry.FullName)
                    {
                        case "content/root.dat":
                            target.Write(PatchRoot(ReadEntry(entry)));
                            break;
                        case "META-INF/metadata.xml":
                            target.Write(PatchMetadata(ReadEntry(entry)));
                            break;
                        case "styles/document.cdss":
                            target.Write(PatchStyles(ReadEntry(entry)));
                            break;
                        default:
                            using (var source = entry.Open())
                                source.CopyTo(target, 128 * 1024);
                            break;
                    }
                }

                copied += entry.Length;
                // The patched entries are read first in practice, but count them as copied too.
                progress?.Report(0.05 + 0.9 * Math.Min(1.0, (double)copied / total));
            }
        }
    }

    /// <summary>
    /// The fver chunk carries the version twice (a 16-bit number and major / minor bytes) and vrsn once. All of
    /// them must agree, or CorelDRAW trusts the newer one.
    /// </summary>
    private static byte[] PatchRoot(byte[] root)
    {
        var copy = (byte[])root.Clone();
        var fver = FindChunk(copy, "fver");
        var vrsn = FindChunk(copy, "vrsn");
        if (fver < 0 || vrsn < 0
            || BitConverter.ToInt32(copy, fver + 4) != 16
            || BitConverter.ToInt32(copy, vrsn + 4) < 12)
            throw new ConversionRefusedException("error.cdr_no_version");

        var versionData = fver + 8;
        copy[versionData + 8] = (byte)(TargetVersion & 0xFF);
        copy[versionData + 9] = (byte)(TargetVersion >> 8);
        copy[versionData + 13] = (byte)(TargetVersion % 100 / 10);
        copy[versionData + 14] = (byte)(TargetVersion / 100);
        var valueData = vrsn + 8;
        copy[valueData + 8] = (byte)(TargetVersion & 0xFF);
        copy[valueData + 9] = (byte)(TargetVersion >> 8);
        copy[valueData + 10] = 0;
        copy[valueData + 11] = 0;
        return copy;
    }

    /// <summary>metadata.xml repeats the version as text; readers that look only there must see the same one.</summary>
    private static byte[] PatchMetadata(byte[] metadata)
    {
        var text = Encoding.UTF8.GetString(metadata);
        text = VersionElement().Replace(text, m => $"<cdr:{m.Groups[1].Value}Version>{TargetVersion}</cdr:{m.Groups[1].Value}Version>");
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>The style stream starts with the app version it was written by, e.g. <c>"version": 2510</c>.</summary>
    private static byte[] PatchStyles(byte[] styles)
    {
        var text = Encoding.UTF8.GetString(styles);
        text = StyleVersion().Replace(text, $"\"version\": {TargetVersion}");
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Reopens the result: a truncated or unreadable rewrite must never replace the user's source.</summary>
    private static void Verify(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var root = ReadEntry(archive, "content/root.dat");
            var fver = FindChunk(root, "fver");
            if (fver < 0 || (root[fver + 16] | root[fver + 17] << 8) != TargetVersion)
                throw new InvalidDataException("The rewritten CorelDRAW file has the wrong version.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new InvalidDataException("The rewritten CorelDRAW file could not be verified.", ex);
        }
    }

    private static byte[] ReadEntry(ZipArchive archive, string name) =>
        archive.GetEntry(name) is { } entry
            ? ReadEntry(entry)
            : throw new ConversionRefusedException("error.cdr_not_zip");

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream(checked((int)entry.Length));
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Offset of a RIFF chunk's header in a root.dat, or -1.</summary>
    private static int FindChunk(byte[] data, string id)
    {
        if (data.Length < 16 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF")
            return -1;
        var position = 12;
        while (position + 8 <= data.Length)
        {
            var size = BitConverter.ToInt32(data, position + 4);
            if (size < 0 || position + 8L + size > data.Length)
                return -1;
            if (Encoding.ASCII.GetString(data, position, 4) == id)
                return position;
            position += 8 + size + (size & 1);
        }
        return -1;
    }

    [GeneratedRegex(@"<cdr:(Core|App)Version>(\d+)</cdr:\1Version>")]
    private static partial Regex VersionElement();

    [GeneratedRegex(@"""version"":\s*\d+")]
    private static partial Regex StyleVersion();
}
