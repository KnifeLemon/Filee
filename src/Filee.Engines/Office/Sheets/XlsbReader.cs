// XLSB (Excel binary workbook, BIFF12 inside a ZIP package) → Workbook without Excel or LibreOffice: the binary
// records are read by ExcelDataReader (MIT) through the same cell and layout loop as XlsReader.
// ExcelDataReader exposes no fonts, fills or borders, so XLSB cells keep the default look; widths and heights come
// from ExcelDataReader itself.

using System.IO.Compression;
using ExcelDataReader.Exceptions;

namespace Filee.Engines.Office.Sheets;

internal static class XlsbReader
{
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0];

    /// <summary>Reads every visible sheet of <paramref name="path"/>.</summary>
    public static Workbook Read(string path)
    {
        // A password-protected XLSB is wrapped in an OLE compound file (EncryptedPackage / EncryptionInfo).
        if (XlsReader.StartsWith(path, OleSignature))
            throw new InvalidDataException("The workbook is protected with a password (encrypted). Remove the password in Excel and convert it again.");

        if (!XlsReader.StartsWith(path, "PK"u8))
            throw new InvalidDataException("The file is not an Excel binary workbook (XLSB).");

        var hasWorkbookBin = false;
        var hasWorkbookXml = false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Equals("xl/workbook.bin", StringComparison.OrdinalIgnoreCase))
                    hasWorkbookBin = true;
                else if (entry.FullName.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase))
                    hasWorkbookXml = true;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The file is not an Excel binary workbook (XLSB).", ex);
        }

        if (!hasWorkbookBin)
        {
            // XLSX files saved with an .xlsb name still convert through the XLSX reader (same courtesy as XlsReader).
            if (hasWorkbookXml)
                return XlsxReader.Read(path);
            throw new InvalidDataException("The file is not an Excel binary workbook (XLSB).");
        }

        try
        {
            using var stream = File.OpenRead(path);
            return XlsReader.ReadWithExcelDataReader(stream, layouts: null);
        }
        catch (InvalidPasswordException)
        {
            throw new InvalidDataException("The workbook is protected with a password (encrypted). Remove the password in Excel and convert it again.");
        }
        catch (Exception ex) when (ex is ExcelReaderException or NotSupportedException or EndOfStreamException or InvalidDataException)
        {
            throw new InvalidDataException("The Excel binary workbook (XLSB) could not be read: " + ex.Message, ex);
        }
    }
}
