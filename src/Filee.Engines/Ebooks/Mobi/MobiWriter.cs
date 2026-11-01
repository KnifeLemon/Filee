// Book → AZW3 (KF8) and MOBI (MOBI 6 + KF8 in one file), written from the MobileRead wiki's format description. The
// content is the same XHTML the EPUB writer makes (XhtmlWriter: chapters split at level-1 headings, one style sheet);
// MobiReader reads it back into the same document model.
//
// KF8 part:
//  * each chapter file is cut into a skeleton (the page without the inside of <body aid="0">) and fragments (the
//    body's content in pieces of about 8 KB, cut between its top-level elements); the text stream holds every
//    skeleton followed by its fragments, and the SKEL and FRAG indexes say where each piece goes;
//  * links inside the book become kindle:pos:fid:XXXX:off:YYYYYYYYYY (a fragment and an offset in it, base 32),
//    pictures kindle:embed:XXXX (resource numbers from 1), the style sheet is flow 1 (kindle:flow:0001), listed in
//    the FDST record;
//  * the NCX index holds the table of contents (the two highest heading levels, nested), with the labels in CNCX;
//  * EXTH metadata: title, authors, publisher, description, language, cover and thumbnail, document type EBOK.
// MOBI 6 part (.mobi only, for Kindles and apps from before KF8): one HTML document with <mbp:pagebreak/> between
// the chapters, filepos= links (byte offsets, ten digits), recindex pictures and a contents page that the guide
// points at. The pictures are stored once, before the BOUNDARY record that separates the two parts; EXTH 121 in the
// MOBI 6 header names the KF8 header's record.
// Text records hold 4,096 bytes each, PalmDOC-compressed, with the multibyte overlap entry for UTF-8 characters that
// a record boundary cuts.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static partial class MobiWriter
{
    private const int RecordSize = 4096;
    private const int FragmentSize = 8192;
    private const string Selector = "P-//*[@aid='0']";
    private const string PosPlaceholder = "kindle:pos:fid:0000:off:0000000000";
    private const string FileposPlaceholder = "filepos=0000000000";

    /// <summary>Writes an AZW3 (<paramref name="mobi6"/> false) or a MOBI with both parts (true).</summary>
    public static void Write(Book book, string outputPath, string sourcePath, string workFolder, bool mobi6)
    {
        var title = book.DisplayTitle(sourcePath);
        var language = book.DisplayLanguage();
        var resources = new Resources(EbookFiles.NewFolder(workFolder, "kindle-images"));

        var chapters = XhtmlWriter.Write(book.Document, new XhtmlOptions
        {
            SplitChapters = true,
            ChapterFile = PartName,
            ImageSource = path => resources.Add(path) is { } index ? $"kindle:embed:{Base32(index + 1, 4)}?mime={resources.MediaType(index)}" : null,
            Epub = true,
        });
        var kf8 = Kf8Text.Build(chapters, title, language);

        // MOBI 6 numbers its pictures the same way: the pictures come in the same order.
        string? html = null;
        if (mobi6)
        {
            var mobiChapters = XhtmlWriter.Write(book.Document, new XhtmlOptions
            {
                SplitChapters = true,
                ChapterFile = PartName,
                ImageSource = path => resources.Add(path) is { } index ? $"recindex:{index + 1:00000}" : null,
            });
            html = Mobi6Html(mobiChapters, title, language);
        }

        int? cover = null, thumbnail = null;
        if (book.Metadata.CoverImage is { } coverFile && File.Exists(coverFile) && resources.Add(coverFile) is { } coverIndex)
        {
            cover = coverIndex;
            thumbnail = resources.AddThumbnail(coverIndex);
        }

        var metadata = Exth(book.Metadata, title, language, resources.Count, cover, thumbnail);
        var uniqueId = (uint)Random.Shared.Next(1, int.MaxValue);
        var records = new List<byte[]> { Array.Empty<byte>() };
        if (html is not null)
        {
            var text = Encoding.UTF8.GetBytes(html);
            records.AddRange(TextRecords(text));
            var textRecords = records.Count - 1;
            var firstImage = records.Count;
            records.AddRange(resources.Records());
            var lastContent = records.Count - 1;
            records.Add("BOUNDARY"u8.ToArray());
            var kf8Start = records.Count;
            records[0] = Record0(new HeaderFields
            {
                Version = 6,
                TextLength = text.Length,
                TextRecords = textRecords,
                FirstNonBook = textRecords + 1,
                FirstImage = resources.Count > 0 ? firstImage : -1,
                LastContent = lastContent,
                UniqueId = uniqueId,
                Language = language,
                Title = title,
                Exth = [.. metadata, (121, Be32(kf8Start))],
            });
            AddKf8(records, kf8, metadata, title, language, uniqueId, kf8Start, firstImage: resources.Count > 0 ? firstImage : -1, resources: null);
        }
        else
        {
            AddKf8(records, kf8, metadata, title, language, uniqueId, 0, firstImage: -1, resources);
        }
        records.Add([0xE9, 0x8E, 0x0D, 0x0A]); // end of file
        File.WriteAllBytes(outputPath, PalmDatabaseFile(title, records));
    }

    /// <summary>The KF8 part from record <paramref name="start"/>: header, text, indexes, pictures (AZW3), FDST, FLIS, FCIS.</summary>
    private static void AddKf8(List<byte[]> records, Kf8Text kf8, List<(int Type, byte[] Data)> metadata, string title, string language,
        uint uniqueId, int start, int firstImage, Resources? resources)
    {
        var header = start == 0 ? 0 : records.Count;
        if (start != 0)
            records.Add([]);
        var text = kf8.Text;
        records.AddRange(TextRecords(text));
        var textRecords = records.Count - header - 1;

        var cncx = new CncxWriter();
        var selector = cncx.Add(Selector);
        var fragmentIndex = records.Count - start;
        records.AddRange(MobiIndexWriter.Write(
            [new IndexTag(2, 1, 1), new IndexTag(3, 1, 2), new IndexTag(4, 1, 4), new IndexTag(6, 2, 8)],
            kf8.Fragments.Select((f, i) => new IndexEntry(f.InsertPosition.ToString("0000000000", CultureInfo.InvariantCulture), new Dictionary<int, long[]>
            {
                [2] = [selector],
                [3] = [f.File],
                [4] = [i],
                [6] = [f.StartInFile, f.Length],
            })).ToList(), cncx));
        var skeletonIndex = records.Count - start;
        records.AddRange(MobiIndexWriter.Write(
            [new IndexTag(1, 1, 3), new IndexTag(6, 2, 12)],
            kf8.Skeletons.Select((s, i) => new IndexEntry($"SKEL{i:0000000000}", new Dictionary<int, long[]>
            {
                [1] = [s.Fragments],
                [6] = [s.Start, s.Length, s.Start, s.Length],
            })).ToList()));
        var ncxIndex = -1;
        if (kf8.Toc.Count > 0)
        {
            ncxIndex = records.Count - start;
            records.AddRange(Ncx(kf8));
        }
        if (resources is not null && resources.Count > 0)
        {
            firstImage = records.Count;
            records.AddRange(resources.Records());
        }
        var fdst = records.Count - start;
        records.Add(Fdst(kf8.FlowEnds));
        var flis = records.Count - start;
        records.Add(Flis());
        var fcis = records.Count - start;
        records.Add(Fcis(text.Length));

        var exth = new List<(int, byte[])>(metadata);
        if (metadata.Any(m => m.Type == 201))
            exth.Add((129, Encoding.ASCII.GetBytes($"kindle:embed:{Base32(BinaryPrimitives.ReadInt32BigEndian(metadata.First(m => m.Type == 201).Data) + 1, 4)}")));
        records[header] = Record0(new HeaderFields
        {
            Version = 8,
            TextLength = text.Length,
            TextRecords = textRecords,
            FirstNonBook = textRecords + 1,
            FirstImage = firstImage,
            UniqueId = uniqueId,
            Language = language,
            Title = title,
            Exth = exth,
            Fdst = fdst,
            FdstCount = kf8.FlowEnds.Count,
            Fcis = fcis,
            Flis = flis,
            Ncx = ncxIndex,
            Fragment = fragmentIndex,
            Skeleton = skeletonIndex,
        });
    }

    private static string PartName(int index) => $"part{index:0000}.xhtml";

    /// <summary>Kindle's base-32 digits (0-9, A-V), zero-padded to <paramref name="width"/>.</summary>
    internal static string Base32(long value, int width)
    {
        var digits = new char[width];
        for (var i = width - 1; i >= 0; i--, value >>= 5)
            digits[i] = "0123456789ABCDEFGHIJKLMNOPQRSTUV"[(int)(value & 31)];
        return new string(digits);
    }

    // ───────────────────────── KF8 text ─────────────────────────

    private sealed record Skeleton(long Start, long Length, int Fragments);

    private sealed record Fragment(int File, long InsertPosition, long StartInFile, long Length);

    /// <summary>A table of contents entry: its label, depth (0 = top) and the fragment and offset it points at.</summary>
    internal sealed record TocEntry(string Label, int Depth, int Fid, long Offset, long Position);

    /// <summary>The text stream of the KF8 part and the tables that describe it.</summary>
    private sealed class Kf8Text
    {
        public byte[] Text { get; private init; } = [];
        public List<long> FlowEnds { get; } = [];
        public List<Skeleton> Skeletons { get; } = [];
        public List<Fragment> Fragments { get; } = [];
        public List<TocEntry> Toc { get; } = [];

        public static Kf8Text Build(IReadOnlyList<XhtmlChapter> chapters, string title, string language)
        {
            // Pages with placeholders for links into the book: (file, target file, target id) in order.
            var links = new List<List<(int File, string? Id)>>();
            var files = new List<byte[]>();
            for (var i = 0; i < chapters.Count; i++)
            {
                var chapter = chapters[i];
                var page = XhtmlWriter.Page(chapter.Title ?? title, chapter.Body, language, "kindle:flow:0001?mime=text/css", epub: true)
                    .Replace("<body>", "<body aid=\"0\">", StringComparison.Ordinal);
                var targets = new List<(int, string?)>();
                var number = i;
                page = InternalLink().Replace(page, m =>
                {
                    targets.Add(Target(m.Groups[1].Value, number));
                    return $"href=\"{PosPlaceholder}\"";
                });
                links.Add(targets);
                files.Add(Encoding.UTF8.GetBytes(page));
            }

            // Skeletons and fragments; the text stream holds each skeleton followed by its fragments.
            var kf8 = new Kf8Text();
            var text = new MemoryStream();
            var layout = new List<(long FileStart, int PrefixLength, int BodyLength, int FirstFragment)>();
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var bodyStart = file.AsSpan().IndexOf("<body aid=\"0\">"u8) + "<body aid=\"0\">"u8.Length;
                var bodyEnd = file.AsSpan().LastIndexOf("</body>"u8);
                var start = text.Length;
                text.Write(file.AsSpan(0, bodyStart));
                text.Write(file.AsSpan(bodyEnd));
                var cuts = Cuts(file.AsSpan(bodyStart, bodyEnd - bodyStart));
                layout.Add((start, bodyStart, bodyEnd - bodyStart, kf8.Fragments.Count));
                kf8.Skeletons.Add(new Skeleton(start, bodyStart + file.Length - bodyEnd, cuts.Count - 1));
                for (var c = 0; c + 1 < cuts.Count; c++)
                {
                    kf8.Fragments.Add(new Fragment(i, start + bodyStart + cuts[c], cuts[c], cuts[c + 1] - cuts[c]));
                    text.Write(file.AsSpan(bodyStart + cuts[c], cuts[c + 1] - cuts[c]));
                }
            }

            // Where a link lands: the fragment holding the tag with that id, and the offset of the tag in it.
            var ids = files.Select(IdPositions).ToList();
            (int Fid, long Offset) Locate(int file, string? id)
            {
                var (_, prefix, bodyLength, first) = layout[file];
                var position = id is not null && ids[file].TryGetValue(id, out var at) ? at - prefix : 0;
                position = Math.Clamp(position, 0, Math.Max(0, bodyLength - 1));
                var fid = first;
                while (fid + 1 < kf8.Fragments.Count && kf8.Fragments[fid + 1].File == file && kf8.Fragments[fid + 1].StartInFile <= position)
                    fid++;
                return (fid, position - kf8.Fragments[fid].StartInFile);
            }
            var bytes = text.ToArray();
            for (var i = 0; i < files.Count; i++)
            {
                var placeholders = Occurrences(files[i], Encoding.ASCII.GetBytes(PosPlaceholder));
                for (var k = 0; k < placeholders.Count && k < links[i].Count; k++)
                {
                    var (fid, offset) = Locate(links[i][k].File, links[i][k].Id);
                    var link = Encoding.ASCII.GetBytes($"kindle:pos:fid:{Base32(fid, 4)}:off:{Base32(offset, 10)}");
                    // The placeholder's place in the text stream: in the skeleton or in a fragment.
                    var at = placeholders[k];
                    var (fileStart, prefix, bodyLength, first) = layout[i];
                    long streamAt;
                    if (at < prefix)
                    {
                        streamAt = fileStart + at;
                    }
                    else if (at >= prefix + bodyLength)
                    {
                        streamAt = fileStart + prefix + (at - prefix - bodyLength);
                    }
                    else
                    {
                        var f = first;
                        while (f + 1 < kf8.Fragments.Count && kf8.Fragments[f + 1].File == i && kf8.Fragments[f + 1].StartInFile <= at - prefix)
                            f++;
                        streamAt = FragmentStreamStart(kf8, layout, f) + (at - prefix - kf8.Fragments[f].StartInFile);
                    }
                    link.CopyTo(bytes, streamAt);
                }
            }

            // The table of contents: the two highest heading levels.
            var headings = chapters.SelectMany(c => c.Headings).Where(h => h.Text.Length > 0).ToList();
            if (headings.Count > 0)
            {
                var top = headings.Min(h => h.Level);
                foreach (var heading in headings.Where(h => h.Level <= top + 1))
                {
                    var (file, id) = Target(heading.Target, 0);
                    var (fid, offset) = Locate(file, id);
                    kf8.Toc.Add(new TocEntry(heading.Text, heading.Level - top, fid, offset, kf8.Fragments[fid].InsertPosition + offset));
                }
            }
            else
            {
                for (var i = 0; i < chapters.Count; i++)
                {
                    var label = i == 0 ? title : chapters[i].PlainStart.Length > 0 ? chapters[i].PlainStart + "…" : $"{i + 1}";
                    var (fid, offset) = Locate(i, null);
                    kf8.Toc.Add(new TocEntry(label, 0, fid, offset, kf8.Fragments[fid].InsertPosition + offset));
                }
            }

            var css = Encoding.UTF8.GetBytes(XhtmlWriter.Css);
            kf8.FlowEnds.Add(bytes.Length);
            kf8.FlowEnds.Add(bytes.Length + css.Length);
            return new Kf8Text
            {
                Text = [.. bytes, .. css],
            }.With(kf8);
        }

        private Kf8Text With(Kf8Text tables)
        {
            FlowEnds.AddRange(tables.FlowEnds);
            Skeletons.AddRange(tables.Skeletons);
            Fragments.AddRange(tables.Fragments);
            Toc.AddRange(tables.Toc);
            return this;
        }

        private static long FragmentStreamStart(Kf8Text kf8, List<(long FileStart, int PrefixLength, int BodyLength, int FirstFragment)> layout, int fid)
        {
            var fragment = kf8.Fragments[fid];
            var skeleton = kf8.Skeletons[fragment.File];
            return layout[fragment.File].FileStart + skeleton.Length + fragment.StartInFile;
        }
    }

    /// <summary>(file, id) of an internal link: "#id" in <paramref name="file"/>, "part0003.xhtml#id" or "part0003.xhtml".</summary>
    private static (int File, string? Id) Target(string href, int file)
    {
        var hash = href.IndexOf('#');
        var name = hash < 0 ? href : href[..hash];
        var id = hash < 0 || hash == href.Length - 1 ? null : System.Net.WebUtility.HtmlDecode(href[(hash + 1)..]);
        if (name.Length > 0 && int.TryParse(name.AsSpan(4, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            file = number;
        return (file, id);
    }

    [GeneratedRegex(@"href=""((?:part\d{4}\.xhtml)?#[^""]*|part\d{4}\.xhtml)""")]
    private static partial Regex InternalLink();

    /// <summary>
    /// Fragment boundaries in a body: 0, positions between top-level elements once a fragment has about 8 KB, and the
    /// end. Text between tags and nested elements always stay inside one fragment.
    /// </summary>
    private static List<int> Cuts(ReadOnlySpan<byte> body)
    {
        var cuts = new List<int> { 0 };
        var depth = 0;
        for (var i = 0; i < body.Length;)
        {
            if (body[i] != '<')
            {
                i++;
                continue;
            }
            var end = body[i..].IndexOf((byte)'>');
            if (end < 0)
                break;
            end += i;
            if (body[i..].StartsWith("<!--"u8))
            {
                var close = body[i..].IndexOf("-->"u8);
                end = close < 0 ? body.Length - 1 : i + close + 2;
            }
            else if (i + 1 < body.Length && body[i + 1] == '/')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (body[end - 1] != '/' && i + 1 < body.Length && body[i + 1] is not ((byte)'!' or (byte)'?'))
            {
                depth++;
            }
            i = end + 1;
            if (depth == 0 && i - cuts[^1] >= FragmentSize && i < body.Length)
                cuts.Add(i);
        }
        cuts.Add(body.Length);
        return cuts;
    }

    /// <summary>Byte position of the tag that carries each id attribute.</summary>
    private static Dictionary<string, long> IdPositions(byte[] file)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var at in Occurrences(file, " id=\""u8.ToArray()))
        {
            var start = at + 5;
            var end = Array.IndexOf(file, (byte)'"', start);
            var tag = Array.LastIndexOf(file, (byte)'<', at);
            if (end > start && tag >= 0)
                ids.TryAdd(System.Net.WebUtility.HtmlDecode(Encoding.UTF8.GetString(file, start, end - start)), tag);
        }
        return ids;
    }

    private static List<int> Occurrences(byte[] data, byte[] needle)
    {
        var found = new List<int>();
        for (var at = 0; at <= data.Length - needle.Length;)
        {
            var next = data.AsSpan(at).IndexOf(needle);
            if (next < 0)
                break;
            found.Add(at + next);
            at += next + needle.Length;
        }
        return found;
    }

    /// <summary>The NCX index: entries by depth (all top-level entries first), with parent and child links.</summary>
    private static List<byte[]> Ncx(Kf8Text kf8)
    {
        var toc = kf8.Toc;
        var parents = new int[toc.Count];
        for (var i = 0; i < toc.Count; i++)
        {
            parents[i] = -1;
            if (toc[i].Depth > 0)
                for (var p = i - 1; p >= 0; p--)
                    if (toc[p].Depth < toc[i].Depth)
                    {
                        parents[i] = p;
                        break;
                    }
        }
        // Order: depth 0 in reading order, then depth 1, ... (each child group in its parent's order).
        var order = Enumerable.Range(0, toc.Count).OrderBy(i => toc[i].Depth).ThenBy(i => i).ToList();
        var indexOf = new int[toc.Count];
        for (var n = 0; n < order.Count; n++)
            indexOf[order[n]] = n;
        var cncx = new CncxWriter();
        var end = kf8.FlowEnds[0];
        var entries = new List<IndexEntry>();
        foreach (var i in order)
        {
            var entry = toc[i];
            var next = toc.Skip(i + 1).Select(t => t.Position).Where(p => p > entry.Position).DefaultIfEmpty(end).First();
            var values = new Dictionary<int, long[]>
            {
                [1] = [entry.Position],
                [2] = [Math.Max(1, next - entry.Position)],
                [3] = [cncx.Add(entry.Label)],
                [4] = [entry.Depth],
                [6] = [entry.Fid, entry.Offset],
            };
            if (parents[i] >= 0)
                values[21] = [indexOf[parents[i]]];
            var children = Enumerable.Range(0, toc.Count).Where(c => parents[c] == i).Select(c => indexOf[c]).ToList();
            if (children.Count > 0)
            {
                values[22] = [children.Min()];
                values[23] = [children.Max()];
            }
            entries.Add(new IndexEntry(entries.Count.ToString("X3", CultureInfo.InvariantCulture), values));
        }
        return MobiIndexWriter.Write(
            [new IndexTag(1, 1, 1), new IndexTag(2, 1, 2), new IndexTag(3, 1, 4), new IndexTag(4, 1, 8), new IndexTag(6, 2, 16),
             new IndexTag(21, 1, 32), new IndexTag(22, 1, 64), new IndexTag(23, 1, 128)],
            entries, cncx);
    }

    // ───────────────────────── MOBI 6 ─────────────────────────

    /// <summary>
    /// One MOBI 6 HTML document: a contents page, then the chapters with page breaks between them. Links become
    /// filepos= placeholders that are filled with byte offsets once the text is final.
    /// </summary>
    private static string Mobi6Html(IReadOnlyList<XhtmlChapter> chapters, string title, string language)
    {
        var targets = new List<(int File, string? Id)>();
        string Filepos((int File, string? Id) target)
        {
            targets.Add(target);
            return FileposPlaceholder;
        }
        var sb = new StringBuilder();
        sb.Append("<html><head><guide>");
        var headings = chapters.SelectMany(c => c.Headings).Where(h => h.Text.Length > 0).ToList();
        var contents = headings.Count > 0;
        if (contents)
            sb.Append($"<reference type=\"toc\" title=\"{XhtmlWriter.Escape(ContentsLabel(language))}\" {Filepos((-1, "mobi6-toc"))} />");
        sb.Append($"<reference type=\"text\" title=\"{XhtmlWriter.Escape(title)}\" {Filepos((0, null))} />");
        sb.Append("</guide></head><body>\n");
        if (contents)
        {
            var top = headings.Min(h => h.Level);
            sb.Append("<a id=\"mobi6-toc\"></a><h2>").Append(XhtmlWriter.Escape(ContentsLabel(language))).Append("</h2>\n");
            foreach (var heading in headings.Where(h => h.Level <= top + 1))
            {
                var indent = heading.Level > top ? " style=\"margin-left:2em\"" : "";
                sb.Append($"<p{indent}><a {Filepos(Target(heading.Target, 0))}>{XhtmlWriter.Escape(heading.Text)}</a></p>\n");
            }
            sb.Append("<mbp:pagebreak/>\n");
        }
        for (var i = 0; i < chapters.Count; i++)
        {
            if (i > 0)
                sb.Append("<mbp:pagebreak/>\n");
            var number = i;
            var body = InternalLink().Replace(chapters[i].Body, m => Filepos(Target(m.Groups[1].Value, number)));
            body = RecIndexSource().Replace(body, m => $"recindex=\"{m.Groups[1].Value}\"");
            sb.Append($"<a id=\"mobi6-part-{i}\"></a>").Append(body);
        }
        sb.Append("</body></html>\n");

        // Fill in the offsets: the tag with the id, or the start of the chapter.
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var ids = IdPositions(bytes);
        var placeholders = Occurrences(bytes, Encoding.ASCII.GetBytes(FileposPlaceholder));
        for (var k = 0; k < placeholders.Count && k < targets.Count; k++)
        {
            var (file, id) = targets[k];
            var position = id is not null && ids.TryGetValue(id, out var at) ? at
                : ids.GetValueOrDefault($"mobi6-part-{Math.Max(0, file)}", 0);
            Encoding.ASCII.GetBytes($"filepos={position:0000000000}").CopyTo(bytes, placeholders[k]);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    [GeneratedRegex(@"src=""recindex:(\d{5})""")]
    private static partial Regex RecIndexSource();

    private static string ContentsLabel(string language) => language.Split('-')[0].ToLowerInvariant() switch
    {
        "ko" => "목차",
        "zh" => "目录",
        "ja" => "目次",
        _ => "Contents",
    };

    // ───────────────────────── Records ─────────────────────────

    /// <summary>Text cut into 4,096-byte records, each PalmDOC-compressed with its multibyte overlap entry.</summary>
    private static List<byte[]> TextRecords(byte[] text)
    {
        var records = new List<byte[]>();
        for (var offset = 0; offset < text.Length; offset += RecordSize)
        {
            var length = Math.Min(RecordSize, text.Length - offset);
            var compressed = PalmDocCompression.Compress(text.AsSpan(offset, length));
            // The bytes of a UTF-8 character that continue in the next record, then their count.
            var overlap = 0;
            var end = offset + length;
            if (end < text.Length)
            {
                var lead = end - 1;
                while (lead > offset && (text[lead] & 0xC0) == 0x80)
                    lead--;
                var size = text[lead] >= 0xF0 ? 4 : text[lead] >= 0xE0 ? 3 : text[lead] >= 0xC0 ? 2 : 1;
                overlap = Math.Clamp(lead + size - end, 0, 3);
            }
            records.Add([.. compressed, .. text.AsSpan(end, overlap), (byte)overlap]);
        }
        return records;
    }

    private sealed class HeaderFields
    {
        public int Version { get; init; }
        public long TextLength { get; init; }
        public int TextRecords { get; init; }
        public int FirstNonBook { get; init; }
        public int FirstImage { get; init; } = -1;
        public int LastContent { get; init; } = -1;
        public uint UniqueId { get; init; }
        public required string Language { get; init; }
        public required string Title { get; init; }
        public required List<(int Type, byte[] Data)> Exth { get; init; }
        public int Fdst { get; init; } = -1;
        public int FdstCount { get; init; }
        public int Fcis { get; init; } = -1;
        public int Flis { get; init; } = -1;
        public int Ncx { get; init; } = -1;
        public int Fragment { get; init; } = -1;
        public int Skeleton { get; init; } = -1;
    }

    /// <summary>Record 0: the PalmDOC header, the MOBI header (version 6 or 8), EXTH and the full title.</summary>
    private static byte[] Record0(HeaderFields f)
    {
        var mobiLength = f.Version >= 8 ? 0x108 : 0xE8;
        var header = new byte[16 + mobiLength];
        var span = header.AsSpan();
        void U32(int offset, long value) => BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(offset), value < 0 ? uint.MaxValue : (uint)value);
        BinaryPrimitives.WriteUInt16BigEndian(span, 2); // PalmDOC compression
        U32(4, f.TextLength);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)f.TextRecords);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], RecordSize);
        "MOBI"u8.CopyTo(span[16..]);
        U32(0x14, mobiLength);
        U32(0x18, 2); // book
        U32(0x1C, 65001); // UTF-8
        U32(0x20, f.UniqueId);
        U32(0x24, f.Version);
        for (var o = 0x28; o < 0x50; o += 4)
            U32(o, -1); // orthographic, inflection, index names, keys, extra indexes
        U32(0x50, f.FirstNonBook);
        U32(0x5C, Locale(f.Language));
        U32(0x68, f.Version);
        U32(0x6C, f.FirstImage);
        U32(0x80, 0x50); // EXTH present
        U32(0xA4, -1);
        U32(0xA8, -1); // no DRM
        if (f.Version >= 8)
        {
            U32(0xC0, f.Fdst);
            U32(0xC4, f.FdstCount);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[0xC0..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(span[0xC2..], (ushort)Math.Max(1, f.LastContent));
            U32(0xC4, 1);
        }
        U32(0xC8, f.Fcis);
        U32(0xCC, 1);
        U32(0xD0, f.Flis);
        U32(0xD4, 1);
        U32(0xE0, -1);
        U32(0xE8, -1);
        U32(0xEC, -1);
        U32(0xF0, 1); // extra data flags: multibyte overlap
        U32(0xF4, f.Ncx);
        if (f.Version >= 8)
        {
            U32(0xF8, f.Fragment);
            U32(0xFC, f.Skeleton);
            for (var o = 0x100; o < 16 + mobiLength; o += 4)
                U32(o, -1); // DATP, guide and the rest
        }

        var exth = new MemoryStream();
        var exthLength = 12 + f.Exth.Sum(e => 8 + e.Data.Length);
        var padding = (4 - exthLength % 4) % 4;
        exth.Write("EXTH"u8);
        exth.Write(Be32(exthLength + padding));
        exth.Write(Be32(f.Exth.Count));
        foreach (var (type, data) in f.Exth)
        {
            exth.Write(Be32(type));
            exth.Write(Be32(8 + data.Length));
            exth.Write(data);
        }
        exth.Write(new byte[padding]);

        var name = Encoding.UTF8.GetBytes(f.Title);
        U32(0x54, header.Length + exth.Length);
        U32(0x58, name.Length);
        var record = new MemoryStream();
        record.Write(header);
        exth.WriteTo(record);
        record.Write(name);
        // At least two zero bytes after the name, up to a multiple of four.
        record.Write([0, 0]);
        while (record.Length % 4 != 0)
            record.WriteByte(0);
        return record.ToArray();
    }

    /// <summary>The Windows language id Kindle readers expect for the book's language (English when unknown).</summary>
    private static int Locale(string language)
    {
        try
        {
            var lcid = CultureInfo.GetCultureInfo(language).LCID;
            return lcid is 0x7F or 0x1000 ? 0x09 : lcid;
        }
        catch (CultureNotFoundException)
        {
            return 0x09;
        }
    }

    private static List<(int Type, byte[] Data)> Exth(BookMetadata metadata, string title, string language, int resources, int? cover, int? thumbnail)
    {
        var exth = new List<(int, byte[])>();
        foreach (var author in metadata.Authors)
            exth.Add((100, Encoding.UTF8.GetBytes(author)));
        if (metadata.Publisher is { Length: > 0 } publisher)
            exth.Add((101, Encoding.UTF8.GetBytes(publisher)));
        if (metadata.Description is { Length: > 0 } description)
            exth.Add((103, Encoding.UTF8.GetBytes(description)));
        exth.Add((113, Encoding.ASCII.GetBytes(Guid.NewGuid().ToString())));
        exth.Add((501, "EBOK"u8.ToArray()));
        exth.Add((503, Encoding.UTF8.GetBytes(title)));
        exth.Add((524, Encoding.UTF8.GetBytes(language)));
        exth.Add((125, Be32(resources)));
        if (cover is { } c)
        {
            exth.Add((201, Be32(c)));
            exth.Add((203, Be32(0)));
        }
        if (thumbnail is { } t)
            exth.Add((202, Be32(t)));
        return exth;
    }

    private static byte[] Fdst(List<long> flowEnds)
    {
        var record = new MemoryStream();
        record.Write("FDST"u8);
        record.Write(Be32(12));
        record.Write(Be32(flowEnds.Count));
        long start = 0;
        foreach (var end in flowEnds)
        {
            record.Write(Be32((int)start));
            record.Write(Be32((int)end));
            start = end;
        }
        return record.ToArray();
    }

    private static byte[] Flis() =>
    [
        .. "FLIS"u8, 0, 0, 0, 8, 0, 0x41, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 1, 0, 3, 0, 0, 0, 3, 0, 0, 0, 1,
        0xFF, 0xFF, 0xFF, 0xFF,
    ];

    private static byte[] Fcis(long textLength) =>
    [
        .. "FCIS"u8, 0, 0, 0, 0x14, 0, 0, 0, 0x10, 0, 0, 0, 1, 0, 0, 0, 0, .. Be32((int)textLength), 0, 0, 0, 0, 0, 0, 0,
        0x20, 0, 0, 0, 8, 0, 1, 0, 1, 0, 0, 0, 0,
    ];

    /// <summary>The Palm database: a 78-byte header, the record list and the records.</summary>
    private static byte[] PalmDatabaseFile(string title, List<byte[]> records)
    {
        var header = new byte[78 + records.Count * 8 + 2];
        var name = new string(title.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').Take(27).ToArray());
        Encoding.ASCII.GetBytes(name.Length > 0 ? name : "Book").CopyTo(header, 0);
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), now);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(40), now);
        "BOOKMOBI"u8.CopyTo(header.AsSpan(60));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(68), (uint)(2 * records.Count - 1));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(76), (ushort)records.Count);
        var offset = header.Length;
        for (var i = 0; i < records.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(78 + i * 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(78 + i * 8 + 4), (uint)(2 * i) & 0x00FFFFFF);
            offset += records[i].Length;
        }
        using var file = new MemoryStream(offset);
        file.Write(header);
        foreach (var record in records)
            file.Write(record);
        return file.ToArray();
    }

    private static byte[] Be32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>The pictures of the book as JPEG, PNG or GIF records, numbered from 0 in the order they are first used.</summary>
    private sealed class Resources(string folder)
    {
        private readonly List<(string File, string MediaType)> _items = [];
        private readonly Dictionary<string, int> _bySource = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _items.Count;

        public string MediaType(int index) => _items[index].MediaType;

        public int? Add(string path)
        {
            if (_bySource.TryGetValue(path, out var known))
                return known;
            if (_items.Count >= 65000 || EbookFiles.CommonImage(path, folder, gifAndSvg: false) is not ({ } file, { } mediaType))
                return null;
            _items.Add((file, mediaType));
            _bySource[path] = _items.Count - 1;
            return _items.Count - 1;
        }

        /// <summary>A small JPEG of the cover for the Kindle's library (EXTH 202).</summary>
        public int? AddThumbnail(int cover)
        {
            try
            {
                using var image = new ImageMagick.MagickImage(_items[cover].File);
                if (image.Height > 330)
                    image.Resize(0, 330);
                image.BackgroundColor = ImageMagick.MagickColors.White;
                image.Alpha(ImageMagick.AlphaOption.Remove);
                image.Quality = 85;
                var file = Path.Combine(folder, $"thumbnail-{Guid.NewGuid():N}.jpg");
                image.Write(file, ImageMagick.MagickFormat.Jpeg);
                _items.Add((file, "image/jpeg"));
                return _items.Count - 1;
            }
            catch (ImageMagick.MagickException)
            {
                return null;
            }
        }

        public IEnumerable<byte[]> Records() => _items.Select(i => File.ReadAllBytes(i.File));
    }
}
