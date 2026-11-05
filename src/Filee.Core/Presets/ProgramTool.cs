// The command line of a "Run a program" preset: the argument template with its placeholders expanded.
// Pure logic, unit tested. The expansion rules are what issue #966 agrees on with the maintainer:
// every placeholder expands inside one argument and is never split; {inputs} is the one placeholder that
// becomes several arguments (one per path) and only when it stands alone as an argument. The expansion
// runs in ONE pass, so a path that happens to contain "{name}" is never expanded again.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Settings;

namespace Filee.Core.Presets;

/// <summary>Expands the argument template of a "Run a program" preset into single arguments.</summary>
public static partial class ProgramTool
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

    /// <summary>Windows stops accepting a command line past this many characters: the start would fail with no message.</summary>
    public const int MaxCommandLineLength = 32767;

    /// <summary>Key of the error shown for a template that uses <c>{inputs}</c> inside a longer argument.</summary>
    public const string InputsAloneErrorKey = "error.program_inputs_alone";

    [GeneratedRegex(@"\{(input|inputs|name|ext|dir|output|outputDir)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>
    /// Splits the template the way a command line is read (spaces separate, double quotes group) and expands the
    /// placeholders in one pass. Every placeholder fills one argument; <see cref="InputsPlaceholder"/> becomes one
    /// argument per path, but only when it stands alone (see <see cref="InputsAlone"/>). <paramref name="output"/>
    /// may be null while the placeholder still expands, as an empty argument.
    /// </summary>
    /// <exception cref="ArgumentException">The template uses <c>{inputs}</c> inside a longer argument.</exception>
    public static List<string> BuildArguments(string? template, IReadOnlyList<string> inputs, string? output)
    {
        if (!InputsAlone(template))
            throw new ArgumentException($"{InputsPlaceholder} must stand alone as an argument.", nameof(template));

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

    /// <summary>
    /// True unless <c>{inputs}</c> appears inside a longer argument (<c>--files={inputs}</c>): no separator is right
    /// for every program, so the editor refuses it instead of guessing (issue #966).
    /// </summary>
    public static bool InputsAlone(string? template) =>
        template is null
        || Tokens(template).All(token => token.Equals(InputsPlaceholder, StringComparison.OrdinalIgnoreCase)
                                          || !token.Contains(InputsPlaceholder, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the template asks for the output path Filee picks.</summary>
    public static bool ReferencesOutput(string? template) =>
        template is not null
        && (template.Contains(OutputPlaceholder, StringComparison.OrdinalIgnoreCase)
            || template.Contains(OutputDirPlaceholder, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What the confirmation step of an imported preset hashes: the program, the argument template and the mode —
    /// not the expanded command (every new input would ask again) and not the program's contents (issue #966).
    /// An empty or relative program hashes to nothing, so it never counts as confirmed.
    /// </summary>
    public static string CommandHash(string? executable, string? arguments, bool allFiles)
    {
        var program = (executable ?? "").Trim();
        if (program.Length == 0 || !Path.IsPathRooted(program))
            return "";
        var normalized = Path.GetFullPath(program).ToLowerInvariant()
                         + "\n" + (allFiles ? "all\n" : "one\n")
                         + (arguments ?? "").Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// True when this exact program, template and mode were approved on this computer. The app and the CLI both ask
    /// before a preset runs: an imported bundle replaces presets by id, so a shared preset must never run unseen
    /// (issue #966).
    /// </summary>
    public static bool IsConfirmed(AppSettings settings, Preset preset)
    {
        var hash = CommandHash(preset.Program.Executable, preset.Program.Arguments, preset.Program.AllFiles);
        return hash.Length > 0
               && settings.ConfirmedPrograms.TryGetValue(preset.Id, out var stored)
               && stored == hash;
    }

    /// <summary>Records the approval of a preset's current command, on this computer.</summary>
    public static void MarkConfirmed(AppSettings settings, Preset preset) =>
        settings.ConfirmedPrograms[preset.Id] =
            CommandHash(preset.Program.Executable, preset.Program.Arguments, preset.Program.AllFiles);

    /// <summary>
    /// The longest the start could be on Windows: the program plus every argument, quoted as needed, and the spaces
    /// between them. Past <see cref="MaxCommandLineLength"/> nothing starts at all, so the job stops first with a
    /// message instead (a big all-files run is what gets there).
    /// </summary>
    public static int CommandLineLength(string? executable, IReadOnlyList<string> arguments)
    {
        var length = Quoted(executable ?? "").Length;
        foreach (var argument in arguments)
            length += 1 + Quoted(argument).Length;
        return length;
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

    /// <summary>One pass over the whole token: text a path brings in is never expanded again.</summary>
    private static string Expand(string token, IReadOnlyList<string> inputs, string? output)
    {
        var first = inputs.Count > 0 ? inputs[0] : "";
        return Placeholder().Replace(token, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "input" or "inputs" => first,
            "name" => Path.GetFileNameWithoutExtension(first),
            "ext" => Path.GetExtension(first).TrimStart('.'),
            "dir" => first.Length == 0 ? "" : Path.GetDirectoryName(Path.GetFullPath(first)) ?? "",
            "output" => output ?? "",
            "outputdir" => output is null ? "" : Path.GetDirectoryName(Path.GetFullPath(output)) ?? "",
            _ => match.Value,
        });
    }

    private static string Quoted(string argument) => argument.Contains(' ') ? $"\"{argument}\"" : argument;
}
