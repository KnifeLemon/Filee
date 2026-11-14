// Mutates the streams of a PDF built in code (content streams, font programs, pictures, functions, CMaps) and saves
// it again, for fuzzing the PostScript writer: the file stays a PDF PDFsharp opens, so the mutations reach the
// translation instead of stopping at the cross-reference table.

using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

internal sealed class PdfStreamFuzzer(byte[] seed, Random random)
{
    /// <summary>Tokens spliced into content streams: operators, odd numbers, unbalanced delimiters.</summary>
    private static readonly string[] Tokens =
    [
        "q", "Q", "cm", "re", "W", "n", "f", "S", "BT", "ET", "Tf", "Tj", "TJ", "Td", "Tm", "Tr", "Do", "sh", "gs", "cs", "scn",
        "BI", "ID", "EI", "d0", "d1", "BDC", "EMC", "[", "]", "<<", ">>", "(", ")", "<", ">", "/F1", "/Fm1", "/P1", "/Ax",
        "-1", "0", "1e9", "-99999", "3.5", "65535", "NaN", "7", "/", "%",
    ];

    public byte[] Next()
    {
        using var input = new MemoryStream(seed);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var streams = document.Internals.GetAllObjects().OfType<PdfDictionary>().Where(d => d.Stream is not null).ToList();
        var mutations = 1 + random.Next(3);
        for (var m = 0; m < mutations && streams.Count > 0; m++)
        {
            var stream = streams[random.Next(streams.Count)];
            byte[] data;
            try
            {
                data = stream.Stream.UnfilteredValue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                continue;
            }
            stream.Stream.Value = Mutate(data);
            stream.Elements.Remove("/Filter");
            stream.Elements.Remove("/DecodeParms");
        }
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private byte[] Mutate(byte[] data)
    {
        var bytes = new List<byte>(data);
        switch (random.Next(5))
        {
            case 0 when bytes.Count > 0: // flip bytes
                for (var i = 0; i < 1 + random.Next(8); i++)
                    bytes[random.Next(bytes.Count)] = (byte)random.Next(256);
                break;
            case 1: // splice tokens
                for (var i = 0; i < 1 + random.Next(6); i++)
                    bytes.InsertRange(random.Next(bytes.Count + 1), Encoding.ASCII.GetBytes(" " + Tokens[random.Next(Tokens.Length)] + " "));
                break;
            case 2 when bytes.Count > 1: // cut a range
                {
                    var start = random.Next(bytes.Count);
                    bytes.RemoveRange(start, Math.Min(bytes.Count - start, 1 + random.Next(64)));
                    break;
                }
            case 3 when bytes.Count > 1: // repeat a range
                {
                    var start = random.Next(bytes.Count);
                    var length = Math.Min(bytes.Count - start, 1 + random.Next(64));
                    var part = bytes.GetRange(start, length);
                    for (var i = 0; i < 1 + random.Next(20); i++)
                        bytes.InsertRange(start, part);
                    break;
                }
            default: // truncate
                bytes = bytes.GetRange(0, random.Next(bytes.Count + 1));
                break;
        }
        return [.. bytes];
    }
}
