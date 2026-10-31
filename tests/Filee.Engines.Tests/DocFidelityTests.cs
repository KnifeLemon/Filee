// The DOC reader against real Word 97–2003 files: DOCX documents written in code (Filee's rich DOCX and the render
// features written by the DOCX writer) are saved as DOC by LibreOffice (when it is installed; nothing is committed) and
// the DOC must read into the model of its DOCX twin: the same text, paragraph and character formatting, lists and
// tables. LibreOffice's own DOCX of the same DOC is the second twin: it shows what LibreOffice's DOC export keeps, so
// a difference both twins share is LibreOffice's, not the reader's.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Doc;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Tests.RenderQuality;

namespace Filee.Engines.Tests;

public class DocFidelityTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private IConverter? LibreOffice()
    {
        var libreOffice = fx.Converters.Single(c => c.Id == "libreoffice");
        return fx.Catalog.StatusOf(libreOffice).IsAvailable && OperatingSystem.IsWindows() ? libreOffice : null;
    }

    private static async Task<string> ConvertAsync(IConverter libreOffice, string input, string from, string to, string output)
    {
        await HwpxAssert.LibreOfficeGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var written = await libreOffice.ConvertAsync(new ConversionStep(input, from, to, new Preset(), new HwpxAssert.FixedPath(output), Path.GetDirectoryName(output)!),
                null, TestContext.Current.CancellationToken);
            return written.Single();
        }
        finally
        {
            HwpxAssert.LibreOfficeGate.Release();
        }
    }

    /// <summary>The DOCX sources: the rich DOCX and every render feature written as DOCX.</summary>
    /// <param name="keep">Sources already in the folder stay as they are (their DOC was made from them).</param>
    private static List<(string Name, string Docx)> Sources(string folder, bool keep)
    {
        var rich = Path.Combine(folder, "rich.docx");
        var sources = new List<(string, string)> { ("rich", keep && File.Exists(rich) ? rich : DocumentFuzzTests.RichDocx(folder)) };
        foreach (var feature in RenderFeatures.All)
        {
            var docx = Path.Combine(folder, feature.Name + ".docx");
            if (!(keep && File.Exists(docx)))
            {
                var hwpx = feature.Write(Path.Combine(folder, feature.Name));
                DocxWriter.Write(HwpxReader.Read(hwpx, Path.Combine(folder, feature.Name + "-media")), docx);
            }
            sources.Add((feature.Name, docx));
        }
        return sources;
    }

    /// <summary>
    /// FILEE_DOC_FIDELITY names a folder for a machine where LibreOffice is too slow for its time limit: the sources
    /// are written there, and the DOC of each (lo/name.doc) and LibreOffice's DOCX of that (lo-back/name.docx) are
    /// taken from there when they exist, converted beforehand with soffice by hand. FILEE_DOC_DIFF keeps the files and
    /// a dump of each model; FILEE_DOC_REPORT the scores.
    /// </summary>
    [Fact]
    public async Task Doc_files_saved_by_libreoffice_read_like_their_docx()
    {
        var given = Environment.GetEnvironmentVariable("FILEE_DOC_FIDELITY") is { Length: > 0 } f ? f : null;
        Assert.SkipUnless(LibreOffice() is not null || given is not null, "LibreOffice not found");
        var libreOffice = LibreOffice();
        var folder = given ?? fx.NewFolder();
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        var totals = new Score();
        var againstLibreOffice = new Score();
        var diff = Environment.GetEnvironmentVariable("FILEE_DOC_DIFF") is { Length: > 0 } d ? d : null;
        foreach (var (name, docx) in Sources(folder, keep: given is not null))
        {
            if (diff is not null)
            {
                Directory.CreateDirectory(diff);
                File.Copy(docx, Path.Combine(diff, name + ".docx"), true);
            }
            string doc = Path.Combine(folder, "lo", name + ".doc"), back = Path.Combine(folder, "lo-back", name + ".docx");
            try
            {
                if (given is not null && !(File.Exists(doc) && File.Exists(back)))
                {
                    report.AppendLine($"{name}: no DOC in {folder}");
                    continue;
                }
                if (given is null)
                {
                    doc = await ConvertAsync(libreOffice!, docx, "docx", "doc", Path.Combine(folder, name + ".doc"));
                    back = await ConvertAsync(libreOffice!, doc, "doc", "docx", Path.Combine(folder, name + ".lo.docx"));
                }
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                // LibreOffice 26.2 doesn't convert every DOCX (it gives up on some, slowly): nothing to compare.
                report.AppendLine($"{name}: LibreOffice did not convert it ({ex.GetType().Name})");
                continue;
            }
            var twin = DocxReader.Read(docx, Path.Combine(folder, name + "-twin"));
            var read = DocReader.Read(doc, Path.Combine(folder, name + "-doc"));
            var loTwin = DocxReader.Read(back, Path.Combine(folder, name + "-lo"));
            var score = Score.Of(twin, read);
            var lo = Score.Of(loTwin, read);
            totals.Add(score);
            againstLibreOffice.Add(lo);
            report.AppendLine($"{name}: DOCX twin {score} | LibreOffice's DOCX {lo}");
            if (diff is not null)
            {
                File.Copy(doc, Path.Combine(diff, name + ".doc"), true);
                File.WriteAllText(Path.Combine(diff, name + ".twin.txt"), Score.Dump(twin));
                File.WriteAllText(Path.Combine(diff, name + ".lo.txt"), Score.Dump(loTwin));
                File.WriteAllText(Path.Combine(diff, name + ".doc.txt"), Score.Dump(read));
            }
        }
        report.AppendLine($"total: DOCX twin {totals} | LibreOffice's DOCX {againstLibreOffice}");
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("FILEE_DOC_REPORT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
        // Every paragraph's text arrives.
        Assert.Equal(totals.Paragraphs, totals.Text);
    }

    /// <summary>Paragraphs (in tables too) whose text, paragraph format, character formats and list match; tables whose shape does.</summary>
    private sealed class Score
    {
        public int Paragraphs, Text, Format, Characters, ListItems, Lists, Tables, TableShapes;

        public void Add(Score other)
        {
            Paragraphs += other.Paragraphs;
            Text += other.Text;
            Format += other.Format;
            Characters += other.Characters;
            ListItems += other.ListItems;
            Lists += other.Lists;
            Tables += other.Tables;
            TableShapes += other.TableShapes;
        }

        public override string ToString() =>
            $"text {Text}/{Paragraphs}, paragraph format {Format}/{Text}, character format {Characters}/{Text}, lists {Lists}/{ListItems}, tables {TableShapes}/{Tables}";

        public static Score Of(HDocument expected, HDocument actual)
        {
            var score = new Score();
            var a = Items(expected);
            var b = Items(actual);
            // Paragraphs are matched in order by their text, so one extra or missing paragraph doesn't shift the rest.
            var used = 0;
            foreach (var item in a.Where(i => i.Paragraph is not null))
            {
                score.Paragraphs++;
                var index = b.FindIndex(used, x => x.Paragraph is not null && TextOf(x.Paragraph) == TextOf(item.Paragraph!));
                if (index < 0)
                    continue;
                used = index + 1;
                score.Text++;
                var other = b[index].Paragraph!;
                var p = item.Paragraph!;
                if (p.Format.Equals(other.Format) && p.HeadingLevel == other.HeadingLevel)
                    score.Format++;
                if (CharacterFormats(p) == CharacterFormats(other))
                    score.Characters++;
                if (p.List is not null || other.List is not null)
                {
                    score.ListItems++;
                    if (ListOf(p) == ListOf(other))
                        score.Lists++;
                }
            }
            var tablesA = a.Where(i => i.Table is not null).Select(i => Shape(i.Table!)).ToList();
            var tablesB = b.Where(i => i.Table is not null).Select(i => Shape(i.Table!)).ToList();
            score.Tables = tablesA.Count;
            score.TableShapes = tablesA.Where((shape, i) => i < tablesB.Count && tablesB[i] == shape).Count();
            return score;
        }

        private sealed record Item(HParagraph? Paragraph, HTable? Table);

        /// <summary>Paragraphs and tables in reading order, cells and text boxes included (headers and notes are not).</summary>
        private static List<Item> Items(HDocument document)
        {
            var items = new List<Item>();
            void Walk(IEnumerable<HBlock> blocks)
            {
                foreach (var block in blocks)
                {
                    switch (block)
                    {
                        case HParagraph p:
                            items.Add(new Item(p, null));
                            foreach (var box in p.Inlines.OfType<HTextBox>())
                                Walk(box.Blocks);
                            break;
                        case HTable t:
                            items.Add(new Item(null, t));
                            foreach (var cell in t.Rows.SelectMany(r => r.Cells))
                                Walk(cell.Blocks);
                            break;
                    }
                }
            }
            foreach (var section in document.Sections)
                Walk(section.Blocks);
            return items;
        }

        private static string TextOf(HParagraph paragraph) => HDocumentWalker.Text(paragraph.Inlines).Trim();

        /// <summary>The format of the paragraph's text, runs of one format joined (writers split runs differently).</summary>
        private static string CharacterFormats(HParagraph paragraph)
        {
            var runs = new List<(string Text, HCharFormat Format)>();
            foreach (var text in HDocumentWalker.InlinesOf(paragraph.Inlines).OfType<HText>())
            {
                if (runs.Count > 0 && (runs[^1].Format.Equals(text.Format) || text.Text.Trim().Length == 0))
                    runs[^1] = (runs[^1].Text + text.Text, runs[^1].Format);
                else
                    runs.Add((text.Text, text.Format));
            }
            return string.Join("|", runs.Where(r => r.Text.Trim().Length > 0).Select(r => $"{r.Text.Trim()}:{r.Format}"));
        }

        private static string ListOf(HParagraph paragraph) => paragraph.List is not { } list ? "none"
            : $"{list.Level}/{list.Numbered}/" + (list.Level < list.Numbering.Levels.Count
                ? list.Numbering.Levels[list.Level] is var level ? $"{level.Format}:{level.Text}:{level.Start}:{level.Bullet}" : ""
                : "");

        private static string Shape(HTable table) =>
            string.Join(";", table.Rows.Select(r => string.Join(",", r.Cells.Select(c => $"{c.RowSpan}x{c.ColSpan}"))));

        public static string Dump(HDocument document) => HDocumentAssert.Dump(document, layout: false);
    }
}
