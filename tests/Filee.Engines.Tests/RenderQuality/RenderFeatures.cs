// The feature matrix of the render quality harness: small documents written in code at test time, one per feature,
// so no input or reference file is kept in the repository. Most are built as a document model (HDocument) and written
// by Filee's HWPX writer; floating objects and equations go through DocxBuilder and the DOCX reader, the way a Word
// document reaches the renderer. Some are also made binary HWP files with `rhwp convert` (DocumentRenderers) while
// rhwp is bundled.
//
// Each document stays under a page or two and a few hundred characters: rhwp needs some 3.5 ms per character, and the
// whole matrix has to fit in a minute of the Windows CI job. FILEE_RENDER_FULL=1 adds the longer variants.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests.RenderQuality;

/// <summary>One document of the feature matrix.</summary>
/// <param name="Name">Feature name, the key of its thresholds.</param>
/// <param name="Write">Writes the document as HWPX into the given folder and returns its path.</param>
/// <param name="Text">Text the PDF must hold, in reading order (white space doesn't count).</param>
/// <param name="Pages">Pages the document takes; null where only a compare can tell.</param>
/// <param name="Pitch">Baseline-to-baseline distance of every line in points, for documents with one line height.</param>
/// <param name="Hwp">Also rendered from a binary HWP made by <c>rhwp convert</c>.</param>
internal sealed record RenderFeature(string Name, Func<string, string> Write, string Text, int? Pages = 1, double? Pitch = null, bool Hwp = false)
{
    public override string ToString() => Name;
}

internal static class RenderFeatures
{
    /// <summary>A4 with 한글's default margins (30 mm left and right, 20 mm top, 15 mm bottom, 15 mm header and footer).</summary>
    public static readonly HPage Page = new(59528, 84186, 8504, 8504, 5669, 4252, 4252, 4252, 0);

    private const string Korean = "한글 문서는 줄 간격과 정렬에 따라 줄이 나뉘는 자리가 달라집니다. 같은 문단을 몇 번 그려도 줄은 같은 자리에서 끝나야 합니다.";
    private const string Latin = "Filee lays out every paragraph the way the document asks: line spacing, alignment and the place where each line ends.";

    /// <summary>Paragraphs in the scripts Filee's renderer must shape: name, text, font and right-to-left.</summary>
    public static readonly (string Name, string Text, string? Font, bool RightToLeft)[] Scripts =
    [
        ("ko", "한국어 문단입니다. 어절 단위로 줄을 나누고 문장 부호는 줄 머리에 오지 않습니다. “따옴표”와 ‘작은따옴표’, 「낫표」와 (괄호) 속 PDF 2.0도 겹치지 않습니다. 다람쥐 헌 쳇바퀴에 타고파.", null, false),
        ("zh", "文件转换工具应当保持段落的排版和换行位置。汉字之间可以在任意位置断行，但标点符号不能出现在行首。“引号”和‘单引号’、《书名号》、（括号）里的 PDF 2.0 都不会盖住文字……", "Microsoft YaHei", false),
        ("ja", "文書の変換では、段落の行送りと改行の位置を保つことが大切です。句読点は行頭に来ないように調整されます。「かぎ括弧」と『二重かぎ括弧』、（括弧）の中の PDF 2.0 も重なりません。", "Yu Gothic", false),
        ("ar", "يحافظ برنامج التحويل على تنسيق الفقرات ومواضع فواصل الأسطر. يجب أن تتصل الحروف العربية وأن يقرأ النص من اليمين إلى اليسار.", "Segoe UI", true),
        ("he", "תוכנת ההמרה שומרת על עיצוב הפסקאות ועל מקומות שבירת השורות. הטקסט העברי נקרא מימין לשמאל.", "Segoe UI", true),
        ("hi", "रूपांतरण उपकरण अनुच्छेदों के स्वरूप और पंक्ति विराम की जगह को बनाए रखता है। संयुक्ताक्षर जैसे क्ष, त्र और ज्ञ सही दिखने चाहिए।", "Nirmala UI", false),
        ("gu", "રૂપાંતર સાધન ફકરાઓનું સ્વરૂપ અને લીટી તૂટવાની જગ્યા જાળવે છે. જોડાક્ષરો જેમ કે ક્ષ, જ્ઞ અને શ્ર યોગ્ય રીતે દેખાવા જોઈએ.", "Nirmala UI", false),
        ("th", "โปรแกรมแปลงไฟล์ต้องรักษารูปแบบของย่อหน้าและตำแหน่งการตัดบรรทัด ภาษาไทยไม่เว้นวรรคระหว่างคำ จึงต้องตัดคำตามพจนานุกรม", "Leelawadee UI", false),
        ("lo", "ໂປຣແກຣມແປງໄຟລ໌ຕ້ອງຮັກສາຮູບແບບຂອງວັກ ແລະ ຕຳແໜ່ງການຕັດແຖວ ພາສາລາວບໍ່ມີການເວັ້ນວ່າງລະຫວ່າງຄຳ", "Leelawadee UI", false),
        ("km", "កម្មវិធីបម្លែងឯកសារត្រូវរក្សាទម្រង់កថាខណ្ឌ និងទីតាំងនៃការបំបែកបន្ទាត់ ភាសាខ្មែរមិនដកឃ្លារវាងពាក្យទេ", "Khmer UI", false),
        ("my", "ဖိုင်ပြောင်းသည့်ပရိုဂရမ်သည် စာပိုဒ်ပုံစံနှင့် စာကြောင်းခွဲသည့်နေရာကို ထိန်းသိမ်းရမည်။ မြန်မာဘာသာစကားသည် စကားလုံးများကြား နေရာမခြားပါ။", "Myanmar Text", false),
        // Right-to-left text with Latin, numbers and brackets in it (UAX #9: weak types, brackets, numbers).
        ("bidi", "גרסה 2.1 של Filee (בטא) ממירה קבצי PDF ו-DOCX, ושומרת על מספרים כמו 1,234.5% ועל [סוגריים]. الإصدار 2.1 من Filee يحفظ الأرقام ١٢٣ و(الأقواس).", "Segoe UI", true),
    ];

    private static readonly HAlign[] Alignments = [HAlign.Left, HAlign.Right, HAlign.Center, HAlign.Justify, HAlign.Distribute];

    /// <summary>The matrix the CI runs.</summary>
    public static IReadOnlyList<RenderFeature> All { get; } = Build(full: false);

    /// <summary>The longer run: the script paragraphs twelve times, a document of some pages, and every feature also as HWP.</summary>
    public static IReadOnlyList<RenderFeature> Full { get; } = Build(full: true);

    /// <summary><see cref="Full"/> with FILEE_RENDER_FULL=1, else <see cref="All"/>.</summary>
    public static IReadOnlyList<RenderFeature> Selected =>
        Environment.GetEnvironmentVariable("FILEE_RENDER_FULL") is "1" or "true" ? Full : All;

    private static List<RenderFeature> Build(bool full)
    {
        var features = new List<RenderFeature>
        {
            LineSpacing("line-spacing-percent", new HLineSpacing(HLineSpacingKind.Percent, 160), 16, hwp: true),
            LineSpacing("line-spacing-fixed", new HLineSpacing(HLineSpacingKind.Fixed, 2000), 20),
            LineSpacing("line-spacing-at-least", new HLineSpacing(HLineSpacingKind.AtLeast, 2400), 24),
            Tabs(),
            Lists(),
            Tables(),
            HeadersAndFooters(),
            Notes(),
            Columns(),
            FloatingObjects(),
            Equations(),
        };
        features.AddRange(Scripts.Select(s => Script(s.Name, s.Text, s.Font, s.RightToLeft, repeat: full ? 12 : 2, hwp: s.Name == "ko")));
        if (!full)
            return features;
        features.Add(LongDocument());
        return [.. features.Select(f => f with { Hwp = true })];
    }

    // ───────────────────────── Features ─────────────────────────

    /// <summary>One paragraph per alignment, all with one line spacing and size, so every line is one pitch apart.</summary>
    private static RenderFeature LineSpacing(string name, HLineSpacing spacing, double pitch, bool hwp = false)
    {
        var text = Alignments.Select(align => $"{align}: {Korean} {Latin}").ToList();
        return new RenderFeature(name, folder => Model(folder, name, section =>
        {
            for (var i = 0; i < Alignments.Length; i++)
                section.Blocks.Add(Paragraph(text[i], new HParaFormat(Align: Alignments[i], Before: 0, After: 0, LineSpacing: spacing)));
        }), string.Concat(text), Pitch: pitch, Hwp: hwp);
    }

    private static RenderFeature Tabs()
    {
        HTabStop[] stops = [new(12000, HTabKind.Left, "NONE"), new(24000, HTabKind.Center, "DOT"), new(36000, HTabKind.Decimal, "DASH"), new(42520, HTabKind.Right, "DOT")];
        string[][] rows = [["품목", "분류", "123.45", "끝"], ["Item", "Group", "7.5", "End"], ["항목 이름이 긴 줄", "가운데", "1,234.5", "오른쪽"]];
        return new RenderFeature("tabs", folder => Model(folder, "tabs", section =>
        {
            foreach (var row in rows)
            {
                var paragraph = new HParagraph { Format = new HParaFormat(Tabs: stops) };
                for (var i = 0; i < row.Length; i++)
                {
                    paragraph.Inlines.Add(new HTab(default));
                    paragraph.Inlines.Add(new HText(row[i], default));
                }
                section.Blocks.Add(paragraph);
            }
        }), string.Concat(rows.SelectMany(r => r)));
    }

    private static RenderFeature Lists()
    {
        var numbers = new HNumbering();
        numbers.Levels.Add(new HNumberingLevel("DIGIT", "^1.", 1, false));
        numbers.Levels.Add(new HNumberingLevel("HANGUL_SYLLABLE", "^2)", 1, false));
        var bullets = new HNumbering();
        bullets.Levels.Add(new HNumberingLevel("DIGIT", "•", 1, true));
        (string Text, HNumbering List, int Level)[] items =
        [
            ("첫째 항목: 번호가 붙은 목록의 줄은 번호 뒤에서 시작합니다.", numbers, 0),
            ("하위 항목은 한 단계 들여 씁니다.", numbers, 1),
            ("Second item with a hanging indent that wraps onto a second line so the indent of the wrapped line shows.", numbers, 0),
            ("글머리표 항목", bullets, 0),
            ("Bulleted item", bullets, 0),
        ];
        return new RenderFeature("lists", folder => Model(folder, "lists", section =>
        {
            foreach (var (text, list, level) in items)
            {
                var paragraph = Paragraph(text, new HParaFormat(Left: 2000 * (level + 1), FirstLine: -2000));
                paragraph.List = new HListRef(list, level, true);
                section.Blocks.Add(paragraph);
            }
        }), string.Concat(items.Select(i => i.Text)));
    }

    private static RenderFeature Tables()
    {
        string[][] cells = [["구분", "내용", "비고"], ["가", "표 안의 글이 칸 너비를 넘으면 칸 안에서 줄을 바꿉니다.", "1"], ["나", "Cell text wraps inside its column.", "2"]];
        return new RenderFeature("tables", folder => Model(folder, "tables", section =>
        {
            section.Blocks.Add(Paragraph("표 앞 문단"));
            var table = new HTable { ColumnCount = 3, ColumnWidths = [6000, 26520, 10000], Borders = HBorders.Grid };
            for (var r = 0; r < cells.Length; r++)
            {
                var row = new HRow { Header = r == 0 };
                foreach (var text in cells[r])
                {
                    var cell = new HCell { Fill = r == 0 ? "#D9E2F3" : null, VerticalAlign = HVerticalAlign.Center };
                    cell.Blocks.Add(Paragraph(text, new HParaFormat(Align: r == 0 ? HAlign.Center : HAlign.Left)));
                    row.Cells.Add(cell);
                }
                table.Rows.Add(row);
            }
            var merged = new HRow();
            var wide = new HCell { ColSpan = 2 };
            wide.Blocks.Add(Paragraph("병합된 칸"));
            var last = new HCell();
            last.Blocks.Add(Paragraph("끝"));
            merged.Cells.Add(wide);
            merged.Cells.Add(last);
            table.Rows.Add(merged);
            section.Blocks.Add(table);
            section.Blocks.Add(Paragraph("표 뒤 문단"));
        }), "표 앞 문단" + string.Concat(cells.SelectMany(r => r)) + "병합된 칸끝표 뒤 문단", Hwp: true);
    }

    private static RenderFeature HeadersAndFooters()
    {
        string[] body = ["첫 쪽 본문입니다. 머리말과 꼬리말은 본문 위와 아래의 여백 안에 그려집니다.", "둘째 쪽 본문입니다."];
        return new RenderFeature("headers-footers", folder => Model(folder, "headers-footers", section =>
        {
            var header = new HHeaderFooter(HPageType.Both);
            header.Blocks.Add(Paragraph("머리말 Header", new HParaFormat(Align: HAlign.Right)));
            section.Headers.Add(header);
            var footer = new HHeaderFooter(HPageType.Both);
            var number = new HParagraph { Format = new HParaFormat(Align: HAlign.Center) };
            number.Inlines.Add(new HText("- ", default));
            number.Inlines.Add(new HField(HFieldKind.PageNumber, default));
            number.Inlines.Add(new HText(" -", default));
            footer.Blocks.Add(number);
            section.Footers.Add(footer);
            section.Blocks.Add(Paragraph(body[0]));
            var second = Paragraph(body[1]);
            second.PageBreakBefore = true;
            section.Blocks.Add(second);
        }), string.Concat(body), Pages: 2);
    }

    /// <summary>A footnote and an endnote; 한글 starts the endnotes on a page of their own (2 pages).</summary>
    private static RenderFeature Notes()
    {
        return new RenderFeature("notes", folder => Model(folder, "notes", section =>
        {
            var paragraph = Paragraph("각주가 달린 문장입니다");
            var note = new HNote(endnote: false);
            note.Blocks.Add(Paragraph("각주 내용은 쪽 아래에 작은 글자로 놓입니다."));
            paragraph.Inlines.Add(note);
            paragraph.Inlines.Add(new HText(". A sentence with an endnote", default));
            var endnote = new HNote(endnote: true);
            endnote.Blocks.Add(Paragraph("미주 내용"));
            paragraph.Inlines.Add(endnote);
            paragraph.Inlines.Add(new HText(".", default));
            section.Blocks.Add(paragraph);
        }), "각주가 달린 문장입니다. A sentence with an endnote.", Pages: 2);
    }

    private static RenderFeature Columns()
    {
        string[] text = [$"{Korean} {Korean}", $"{Latin} {Latin}"];
        return new RenderFeature("columns", folder => Model(folder, "columns", section =>
        {
            section.Columns = new HColumns(2, 2268, true);
            section.Blocks.Add(Paragraph(text[0], new HParaFormat(Align: HAlign.Justify)));
            var second = Paragraph(text[1], new HParaFormat(Align: HAlign.Justify));
            second.ColumnBreakBefore = true;
            section.Blocks.Add(second);
        }), string.Concat(text));
    }

    /// <summary>Pictures and text boxes floating beside, above, behind and in front of the text (DOCX → HWPX).</summary>
    private static RenderFeature FloatingObjects()
    {
        const string Body = "그림 옆으로 글이 흐릅니다. Text flows around the picture on its right side and continues below it once the picture ends. ";
        return new RenderFeature("floating", folder => Docx(folder, "floating", docx =>
        {
            var picture = docx.Image(Png(160, 120));
            const string Margin = "<wp:positionH relativeFrom=\"margin\"><wp:posOffset>0</wp:posOffset></wp:positionH>";
            const string Paragraph = "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>";
            const string Right = "<wp:positionH relativeFrom=\"margin\"><wp:align>right</wp:align></wp:positionH>";
            string TextBox(string text) =>
                "<wps:wsp><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"2286000\" cy=\"457200\"/></a:xfrm><a:prstGeom prst=\"rect\"/>" +
                "<a:solidFill><a:srgbClr val=\"FFF2CC\"/></a:solidFill><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"C00000\"/></a:solidFill></a:ln></wps:spPr>" +
                $"<wps:txbx><w:txbxContent>{P(R(text))}</w:txbxContent></wps:txbx><wps:bodyPr anchor=\"ctr\"/></wps:wsp>";
            var behind = Anchor(PictureUri, Picture(picture, 1828800, 914400), 1828800, 914400, Right, Paragraph, "<wp:wrapNone/>")
                .Replace("behindDoc=\"0\"", "behindDoc=\"1\"", StringComparison.Ordinal);
            docx.Paragraph(P(Anchor(PictureUri, Picture(picture, 1371600, 1028700), 1371600, 1028700, Margin, Paragraph) + R(Body + Body)))
                .Paragraph(P(Anchor(ShapeUri, TextBox("위아래 배치 글상자"), 2286000, 457200,
                    "<wp:positionH relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionH>", Paragraph, "<wp:wrapTopAndBottom/>") + R("글상자 아래 문단.")))
                .Paragraph(P(behind + R("Text over a picture behind it. 글 뒤에 놓인 그림 위의 글.")))
                .Paragraph(P(Anchor(ShapeUri, TextBox("글 앞 글상자"), 2286000, 457200, Right, Paragraph, "<wp:wrapNone/>") + R("Text under a box in front of it.")));
        }), Body + Body + "글상자 아래 문단.Text over a picture behind it. 글 뒤에 놓인 그림 위의 글.Text under a box in front of it.");
    }

    /// <summary>Inline and display equations (Word math → 한글 equation objects).</summary>
    private static RenderFeature Equations()
    {
        static string Run(string text) => $"<m:r><m:t>{text}</m:t></m:r>";
        var inline = "<m:oMath>" + Run("E=m") + $"<m:sSup><m:e>{Run("c")}</m:e><m:sup>{Run("2")}</m:sup></m:sSup></m:oMath>";
        var fraction = $"<m:oMathPara><m:oMath><m:f><m:num>{Run("a+b")}</m:num><m:den>{Run("2c")}</m:den></m:f></m:oMath></m:oMathPara>";
        var sum = "<m:oMathPara><m:oMath><m:nary><m:naryPr><m:chr m:val=\"∑\"/></m:naryPr>" +
                  $"<m:sub>{Run("i=1")}</m:sub><m:sup>{Run("n")}</m:sup><m:e><m:sSup><m:e>{Run("i")}</m:e><m:sup>{Run("2")}</m:sup></m:sSup></m:e></m:nary></m:oMath></m:oMathPara>";
        return new RenderFeature("equations", folder => Docx(folder, "equations", docx =>
        {
            docx.Paragraph(P(R("Inline ") + inline + R(" in a sentence.")))
                .Paragraph(P(fraction))
                .Paragraph(P(sum))
                .Paragraph(P(R("수식 뒤 문단.")));
        }), "Inline in a sentence.수식 뒤 문단.");
    }

    private static RenderFeature Script(string name, string text, string? font, bool rightToLeft, int repeat, bool hwp)
    {
        var body = string.Join(' ', Enumerable.Repeat(text, repeat));
        var format = new HCharFormat(Size: 1100, Font: font, EastAsianFont: name is "zh" or "ja" ? font : null);
        return new RenderFeature($"script-{name}", folder => Model(folder, $"script-{name}", section =>
        {
            section.Blocks.Add(Paragraph(body, new HParaFormat(Align: rightToLeft ? HAlign.Left : HAlign.Justify, RightToLeft: rightToLeft), format));
            section.Blocks.Add(Paragraph(text, new HParaFormat(RightToLeft: rightToLeft), format));
        }), body + text, Hwp: hwp);
    }

    /// <summary>Some pages of mixed paragraphs (FILEE_RENDER_FULL only).</summary>
    private static RenderFeature LongDocument()
    {
        var text = Enumerable.Range(1, 60).Select(i => $"{i}. {Korean} {Latin}").ToList();
        return new RenderFeature("long", folder => Model(folder, "long", section =>
        {
            for (var i = 0; i < text.Count; i++)
                section.Blocks.Add(Paragraph(text[i], new HParaFormat(Align: Alignments[i % Alignments.Length])));
        }), string.Concat(text), Pages: null);
    }

    // ───────────────────────── Building ─────────────────────────

    private static string Model(string folder, string name, Action<HSection> fill)
    {
        var section = new HSection { Page = Page };
        fill(section);
        var document = new HDocument { Title = name };
        document.Sections.Add(section);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".hwpx");
        HwpxWriter.Write(document, path);
        return path;
    }

    private static string Docx(string folder, string name, Action<DocxBuilder> fill)
    {
        // 한글's margins in Word's terms: Word measures the body from the paper edge, 한글 stacks the header area on top.
        var docx = new DocxBuilder
        {
            SectionProperties = "<w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1984\" w:right=\"1701\" w:bottom=\"1701\" w:left=\"1701\" w:header=\"1134\" w:footer=\"850\" w:gutter=\"0\"/>",
        };
        fill(docx);
        Directory.CreateDirectory(folder);
        var source = docx.Save(Path.Combine(folder, name + ".docx"));
        var path = Path.Combine(folder, name + ".hwpx");
        HwpxWriter.Write(DocxReader.Read(source, Path.Combine(folder, name + "-media")), path);
        return path;
    }

    private static HParagraph Paragraph(string text, HParaFormat format = default, HCharFormat chars = default)
    {
        var paragraph = new HParagraph { Format = format, MarkFormat = chars };
        paragraph.Inlines.Add(new HText(text, chars));
        return paragraph;
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [new SKColor(0x7F, 0x77, 0xDD), new SKColor(0x1D, 0x9E, 0x75)], SKShaderTileMode.Clamp) };
            canvas.DrawRect(0, 0, width, height, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
