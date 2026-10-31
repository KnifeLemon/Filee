// OEB (Open eBook 1.x, the predecessor of EPUB) → Book: an OPF package and the HTML documents it lists, either as
// loose files next to the .opf or zipped into an .oeb. The book is copied (or unpacked) into the work folder first, so
// nothing is written next to the user's files and only files inside the package's folder are read; then the package
// is read like an EPUB's (OEB 1 documents, text/x-oeb1-document, are HTML).

using System.IO.Compression;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class OebReader
{
    public static HDocument Read(string path, string workFolder) => ReadBook(path, workFolder).Document;

    public static Book ReadBook(string path, string workFolder, CancellationToken cancellationToken = default)
    {
        var limits = DocumentLimits.Default;
        var folder = EbookFiles.NewFolder(workFolder, "oeb");
        string opf;
        if (IsZip(path))
        {
            using (var package = DocumentPackage.Open(path, limits, () => new InvalidDataException("This OEB file is not a valid ZIP package.")))
            {
                foreach (var entry in package.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/') || EbookFiles.SafePath(folder, entry.FullName) is not { } target)
                        continue;
                    using var source = package.Open(entry);
                    using var bytes = new MemoryStream();
                    source.CopyTo(bytes);
                    EbookFiles.TryWrite(target, bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
                }
            }
            opf = OpfPackage.RootFile(folder);
        }
        else
        {
            opf = CopyLoosePackage(path, folder, limits, cancellationToken);
        }
        return EpubReader.ReadPackage(opf, Path.GetDirectoryName(opf)!, "OEB book", cancellationToken);
    }

    private static bool IsZip(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return stream.Read(head) == 4 && head.SequenceEqual((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]);
    }

    /// <summary>Copies the .opf and the files its manifest lists (inside its folder) into <paramref name="folder"/>.</summary>
    private static string CopyLoosePackage(string opfPath, string folder, DocumentLimits limits, CancellationToken cancellationToken)
    {
        if (new FileInfo(opfPath).Length > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The OEB package is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        var source = Path.GetDirectoryName(Path.GetFullPath(opfPath))!;
        var copy = Path.Combine(folder, Path.GetFileName(opfPath));
        File.Copy(opfPath, copy);
        OpfPackage package;
        try
        {
            package = OpfPackage.Load(copy);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException("The OEB package (.opf) is not well-formed XML.", ex);
        }
        if (package.Manifest.Count > limits.MaxPackageEntries)
            throw DocumentLimits.Exceeded($"The OEB package lists more than {DocumentLimits.Count(limits.MaxPackageEntries)} files.");
        long total = 0;
        foreach (var item in package.Manifest.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Manifest paths were resolved inside the copy's folder; the same relative path inside the source folder.
            var relative = Path.GetRelativePath(folder, item.Path);
            if (EbookFiles.SafePath(source, relative) is not { } original || !File.Exists(original))
                continue;
            var length = new FileInfo(original).Length;
            if (length > limits.MaxPartBytes)
                throw DocumentLimits.Exceeded($"The file {relative} of the OEB book is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
            if ((total += length) > limits.MaxPackageBytes)
                throw DocumentLimits.Exceeded($"The OEB book is larger than {DocumentLimits.Megabytes(limits.MaxPackageBytes)}.");
            Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
            if (!File.Exists(item.Path))
                File.Copy(original, item.Path);
        }
        // Pictures and style sheets the chapters use but the manifest leaves out are not copied: OEB requires them listed.
        return copy;
    }
}
