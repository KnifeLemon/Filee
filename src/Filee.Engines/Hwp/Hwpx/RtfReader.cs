// Reads Rich Text Format (as Word, WordPad and LibreOffice write it) into the HDocument model, so RTF → DOCX, HWPX,
// PDF, HTML, Markdown, ... need neither Pandoc nor LibreOffice. Split over partial files:
//  * RtfReader.cs        groups and destinations, character and paragraph properties, paragraphs, sections, notes,
//                        fields (hyperlinks, page numbers), bookmarks, headers and footers;
//  * RtfReader.Tables.cs table rows (\trowd ... \cell ... \row), merged cells, lists (\listtable, \ls) and pictures;
//  * RtfReader.Lexer.cs  tokens.
//
// Text is decoded with the code page of the font in effect (\fcharset, \cpg) or the document's (\ansicpg), so
// Korean, Chinese and Japanese RTF from older programs reads right; \uN takes Unicode characters and skips their
// \ucN fallback. Destinations the reader doesn't know (\*\... and the index, comment and object data groups) are
// skipped with their content. Group depth and everything built count against DocumentLimits.

using System.Globalization;
using System.Text;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Reads RTF files into the document model.</summary>
internal sealed partial class RtfReader
{
    /// <summary>Word's single line spacing in 한글 percent, as the DOCX reader has it.</summary>
    private const int SingleLinePercent = 130;

    /// <summary>RTF's default font size: 12 pt, in half points.</summary>
    private const int DefaultHalfPoints = 24;

    private enum Dest
    {
        /// <summary>Text of the document: body, cells, notes, headers, footers, field results.</summary>
        Text,
        Skip,
        FontTable,
        ColorTable,
        StyleSheet,
        Info,
        Title,
        ListTable,
        ListOverrideTable,
        LevelText,
        Picture,
        FieldInstruction,
        BookmarkStart,
    }

    /// <summary>Where text and paragraphs go: the body of a section, a note, a header or a footer.</summary>
    private sealed class Story
    {
        public List<HBlock> Blocks { get; set; } = [];
        public List<HInline> Inlines { get; } = [];
        public TableBuilder? Table { get; set; }
        public bool PageBreak { get; set; }
    }

    private sealed class FieldContext
    {
        public StringBuilder Instruction { get; } = new();
    }

    private record struct CharProps(bool Bold, bool Italic, bool Underline, bool Strike, bool Superscript, bool Subscript,
        int HalfPoints, int Font, int Color, int Shade, bool Hidden)
    {
        public static CharProps Default => new() { HalfPoints = DefaultHalfPoints, Font = -1 };
    }

    private record struct ParaProps(HAlign? Align, int Left, int Right, int FirstLine, int Before, int After, int Line, bool Multiple,
        bool KeepNext, bool Keep, bool PageBreakBefore, bool InTable, int Style, int ListOverride, int ListLevel, int Outline)
    {
        public static ParaProps Default => new() { Style = -1, ListOverride = -1, Outline = -1 };
    }

    /// <summary>The state of a group; a nested group starts as a copy of its parent's.</summary>
    private sealed class Group
    {
        public Dest Dest { get; set; }
        public CharProps Char { get; set; } = CharProps.Default;
        public ParaProps Para { get; set; } = ParaProps.Default;
        public int Uc { get; set; } = 1;
        public bool Ignorable { get; set; }
        public required Story Story { get; set; }

        /// <summary>Inlines of an open hyperlink, which take the text instead of the paragraph.</summary>
        public List<HInline>? Sink { get; set; }
        public FieldContext? Field { get; set; }

        /// <summary>The note this group started is an endnote (\ftnalt).</summary>
        public bool Endnote { get; set; }

        /// <summary>Called when this group closes: it started a destination.</summary>
        public Action? Closer { get; set; }

        public Group Clone() => new()
        {
            Dest = Dest,
            Char = Char,
            Para = Para,
            Uc = Uc,
            Story = Story,
            Sink = Sink,
            Field = Field,
        };
    }

    private readonly Lexer _lexer;
    private readonly string _mediaFolder;
    private readonly DocumentBudget _budget;
    private readonly HDocument _document = new();
    private readonly Stack<Group> _groups = new();
    private Group _g;
    private HSection _section;
    private readonly Story _body;

    private int _ansiCodePage = 1252;
    private int _defaultFont = -1;
    private readonly Dictionary<int, (string Name, int CodePage, bool EastAsian)> _fonts = [];
    private readonly List<string?> _colors = [];
    private readonly Dictionary<int, (string Name, CharProps Char, ParaProps Para)> _styles = [];

    // What the open font, colour, style, list or picture group is collecting.
    private int _fontIndex = -1;
    private int _fontCharset = -1;
    private int _fontCodePage;
    private readonly StringBuilder _collected = new();
    private int _red, _green, _blue;
    private int _styleId = -1;

    // Text bytes waiting to be decoded with the code page they came in.
    private readonly List<byte> _pending = [];
    private int _pendingCodePage;
    private int _skipFallback;

    // Page setup: the document's, and the current section's.
    private PageProps _documentPage = PageProps.Default;
    private PageProps _sectionPage = PageProps.Default;

    private RtfReader(byte[] rtf, string mediaFolder, DocumentLimits limits)
    {
        _lexer = new Lexer(rtf);
        _mediaFolder = mediaFolder;
        _budget = new DocumentBudget(limits);
        _section = new HSection();
        _document.Sections.Add(_section);
        _body = new Story { Blocks = _section.Blocks };
        _g = new Group { Story = _body };
    }

    public static HDocument Read(string path, string mediaFolder) => Read(File.ReadAllBytes(path), mediaFolder, DocumentLimits.Default);

    internal static HDocument Read(byte[] rtf, string mediaFolder, DocumentLimits limits)
    {
        if (rtf.Length > limits.MaxPackageBytes)
            throw DocumentLimits.Exceeded($"The RTF file is larger than {limits.MaxPackageBytes / (1024 * 1024)} MB.");
        if (!rtf.AsSpan().TrimStart(" \t\r\n"u8).StartsWith("{\\rtf"u8))
            throw new InvalidDataException("The file is not an RTF document.");
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new RtfReader(rtf, mediaFolder, limits).Run();
    }

    private HDocument Run()
    {
        var maxDepth = _budget.Limits.MaxXmlDepth;
        while (true)
        {
            var token = _lexer.Next();
            if (token.Kind == TokenKind.End)
                break;
            switch (token.Kind)
            {
                case TokenKind.Open:
                    Flush();
                    if (_groups.Count >= maxDepth)
                        throw DocumentLimits.Exceeded($"The RTF groups are nested more than {maxDepth} levels deep.");
                    _groups.Push(_g);
                    _g = _g.Clone();
                    break;
                case TokenKind.Close:
                    Flush();
                    if (_groups.Count == 0)
                        break; // a stray closing brace after the document
                    var closing = _g;
                    _g = _groups.Pop();
                    _skipFallback = 0;
                    closing.Closer?.Invoke();
                    break;
                case TokenKind.Word when _g.Dest != Dest.Skip:
                    Flush();
                    Word(token.Word, token.Parameter);
                    break;
                case TokenKind.Symbol when _g.Dest != Dest.Skip:
                    Flush();
                    Symbol(token.Word);
                    break;
                case TokenKind.Hex when _g.Dest != Dest.Skip:
                    if (_skipFallback > 0)
                        _skipFallback--;
                    else if (_g.Dest == Dest.Picture)
                        PictureByte(token.Byte);
                    else
                        Pending([token.Byte]);
                    break;
                case TokenKind.Text when _g.Dest != Dest.Skip:
                    var bytes = _lexer.Data.AsSpan(token.Start, token.Length);
                    var skip = Math.Min(_skipFallback, bytes.Length);
                    _skipFallback -= skip;
                    if (_g.Dest == Dest.Picture)
                        PictureHex(bytes[skip..]);
                    else
                        Pending(bytes[skip..]);
                    break;
                case TokenKind.Binary when _g.Dest == Dest.Picture:
                    PictureBinary(_lexer.Data.AsSpan(token.Start, token.Length));
                    break;
            }
        }
        Flush();
        while (_groups.Count > 0)
        {
            var closing = _g;
            _g = _groups.Pop();
            closing.Closer?.Invoke();
        }
        EndParagraph(_body, force: false);
        CloseTable(_body);
        ApplyPage();
        return _document;
    }

    // ───────────────────────── Control words ─────────────────────────

    private void Word(string word, int? parameter)
    {
        var ignorable = _g.Ignorable;
        _g.Ignorable = false;
        if (Destination(word, parameter))
            return;
        if (ignorable)
        {
            _g.Dest = Dest.Skip;
            return;
        }

        if (_g.Dest is Dest.ListTable or Dest.ListOverrideTable && Lists(word, parameter))
            return;
        var value = parameter ?? 1;
        var on = parameter != 0;
        var c = _g.Char;
        var p = _g.Para;
        switch (word)
        {
            // Document
            case "ansicpg": _ansiCodePage = value; return;
            case "mac": _ansiCodePage = 10000; return;
            case "pc": _ansiCodePage = 437; return;
            case "pca": _ansiCodePage = 850; return;
            case "deff": _defaultFont = value; return;
            case "uc": _g.Uc = Math.Clamp(value, 0, 16); return;
            case "u": Unicode(value); return;

            // Font, colour and style tables
            case "f" when _g.Dest == Dest.FontTable: _fontIndex = value; return;
            case "fcharset" when _g.Dest == Dest.FontTable: _fontCharset = value; return;
            case "cpg" when _g.Dest == Dest.FontTable: _fontCodePage = value; return;
            case "red" when _g.Dest == Dest.ColorTable: _red = value; return;
            case "green" when _g.Dest == Dest.ColorTable: _green = value; return;
            case "blue" when _g.Dest == Dest.ColorTable: _blue = value; return;
            case "s" when _g.Dest == Dest.StyleSheet: _styleId = value; break;
            case "s":
                if (_styles.TryGetValue(value, out var style))
                {
                    _g.Char = style.Char;
                    _g.Para = style.Para with { InTable = p.InTable, Style = value, ListOverride = p.ListOverride, ListLevel = p.ListLevel };
                }
                else
                {
                    _g.Para = p with { Style = value };
                }
                return;

            // Character properties
            case "plain": _g.Char = CharProps.Default with { Font = _defaultFont }; return;
            case "b": _g.Char = c with { Bold = on }; return;
            case "i": _g.Char = c with { Italic = on }; return;
            case "ul" or "uld" or "uldb" or "ulw" or "uldash" or "uldashd" or "uldashdd" or "ulth" or "ulwave" or "ulhwave" or "ululdbwave" or "ulthd" or "ulthdash":
                _g.Char = c with { Underline = on };
                return;
            case "ulnone": _g.Char = c with { Underline = false }; return;
            case "strike" or "striked": _g.Char = c with { Strike = on }; return;
            case "super": _g.Char = c with { Superscript = on, Subscript = false }; return;
            case "sub": _g.Char = c with { Subscript = on, Superscript = false }; return;
            case "nosupersub": _g.Char = c with { Superscript = false, Subscript = false }; return;
            case "fs": _g.Char = c with { HalfPoints = Math.Clamp(value, 2, 3276) }; return;
            case "f": _g.Char = c with { Font = value }; return;
            case "cf": _g.Char = c with { Color = value }; return;
            case "highlight" or "cb" or "chcbpat": _g.Char = c with { Shade = value }; return;
            case "v": _g.Char = c with { Hidden = on }; return;

            // Paragraph properties
            case "pard": _g.Para = ParaProps.Default; return;
            case "ql": _g.Para = p with { Align = HAlign.Left }; return;
            case "qc": _g.Para = p with { Align = HAlign.Center }; return;
            case "qr": _g.Para = p with { Align = HAlign.Right }; return;
            case "qj": _g.Para = p with { Align = HAlign.Justify }; return;
            case "qd": _g.Para = p with { Align = HAlign.Distribute }; return;
            case "li": _g.Para = p with { Left = value }; return;
            case "ri": _g.Para = p with { Right = value }; return;
            case "fi": _g.Para = p with { FirstLine = value }; return;
            case "sb": _g.Para = p with { Before = value }; return;
            case "sa": _g.Para = p with { After = value }; return;
            case "sl": _g.Para = p with { Line = parameter ?? 0 }; return;
            case "slmult": _g.Para = p with { Multiple = on }; return;
            case "keepn": _g.Para = p with { KeepNext = on }; return;
            case "keep": _g.Para = p with { Keep = on }; return;
            case "pagebb": _g.Para = p with { PageBreakBefore = on }; return;
            case "intbl": _g.Para = p with { InTable = true }; return;
            case "ls": _g.Para = p with { ListOverride = value }; return;
            case "ilvl": _g.Para = p with { ListLevel = Math.Clamp(value, 0, 8) }; return;
            case "outlinelevel": _g.Para = p with { Outline = value }; return;

            // Text
            case "par" or "line" or "tab" or "page" or "emdash" or "endash" or "bullet" or "lquote" or "rquote" or "ldblquote"
                or "rdblquote" or "enspace" or "qmspace" or "emspace" when _g.Dest != Dest.Text:
                return;
            case "par": EndParagraph(_g.Story, force: true); return;
            case "sect": EndParagraph(_body, force: false); CloseTable(_body); NewSection(); return;
            case "page":
                EndParagraph(_g.Story, force: false);
                _g.Story.PageBreak = true;
                return;
            case "line": AddInline(new HLineBreak(Format(_g.Char))); return;
            case "tab": AddInline(new HTab(Format(_g.Char))); return;
            case "emdash": Text("—"); return;
            case "endash": Text("–"); return;
            case "bullet": Text("•"); return;
            case "lquote": Text("‘"); return;
            case "rquote": Text("’"); return;
            case "ldblquote": Text("“"); return;
            case "rdblquote": Text("”"); return;
            case "enspace" or "qmspace": Text(" "); return;
            case "emspace": Text(" "); return;
            case "chftn": return; // the note's reference mark: the note itself stands there
            case "ftnalt": _g.Endnote = true; return;
        }

        if (Table(word, parameter) || Lists(word, parameter) || Picture(word, parameter) || Page(word, parameter))
            return;
    }

    /// <summary>Starts a destination; false for words that aren't one.</summary>
    private bool Destination(string word, int? parameter)
    {
        switch (word)
        {
            case "fonttbl":
                _g.Dest = Dest.FontTable;
                return true;
            case "colortbl":
                _g.Dest = Dest.ColorTable;
                _colors.Clear();
                return true;
            case "stylesheet":
                _g.Dest = Dest.StyleSheet;
                return true;
            case "info":
                _g.Dest = Dest.Info;
                return true;
            case "title" when _g.Dest == Dest.Info:
                _g.Dest = Dest.Title;
                _collected.Clear();
                _g.Closer = () => _document.Title = _collected.ToString().Trim() is { Length: > 0 } title ? title : null;
                return true;
            case "listtable":
                _g.Dest = Dest.ListTable;
                return true;
            case "listoverridetable":
                _g.Dest = Dest.ListOverrideTable;
                return true;
            case "leveltext" when _g.Dest == Dest.ListTable:
                _g.Dest = Dest.LevelText;
                _collected.Clear();
                _g.Closer = EndLevelText;
                return true;
            case "pict":
                StartPicture();
                return true;
            case "shppict" or "shp" or "shprslt" or "object" or "result" or "upr" or "ud":
                return true; // a wrapper: its content is read as text
            case "footnote":
                StartNote();
                return true;
            case "header" or "headerl" or "headerr" or "headerf" or "footer" or "footerl" or "footerr" or "footerf":
                StartHeaderFooter(word);
                return true;
            case "field":
                _g.Field = new FieldContext();
                return true;
            case "fldinst" when _g.Field is not null:
                _g.Dest = Dest.FieldInstruction;
                return true;
            case "fldrslt" when _g.Field is not null:
                StartFieldResult(_g.Field);
                return true;
            case "bkmkstart":
                _g.Dest = Dest.BookmarkStart;
                _collected.Clear();
                _g.Closer = () =>
                {
                    if (_collected.ToString().Trim() is { Length: > 0 } name)
                        AddInline(new HBookmark(name));
                };
                return true;
            case "nonshppict" or "listtext" or "pntext" or "xe" or "tc" or "txe" or "rxe" or "bkmkend" or "annotation" or "atnid"
                or "atnauthor" or "template" or "comment" or "author" or "operator" or "company" or "manager" or "category"
                or "keywords" or "subject" or "doccomm" or "hlinkbase" or "creatim" or "revtim" or "printim" or "buptim"
                or "objdata" or "datafield" or "formfield" or "fname" or "falt" or "panose":
                _g.Dest = Dest.Skip;
                return true;
        }
        if (_g.Dest == Dest.Info)
        {
            // Other information groups (author, company, dates, ...).
            _g.Dest = Dest.Skip;
            return true;
        }
        return false;
    }

    private void Symbol(string symbol)
    {
        switch (symbol)
        {
            case "*":
                _g.Ignorable = true;
                return;
            case "~":
                Text(" ");
                return;
            case "_":
                Text("‑");
                return;
            case "-":
                return; // optional hyphen
            case "\\" or "{" or "}":
                Pending([(byte)symbol[0]]);
                return;
            case "|" or ":":
                return; // formula and index characters
        }
    }

    private void Unicode(int value)
    {
        var character = value < 0 ? value + 65536 : value;
        if (_g.Dest == Dest.Picture)
            return;
        Decoded(((char)character).ToString());
        _skipFallback = _g.Uc;
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>Queues bytes to decode with the current font's code page.</summary>
    private void Pending(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        var codePage = CodePage();
        if (_pending.Count > 0 && codePage != _pendingCodePage)
            Flush();
        _pendingCodePage = codePage;
        foreach (var b in bytes)
            _pending.Add(b);
    }

    /// <summary>Decodes the queued bytes into the current destination.</summary>
    private void Flush()
    {
        if (_pending.Count == 0)
            return;
        var text = CodePageEncoding(_pendingCodePage).GetString([.. _pending]);
        _pending.Clear();
        Decoded(text);
    }

    private void Text(string text)
    {
        Flush();
        Decoded(text);
    }

    private void Decoded(string text)
    {
        switch (_g.Dest)
        {
            case Dest.Text:
                if (!_g.Char.Hidden)
                    AddText(text);
                break;
            case Dest.FontTable:
                FontName(text);
                break;
            case Dest.ColorTable:
                foreach (var c in text)
                {
                    if (c == ';')
                    {
                        _colors.Add(_colors.Count == 0 && _red == 0 && _green == 0 && _blue == 0 ? null : $"#{_red:X2}{_green:X2}{_blue:X2}");
                        _red = _green = _blue = 0;
                    }
                }
                break;
            case Dest.StyleSheet:
                _collected.Append(text);
                if (_collected.ToString().IndexOf(';') is >= 0 and var end)
                {
                    var name = _collected.ToString(0, end).Split(',')[0].Trim();
                    if (_styleId >= 0)
                        _styles[_styleId] = (name, _g.Char, _g.Para);
                    _collected.Clear();
                    _styleId = -1;
                }
                break;
            case Dest.Title or Dest.LevelText or Dest.BookmarkStart:
                _collected.Append(text);
                break;
            case Dest.FieldInstruction:
                _g.Field?.Instruction.Append(text);
                break;
        }
    }

    private void FontName(string text)
    {
        foreach (var c in text)
        {
            if (c != ';')
            {
                _collected.Append(c);
                continue;
            }
            if (_fontIndex >= 0)
            {
                var codePage = _fontCodePage > 0 ? _fontCodePage : CharsetCodePage(_fontCharset);
                var eastAsian = _fontCharset is 128 or 129 or 130 or 134 or 136;
                _fonts[_fontIndex] = (_collected.ToString().Trim(), codePage, eastAsian);
            }
            _collected.Clear();
            _fontIndex = _fontCharset = -1;
            _fontCodePage = 0;
        }
    }

    /// <summary>The code page text is decoded with: the font's, else the document's.</summary>
    private int CodePage()
    {
        if (_g.Dest == Dest.FontTable)
            return _fontCodePage > 0 ? _fontCodePage : CharsetCodePage(_fontCharset) is > 0 and var charset ? charset : _ansiCodePage;
        var font = _g.Char.Font >= 0 ? _g.Char.Font : _defaultFont;
        return _fonts.TryGetValue(font, out var entry) && entry.CodePage > 0 ? entry.CodePage : _ansiCodePage;
    }

    /// <summary>Windows code page of an RTF \fcharset; 0 for the document's own.</summary>
    private static int CharsetCodePage(int charset) => charset switch
    {
        77 => 10000,
        128 => 932,
        129 => 949,
        130 => 1361,
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
        255 => 437,
        _ => 0,
    };

    private static readonly Dictionary<int, Encoding> Encodings = [];

    private static Encoding CodePageEncoding(int codePage)
    {
        lock (Encodings)
        {
            if (Encodings.TryGetValue(codePage, out var known))
                return known;
            Encoding encoding;
            try
            {
                encoding = System.Text.Encoding.GetEncoding(codePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                encoding = System.Text.Encoding.GetEncoding(1252);
            }
            Encodings[codePage] = encoding;
            return encoding;
        }
    }

    private void AddText(string text)
    {
        var format = Format(_g.Char);
        var inlines = _g.Sink ?? _g.Story.Inlines;
        if (inlines is [.., HText { } last] && last.GetType() == typeof(HText) && last.Format.SameLayout(format))
            inlines[^1] = new HText(last.Text + text, format);
        else
            inlines.Add(new HText(text, format));
    }

    private void AddInline(HInline inline) => (_g.Sink ?? _g.Story.Inlines).Add(inline);

    private HCharFormat Format(CharProps c)
    {
        string? font = null, eastAsian = null;
        var index = c.Font >= 0 ? c.Font : _defaultFont;
        if (_fonts.TryGetValue(index, out var entry) && entry.Name.Length > 0)
        {
            font = entry.Name;
            eastAsian = entry.EastAsian ? entry.Name : null;
        }
        return new HCharFormat(
            Bold: c.Bold ? true : null,
            Italic: c.Italic ? true : null,
            Underline: c.Underline ? true : null,
            Strike: c.Strike ? true : null,
            Superscript: c.Superscript ? true : null,
            Subscript: c.Subscript ? true : null,
            Size: c.HalfPoints * 50,
            Font: font,
            EastAsianFont: eastAsian,
            Color: c.Color > 0 && c.Color < _colors.Count ? _colors[c.Color] : null,
            Shade: c.Shade > 0 && c.Shade < _colors.Count ? _colors[c.Shade] : null);
    }

    private static HParaFormat Format(ParaProps p) => new(
        Align: p.Align,
        Left: HwpxUnits.FromTwips(p.Left),
        Right: HwpxUnits.FromTwips(p.Right),
        FirstLine: HwpxUnits.FromTwips(p.FirstLine),
        Before: HwpxUnits.FromTwips(p.Before),
        After: HwpxUnits.FromTwips(p.After),
        LineSpacing: p.Line switch
        {
            0 => new HLineSpacing(HLineSpacingKind.Percent, SingleLinePercent),
            < 0 => new HLineSpacing(HLineSpacingKind.Fixed, HwpxUnits.FromTwips(-p.Line)),
            _ when p.Multiple => new HLineSpacing(HLineSpacingKind.Percent, (int)Math.Round(p.Line / 240.0 * SingleLinePercent)),
            _ => new HLineSpacing(HLineSpacingKind.AtLeast, HwpxUnits.FromTwips(p.Line)),
        },
        KeepWithNext: p.KeepNext ? true : null,
        KeepLines: p.Keep ? true : null);

    // ───────────────────────── Paragraphs and sections ─────────────────────────

    /// <summary>Ends the paragraph of <paramref name="story"/>; without <paramref name="force"/>, only one with content.</summary>
    private void EndParagraph(Story story, bool force)
    {
        if (!force && story.Inlines.Count == 0)
            return;
        _budget.Paragraph();
        var para = _g.Para;
        var paragraph = new HParagraph
        {
            Format = Format(para),
            MarkFormat = Format(_g.Char),
            PageBreakBefore = para.PageBreakBefore || story.PageBreak,
            HeadingLevel = Heading(para),
            List = ListOf(para),
        };
        story.PageBreak = false;
        paragraph.Inlines.AddRange(story.Inlines);
        story.Inlines.Clear();
        if (para.InTable && story.Table is { } table)
        {
            table.Cell.Add(paragraph);
            return;
        }
        if (para.InTable)
        {
            (story.Table = new TableBuilder()).Cell.Add(paragraph);
            return;
        }
        CloseTable(story);
        story.Blocks.Add(paragraph);
    }

    private int Heading(ParaProps p)
    {
        if (p.Outline is >= 0 and < 9)
            return p.Outline + 1;
        if (p.Style >= 0 && _styles.TryGetValue(p.Style, out var style)
            && style.Name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style.Name.AsSpan(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level is >= 1 and <= 9)
            return level;
        return 0;
    }

    private void NewSection()
    {
        ApplyPage();
        _section = new HSection();
        _document.Sections.Add(_section);
        _body.Blocks = _section.Blocks;
    }

    // ───────────────────────── Notes, headers, footers, fields ─────────────────────────

    private void StartNote()
    {
        var parent = _g.Sink ?? _g.Story.Inlines;
        var story = new Story();
        _g.Story = story;
        _g.Sink = null;
        _g.Para = ParaProps.Default;
        _budget.Object();
        var level = _budget.Enter();
        var group = _g;
        _g.Closer = () =>
        {
            var inner = _g;
            _g = group; // end the note's last paragraph with the note's own properties
            EndParagraph(story, force: false);
            CloseTable(story);
            _g = inner;
            level.Dispose();
            var note = new HNote(group.Endnote);
            note.Blocks.AddRange(story.Blocks);
            parent.Add(note);
        };
    }

    private void StartHeaderFooter(string word)
    {
        var story = new Story();
        _g.Story = story;
        _g.Sink = null;
        _g.Para = ParaProps.Default;
        var section = _section;
        var group = _g;
        _g.Closer = () =>
        {
            var inner = _g;
            _g = group;
            EndParagraph(story, force: false);
            CloseTable(story);
            _g = inner;
            // First-page headers and footers have no place in the model: the others apply.
            if (word.EndsWith('f'))
                return;
            var pages = word.EndsWith('l') ? HPageType.Even : word.EndsWith('r') ? HPageType.Odd : HPageType.Both;
            var part = new HHeaderFooter(pages);
            part.Blocks.AddRange(story.Blocks);
            (word.StartsWith("header", StringComparison.Ordinal) ? section.Headers : section.Footers).Add(part);
        };
    }

    private void StartFieldResult(FieldContext field)
    {
        var instruction = field.Instruction.ToString().Trim();
        var name = instruction.Split(' ', 2)[0].ToUpperInvariant();
        switch (name)
        {
            case "HYPERLINK" when LinkTarget(instruction) is { } target:
                var link = new HLink(target);
                AddInline(link);
                _g.Sink = link.Content;
                break;
            case "PAGE":
                AddInline(new HField(HFieldKind.PageNumber, Format(_g.Char)));
                _g.Dest = Dest.Skip;
                break;
            case "NUMPAGES" or "SECTIONPAGES":
                AddInline(new HField(HFieldKind.TotalPages, Format(_g.Char)));
                _g.Dest = Dest.Skip;
                break;
        }
    }

    /// <summary>The URL of HYPERLINK "url", or "#bookmark" for HYPERLINK \l "bookmark".</summary>
    private static string? LinkTarget(string instruction)
    {
        var parts = instruction.Split('"');
        var local = instruction.Contains("\\l", StringComparison.Ordinal);
        for (var i = 1; i < parts.Length; i += 2)
        {
            if (parts[i].Length > 0)
                return local && !parts[i].Contains("://", StringComparison.Ordinal) ? "#" + parts[i] : parts[i];
        }
        var bare = instruction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return bare.Length > 1 && !bare[1].StartsWith('\\') ? bare[1] : null;
    }

    // ───────────────────────── Page setup ─────────────────────────

    private record struct PageProps(int Width, int Height, int Left, int Right, int Top, int Bottom, int HeaderFromEdge, int FooterFromEdge,
        int Gutter, bool Landscape, int Columns, int ColumnGap, bool Set)
    {
        public static PageProps Default => new(12240, 15840, 1800, 1800, 1440, 1440, 720, 720, 0, false, 1, 720, false);
    }

    private bool Page(string word, int? parameter)
    {
        var value = parameter ?? 0;
        var document = _documentPage;
        var section = _sectionPage;
        switch (word)
        {
            case "paperw": _documentPage = document with { Width = value, Set = true }; _sectionPage = section with { Width = value, Set = true }; break;
            case "paperh": _documentPage = document with { Height = value, Set = true }; _sectionPage = section with { Height = value, Set = true }; break;
            case "margl": _documentPage = document with { Left = value, Set = true }; _sectionPage = section with { Left = value, Set = true }; break;
            case "margr": _documentPage = document with { Right = value, Set = true }; _sectionPage = section with { Right = value, Set = true }; break;
            case "margt": _documentPage = document with { Top = value, Set = true }; _sectionPage = section with { Top = value, Set = true }; break;
            case "margb": _documentPage = document with { Bottom = value, Set = true }; _sectionPage = section with { Bottom = value, Set = true }; break;
            case "gutter": _documentPage = document with { Gutter = value, Set = true }; _sectionPage = section with { Gutter = value, Set = true }; break;
            case "landscape": _documentPage = document with { Landscape = true }; _sectionPage = section with { Landscape = true }; break;
            case "sectd": _sectionPage = _documentPage; break;
            case "pgwsxn": _sectionPage = section with { Width = value, Set = true }; break;
            case "pghsxn": _sectionPage = section with { Height = value, Set = true }; break;
            case "marglsxn": _sectionPage = section with { Left = value, Set = true }; break;
            case "margrsxn": _sectionPage = section with { Right = value, Set = true }; break;
            case "margtsxn": _sectionPage = section with { Top = value, Set = true }; break;
            case "margbsxn": _sectionPage = section with { Bottom = value, Set = true }; break;
            case "guttersxn": _sectionPage = section with { Gutter = value, Set = true }; break;
            case "headery": _sectionPage = section with { HeaderFromEdge = value, Set = true }; break;
            case "footery": _sectionPage = section with { FooterFromEdge = value, Set = true }; break;
            case "lndscpsxn": _sectionPage = section with { Landscape = true }; break;
            case "cols": _sectionPage = section with { Columns = Math.Clamp(value, 1, 32) }; break;
            case "colsx": _sectionPage = section with { ColumnGap = value }; break;
            case "pgnstarts": _section.StartPageNumber = value; break;
            case "pgnrestart": _section.StartPageNumber ??= 1; break;
            default: return false;
        }
        return true;
    }

    /// <summary>The current section's page setup into the model.</summary>
    private void ApplyPage()
    {
        var p = _sectionPage;
        if (p.Columns > 1)
            _section.Columns = new HColumns(p.Columns, HwpxUnits.FromTwips(p.ColumnGap), false);
        if (!p.Set && !p.Landscape)
            return;
        var (width, height) = p.Landscape && p.Width < p.Height ? (p.Height, p.Width) : (p.Width, p.Height);
        // 한글 measures the top margin to the header and the header area under it; Word measures to the body.
        var top = Math.Min(p.HeaderFromEdge, p.Top);
        var bottom = Math.Min(p.FooterFromEdge, p.Bottom);
        _section.Page = new HPage(
            HwpxUnits.FromTwips(width), HwpxUnits.FromTwips(height),
            HwpxUnits.FromTwips(p.Left), HwpxUnits.FromTwips(p.Right),
            HwpxUnits.FromTwips(top), HwpxUnits.FromTwips(bottom),
            HwpxUnits.FromTwips(p.Top - top), HwpxUnits.FromTwips(p.Bottom - bottom),
            HwpxUnits.FromTwips(p.Gutter));
    }
}
