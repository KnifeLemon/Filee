// Line breaks inside HWPX paragraphs for Unhwp (issue #1416). 한글 writes a line break as <hp:lineBreak/> inside the
// paragraph's text (<hp:t>a<hp:lineBreak/>b</hp:t>), and Unhwp 0.13 skips it: every line of the paragraph came out
// joined, indentation and empty lines included. So before Unhwp parses an HWPX that has line breaks, Filee writes a copy
// in which each one is a private-use character of the text. Unhwp keeps that character as it is (spaces after it too),
// and its output gets a line break in its place: a newline in plain text, a hard break in Markdown.
//
// HWP 5 stores a line break as a character of the paragraph text (0x0A), which Unhwp already turns into a line end
// (a hard break of two spaces in Markdown), so binary HWP needs nothing of this.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Ebooks;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;

namespace Filee.Engines.Hwp;

/// <summary>Keeps the line breaks of HWPX paragraphs through Unhwp, which skips them.</summary>
internal static partial class UnhwpLineBreaks
{
    private const string HwpmlNamespace = "http://www.hancom.co.kr/hwpml/";

    private static ReadOnlySpan<byte> LineBreakName => "lineBreak"u8;

    /// <summary>
    /// A copy of the package at <paramref name="hwpx"/> in a new folder of <paramref name="workDirectory"/>, with each
    /// line break of its sections as <c>Mark</c>, a private-use character the document doesn't use; null when the
    /// sections have no line break, or the file is no package Filee can read this way (Unhwp then reads it as it is).
    /// </summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The sections unpack to more than a document can hold.</exception>
    public static (string Path, char Mark)? Mark(string hwpx, string workDirectory)
    {
        var replaced = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        char mark;
        try
        {
            using var zip = ZipFile.OpenRead(hwpx);
            var sections = zip.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase)
                                                  && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
            var used = new HashSet<char>();
            var withBreaks = new List<ZipArchiveEntry>();
            foreach (var section in sections)
            {
                switch (Scan(section, used))
                {
                    case null:
                        return null; // larger than any part is read
                    case true:
                        withBreaks.Add(section);
                        break;
                }
            }
            if (withBreaks.Count == 0 || Unused(used) is not { } free)
                return null;
            mark = free;

            var xml = new BoundedXml();
            foreach (var section in withBreaks)
            {
                var document = xml.Load(section, LoadOptions.PreserveWhitespace);
                var breaks = document.Descendants().Where(IsLineBreak).ToList();
                if (breaks.Count == 0)
                    continue; // the word was in the text
                foreach (var lineBreak in breaks)
                {
                    // In the text where 한글 puts it; elsewhere in a text element of its own.
                    lineBreak.ReplaceWith(lineBreak.Parent?.Name.LocalName == "t"
                        ? new XText(mark.ToString())
                        : new XElement(lineBreak.Name.Namespace + "t", mark.ToString()));
                }
                replaced[section.FullName] = Bytes(document);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            return null;
        }
        if (replaced.Count == 0)
            return null;

        var copy = Path.Combine(EbookFiles.NewFolder(workDirectory, "unhwp-line-breaks"), Path.GetFileName(hwpx));
        PackageRepacker.Repack(hwpx, copy, replaced);
        return (copy, mark);
    }

    /// <summary>
    /// Whether the section's bytes hold "lineBreak" (null when it unpacks to more than any part is read); the
    /// private-use characters it holds go into <paramref name="used"/>.
    /// </summary>
    private static bool? Scan(ZipArchiveEntry section, HashSet<char> used)
    {
        if (section.Length > BoundedXml.MaxPartBytes)
            return null;
        using var stream = section.Open();
        // Room for the end of the previous read: a name or a character split between two reads is still found.
        const int Carry = 8;
        var buffer = new byte[Carry + 81920];
        var kept = 0;
        long total = 0;
        var found = false;
        int count;
        while ((count = stream.Read(buffer, kept, buffer.Length - kept)) > 0)
        {
            total += count;
            if (total > BoundedXml.MaxPartBytes)
                return null;
            var window = buffer.AsSpan(0, kept + count);
            found |= window.IndexOf(LineBreakName) >= 0;
            // U+E000–U+F8FF in UTF-8: EE 80 80 to EF A3 BF.
            for (var i = 0; i + 2 < window.Length; i++)
            {
                if (window[i] is 0xEE or 0xEF && (window[i + 1] & 0xC0) == 0x80 && (window[i + 2] & 0xC0) == 0x80)
                {
                    var c = (char)(((window[i] & 0x0F) << 12) | ((window[i + 1] & 0x3F) << 6) | (window[i + 2] & 0x3F));
                    if (c is >= '' and <= '')
                        used.Add(c);
                }
            }
            kept = Math.Min(Carry, window.Length);
            window[^kept..].CopyTo(buffer);
        }
        return found;
    }

    /// <summary>The last private-use character the document doesn't use.</summary>
    private static char? Unused(HashSet<char> used)
    {
        for (var c = ''; c >= ''; c--)
        {
            if (!used.Contains(c))
                return c;
        }
        return null;
    }

    /// <summary>An hp:lineBreak, whatever its prefix: the HWPML paragraph namespace of any version.</summary>
    private static bool IsLineBreak(XElement element) =>
        element.Name.LocalName == "lineBreak"
        && element.Name.NamespaceName.StartsWith(HwpmlNamespace, StringComparison.Ordinal)
        && element.Name.NamespaceName.EndsWith("/paragraph", StringComparison.Ordinal);

    private static byte[] Bytes(XDocument document)
    {
        using var output = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(output, settings))
            document.Save(writer);
        return output.ToArray();
    }

    // ───────────────────────── Output ─────────────────────────

    /// <summary>Plain text: each line break a line end.</summary>
    public static string Text(string text, char mark) => text.Replace(mark, '\n');

    /// <summary>
    /// Markdown: each line break a hard break (backslash, line end), escaping what would start a block at the start of
    /// the next line; a &lt;br&gt; in a table row or heading, which are one line. Breaks at the end of a paragraph are
    /// dropped (a hard break can't end one). For <paramref name="html"/> the spaces that start a line become no-break
    /// spaces, which HTML doesn't fold away.
    /// </summary>
    public static string Markdown(string markdown, char mark, bool html)
    {
        if (!markdown.Contains(mark))
            return markdown;
        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(mark))
                lines[i] = Line(lines[i], mark, html);
        }
        return string.Join('\n', lines);
    }

    private static string Line(string line, char mark, bool html)
    {
        var start = line.TrimStart();
        var oneLine = start.StartsWith('|') || start.StartsWith('<') || AtxHeading().IsMatch(start);
        var parts = Emphasis(line, mark).Split(mark).ToList();
        if (!oneLine)
        {
            while (parts.Count > 1 && string.IsNullOrWhiteSpace(parts[^1]))
                parts.RemoveAt(parts.Count - 1);
        }

        var sb = new StringBuilder(parts[0]);
        foreach (var part in parts.Skip(1))
        {
            var rest = part.TrimStart(' ', '\t');
            var lead = part[..(part.Length - rest.Length)];
            sb.Append(oneLine ? "<br>" : "\\\n")
                .Append(html ? lead.Replace(" ", "&nbsp;", StringComparison.Ordinal).Replace("\t", "&nbsp;&nbsp;&nbsp;&nbsp;", StringComparison.Ordinal) : lead)
                .Append(oneLine ? rest : EscapeBlockStart(rest));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Moves line breaks out of emphasis that Unhwp closes right after one (<c>**bold⏎**plain</c>) or opens right
    /// before one (<c>plain**⏎bold**</c>): a delimiter at the start or end of a line no longer counts as one.
    /// </summary>
    private static string Emphasis(string line, char mark)
    {
        // Runs of the same character: marks, emphasis delimiters, other text.
        var runs = new List<(string Text, int Kind)>(); // 0 text, 1 mark, 2 opening delimiter, 3 closing delimiter
        var open = new Stack<string>();
        for (var i = 0; i < line.Length;)
        {
            var c = line[i];
            var end = i + 1;
            var delimiter = c is '*' or '_' or '~' && (i == 0 || line[i - 1] != '\\');
            if (c == mark || delimiter)
            {
                while (end < line.Length && line[end] == c)
                    end++;
            }
            else
            {
                while (end < line.Length && line[end] != mark && line[end] is not ('*' or '_' or '~'))
                    end++;
            }
            var text = line[i..end];
            runs.Add((text, c == mark ? 1 : delimiter ? Delimiter(line, i, end, open) : 0));
            i = end;
        }

        // A line break moves to the right of closing delimiters and to the left of opening ones.
        for (var moved = true; moved;)
        {
            moved = false;
            for (var i = 0; i + 1 < runs.Count; i++)
            {
                if ((runs[i].Kind == 1 && runs[i + 1].Kind == 3) || (runs[i].Kind == 2 && runs[i + 1].Kind == 1))
                {
                    (runs[i], runs[i + 1]) = (runs[i + 1], runs[i]);
                    moved = true;
                }
            }
        }
        return string.Concat(runs.Select(r => r.Text));
    }

    /// <summary>
    /// Whether the delimiter run <c>line[start..end]</c> closes emphasis opened before it (3), opens emphasis (2) or is
    /// text (0); a line break counts as a letter, as it does for Unhwp's Markdown.
    /// </summary>
    private static int Delimiter(string line, int start, int end, Stack<string> open)
    {
        var run = line[start..end];
        var before = start > 0 ? line[start - 1] : ' ';
        var after = end < line.Length ? line[end] : ' ';
        if (run[0] == '_' && char.IsLetterOrDigit(before) && char.IsLetterOrDigit(after))
            return 0; // snake_case
        if (!char.IsWhiteSpace(before) && open.TryPeek(out var top) && top == run)
        {
            open.Pop();
            return 3;
        }
        if (!char.IsWhiteSpace(after))
        {
            open.Push(run);
            return 2;
        }
        return 0;
    }

    /// <summary>A backslash before what would start a block (heading, quote, list, fence, rule, HTML, definition) at the start of a line.</summary>
    private static string EscapeBlockStart(string text)
    {
        if (text.Length == 0)
            return text;
        if (OrderedListMarker().Match(text) is { Success: true } ordered)
            return text.Insert(ordered.Length - 1, "\\");
        return BlockStart().IsMatch(text) ? "\\" + text : text;
    }

    [GeneratedRegex(@"^#{1,6}(?:[ \t]|$)")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^\d{1,9}[.)](?=[ \t]|$)")]
    private static partial Regex OrderedListMarker();

    [GeneratedRegex(@"^(?:#{1,6}(?:[ \t]|$)|[-+*](?:[ \t]|$)|>|`{3}|~{3}|=+[ \t]*$|-+[ \t]*$|([*_])[ \t]*(?:\1[ \t]*){2,}$|<[A-Za-z/!?]|:(?:[ \t]|$)|\^\^)")]
    private static partial Regex BlockStart();
}
