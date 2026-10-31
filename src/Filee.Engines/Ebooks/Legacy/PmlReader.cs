// PML (Palm Markup Language, the source format of eReader books) → Book, written from the published PML reference.
// Each line is a paragraph; codes start with a backslash:
//   \p page break            \x … \x chapter title      \Xn … \Xn title of level n (0-4)   \Cn="…" table of contents only
//   \c … \c centred          \r … \r right-aligned      \t … \t indented                    \T="50%" this line indented
//   \i \u \o \B \b \l \k \Sp \Sb  italic, underline, strike-through, bold, bold, large, small caps, superscript, subscript
//   \v … \v hidden text      \n \s normal font          \w="50%" a rule                     \m="name.png" a picture
//   \q="#a" … \q a link      \Q="a" an anchor           \Fn="id" … \Fn / \Sd="id" … \Sd links to notes and sidebars
//   \aNNN Windows-1252 char  \UXXXX Unicode char        \\ a backslash                      \- soft hyphen
// <footnote id="…"> and <sidebar id="…"> blocks hold the notes; they go to the end of the book. Pictures are looked
// up next to the file, in its "<name>_img" folder and in "images".
// The markup becomes HTML for HtmlReader.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static partial class PmlReader
{
    public static HDocument Read(string path, string workFolder) => ReadBook(path, workFolder).Document;

    public static Book ReadBook(string path, string workFolder, CancellationToken cancellationToken = default)
    {
        var limits = DocumentLimits.Default;
        if (new FileInfo(path).Length > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The PML file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var pictures = new[] { folder, Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "_img"), Path.Combine(folder, "images") };
        var html = ToHtml(Decode(File.ReadAllBytes(path)), name => Picture(pictures, folder, name), cancellationToken);
        var page = Path.Combine(EbookFiles.NewFolder(workFolder, "pml"), "book.html");
        File.WriteAllText(page, html, new UTF8Encoding(true));
        var media = Path.Combine(Path.GetDirectoryName(page)!, "~media");
        var content = HtmlReader.ReadFile(page, new HtmlReadOptions { BaseFolder = folder, MediaFolder = media, ResourceRoot = folder });
        var document = HtmlReader.ToDocument(content);
        return new Book(document, new BookMetadata());
    }

    /// <summary>UTF-8 when valid (with or without a byte order mark), else Windows-1252.</summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <summary>A picture name as a path relative to the book's folder, or null when there is no such picture.</summary>
    private static string? Picture(string[] folders, string root, string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/'));
        if (file.Length == 0)
            return null;
        foreach (var candidate in folders.Select(f => EbookFiles.SafePath(f, file)))
            if (candidate is not null && File.Exists(candidate) && HtmlReader.InsideFolder(candidate, root))
                return Path.GetRelativePath(root, candidate).Replace('\\', '/');
        return null;
    }

    /// <summary>The PML text as an HTML page.</summary>
    internal static string ToHtml(string pml, Func<string, string?> picture, CancellationToken cancellationToken = default)
    {
        var notes = new List<(string Id, string Kind, string Text)>();
        pml = TakeNotes(pml, notes);
        var html = new StringBuilder("<html><head><meta charset=\"utf-8\"/></head><body>\n");
        new Converter(html, picture, cancellationToken).Convert(pml);
        foreach (var (id, kind, text) in notes)
        {
            html.Append($"<div class=\"{kind}\"><p><a id=\"{Escape(id)}\"></a></p>\n");
            new Converter(html, picture, cancellationToken).Convert(text);
            html.Append("</div>\n");
        }
        return html.Append("</body></html>\n").ToString();
    }

    /// <summary>
    /// Moves the &lt;footnote id="…"&gt; … &lt;/footnote&gt; and &lt;sidebar&gt; blocks into <paramref name="notes"/>, in
    /// one pass: a block that is never closed ends the search (the rest stays text).
    /// </summary>
    private static string TakeNotes(string pml, List<(string Id, string Kind, string Text)> notes)
    {
        var text = new StringBuilder(pml.Length);
        var position = 0;
        while (position < pml.Length)
        {
            var start = pml.IndexOf('<', position);
            if (start < 0)
                break;
            var kind = string.Compare(pml, start + 1, "footnote", 0, 8, StringComparison.OrdinalIgnoreCase) == 0 ? "footnote"
                : string.Compare(pml, start + 1, "sidebar", 0, 7, StringComparison.OrdinalIgnoreCase) == 0 ? "sidebar" : null;
            var tagEnd = kind is null ? -1 : pml.IndexOf('>', start);
            var close = tagEnd < 0 ? -1 : pml.IndexOf("</" + kind + ">", tagEnd, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                text.Append(pml, position, start + 1 - position);
                position = start + 1;
                if (kind is not null)
                    break;
                continue;
            }
            text.Append(pml, position, start - position);
            var id = NoteId().Match(pml, start, tagEnd - start) is { Success: true } match ? match.Groups[1].Value : "";
            notes.Add((id, kind!, pml[(tagEnd + 1)..close]));
            position = close + kind!.Length + 3;
        }
        if (position < pml.Length)
            text.Append(pml, position, pml.Length - position);
        return text.ToString();
    }

    [GeneratedRegex(@"\bid\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex NoteId();

    private static string Escape(string text) => XhtmlWriter.Escape(text);

    private sealed class Converter(StringBuilder html, Func<string, string?> picture, CancellationToken cancellationToken)
    {
        private static readonly Dictionary<string, (string Open, string Close)> Inline = new(StringComparer.Ordinal)
        {
            ["i"] = ("<i>", "</i>"),
            ["u"] = ("<u>", "</u>"),
            ["o"] = ("<s>", "</s>"),
            ["B"] = ("<b>", "</b>"),
            ["b"] = ("<b>", "</b>"),
            ["l"] = ("<span style=\"font-size:1.3em\">", "</span>"),
            ["k"] = ("<span style=\"font-variant:small-caps\">", "</span>"),
            ["Sp"] = ("<sup>", "</sup>"),
            ["Sb"] = ("<sub>", "</sub>"),
        };

        private readonly StringBuilder _line = new();
        private readonly List<string> _open = []; // inline codes in the order they were opened
        private string? _heading;
        private string? _align;
        private bool _indent;
        private string? _lineIndent;
        private bool _hidden;
        private bool _pageBreak;
        private bool _link;

        public void Convert(string pml)
        {
            for (var i = 0; i < pml.Length;)
            {
                if ((i & 0xFFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var ch = pml[i];
                if (ch == '\r')
                {
                    i++;
                    continue;
                }
                if (ch == '\n')
                {
                    if (_heading is not null)
                        Text(" ");
                    else
                        EndParagraph();
                    i++;
                    continue;
                }
                if (ch != '\\' || i + 1 >= pml.Length)
                {
                    Text(ch.ToString());
                    i++;
                    continue;
                }
                i = Code(pml, i + 1);
            }
            EndParagraph();
        }

        /// <summary>Handles the code after a backslash at <paramref name="i"/>; returns the position after it.</summary>
        private int Code(string pml, int i)
        {
            var c = pml[i];
            switch (c)
            {
                case '\\':
                    Text("\\");
                    return i + 1;
                case '-':
                    Text("­");
                    return i + 1;
                case 'p':
                    EndParagraph();
                    _pageBreak = true;
                    return i + 1;
                case 'x':
                    ToggleHeading("1");
                    return i + 1;
                case 'X' when i + 1 < pml.Length && pml[i + 1] is >= '0' and <= '4':
                    ToggleHeading(((char)(pml[i + 1] + 1)).ToString());
                    return i + 2;
                case 'C' when i + 1 < pml.Length && char.IsAsciiDigit(pml[i + 1]):
                    return Argument(pml, i + 2, out _); // a table of contents entry only
                case 'c':
                    EndParagraph();
                    _align = _align == "center" ? null : "center";
                    return i + 1;
                case 'r':
                    EndParagraph();
                    _align = _align == "right" ? null : "right";
                    return i + 1;
                case 't':
                    EndParagraph();
                    _indent = !_indent;
                    return i + 1;
                case 'T':
                    {
                        var next = Argument(pml, i + 1, out var value);
                        _lineIndent = Percent(value);
                        return next;
                    }
                case 'w':
                    {
                        var next = Argument(pml, i + 1, out var value);
                        EndParagraph();
                        html.Append($"<hr style=\"width:{Percent(value) ?? "100%"}\"/>\n");
                        return next;
                    }
                case 'v':
                    _hidden = !_hidden;
                    return i + 1;
                case 'n' or 's':
                    return i + 1;
                case 'm':
                    {
                        var next = Argument(pml, i + 1, out var value);
                        if (value is not null && picture(value) is { } source)
                            Markup($"<img src=\"{Escape(source)}\" alt=\"\"/>");
                        return next;
                    }
                case 'q':
                    {
                        if (_link)
                        {
                            Markup("</a>");
                            _link = false;
                            return i + 1;
                        }
                        var next = Argument(pml, i + 1, out var value);
                        Markup($"<a href=\"{Escape(value is null ? "#" : value.StartsWith('#') ? value : "#" + value)}\">");
                        _link = true;
                        return next;
                    }
                case 'Q':
                    {
                        var next = Argument(pml, i + 1, out var value);
                        if (value is { Length: > 0 })
                            Markup($"<a id=\"{Escape(value)}\"></a>");
                        return next;
                    }
                case 'F' when i + 1 < pml.Length && pml[i + 1] == 'n':
                    return NoteLink(pml, i + 2);
                case 'S' when i + 1 < pml.Length && pml[i + 1] == 'd':
                    return NoteLink(pml, i + 2);
                case 'S' when i + 1 < pml.Length && pml[i + 1] is 'p' or 'b':
                    ToggleInline(pml.Substring(i, 2));
                    return i + 2;
                case 'a' when i + 3 < pml.Length && int.TryParse(pml.AsSpan(i + 1, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code):
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        Text(Encoding.GetEncoding(1252).GetString([(byte)Math.Min(code, 255)]));
                        return i + 4;
                    }
                case 'U' when i + 4 < pml.Length && int.TryParse(pml.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                    if (code is not (>= 0xD800 and <= 0xDFFF) && code > 0)
                        Text(((char)code).ToString());
                    return i + 5;
                default:
                    if (Inline.ContainsKey(c.ToString()))
                    {
                        ToggleInline(c.ToString());
                        return i + 1;
                    }
                    return i + 1; // an unknown code is left out
            }
        }

        /// <summary>\Fn="id"text\Fn: a link to a note (the closing code ends the link).</summary>
        private int NoteLink(string pml, int i)
        {
            if (_link)
            {
                Markup("</a></sup>");
                _link = false;
                return i;
            }
            var next = Argument(pml, i, out var value);
            Markup($"<sup><a href=\"#{Escape(value ?? "")}\">");
            _link = true;
            return next;
        }

        /// <summary>An argument ="…" after a code; the position after it.</summary>
        private static int Argument(string pml, int i, out string? value)
        {
            value = null;
            if (i + 1 >= pml.Length || pml[i] != '=' || pml[i + 1] != '"')
                return i;
            var end = pml.IndexOf('"', i + 2);
            if (end < 0 || end - i > 1000)
                return i + 2;
            value = pml[(i + 2)..end];
            return end + 1;
        }

        private static string? Percent(string? value) =>
            value is not null && PercentValue().Match(value) is { Success: true } match ? match.Groups[1].Value + "%" : null;

        private void ToggleHeading(string level)
        {
            if (_heading is null)
            {
                EndParagraph();
                _heading = level;
                if (level == "1")
                    _pageBreak = html.Length > 60; // a chapter starts a new page, except the first
                return;
            }
            EndParagraph();
        }

        private void ToggleInline(string code)
        {
            var index = _open.LastIndexOf(code);
            if (index < 0)
            {
                _open.Add(code);
                Markup(Inline[code].Open);
                return;
            }
            // Close the codes opened after it too, then open them again.
            for (var k = _open.Count - 1; k >= index; k--)
                Markup(Inline[_open[k]].Close);
            _open.RemoveAt(index);
            for (var k = index; k < _open.Count; k++)
                Markup(Inline[_open[k]].Open);
        }

        private void Text(string text)
        {
            if (_hidden)
                return;
            _line.Append(Escape(text));
        }

        private void Markup(string markup)
        {
            if (!_hidden)
                _line.Append(markup);
        }

        private void EndParagraph()
        {
            var content = _line.ToString();
            _line.Clear();
            if (_link)
            {
                content += "</a>";
                _link = false;
            }
            for (var k = _open.Count - 1; k >= 0; k--)
                content += Inline[_open[k]].Close;
            var empty = content.Replace("&#160;", "").Trim().Length == 0 || StripTags().Replace(content, "").Trim().Length == 0 && !content.Contains("<img", StringComparison.Ordinal) && !content.Contains("<a id", StringComparison.Ordinal);
            if (!empty)
            {
                var style = new List<string>();
                if (_pageBreak)
                    style.Add("page-break-before:always");
                if (_align is { } align)
                    style.Add("text-align:" + align);
                if (_lineIndent is { } indent)
                    style.Add("margin-left:" + indent);
                else if (_indent)
                    style.Add("margin-left:5%");
                var tag = _heading is { } level ? "h" + level : "p";
                html.Append('<').Append(tag);
                if (style.Count > 0)
                    html.Append(" style=\"").Append(string.Join(';', style)).Append('"');
                html.Append('>').Append(content).Append("</").Append(tag).Append(">\n");
                _pageBreak = false;
            }
            _heading = null;
            _lineIndent = null;
            // Inline codes stay open into the next paragraph.
            foreach (var code in _open)
                _line.Append(Inline[code].Open);
        }
    }

    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex PercentValue();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex StripTags();
}
