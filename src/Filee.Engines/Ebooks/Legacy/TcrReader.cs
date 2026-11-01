// TCR (the text compression of Psion e-book readers) → Book. After the "!!8-Bit!!" signature come 256 dictionary
// entries, each a length byte and that many bytes; every following byte of the file stands for the entry of that
// number. The text is plain text, mostly in Windows-1252.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class TcrReader
{
    private static readonly byte[] Signature = "!!8-Bit!!"u8.ToArray();

    public static HDocument Read(string path, string workFolder) => ReadBook(path, workFolder).Document;

    public static Book ReadBook(string path, string workFolder, CancellationToken cancellationToken = default)
    {
        var limits = DocumentLimits.Default;
        if (new FileInfo(path).Length > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The TCR file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        var text = Decompress(File.ReadAllBytes(path), cancellationToken);
        return new Book(PlainText.ToDocument(MobiReader.DecodeText(text)), new BookMetadata());
    }

    internal static byte[] Decompress(byte[] data, CancellationToken cancellationToken = default)
    {
        if (!data.AsSpan().StartsWith(Signature))
            throw new InvalidDataException("This is not a TCR file (no \"!!8-Bit!!\" signature).");
        var entries = new byte[256][];
        var position = Signature.Length;
        for (var i = 0; i < 256; i++)
        {
            if (position >= data.Length)
                throw new InvalidDataException("The TCR file's dictionary is cut short.");
            var length = data[position++];
            if (position + length > data.Length)
                throw new InvalidDataException("The TCR file's dictionary is cut short.");
            entries[i] = data.AsSpan(position, length).ToArray();
            position += length;
        }
        using var text = new MemoryStream();
        for (; position < data.Length; position++)
        {
            if ((position & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MobiReader.CheckTextSize(text.Length);
            }
            text.Write(entries[data[position]]);
        }
        MobiReader.CheckTextSize(text.Length);
        return text.ToArray();
    }
}
