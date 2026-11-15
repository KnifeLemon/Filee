// What checking a shared preset found (#1530): the problems, each with a stable code for the gallery's CI, and what
// the preset is (its kinds, formats and the lowest Filee that runs it), which the CI writes into meta.json.

using Filee.Core.Settings;

namespace Filee.Core.Gallery;

/// <summary>
/// One problem: <see cref="Code"/> is stable (scripts match it), <see cref="File"/> the file in the preset's folder
/// (<c>preset.json</c>, <c>images/2.png</c>), <see cref="Path"/> where in it (<c>presets[0].output.customFolder</c>,
/// empty for the whole file) and <see cref="Message"/> what is wrong, in English, for people.
/// </summary>
public sealed record PresetCheckError(string Code, string File, string Path, string Message)
{
    public override string ToString() =>
        Path.Length == 0 ? $"{File}: {Message} ({Code})" : $"{File}: {Path}: {Message} ({Code})";
}

/// <summary>
/// What a preset.json is, derived from it: <see cref="Kinds"/> (<c>convert</c>, <c>tool</c>, <c>toolbar</c>), the
/// formats it reads (<see cref="From"/>: its donut's extensions) and writes (<see cref="To"/>), the lowest Filee that
/// can install it, and the SHA-256 of the file (lowercase hex) that index.json lists.
/// </summary>
public sealed record PresetCheckDerived(
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> From,
    IReadOnlyList<string> To,
    string MinVersion,
    int Presets,
    int Profiles,
    string Sha256)
{
    /// <summary>The settings that touch the installer's files, most serious first (<see cref="SharedPresetWarnings"/>).</summary>
    public IReadOnlyList<PresetCheckWarning> Warnings { get; init; } = [];

    /// <summary>
    /// The languages the preset is written in, from meta.json: its main <c>language</c> first, then every language with
    /// a title of its own (<c>titleByLang</c>); empty without meta.json.
    /// </summary>
    public IReadOnlyList<string> Languages { get; init; } = [];
}

/// <summary>
/// A setting of a shared preset that touches the installer's files: <see cref="Code"/> (stable, see
/// <see cref="SharedPresetWarnings"/>), <see cref="Severity"/> ("high", "medium" or "info"), what it does in English,
/// and the ids of the presets in the file that have it.
/// </summary>
public sealed record PresetCheckWarning(string Code, string Severity, string Message, IReadOnlyList<string> Presets);

/// <summary>The outcome of <see cref="SharedBundleCheck"/> or <see cref="SharedPresetFolderCheck"/>.</summary>
public sealed class PresetCheckResult
{
    /// <summary>Most problems reported; a file with more gets one last "too many problems".</summary>
    public const int MaxErrors = 200;

    private readonly List<PresetCheckError> _errors = [];

    public IReadOnlyList<PresetCheckError> Errors => _errors;

    /// <summary>True when nothing is wrong.</summary>
    public bool Ok => _errors.Count == 0;

    /// <summary>What the preset.json is; null when it couldn't be read as a preset bundle.</summary>
    public PresetCheckDerived? Derived { get; internal set; }

    /// <summary>
    /// The bundle as the app reads it, when preset.json passed (what an install adds). Each check reads it anew, so it
    /// may be changed.
    /// </summary>
    public PresetBundle? Bundle { get; internal set; }

    /// <summary>True once <see cref="MaxErrors"/> problems were found: further checks stop.</summary>
    internal bool Full => _errors.Count > MaxErrors;

    internal void Add(string code, string file, string path, string message)
    {
        if (_errors.Count < MaxErrors)
            _errors.Add(new PresetCheckError(code, file, path, message));
        else if (_errors.Count == MaxErrors)
            _errors.Add(new PresetCheckError("check.too_many_errors", file, "", $"more than {MaxErrors} problems; the rest aren't listed"));
    }

    internal void AddRange(PresetCheckResult other)
    {
        foreach (var error in other._errors)
            Add(error.Code, error.File, error.Path, error.Message);
    }
}
