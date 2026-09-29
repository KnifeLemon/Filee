// Turns an OutputRule + source file into the concrete output path. Pure logic, unit tested.

using System.Text;

namespace Filee.Core.Presets;

/// <summary>Resolves output file paths from <see cref="OutputRule"/>s.</summary>
public static class OutputPathResolver
{
    /// <summary>Values available to file name pattern tokens.</summary>
    public readonly record struct Tokens(string SourcePath, string PresetName, int Index, DateTime Now);

    /// <summary>
    /// Computes the output path for one output file.
    /// </summary>
    /// <param name="rule">Placement rule of the preset.</param>
    /// <param name="tokens">Token values for the file name pattern.</param>
    /// <param name="targetExtension">Extension of the output, without dot.</param>
    /// <param name="exists">Returns true if a path is already taken (on disk or reserved by this batch).</param>
    /// <param name="suffix">Optional suffix appended to the name, e.g. <c>"_p3"</c> for page 3.</param>
    /// <returns>The path to write, or <c>null</c> when the rule says to skip an existing file.</returns>
    public static string? Resolve(OutputRule rule, Tokens tokens, string targetExtension,
        Func<string, bool> exists, string? suffix = null)
    {
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(tokens.SourcePath)) ?? "";
        var dir = rule.Location switch
        {
            OutputLocation.Subfolder => Path.Combine(sourceDir, Sanitize(ExpandPattern(rule.SubfolderName, tokens), "converted")),
            OutputLocation.CustomFolder when !string.IsNullOrWhiteSpace(rule.CustomFolder) => rule.CustomFolder,
            _ => sourceDir,
        };

        var sourceName = Path.GetFileNameWithoutExtension(tokens.SourcePath);
        var name = Sanitize(ExpandPattern(rule.FileNamePattern, tokens), sourceName) + suffix;
        var ext = targetExtension.TrimStart('.');
        var candidate = Path.Combine(dir, $"{name}.{ext}");

        // The source file is never overwritten, whatever the policy says.
        var isSource = PathsEqual(candidate, tokens.SourcePath);
        if (!isSource && !exists(candidate))
            return candidate;

        if (!isSource && rule.Conflict == ConflictPolicy.Overwrite)
            return candidate;
        if (!isSource && rule.Conflict == ConflictPolicy.Skip)
            return null;

        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(dir, $"{name} ({n}).{ext}");
            if (!exists(candidate) && !PathsEqual(candidate, tokens.SourcePath))
                return candidate;
        }
    }

    /// <summary>Replaces <c>{token}</c> placeholders in a file name pattern.</summary>
    public static string ExpandPattern(string pattern, Tokens tokens)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            pattern = "{name}";

        var ext = Path.GetExtension(tokens.SourcePath).TrimStart('.');
        return pattern
            .Replace("{name}", Path.GetFileNameWithoutExtension(tokens.SourcePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", ext, StringComparison.OrdinalIgnoreCase)
            .Replace("{preset}", tokens.PresetName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", tokens.Now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", tokens.Now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{index}", tokens.Index.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes characters that are invalid in file names on any supported OS.</summary>
    public static string Sanitize(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);

        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? fallback : result;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
