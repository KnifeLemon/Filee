// The documents of a job that carry macros or other active content, shown in ActiveContentWindow before any of them is
// opened in an office suite. Filee never runs that content; the user decides whether to convert the files at all.

using Filee.Core.Conversion;
using Filee.Core.Localization;

namespace Filee.App.ViewModels;

/// <summary>One document and what it carries.</summary>
/// <param name="Name">File name.</param>
/// <param name="Folder">Folder it is in.</param>
/// <param name="Path">Full path (tooltip).</param>
/// <param name="Findings">What was found, one line each.</param>
/// <param name="StartsByItself">A macro would start by itself when the document opens.</param>
public sealed record ActiveContentFile(string Name, string Folder, string Path, IReadOnlyList<string> Findings, bool StartsByItself);

/// <summary>Everything the window shows.</summary>
public sealed class ActiveContentViewModel
{
    public ActiveContentViewModel(IReadOnlyList<ActiveContentReport> reports, ILocalizer loc)
    {
        Files = reports.Select(r => new ActiveContentFile(
                System.IO.Path.GetFileName(r.SourcePath),
                System.IO.Path.GetDirectoryName(r.SourcePath) ?? "",
                r.SourcePath,
                r.Findings.Select(f => Line(f, loc)).ToList(),
                r.Findings.Any(f => f.Kind == ActiveContentKind.AutoRunMacro)))
            .ToList();
        Summary = string.Format(loc["active.summary"], Files.Count);
    }

    public IReadOnlyList<ActiveContentFile> Files { get; }

    /// <summary>"2 files contain macros or other active content."</summary>
    public string Summary { get; }

    /// <summary>"Macro: Module1", "Starts by itself: AutoOpen (Module1)", "External link: attachedTemplate — http://…".</summary>
    internal static string Line(ActiveContentFinding finding, ILocalizer loc)
    {
        var kind = loc[$"active.kind.{Key(finding.Kind)}"];
        return finding.Detail is { Length: > 0 } detail ? $"{kind}: {finding.Name} — {detail}" : $"{kind}: {finding.Name}";
    }

    /// <summary>The localization key part of a kind (<c>active.kind.*</c>).</summary>
    internal static string Key(ActiveContentKind kind) => kind switch
    {
        ActiveContentKind.Macro => "macro",
        ActiveContentKind.AutoRunMacro => "autorun",
        ActiveContentKind.Script => "script",
        ActiveContentKind.ActiveX => "activex",
        ActiveContentKind.ExternalLink => "external_link",
        ActiveContentKind.Dde => "dde",
        _ => "embedded",
    };
}
