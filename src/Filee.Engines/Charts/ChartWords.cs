// Words a chart shows that its file doesn't hold. A waterfall chart's part names none of its legend's three entries,
// and Excel shows "Increase", "Decrease" and "Total" in its own display language, whatever the workbook's language;
// Filee shows them in the app's language (CultureInfo.CurrentUICulture, which the app sets),
// from this table of the languages the app ships, English for any other. The engine has no string resources of its
// own, so the table lives here; the words are Filee's translations, not checked against Excel's localized builds.

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
