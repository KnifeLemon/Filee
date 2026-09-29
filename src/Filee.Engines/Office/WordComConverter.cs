// Microsoft Word automation (Windows, only when Word is installed). Highest fidelity for DOCX → PDF and the
// only reasonable PDF → DOCX path (Word's "PDF Reflow").

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Converts documents by remote-controlling Microsoft Word through COM.</summary>
public sealed class WordComConverter : IConverter
{
    private const string ProgId = "Word.Application";

    // WdSaveFormat values (https://learn.microsoft.com/office/vba/api/word.wdsaveformat)
    private const int WdFormatDocument97 = 0;
    private const int WdFormatRtf = 6;
    private const int WdFormatEncodedText = 7;
    private const int WdFormatFilteredHtml = 10;
    private const int WdFormatDocumentDefault = 16;
    private const int WdFormatPdf = 17;
    private const int WdFormatOpenDocumentText = 23;
    private const int Utf8CodePage = 65001;

    private static readonly string[] Sources = ["docx", "doc", "rtf", "odt", "txt", "html"];
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public string Id => "word";
    public string DisplayName => "Microsoft Word";

    /// <summary>One Word instance at a time keeps memory use and COM trouble low.</summary>
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from s in Sources
           from t in new[] { "pdf", "docx", "doc", "rtf", "odt", "txt", "html" }
           where s != t
           select new ConversionEdge(s, t),
        new("pdf", "docx", 20), // PDF reflow: works, but layout fidelity varies
    ];

    public EngineStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows())
            return EngineStatus.Unavailable("engine.reason.windows_only");
        return Type.GetTypeFromProgID(ProgId) is null
            ? EngineStatus.Unavailable("engine.reason.not_installed")
            : EngineStatus.Available();
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        var target = step.Output.Allocate(step.To);
        if (target is null)
            return [];

        progress?.Report(0.1);
#pragma warning disable CA1416 // guarded by the OperatingSystem.IsWindows() check above; the analyzer does not see into lambdas
        using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(4)))
            await new AutomationProcessGuard("WINWORD").RunAsync(() => Convert(step.InputPath, target, step.To), Timeout, cancellationToken);
#pragma warning restore CA1416
        progress?.Report(1);
        return [target];
    }

    [SupportedOSPlatform("windows")]
    private static bool Convert(string input, string output, string to)
    {
        var type = Type.GetTypeFromProgID(ProgId) ?? throw new InvalidOperationException("Word is not installed.");
        dynamic? word = null;
        dynamic? document = null;
        try
        {
            word = Activator.CreateInstance(type)!;
            word.Visible = false;
            word.DisplayAlerts = 0; // wdAlertsNone
            document = word.Documents.Open(input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, Visible: false);

            if (to == "pdf")
            {
                document.ExportAsFixedFormat(output, WdFormatPdf);
            }
            else
            {
                var format = to switch
                {
                    "docx" => WdFormatDocumentDefault,
                    "doc" => WdFormatDocument97,
                    "rtf" => WdFormatRtf,
                    "odt" => WdFormatOpenDocumentText,
                    "html" => WdFormatFilteredHtml,
                    "txt" => WdFormatEncodedText,
                    _ => throw new NotSupportedException(to),
                };
                if (to == "txt")
                    document.SaveAs2(output, format, Encoding: Utf8CodePage);
                else
                    document.SaveAs2(output, format);
            }
            return true;
        }
        finally
        {
            try { document?.Close(false); } catch (COMException) { }
            try { word?.Quit(false); } catch (COMException) { }
            if (document is not null) Marshal.FinalReleaseComObject(document);
            if (word is not null) Marshal.FinalReleaseComObject(word);
        }
    }
}
