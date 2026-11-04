// Takes out what a document only links to before LibreOffice opens it: linked pictures, OLE objects, an attached
// template, linked sections and the like that point at another file, a share (\\host\share, file://host/) or a web
// address. LibreOffice would read those while converting: a local picture outside the document would end up in the
// result, and a share or address would be contacted (for a share, possibly with the user's Windows credentials).
//
// The worker profiles already block most of it (LibreOfficeProfile: BlockUntrustedRefererLinks, no link updates), but
// not everything: the RTF reader loads an INCLUDEPICTURE field's file by itself, whatever the profile says. And a
// LibreOffice the user picked may be older or set up differently. So the copy LibreOffice gets has no such links:
//  * Office Open XML: relationships with TargetMode="External", except hyperlinks;
//  * OpenDocument (packages and flat XML): xlink:href outside the package on anything but hyperlinks and event
//    listeners (pictures, objects, section and table sources, sounds, fill images);
//  * HTML: picture, frame, object and style sheet addresses and CSS url() other than data: URIs (hyperlinks stay);
//  * RTF: INCLUDEPICTURE fields (their last result stays, as for any field LibreOffice doesn't know).
// The user's own file is never changed: the result is a copy under the same name in a folder of its own.

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Office;

internal static partial class LinkedContent
{
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    /// <summary>Elements whose xlink:href is a hyperlink or a macro binding, not content: they keep it.</summary>
    private static readonly HashSet<XName> KeepHref =
    [
        XName.Get("a", "urn:oasis:names:tc:opendocument:xmlns:text:1.0"),
        XName.Get("a", "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"),
        XName.Get("event-listener", "urn:oasis:names:tc:opendocument:xmlns:script:1.0"),
        XName.Get("event-listener", "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0"),
    ];

    /// <summary>
    /// A copy of <paramref name="input"/> (format <paramref name="from"/>) without links to content outside it, in a new
    /// folder under <paramref name="workDirectory"/>; null when it has none, or is a format this doesn't look into
    /// (binary Office files are left to the profile).
    /// </summary>
    public static string? Strip(string input, string from, string workDirectory)
    {
        byte[] head = new byte[8];
        using (var stream = File.OpenRead(input))
            head = head[..stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
        if (head is [(byte)'P', (byte)'K', ..])
            return StripPackage(input, workDirectory);
        if (Encoding.ASCII.GetString(head).StartsWith(@"{\rtf", StringComparison.Ordinal))
            return Write(input, workDirectory, StripRtf(File.ReadAllBytes(input)));
        if (from is "fodt" or "fods" or "fodp" or "fodg")
            return Write(input, workDirectory, StripOpenDocument(File.ReadAllBytes(input), entries: null));
        if (from == "html")
            return Write(input, workDirectory, StripHtml(File.ReadAllBytes(input)));
        return null;
    }

    private static string? StripPackage(string input, string workDirectory)
    {
        var replaced = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var zip = ZipFile.OpenRead(input);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            var openDocument = names.Contains("mimetype");
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                var isPart = openDocument
                    ? name.EndsWith("content.xml", StringComparison.Ordinal) || name.EndsWith("styles.xml", StringComparison.Ordinal)
                    : name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
                if (!isPart)
                    continue;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var stripped = openDocument ? StripOpenDocument(buffer.ToArray(), names) : StripRelationships(buffer.ToArray());
                if (stripped is not null)
                    replaced[name] = stripped;
            }
        }
        catch (InvalidDataException)
        {
            return null; // not a ZIP LibreOffice could read either
        }
        if (replaced.Count == 0)
            return null;
        var output = Target(input, workDirectory);
        PackageRepacker.Repack(input, output, replaced);
        return output;
    }

    /// <summary>An Office Open XML .rels part without its external relationships other than hyperlinks, or null.</summary>
    internal static byte[]? StripRelationships(byte[] part)
    {
        if (Load(part) is not { Root: { } root } document)
            return null;
        var external = root.Elements(Relationships + "Relationship")
            .Where(r => string.Equals((string?)r.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                        && !((string?)r.Attribute("Type") ?? "").EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (external.Count == 0)
            return null;
        external.ForEach(r => r.Remove());
        return Save(document);
    }

    /// <summary>
    /// An OpenDocument part (or flat document) without xlink:href outside the package, or null when it has none.
    /// <paramref name="entries"/> are the package's part names; a flat document has none, so every address is outside.
    /// </summary>
    internal static byte[]? StripOpenDocument(byte[] part, IReadOnlySet<string>? entries)
    {
        if (Load(part) is not { Root: { } root } document)
            return null;
        var outside = root.DescendantsAndSelf()
            .Where(e => !KeepHref.Contains(e.Name))
            .Select(e => e.Attribute(XLink + "href"))
            .OfType<XAttribute>()
            .Where(href => IsOutside(href.Value, entries))
            .ToList();
        if (outside.Count == 0)
            return null;
        outside.ForEach(href => href.Remove());
        return Save(document);
    }

    /// <summary>True for an address that isn't a part of the package: a URL, an absolute or parent path, a missing part.</summary>
    private static bool IsOutside(string href, IReadOnlySet<string>? entries)
    {
        var value = href.Trim();
        if (value.Length == 0 || value.StartsWith('#'))
            return false;
        // Old OpenOffice files name their pictures inside the package this way.
        if (value.StartsWith("vnd.sun.star.Package:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vnd.sun.star.GraphicObject:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Scheme().IsMatch(value) || value.StartsWith('/') || value.StartsWith('\\'))
            return true;
        var path = Uri.UnescapeDataString(value).Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (entries is null || path.Split('/').Contains(".."))
            return true;
        path = path.TrimEnd('/');
        return !entries.Contains(path) && !entries.Any(e => e.StartsWith(path + "/", StringComparison.Ordinal));
    }

    /// <summary>HTML without addresses of pictures, frames, objects and style sheets other than data: URIs, or null.</summary>
    internal static byte[]? StripHtml(byte[] html)
    {
        // Latin-1 keeps every byte as it is, whatever the page's encoding: only ASCII markup is changed.
        var text = Encoding.Latin1.GetString(html);
        var stripped = Tag().Replace(text, tag =>
        {
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            var attributes = Attribute().Replace(tag.Groups["attributes"].Value, attribute =>
            {
                var key = attribute.Groups["key"].Value.ToLowerInvariant();
                var value = WebUtility.HtmlDecode(attribute.Groups["value"].Value.Trim('"', '\''));
                if (key == "style")
                    return CssUrls(value) is var css && css == value ? attribute.Value : $"{attribute.Groups["space"].Value}style=\"{WebUtility.HtmlEncode(css)}\"";
                var resource = key is "src" or "srcset" or "lowsrc" or "dynsrc" or "data" or "poster" or "background" or "codebase"
                                   or "xlink:href" or "longdesc"
                               || key == "href" && name is "link" or "base" or "image" or "use";
                return resource && !value.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? "" : attribute.Value;
            });
            return $"<{tag.Groups["name"].Value}{attributes}{tag.Groups["end"].Value}>";
        });
        stripped = StyleBlock().Replace(stripped, block => block.Groups["open"].Value + CssUrls(block.Groups["css"].Value) + block.Groups["close"].Value);
        return stripped == text ? null : Encoding.Latin1.GetBytes(stripped);
    }

    /// <summary>CSS with url() other than data: emptied and @import rules taken out.</summary>
    private static string CssUrls(string css) =>
        Import().Replace(CssUrl().Replace(css, url =>
            url.Groups["address"].Value.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? url.Value : "url()"), "");

    /// <summary>RTF whose INCLUDEPICTURE fields are renamed to a field LibreOffice doesn't know, or null without any.</summary>
    internal static byte[]? StripRtf(byte[] rtf)
    {
        var text = Encoding.Latin1.GetString(rtf);
        var builder = new StringBuilder(text.Length);
        var changed = false;
        var at = 0;
        // LibreOffice takes the field type from the first word of the instruction, after any control words.
        for (var start = text.IndexOf(@"\fldinst", StringComparison.Ordinal); start >= 0;
             start = text.IndexOf(@"\fldinst", at, StringComparison.Ordinal))
        {
            var end = text.IndexOf(@"\fldrslt", start, StringComparison.Ordinal);
            end = end < 0 ? text.Length : end;
            var instruction = text[start..end];
            var renamed = IncludePicture().Replace(instruction, "FILEEREMOVED");
            builder.Append(text, at, start - at).Append(renamed);
            changed |= renamed != instruction;
            at = end;
        }
        builder.Append(text, at, text.Length - at);
        return changed ? Encoding.Latin1.GetBytes(builder.ToString()) : null;
    }

    private static string? Write(string input, string workDirectory, byte[]? content)
    {
        if (content is null)
            return null;
        var output = Target(input, workDirectory);
        File.WriteAllBytes(output, content);
        return output;
    }

    /// <summary>The copy's path: a new folder, the same name (LibreOffice names its result after it).</summary>
    private static string Target(string input, string workDirectory)
    {
        var folder = Path.Combine(workDirectory, "lo-unlinked-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Path.GetFileName(input));
    }

    private static XDocument? Load(byte[] part)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(part),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null; // LibreOffice can't read the part either
        }
    }

    private static byte[] Save(XDocument document)
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            document.Save(writer);
        return buffer.ToArray();
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:")]
    private static partial Regex Scheme();

    [GeneratedRegex("""<(?<name>[A-Za-z][\w:.\-]*)(?<attributes>(?:\s+[^\s=>/]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+))?)*)(?<end>\s*/?)>""")]
    private static partial Regex Tag();

    [GeneratedRegex("""(?<space>\s+)(?<key>[^\s=>/]+)(?:\s*=\s*(?<value>"[^"]*"|'[^']*'|[^\s>]+))?""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"(?<open><style\b[^>]*>)(?<css>.*?)(?<close></style\s*>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlock();

    [GeneratedRegex("""url\(\s*(?<quote>["']?)(?<address>.*?)\k<quote>\s*\)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CssUrl();

    [GeneratedRegex("""@import\s+(?:url\(\))?\s*(?:"[^"]*"|'[^']*')?[^;]*;?""", RegexOptions.IgnoreCase)]
    private static partial Regex Import();

    [GeneratedRegex("INCLUDEPICTURE", RegexOptions.IgnoreCase)]
    private static partial Regex IncludePicture();
}
