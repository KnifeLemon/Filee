// Versions of engine copies Filee didn't download (the user's own, or one found on the PC), compared with the version
// Filee is tested with (engines.json). An older copy still works for most conversions; the Engines page only says
// that an update is recommended.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Filee.Engines.Infrastructure;

public static partial class CopyVersions
{
    private static readonly ConcurrentDictionary<string, Version?> Cache = new();

    /// <summary>The version Filee is tested with ("9.0"), from engines.json; null for unknown packages.</summary>
    // Where Filee offers no download (FFmpeg and Ghostscript on macOS come from Homebrew), the version it is tested
    // with is the one the Windows list names, so an older copy still gets "update recommended".
    public static Version? Expected(string id) =>
        Parse(EngineVersions.Component(id) ?? ReferenceComponents.Value.GetValueOrDefault(id)?.Version);

    private static readonly Lazy<IReadOnlyDictionary<string, EngineComponent>> ReferenceComponents =
        new(() => EngineDownloads.ComponentsForRuntime("win-x64"));

    /// <summary>
    /// The version of the copy in <paramref name="folder"/> (major.minor), or null when it can't be told (e.g. an
    /// FFmpeg git build named by date). Cached per program and file time once the program answered: one that didn't
    /// start, timed out or failed is asked again next time, while one that answered keeps its version until its file
    /// changes, even if it stops working (Runs starts it again).
    /// </summary>
    public static Version? Installed(string id, string folder)
    {
        if (ProgramOf(id, folder) is not { } program)
            return null;
        var key = $"{program}|{File.GetLastWriteTimeUtc(program).Ticks}";
        if (Cache.TryGetValue(key, out var known))
            return known;
        var (text, works) = Read(id, program);
        var version = ParseVersion(id, text);
        // A start that failed or timed out once (many programs started together, a library missing until repaired) is
        // tried again next time.
        if (works || version is not null)
            Cache[key] = version;
        return version;
    }

    /// <summary>
    /// True when the copy's program in <paramref name="folder"/> starts and answers its version switch (exit code 0, or
    /// a version in what it printed). Started again on every call: the copy may have stopped working since (a library
    /// it needs was removed) while the program file stayed the same.
    /// </summary>
    public static bool Runs(string id, string folder)
    {
        if (ProgramOf(id, folder) is not { } program)
            return false;
        if (id == "libreoffice" && OperatingSystem.IsWindows())
            return File.Exists(Path.Combine(folder, "soffice.exe"));
        // The file's time is taken before it runs: replaced meanwhile (an upgrade), the new file gets its own read.
        var key = $"{program}|{File.GetLastWriteTimeUtc(program).Ticks}";
        var (text, finished, exitCode) = Start(id, program);
        var version = ParseVersion(id, text);
        // The version read here serves Installed's next call for the same file, so the program starts once.
        if (finished && (exitCode == 0 || version is not null))
        {
            Cache[key] = version;
            return true;
        }
        return false;
    }

    /// <summary>The file the version is read from: the main program, or LibreOffice's version.ini on Windows.</summary>
    private static string? ProgramOf(string id, string folder)
    {
        var program = id switch
        {
            "libreoffice" when OperatingSystem.IsWindows() => Path.Combine(folder, "version.ini"),
            _ when EngineEnvironment.OwnCopyPrograms.TryGetValue(id, out var programs) => Path.Combine(folder, programs[0]),
            _ => null,
        };
        return program is not null && File.Exists(program) ? program : null;
    }

    /// <summary>True when the copy is older (major.minor) than the version Filee is tested with.</summary>
    public static bool IsOutdated(string id, string folder) =>
        Installed(id, folder) is { } installed && Expected(id) is { } expected && installed < expected;

    /// <summary>The version in a program's version output (or LibreOffice's version.ini).</summary>
    internal static Version? ParseVersion(string id, string text)
    {
        var match = id switch
        {
            "ffmpeg" => FfmpegVersion().Match(text),
            "pandoc" => PandocVersion().Match(text),
            "calibre" => CalibreVersion().Match(text),
            "weasyprint" => WeasyPrintVersion().Match(text),
            "libreoffice" => LibreOfficeVersion().Match(text),
            "tesseract" => TesseractVersion().Match(text),
            _ => LeadingVersion().Match(text),
        };
        return VersionOf(match);
    }

    private static Version? Parse(string? text) => VersionOf(LeadingVersion().Match(text ?? ""));

    /// <summary>Major and minor from a match; null when there is none or a number is too big to be a version.</summary>
    private static Version? VersionOf(Match match) =>
        match.Success && int.TryParse(match.Groups[1].ValueSpan, out var major) && int.TryParse(match.Groups[2].ValueSpan, out var minor)
            ? new Version(major, minor) : null;

    /// <summary>What the program says about its version, and whether it started and ended well (exit code 0).</summary>
    private static (string Text, bool Works) Read(string id, string program)
    {
        if (id == "libreoffice" && OperatingSystem.IsWindows())
        {
            // LibreOffice 26 dropped MsiProductVersion from version.ini; soffice.exe next to it still says it.
            var soffice = Path.Combine(Path.GetDirectoryName(program)!, "soffice.exe");
            var product = File.Exists(soffice) ? FileVersionInfo.GetVersionInfo(soffice).ProductVersion : null;
            return (File.ReadAllText(program) + (product is null ? "" : $"\nLibreOffice {product}"), true);
        }
        var (text, finished, exitCode) = Start(id, program);
        return (text, finished && exitCode == 0);
    }

    /// <summary>
    /// Starts the program with its version switch: its output, whether it finished in time, and its exit code.
    /// </summary>
    private static (string Text, bool Finished, int ExitCode) Start(string id, string program)
    {
        // ExifTool's --version prints its manual; -ver prints the number alone.
        var arguments = id switch
        {
            "ffmpeg" => "-hide_banner -version",
            "exiftool" => "-ver",
            _ => "--version",
        };
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return ("", false, -1);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return ("", false, -1);
            }
            return (output.Result + "\n" + errors.Result, true, process.ExitCode);
        }
        // AggregateException: reading what it printed failed (the pipe broke).
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException
            or AggregateException)
        {
            return ("", false, -1);
        }
    }

    [GeneratedRegex(@"ffmpeg version n?(\d+)\.(\d+)")]
    private static partial Regex FfmpegVersion();

    [GeneratedRegex(@"^pandoc(?:\.exe)? (\d+)\.(\d+)", RegexOptions.Multiline)]
    private static partial Regex PandocVersion();

    [GeneratedRegex(@"calibre (\d+)\.(\d+)")]
    private static partial Regex CalibreVersion();

    [GeneratedRegex(@"WeasyPrint version (\d+)\.(\d+)")]
    private static partial Regex WeasyPrintVersion();

    [GeneratedRegex(@"(?:MsiProductVersion=|LibreOffice\s+)(\d+)\.(\d+)")]
    private static partial Regex LibreOfficeVersion();

    // "tesseract 5.5.3" (Homebrew), "tesseract v5.4.0.20240606" (the Windows build Filee downloads).
    [GeneratedRegex(@"^tesseract(?:\.exe)? v?(\d+)\.(\d+)", RegexOptions.Multiline)]
    private static partial Regex TesseractVersion();

    [GeneratedRegex(@"^\s*(\d+)\.(\d+)", RegexOptions.Multiline)]
    private static partial Regex LeadingVersion();
}
