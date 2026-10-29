// FreeMASTER oscilloscope TXT exports: a marker, a tab-separated header and tab-separated measurement rows.

using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Data;

internal static class FreeMasterText
{
    public static List<List<string>> Read(string text)
    {
        var rows = CsvFormat.Parse(text.TrimStart('\uFEFF'), CsvFormat.Tab)
            .Where(row => row.Count != 1 || row[0].Length != 0).ToList();
        if (rows.Count < 3 || rows[0].Count != 1 || rows[0][0].Trim() != "# Oscilloscope Data"
            || rows[1].Count < 2 || rows[1][0].Trim() != "# Seconds")
            throw new InvalidDataException("Expected a FreeMASTER oscilloscope export with a tab-separated header and data.");

        var header = rows[1];
        header[0] = "Seconds";
        var result = rows.Skip(1).ToList();
        if (result.Skip(1).Any(row => row.Count != header.Count))
            throw new InvalidDataException("A FreeMASTER data row has a different number of columns from the header.");
        return result;
    }
}
