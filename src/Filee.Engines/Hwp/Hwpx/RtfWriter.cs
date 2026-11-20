// Writes Rich Text Format from the HDocument model — the counterpart of RtfReader, written by hand from the RTF 1.9
// specification like the DOCX, ODT and HWPX writers, so anything Filee's readers understand becomes an RTF file that
// Word, WordPad and LibreOffice open, without LibreOffice or Pandoc.
//
// Split over partial files:
//  * RtfWriter.cs         the file: its header (fonts with their other names, colours, styles, lists, information,
//                         document settings), sections with their page setup, headers and footers; the output and
//                         how text is escaped;
//  * RtfWriter.Body.cs    paragraphs, character formatting, links, bookmarks, fields, notes and tables;
//  * RtfWriter.Objects.cs pictures (PNG and JPEG as they are, EMF and WMF as metafiles, others as PNG), text boxes
//                         and shapes.
//
// The body is written first, into a temporary file beside the output, because the font, colour and list tables that
// come before it fill as the body uses them. The file is ASCII: other characters are \uN followed by the same
// character in the code page of the font it is in (\ucN says how many bytes that takes), or "?" where that code page
// has none, so programs without Unicode RTF still show what they can.

using System.Globalization;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Converts an <see cref="HDocument"/> into an .rtf file.</summary>
internal sealed partial class RtfWriter
{
    /// <summary>A4 with one-inch margins and headers / footers 1.5 cm from the edge, as the DOCX writer has it.</summary>
    private static readonly HPage DefaultPage = new(59528, 84188, 7200, 7200, 4255, 4255, 2945, 2945, 0);

    /// <summary>The fonts the model leaves open: the DOCX writer's Calibri and 맑은 고딕 (font 0 and 1).</summary>
    private const string DefaultLatinFont = "Calibri";
    private const string DefaultEastAsianFont = "맑은 고딕";

    /// <summary>Body text size (half points): 10 pt, the DOCX writer's Normal.</summary>
    private const int DefaultHalfPoints = 20;

    /// <summary>Word's distance between default tab stops (twips) when the model gives none: 한글's 40 pt.</summary>
    private const int DefaultTabTwips = 800;

    /// <summary>Heading sizes (half points) of "heading 1".."heading 9", as the DOCX writer's.</summary>
    private static readonly int[] HeadingSizes = [32, 28, 26, 24, 22, 20, 20, 20, 20];

    private readonly HDocument _document;
    private readonly DocumentLimits _limits;
    private readonly CancellationToken _token;
    private Output _out = null!;

    private RtfWriter(HDocument document, DocumentLimits limits, CancellationToken token)
    {
        _document = document;
        _limits = limits;
        _token = token;
        // Font 0 and 1 are the defaults, whatever the text uses.
        FontIndex(DefaultLatinFont, 0);
        EastAsianFontIndex(DefaultEastAsianFont, 129);
    }

    /// <summary>Writes <paramref name="document"/> as an .rtf file (replaced atomically, through a temporary file of its own).</summary>
    public static void Write(HDocument document, string outputPath, CancellationToken cancellationToken = default) =>
        Write(document, outputPath, DocumentLimits.Default, cancellationToken);

    /// <summary>Writes within other <paramref name="limits"/> than <see cref="DocumentLimits.Default"/> (for tests).</summary>
    internal static void Write(HDocument document, string outputPath, DocumentLimits limits, CancellationToken cancellationToken = default)
    {
        Nesting.Limit(document); // the writer recurses per table, text box and note level
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var writer = new RtfWriter(document, limits, cancellationToken);

        // The body goes into a file of its own first: the tables before it are only known once it is written.
        using var body = WriterTempFile.Create(outputPath, out var bodyPath);
        try
        {
            writer._out = new Output(body, limits.MaxPackageBytes, cancellationToken);
            writer.Body();
            writer._out.Flush();
            var bodyLength = body.Length;
            WriterTempFile.Write(outputPath, file =>
            {
                var header = new Output(file, limits.MaxPackageBytes - bodyLength, cancellationToken);
                writer._out = header;
                writer.Header();
                header.Flush();
                body.Position = 0;
                body.CopyTo(file);
                file.WriteByte((byte)'}');
            });
        }
        finally
        {
            body.Dispose();
            WriterTempFile.Discard(bodyPath);
        }
    }

    // ───────────────────────── Output ─────────────────────────

    /// <summary>
    /// The RTF text being written: control words, groups and escaped text. It counts what it writes against the limit
    /// a file may have (the RTF reader refuses larger ones) and keeps the \uc value of each open group.
    /// </summary>
    private sealed class Output(Stream stream, long limit, CancellationToken token)
    {
        private readonly StreamWriter _writer = new(stream, Encoding.ASCII, 1 << 16, leaveOpen: true);
        private readonly Stack<int> _ucs = new();
        private long _written;
        private long _checked;

        /// <summary>A control word was written last: text after it needs a space between.</summary>
        private bool _delimit;

        /// <summary>Bytes of fallback after each \uN in the current group.</summary>
        public int Uc { get; private set; } = 1;

        public int Depth => _ucs.Count;

        public void Flush() => _writer.Flush();

        private void Put(string text)
        {
            _written += text.Length;
            if (_written > limit)
                throw DocumentLimits.TooLarge($"The RTF file would be larger than {limit / (1024 * 1024)} MB.");
            if (_written - _checked > 1 << 16)
            {
                _checked = _written;
                token.ThrowIfCancellationRequested();
            }
            _writer.Write(text);
        }

        private void Put(char c)
        {
            if (++_written > limit)
                throw DocumentLimits.TooLarge($"The RTF file would be larger than {limit / (1024 * 1024)} MB.");
            _writer.Write(c);
        }

        public void Open()
        {
            Put('{');
            _ucs.Push(Uc);
            _delimit = false;
        }

        public void Close()
        {
            Put('}');
            if (_ucs.Count > 0)
                Uc = _ucs.Pop();
            _delimit = false;
        }

        /// <summary>One or more control words ("\b\fs20"), or a control symbol.</summary>
        public void Control(string words)
        {
            Put(words);
            _delimit = words.Length > 0 && (char.IsAsciiLetterOrDigit(words[^1]) || words[^1] == '-');
        }

        /// <summary>A byte as \'hh (in \leveltext and \levelnumbers).</summary>
        public void Byte(int value)
        {
            Put($"\\'{value & 0xFF:x2}");
            _delimit = false;
        }

        /// <summary>A line end between pieces of the file (RTF ignores it; it keeps lines short for people reading it).</summary>
        public void Line()
        {
            Put("\r\n");
            _delimit = false;
        }

        /// <summary>ASCII text that needs no escaping (hex digits of a picture).</summary>
        public void Raw(string text)
        {
            if (_delimit)
                Put(' ');
            Put(text);
            _delimit = false;
        }

        /// <summary>
        /// Text with RTF's special characters escaped, tabs and line ends as \tab and \line, and characters beyond ASCII
        /// as \uN with their bytes in <paramref name="codePage"/> (or "?") for programs that don't read \u.
        /// </summary>
        public void Text(string text, int codePage)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '\\' or '{' or '}':
                        Put('\\');
                        Put(c);
                        _delimit = false;
                        continue;
                    case '\t':
                        Control("\\tab");
                        continue;
                    case '\n':
                        Control("\\line");
                        continue;
                    case '\r':
                        if (i + 1 >= text.Length || text[i + 1] != '\n')
                            Control("\\line");
                        continue;
                    case '\u00A0':
                        Control("\\~");
                        continue;
                    case '\u2011':
                        Control("\\_");
                        continue;
                    case '\u00AD':
                        Control("\\-");
                        continue;
                }
                if (c < ' ' || c == '\u007F')
                    continue;
                if (c < '\u0080')
                {
                    if (_delimit)
                        Put(' ');
                    _delimit = false;
                    Put(c);
                    continue;
                }
                var fallback = Fallback(c, codePage);
                if (fallback.Length != Uc)
                {
                    Uc = fallback.Length;
                    Control($"\\uc{Uc}");
                }
                Control($"\\u{(short)c}");
                foreach (var b in fallback)
                {
                    if (b is >= 0x20 and < 0x7F && b is not ((byte)'\\' or (byte)'{' or (byte)'}'))
                    {
                        // After \uN only a digit or a space would be taken as part of the control word.
                        if (_delimit && (char.IsAsciiDigit((char)b) || b == ' '))
                            Put(' ');
                        Put((char)b);
                    }
                    else
                    {
                        Put($"\\'{b:x2}");
                    }
                    _delimit = false;
                }
            }
        }
    }

    private static readonly Dictionary<int, Encoding> Encodings = [];

    /// <summary>The bytes of <paramref name="c"/> in <paramref name="codePage"/>, or "?" when it has none.</summary>
    private static byte[] Fallback(char c, int codePage)
    {
        if (char.IsSurrogate(c))
            return [(byte)'?'];
        Encoding encoding;
        lock (Encodings)
        {
            if (!Encodings.TryGetValue(codePage, out encoding!))
            {
                try
                {
                    encoding = System.Text.Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    encoding = System.Text.Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                }
                Encodings[codePage] = encoding;
            }
        }
        try
        {
            var bytes = encoding.GetBytes([c]);
            // Only a character the code page has itself, not a look-alike, and at most a double-byte one.
            return bytes.Length is 1 or 2 && encoding.GetString(bytes) == c.ToString() ? bytes : [(byte)'?'];
        }
        catch (Exception ex) when (ex is EncoderFallbackException or DecoderFallbackException or ArgumentException)
        {
            return [(byte)'?'];
        }
    }

    // ───────────────────────── Fonts and colours ─────────────────────────

    /// <summary>A font table entry: its name, \fcharset and the other name of the font.</summary>
    private sealed record FontEntry(string Name, int Charset, bool EastAsian);

    private readonly List<FontEntry> _fonts = [];
    private readonly Dictionary<(string Name, int Charset), int> _latinFonts = [];
    private readonly Dictionary<string, int> _eastAsianFonts = new(StringComparer.Ordinal);
    private readonly List<string> _colors = [];
    private readonly Dictionary<string, int> _colorIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The index of a font for text of <paramref name="charset"/>, added on first use.</summary>
    private int FontIndex(string name, int charset)
    {
        name = CleanFontName(name);
        if (_latinFonts.TryGetValue((name, charset), out var index))
            return index;
        CheckEntries(_fonts.Count, "fonts");
        _fonts.Add(new FontEntry(name, charset, EastAsian: false));
        return _latinFonts[(name, charset)] = _fonts.Count - 1;
    }

    /// <summary>
    /// The index of an East Asian font, added on first use with <paramref name="charset"/> (the script of the text
    /// that first used it): RtfReader and Word take a font of an East Asian \fcharset as the East Asian font.
    /// </summary>
    private int EastAsianFontIndex(string name, int charset)
    {
        name = CleanFontName(name);
        if (_eastAsianFonts.TryGetValue(name, out var index))
            return index;
        CheckEntries(_fonts.Count, "fonts");
        _fonts.Add(new FontEntry(name, charset, EastAsian: true));
        return _eastAsianFonts[name] = _fonts.Count - 1;
    }

    /// <summary>A font name without the characters that end or open a font table entry.</summary>
    private static string CleanFontName(string name)
    {
        var clean = name.Replace(";", "", StringComparison.Ordinal).Trim();
        return clean.Length == 0 ? DefaultLatinFont : clean;
    }

    /// <summary>The colour table index of "#RRGGBB" (0, the automatic colour, for none).</summary>
    private int ColorIndex(string? color)
    {
        if (Odt.Odf.Color(color) is not { } hex)
            return 0;
        if (_colorIndex.TryGetValue(hex, out var index))
            return index;
        CheckEntries(_colors.Count, "colours");
        _colors.Add(hex);
        return _colorIndex[hex] = _colors.Count;
    }

    private void CheckEntries(int count, string what)
    {
        if (count >= _limits.MaxObjects)
            throw DocumentLimits.Exceeded($"The RTF file would define more than {DocumentLimits.Count(_limits.MaxObjects)} {what}.");
    }

    /// <summary>Windows code page of an \fcharset (the inverse of RtfReader's table), 1252 for ANSI.</summary>
    internal static int CodePage(int charset) => charset switch
    {
        128 => 932,
        129 => 949,
        134 => 936,
        136 => 950,
        161 => 1253,
        162 => 1254,
        163 => 1258,
        177 => 1255,
        178 => 1256,
        186 => 1257,
        204 => 1251,
        222 => 874,
        238 => 1250,
        _ => 1252,
    };

    /// <summary>
    /// The other name of a font, which Word and RtfReader use when the font is not installed: the document's own
    /// (DOCX w:altName), else the font's name in the other language (맑은 고딕 / Malgun Gothic) or a font of the same
    /// widths (Calibri / Carlito).
    /// </summary>
    private string? Alternate(string name)
    {
        if (_document.FontAlternates.TryGetValue(name, out var known) && !string.IsNullOrWhiteSpace(known))
            return known;
        return FontNames.Alternatives(name).FirstOrDefault(other => !other.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── Header ─────────────────────────

    /// <summary>Everything before the body: the tables the body filled, the document's information and settings.</summary>
    private void Header()
    {
        var o = _out;
        o.Control("{\\rtf1\\ansi\\ansicpg1252\\deff0\\deflang1033\\uc1");
        o.Line();

        o.Open();
        o.Control("\\fonttbl");
        for (var i = 0; i < _fonts.Count; i++)
        {
            var font = _fonts[i];
            o.Line();
            o.Open();
            var family = FontNames.IsMonospace(font.Name) ? "\\fmodern" : "\\fnil";
            o.Control($"\\f{Number(i)}{family}\\fcharset{Number(font.Charset)}\\fprq{(family == "\\fmodern" ? 1 : 2)}");
            o.Text(font.Name, CodePage(font.Charset));
            if (Alternate(font.Name) is { } alternate)
            {
                o.Open();
                o.Control("\\*\\falt");
                o.Text(CleanFontName(alternate), CodePage(font.Charset));
                o.Close();
            }
            o.Raw(";");
            o.Close();
        }
        o.Close();
        o.Line();

        o.Open();
        o.Control("\\colortbl");
        o.Raw(";");
        foreach (var color in _colors)
        {
            var value = int.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            o.Control($"\\red{Number(value >> 16 & 0xFF)}\\green{Number(value >> 8 & 0xFF)}\\blue{Number(value & 0xFF)}");
            o.Raw(";");
        }
        o.Close();
        o.Line();

        StyleSheet();
        ListTables();
        Information();

        // Document settings: the first section's page, the default tab interval, notes.
        var first = _document.Sections.FirstOrDefault();
        var page = first?.Page ?? DefaultPage;
        o.Control($"\\paperw{Number(Twips(page.Width))}\\paperh{Number(Twips(page.Height))}" +
                  $"\\margl{Number(Twips(page.Left))}\\margr{Number(Twips(page.Right))}" +
                  $"\\margt{Number(Twips(page.Top + page.Header))}\\margb{Number(Twips(page.Bottom + page.Footer))}" +
                  $"\\gutter{Number(Twips(page.Gutter))}{(page.Landscape ? "\\landscape" : "")}");
        if (_facingPages)
            o.Control("\\facingp");
        var tab = _document.DefaultTabStop is > 0 and var interval ? Twips(interval) : DefaultTabTwips;
        o.Control($"\\deftab{Number(Math.Max(1, tab))}\\widowctrl");
        o.Control($"\\ftnbj{NoteNumbering("ftn", (first?.Footnotes ?? HNoteShape.Footnote).Format)}\\aenddoc{NoteNumbering("aftn", (first?.Endnotes ?? HNoteShape.Endnote).Format)}\\fet2");
        o.Line();
    }

    /// <summary>\ftnnar, \aftnnrlc ...: how notes are numbered.</summary>
    private static string NoteNumbering(string prefix, HNoteFormat format) => "\\" + prefix + format.Type switch
    {
        "ROMAN_SMALL" => "nrlc",
        "ROMAN_CAPITAL" => "nruc",
        "LATIN_SMALL" => "nalc",
        "LATIN_CAPITAL" => "nauc",
        "HANGUL_SYLLABLE" => "nganada",
        "HANGUL_JAMO" => "nchosung",
        "CIRCLED_DIGIT" => "ncnum",
        _ => "nar",
    };

    /// <summary>
    /// Normal and Word's "heading 1".."heading 9" (outline levels, which RtfReader and Word read as headings), with
    /// the properties paragraphs of those styles repeat (RTF paragraphs carry their formatting themselves).
    /// </summary>
    private void StyleSheet()
    {
        var o = _out;
        o.Open();
        o.Control("\\stylesheet");
        o.Line();
        o.Open();
        o.Control($"\\s0\\ql\\widctlpar\\sl295\\slmult1\\f0\\fs{Number(DefaultHalfPoints)}\\snext0");
        o.Raw("Normal;");
        o.Close();
        for (var level = 1; level <= 9; level++)
        {
            o.Line();
            o.Open();
            o.Control($"\\s{Number(level)}\\ql\\keepn\\keep\\widctlpar\\sb{Number(level <= 2 ? 240 : 160)}\\sa80\\sl295\\slmult1\\outlinelevel{Number(level - 1)}" +
                      $"\\b\\f0\\fs{Number(HeadingSizes[level - 1])}\\sbasedon0\\snext0");
            o.Raw($"heading {Number(level)};");
            o.Close();
        }
        o.Close();
        o.Line();
    }

    private void Information()
    {
        var o = _out;
        if (string.IsNullOrWhiteSpace(_document.Title) && _document.Authors.Count == 0)
            return;
        o.Open();
        o.Control("\\info");
        if (!string.IsNullOrWhiteSpace(_document.Title))
        {
            o.Open();
            o.Control("\\title");
            o.Text(_document.Title.Trim(), 1252);
            o.Close();
        }
        if (_document.Authors.Count > 0)
        {
            o.Open();
            o.Control("\\author");
            o.Text(string.Join("; ", _document.Authors), 1252);
            o.Close();
        }
        o.Close();
        o.Line();
    }

    // ───────────────────────── Lists ─────────────────────────

    /// <summary>Indent of list level <paramref name="level"/> (twips): 400 per level with a 400 hanging number, as the DOCX writer's.</summary>
    private static int ListIndent(int level) => (level + 1) * 400;

    private readonly Dictionary<HNumbering, int> _numberings = new(ReferenceEqualityComparer.Instance);

    /// <summary>The \ls number of a list definition, added on first use.</summary>
    private int ListNumber(HNumbering numbering)
    {
        if (_numberings.TryGetValue(numbering, out var number))
            return number;
        CheckEntries(_numberings.Count, "lists");
        return _numberings[numbering] = _numberings.Count + 1;
    }

    /// <summary>\listtable with nine levels per list, and \listoverridetable with one \ls per list.</summary>
    private void ListTables()
    {
        if (_numberings.Count == 0)
            return;
        var o = _out;
        o.Open();
        o.Control("\\*\\listtable");
        foreach (var (numbering, number) in _numberings)
        {
            o.Line();
            o.Open();
            o.Control($"\\list\\listtemplateid{Number(ListId(number))}\\listhybrid");
            for (var level = 0; level < 9; level++)
            {
                var definition = level < numbering.Levels.Count ? numbering.Levels[level] : new HNumberingLevel("DIGIT", $"^{level + 1}.", 1, Bullet: false);
                o.Open();
                var format = definition.Bullet ? 23 : LevelFormat(definition.Format);
                o.Control($"\\listlevel\\levelnfc{Number(format)}\\levelnfcn{Number(format)}\\leveljc0\\leveljcn0\\levelfollow0\\levelstartat{Number(Math.Max(0, definition.Start))}");
                var (text, numbers) = LevelText(definition);
                o.Open();
                o.Control("\\leveltext");
                // The length first, then the text with \'00..\'08 for the numbers of those levels.
                o.Byte(Math.Min(text.Length, 255));
                foreach (var c in text.Take(255))
                {
                    if (c < 9)
                        o.Byte(c);
                    else
                        o.Text(c.ToString(), 1252);
                }
                o.Raw(";");
                o.Close();
                o.Open();
                o.Control("\\levelnumbers");
                foreach (var position in numbers)
                    o.Byte(position);
                o.Raw(";");
                o.Close();
                o.Control($"\\fi-400\\li{Number(ListIndent(level))}\\lin{Number(ListIndent(level))}");
                o.Close();
            }
            o.Open();
            o.Control("\\listname");
            o.Raw(";");
            o.Close();
            o.Control($"\\listid{Number(ListId(number))}");
            o.Close();
        }
        o.Close();
        o.Line();

        o.Open();
        o.Control("\\*\\listoverridetable");
        foreach (var number in _numberings.Values)
        {
            o.Line();
            o.Open();
            o.Control($"\\listoverride\\listid{Number(ListId(number))}\\listoverridecount0\\ls{Number(number)}");
            o.Close();
        }
        o.Close();
        o.Line();
    }

    private static int ListId(int number) => 1000 + number;

    /// <summary>OWPML number format → \levelnfc (Word's number formats, the inverse of RtfReader's).</summary>
    internal static int LevelFormat(string owpml) => owpml switch
    {
        "ROMAN_CAPITAL" => 1,
        "ROMAN_SMALL" => 2,
        "LATIN_CAPITAL" => 3,
        "LATIN_SMALL" => 4,
        "IDEOGRAPH" => 10,
        "CIRCLED_DIGIT" => 18,
        "HANGUL_SYLLABLE" => 24,
        "HANGUL_JAMO" => 25,
        "HANGUL_PHONETIC" => 40,
        _ => 0,
    };

    /// <summary>
    /// A level's text with (char)0..(char)8 for the numbers of levels 1..9 ("^1.^2." → "\0.\1."), and the 1-based
    /// positions of those numbers in it (\levelnumbers). A bullet is its character.
    /// </summary>
    internal static (string Text, List<int> Numbers) LevelText(HNumberingLevel level)
    {
        if (level.Bullet)
            return (level.Text.Length > 0 ? StringInfo.GetNextTextElement(level.Text) : "•", []);
        var text = new StringBuilder();
        var numbers = new List<int>();
        for (var i = 0; i < level.Text.Length; i++)
        {
            if (level.Text[i] == '^' && i + 1 < level.Text.Length && level.Text[i + 1] is >= '1' and <= '9')
            {
                text.Append((char)(level.Text[i + 1] - '1'));
                numbers.Add(text.Length);
                i++;
                continue;
            }
            text.Append(level.Text[i]);
        }
        return (text.ToString(), numbers);
    }

    // ───────────────────────── Sections ─────────────────────────

    /// <summary>Some section has a header or footer for even pages: the document has facing pages (\facingp).</summary>
    private bool _facingPages;

    private void Body()
    {
        var sections = _document.Sections.Count > 0 ? _document.Sections : [new HSection()];
        _facingPages = sections.Any(s => s.Headers.Concat(s.Footers).Any(h => h.Pages == HPageType.Even));
        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];
            var page = section.Page ?? DefaultPage;
            var parts = SplitAtColumnChanges(section);
            for (var p = 0; p < parts.Count; p++)
            {
                _token.ThrowIfCancellationRequested();
                var (columns, blocks) = parts[p];
                if (s > 0 || p > 0)
                {
                    _out.Control("\\sect");
                    _out.Line();
                }
                SectionProperties(section, page, columns, continuous: p > 0);
                _width = ColumnWidth(page.TextWidth, columns);
                Blocks(blocks, Story.Body, 0, BlockEnd.Story);
            }
        }
    }

    /// <summary>Splits a section where its columns change: each piece becomes a section of its own, without a page break.</summary>
    private static List<(HColumns Columns, List<HBlock> Blocks)> SplitAtColumnChanges(HSection section)
    {
        var parts = new List<(HColumns, List<HBlock>)>();
        var columns = section.Columns;
        var current = new List<HBlock>();
        foreach (var block in section.Blocks)
        {
            if (block is HParagraph { ColumnsChange: { } change })
            {
                if (current.Count > 0)
                {
                    parts.Add((columns, current));
                    current = [];
                }
                columns = change;
            }
            current.Add(block);
        }
        parts.Add((columns, current));
        return parts;
    }

    private static int ColumnWidth(int textWidth, HColumns columns) =>
        columns.Count <= 1 ? textWidth : Math.Max(2000, (textWidth - columns.Gap * (columns.Count - 1)) / columns.Count);

    /// <summary>
    /// \sectd and the section's page, columns, numbering, headers and footers. The model's Top / Bottom run from the
    /// paper edge to the header / footer and Header / Footer are their heights (as in 한글); RTF's margins reach the
    /// body and \headery / \footery are the distances.
    /// </summary>
    private void SectionProperties(HSection section, HPage page, HColumns columns, bool continuous)
    {
        var o = _out;
        var sb = new StringBuilder("\\sectd");
        if (continuous)
            sb.Append("\\sbknone");
        sb.Append($"\\pgwsxn{Number(Twips(page.Width))}\\pghsxn{Number(Twips(page.Height))}")
          .Append($"\\marglsxn{Number(Twips(page.Left))}\\margrsxn{Number(Twips(page.Right))}")
          .Append($"\\margtsxn{Number(Twips(page.Top + page.Header))}\\margbsxn{Number(Twips(page.Bottom + page.Footer))}")
          .Append($"\\guttersxn{Number(Twips(page.Gutter))}\\headery{Number(Twips(page.Top))}\\footery{Number(Twips(page.Bottom))}");
        if (page.Landscape)
            sb.Append("\\lndscpsxn");
        if (columns.Count > 1)
        {
            sb.Append($"\\cols{Number(Math.Min(columns.Count, 255))}\\colsx{Number(Twips(columns.Gap))}");
            if (columns.Separator)
                sb.Append("\\linebetcol");
            if (columns.Sizes is { } sizes && sizes.Count == columns.Count)
            {
                for (var i = 0; i < sizes.Count; i++)
                {
                    sb.Append($"\\colno{Number(i + 1)}\\colw{Number(Twips(sizes[i].Width))}");
                    if (i + 1 < sizes.Count)
                        sb.Append($"\\colsr{Number(Twips(sizes[i].Gap))}");
                }
            }
        }
        if (columns.RightToLeft)
            sb.Append("\\rtlsect");
        if (!continuous && section.StartPageNumber is { } start)
            sb.Append($"\\pgnstarts{Number(Math.Max(0, start))}\\pgnrestart");
        var titlePage = !continuous && (section.HideFirstHeader || section.HideFirstFooter || section.FirstPageHeader is not null || section.FirstPageFooter is not null);
        if (titlePage)
            sb.Append("\\titlepg");
        o.Control(sb.ToString());
        o.Line();
        if (continuous)
            return;

        _width = page.TextWidth;
        HeadersFooters("header", section.Headers, section.HideFirstHeader ? [] : section.FirstPageHeader, titlePage);
        HeadersFooters("footer", section.Footers, section.HideFirstFooter ? [] : section.FirstPageFooter, titlePage);
    }

    /// <summary>
    /// \header (every page), \headerr and \headerl (odd and even pages, with facing pages) and \headerf (the first
    /// page, empty when the section hides its header there).
    /// </summary>
    private void HeadersFooters(string kind, List<HHeaderFooter> items, List<HBlock>? firstPage, bool titlePage)
    {
        var both = items.LastOrDefault(i => i.Pages == HPageType.Both)?.Blocks;
        var odd = items.LastOrDefault(i => i.Pages == HPageType.Odd)?.Blocks;
        var even = items.LastOrDefault(i => i.Pages == HPageType.Even)?.Blocks;
        if (_facingPages)
        {
            Part(kind + "r", odd ?? both);
            Part(kind + "l", even ?? both);
        }
        else
        {
            Part(kind, odd ?? both);
        }
        if (titlePage)
            Part(kind + "f", firstPage ?? odd ?? both ?? []);
    }

    private void Part(string destination, List<HBlock>? blocks)
    {
        if (blocks is null)
            return;
        _out.Open();
        _out.Control("\\" + destination);
        Blocks(blocks, Story.Header, 0, BlockEnd.Story);
        _out.Close();
        _out.Line();
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>HWPUNIT → twips (1/1440 inch).</summary>
    private static int Twips(int hwpUnit) => (int)Math.Round(hwpUnit / 5.0);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
