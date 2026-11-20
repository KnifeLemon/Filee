// Book → PML (Palm Markup Language, the source format of eReader books), from the published PML reference that
// PmlReader follows too. The file is plain ASCII with CR LF line ends: characters of Windows-1252 above 127 become
// \aNNN, other characters \UXXXX (outside the Basic Multilingual Plane, which \U can't reach, U+FFFD), a backslash \\
// and '<' \a060 (so text never looks like a <footnote> block).
//  * Each paragraph is a line, with an empty line after it. Level-1 headings are chapters (\x … \x), levels 2 to 5
//    \X1 … \X4 (deeper ones \X4): eReader lists both as the book's contents.
//  * Bold \B, italic \i, underline \u, strike-through \o, superscript \Sp and subscript \Sb are opened and closed as
//    the runs change, and all closed at the end of every line (PML codes left open run into the next paragraph).
//  * Centred and right-aligned paragraphs are \c … \c and \r … \r; list items get their marker and \T="n%" indents;
//    page breaks are \p; a paragraph that is only a horizontal line is a rule (\w="50%").
//  * Links inside the book are \q="#id" … \q to anchors \Q="id" (links to the web keep their text only); notes are
//    \Fn="fnN"N\Fn with the note in a <footnote id="fnN"> block at the end of the file.
//  * Pictures are \m="img0001.png": PNG files (eReader shows no other format) in the "<name>_img" folder beside the
//    file, where eReader and PmlReader look for them, reserved through the output allocator like any output.
//  * Table rows are lines of their cells separated by " | ".

using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal sealed class PmlWriter
{
    private const string NewLine = "\r\n";

    /// <summary>Windows-1252 bytes 128-255 by character, for \aNNN.</summary>
    private static readonly Dictionary<char, int> UpperWindows1252 = BuildUpperWindows1252();

    private readonly StringBuilder _pml = new();
    private readonly Func<string, string?>? _picture;
    private readonly List<List<HBlock>> _notes = [];
    private readonly ListNumbers _numbers = new();
    private readonly List<string> _open = [];
    private readonly long _maxChars = DocumentLimits.Default.MaxPartBytes;
    private readonly CancellationToken _cancellationToken;
    private bool _inLink;

    private PmlWriter(Func<string, string?>? picture, CancellationToken cancellationToken)
    {
        _picture = picture;
        _cancellationToken = cancellationToken;
    }

    /// <param name="pictures">Where pictures go, or null to leave them out.</param>
    public static void Write(Book book, string outputPath, PmlPictures? pictures, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.ASCII.GetBytes(ToPml(book.Document, pictures is null ? null : pictures.Add, cancellationToken));
        WriterTempFile.Write(outputPath, stream => stream.Write(bytes));
    }

    /// <summary>The PML text of a document; <paramref name="picture"/> gives the file name of a picture, or null.</summary>
    internal static string ToPml(HDocument document, Func<string, string?>? picture, CancellationToken cancellationToken = default)
    {
        Nesting.Limit(document);
        var writer = new PmlWriter(picture, cancellationToken);
        foreach (var section in document.Sections)
            writer.Blocks(section.Blocks);
        // Notes may hold notes of their own: they are numbered on as they come.
        for (var i = 0; i < writer._notes.Count; i++)
        {
            writer._pml.Append($"<footnote id=\"fn{i + 1}\">").Append(NewLine);
            writer.Blocks(writer._notes[i]);
            writer._pml.Append("</footnote>").Append(NewLine).Append(NewLine);
            writer.CheckSize();
        }
        return writer._pml.ToString();
    }

    private void Blocks(List<HBlock> blocks)
    {
        foreach (var block in blocks)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (block)
            {
                case HParagraph paragraph:
                    Paragraph(paragraph);
                    break;
                case HTable table:
                    Table(table);
                    break;
            }
            CheckSize();
        }
    }

    private void Paragraph(HParagraph paragraph)
    {
        var inlines = HDocumentWalker.InlinesOf(paragraph.Inlines).ToList();
        var visible = inlines.Any(i => i is HImage or HNote or HTextBox || i is HText { Text: var t } && t.Trim().Length > 0);
        if (paragraph.PageBreakBefore && paragraph.HeadingLevel != 1 && _pml.Length > 0)
            _pml.Append("\\p").Append(NewLine);
        if (!visible)
        {
            if (inlines.Any(i => i is HShape { Kind: HShapeKind.Line }))
                _pml.Append("\\w=\"50%\"").Append(NewLine).Append(NewLine);
            return;
        }
        if (paragraph.HeadingLevel > 0)
        {
            // \x starts a new page in eReader by itself.
            var code = paragraph.HeadingLevel == 1 ? "\\x" : $"\\X{Math.Min(paragraph.HeadingLevel, 5) - 1}";
            _pml.Append(code);
            Inlines(paragraph.Inlines, heading: true);
            CloseAll();
            _pml.Append(code).Append(NewLine).Append(NewLine);
            return;
        }
        var align = paragraph.Format.Align switch
        {
            HAlign.Center => "\\c",
            HAlign.Right => "\\r",
            _ => null,
        };
        _pml.Append(align);
        if (paragraph.List is { } list)
        {
            _pml.Append($"\\T=\"{Math.Min(5 * (list.Level + 1), 50)}%\"");
            Text((list.Numbered ? _numbers.Next(list) : "•") + " ");
        }
        Inlines(paragraph.Inlines, heading: false);
        CloseAll();
        _pml.Append(align).Append(NewLine).Append(NewLine);
    }

    /// <summary>A row per line, its cells separated by " | "; header rows bold.</summary>
    private void Table(HTable table)
    {
        foreach (var row in table.Rows)
        {
            if (row.Header)
                Open("B");
            for (var c = 0; c < row.Cells.Count; c++)
            {
                if (c > 0)
                    Text(" | ");
                CellInlines(row.Cells[c].Blocks, row.Header);
            }
            CloseAll();
            _pml.Append(NewLine).Append(NewLine);
        }
    }

    /// <summary>A cell's paragraphs (and those of tables inside it) on one line, separated by a space.</summary>
    private void CellInlines(List<HBlock> blocks, bool bold)
    {
        var first = true;
        foreach (var block in blocks)
        {
            if (block is HTable inner)
            {
                foreach (var cell in inner.Rows.SelectMany(r => r.Cells))
                    CellInlines(cell.Blocks, bold);
                continue;
            }
            if (block is not HParagraph paragraph)
                continue;
            if (!first)
                Text(" ");
            first = false;
            Inlines(paragraph.Inlines.Where(i => i is not HImage).ToList(), heading: true, bold);
        }
    }

    /// <param name="heading">On one line: line breaks become spaces (a heading or a table cell).</param>
    /// <param name="bold">Bold whatever the runs say (table header cells).</param>
    private void Inlines(List<HInline> inlines, bool heading, bool bold = false)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    Format(bold ? text.Format with { Bold = true } : text.Format);
                    Text(text.Text);
                    break;
                case HTab:
                    Text(" ");
                    break;
                case HLineBreak:
                    if (heading)
                    {
                        Text(" ");
                    }
                    else
                    {
                        CloseAll();
                        _pml.Append(NewLine);
                    }
                    break;
                case HLink link:
                    Link(link, heading, bold);
                    break;
                case HBookmark bookmark:
                    _pml.Append($"\\Q=\"{Id(bookmark.Name)}\"");
                    break;
                case HImage image:
                    if (_picture?.Invoke(image.Path) is { } name)
                        _pml.Append($"\\m=\"{name}\"");
                    break;
                case HNote note:
                    _notes.Add(note.Blocks);
                    var number = _notes.Count.ToString(CultureInfo.InvariantCulture);
                    if (_inLink)
                        Text($"[{number}]"); // \Fn is a link of its own and would end the one it is in
                    else
                        _pml.Append($"\\Fn=\"fn{number}\"").Append(number).Append("\\Fn");
                    break;
                case HTextBox box:
                    foreach (var paragraph in box.Blocks.OfType<HParagraph>())
                    {
                        Text(" ");
                        Inlines(paragraph.Inlines, heading: true, bold);
                    }
                    break;
            }
        }
    }

    /// <summary>A link inside the book is \q="#id" … \q; one to anywhere else keeps its text.</summary>
    private void Link(HLink link, bool heading, bool bold)
    {
        if (_inLink || !link.Target.StartsWith('#') || link.Target.Length < 2)
        {
            Inlines(link.Content, heading, bold);
            return;
        }
        _pml.Append($"\\q=\"#{Id(link.Target[1..])}\"");
        _inLink = true;
        Inlines(link.Content, heading: true, bold);
        _inLink = false;
        _pml.Append("\\q");
    }

    /// <summary>Opens and closes the style codes so that exactly those of <paramref name="format"/> are open.</summary>
    private void Format(HCharFormat format)
    {
        var wanted = new List<string>(5);
        if (format.Bold is true)
            wanted.Add("B");
        if (format.Italic is true)
            wanted.Add("i");
        if (format.Underline is true)
            wanted.Add("u");
        if (format.Strike is true)
            wanted.Add("o");
        if (format.Superscript is true)
            wanted.Add("Sp");
        else if (format.Subscript is true)
            wanted.Add("Sb");
        for (var k = _open.Count - 1; k >= 0; k--)
        {
            if (!wanted.Contains(_open[k]))
            {
                _pml.Append('\\').Append(_open[k]);
                _open.RemoveAt(k);
            }
        }
        foreach (var code in wanted)
            Open(code);
    }

    private void Open(string code)
    {
        if (_open.Contains(code))
            return;
        _pml.Append('\\').Append(code);
        _open.Add(code);
    }

    private void CloseAll()
    {
        for (var k = _open.Count - 1; k >= 0; k--)
            _pml.Append('\\').Append(_open[k]);
        _open.Clear();
    }

    /// <summary>Text with PML's escapes; control characters are left out.</summary>
    private void Text(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
                _pml.Append("\\\\");
            else if (c == '<')
                _pml.Append("\\a060");
            else if (c is >= ' ' and <= '~')
                _pml.Append(c);
            else if (c is '\t' or '\n' or '\r')
                _pml.Append(' ');
            else if (c < ' ' || c == '\u007F')
                continue;
            else if (c == '\u00AD')
                _pml.Append("\\-");
            else if (UpperWindows1252.TryGetValue(c, out var code))
                _pml.Append("\\a").Append(code.ToString("000", CultureInfo.InvariantCulture));
            else if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;
                _pml.Append("\\UFFFD");
            }
            else
                _pml.Append("\\U").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>An anchor name PML can quote: letters, digits, '-', '_' and '.'.</summary>
    private static string Id(string name)
    {
        var id = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return id.Length > 0 ? id : "_";
    }

    /// <summary>Refuses a book whose PML passes the limit PmlReader applies to the file.</summary>
    private void CheckSize()
    {
        if (_pml.Length > _maxChars)
            throw DocumentLimits.Exceeded($"The PML book would be larger than {DocumentLimits.Megabytes(_maxChars)}.");
    }

    private static Dictionary<char, int> BuildUpperWindows1252()
    {
        var map = new Dictionary<char, int>();
        var encoding = LegacyWriting.StrictWindows1252;
        for (var b = 0x80; b <= 0xFF; b++)
        {
            try
            {
                map.TryAdd(encoding.GetString([(byte)b])[0], b);
            }
            catch (DecoderFallbackException)
            {
                // Bytes Windows-1252 leaves undefined.
            }
        }
        map.Remove('\u00AD'); // the soft hyphen is \- of its own
        return map;
    }
}

/// <summary>
/// The pictures of a PML book: PNG files in the "&lt;name&gt;_img" folder beside it, created on the first picture
/// and reserved through the allocator ("book_img", "book_img (2)" or skipped, as the preset's conflict rule says). An
/// intermediate PML file (a step of a longer route) keeps the folder right beside itself, so the next step finds it.
/// </summary>
internal sealed class PmlPictures(string pml, IOutputAllocator output, string workDirectory)
{
    public const string FolderSuffix = "_img";

    private readonly string _convertFolder = Path.Combine(workDirectory, "pml-pictures-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private string? _folder;
    private bool _skipped;

    /// <summary>The file name of the picture at <paramref name="path"/> in the folder, or null when it can't be read.</summary>
    public string? Add(string path)
    {
        if (_names.TryGetValue(path, out var known))
            return known;
        if (LegacyWriting.Png(path, _convertFolder) is not { } file || Folder() is not { } folder)
            return null;
        var name = $"img{_names.Count + 1:0000}.png";
        File.Copy(file, Path.Combine(folder, name), overwrite: true);
        return _names[path] = name;
    }

    private string? Folder()
    {
        if (_folder is not null || _skipped)
            return _folder;
        var beside = Path.GetDirectoryName(Path.GetFullPath(pml))!;
        var folder = output.Allocate("", FolderSuffix);
        if (folder is not null && !string.Equals(Path.GetDirectoryName(Path.GetFullPath(folder)), beside, StringComparison.OrdinalIgnoreCase))
            folder = Path.Combine(beside, Path.GetFileNameWithoutExtension(pml) + FolderSuffix);
        if (folder is null)
        {
            _skipped = true;
            return null;
        }
        Directory.CreateDirectory(folder);
        return _folder = folder;
    }
}
