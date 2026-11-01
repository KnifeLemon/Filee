// Word 97–2003 (DOC) → WordprocessingML: the binary document written out as the DOCX package Word itself would save
// for it (styles.xml from the stylesheet, numbering.xml from the list tables, document.xml from the text with its
// paragraph, character, section and table properties, headers, footers, notes and pictures), so DocxReader reads both
// Word formats with one set of mappings, the way HwpToHwpx lets HwpxReader read both 한글 formats.
//
// Split over partial files:
//  * DocToDocx.cs             package: parts, relationships, the stories (main text, headers, notes, text boxes) and
//                             their paragraphs, runs, fields, bookmarks and sections;
//  * DocToDocx.Properties.cs  sprms → pPr, rPr, sectPr, borders and shading;
//  * DocToDocx.Styles.cs      the stylesheet (STSH), fonts (SttbfFfn) and lists (PlfLst / PlfLfo) → styles.xml and
//                             numbering.xml;
//  * DocToDocx.Tables.cs      table rows (TAP from the row end's PAPX) → w:tbl;
//  * DocToDocx.Objects.cs     pictures (PICF in the Data stream, OfficeArt BLIPs), floating pictures, shapes and text
//                             boxes (OfficeArt drawings, SPA anchors, text box stories).
//
// Values are copied, not interpreted, wherever WordprocessingML has the same property: DocxReader does the
// interpreting. Structures follow Microsoft's published [MS-DOC] and [MS-ODRAW] specifications.

using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Doc;

/// <summary>Writes the WordprocessingML package of a Word 97–2003 document.</summary>
internal sealed partial class DocToDocx
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";

    private const string RelationshipBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private readonly DocFile _doc;
    private readonly DocumentBudget _budget;
    private readonly ZipArchive _zip;

    /// <summary>Parts written so far (their relationships are written last).</summary>
    private readonly List<Part> _parts = [];
    private Part _part;
    private int _mediaCount;
    private int _drawingId;

    /// <summary>Open fields of the story being written: true while in the instruction, false in the result.</summary>
    private readonly List<bool> _fields = [];

    /// <summary>Section start CPs of the main text after the first (a paragraph ending before one ends its section).</summary>
    private readonly List<int> _sectionEnds = [];

    private sealed class Part(string path)
    {
        public string Path { get; } = path;
        public List<(string Id, string Type, string Target, bool External)> Relationships { get; } = [];

        public string Add(string type, string target, bool external = false)
        {
            var id = $"rId{Relationships.Count + 1}";
            Relationships.Add((id, type, target, external));
            return id;
        }
    }

    private DocToDocx(DocFile doc, ZipArchive zip)
    {
        _doc = doc;
        _zip = zip;
        _budget = new DocumentBudget(doc.Limits);
        _part = new Part("word/document.xml");
        _parts.Add(_part);
    }

    /// <summary>Writes the package of <paramref name="doc"/> to <paramref name="output"/>.</summary>
    public static void Write(DocFile doc, string output)
    {
        using var stream = File.Create(output);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        new DocToDocx(doc, zip).WritePackage();
    }

    private void WritePackage()
    {
        ReadFonts();
        ReadStyles();
        ReadLists();
        ReadNotes();
        ReadBookmarks();
        ReadDrawings();

        var body = new XElement(W + "body");
        var sections = Sections();
        foreach (var section in sections.Skip(1))
            _sectionEnds.Add(section.Start);
        var paragraphs = Paragraphs(0, _doc.CcpText, main: true);
        var blocks = new List<XElement>();
        var index = 0;
        Blocks(paragraphs, ref index, 0, blocks, sections);
        body.Add(blocks);
        body.Add(SectionProperties(sections[^1]));
        WriteNotes();

        AddPart("word/styles.xml", "styles", StylesXml());
        if (_lists.Count > 0)
            AddPart("word/numbering.xml", "numbering", NumberingXml());
        AddPart("word/settings.xml", "settings", SettingsXml());

        WriteXml("word/document.xml", new XElement(W + "document", Namespaces(), body));
        WriteXml("docProps/core.xml", new XElement(Cp + "coreProperties",
            new XAttribute(XNamespace.Xmlns + "cp", Cp), new XAttribute(XNamespace.Xmlns + "dc", Dc),
            _doc.Title() is { } title ? new XElement(Dc + "title", Clean(title)) : null));
        WriteRelationships("_rels/.rels",
        [
            ("rId1", RelationshipBase + "officeDocument", "word/document.xml", false),
            ("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml", false),
        ]);
        foreach (var part in _parts.Where(p => p.Relationships.Count > 0))
        {
            var folder = part.Path[..part.Path.LastIndexOf('/')];
            var name = part.Path[(part.Path.LastIndexOf('/') + 1)..];
            WriteRelationships($"{folder}/_rels/{name}.rels", part.Relationships);
        }
        WriteContentTypes();
    }

    // ───────────────────────── Parts ─────────────────────────

    private static object[] Namespaces() =>
    [
        new XAttribute(XNamespace.Xmlns + "w", W),
        new XAttribute(XNamespace.Xmlns + "r", R),
        new XAttribute(XNamespace.Xmlns + "wp", Wp),
        new XAttribute(XNamespace.Xmlns + "a", A),
        new XAttribute(XNamespace.Xmlns + "pic", Pic),
        new XAttribute(XNamespace.Xmlns + "wps", Wps),
        new XAttribute(XNamespace.Xmlns + "v", V),
        new XAttribute(XNamespace.Xmlns + "o", O),
    ];

    /// <summary>Writes a part related from the main document.</summary>
    private void AddPart(string path, string type, XElement root)
    {
        WriteXml(path, root);
        _parts[0].Add(RelationshipBase + type, path["word/".Length..]);
    }

    private void WriteXml(string path, XElement root)
    {
        var entry = _zip.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false });
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(writer);
    }

    private void WriteRelationships(string path, IEnumerable<(string Id, string Type, string Target, bool External)> relationships)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        WriteXml(path, new XElement(ns + "Relationships",
            relationships.Select(r => new XElement(ns + "Relationship",
                new XAttribute("Id", r.Id), new XAttribute("Type", r.Type), new XAttribute("Target", r.Target),
                r.External ? new XAttribute("TargetMode", "External") : null))));
    }

    private void WriteContentTypes()
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        const string Wml = "application/vnd.openxmlformats-officedocument.wordprocessingml.";
        var types = new XElement(ns + "Types",
            new XElement(ns + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")));
        foreach (var (extension, type) in new[] { ("png", "image/png"), ("jpg", "image/jpeg"), ("bmp", "image/bmp"), ("tif", "image/tiff"), ("emf", "image/x-emf"), ("wmf", "image/x-wmf") })
            types.Add(new XElement(ns + "Default", new XAttribute("Extension", extension), new XAttribute("ContentType", type)));
        void Override(string part, string type) =>
            types.Add(new XElement(ns + "Override", new XAttribute("PartName", "/" + part), new XAttribute("ContentType", type)));
        Override("word/document.xml", Wml + (_doc.Template ? "template.main+xml" : "document.main+xml"));
        Override("word/styles.xml", Wml + "styles+xml");
        Override("word/settings.xml", Wml + "settings+xml");
        if (_lists.Count > 0)
            Override("word/numbering.xml", Wml + "numbering+xml");
        foreach (var part in _parts.Skip(1))
        {
            var kind = part.Path.Contains("/header", StringComparison.Ordinal) ? "header+xml"
                : part.Path.Contains("/footer", StringComparison.Ordinal) ? "footer+xml"
                : part.Path.EndsWith("footnotes.xml", StringComparison.Ordinal) ? "footnotes+xml" : "endnotes+xml";
            Override(part.Path, Wml + kind);
        }
        Override("docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        WriteXml("[Content_Types].xml", types);
    }

    /// <summary>A picture in word/media, related from the part being written; returns the relationship id.</summary>
    private string AddMedia(byte[] bytes, string extension)
    {
        var name = $"media/image{++_mediaCount}.{extension}";
        var entry = _zip.CreateEntry("word/" + name, CompressionLevel.NoCompression);
        using (var stream = entry.Open())
            stream.Write(bytes);
        return _part.Add(RelationshipBase + "image", name);
    }

    private XElement SettingsXml()
    {
        var settings = new XElement(W + "settings", Namespaces());
        var dop = _doc.Blob(DocFile.FibIndex.Dop);
        // Dop: fFacingPages (different odd and even headers) is the first bit.
        if (dop.Length > 0 && (dop[0] & 1) != 0)
            settings.Add(new XElement(W + "evenAndOddHeaders"));
        return settings;
    }

    // ───────────────────────── Stories and paragraphs ─────────────────────────

    /// <summary>A paragraph of a story: CPs [Start, End) of its text, its mark at End, and its properties.</summary>
    private sealed class Para
    {
        public int Start;
        public int End;
        public char Mark;
        public int Istd;
        public List<Sprm> Sprms = [];
        public int Itap;
        public bool InnerCell, InnerTtp, Ttp;

        public bool CellEnd(int depth) => depth == 1 ? Mark == '\a' && !Ttp : Itap == depth && InnerCell;

        public bool RowEnd(int depth) => depth == 1 ? Mark == '\a' && Ttp : Itap == depth && InnerTtp;
    }

    /// <summary>
    /// The paragraphs of CPs [<paramref name="start"/>, <paramref name="end"/>): each ends with a paragraph mark (13),
    /// a cell or row mark (7) or, in the main text, the section mark (12) before a new section.
    /// </summary>
    private List<Para> Paragraphs(int start, int end, bool main)
    {
        var text = _doc.Text(start, end);
        var paragraphs = new List<Para>();
        var from = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var mark = c is '\r' or '\a' || (c == '\f' && main && _sectionEnds.Contains(start + i + 1));
            if (!mark && i < text.Length - 1)
                continue;
            if (!mark)
            {
                i++; // the story's text ends without a mark: its last paragraph still counts
            }
            paragraphs.Add(Paragraph(start + from, start + i, mark ? c : '\r'));
            from = i + 1;
        }
        return paragraphs;
    }

    private Para Paragraph(int start, int markCp, char mark)
    {
        _budget.Paragraph();
        var para = new Para { Start = start, End = markCp, Mark = mark };
        var piece = _doc.PieceAt(Math.Min(markCp, _doc.CpEnd - 1));
        byte[] grpprl = [0, 0];
        if (piece >= 0)
        {
            var fc = _doc.Pieces[piece].FcOf(Math.Clamp(markCp, _doc.Pieces[piece].Cp, _doc.Pieces[piece].CpEnd - 1));
            var run = DocFile.RunAt(_doc.ParagraphRuns, fc);
            if (run >= 0)
                grpprl = _doc.ParagraphRuns[run].Grpprl;
        }
        para.Istd = grpprl.Length >= 2 ? grpprl[0] | grpprl[1] << 8 : 0;
        para.Sprms = Sprm.Parse(grpprl.AsSpan(2));
        // A PAPX too large for its page lives in the Data stream (sprmPHugePapx: its offset there, then cb and grpprl).
        if (para.Sprms.FirstOrDefault(s => s.Op == Ops.PHugePapx) is { Operand.Length: 4 } huge)
        {
            var at = (uint)huge.Int;
            var data = _doc.Data;
            if (at <= data.Length - 2L && data[(int)at] + (data[(int)at + 1] << 8) is var cb && at + 2L + cb <= data.Length)
                para.Sprms = [.. para.Sprms.Where(s => s.Op != Ops.PHugePapx), .. Sprm.Parse(data.AsSpan((int)at + 2, cb))];
        }
        if (piece >= 0 && _doc.Pieces[piece].Prm is { } prm)
            para.Sprms.AddRange(Sprm.Parse(prm).Where(s => s.Group is 1 or 5));
        foreach (var sprm in para.Sprms)
        {
            switch (sprm.Op)
            {
                case Ops.PFInTable:
                    para.Itap = Math.Max(para.Itap, sprm.Byte != 0 ? 1 : 0);
                    break;
                case Ops.PItap:
                    para.Itap = Math.Clamp(sprm.Int, 0, _doc.Limits.MaxNestingDepth + 1); // one past the limit is refused
                    break;
                case Ops.PFTtp:
                    para.Ttp = sprm.Byte != 0;
                    break;
                case Ops.PFInnerTableCell:
                    para.InnerCell = sprm.Byte != 0;
                    break;
                case Ops.PFInnerTtp:
                    para.InnerTtp = sprm.Byte != 0;
                    break;
            }
        }
        return para;
    }

    /// <summary>
    /// The blocks of <paramref name="paragraphs"/> from <paramref name="index"/> at table depth <paramref name="depth"/>:
    /// paragraphs, and tables for runs of paragraphs inside tables. Stops at a paragraph of a shallower depth.
    /// </summary>
    private void Blocks(List<Para> paragraphs, ref int index, int depth, List<XElement> output, List<Section>? sections = null)
    {
        while (index < paragraphs.Count)
        {
            var para = paragraphs[index];
            if (para.Itap > depth)
            {
                output.Add(Table(paragraphs, ref index, depth + 1));
                continue;
            }
            if (para.Itap < depth)
                return;
            index++;
            var p = ParagraphXml(para);
            if (sections is not null && _sectionEnds.IndexOf(para.End + 1) is >= 0 and var ended)
                PPr(p).Add(SectionProperties(sections[ended]));
            output.Add(p);
        }
    }

    /// <summary>The w:pPr of a paragraph, created when it has none.</summary>
    private static XElement PPr(XElement p)
    {
        if (p.Element(W + "pPr") is { } existing)
            return existing;
        var pPr = new XElement(W + "pPr");
        p.AddFirst(pPr);
        return pPr;
    }

    private XElement ParagraphXml(Para para)
    {
        var p = new XElement(W + "p");
        var pPr = ParagraphProperties(para.Istd, para.Sprms);
        var markRun = RunInfoAt(para.End, para.Istd);
        if (markRun.Properties is { HasElements: true } markProperties)
            pPr.Add(new XElement(markProperties));
        if (pPr.HasElements)
            p.Add(pPr);
        Runs(para.Start, para.End, para.Istd, p);
        return p;
    }

    /// <summary>A story (header, note, text box) as blocks; a story that ends in the middle of a table closes it.</summary>
    /// <param name="terminated">
    /// The story ends with a paragraph mark of its own after its last paragraph (headers, footers and text boxes, as
    /// Word writes them): it is left out, so the story doesn't end with an empty paragraph.
    /// </param>
    private List<XElement> Story(int start, int end, bool terminated = false)
    {
        if (terminated && end > start)
            end--;
        var paragraphs = Paragraphs(start, end, main: false);
        var blocks = new List<XElement>();
        var index = 0;
        var fields = _fields.ToList();
        _fields.Clear();
        Blocks(paragraphs, ref index, 0, blocks);
        _fields.Clear();
        _fields.AddRange(fields);
        if (blocks.Count == 0 || blocks[^1].Name != W + "p")
            blocks.Add(new XElement(W + "p"));
        return blocks;
    }

    // ───────────────────────── Runs ─────────────────────────

    /// <summary>What a run of text is: its rPr and the special meaning of its characters.</summary>
    private sealed class RunInfo
    {
        public XElement? Properties;
        public bool Special, Data, Ole2, Deleted;
        public int PicLocation = -1;
        public (int Font, int Code)? Symbol;
    }

    private readonly Dictionary<(int Run, int Piece, int Istd), RunInfo> _runInfos = [];

    /// <summary>The run properties of the character at <paramref name="cp"/> in a paragraph of style <paramref name="istd"/>.</summary>
    private RunInfo RunInfoAt(int cp, int istd)
    {
        var piece = _doc.PieceAt(cp);
        var run = piece < 0 ? -1 : DocFile.RunAt(_doc.CharacterRuns, _doc.Pieces[piece].FcOf(cp));
        return RunInfoOf(run, piece, istd);
    }

    private RunInfo RunInfoOf(int run, int piece, int istd)
    {
        if (_runInfos.TryGetValue((run, piece, istd), out var cached))
            return cached;
        var sprms = run >= 0 ? Sprm.Parse(_doc.CharacterRuns[run].Grpprl) : [];
        if (piece >= 0 && _doc.Pieces[piece].Prm is { } prm)
            sprms.AddRange(Sprm.Parse(prm).Where(s => s.Group == 2));
        var info = new RunInfo();
        foreach (var sprm in sprms)
        {
            switch (sprm.Op)
            {
                case Ops.CFSpec:
                    info.Special = sprm.Byte != 0;
                    break;
                case Ops.CPicLocation:
                    info.PicLocation = sprm.Int;
                    break;
                case Ops.CFData:
                    info.Data = sprm.Byte != 0;
                    break;
                case Ops.CFOle2:
                    info.Ole2 = sprm.Byte != 0;
                    break;
                case Ops.CFRMarkDel:
                    info.Deleted = sprm.Byte != 0;
                    break;
                case Ops.CSymbol when sprm.Operand.Length >= 4:
                    info.Symbol = (sprm.Operand[0] | sprm.Operand[1] << 8, sprm.Operand[2] | sprm.Operand[3] << 8);
                    break;
            }
        }
        info.Properties = RunProperties(sprms, istd);
        _runInfos[(run, piece, istd)] = info;
        return info;
    }

    /// <summary>The runs of CPs [<paramref name="start"/>, <paramref name="end"/>) of a paragraph, added to <paramref name="p"/>.</summary>
    private void Runs(int start, int end, int istd, XElement p)
    {
        var cp = start;
        while (cp < end)
        {
            var piece = _doc.PieceAt(cp);
            if (piece < 0)
                break;
            var segmentEnd = Math.Min(end, _doc.Pieces[piece].CpEnd);
            var fc = _doc.Pieces[piece].FcOf(cp);
            var run = DocFile.RunAt(_doc.CharacterRuns, fc);
            if (run >= 0)
                segmentEnd = Math.Min(segmentEnd, cp + Math.Max(1, (_doc.CharacterRuns[run].FcEnd - fc + _doc.Pieces[piece].Width - 1) / _doc.Pieces[piece].Width));
            else if (DocFile.RunAfter(_doc.CharacterRuns, fc) is { } next)
                segmentEnd = Math.Min(segmentEnd, cp + Math.Max(1, (next - fc + _doc.Pieces[piece].Width - 1) / _doc.Pieces[piece].Width));
            // Bookmarks start between runs.
            if (_bookmarkStarts.Count > 0)
            {
                foreach (var name in BookmarksAt(cp))
                    p.Add(new XElement(W + "bookmarkStart", new XAttribute(W + "id", _bookmarkId++), new XAttribute(W + "name", name)));
                if (NextBookmark(cp) is { } bookmark && bookmark < segmentEnd)
                    segmentEnd = bookmark;
            }
            var info = RunInfoOf(run, piece, istd);
            if (!info.Deleted)
                RunContent(cp, _doc.Text(cp, segmentEnd), info, p);
            cp = segmentEnd;
        }
    }

    /// <summary>The characters of one run: text, tabs, breaks, field characters, notes, pictures and symbols.</summary>
    private void RunContent(int cp, string text, RunInfo info, XElement p)
    {
        var r = NewRun(info);
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0)
                return;
            var inInstruction = _fields.Count > 0 && _fields[^1];
            r.Add(new XElement(W + (inInstruction ? "instrText" : "t"), new XAttribute(XNamespace.Xml + "space", "preserve"), plain.ToString()));
            plain.Clear();
        }
        void Element(XElement element)
        {
            Flush();
            r.Add(element);
        }
        for (var i = 0; i < text.Length; i++, cp++)
        {
            var c = text[i];
            switch (c)
            {
                case '\t':
                    Element(new XElement(W + "tab"));
                    break;
                case '\v':
                    Element(new XElement(W + "br"));
                    break;
                case '\f':
                    Element(new XElement(W + "br", new XAttribute(W + "type", "page")));
                    break;
                case '\u000E':
                    Element(new XElement(W + "br", new XAttribute(W + "type", "column")));
                    break;
                case '\u0013':
                    _budget.Fields(_fields.Count + 1);
                    Element(new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "begin")));
                    _fields.Add(true);
                    break;
                case '\u0014':
                    if (_fields.Count > 0 && _fields[^1])
                    {
                        Element(new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "separate")));
                        _fields[^1] = false;
                    }
                    break;
                case '\u0015':
                    if (_fields.Count > 0)
                    {
                        Element(new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "end")));
                        _fields.RemoveAt(_fields.Count - 1);
                    }
                    break;
                case '\u001E':
                    Element(new XElement(W + "noBreakHyphen"));
                    break;
                case '\u0002' when info.Special:
                    if (NoteReference(cp) is { } note)
                    {
                        Element(note);
                    }
                    else if (_inNote is { } endnote)
                    {
                        Element(new XElement(W + (endnote ? "endnoteRef" : "footnoteRef")));
                    }
                    break;
                case '\u0001' when info.Special:
                    if (!info.Data && InlinePicture(info) is { } picture)
                        Element(picture);
                    break;
                case '\u0008' when info.Special:
                    if (FloatingObject(cp) is { } drawing)
                        Element(drawing);
                    break;
                case '(' when info.Special && info.Symbol is { } symbol:
                    Element(new XElement(W + "sym", new XAttribute(W + "font", FontName(symbol.Font) ?? "Symbol"),
                        new XAttribute(W + "char", symbol.Code.ToString("X4", System.Globalization.CultureInfo.InvariantCulture))));
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        plain.Append(c).Append(text[i + 1]);
                        i++;
                        cp++;
                    }
                    else if (c >= ' ' && !char.IsSurrogate(c) && c is not ('￾' or '￿'))
                    {
                        plain.Append(c);
                    }
                    break; // other control characters (soft hyphens, separators, annotation marks) show nothing
            }
        }
        Flush();
        if (r.Elements().Any(e => e.Name != W + "rPr"))
            p.Add(r);
    }

    private static XElement NewRun(RunInfo info)
    {
        var r = new XElement(W + "r");
        if (info.Properties is { HasElements: true } properties)
            r.Add(new XElement(properties));
        return r;
    }

    // ───────────────────────── Bookmarks ─────────────────────────

    private readonly SortedDictionary<int, List<string>> _bookmarkStarts = [];
    private int _bookmarkId;

    /// <summary>Bookmark names (SttbfBkmk) and where each starts (PlcfBkf). Only starts are written; DocxReader reads no ends.</summary>
    private void ReadBookmarks()
    {
        var names = Sttb(_doc.Blob(DocFile.FibIndex.SttbfBkmk));
        var plc = _doc.Blob(DocFile.FibIndex.PlcfBkf);
        var count = Plc.Count(plc.Length, 4);
        for (var i = 0; i < count && i < names.Count; i++)
        {
            var cp = Plc.Cp(plc, i);
            if (names[i].Length == 0)
                continue;
            if (!_bookmarkStarts.TryGetValue(cp, out var list))
                _bookmarkStarts[cp] = list = [];
            list.Add(names[i]);
        }
    }

    private IEnumerable<string> BookmarksAt(int cp)
    {
        if (!_bookmarkStarts.TryGetValue(cp, out var names))
            return [];
        _bookmarkStarts.Remove(cp); // written once
        return names;
    }

    private int? NextBookmark(int cp)
    {
        foreach (var key in _bookmarkStarts.Keys)
        {
            if (key > cp)
                return key;
        }
        return null;
    }

    /// <summary>
    /// The strings of an STTB: fExtend (0xFFFF for UTF-16 strings), cData, cbExtra, then each string (its length, in
    /// characters or bytes, then the characters) and its extra data.
    /// </summary>
    private static List<string> Sttb(ReadOnlySpan<byte> sttb)
    {
        var strings = new List<string>();
        if (sttb.Length < 4)
            return strings;
        var extended = sttb[0] == 0xFF && sttb[1] == 0xFF;
        var at = extended ? 2 : 0;
        if (at + 4 > sttb.Length)
            return strings;
        var count = sttb[at] | sttb[at + 1] << 8;
        var extra = sttb[at + 2] | sttb[at + 3] << 8;
        at += 4;
        for (var i = 0; i < count && at < sttb.Length; i++)
        {
            string text;
            if (extended)
            {
                if (at + 2 > sttb.Length)
                    break;
                var cch = sttb[at] | sttb[at + 1] << 8;
                at += 2;
                if (at + cch * 2 > sttb.Length)
                    break;
                text = Encoding.Unicode.GetString(sttb.Slice(at, cch * 2));
                at += cch * 2;
            }
            else
            {
                var cch = sttb[at++];
                if (at + cch > sttb.Length)
                    break;
                text = Encoding.Latin1.GetString(sttb.Slice(at, cch));
                at += cch;
            }
            at += extra;
            strings.Add(Clean(text));
        }
        return strings;
    }

    // ───────────────────────── Sections ─────────────────────────

    /// <param name="Index">The section's place in PlcfSed, empty ones counted (its header stories are by that).</param>
    private sealed record Section(int Start, int End, List<Sprm> Sprms, int Index);

    /// <summary>The sections of the main text (PlcfSed: CPs and SEDs, whose fcSepx points to the SEPX).</summary>
    private List<Section> Sections()
    {
        var plc = _doc.Blob(DocFile.FibIndex.PlcfSed);
        var count = Plc.Count(plc.Length, 12);
        var sections = new List<Section>();
        var previous = 0;
        for (var i = 0; i < count; i++)
        {
            var start = Plc.Cp(plc, i);
            var end = Plc.Cp(plc, i + 1);
            if (start != previous || end < start || start >= _doc.CcpText)
                break;
            previous = end;
            if (end == start)
                continue; // an empty section (LibreOffice writes one first) holds nothing
            // The last section's end is the end of the main text (writers put the end of all stories there too).
            end = Math.Min(end, _doc.CcpText);
            var fcSepx = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Plc.Data(plc, count, 12, i)[2..]);
            var sprms = new List<Sprm>();
            if (fcSepx >= 0 && fcSepx <= _doc.Word.Length - 2)
            {
                var cb = _doc.Word[fcSepx] | _doc.Word[fcSepx + 1] << 8;
                if (fcSepx + 2L + cb <= _doc.Word.Length)
                    sprms = Sprm.Parse(_doc.Word.AsSpan(fcSepx + 2, cb));
            }
            sections.Add(new Section(start, end, sprms, i));
        }
        if (sections.Count == 0)
            sections.Add(new Section(0, _doc.CcpText, [], 0));
        return sections;
    }

    // ───────────────────────── Headers and footers ─────────────────────────

    /// <summary>
    /// PlcfHdd: six stories of separators, then six per section (even header, odd header, even footer, odd footer,
    /// first-page header, first-page footer). An empty story is the previous section's, as in Word.
    /// </summary>
    private int[]? _headerCps;

    private readonly Dictionary<(int Start, int End), string> _headerParts = [];

    private IEnumerable<XElement> HeaderReferences(int section)
    {
        _headerCps ??= Plc.Cps(_doc.Blob(DocFile.FibIndex.PlcfHdd));
        string[] kinds = ["even", "default", "even", "default", "first", "first"];
        for (var k = 0; k < 6; k++)
        {
            var story = 6 + section * 6 + k;
            if (story + 1 >= _headerCps.Length)
                yield break;
            var (start, end) = (_headerCps[story], _headerCps[story + 1]);
            if (end <= start || end > _doc.CcpHdd)
                continue;
            var footer = k is 2 or 3 or 5;
            if (!_headerParts.TryGetValue((start, end), out var id))
            {
                var number = _parts.Count;
                var path = $"word/{(footer ? "footer" : "header")}{number}.xml";
                var part = new Part(path);
                _parts.Add(part);
                var previous = _part;
                _part = part;
                List<XElement> blocks;
                try
                {
                    _inHeader = true;
                    blocks = Story(_doc.HeaderStart + start, _doc.HeaderStart + end, terminated: true);
                }
                finally
                {
                    _inHeader = false;
                    _part = previous;
                }
                WriteXml(path, new XElement(W + (footer ? "ftr" : "hdr"), Namespaces(), blocks));
                id = _parts[0].Add(RelationshipBase + (footer ? "footer" : "header"), path["word/".Length..]);
                _headerParts[(start, end)] = id;
            }
            yield return new XElement(W + (footer ? "footerReference" : "headerReference"), new XAttribute(W + "type", kinds[k]), new XAttribute(R + "id", id));
        }
    }

    /// <summary>True while a header or footer story is written (its drawings are the header drawing's).</summary>
    private bool _inHeader;

    // ───────────────────────── Notes ─────────────────────────

    private readonly Dictionary<int, int> _footnoteRefs = [];
    private readonly Dictionary<int, int> _endnoteRefs = [];
    private int[] _footnoteTexts = [];
    private int[] _endnoteTexts = [];
    private readonly List<XElement> _footnotes = [];
    private readonly List<XElement> _endnotes = [];
    private Part? _footnotesPart, _endnotesPart;

    /// <summary>The note being written: false for a footnote, true for an endnote, null outside notes.</summary>
    private bool? _inNote;

    /// <summary>PlcffndRef / PlcfendRef: the CP of each reference; PlcffndTxt / PlcfendTxt: where each note's text is.</summary>
    private void ReadNotes()
    {
        static void References(ReadOnlySpan<byte> plc, Dictionary<int, int> target)
        {
            var count = Plc.Count(plc.Length, 2);
            for (var i = 0; i < count; i++)
                target.TryAdd(Plc.Cp(plc, i), i);
        }
        References(_doc.Blob(DocFile.FibIndex.PlcffndRef), _footnoteRefs);
        References(_doc.Blob(DocFile.FibIndex.PlcfendRef), _endnoteRefs);
        _footnoteTexts = Plc.Cps(_doc.Blob(DocFile.FibIndex.PlcffndTxt));
        _endnoteTexts = Plc.Cps(_doc.Blob(DocFile.FibIndex.PlcfendTxt));
    }

    /// <summary>The reference of the note at <paramref name="cp"/> of the main text; its text goes into the notes part.</summary>
    private XElement? NoteReference(int cp)
    {
        if (_inNote is not null || _inHeader || _inTextBox > 0)
            return null;
        var endnote = !_footnoteRefs.ContainsKey(cp);
        if (!(endnote ? _endnoteRefs : _footnoteRefs).TryGetValue(cp, out var index))
            return null;
        var texts = endnote ? _endnoteTexts : _footnoteTexts;
        if (index + 1 >= texts.Length)
            return null;
        var (start, end) = (texts[index], texts[index + 1]);
        var length = endnote ? _doc.CcpEdn : _doc.CcpFtn;
        if (end <= start || end > length)
            return null;
        var notes = endnote ? _endnotes : _footnotes;
        var id = notes.Count + 1;
        if (endnote)
            _endnotesPart ??= NotesPart("endnotes");
        else
            _footnotesPart ??= NotesPart("footnotes");
        var previous = _part;
        _part = endnote ? _endnotesPart! : _footnotesPart!;
        _inNote = endnote;
        try
        {
            using var level = _budget.Enter();
            var offset = endnote ? _doc.EndnoteStart : _doc.FootnoteStart;
            notes.Add(new XElement(W + (endnote ? "endnote" : "footnote"), new XAttribute(W + "id", id), Story(offset + start, offset + end)));
        }
        finally
        {
            _inNote = null;
            _part = previous;
        }
        return new XElement(W + (endnote ? "endnoteReference" : "footnoteReference"), new XAttribute(W + "id", id));
    }

    private Part NotesPart(string name)
    {
        var part = new Part($"word/{name}.xml");
        _parts.Add(part);
        _parts[0].Add(RelationshipBase + name, $"{name}.xml");
        return part;
    }

    /// <summary>Writes the notes parts once the main text has added every note.</summary>
    private void WriteNotes()
    {
        if (_footnotesPart is not null)
            WriteXml(_footnotesPart.Path, new XElement(W + "footnotes", Namespaces(), _footnotes));
        if (_endnotesPart is not null)
            WriteXml(_endnotesPart.Path, new XElement(W + "endnotes", Namespaces(), _endnotes));
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>Text without what XML can't hold (control characters, unpaired surrogates).</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                builder.Append(c).Append(text[++i]);
            else if (!char.IsSurrogate(c) && (c >= ' ' || c is '\t') && c is not ('￾' or '￿'))
                builder.Append(c);
        }
        return builder.ToString();
    }
}
