// Words the printer prints for notes that the workbook doesn't hold: the list of notes at the end of a sheet names
// each note's cell and text ("Cell: B2", "Comment: ..."), and a threaded comment's replies follow it ("Reply: ...").
// Excel prints these in its own display language, whatever the workbook's; Filee prints them in the app's language
// (CultureInfo.CurrentUICulture, which the app sets; ChartWords.Language picks the app language), English for any
// other. The words are Office's own terms for a cell and a comment where they are known (Korean 메모, Russian
// Примечание, Turkish Açıklama, Chinese 批注 / 註解), otherwise plain translations; none is checked against a printout
// of Excel's localized builds.

using System.Globalization;
using Filee.Engines.Charts;

namespace Filee.Engines.Office.Sheets;

internal static class SheetWords
{
    /// <summary>The words before a note's cell, before its text, and before a reply, by the app's language codes.</summary>
    private static readonly Dictionary<string, (string Cell, string Comment, string Reply)> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = ("الخلية:", "تعليق:", "رد:"),
        ["bn-BD"] = ("ঘর:", "মন্তব্য:", "উত্তর:"),
        ["de"] = ("Zelle:", "Kommentar:", "Antwort:"),
        ["en"] = ("Cell:", "Comment:", "Reply:"),
        ["es"] = ("Celda:", "Comentario:", "Respuesta:"),
        ["fa"] = ("سلول:", "نظر:", "پاسخ:"),
        ["fr"] = ("Cellule\u00A0:", "Commentaire\u00A0:", "Réponse\u00A0:"),
        ["hi"] = ("कक्ष:", "टिप्पणी:", "उत्तर:"),
        ["id"] = ("Sel:", "Komentar:", "Balasan:"),
        ["it"] = ("Cella:", "Commento:", "Risposta:"),
        ["ja"] = ("セル:", "コメント:", "返信:"),
        ["ko"] = ("셀:", "메모:", "회신:"),
        ["mn"] = ("Нүд:", "Тайлбар:", "Хариу:"),
        ["ne"] = ("कक्ष:", "टिप्पणी:", "जवाफ:"),
        ["pl"] = ("Komórka:", "Komentarz:", "Odpowiedź:"),
        ["pt-BR"] = ("Célula:", "Comentário:", "Resposta:"),
        ["pt-PT"] = ("Célula:", "Comentário:", "Resposta:"),
        ["ru"] = ("Ячейка:", "Примечание:", "Ответ:"),
        ["th"] = ("เซลล์:", "ข้อคิดเห็น:", "ตอบกลับ:"),
        ["tr"] = ("Hücre:", "Açıklama:", "Yanıt:"),
        ["vi"] = ("Ô:", "Chú thích:", "Trả lời:"),
        ["zh-CN"] = ("单元格:", "批注:", "回复:"),
        ["zh-TW"] = ("儲存格:", "註解:", "回覆:"),
    };

    /// <summary>The note words in <paramref name="culture"/>'s app language.</summary>
    public static (string Cell, string Comment, string Reply) Of(CultureInfo culture) =>
        Notes.TryGetValue(ChartWords.Language(culture), out var words) ? words : Notes["en"];
}
