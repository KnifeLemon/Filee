// Worksheet → minimal XLSX package (one sheet, values only): numbers as numbers, everything else as inline strings,
// column widths from the sheet. Used for CSV → XLSX; opens in Excel, LibreOffice and Google Sheets.

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Filee.Engines.Office.Sheets;

internal static class XlsxWriter
{
    public static void Write(Worksheet sheet, string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
            """);
        Add(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Add(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
            """);
        Add(zip, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Escape(SheetName(sheet.Name))}" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        Add(zip, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/><family val="2"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
            """);
        Add(zip, "xl/worksheets/sheet1.xml", SheetXml(sheet));
    }

    private static string SheetXml(Worksheet sheet)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        if (sheet.ColumnWidths.Count > 0)
        {
            sb.Append("<cols>");
            foreach (var (column, width) in sheet.ColumnWidths.OrderBy(kv => kv.Key))
                sb.Append(CultureInfo.InvariantCulture, $"<col min=\"{column + 1}\" max=\"{column + 1}\" width=\"{width:0.##}\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        foreach (var (row, cells) in sheet.Rows)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{row + 1}\">");
            foreach (var (column, cell) in cells)
            {
                var reference = ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture);
                if (cell.IsNumber && double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"><v>{number.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                else
                    sb.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(cell.Text)}</t></is></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    /// <summary>0 → "A", 26 → "AA".</summary>
    internal static string ColumnName(int column)
    {
        var name = "";
        for (var c = column + 1; c > 0; c = (c - 1) / 26)
            name = (char)('A' + (c - 1) % 26) + name;
        return name;
    }

    /// <summary>Excel sheet names: at most 31 characters, none of : \ / ? * [ ].</summary>
    private static string SheetName(string name)
    {
        var clean = new string([.. name.Where(ch => ch is not (':' or '\\' or '/' or '?' or '*' or '[' or ']'))]).Trim('\'').Trim();
        clean = clean.Length == 0 ? "Sheet1" : clean;
        return clean.Length > 31 ? clean[..31] : clean;
    }

    private static string Escape(string text) =>
        SecurityElement.Escape(new string([.. text.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ')])) ?? "";

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
