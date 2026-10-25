// Active content in documents: macros, ActiveX controls, external links and DDE fields. Filee never runs any of it,
// but a document that carries it is worth a look before an office suite opens it: JobQueue asks (or follows
// Settings → Security and limits) before a step through an engine that opens documents in an office suite.

namespace Filee.Core.Conversion;

/// <summary>A kind of active content a document can carry.</summary>
public enum ActiveContentKind
{
    /// <summary>A macro module or library (VBA, LibreOffice Basic).</summary>
    Macro,

    /// <summary>A macro that would start by itself (AutoOpen, Document_Open, Workbook_Open, an "on load" event).</summary>
    AutoRunMacro,

    /// <summary>A script library other than Basic (Python, JavaScript, BeanShell) stored in the document.</summary>
    Script,

    /// <summary>An ActiveX control.</summary>
    ActiveX,

    /// <summary>A link to an outside file or address whose content would be loaded (an image, a template, a data source).</summary>
    ExternalLink,

    /// <summary>A DDE field or link, which asks another program for data.</summary>
    Dde,

    /// <summary>An embedded OLE object or package (another program's file inside the document).</summary>
    EmbeddedObject,
}

/// <summary>One piece of active content.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Name">Its name in the document: module, event, control or part name.</param>
/// <param name="Detail">More about it when known: the address of a link, the macro an event starts.</param>
public sealed record ActiveContentFinding(ActiveContentKind Kind, string Name, string? Detail = null);

/// <summary>What a document carries.</summary>
/// <param name="SourcePath">The document.</param>
/// <param name="Findings">Everything found, never empty.</param>
public sealed record ActiveContentReport(string SourcePath, IReadOnlyList<ActiveContentFinding> Findings)
{
    /// <summary>True when the document has macros or scripts (not only links or objects).</summary>
    public bool HasCode => Findings.Any(f => f.Kind is ActiveContentKind.Macro or ActiveContentKind.AutoRunMacro or ActiveContentKind.Script);
}

/// <summary>Finds active content in documents. Implemented in Filee.Engines (Office Open XML, OpenDocument, Office 97–2003).</summary>
public interface IActiveContentScanner
{
    /// <summary>
    /// Scans <paramref name="path"/>. Quick: it reads the package's parts, never the whole document. Null when the file
    /// carries nothing, isn't a format the scanner knows, or can't be read (the conversion reports that itself).
    /// </summary>
    /// <param name="formatId">Format id of the file.</param>
    ActiveContentReport? Scan(string path, string formatId);
}

/// <summary>
/// An engine that opens documents in an office suite (LibreOffice). Before a step through it, <see cref="JobQueue"/>
/// checks the source for active content and asks, or follows <see cref="Settings.SecuritySettings.ActiveContent"/>.
/// </summary>
public interface IOpensDocumentsInOfficeSuite
{
}

/// <summary>What the user decided about a job's documents with active content.</summary>
public enum ActiveContentDecision
{
    /// <summary>Convert them.</summary>
    Trust,

    /// <summary>Convert the job's other files, leave these out.</summary>
    Skip,

    /// <summary>Cancel the whole job.</summary>
    Cancel,
}

/// <summary>Asks the user about a job's documents with active content (the app shows a window; the CLI has none).</summary>
/// <param name="job">The job the documents belong to.</param>
/// <param name="reports">The documents and what they carry.</param>
public delegate Task<ActiveContentDecision> ActiveContentPrompt(ConversionJob job, IReadOnlyList<ActiveContentReport> reports,
    CancellationToken cancellationToken);
