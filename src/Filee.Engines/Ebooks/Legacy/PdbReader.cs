// PDB e-books → Book. A .pdb is a Palm database whose type and creator say what is inside:
//  * TEXtREAd (PalmDOC, also called AportisDoc): text records, PalmDOC-compressed or stored, read by MobiReader;
//  * BOOKMOBI: a Mobipocket book that was saved as .pdb, read by MobiReader;
//  * zTXTGPlm (zTXT, Weasel Reader): text records that form one zlib stream.
// eReader (Palm Reader) books are refused: most of them are sold with DRM, and the format is not described openly.
// Plucker, iSilo and the other Palm document formats are refused with their name.

using System.IO.Compression;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class PdbReader
{
    /// <summary>Message for eReader books.</summary>
    public const string EreaderMessage =
        "This is an eReader (Palm Reader) book, which Filee does not read: most eReader books are protected with DRM. " +
        "Open it in an eReader app.";

    public static HDocument Read(string path, string workFolder) => ReadBook(path, workFolder).Document;

    public static Book ReadBook(string path, string workFolder, CancellationToken cancellationToken = default)
    {
        var limits = DocumentLimits.Default;
        if (new FileInfo(path).Length > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The PDB file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        var database = PalmDatabase.Open(File.ReadAllBytes(path));
        return database.TypeCreator switch
        {
            "TEXtREAd" => MobiReader.PalmDocBook(database),
            "BOOKMOBI" => MobiReader.ReadBook(path, workFolder, cancellationToken),
            "zTXTGPlm" => Ztxt(database, limits),
            "PNRdPPrs" or "PNPdPPrs" => throw new InvalidOperationException(EreaderMessage),
            var type => throw new InvalidDataException($"This PDB file is {Kind(type)}, which Filee does not read."),
        };
    }

    private static string Kind(string typeCreator) => typeCreator switch
    {
        "DataPlkr" => "a Plucker book",
        "ToGoToGo" or "SDocSilX" => "an iSilo document",
        "BOOKMTIT" or "BOOKMTIU" => "a Haodoo book",
        "DB99DBOS" => "a DB database",
        _ => $"not an e-book (type \"{new string(typeCreator.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '?').ToArray())}\")",
    };

    /// <summary>
    /// zTXT: record 0 holds the version, the number of text records and the text size; the text records are one zlib
    /// stream (flushed at each record when the book allows random access).
    /// </summary>
    private static Book Ztxt(PalmDatabase database, DocumentLimits limits)
    {
        var header = database.Record(0);
        if (header.Length < 20)
            throw new InvalidDataException("The zTXT book's header is damaged.");
        var records = PalmDatabase.U16(header, 2);
        using var compressed = new MemoryStream();
        for (var i = 1; i <= records && i < database.Count; i++)
            compressed.Write(database.Record(i));
        compressed.Position = 0;
        using var text = new MemoryStream();
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer)) > 0)
            {
                text.Write(buffer, 0, read);
                MobiReader.CheckTextSize(text.Length);
                if (text.Length > limits.InflateRatioFloor && text.Length / Math.Max(1, compressed.Length) > limits.MaxInflateRatio)
                    throw DocumentLimits.Exceeded($"The zTXT text unpacks to more than {limits.MaxInflateRatio} times its size.");
            }
        }
        catch (InvalidDataException) when (text.Length > 0)
        {
            // The end of the stream is damaged: keep the text before it.
        }
        return new Book(PlainText.ToDocument(MobiReader.DecodeText(text.ToArray())), new BookMetadata { Title = database.Name });
    }
}
