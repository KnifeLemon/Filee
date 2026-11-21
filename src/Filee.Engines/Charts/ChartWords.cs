// Words a chart shows that its file doesn't hold. A waterfall chart's part names none of its legend's three entries,
// and Excel shows "Increase", "Decrease" and "Total" in its own display language, whatever the workbook's language;
// a chart template (.crtx) without values gets Office's sample names, "Category 1" … and "Series 1" …, which Office
// also writes in its display language. Filee shows them in the app's language (CultureInfo.CurrentUICulture, which
// the app sets), from these tables of the languages the app ships, English for any other. The engine has no string
// resources of its own, so the tables live here; the words are Filee's translations, not checked against Excel's
// localized builds.

using System.Globalization;

namespace Filee.Engines.Charts;

internal static class ChartWords
{
    /// <summary>A waterfall's legend entries (increase, decrease, total) by the app's language codes.</summary>
    private static readonly Dictionary<string, string[]> Waterfalls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = ["زيادة", "نقصان", "الإجمالي"],
        ["bn-BD"] = ["বৃদ্ধি", "হ্রাস", "মোট"],
        ["de"] = ["Zunahme", "Abnahme", "Summe"],
        ["en"] = ["Increase", "Decrease", "Total"],
        ["es"] = ["Aumento", "Disminución", "Total"],
        ["fa"] = ["افزایش", "کاهش", "مجموع"],
        ["fr"] = ["Augmentation", "Diminution", "Total"],
        ["hi"] = ["वृद्धि", "कमी", "कुल"],
        ["id"] = ["Kenaikan", "Penurunan", "Total"],
        ["it"] = ["Aumento", "Diminuzione", "Totale"],
        ["ja"] = ["増加", "減少", "合計"],
        ["ko"] = ["증가", "감소", "합계"],
        ["mn"] = ["Өсөлт", "Бууралт", "Нийт"],
        ["ne"] = ["वृद्धि", "कमी", "जम्मा"],
        ["pl"] = ["Wzrost", "Spadek", "Suma"],
        ["pt-BR"] = ["Aumento", "Redução", "Total"],
        ["pt-PT"] = ["Aumento", "Diminuição", "Total"],
        ["ru"] = ["Увеличение", "Уменьшение", "Итог"],
        ["th"] = ["เพิ่มขึ้น", "ลดลง", "ผลรวม"],
        ["tr"] = ["Artış", "Azalış", "Toplam"],
        ["vi"] = ["Tăng", "Giảm", "Tổng"],
        ["zh-CN"] = ["增加", "减少", "汇总"],
        ["zh-TW"] = ["增加", "減少", "總計"],
    };

    /// <summary>Office's sample category and series names ({0} = the number) by the app's language codes.</summary>
    private static readonly Dictionary<string, (string Category, string Series)> Samples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = ("الفئة {0}", "سلسلة {0}"),
        ["bn-BD"] = ("বিভাগ {0}", "সিরিজ {0}"),
        ["de"] = ("Kategorie {0}", "Datenreihe {0}"),
        ["en"] = ("Category {0}", "Series {0}"),
        ["es"] = ("Categoría {0}", "Serie {0}"),
        ["fa"] = ("دسته {0}", "سری {0}"),
        ["fr"] = ("Catégorie {0}", "Série {0}"),
        ["hi"] = ("श्रेणी {0}", "शृंखला {0}"),
        ["id"] = ("Kategori {0}", "Seri {0}"),
        ["it"] = ("Categoria {0}", "Serie {0}"),
        ["ja"] = ("分類 {0}", "系列 {0}"),
        ["ko"] = ("항목 {0}", "계열 {0}"),
        ["mn"] = ("Ангилал {0}", "Цуврал {0}"),
        ["ne"] = ("वर्ग {0}", "शृङ्खला {0}"),
        ["pl"] = ("Kategoria {0}", "Seria {0}"),
        ["pt-BR"] = ("Categoria {0}", "Série {0}"),
        ["pt-PT"] = ("Categoria {0}", "Série {0}"),
        ["ru"] = ("Категория {0}", "Ряд {0}"),
        ["th"] = ("ประเภท {0}", "ชุด {0}"),
        ["tr"] = ("Kategori {0}", "Seri {0}"),
        ["vi"] = ("Thể loại {0}", "Chuỗi {0}"),
        ["zh-CN"] = ("类别 {0}", "系列 {0}"),
        ["zh-TW"] = ("類別 {0}", "數列 {0}"),
    };

    /// <summary>Office's name of sample category <paramref name="number"/> (1-based) in <paramref name="culture"/>'s language.</summary>
    public static string SampleCategory(CultureInfo culture, int number) =>
        string.Format(CultureInfo.InvariantCulture, Samples[Language(culture)].Category, number);

    /// <summary>Office's name of sample series <paramref name="number"/> (1-based) in <paramref name="culture"/>'s language.</summary>
    public static string SampleSeries(CultureInfo culture, int number) =>
        string.Format(CultureInfo.InvariantCulture, Samples[Language(culture)].Series, number);

    /// <summary>The legend names of a waterfall's increases, decreases and totals in <paramref name="culture"/>'s language.</summary>
    public static IReadOnlyList<string> Waterfall(CultureInfo culture) => Waterfalls[Language(culture)];

    /// <summary>The app language of <paramref name="culture"/>: its own name, else its script or region's, else its language's; English otherwise.</summary>
    internal static string Language(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            var name = c.Name switch
            {
                "zh-Hant" or "zh-HK" or "zh-MO" => "zh-TW",
                "zh" or "zh-Hans" or "zh-SG" => "zh-CN",
                "pt" => "pt-BR",
                "bn" => "bn-BD",
                var own => own,
            };
            if (Waterfalls.ContainsKey(name))
                return name;
        }
        return "en";
    }
}
