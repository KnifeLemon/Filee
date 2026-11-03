// The command line of a "Run a program" preset: the argument template with its placeholders expanded.
// Pure logic, unit tested. The expansion rules are what issue #966 agrees on with the maintainer:
// every placeholder expands inside one argument and is never split; {inputs} is the one placeholder
// that becomes several arguments (one per path), so a file name can never inject a command.

using System.Security.Cryptography;
using System.Text;

namespace Filee.Core.Presets;

/// <summary>Expands the argument template of a "Run a program" preset into single arguments.</summary>
public static class ProgramTool
{
    /// <summary>The one path of a per-file run.</summary>
    public const string InputPlaceholder = "{input}";

    /// <summary>All paths of an all-files run: the one placeholder that expands into several arguments.</summary>
    public const string InputsPlaceholder = "{inputs}";

    /// <summary>Name without extension of the first input.</summary>
    public const string NamePlaceholder = "{name}";

    /// <summary>Extension without dot of the first input.</summary>
    public const string ExtPlaceholder = "{ext}";

    /// <summary>Directory of the first input.</summary>
    public const string DirPlaceholder = "{dir}";

    /// <summary>The output path Filee picked under the usual output rules.</summary>
    public const string OutputPlaceholder = "{output}";

    /// <summary>The directory the output goes into.</summary>
    public const string OutputDirPlaceholder = "{outputDir}";

    /// <summary>
    /// Splits the template the way a command line is read (spaces separate, double quotes group) and expands the
    /// placeholders. Every placeholder fills one argument; <see cref="InputsPlaceholder"/> becomes one argument per
    /// path. <paramref name="output"/> may be null while the placeholder still expands, as an empty argument.
    /// </summary>
    public static List<string> BuildArguments(string? template, IReadOnlyList<string> inputs, string? output)
    {
        var arguments = new List<string>();
        foreach (var token in Tokens(template ?? ""))
        {
            if (token.Equals(InputsPlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                // One entry per path: still just ArgumentList.Add, so no shell and no injection surface.
                arguments.AddRange(inputs);
            }
            else
            {
                arguments.Add(Expand(token, inputs, output));
            }
        }
        return arguments;
    }

    /// <summary>True if the template asks for the output path Filee picks.</summary>
    public static bool ReferencesOutput(string? template) =>
        template is not null
        && (template.Contains(OutputPlaceholder, StringComparison.OrdinalIgnoreCase)
            || template.Contains(OutputDirPlaceholder, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What the confirmation step of an imported preset hashes: the program and the argument template — not the
    /// expanded command, or every new input would ask again. Paths are compared without case on Windows.
    /// </summary>
    public static string CommandHash(string executable, string arguments)
    {
        var normalized = Path.GetFullPath(executable.Trim()).ToLowerInvariant() + "\n" + arguments.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash);
    }

    /// <summary>Splits a command line into tokens: spaces separate, double quotes group and are removed.</summary>
    private static IEnumerable<string> Tokens(string template)
    {
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var ch in template)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (!quoted && char.IsWhiteSpace(ch))
            {
                if (started || current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(ch);
                started = true;
            }
        }
        if (started || current.Length > 0)
            yield return current.ToString();
    }

    private static string Expand(string token, IReadOnlyList<string> inputs, string? output)
    {
        var first = inputs.Count > 0 ? inputs[0] : "";
        return token.Replace(InputPlaceholder, first, StringComparison.OrdinalIgnoreCase)
            .Replace(InputsPlaceholder, first, StringComparison.OrdinalIgnoreCase)
            .Replace(OutputPlaceholder, output ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace(OutputDirPlaceholder, output is null ? "" : Path.GetDirectoryName(Path.GetFullPath(output)) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace(NamePlaceholder, Path.GetFileNameWithoutExtension(first), StringComparison.OrdinalIgnoreCase)
            .Replace(ExtPlaceholder, Path.GetExtension(first).TrimStart('.'), StringComparison.OrdinalIgnoreCase)
            .Replace(DirPlaceholder, first.Length == 0 ? "" : Path.GetDirectoryName(Path.GetFullPath(first)) ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
