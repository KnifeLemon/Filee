// Locale-dependent Excel number formats (#1795): the system long date / time ([$-F800], [$-F400]) follows the
// system culture like Excel does, and the East Asian built-ins 27–36 / 50–58 follow the Chinese, Japanese or Korean
// locale of the system or, failing that, of the workbook. Workbooks are built in code.

using System.Globalization;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Tests;

public class NumberFormatterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>23 November 2026, 18:05:09.</summary>
    private static readonly double Evening = NumberFormatter.ToSerial(new DateTime(2026, 11, 23, 18, 5, 9));

    private static readonly int[] EastAsianIds = [.. Enumerable.Range(27, 10), .. Enumerable.Range(50, 9)];

    /// <summary>zh-CN with Windows' default long date (ICU's adds the weekday).</summary>
    private static CultureInfo WindowsChinese()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("zh-CN").Clone();
        culture.DateTimeFormat.LongDatePattern = "yyyy'年'M'月'd'日'";
        return culture;
    }

    private static NumberFormatter For(string culture) => new(culture: CultureInfo.GetCultureInfo(culture));

    // ───────────────────────── System long date and time ─────────────────────────

    [Fact]
    public void System_long_date_follows_the_system_culture_not_the_stored_pattern()
    {
        var formatter = new NumberFormatter(culture: WindowsChinese());

        Assert.Equal("2026年11月23日", formatter.Format(Evening, "[$-F800]dddd\\,\\ mmmm\\ dd\\,\\ yyyy"));
        Assert.Equal("2026年11月23日", formatter.Format(Evening, "[$-x-sysdate]dddd, mmmm dd, yyyy"));
        Assert.Equal("Monday, November 23, 2026", For("en-US").Format(Evening, "[$-F800]dddd, mmmm dd, yyyy"));
        Assert.Equal("2026년 11월 23일 월요일", For("ko-KR").Format(Evening, "[$-F800]dddd, mmmm dd, yyyy"));
        Assert.True(formatter.IsDate("[$-F800]dddd, mmmm dd, yyyy"));
    }

    [Fact]
    public void System_long_time_follows_the_system_culture()
    {
        Assert.Equal("18:05:09", For("zh-CN").Format(Evening, "[$-F400]h:mm:ss AM/PM"));
        // The culture's own long time pattern: ICU (Linux, macOS) puts a narrow no-break space before "PM", Windows a space.
        var us = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal(new DateTime(2026, 11, 23, 18, 5, 9).ToString(us.DateTimeFormat.LongTimePattern, us),
            For("en-US").Format(Evening, "[$-x-systime]h:mm:ss AM/PM"));
        Assert.StartsWith("6:05:09", For("en-US").Format(Evening, "[$-x-systime]h:mm:ss AM/PM"));
        Assert.True(For("zh-CN").IsDate("[$-F400]h:mm:ss AM/PM"));
    }

    [Fact]
    public void System_long_date_without_a_system_culture_uses_the_workbook_locale_then_the_stored_pattern()
    {
        var formatter = new NumberFormatter(culture: CultureInfo.InvariantCulture);
        Assert.Equal("Monday, November 23, 2026", formatter.Format(Evening, "[$-F800]dddd, mmmm dd, yyyy"));

        formatter.WorkbookCulture = CultureInfo.GetCultureInfo("zh-CN");
        Assert.StartsWith("2026年11月23日", formatter.Format(Evening, "[$-F800]dddd, mmmm dd, yyyy"));
    }

    [Fact]
    public void System_long_date_counts_from_1904_in_1904_workbooks()
    {
        var formatter = new NumberFormatter(date1904: true, culture: WindowsChinese());
        Assert.Equal("2026年11月23日", formatter.Format(Evening - 1462, "[$-F800]dddd, mmmm dd, yyyy"));
    }

    [Fact]
    public void System_long_date_out_of_range_falls_back_like_other_dates()
    {
        var formatter = new NumberFormatter(culture: WindowsChinese());
        Assert.Equal(formatter.Format(-1, "dddd, mmmm dd, yyyy"), formatter.Format(-1, "[$-F800]dddd, mmmm dd, yyyy"));
    }

    // ───────────────────────── East Asian built-ins ─────────────────────────

    [Theory]
    [InlineData("zh-CN", 27, "2026年11月")]
    [InlineData("zh-CN", 28, "11月23日")]
    [InlineData("zh-CN", 30, "11-23-26")]
    [InlineData("zh-CN", 31, "2026年11月23日")]
    [InlineData("zh-CN", 32, "18时05分")]
    [InlineData("zh-CN", 33, "18时05分09秒")]
    [InlineData("zh-CN", 34, "下午6时05分")]
    [InlineData("zh-CN", 35, "下午6时05分09秒")]
    [InlineData("zh-CN", 58, "11月23日")]
    [InlineData("zh-TW", 27, "115/11/23")]
    [InlineData("zh-TW", 28, "115年11月23日")]
    [InlineData("zh-TW", 31, "2026年11月23日")]
    [InlineData("zh-TW", 32, "18時05分")]
    [InlineData("zh-TW", 34, "下午06時05分")]
    [InlineData("zh-HK", 27, "115/11/23")]
    [InlineData("ja-JP", 27, "R8.11.23")]
    [InlineData("ja-JP", 28, "令和8年11月23日")]
    [InlineData("ja-JP", 31, "2026年11月23日")]
    [InlineData("ja-JP", 32, "18時05分")]
    [InlineData("ja-JP", 34, "2026年11月")]
    [InlineData("ja-JP", 35, "11月23日")]
    [InlineData("ko-KR", 27, "2026年 11月 23日")]
    [InlineData("ko-KR", 28, "11-23")]
    [InlineData("ko-KR", 31, "2026년 11월 23일")]
    [InlineData("ko-KR", 33, "18시 05분 09초")]
    [InlineData("ko-KR", 34, "2026-11-23")]
    public void East_asian_built_ins_follow_the_system_locale(string culture, int id, string expected)
    {
        var formatter = For(culture);
        var code = formatter.BuiltIn(id);
        Assert.NotNull(code);
        Assert.Equal(expected, formatter.Format(Evening, code));
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void East_asian_built_ins_are_dates(string culture)
    {
        var formatter = For(culture);
        foreach (var id in EastAsianIds)
        {
            var code = formatter.BuiltIn(id)!;
            Assert.True(formatter.IsDate(code), $"{culture} {id}: {code}");
            Assert.NotEqual(NumberFormatter.General(Evening), formatter.Format(Evening, code));
        }
    }

    [Fact]
    public void East_asian_built_ins_on_other_systems_use_the_workbook_locale_then_simplified_chinese()
    {
        var formatter = For("en-US");
        Assert.Equal("2026年11月", formatter.Format(Evening, formatter.BuiltIn(27)));

        formatter.WorkbookCulture = NumberFormatter.CultureOfCharset(128);
        Assert.Equal("R8.11.23", formatter.Format(Evening, formatter.BuiltIn(27)));

        // The system's locale wins over the workbook's, as in Excel.
        var korean = For("ko-KR");
        korean.WorkbookCulture = NumberFormatter.CultureOfCharset(134);
        Assert.Equal("2026-11-23", korean.Format(Evening, korean.BuiltIn(34)));
    }

    [Theory]
    [InlineData(0, 30, "上午12时30分")]
    [InlineData(9, 15, "上午9时15分")]
    [InlineData(12, 30, "下午12时30分")]
    [InlineData(23, 59, "下午11时59分")]
    public void Chinese_am_pm_shows_twelve_hour_time(int hour, int minute, string expected)
    {
        var value = NumberFormatter.ToSerial(new DateTime(2026, 11, 23, hour, minute, 0));
        Assert.Equal(expected, For("zh-CN").Format(value, "上午/下午h\"时\"mm\"分\""));
        Assert.Equal("2026/11/23 " + expected, For("zh-CN").Format(value, "yyyy/m/d 上午/下午h\"时\"mm\"分\""));
    }

    [Fact]
    public void Japanese_eras_change_on_their_first_day()
    {
        var formatter = For("ja-JP");
        const string Code = "[$-411]ggge\"年\"m\"月\"d\"日\"";
        Assert.Equal("平成31年4月30日", formatter.Format(NumberFormatter.ToSerial(new DateTime(2019, 4, 30)), Code));
        Assert.Equal("令和1年5月1日", formatter.Format(NumberFormatter.ToSerial(new DateTime(2019, 5, 1)), Code));
        Assert.Equal("H31.4.30", formatter.Format(NumberFormatter.ToSerial(new DateTime(2019, 4, 30)), "[$-411]ge.m.d"));
        Assert.Equal("令8年", formatter.Format(Evening, "[$-411]gge\"年\""));
    }

    [Theory]
    [InlineData("0.00E+00", 1234.5, "1.23E+03")]
    [InlineData("#,##0\" Yen\"", 1234.6, "1,235 Yen")]
    [InlineData("[Green]0", 5, "5")]
    [InlineData("d \"de\" mmmm", 46349, "23 de November")]
    public void Formats_with_an_e_or_g_that_are_not_eras_are_unchanged(string code, double value, string expected) =>
        Assert.Equal(expected, For("ja-JP").Format(value, code));

    // ───────────────────────── Readers ─────────────────────────

    [Fact]
    public void Xlsx_system_long_date_and_east_asian_built_ins_are_read_for_the_system_and_workbook_locale()
    {
        var path = Workbook(fx.NewFolder(), charset: 134);

        using (new CultureScope(WindowsChinese()))
        {
            var sheet = XlsxReader.Read(path).Sheets[0];
            Assert.Equal("2026年11月23日", sheet.Get(0, 0)!.Text);
            Assert.Equal("2026年11月23日", sheet.Get(1, 0)!.Text);
            Assert.Equal("yyyy\"年\"m\"月\"d\"日\"", sheet.Get(1, 0)!.NumberFormat);
        }
        using (new CultureScope(CultureInfo.GetCultureInfo("en-US")))
        {
            var sheet = XlsxReader.Read(path).Sheets[0];
            Assert.Equal("Monday, November 23, 2026", sheet.Get(0, 0)!.Text);
            Assert.Equal("2026年11月", sheet.Get(2, 0)!.Text);
            Assert.Equal("2026年11月", XlsxReader.Read(Workbook(fx.NewFolder(), charset: null)).Sheets[0].Get(2, 0)!.Text);
            Assert.Equal("R8.11.23", XlsxReader.Read(Workbook(fx.NewFolder(), charset: 128)).Sheets[0].Get(2, 0)!.Text);
            Assert.Equal("2026年 11月 23日", XlsxReader.Read(Workbook(fx.NewFolder(), charset: 129)).Sheets[0].Get(2, 0)!.Text);
        }
    }

    [Fact]
    public void Xls_east_asian_built_ins_are_read_like_xlsx()
    {
        var xls = new XlsBuilder();
        xls.Sheet("日期").Number(0, 0, Math.Floor(Evening), XlsBuilder.EastAsianDate);
        var path = xls.Save(Path.Combine(fx.NewFolder(), "日期.xls"));

        using (new CultureScope(CultureInfo.GetCultureInfo("ko-KR")))
        {
            var cell = XlsReader.Read(path).Sheets[0].Get(0, 0)!;
            Assert.Equal(("2026년 11월 23일", Math.Floor(Evening)), (cell.Text, cell.Value));
        }
        using (new CultureScope(CultureInfo.GetCultureInfo("ja-JP")))
            Assert.Equal("2026年11月23日", XlsReader.Read(path).Sheets[0].Get(0, 0)!.Text);
    }

    [Fact]
    public void Chinese_am_pm_becomes_an_opendocument_am_pm()
    {
        var (xml, type) = OdsNumberStyles.ToOdf("上午/下午h\"时\"mm\"分\"", "N1")!.Value;
        Assert.Equal("time", type);
        Assert.Contains("<number:am-pm/>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("上午", xml, StringComparison.Ordinal);
        Assert.Null(OdsNumberStyles.ToOdf("[$-411]ge.m.d", "N1")); // eras have no data style: plain numbers
    }

    /// <summary>
    /// A1: [$-F800] long date, A2: built-in 31, A3: built-in 27, all on 23 November 2026; the second font carries
    /// <paramref name="charset"/> like the East Asian font Chinese, Japanese and Korean Excel add.
    /// </summary>
    private static string Workbook(string folder, int? charset)
    {
        const string Ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var date = Math.Floor(Evening).ToString(CultureInfo.InvariantCulture);
        var font = charset is { } c ? $"<font><sz val=\"9\"/><name val=\"等线\"/><charset val=\"{c}\"/></font>" : "<font><sz val=\"9\"/><name val=\"Calibri\"/></font>";
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = OpcWriter.Relationships([("rId1", "officeDocument", "xl/workbook.xml")]),
            ["xl/workbook.xml"] = $"<workbook {Ns}><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = OpcWriter.Relationships([("rId1", "worksheet", "worksheets/sheet1.xml"), ("rIdT", "styles", "styles.xml")]),
            ["xl/styles.xml"] = $"<styleSheet {Ns}><numFmts count=\"1\"><numFmt numFmtId=\"176\" formatCode=\"[$-F800]dddd\\,\\ mmmm\\ dd\\,\\ yyyy\"/></numFmts>" +
                                $"<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>{font}</fonts>" +
                                "<cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\"/><xf numFmtId=\"176\" fontId=\"0\" applyNumberFormat=\"1\"/>" +
                                "<xf numFmtId=\"31\" fontId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"27\" fontId=\"0\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet {Ns}><sheetData>" +
                                           $"<row r=\"1\"><c r=\"A1\" s=\"1\"><v>{date}</v></c></row>" +
                                           $"<row r=\"2\"><c r=\"A2\" s=\"2\"><v>{date}</v></c></row>" +
                                           $"<row r=\"3\"><c r=\"A3\" s=\"3\"><v>{date}</v></c></row></sheetData></worksheet>",
            ["[Content_Types].xml"] = OpcWriter.ContentTypes([
                ("xl/workbook.xml", "spreadsheetml.sheet.main"),
                ("xl/worksheets/sheet1.xml", "spreadsheetml.worksheet"),
                ("xl/styles.xml", "spreadsheetml.styles")]),
        };
        var path = Path.Combine(folder, "日期.xlsx");
        OpcWriter.Save(path, parts, new Dictionary<string, byte[]>());
        return path;
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _before = CultureInfo.CurrentCulture;

        public CultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;

        public void Dispose() => CultureInfo.CurrentCulture = _before;
    }
}
