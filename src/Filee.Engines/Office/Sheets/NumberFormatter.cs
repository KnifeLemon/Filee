// Excel number formats → the text a cell shows, shared by the XLSX, XLS and ODS readers so a value looks the same
// whichever file it came from. Formatting is done by ExcelNumberFormat (MIT); dates are Excel serial days.
// What ExcelNumberFormat can't do is handled here first: the system long date / time ([$-F800], [$-F400]), the
// East Asian built-in formats (27–36, 50–58), Japanese and Taiwanese era years (g, e) and 上午/下午.

using System.Globalization;
using System.Text;
using ExcelNumberFormat;

namespace Filee.Engines.Office.Sheets;

/// <summary>Formats cell values with Excel number format codes; parsed formats are cached per instance.</summary>
/// <param name="culture">
/// The system culture, for the formats Excel takes from Windows (system long date and time, East Asian built-ins).
/// Null for <see cref="CultureInfo.CurrentCulture"/>: Filee runs on the user's machine, like Excel does.
/// </param>
internal sealed class NumberFormatter(bool date1904 = false, CultureInfo? culture = null)
{
    /// <summary>Built-in number formats (ECMA-376 18.8.30); 14 uses the ISO date order most locales read.</summary>
    public static readonly IReadOnlyDictionary<int, string> BuiltInFormats = new Dictionary<int, string>
    {
        [0] = "General",
        [1] = "0",
        [2] = "0.00",
        [3] = "#,##0",
        [4] = "#,##0.00",
        [9] = "0%",
        [10] = "0.00%",
        [11] = "0.00E+00",
        [12] = "# ?/?",
        [13] = "# ??/??",
        [14] = "yyyy-mm-dd",
        [15] = "d-mmm-yy",
        [16] = "d-mmm",
        [17] = "mmm-yy",
        [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM",
        [20] = "h:mm",
        [21] = "h:mm:ss",
        [22] = "yyyy-mm-dd h:mm",
        [37] = "#,##0 ;(#,##0)",
        [38] = "#,##0 ;[Red](#,##0)",
        [39] = "#,##0.00;(#,##0.00)",
        [40] = "#,##0.00;[Red](#,##0.00)",
        [45] = "mm:ss",
        [46] = "[h]:mm:ss",
        [47] = "mmss.0",
        [48] = "##0.0E+0",
        [49] = "@",
    };

    // East Asian built-ins 27–36 and 50–58 ([MS-OI29500] 2.1.796, ECMA-376 18.8.30): the file stores only the id,
    // and Excel shows it the way its own locale defines it.
    private static readonly Dictionary<int, string> SimplifiedChinese = EastAsian(
        "yyyy\"年\"m\"月\"", "m\"月\"d\"日\"", "m\"月\"d\"日\"", "m-d-yy", "yyyy\"年\"m\"月\"d\"日\"",
        "h\"时\"mm\"分\"", "h\"时\"mm\"分\"ss\"秒\"", "上午/下午h\"时\"mm\"分\"", "上午/下午h\"时\"mm\"分\"ss\"秒\"", "yyyy\"年\"m\"月\"",
        "yyyy\"年\"m\"月\"", "m\"月\"d\"日\"", "yyyy\"年\"m\"月\"", "m\"月\"d\"日\"", "m\"月\"d\"日\"",
        "上午/下午h\"时\"mm\"分\"", "上午/下午h\"时\"mm\"分\"ss\"秒\"", "yyyy\"年\"m\"月\"", "m\"月\"d\"日\"");

    private static readonly Dictionary<int, string> TraditionalChinese = EastAsian(
        "[$-404]e/m/d", "[$-404]e\"年\"m\"月\"d\"日\"", "[$-404]e\"年\"m\"月\"d\"日\"", "m/d/yy", "yyyy\"年\"m\"月\"d\"日\"",
        "hh\"時\"mm\"分\"", "hh\"時\"mm\"分\"ss\"秒\"", "上午/下午hh\"時\"mm\"分\"", "上午/下午hh\"時\"mm\"分\"ss\"秒\"", "[$-404]e/m/d",
        "[$-404]e/m/d", "[$-404]e\"年\"m\"月\"d\"日\"", "上午/下午hh\"時\"mm\"分\"", "上午/下午hh\"時\"mm\"分\"ss\"秒\"", "[$-404]e\"年\"m\"月\"d\"日\"",
        "上午/下午hh\"時\"mm\"分\"", "上午/下午hh\"時\"mm\"分\"ss\"秒\"", "[$-404]e/m/d", "[$-404]e\"年\"m\"月\"d\"日\"");

    private static readonly Dictionary<int, string> Japanese = EastAsian(
        "[$-411]ge.m.d", "[$-411]ggge\"年\"m\"月\"d\"日\"", "[$-411]ggge\"年\"m\"月\"d\"日\"", "m/d/yy", "yyyy\"年\"m\"月\"d\"日\"",
        "h\"時\"mm\"分\"", "h\"時\"mm\"分\"ss\"秒\"", "yyyy\"年\"m\"月\"", "m\"月\"d\"日\"", "[$-411]ge.m.d",
        "[$-411]ge.m.d", "[$-411]ggge\"年\"m\"月\"d\"日\"", "yyyy\"年\"m\"月\"", "m\"月\"d\"日\"", "[$-411]ggge\"年\"m\"月\"d\"日\"",
        "yyyy\"年\"m\"月\"", "m\"月\"d\"日\"", "[$-411]ge.m.d", "[$-411]ggge\"年\"m\"月\"d\"日\"");

    private static readonly Dictionary<int, string> Korean = EastAsian(
        "yyyy\"年\" mm\"月\" dd\"日\"", "mm-dd", "mm-dd", "mm-dd-yy", "yyyy\"년\" mm\"월\" dd\"일\"",
        "h\"시\" mm\"분\"", "h\"시\" mm\"분\" ss\"초\"", "yyyy-mm-dd", "yyyy-mm-dd", "yyyy\"年\" mm\"月\" dd\"日\"",
        "yyyy\"年\" mm\"月\" dd\"日\"", "mm-dd", "yyyy-mm-dd", "yyyy-mm-dd", "mm-dd",
        "yyyy-mm-dd", "yyyy-mm-dd", "yyyy\"年\" mm\"月\" dd\"日\"", "mm-dd");

    /// <summary>Japanese era initials Excel writes for g (Meiji to Reiwa, as JapaneseCalendar numbers them).</summary>
    private static readonly string[] JapaneseEraLetters = ["M", "T", "S", "H", "R"];

    private static readonly Lazy<DateTimeFormatInfo> JapaneseEras = new(() =>
    {
        var format = (DateTimeFormatInfo)CultureInfo.GetCultureInfo("ja-JP").DateTimeFormat.Clone();
        format.Calendar = new JapaneseCalendar();
        return format;
    });

    private readonly CultureInfo _culture = culture ?? CultureInfo.CurrentCulture;
    private readonly Dictionary<string, NumberFormat> _formats = [];

    /// <summary>
    /// The East Asian locale the workbook was written in (from its fonts' character sets), or null. Used when the
    /// system culture can't say how to show a locale-dependent format.
    /// </summary>
    public CultureInfo? WorkbookCulture { get; set; }

    /// <summary>True for a missing format or "General" (any case).</summary>
    public static bool IsGeneral(string? format) =>
        string.IsNullOrEmpty(format) || format.Equals("General", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The code of built-in format <paramref name="id"/>, or null when it isn't one. The East Asian ids follow the
    /// system culture when it is Chinese, Japanese or Korean, then the workbook's, then Simplified Chinese.
    /// </summary>
    public string? BuiltIn(int id) =>
        BuiltInFormats.TryGetValue(id, out var code) ? code
        : id is >= 27 and <= 36 or >= 50 and <= 58 ? (EastAsianTable(_culture) ?? EastAsianTable(WorkbookCulture) ?? SimplifiedChinese)[id]
        : null;

    /// <summary>The East Asian locale a font character set (ECMA-376 18.4.1) is written for, or null.</summary>
    public static CultureInfo? CultureOfCharset(int charset) => charset switch
    {
        128 => CultureInfo.GetCultureInfo("ja-JP"), // SHIFTJIS_CHARSET
        129 or 130 => CultureInfo.GetCultureInfo("ko-KR"), // HANGUL_CHARSET, JOHAB_CHARSET
        134 => CultureInfo.GetCultureInfo("zh-CN"), // GB2312_CHARSET
        136 => CultureInfo.GetCultureInfo("zh-TW"), // CHINESEBIG5_CHARSET
        _ => null,
    };

    /// <summary>The text Excel shows for <paramref name="value"/> under <paramref name="format"/>.</summary>
    public string Format(double value, string? format)
    {
        if (IsGeneral(format))
            return General(value);
        if (SystemDateTime(value, format!) is { } system)
            return system;
        var code = EastAsianTokens(format!, ref value);
        if (Parse(code) is not { } parsed)
            return General(value);
        try
        {
            return parsed.Format(value, CultureInfo.InvariantCulture, date1904);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return General(value);
        }
    }

    /// <summary>True when <paramref name="format"/> shows a date or time of day (not a duration like [h]:mm).</summary>
    public bool IsDate(string? format)
    {
        if (IsGeneral(format))
            return false;
        var sample = 1.5;
        return Parse(EastAsianTokens(format!, ref sample)) is { IsDateTimeFormat: true, IsTimeSpanFormat: false };
    }

    private NumberFormat? Parse(string format)
    {
        if (!_formats.TryGetValue(format, out var parsed))
            _formats[format] = parsed = new NumberFormat(format);
        return parsed.IsValid ? parsed : null;
    }

    // ───────────────────────── System date and time ─────────────────────────

    /// <summary>
    /// [$-F800] (or [$-x-sysdate]) is Windows' long date and [$-F400] ([$-x-systime]) its long time: Excel ignores
    /// the pattern after it, which is only a fallback for other programs. Null for other formats, and when no culture
    /// knows the pattern (invariant system, no workbook hint): then the stored fallback is used.
    /// </summary>
    private string? SystemDateTime(double value, string format)
    {
        var date = format.StartsWith("[$-F800]", StringComparison.OrdinalIgnoreCase) || format.StartsWith("[$-x-sysdate]", StringComparison.OrdinalIgnoreCase);
        if (!date && !format.StartsWith("[$-F400]", StringComparison.OrdinalIgnoreCase) && !format.StartsWith("[$-x-systime]", StringComparison.OrdinalIgnoreCase))
            return null;
        var culture = _culture.Name.Length > 0 ? _culture : WorkbookCulture;
        if (culture is null || ToDateTime(value) is not { } when)
            return null;
        var info = culture.DateTimeFormat;
        try
        {
            return when.ToString(date ? info.LongDatePattern : info.LongTimePattern, info);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // outside the culture's calendar
        }
    }

    /// <summary>The date and time of a serial, rounded to the second as Excel shows it; null outside 1900–9999.</summary>
    private DateTime? ToDateTime(double value)
    {
        if (double.IsNaN(value) || value < 0 || value >= (date1904 ? 2957004 : 2958466))
            return null;
        var date = date1904 ? new DateTime(1904, 1, 1).AddDays(value) : FromSerial(value);
        return new DateTime((date.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
    }

    // ───────────────────────── East Asian tokens ─────────────────────────

    /// <summary>A table from the codes of ids 27–36 and 50–58, in that order.</summary>
    private static Dictionary<int, string> EastAsian(params string[] codes) =>
        Enumerable.Range(27, 10).Concat(Enumerable.Range(50, 9)).Zip(codes).ToDictionary(p => p.First, p => p.Second);

    private static Dictionary<int, string>? EastAsianTable(CultureInfo? culture)
    {
        if (culture is null)
            return null;
        return culture.TwoLetterISOLanguageName switch
        {
            "ja" => Japanese,
            "ko" => Korean,
            "zh" => IsTraditionalChinese(culture) ? TraditionalChinese : SimplifiedChinese,
            _ => null,
        };
    }

    /// <summary>zh-TW, zh-HK, zh-MO and zh-Hant-*.</summary>
    private static bool IsTraditionalChinese(CultureInfo culture)
    {
        for (var c = culture; c.Name.Length > 0; c = c.Parent)
        {
            if (c.Name is "zh-Hant" or "zh-TW" or "zh-HK" or "zh-MO" or "zh-CHT")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Rewrites what ExcelNumberFormat can't read into literals for this value: era names (g, gg, ggg) and years in
    /// the era (e, ee) of the Japanese calendar under [$-411] and the Republic of China calendar under [$-404], and
    /// the Chinese 上午/下午 marker, which makes the hours 12-hour (the value moves by 12 hours within its day, so the
    /// date and minutes stay). Number formats and codes without these tokens come back unchanged.
    /// </summary>
    private string EastAsianTokens(string format, ref double value)
    {
        const string Marker = "上午/下午";
        var hasMarker = format.Contains(Marker, StringComparison.Ordinal);
        if (!hasMarker && format.IndexOfAny(['e', 'g']) < 0)
            return format;
        if (format.Contains("general", StringComparison.OrdinalIgnoreCase) || ToDateTime(value) is not { } when)
            return format;

        var calendar = EraCalendar(format);
        var sb = new StringBuilder(format.Length + 8);
        var changed = false;
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            switch (ch)
            {
                case '"':
                    var end = format.IndexOf('"', i + 1);
                    end = end < 0 ? format.Length - 1 : end;
                    sb.Append(format, i, end - i + 1);
                    i = end;
                    continue;
                case '[':
                    var close = format.IndexOf(']', i + 1);
                    close = close < 0 ? format.Length - 1 : close;
                    sb.Append(format, i, close - i + 1);
                    i = close;
                    continue;
                case '\\' or '_' or '*':
                    sb.Append(format, i, Math.Min(2, format.Length - i));
                    i++;
                    continue;
                case '0' or '#' or '?':
                    return format; // a number format: e is its exponent
                case 'e' or 'g':
                    var run = 1;
                    while (i + run < format.Length && format[i + run] == ch)
                        run++;
                    sb.Append('"').Append(ch == 'e' ? EraYear(when, calendar, run) : EraName(when, calendar, run)).Append('"');
                    i += run - 1;
                    changed = true;
                    continue;
            }
            if (hasMarker && string.CompareOrdinal(format, i, Marker, 0, Marker.Length) == 0)
            {
                sb.Append('"').Append(when.Hour < 12 ? "上午" : "下午").Append('"');
                i += Marker.Length - 1;
                changed = true;
                continue;
            }
            sb.Append(ch);
        }
        if (!changed)
            return format;
        if (hasMarker)
            value += when.Hour == 0 ? 0.5 : when.Hour > 12 ? -0.5 : 0;
        return sb.ToString();
    }

    private enum Era
    {
        Gregorian,
        Japanese,
        Taiwan,
    }

    /// <summary>The calendar g and e count in: from the code's locale ([$-411], [$-404]), else the system's.</summary>
    private Era EraCalendar(string format)
    {
        var start = format.IndexOf("[$", StringComparison.Ordinal);
        var dash = start < 0 ? -1 : format.IndexOf('-', start);
        var close = start < 0 ? -1 : format.IndexOf(']', start);
        if (dash > start && close > dash
            && int.TryParse(format.AsSpan(dash + 1, close - dash - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lcid))
        {
            return (lcid & 0xFFFF) switch
            {
                0x411 => Era.Japanese,
                0x404 => Era.Taiwan,
                _ => Era.Gregorian,
            };
        }
        var table = EastAsianTable(_culture) ?? EastAsianTable(WorkbookCulture);
        return table == Japanese ? Era.Japanese : table == TraditionalChinese ? Era.Taiwan : Era.Gregorian;
    }

    private static string EraYear(DateTime when, Era calendar, int length)
    {
        var year = calendar switch
        {
            Era.Japanese when when >= JapaneseEras.Value.Calendar.MinSupportedDateTime => JapaneseEras.Value.Calendar.GetYear(when),
            Era.Taiwan when when.Year >= 1912 => when.Year - 1911,
            _ => when.Year,
        };
        return year.ToString(length > 1 ? "00" : "0", CultureInfo.InvariantCulture);
    }

    /// <summary>g: initial (R), gg: first character (令), ggg: full name (令和); 民國 / 中華民國 for Taiwan.</summary>
    private static string EraName(DateTime when, Era calendar, int length)
    {
        switch (calendar)
        {
            case Era.Japanese when when >= JapaneseEras.Value.Calendar.MinSupportedDateTime:
                var era = JapaneseEras.Value.Calendar.GetEra(when);
                var name = JapaneseEras.Value.GetEraName(era);
                return length switch
                {
                    1 when era >= 1 && era <= JapaneseEraLetters.Length => JapaneseEraLetters[era - 1],
                    1 or 2 => name[..Math.Min(1, name.Length)],
                    _ => name,
                };
            case Era.Taiwan when when.Year >= 1912:
                return length > 2 ? "中華民國" : "民國";
            default:
                return "";
        }
    }

    // ───────────────────────── General and serials ─────────────────────────

    /// <summary>Excel's General format: up to 11 significant characters, no trailing zeros.</summary>
    public static string General(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e11)
            return value.ToString("0", CultureInfo.InvariantCulture);
        var text = value.ToString("G10", CultureInfo.InvariantCulture);
        return text.Contains('E') ? value.ToString("0.#####E+00", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>
    /// A date as Excel serial days. Excel counts the non-existent 29 February 1900, so its serials before
    /// 1 March 1900 are one lower than OLE Automation dates.
    /// </summary>
    public static double ToSerial(DateTime date)
    {
        var oa = date.ToOADate();
        return oa is >= 1 and < 61 ? oa - 1 : oa;
    }

    /// <summary>The date of an Excel serial (the inverse of <see cref="ToSerial"/>).</summary>
    public static DateTime FromSerial(double serial) =>
        DateTime.FromOADate(serial is >= 0 and < 60 ? serial + 1 : serial);
}
