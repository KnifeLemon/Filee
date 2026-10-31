// Subsets the fonts embedded in a PDF again to the glyphs its pages draw, for PdfCompressor. Fonts embedded whole
// (a 10 MB CJK font for a few lines) and fonts of a document whose pages were split or extracted shrink to what is
// left. Glyph IDs stay as they were (FontSubsetter and CffSubsetter keep them), so nothing in the content, the widths
// or the ToUnicode maps changes. Handled: Type 0 fonts with Identity encodings (TrueType and CFF outlines) and simple
// TrueType fonts; a font is left whole when Filee can't follow every use of it (PdfFontUses) or can't tell which
// glyphs its codes draw.

using System.Security.Cryptography;
using System.Text;
using Filee.Engines.Fonts;
using Filee.Engines.Fonts.Cff;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Filee.Engines.Pdf;

/// <summary>Re-subsets a document's embedded fonts to the glyphs it uses.</summary>
internal static class PdfFontSubsetter
{
    /// <summary>A font program, the kind of outlines it holds, and the glyphs the fonts using it draw.</summary>
    private sealed class Program(PdfDictionary stream, string key)
    {
        public PdfDictionary Stream { get; } = stream;

        /// <summary>FontFile2, FontFile3/Type1C, FontFile3/CIDFontType0C or FontFile3/OpenType.</summary>
        public string Key { get; } = key;

        public HashSet<int> Glyphs { get; } = [0];
        public List<PdfDictionary> Fonts { get; } = [];
        public bool Simple { get; set; }
        public bool Cid { get; set; }
        public bool Unsafe { get; set; }
    }

    /// <summary>Subsets the fonts of <paramref name="document"/>; returns how many font programs got smaller.</summary>
    public static int Subset(PdfDocument document, CancellationToken cancellationToken)
    {
        var uses = PdfFontUses.Find(document, cancellationToken);
        var programs = new Dictionary<PdfDictionary, Program>(ReferenceEqualityComparer.Instance);
        foreach (var font in Fonts(document, uses))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProgramOf(font) is not var (stream, key, descendant))
                continue;
            if (!programs.TryGetValue(stream, out var program))
                programs[stream] = program = new Program(stream, key);
            program.Fonts.Add(font);
            program.Simple |= descendant is null;
            program.Cid |= descendant is not null;
            if (uses.Unsure.Contains(font) || (program.Simple && program.Cid))
            {
                program.Unsafe = true;
                continue;
            }
            var codes = uses.Codes.GetValueOrDefault(font) ?? [];
            if (codes.Count == 0)
                continue;
            try
            {
                if (Glyphs(font, descendant, program, codes) is { } glyphs)
                    program.Glyphs.UnionWith(glyphs);
                else
                    program.Unsafe = true;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
            {
                program.Unsafe = true;
            }
        }

        var smaller = 0;
        foreach (var program in programs.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!program.Unsafe && Rewrite(program, cancellationToken))
                smaller++;
        }
        return smaller;
    }

    /// <summary>Every font dictionary of the document: in resources, and stored as objects of their own.</summary>
    private static IEnumerable<PdfDictionary> Fonts(PdfDocument document, PdfFontUses uses)
    {
        var all = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is PdfDictionary { Stream: null } dictionary && dictionary.Elements.GetName("/Type") == "/Font"
                && dictionary.Elements.GetName("/Subtype") is not ("/CIDFontType0" or "/CIDFontType2"))
                all.Add(dictionary);
        }
        all.UnionWith(uses.Codes.Keys);
        all.UnionWith(uses.Unsure);
        return all;
    }

    /// <summary>The font program a font draws with, its kind, and the descendant CIDFont of a Type 0 font.</summary>
    private static (PdfDictionary Stream, string Key, PdfDictionary? Descendant)? ProgramOf(PdfDictionary font)
    {
        PdfDictionary? descendant = null;
        var holder = font;
        if (font.Elements.GetName("/Subtype") == "/Type0")
        {
            if (PdfCompressor.Resolve(font.Elements["/DescendantFonts"]) is not PdfArray { Elements.Count: > 0 } descendants
                || PdfCompressor.Resolve(descendants.Elements[0]) is not PdfDictionary cidFont)
                return null;
            descendant = holder = cidFont;
        }
        if (PdfCompressor.Resolve(holder.Elements["/FontDescriptor"]) is not PdfDictionary descriptor)
            return null;
        if (PdfCompressor.Resolve(descriptor.Elements["/FontFile2"]) is PdfDictionary { Stream: not null } trueType)
            return (trueType, "FontFile2", descendant);
        if (PdfCompressor.Resolve(descriptor.Elements["/FontFile3"]) is PdfDictionary { Stream: not null } cff
            && cff.Elements.GetName("/Subtype") is "/Type1C" or "/CIDFontType0C" or "/OpenType" && cff.Elements.GetName("/Subtype") is var subtype)
            return (cff, "FontFile3" + subtype, descendant);
        return null;
    }

    /// <summary>The glyphs <paramref name="codes"/> draw, or null when Filee can't tell.</summary>
    private static IEnumerable<int>? Glyphs(PdfDictionary font, PdfDictionary? descendant, Program program, HashSet<int> codes)
    {
        if (descendant is not null)
        {
            if (font.Elements.GetName("/Encoding") is not ("/Identity-H" or "/Identity-V"))
                return null; // a CMap of its own: codes aren't CIDs
            return descendant.Elements.GetName("/Subtype") switch
            {
                "/CIDFontType2" when program.Key == "FontFile2" => CidToGid(descendant, codes),
                "/CIDFontType0" when program.Key is "FontFile3/CIDFontType0C" or "FontFile3/OpenType" => CffGlyphs(program, codes),
                _ => null,
            };
        }
        return font.Elements.GetName("/Subtype") == "/TrueType" && program.Key == "FontFile2" ? SimpleTrueType(font, program, codes) : null;
    }

    /// <summary>CIDs to glyphs through the CIDFont's CIDToGIDMap (Identity, or a stream of two-byte glyph IDs).</summary>
    private static IEnumerable<int>? CidToGid(PdfDictionary cidFont, HashSet<int> cids)
    {
        switch (PdfCompressor.Resolve(cidFont.Elements["/CIDToGIDMap"]))
        {
            case null:
            case PdfName { Value: "/Identity" }:
                return cids;
            case PdfDictionary { Stream: not null } map:
                var bytes = map.Stream.UnfilteredValue;
                return cids.Select(cid => 2 * cid + 1 < bytes.Length ? bytes[2 * cid] << 8 | bytes[2 * cid + 1] : 0);
            default:
                return null;
        }
    }

    /// <summary>CIDs to glyphs through a CID-keyed CFF font's charset; a name-keyed one uses CIDs as glyph IDs.</summary>
    private static IEnumerable<int> CffGlyphs(Program program, HashSet<int> cids)
    {
        var cff = CffData(program, out _);
        var font = CffFont.Parse(cff);
        if (!font.IsCid || font.Charset is null)
            return cids;
        var glyphByCid = new Dictionary<int, int>();
        for (var gid = 0; gid < font.Charset.Length; gid++)
            glyphByCid.TryAdd(font.Charset[gid], gid);
        return cids.Select(cid => glyphByCid.GetValueOrDefault(cid));
    }

    /// <summary>
    /// The glyphs one-byte codes of a simple TrueType font can draw, by every route viewers take (PDF 32000 9.6.6.4):
    /// the (3,0) symbol subtable at the code and at U+F000 + code, the (1,0) Macintosh subtable, and the Unicode
    /// subtable at the character the encoding gives. All of them are kept. Null for encodings Filee can't read.
    /// </summary>
    private static IEnumerable<int>? SimpleTrueType(PdfDictionary font, Program program, HashSet<int> codes)
    {
        var sfnt = SfntFont.Read(program.Stream.Stream.UnfilteredValue);
        if (sfnt["cmap"] is not { } cmap)
            return codes; // no cmap: codes are glyph IDs
        var tables = CmapTable.ReadAll(cmap);
        var unicode = Unicode(font, sfnt, codes, out var named);
        if (unicode is null)
            return null;
        var glyphs = new HashSet<int>(named);
        foreach (var code in codes)
        {
            foreach (var (platform, encoding, map) in tables)
            {
                IEnumerable<int> keys = (platform, encoding) switch
                {
                    (3, 0) => [code, 0xF000 + code, 0xF100 + code, 0xF200 + code],
                    (1, 0) => [code],
                    (3, 1) or (3, 10) or (0, _) => unicode.TryGetValue(code, out var chars) ? [.. chars, code, 0xF000 + code] : [code, 0xF000 + code],
                    _ => [],
                };
                foreach (var key in keys)
                {
                    if (map.TryGetValue(key, out var glyph))
                        glyphs.Add(glyph);
                }
            }
        }
        return glyphs;
    }

    /// <summary>
    /// The characters each code may stand for: Windows-1252 and Mac Roman, which the base encodings follow, and names
    /// of /Differences written as uniXXXX / uXXXX or found in the font's own glyph names (those glyphs go to
    /// <paramref name="named"/>). Null when a Differences name can't be read either way.
    /// </summary>
    private static Dictionary<int, List<int>>? Unicode(PdfDictionary font, SfntFont sfnt, HashSet<int> codes, out List<int> named)
    {
        named = [];
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var windows = Encoding.GetEncoding(1252);
        var mac = Encoding.GetEncoding(10000);
        var result = new Dictionary<int, List<int>>();
        foreach (var code in codes)
            result[code] = [windows.GetString([(byte)code])[0], mac.GetString([(byte)code])[0]];

        if (PdfCompressor.Resolve(font.Elements["/Encoding"]) is not PdfDictionary encoding
            || PdfCompressor.Resolve(encoding.Elements["/Differences"]) is not PdfArray differences)
            return result;
        var postNames = PostNames(sfnt);
        var next = 0;
        foreach (var item in differences.Elements.Select(PdfCompressor.Resolve))
        {
            if (item is PdfInteger number)
            {
                next = number.Value;
                continue;
            }
            if (item is not PdfName name)
                continue;
            var code = next++;
            if (!codes.Contains(code))
                continue;
            var glyphName = name.Value.TrimStart('/');
            if (GlyphNameCharacter(glyphName) is { } character)
                result[code].Add(character);
            else if (postNames.TryGetValue(glyphName, out var glyph))
                named.Add(glyph);
            else if (glyphName != ".notdef")
                return null;
        }
        return result;
    }

    private static int? GlyphNameCharacter(string name)
    {
        if (name.Length == 7 && name.StartsWith("uni", StringComparison.Ordinal) && int.TryParse(name[3..], System.Globalization.NumberStyles.HexNumber, null, out var bmp))
            return bmp;
        if (name.Length is 5 or 6 or 7 && name[0] == 'u' && int.TryParse(name[1..], System.Globalization.NumberStyles.HexNumber, null, out var code))
            return code;
        return name.Length == 1 && name[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? name[0] : null;
    }

    /// <summary>The glyph names of a format 2 'post' table that are its own (index 258 and up), by name.</summary>
    private static Dictionary<string, int> PostNames(SfntFont sfnt)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        if (sfnt["post"] is not { Length: > 34 } post || BigEndianRead.U32(post, 0) != 0x00020000)
            return names;
        int count = BigEndianRead.U16(post, 32);
        var indexAt = 34;
        var stringsAt = indexAt + 2 * count;
        if (stringsAt > post.Length)
            return names;
        var strings = new List<string>();
        for (var at = stringsAt; at < post.Length;)
        {
            int length = post[at];
            if (at + 1 + length > post.Length)
                break;
            strings.Add(Encoding.ASCII.GetString(post, at + 1, length));
            at += 1 + length;
        }
        for (var glyph = 0; glyph < count; glyph++)
        {
            var index = BigEndianRead.U16(post, indexAt + 2 * glyph) - 258;
            if (index >= 0 && index < strings.Count)
                names.TryAdd(strings[index], glyph);
        }
        return names;
    }

    /// <summary>Writes the subset into the program's stream when it is smaller; marks the fonts as subsets.</summary>
    private static bool Rewrite(Program program, CancellationToken cancellationToken)
    {
        byte[] original, subset;
        try
        {
            original = program.Stream.Stream.UnfilteredValue;
            switch (program.Key)
            {
                case "FontFile2":
                {
                    var font = SfntFont.Read(original);
                    if (!FontSubsetter.CanSubset(font, out _) || font.HasCffOutlines)
                        return false;
                    var result = FontSubsetter.Subset(font, program.Glyphs.Where(g => g < font.GlyphCount), cancellationToken, keepGlyphCount: program.Simple);
                    if (program.Simple)
                    {
                        // Viewers find a simple font's glyphs through its own cmap and glyph names.
                        foreach (var table in new[] { "cmap", "post" })
                        {
                            if (font[table] is { } data)
                                result.Tables[table] = data;
                        }
                    }
                    subset = result.Write();
                    break;
                }
                case "FontFile3/OpenType":
                {
                    var font = SfntFont.Read(original);
                    if (font["CFF "] is not { } cff)
                        return false;
                    font.Tables["CFF "] = CffSubsetter.Subset(cff, program.Glyphs);
                    subset = font.Write();
                    break;
                }
                default:
                    subset = CffSubsetter.Subset(CffData(program, out _), program.Glyphs);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            return false; // the font stays as it is
        }
        if (subset.Length >= original.Length)
            return false;

        var stream = program.Stream;
        stream.Stream.Value = subset;
        stream.Elements.Remove("/Filter");
        stream.Elements.Remove("/DecodeParms");
        if (program.Key == "FontFile2")
            stream.Elements.SetInteger("/Length1", subset.Length);
        var tag = Tag(program.Glyphs);
        foreach (var font in program.Fonts)
            MarkSubset(font, tag);
        return true;
    }

    private static byte[] CffData(Program program, out SfntFont? sfnt)
    {
        var data = program.Stream.Stream.UnfilteredValue;
        sfnt = null;
        if (program.Key != "FontFile3/OpenType")
            return data;
        sfnt = SfntFont.Read(data);
        return sfnt["CFF "] ?? throw new InvalidDataException("The OpenType font has no CFF outlines.");
    }

    /// <summary>
    /// Puts a subset tag in front of the font's names (PDF 32000 9.6.4) unless they have one, and drops the glyph
    /// lists (CIDSet, CharSet) that described the whole font.
    /// </summary>
    private static void MarkSubset(PdfDictionary font, string tag)
    {
        var holders = new List<PdfDictionary> { font };
        if (PdfCompressor.Resolve(font.Elements["/DescendantFonts"]) is PdfArray { Elements.Count: > 0 } descendants
            && PdfCompressor.Resolve(descendants.Elements[0]) is PdfDictionary cidFont)
            holders.Add(cidFont);
        foreach (var holder in holders)
        {
            Tagged(holder, "/BaseFont", tag);
            if (PdfCompressor.Resolve(holder.Elements["/FontDescriptor"]) is PdfDictionary descriptor)
            {
                Tagged(descriptor, "/FontName", tag);
                descriptor.Elements.Remove("/CIDSet");
                descriptor.Elements.Remove("/CharSet");
            }
        }

        static void Tagged(PdfDictionary dictionary, string key, string tag)
        {
            var name = dictionary.Elements.GetName(key);
            if (name.Length > 1 && !(name.Length > 8 && name[7] == '+' && name[1..7].All(char.IsAsciiLetterUpper)))
                dictionary.Elements.SetName(key, "/" + tag + "+" + name[1..]);
        }
    }

    /// <summary>Six capital letters from the glyph set, as subset tags are.</summary>
    private static string Tag(HashSet<int> glyphs)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(string.Join(',', glyphs.Order())));
        return new string([.. hash.Take(6).Select(b => (char)('A' + b % 26))]);
    }

    private static class BigEndianRead
    {
        public static uint U32(byte[] data, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));

        public static int U16(byte[] data, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
    }
}
