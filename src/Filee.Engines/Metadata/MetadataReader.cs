// The Metadata tool without ExifTool: every field MetadataExtractor finds (EXIF, GPS, IPTC, XMP, maker notes, the
// file's own facts), read-only. Its directories stand for ExifTool's groups; an XMP list is one field of entries.

using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;

namespace Filee.Engines.Metadata;

public static partial class MetadataReader
{
    /// <summary>
    /// Every field MetadataExtractor reads in <paramref name="path"/>. Throws when it can't read the file.
    /// </summary>
    public static IReadOnlyList<MetadataField> Read(string path)
    {
        var fields = new List<MetadataField>();
        foreach (var directory in ImageMetadataReader.ReadMetadata(path))
        {
            var family = FamilyOf(directory);
            if (directory is XmpDirectory xmp)
            {
                // XMP is one block to MetadataExtractor: its properties are the fields, the entries of a list one each
                // ("dc:subject[1]", "dc:subject[2]" → one field "subject" of two entries).
                var lists = new Dictionary<string, List<string>>();
                foreach (var (name, value) in xmp.GetXmpProperties())
                {
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    var entry = XmpEntry().Match(name);
                    var property = entry.Success ? entry.Groups[1].Value : name;
                    var colon = property.IndexOf(':');
                    var group = colon > 0 ? "XMP-" + property[..colon] : "XMP";
                    var tag = colon > 0 ? property[(colon + 1)..] : property;
                    if (!entry.Success)
                    {
                        fields.Add(new MetadataField(family, group, tag, value, false));
                        continue;
                    }
                    if (!lists.TryGetValue(property, out var items))
                    {
                        lists[property] = items = [];
                        fields.Add(new MetadataField(family, group, tag, items, true, false));
                    }
                    items.Add(value);
                }
                continue;
            }
            foreach (var tag in directory.Tags)
            {
                if (tag.Description is { Length: > 0 } description)
                    fields.Add(new MetadataField(family, directory.Name, tag.Name, description, false));
            }
        }
        return fields;
    }

    /// <summary>"dc:subject[2]": an entry of a list, the property's name and its place.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\A(.+)\[\d+\]\z")]
    private static partial System.Text.RegularExpressions.Regex XmpEntry();

    /// <summary>The broad kind of a directory, as ExifTool's family 0 names it.</summary>
    private static string FamilyOf(MetadataExtractor.Directory directory)
    {
        var name = directory.Name;
        return name.StartsWith("Exif", StringComparison.Ordinal) || name.StartsWith("GPS", StringComparison.Ordinal) ? "EXIF"
            : name.StartsWith("IPTC", StringComparison.Ordinal) ? "IPTC"
            : directory is XmpDirectory ? "XMP"
            : name.Contains("Makernote", StringComparison.OrdinalIgnoreCase) ? "MakerNotes"
            : name.StartsWith("ICC", StringComparison.Ordinal) ? "ICC_Profile"
            : name is "File" or "File Type" ? "File"
            : name;
    }
}
