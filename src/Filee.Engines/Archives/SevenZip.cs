// Runs 7-Zip's console program (7z.exe + 7z.dll on Windows, 7zz elsewhere; GNU LGPL with the unRAR restriction,
// used as a separate process) to list and unpack the rare archives Filee's own readers leave out, and turns its
// messages into clear errors. 7-Zip is an optional engine (Settings → Engines): the copy the user chose, Filee's
// download, or one already on the PC, in that order, like the other optional engines. Filee writes every archive
// format itself and never asks 7-Zip to create one.

using System.Globalization;
using System.Text;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Archives;

/// <summary>One entry of a 7-Zip listing (<c>7z l -slt</c>).</summary>
/// <param name="Path">Path inside the archive as 7-Zip reports it (with backslashes on Windows).</param>
/// <param name="SymbolicLink">Target of a symbolic link entry (tar, cpio, zip from Unix), otherwise null.</param>
/// <param name="HardLink">Target of a hard link entry (tar), otherwise null.</param>
internal sealed record SevenZipEntry(string Path, bool IsFolder, long Size, bool IsEncrypted, string? SymbolicLink, string? HardLink)
{
    /// <summary>Unix mode or attribute string of the entry, used to spot links that carry no target field.</summary>
    public string? Mode { get; init; }

    /// <summary>Modification time (UTC) when the listing gives one.</summary>
    public DateTime? Modified { get; init; }

    /// <summary>True for symbolic links, which Filee never unpacks (they could point anywhere).</summary>
    public bool IsSymbolicLink => !string.IsNullOrEmpty(SymbolicLink)
                                  || Mode is { } mode && mode.Split(' ').Any(part => part.Length == 10 && part[0] == 'l');
}

/// <summary>The 7-Zip console program, when it is installed.</summary>
internal sealed class SevenZip(string executable)
{
    /// <summary>Archives can be many gigabytes; the timeout only ends a process that hangs.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(6);

    /// <summary>
    /// Password passed when reading, so 7-Zip never waits for one on the console. Unencrypted archives ignore it; for
    /// encrypted ones 7-Zip reports "Wrong password", which becomes the "password-protected" message.
    /// </summary>
    private const string NoPassword = "-pfilee-no-password";

    public string Executable { get; } = executable;

    /// <summary>
    /// Set to 1, Filee behaves as if 7-Zip were missing: the archive tests run this way to show which formats need
    /// it (see docs/ENGINES.md, Archives).
    /// </summary>
    public const string HideVariable = "FILEE_HIDE_7ZIP";

    /// <summary>The program's name in 7-Zip's folder: 7z.exe on Windows, 7zz (the full console build) elsewhere.</summary>
    internal static string ProgramName => OperatingSystem.IsWindows() ? "7z.exe" : "7zz";

    /// <summary>
    /// The 7-Zip the archive engine uses: the copy the user chose in Settings → Engines, Filee's download
    /// (engines/7zip), then one on the PC (PATH, Program Files\7-Zip). Null when there is none or it is hidden.
    /// </summary>
    public static SevenZip? Locate()
    {
        if (Environment.GetEnvironmentVariable(HideVariable) == "1")
            return null;
        var program = EngineEnvironment.OwnProgram("7zip", ProgramName)
                      ?? EngineEnvironment.BundledProgram("7zip", ProgramName)
                      ?? EngineEnvironment.SystemProgram("7zip", ProgramName);
        return program is not null && File.Exists(program) ? new SevenZip(program) : null;
    }

    /// <summary>Lists the entries of an archive.</summary>
    public async Task<IReadOnlyList<SevenZipEntry>> ListAsync(string archive, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["l", "-slt", "-sccUTF-8", "-bsp0", NoPassword, "--", Path.GetFullPath(archive)], cancellationToken);
        if (result.ExitCode is not (0 or 1))
            throw Failure(archive, result);
        return ParseListing(result.StandardOutput);
    }

    /// <summary>
    /// Unpacks every entry of <paramref name="archive"/> except <paramref name="exclude"/> into
    /// <paramref name="outputDirectory"/>. Clashing names ("A.txt" and "a.txt" on Windows) are renamed, not overwritten.
    /// The caller passes on the archive's Mark of the Web (not -snz, which 7-Zip older than 22 doesn't know).
    /// </summary>
    public async Task ExtractAsync(string archive, string outputDirectory, IReadOnlyCollection<string> exclude,
        string workDirectory, CancellationToken cancellationToken)
    {
        List<string> args = ["x", "-y", "-aou", "-sccUTF-8", "-scsUTF-8", "-bsp0", "-bb0", NoPassword, "-o" + Path.GetFullPath(outputDirectory)];
        if (exclude.Count > 0)
        {
            // A list file keeps unusual names intact; -spd turns off wildcard matching so "*" in a name is literal.
            var list = Path.Combine(workDirectory, $"exclude-{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(list, exclude, new UTF8Encoding(false), cancellationToken);
            args.AddRange(["-spd", "-x@" + list]);
        }
        args.AddRange(["--", Path.GetFullPath(archive)]);

        var result = await RunAsync(args, cancellationToken);
        if (result.ExitCode is 0 or 1 || result.ExitCode == 2 && OnlyLinkErrors(result))
            return;
        throw Failure(archive, result);
    }

    /// <summary>
    /// Writes what a single-stream archive holds (.gz, .bz2, .xz …, or the .tar of a .tar.gz) to
    /// <paramref name="destination"/> as 7-Zip unpacks it, and stops 7-Zip once more than <paramref name="maxBytes"/>
    /// come out: their headers state no size, or one their author chose. Returns the number of bytes written.
    /// </summary>
    /// <param name="tooLarge">The error for more than <paramref name="maxBytes"/>; by default the lying-header one.</param>
    public async Task<long> DecodeAsync(string archive, Stream destination, long maxBytes, Func<Exception>? tooLarge,
        CancellationToken cancellationToken)
    {
        long written = 0;
        var result = await ProcessRunner.RunWithOutputAsync(Executable,
            ["x", "-so", "-sccUTF-8", "-bsp0", "-bb0", NoPassword, "--", Path.GetFullPath(archive)],
            (output, ct) => written = ArchiveFiles.CopyLimited(output, destination, maxBytes, ct, tooLarge: tooLarge),
            Timeout, cancellationToken);
        if (result.ExitCode is not (0 or 1))
            throw Failure(archive, result);
        return written;
    }

    /// <summary>Parses the technical listing: blocks of "Key = Value" lines, one per entry, after a "----------" line.</summary>
    internal static List<SevenZipEntry> ParseListing(string output)
    {
        var entries = new List<SevenZipEntry>();
        Dictionary<string, string>? fields = null;
        var inEntries = false;

        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (!inEntries)
            {
                inEntries = line.StartsWith("----------", StringComparison.Ordinal);
                continue;
            }
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator > 0)
                (fields ??= new(StringComparer.Ordinal))[line[..separator]] = line[(separator + 3)..];
            else if (line.EndsWith(" =", StringComparison.Ordinal))
                (fields ??= new(StringComparer.Ordinal))[line[..^2]] = "";
        }
        Flush();
        return entries;

        void Flush()
        {
            if (fields is null || !fields.TryGetValue("Path", out var path))
            {
                fields = null;
                return;
            }
            string? Field(string key) => fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
            var attributes = Field("Attributes") ?? "";
            entries.Add(new SevenZipEntry(
                path,
                Field("Folder") == "+" || attributes.StartsWith('D'),
                long.TryParse(Field("Size"), out var size) ? size : 0,
                Field("Encrypted") == "+",
                Field("Symbolic Link"),
                Field("Hard Link"))
            {
                Mode = Field("Mode") ?? Field("Attributes"),
                Modified = ParseTime(Field("Modified")),
            });
            fields = null;
        }
    }

    /// <summary>7-Zip's "2026-11-04 16:45:59" (local time, sometimes with a fraction of a second) as UTC.</summary>
    private static DateTime? ParseTime(string? value) =>
        DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;

    private Task<ProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(Executable, arguments, Timeout, cancellationToken);

    /// <summary>True when every error 7-Zip reported is about a link it did not create (we never want links).</summary>
    private static bool OnlyLinkErrors(ProcessResult result)
    {
        var errors = ErrorLines(result).ToList();
        return errors.Count > 0 && errors.All(e =>
            e.Contains("symbolic link", StringComparison.OrdinalIgnoreCase)
            || e.Contains("Dangerous link", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Turns a failed 7-Zip run on an archive into an exception with a message users understand.</summary>
    private static Exception Failure(string archive, ProcessResult result)
    {
        var name = Path.GetFileName(archive);
        var text = result.StandardError + "\n" + result.StandardOutput;
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("Wrong password") || Has("encrypted archive"))
            return ArchiveSafety.Encrypted(name);
        if (Has("Cannot open the file as") || Has("Is not archive") || Has("Can not open the file as"))
            return ArchiveSafety.NotAnArchive(name);
        if (Has("Unexpected end") || Has("Data Error") || Has("CRC Failed") || Has("Headers Error") || Has("Missing volume"))
            return ArchiveSafety.Damaged(name, ErrorText(result));
        return new InvalidOperationException($"7-Zip could not read {name} (exit {result.ExitCode}). {ErrorText(result)}".Trim());
    }

    /// <summary>
    /// The lines of 7-Zip's output that describe errors ("ERROR: ...", and the lines under an "ERRORS:" heading),
    /// without its banner and statistics.
    /// </summary>
    private static IEnumerable<string> ErrorLines(ProcessResult result)
    {
        var underHeading = false;
        foreach (var line in (result.StandardError + "\n" + result.StandardOutput).Split('\n').Select(l => l.Trim()))
        {
            if (line is "ERRORS:" or "WARNINGS:")
            {
                underHeading = true;
                continue;
            }
            if (line.Length == 0)
            {
                underHeading = false;
                continue;
            }
            if (underHeading
                || line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Open ERROR", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                yield return line;
        }
    }

    private static string ErrorText(ProcessResult result)
    {
        var lines = ErrorLines(result).Distinct().Take(3).ToList();
        return string.Join(" ", lines.Count > 0 ? lines : result.StandardError.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3));
    }
}
